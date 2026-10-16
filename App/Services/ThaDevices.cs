using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AppZC.Configs;

namespace AppZC.Services;

/// <summary>打码结果。</summary>
/// <param name="Ok">是否打码成功（回执成功 + 回执内容与下发一致）。</param>
/// <param name="Code">结果码（写入 %MW97502）。</param>
/// <param name="Message">失败/异常描述。</param>
/// <param name="RawReply">回执原文。</param>
/// <param name="EchoContent">从回执中提取到的内容。</param>
public sealed record MarkerResult(bool Ok, MarkResultCode Code, string? Message, string? RawReply, string? EchoContent)
{
	public static MarkerResult Fail(MarkResultCode code, string? message) => new(false, code, message, null, null);
}

/// <summary>读码结果。</summary>
/// <param name="Ok">是否读到数据（不含业务校验，业务校验在 ThaCodeValidator）。</param>
/// <param name="Code">归一化后的码。</param>
/// <param name="Raw">枪返回原文。</param>
/// <param name="Message">异常描述。</param>
/// <param name="CommError">true=通讯类异常（映射结果码 5）；false=未扫到（映射结果码 2）。</param>
public sealed record ScanRead(bool Ok, string? Code, string? Raw, string? Message, bool CommError)
{
	/// <summary>未扫到码（结果码 2）。</summary>
	public static ScanRead NoRead(string? message = null) => new(false, null, null, message, false);

	/// <summary>设备通讯异常 / 超时（结果码 5）。</summary>
	public static ScanRead Comm(string message) => new(false, null, null, message, true);
}

/// <summary>
/// 打标机抽象（大族激光，TCP/IP）。
/// 具体报文内容与流程待定 → 全部走配置（指令模板 / OK 关键字 / 回执内容正则），
/// 协议确定后只填配置，业务代码不动。
/// </summary>
public interface ILaserMarker
{
	/// <summary>设备模式（Tcp / Mock），仅用于日志与展示。</summary>
	string Mode { get; }

	/// <summary>是否已连接（Mock 恒为 true）。</summary>
	bool IsConnected { get; }

	/// <summary>下发内容并等回执。</summary>
	Task<MarkerResult> MarkAsync(string content, CancellationToken ct);
}

/// <summary>
/// 扫码枪抽象（基恩士 SR-1000，TCP/IP）。
/// 触发指令 / 终止符 / 数据格式待定 → 全部走配置；
/// Trigger=软触发（MES 发指令收数据）与 Push=硬触发（枪推数据）两种会话模型共用本接口。
/// </summary>
public interface IScanner
{
	/// <summary>设备模式（TcpTrigger / TcpPush / Mock）。</summary>
	string Mode { get; }

	/// <summary>是否已连接（Mock 恒为 true）。</summary>
	bool IsConnected { get; }

	/// <summary>触发一次读码并等数据。</summary>
	Task<ScanRead> ReadAsync(CancellationToken ct);
}

/// <summary>外设工厂：按配置选实现，配置变更后重建。</summary>
public static class ThaDevices
{
	public static ILaserMarker CreateLaser(AppConfig cfg)
		=> string.Equals(cfg.LaserMode, "Mock", StringComparison.OrdinalIgnoreCase)
			? new MockLaserMarker(cfg)
			: new TcpLaserMarker(cfg);

	public static IScanner CreateScanner(AppConfig cfg)
		=> string.Equals(cfg.ScannerMode, "Mock", StringComparison.OrdinalIgnoreCase)
			? new MockScanner(cfg)
			: new TcpScanner(cfg);
}

/// <summary>
/// 大族打标机 TCP 客户端（通用骨架）。
/// 流程：连接 → 发送 <c>LaserCommandTemplate</c>（{content} 占位）→ 读到终止符/超时 →
/// 按 <c>LaserOkKeyword</c> 判成功 → 按 <c>LaserEchoRegex</c> 提取回执内容并与下发内容比对。
/// 指令细节确定后只改配置。
/// </summary>
public sealed class TcpLaserMarker(AppConfig cfg) : ILaserMarker, IDisposable
{
	public string Mode => "Tcp";
	public bool IsConnected { get; private set; }

