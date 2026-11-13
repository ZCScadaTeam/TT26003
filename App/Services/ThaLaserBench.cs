using System.Diagnostics;
using AppZC.Configs;

namespace AppZC.Services;

/// <summary>
/// 打标机耗时探针（<c>AppZC.exe --laserbench [内容]</c>，无界面、不写库、不启采集服务）。
///
/// 目的：把「打码触发 → 打码 OK」这一段的耗时拆开，判断慢在**本机代码**还是**打标机回执**。
/// 连续打 <see cref="Rounds"/> 次，逐次打印：
///   ① 连接耗时（首轮常连，之后复用）② 三步总耗时 ③ 每步「清残留 / 写入 / 等回执」三段。
/// 三步内部日志由 <c>TcpLaserMarker</c> 自己打（<c>[THA-LASER]</c> 行），这里只做汇总与统计。
/// </summary>
public static class ThaLaserBench
{
	private const int Rounds = 3;

	/// <param name="cfg">全局配置（传了 ip/port 时只在本进程内覆盖，不写回配置文件）。</param>
	/// <param name="content">打标内容，留空按当前码规则拼一个样例（分段模式用 `Preview`，纯时间码模式用 `CodeDateFormat`）。</param>
	/// <param name="ip">临时覆盖打标机 IP（同时把 LaserMode 切成 Tcp）。</param>
	/// <param name="port">临时覆盖打标机端口。</param>
	public static async Task<int> RunAsync(AppConfig cfg, string? content, string? ip = null, int? port = null)
	{
		// 命令行给了 IP 就临时切 TCP 且不落盘，方便对着假打标机 / 备用机测
		if (!string.IsNullOrWhiteSpace(ip))
		{
			cfg.LaserMode = "Tcp";
			cfg.LaserIp = ip!;
			if (port is > 0) cfg.LaserPort = port.Value;
		}

		var code = string.IsNullOrWhiteSpace(content) ? DefaultContent(cfg) : content!;
		Console.WriteLine("=== THA26003 打标机耗时探针（--laserbench）===");
		Console.WriteLine($"目标 {cfg.LaserIp}:{cfg.LaserPort}  模式={cfg.LaserMode}  帧={cfg.LaserFrameEnabled}");
		Console.WriteLine($"①②超时={cfg.LaserStepTimeoutMs}ms  ③超时={cfg.LaserTimeoutMs}ms  内容={code}");
		Console.WriteLine($"步骤：①{cfg.LaserInitTemplate} ②{cfg.LaserDataTemplate} ③{cfg.LaserStartTemplate}\n");

		var laser = ThaDevices.CreateLaser(cfg);
		var total = new List<long>(Rounds);

		for (var round = 1; round <= Rounds; round++)
		{
			var sw = Stopwatch.StartNew();
			MarkerResult result;
			try
			{
				// 每次略微变化内容，避免打标机对"重复内容"做缓存/短路（真机可能不缓存，但这样更接近产线）
				var body = round == 1 ? code : code[..^1] + (char)('0' + round % 10);
				result = await laser.MarkAsync(body, CancellationToken.None);
				sw.Stop();
				total.Add(sw.ElapsedMilliseconds);
				Console.WriteLine($"[第 {round} 次] 结果={(result.Ok ? "OK" : "NG")} 代码={(int)result.Code} "
					+ $"总耗时={sw.ElapsedMilliseconds}ms 回执={Clip(result.RawReply)} {result.Message}");
			}
			catch (Exception ex)
			{
				sw.Stop();
				Console.WriteLine($"[第 {round} 次] 异常（{sw.ElapsedMilliseconds}ms）：{ex.Message}");
			}
			Console.WriteLine();
		}

		(laser as IDisposable)?.Dispose();

		if (total.Count > 0)
		{
			Console.WriteLine($"=== 汇总：{total.Count} 次  最小 {total.Min()}ms / 最大 {total.Max()}ms / 平均 {total.Sum() / total.Count}ms ===");
			Console.WriteLine("判读：");
			Console.WriteLine("  ①若上面 [THA-LASER] 的「等回执」占了绝大部分 → 慢在打标机（模板加载 / 打标物理时间），本机无优化空间");
			Console.WriteLine("  ②若「清残留」或「写入」占大头 → 网络或连接异常，检查 NoDelay / 网络链路");
			Console.WriteLine("  ③若首轮远慢于后两轮 → 连接建立耗时（考虑把 LaserKeepAlive 设为 true 保持长连接）");
		}

		return 0;
	}

	private static string DefaultContent(AppConfig cfg)
	{
		var preview = ThaCodeAssembly.Preview(cfg.CodeSegments ?? new List<CodeSegment>(),
			cfg.SerialResetHour, cfg.DateFollowProductionDay);
		return preview.Length > 0 ? preview : ThaCodeGenerator.Format(DateTime.Now, cfg.CodeDateFormat);
	}

	private static string Clip(string? s, int max = 80)
	{
		if (string.IsNullOrEmpty(s)) return "(空)";
		var one = s.Replace("\r", "\\r").Replace("\n", "\\n");
		return one.Length <= max ? one : one[..max] + "…";
	}
}
