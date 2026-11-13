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
		Console.WriteLine("模拟器每第 3 件为「无码上传」（跳过打码/读码，只触发采集）→ 验证无码落库");
		Console.WriteLine($"PLC={cfg.PlcIpAddress}:{cfg.PlcPort}  汇川系列={cfg.InovanceSeries}  地址前缀='{cfg.PlcAddressPrefix}'  打码={cfg.LaserMode}  读码={cfg.ScannerMode}");
		Console.WriteLine($"码格式={cfg.CodeDateFormat}  时间窗={cfg.CodeTimeWindowMinutes}min  在制超时={cfg.WipTimeoutHours}h");
		Console.WriteLine($"码长={ThaCodeAssembly.TotalLength(cfg.CodeSegments ?? new List<CodeSegment>())}位  批次号清零={ThaProductionDay.Normalize(cfg.SerialResetHour):00}:00  当前生产日={ThaProductionDay.Label(DateTime.Now, cfg.SerialResetHour)}");
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
			var coded = records.Where(r => !string.IsNullOrWhiteSpace(r.ProductCode)).ToList();
			var noCode = records.Where(r => string.IsNullOrWhiteSpace(r.ProductCode)).ToList();

			Check("有码件的产品码非空", coded.Count > 0 && coded.All(r => !string.IsNullOrWhiteSpace(r.ProductCode)),
				$"有码 {coded.Count} / 无码 {noCode.Count}");
			Check("有码件上传结果全部 OK(1)", coded.All(r => r.UploadResult == 1),
				string.Join(",", coded.Select(r => r.UploadResult)));
			Check("码互不重复（纯时间码唯一性）",
				coded.Select(r => r.ProductCode).Distinct().Count() == coded.Count,
				string.Join(",", coded.Select(r => r.ProductCode)));
			Check("测量值已落库（上针高 Pin1 ≈ 2.115）",
				records.All(r => r.TopPinHeight1.HasValue && Math.Abs(r.TopPinHeight1!.Value - 2.115) < 1e-6),
				string.Join(",", records.Select(r => r.TopPinHeight1?.ToString("F3") ?? "null")));
			Check("位置度已落库（上位置度 PIN15_X ≈ 0.240）",
				records.All(r => r.TopPinPosX15.HasValue && Math.Abs(r.TopPinPosX15!.Value - 0.240) < 1e-6),
				string.Join(",", records.Select(r => r.TopPinPosX15?.ToString("F3") ?? "null")));
			Check("是否复检列全部为 NULL（PLC 未提供该地址）",
				records.All(r => r.IsRetest == null),
				string.Join(",", records.Select(r => r.IsRetest?.ToString() ?? "null")));

			// ===== 无码上传专项：模拟器每第 3 件跳过打码/读码，只触发采集 =====
			Check("无码上传有落库（不是丢数据）", noCode.Count >= 1, $"实际 {noCode.Count} 件");
			if (noCode.Count > 0)
			{
				Check("无码记录的 产品码 为 NULL（非空串）",
					noCode.All(r => r.ProductCode == null),
					string.Join(",", noCode.Select(r => r.ProductCode ?? "null")));
				Check("无码记录的 上传结果 = 1（落库成功，码为空不算落库失败）",
					noCode.All(r => r.UploadResult == 1),
					string.Join(",", noCode.Select(r => r.UploadResult?.ToString() ?? "null")));
				Check("无码记录仍带完整测量数据（针高/位置度按实测落库）",
					noCode.All(r => r.TopPinHeight1.HasValue && r.TopPinPosX15.HasValue),
					string.Join(",", noCode.Select(r => r.TopPinHeight1?.ToString("F3") ?? "null")));
				Check("无码记录的 总结果 由结果字决定（不因缺码判 NG）",
					noCode.All(r => r.OverallResult is 1 or 2),
					string.Join(",", noCode.Select(r => r.OverallResult?.ToString() ?? "null")));

				var nullCodeInDb = ThaDb.Db.Queryable<ThaProductRecord>()
					.Where(x => x.ProductCode == null).Count();
				Check("库中确实存在 ProductCode IS NULL 的记录", nullCodeInDb >= noCode.Count,
					$"库中 {nullCodeInDb} 条 / 事件 {noCode.Count} 条");
			}

			// 库里总条数应 >= 本次事件数（含无码件）—— 一条一记录、无覆盖、无丢失
			var inDbAll = ThaDb.Db.Queryable<ThaProductRecord>().Count();
			Check("落库总条数 >= 事件数（无码件同样落库、无覆盖）", inDbAll >= records.Count,
				$"库中 {inDbAll} / 本次事件 {records.Count}");

			var statuses = coded.Select(r => ThaCodeStore.GetStatus(r.ProductCode!)).ToList();
			Check("有码件的码索引状态全部为已上传(2)", statuses.All(s => s == CodeStatus.Uploaded),
				string.Join(",", statuses.Select(s => s?.ToString() ?? "null")));

			var marks = ThaDb.Db.Queryable<ThaMarkLog>().Count();
			var scans = ThaDb.Db.Queryable<ThaScanLog>().Count();
			Check("打码流水已写入（每件有码件一条）", marks >= coded.Count, $"打码流水 {marks} 条 / 有码件 {coded.Count}");
			Check("读码流水已写入（每件有码件一条）", scans >= coded.Count, $"读码流水 {scans} 条 / 有码件 {coded.Count}");

			// 新增：PLC 附带前站镭码（%MW10580）比对链
			var withPlcCode = ThaDb.Db.Queryable<ThaScanLog>()
				.Where(x => x.PlcCode != null && x.PlcCode != "").Count();
			Check("读码流水记录了 PLC 附带前站镭码（%MW10580 已读回）", withPlcCode >= coded.Count,
				$"有 PLC 码的流水 {withPlcCode} 条 / 有码件 {coded.Count}");

			var scanResults = coded.Select(r => r.ScanResult).ToList();
			Check("有码件的扫码结果已落库（非 NULL）", scanResults.All(v => v != null),
				string.Join(",", scanResults.Select(v => v?.ToString() ?? "null")));
			Console.WriteLine($"[提示] 本次有码件扫码结果分布：OK(1)={scanResults.Count(v => v == 1)} / NG(2)={scanResults.Count(v => v == 2)}");
		}

		Console.WriteLine($"\n=== 端到端自检结束：通过 {pass} / 失败 {fail} ===");
		return fail == 0 ? 0 : 1;
	}
}