	private TcpClient? _client;
	private NetworkStream? _stream;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public async Task<MarkerResult> MarkAsync(string content, CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			var cmd = RenderCommand(cfg.LaserCommandTemplate, content);
			if (string.IsNullOrEmpty(cmd))
			{
				// 指令模板未配置 = 协议细节尚未落地：明确报错而不是假装成功
				IsConnected = false;
				return MarkerResult.Fail(MarkResultCode.InternalError, "LaserCommandTemplate 未配置（协议待定）");
			}

			if (!await EnsureConnectedAsync(ct))
				return MarkerResult.Fail(MarkResultCode.Timeout, $"连接打标机失败 {cfg.LaserIp}:{cfg.LaserPort}");

			try
			{
				var sendBytes = Encoding.GetEncoding(cfg.LaserEncoding).GetBytes(cmd);
				await _stream!.WriteAsync(sendBytes, ct);
				await _stream.FlushAsync(ct);

				var reply = await ReadUntilAsync(cfg.LaserTimeoutMs, ct);
				return Judge(content, reply);
			}
			catch (Exception ex)
			{
				Close();
				return MarkerResult.Fail(MarkResultCode.Timeout, $"打标通讯异常：{ex.Message}");
			}
		}
		finally
		{
			_gate.Release();
			if (!cfg.LaserKeepAlive) Close();
		}
	}

	/// <summary>回执判定：OK 关键字 + 回执内容核对（两段式，见方案 §3.3）。</summary>
	private MarkerResult Judge(string content, string reply)
	{
		if (string.IsNullOrEmpty(reply))
			return MarkerResult.Fail(MarkResultCode.Timeout, "未收到回执");

		var okKeyword = cfg.LaserOkKeyword;
		var hasOk = string.IsNullOrEmpty(okKeyword) || reply.Contains(okKeyword, StringComparison.OrdinalIgnoreCase);
		if (!hasOk)
			return new MarkerResult(false, MarkResultCode.LaserNg, "回执判定为 NG", reply, null);

		string? echo = null;
		if (!string.IsNullOrEmpty(cfg.LaserEchoRegex))
		{
			var m = Regex.Match(reply, cfg.LaserEchoRegex);
			if (m.Success)
				echo = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
		}

		// 回执带内容且能提取 → 做闭环核对；提取不到则降级为"只判回执成功"（不阻断产线）
		if (echo != null && echo.Length > 0)
		{
			if (!string.Equals(echo, content, StringComparison.Ordinal))
				return new MarkerResult(false, MarkResultCode.EchoMismatch, $"回执内容不符（下发 {content} / 回执 {echo}）", reply, echo);
		}

		return new MarkerResult(true, MarkResultCode.Ok, null, reply, echo);
	}

	private static string RenderCommand(string template, string content)
		=> string.IsNullOrEmpty(template) ? "" : template.Replace("{content}", content);

	private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
	{
		if (_client?.Connected == true && _stream != null)
		{
			IsConnected = true;
			return true;
		}
		Close();
		try
		{
			var client = new TcpClient();
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(cfg.LaserConnectTimeoutMs);
			await client.ConnectAsync(cfg.LaserIp, cfg.LaserPort, cts.Token);
			client.NoDelay = true;
			_client = client;
			_stream = client.GetStream();
			IsConnected = true;
			return true;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			LogMgr.Instance.Warn($"[THA-LASER] 连接 {cfg.LaserIp}:{cfg.LaserPort} 失败：{ex.Message}");
			return false;
		}
	}

	private async Task<string> ReadUntilAsync(int timeoutMs, CancellationToken ct)
	{
		var term = cfg.LaserReplyTerminator;
		var termBytes = string.IsNullOrEmpty(term) ? null : Encoding.GetEncoding(cfg.LaserEncoding).GetBytes(term);
		var buf = new byte[512];
		var sb = new StringBuilder();
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeoutMs);
		try
		{
			while (true)
			{
				var n = await _stream!.ReadAsync(buf, cts.Token);
				if (n <= 0) break;
				var text = Encoding.GetEncoding(cfg.LaserEncoding).GetString(buf, 0, n);
				sb.Append(text);
				if (termBytes != null && text.Contains(term, StringComparison.Ordinal)) break;
				// 未配终止符时：读到一段就返回，避免挂死
				if (termBytes == null) break;
			}
		}
		catch (OperationCanceledException)
		{
			// 超时：把已收到的部分作为回执返回
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Warn($"[THA-LASER] 读回执异常：{ex.Message}");
		}
		return sb.ToString();
	}

	private void Close()
	{
		try { _stream?.Dispose(); } catch { }
		try { _client?.Close(); } catch { }
		_stream = null;
		_client = null;
		IsConnected = false;
	}

	public void Dispose() => Close();
}

/// <summary>
/// 基恩士 SR-1000 TCP 客户端（通用骨架）。
/// Trigger 模式：连接 → 发 <c>ScannerTriggerCmd</c> → 读数据（去终止符/提取正则）→ 可选发停止指令。
/// Push 模式：等枪主动推数据（MES 只收）。
/// 指令与数据格式待定 → 全部走配置。
/// </summary>
public sealed class TcpScanner(AppConfig cfg) : IScanner, IDisposable
{
	public string Mode => cfg.ScannerMode;
	public bool IsConnected { get; private set; }

