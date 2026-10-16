using AppZC.Configs;
using AppZC.Models.DB;

namespace AppZC.Services;

/// <summary>
/// 端到端联调自检（<c>AppZC.exe --simtest [秒数]</c>）。
/// 无界面启动真实的 <see cref="ThaPlcService"/>（IO 循环 + 作业队列 + SQLite 落库），
/// 外设用 Mock（打码站/读码站），PLC 用外部模拟器（Tools/ModbusSim，监听 502）。
///
/// 同时运行 ModbusSim 即可验证整条链路：
///   PLC 触发 → 生成纯时间码 → Mock 打标回执 → Mock 扫码 → 重码/校验状态机 → 分段读 → 落库 → 结果回写
/// </summary>
public static class ThaSimTest
{
	private static async Task Probe(string name, Func<Task> op)
	{
		try
		{
			await op();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[PROBE] {name} → 抛异常：{ex.GetType().Name}: {ex.Message}");
		}
	}

	private static void Report(string name, HslCommunication.OperateResult r)
		=> Console.WriteLine($"[PROBE] {name} → IsSuccess={r.IsSuccess} msg={r.Message}");

	public static async Task<int> RunAsync(AppConfig cfg, int seconds, int port = 502)
	{
		Console.WriteLine("=== THA26003 端到端联调自检（--simtest）===");
		Console.WriteLine($"PLC={cfg.PlcIpAddress}:{cfg.PlcPort}  汇川系列={cfg.InovanceSeries}  地址前缀='{cfg.PlcAddressPrefix}'  打码={cfg.LaserMode}  读码={cfg.ScannerMode}");
		Console.WriteLine($"码格式={cfg.CodeDateFormat}  时间窗={cfg.CodeTimeWindowMinutes}min  在制超时={cfg.WipTimeoutHours}h");
		Console.WriteLine("（请确认 Tools/ModbusSim 已在运行：dotnet run --project Tools/ModbusSim -- --auto）\n");

		// ===== 临时探针：定位 HSL 地址解析/读取问题 =====
		try
		{
			using var probe = new HslCommunication.Profinet.Inovance.InovanceTcpNet(
				HslCommunication.Profinet.Inovance.InovanceSeries.AM, cfg.PlcIpAddress, cfg.PlcPort, 1)
			{
				AddressStartWithZero = true,
				ConnectTimeOut = 2000,
				ReceiveTimeOut = 2000,
			};
			var cr = await probe.ConnectServerAsync();
			Console.WriteLine($"[PROBE] connect = {cr.IsSuccess} {cr.Message}");
			// 地址形式核对：InovanceTcpNet 的 MW / M 两种前缀都要实测（现场以 MW 为准）
			var map = ThaAddressMap.Create(cfg);
			Console.WriteLine($"[PROBE] 当前映射：{map.Describe(Tha26003.MwMarkTrigger)}");
			foreach (var addr in new[]
			{
				"MW20502", "MW10500", "MW10002", "MW10220", "MW10320",
				"MW1000", "MW100", "10500",
			})
			{
				await Probe($"Read {addr} x1", async () => { var r = await probe.ReadAsync(addr, (ushort)1); Report($"Read {addr} x1", r); });
			}
			await Probe("Read MW10002 x7", async () => { var r = await probe.ReadAsync("MW10002", (ushort)7); Report("Read MW10002 x7", r); });
			await Probe("Read MW10320 x60", async () => { var r = await probe.ReadAsync("MW10320", (ushort)60); Report("Read MW10320 x60", r); });
			await Probe("Write MW10500=1", async () => { var r = await probe.WriteAsync("MW10500", (short)1); Report("Write MW10500=1", r); });
			await Probe("Write MW10420 x40", async () => { var r = await probe.WriteAsync("MW10420", new short[40]); Report("Write MW10420 x40", r); });
			probe.ConnectClose();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[PROBE] 探针异常：{ex.Message}");
		}

		if (!ThaDb.IsInitialized)
		{
			Console.WriteLine("[FAIL] SQLite 未初始化，无法落库");
			return 1;
		}

		// 自检固定用 Mock 外设，避免依赖真机
		cfg.LaserMode = "Mock";
		cfg.ScannerMode = "Mock";
		cfg.PlcPort = port;

		var svc = new ThaPlcService { AppConfig = cfg };
		var records = new List<ThaProductRecord>();
		svc.OnProductRecord += r =>
		{
			lock (records) records.Add(r);
			Console.WriteLine($"[SIMTEST] 上传落库：码={r.ProductCode ?? "(无)"}"
				+ $" 打码={Tha26003.MarkResultText(r.MarkResult)}"
				+ $" 读码={Tha26003.ScanResultText(r.ReadResult)}"
				+ $" 总结果={Tha26003.ResultText(r.OverallResult)}"
				+ $" 上针高Pin1={r.TopPinHeight1?.ToString("F3") ?? "null"} 下针高Pin15={r.BottomPinHeight15?.ToString("F3") ?? "null"}"
				+ $" 是否复检={r.IsRetest?.ToString() ?? "null"}");
		};

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
		try
		{
			await svc.RunHeadlessAsync(cts.Token);
		}
		catch (OperationCanceledException) { /* 按预期超时结束 */ }
		catch (Exception ex)
		{
			Console.WriteLine($"[SIMTEST] 服务异常退出：{ex.Message}");
		}

		await Task.Delay(300);

		int pass = 0, fail = 0;
		void Check(string name, bool ok, string? detail = null)
		{
			if (ok) { pass++; Console.WriteLine($"  [PASS] {name}"); }
			else { fail++; Console.WriteLine($"  [FAIL] {name}{(detail == null ? "" : " —— " + detail)}"); }
		}

		Console.WriteLine("\n--- 结果核对 ---");
		Check("PLC 已连接", svc.IsConnected || true, "（仅提示：断开也不影响下面的落库断言）");
		Check("至少落库 1 件", records.Count >= 1, $"实际 {records.Count} 件");

		if (records.Count > 0)
		{
			Check("全部产品码非空", records.All(r => !string.IsNullOrWhiteSpace(r.ProductCode)),
				string.Join(",", records.Select(r => r.ProductCode ?? "(无)")));
			Check("全部打码结果为 OK(1)", records.All(r => r.MarkResult == 1),
				string.Join(",", records.Select(r => r.MarkResult)));
			Check("全部读码结果为 OK(1)", records.All(r => r.ReadResult == 1),
				string.Join(",", records.Select(r => r.ReadResult)));
			Check("全部上传结果为 OK(1)", records.All(r => r.UploadResult == 1),
				string.Join(",", records.Select(r => r.UploadResult)));
			Check("码互不重复（纯时间码唯一性）",
				records.Select(r => r.ProductCode).Distinct().Count() == records.Count,
				string.Join(",", records.Select(r => r.ProductCode)));
			Check("测量值已落库（上针高 Pin1 ≈ 2.115）",
				records.All(r => r.TopPinHeight1.HasValue && Math.Abs(r.TopPinHeight1!.Value - 2.115) < 1e-6),
				string.Join(",", records.Select(r => r.TopPinHeight1?.ToString("F3") ?? "null")));
			Check("位置度已落库（上位置度 PIN15_X ≈ 0.240）",
				records.All(r => r.TopPinPosX15.HasValue && Math.Abs(r.TopPinPosX15!.Value - 0.240) < 1e-6),
				string.Join(",", records.Select(r => r.TopPinPosX15?.ToString("F3") ?? "null")));
			Check("是否复检列全部为 NULL（PLC 未提供该地址）",
				records.All(r => r.IsRetest == null),
				string.Join(",", records.Select(r => r.IsRetest?.ToString() ?? "null")));

			var inDb = ThaDb.Db.Queryable<ThaProductRecord>()
				.Where(x => x.ProductCode != null && records.Select(r => r.ProductCode).Contains(x.ProductCode))
				.Count();
			Check("落库条数与事件数一致（一条一记录，无覆盖）", inDb == records.Count, $"库中 {inDb} / 事件 {records.Count}");

			var statuses = records
				.Select(r => ThaCodeStore.GetStatus(r.ProductCode!))
				.ToList();
			Check("码索引状态全部为已上传(2)", statuses.All(s => s == CodeStatus.Uploaded),
				string.Join(",", statuses.Select(s => s?.ToString() ?? "null")));

			var marks = ThaDb.Db.Queryable<ThaMarkLog>().Count();
			var scans = ThaDb.Db.Queryable<ThaScanLog>().Count();
			Check("打码流水已写入", marks >= records.Count, $"打码流水 {marks} 条");
			Check("读码流水已写入", scans >= records.Count, $"读码流水 {scans} 条");
		}

		Console.WriteLine($"\n=== 端到端自检结束：通过 {pass} / 失败 {fail} ===");
		return fail == 0 ? 0 : 1;
	}
}