	private TcpClient? _client;
	private NetworkStream? _stream;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public async Task<ScanRead> ReadAsync(CancellationToken ct)
	{
		var trigger = !string.Equals(cfg.ScannerMode, "Push", StringComparison.OrdinalIgnoreCase);
		if (trigger && string.IsNullOrEmpty(cfg.ScannerTriggerCmd))
			return ScanRead.Comm("ScannerTriggerCmd 未配置（协议待定）");

		await _gate.WaitAsync(ct);
		try
		{
			if (!await EnsureConnectedAsync(ct))
				return ScanRead.Comm($"连接扫码枪失败 {cfg.ScannerIp}:{cfg.ScannerPort}");

			try
			{
				var enc = Encoding.GetEncoding(cfg.ScannerEncoding);
				if (trigger)
				{
					await _stream!.WriteAsync(enc.GetBytes(cfg.ScannerTriggerCmd + cfg.ScannerCommandEnd), ct);
					await _stream.FlushAsync(ct);
				}

				var raw = await ReadUntilAsync(cfg.ScannerTimeoutMs, ct);
				if (string.IsNullOrEmpty(raw))
					return ScanRead.NoRead("未收到扫码数据");

				var code = ExtractCode(raw, enc);
				return code.Length == 0 ? ScanRead.NoRead($"返回内容无法解析为码：{raw}") : new ScanRead(true, code, raw, null, false);
			}
			catch (Exception ex)
			{
				Close();
				return ScanRead.Comm($"扫码枪通讯异常：{ex.Message}");
			}
			finally
			{
				// 软触发后按配置发一次停止指令（读码器回到待触发态）
				if (trigger && !string.IsNullOrEmpty(cfg.ScannerStopCmd) && _stream != null)
				{
					try
					{
						var enc = Encoding.GetEncoding(cfg.ScannerEncoding);
						await _stream.WriteAsync(enc.GetBytes(cfg.ScannerStopCmd + cfg.ScannerCommandEnd), CancellationToken.None);
						await _stream.FlushAsync(CancellationToken.None);
					}
					catch { /* 停止指令失败不影响本次结果 */ }
				}
			}
		}
		finally
		{
			_gate.Release();
			if (!cfg.ScannerKeepAlive)
				Close();
		}
	}

	/// <summary>从原始返回里提取码：先按正则（有配置时），否则按终止符切分取首段。</summary>
	private string ExtractCode(string raw, Encoding enc)
	{
		string text = raw.Trim('\r', '\n', '\0', ' ');
		if (!string.IsNullOrEmpty(cfg.ScannerDataRegex))
		{
			var m = Regex.Match(text, cfg.ScannerDataRegex);
			if (m.Success)
				return (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
			return "";
		}

		var term = cfg.ScannerTerminator;
		if (!string.IsNullOrEmpty(term))
		{
			var idx = text.IndexOf(term, StringComparison.Ordinal);
			if (idx >= 0) text = text[..idx];
		}
		return ThaCodeValidator.Normalize(text);
	}

	private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
	{
		if (_client?.Connected == true && _stream != null)
		{
			IsConnected = true;
			return true;
		}
		Close();
		try
		{
			var client = new TcpClient();
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(cfg.ScannerConnectTimeoutMs);
			await client.ConnectAsync(cfg.ScannerIp, cfg.ScannerPort, cts.Token);
			client.NoDelay = true;
			_client = client;
			_stream = client.GetStream();
			IsConnected = true;
			return true;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			LogMgr.Instance.Warn($"[THA-SCANNER] 连接 {cfg.ScannerIp}:{cfg.ScannerPort} 失败：{ex.Message}");
			return false;
		}
	}

	private async Task<string> ReadUntilAsync(int timeoutMs, CancellationToken ct)
	{
		var enc = Encoding.GetEncoding(cfg.ScannerEncoding);
		var term = !string.IsNullOrEmpty(cfg.ScannerTerminator) ? cfg.ScannerTerminator : "\r";
		var buf = new byte[512];
		var sb = new StringBuilder();
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeoutMs);
		try
		{
			while (true)
			{
				var n = await _stream!.ReadAsync(buf, cts.Token);
				if (n <= 0) break;
				sb.Append(enc.GetString(buf, 0, n));
				if (sb.ToString().Contains(term, StringComparison.Ordinal)) break;
			}
		}
		catch (OperationCanceledException) { /* 超时 → 返回已收内容 */ }
		catch (Exception ex)
		{
			LogMgr.Instance.Warn($"[THA-SCANNER] 读数据异常：{ex.Message}");
		}
		return sb.ToString();
	}

	private void Close()
	{
		try { _stream?.Dispose(); } catch { }
		try { _client?.Close(); } catch { }
		_stream = null;
		_client = null;
		IsConnected = false;
	}

	public void Dispose() => Close();
}

/// <summary>
/// Mock 设备共享总线：让 Mock 扫码枪默认返回"打码站刚生成的那一个码"，
/// 模拟同一件产品从打码站流到读码站的真实现场（否则默认扫码结果必然是"未登记码"）。
/// 需要造异常场景时用 MockScanner.PendingCodes 注入特定内容覆盖。
/// </summary>
public static class MockDeviceBus
{
	/// <summary>打码站最近一次成功生成的码（Mock 扫码枪的默认返回值）。</summary>
	public static volatile string LastMarkedCode = "";
}

/// <summary>
/// 打标机模拟器（脱机开发/联调用）。
/// 可通过 <see cref="NextResultOverride"/> 与 <see cref="NextEchoOverride"/> 强制造出各种场景，
/// 供联调期验证 97502 的 1/2/3/6 四值是否正确回写。
/// </summary>
public sealed class MockLaserMarker(AppConfig cfg) : ILaserMarker
{
	public const string ModeName = "Mock";

	/// <summary>强制下一次打码的结果码（-1 = 按配置正常返回）。线程安全，取后即重置为 -1。</summary>
	public static int NextResultOverride = -1;

	/// <summary>强制下一次回执内容（用于制造"回执不符"）。</summary>
	public static volatile string? NextEchoOverride;

	public string Mode => ModeName;
	public bool IsConnected => true;

	public async Task<MarkerResult> MarkAsync(string content, CancellationToken ct)
	{
		if (cfg.LaserMockDelayMs > 0)
			await Task.Delay(cfg.LaserMockDelayMs, ct);

		var forced = Interlocked.Exchange(ref NextResultOverride, -1);
		var echoOverride = NextEchoOverride;
		NextEchoOverride = null;

		var echo = echoOverride ?? content;
		var raw = $"MOCK MARK OK {echo}";

		if (forced >= 0)
		{
			var code = (MarkResultCode)forced;
			return code switch
			{
				MarkResultCode.Ok => new MarkerResult(true, code, null, raw, echo),
				MarkResultCode.Timeout => MarkerResult.Fail(code, "模拟回执超时"),
				MarkResultCode.EchoMismatch => new MarkerResult(false, code, "模拟回执内容不符", raw, echo),
				_ => MarkerResult.Fail(code, $"模拟结果码 {forced}"),
			};
		}

		if (echo != content)
			return new MarkerResult(false, MarkResultCode.EchoMismatch, "模拟回执内容不符", raw, echo);

		return new MarkerResult(true, MarkResultCode.Ok, null, raw, echo);
	}
}

/// <summary>
/// 扫码枪模拟器（脱机开发/联调用）。
/// 默认返回"当前时间生成的合法时间码"（因此配合校验链天然通过）；
/// 通过 <see cref="PendingCodes"/> 可注入任意待返回内容（空串=模拟未扫到），
/// 通过 <see cref="NextError"/> 模拟枪通讯异常/超时。
/// </summary>
public sealed class MockScanner(AppConfig cfg) : IScanner
{
	public const string ModeName = "Mock";

	/// <summary>按顺序返回的预置内容；队空时自动生成合法时间码。空串代表"未扫到"。</summary>
	public static readonly System.Collections.Concurrent.ConcurrentQueue<string> PendingCodes = new();

	/// <summary>强制下一次返回的异常描述（null = 正常）。取后即清。</summary>
	public static volatile string? NextError;

	public string Mode => ModeName;
	public bool IsConnected => true;

	public async Task<ScanRead> ReadAsync(CancellationToken ct)
	{
		if (cfg.ScannerMockDelayMs > 0)
			await Task.Delay(cfg.ScannerMockDelayMs, ct);

		var err = NextError;
		NextError = null;
		if (err != null)
			return ScanRead.Comm(err);

		if (PendingCodes.TryDequeue(out var injected))
			return injected.Length == 0 ? ScanRead.NoRead("模拟未扫到") : new ScanRead(true, injected, injected, null, false);

		// 默认：返回打码站刚生成的码（同一件产品流转），无则生成当前时间的合法码
		var code = MockDeviceBus.LastMarkedCode;
		if (string.IsNullOrEmpty(code))
			code = ThaCodeGenerator.Format(DateTime.Now, cfg.CodeDateFormat);
		return new ScanRead(true, code, code, null, false);
	}
}
