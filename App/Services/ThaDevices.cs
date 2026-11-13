using System.Diagnostics;
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

/// <summary>外设连接状态（主页显示用，口径与 PLC 连接状态一致）。</summary>
public enum DeviceLinkStatus
{
	/// <summary>尚未探测（服务刚启动 / 适配器未初始化）。</summary>
	Unknown,
	/// <summary>已连接。</summary>
	Up,
	/// <summary>未连接。</summary>
	Down,
}

/// <summary>单个外设的连接状态（不可变，UI 只读）。</summary>
/// <param name="Status">连接状态。</param>
/// <param name="Endpoint">端点（IP:Port，仅日志/排查用，不上界面）。</param>
/// <param name="UpdateTime">本次探测时间。</param>
public sealed record DeviceLinkState(DeviceLinkStatus Status, string Endpoint, DateTime UpdateTime)
{
	public static DeviceLinkState Unknown(string endpoint = "--")
		=> new(DeviceLinkStatus.Unknown, endpoint, DateTime.Now);

	public static DeviceLinkState Up(string endpoint)
		=> new(DeviceLinkStatus.Up, endpoint, DateTime.Now);

	public static DeviceLinkState Down(string endpoint)
		=> new(DeviceLinkStatus.Down, endpoint, DateTime.Now);

	/// <summary>UI 显示文本。</summary>
	public string Text => Status switch
	{
		DeviceLinkStatus.Up => "已连接",
		DeviceLinkStatus.Down => "未连接",
		_ => "--",
	};
}

/// <summary>外设连接状态汇总（打标机 + 扫码枪）。</summary>
public sealed record DeviceLinkSnapshot(DeviceLinkState Laser, DeviceLinkState Scanner, DateTime UpdateTime)
{
	public static readonly DeviceLinkSnapshot Empty = new(
		DeviceLinkState.Unknown(), DeviceLinkState.Unknown(), DateTime.MinValue);
}

/// <summary>可探测连通性的外设（打标机 / 扫码枪共用）。</summary>
public interface IDeviceLink
{
	/// <summary>设备模式（Tcp / Mock），仅用于日志。</summary>
	string Mode { get; }

	/// <summary>是否已连接（Mock 恒为 true）。</summary>
	bool IsConnected { get; }

	/// <summary>
	/// 连通性探测：已持有长连接 → 直接返回已连接（不新建连接）；
	/// 否则用短超时建一次临时 TCP 连接后立即关闭；打码/读码作业进行中视为连接正常。
	/// </summary>
	Task<DeviceLinkState> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// 打标机抽象（大族激光，TCP/IP）。
/// 具体报文内容与流程待定 → 全部走配置（指令模板 / OK 关键字 / 回执内容正则），
/// 协议确定后只填配置，业务代码不动。
/// </summary>
public interface ILaserMarker : IDeviceLink
{
	/// <summary>下发内容并等回执。</summary>
	Task<MarkerResult> MarkAsync(string content, CancellationToken ct);
}

/// <summary>
/// 扫码枪抽象（基恩士 SR-1000，TCP/IP）。
/// 触发指令 / 终止符 / 数据格式待定 → 全部走配置；
/// Trigger=软触发（MES 发指令收数据）与 Push=硬触发（枪推数据）两种会话模型共用本接口。
/// </summary>
public interface IScanner : IDeviceLink
{
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
/// TCP 连通性探测：只建一次临时短连接，验完立即关闭 —— 不碰业务长连接。
/// </summary>
internal static class DeviceProbe
{
	public static async Task<DeviceLinkState> TcpAsync(string ip, int port, int timeoutMs, CancellationToken ct)
	{
		var endpoint = $"{ip}:{port}";
		if (string.IsNullOrWhiteSpace(ip) || port <= 0)
			return DeviceLinkState.Down(endpoint);

		try
		{
			using var client = new TcpClient();
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(timeoutMs <= 0 ? 1000 : timeoutMs);
			await client.ConnectAsync(ip, port, cts.Token);
			return DeviceLinkState.Up(endpoint);
		}
		catch
		{
			return DeviceLinkState.Down(endpoint);
		}
	}
}

/// <summary>
/// 大族打标机 TCP 客户端。
/// 三步流程：① 初始化模板 <c>$Initialize_&lt;模板名&gt;</c> → ② 下发数据 <c>$Data_&lt;内容&gt;</c>
/// → ③ 启动打标 <c>$MarkStart_</c>。三步模板均可配，留空的步骤自动跳过。
/// 回执：每步都必等回执 —— ① 初始化模板 / ② 下发数据用 <c>LaserStepTimeoutMs</c>，
/// ③ 启动打标用 <c>LaserTimeoutMs</c>。回执为空判 3（回执超时），回执不带
/// <c>LaserOkKeyword</c>（默认 OK）判 2（打标NG），并把步骤/指令/回执原文带回上层供弹窗。
/// 三步均通过后按 <c>LaserEchoRegex</c> 提取回执内容与下发内容做闭环核对。
/// </summary>
public sealed class TcpLaserMarker(AppConfig cfg) : ILaserMarker, IDisposable
{
	public string Mode => "Tcp";
	public bool IsConnected { get; private set; }

	private TcpClient? _client;
	private NetworkStream? _stream;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>连通性探测（主页状态显示）。作业中不另建连接，直接报已连接。</summary>
	public async Task<DeviceLinkState> ProbeAsync(CancellationToken ct)
	{
		var endpoint = $"{cfg.LaserIp}:{cfg.LaserPort}";
		if (_gate.CurrentCount == 0)
			return DeviceLinkState.Up(endpoint);

		if (_client?.Connected == true && _stream != null)
		{
			IsConnected = true;
			return DeviceLinkState.Up(endpoint);
		}

		return await DeviceProbe.TcpAsync(cfg.LaserIp, cfg.LaserPort, cfg.DeviceProbeTimeoutMs, ct);
	}

	public async Task<MarkerResult> MarkAsync(string content, CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			var steps = BuildSteps(content);
			if (steps.Count == 0)
			{
				// 三步模板全空 = 协议细节尚未落地：明确报错而不是假装成功
				IsConnected = false;
				return MarkerResult.Fail(MarkResultCode.InternalError, "打标指令模板全空（协议未配置）");
			}

			if (!await EnsureConnectedAsync(ct))
				return MarkerResult.Fail(MarkResultCode.Timeout, $"连接打标机失败 {cfg.LaserIp}:{cfg.LaserPort}");

			LogMgr.Instance.Debug($"[THA-LASER] 三步开始 内容={content} 帧={cfg.LaserFrameEnabled} ①②超时={cfg.LaserStepTimeoutMs}ms ③超时={cfg.LaserTimeoutMs}ms");

			try
			{
				var enc = Encoding.GetEncoding(cfg.LaserEncoding);
				var end = cfg.LaserCommandEnd ?? "";
				var replies = new List<string>(steps.Count);

				// 每步发完必等回执：无回执 → 3（回执超时）；回执未带 OK 关键字 → 2（打标NG）。
				// 回执原文带进 Message，供主页弹窗直接展示现场原因。
				foreach (var step in steps)
				{
					// 分步计时：清残留 / 写指令 / 等回执 三段分开记，慢在哪一段一眼看出
					var sw = Stopwatch.StartNew();

					// 帧模式下先清掉上一帧可能残留的字节（同一连接连发三条时避免串帧）
					if (cfg.LaserFrameEnabled) await DrainInputAsync(ct);
					var tDrain = sw.ElapsedMilliseconds;

					await _stream!.WriteAsync(BuildFrame(enc.GetBytes(step.Command + end)), ct);
					await _stream.FlushAsync(ct);
					var tWrite = sw.ElapsedMilliseconds;

					var reply = Clean(await ReadReplyAsync(step.TimeoutMs, ct));
					var tReply = sw.ElapsedMilliseconds;

					LogMgr.Instance.Debug($"[THA-LASER] {step.Name} 指令={step.Command} 清残留={tDrain}ms 写入={tWrite - tDrain}ms 等回执={tReply - tWrite}ms 回执={Clip(reply, 60)}");
					if (reply.Length == 0)
						return MarkerResult.Fail(MarkResultCode.Timeout,
							$"{step.Name}无回执（指令 {step.Command}）");

					if (!HasOkKeyword(reply))
						return new MarkerResult(false, MarkResultCode.LaserNg,
							$"{step.Name}回执未带 OK（指令 {step.Command} / 回执 {Clip(reply)}）", reply, null);

					replies.Add(reply);
				}

				return Judge(content, string.Join("\r\n", replies));
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

	/// <summary>打标步骤（名称 / 待发指令 / 等回执超时）。</summary>
	/// <param name="Name">步骤名（报错与日志用）。</param>
	/// <param name="Command">待发指令（已渲染占位）。</param>
	/// <param name="TimeoutMs">该步等回执超时。</param>
	private sealed record LaserStep(string Name, string Command, int TimeoutMs);

	// ===== 帧封装：0x02(STX) + ASCII 指令 + 0x03(ETX) =====
	// 大族标准帧；LaserFrameEnabled=false 时发/收都是裸 ASCII。
	private const byte Stx = 0x02;
	private const byte Etx = 0x03;

	/// <summary>发送前组帧：默认在指令前后包 0x02/0x03。</summary>
	private byte[] BuildFrame(byte[] body)
	{
		if (!cfg.LaserFrameEnabled || body.Length == 0) return body;
		var frame = new byte[body.Length + 2];
		frame[0] = Stx;
		Buffer.BlockCopy(body, 0, frame, 1, body.Length);
		frame[^1] = Etx;
		return frame;
	}

	/// <summary>
	/// 读一帧回执：帧模式下以 0x03 为结束标志（不用“读到一段就返回”，避免 TCP 分包丢字节），
	/// 拿到后剔掉 0x02/0x03；非帧模式维持原逻辑（按终止符或读到一段即返回）。
	/// </summary>
	private async Task<string> ReadReplyAsync(int timeoutMs, CancellationToken ct)
	{
		var enc = Encoding.GetEncoding(cfg.LaserEncoding);
		var framed = cfg.LaserFrameEnabled;
		var termBytes = (!framed && !string.IsNullOrEmpty(cfg.LaserReplyTerminator))
			? enc.GetBytes(cfg.LaserReplyTerminator)
			: null;

		var buf = new byte[512];
		using var ms = new MemoryStream(512);
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeoutMs);
		try
		{
			while (true)
			{
				var n = await _stream!.ReadAsync(buf, cts.Token);
				if (n <= 0) break;
				ms.Write(buf, 0, n);

				var data = ms.GetBuffer();
				var len = (int)ms.Length;
				if (framed)
				{
					// 帧尾 0x03 一到就是完整帧（一帧可能分多个 TCP 包到达）
					if (Array.IndexOf(data, Etx, 0, len) >= 0) break;
				}
				else if (termBytes != null)
				{
					if (enc.GetString(data, 0, len).Contains(cfg.LaserReplyTerminator, StringComparison.Ordinal)) break;
				}
				else
				{
					break; // 非帧模式且无终止符：读到一段就返回，避免挂死
				}
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

		return ExtractReply(ms.GetBuffer(), (int)ms.Length, enc);
	}

	/// <summary>从原始字节里取回执正文：帧模式只取 0x02 与 0x03 之间的部分。</summary>
	private string ExtractReply(byte[] raw, int len, Encoding enc)
	{
		if (len <= 0) return "";
		var start = 0;
		var end = len - 1;
		if (cfg.LaserFrameEnabled)
		{
			var stx = Array.IndexOf(raw, Stx, 0, len);
			var etx = Array.IndexOf(raw, Etx, 0, len);
			if (stx >= 0) start = stx + 1;
			if (etx > start) end = etx - 1;
			if (end < start) return "";
			// 尾包后紧跟下一条报文时，还能再遇到 0x03：只取第一帧
			var etx2 = Array.IndexOf(raw, Etx, start, end - start + 1);
			if (etx2 > start) end = etx2 - 1;
		}
		return enc.GetString(raw, start, end - start + 1);
	}

	/// <summary>清掉接收缓冲里残留的字节（上一帧之后的剩余数据），避免下一帧串位。</summary>
	private async Task DrainInputAsync(CancellationToken ct)
	{
		try
		{
			var buf = new byte[256];
			while (_stream != null && _stream.DataAvailable)
			{
				var n = await _stream.ReadAsync(buf, ct);
				if (n <= 0) break;
			}
		}
		catch { /* 清残留失败不影响本步收发 */ }
	}

	/// <summary>回执清理：去掉控制字符（含残留的 0x02/0x03）与首尾空白。</summary>
	private static string Clean(string s)
		=> s.Trim('\u0002', '\u0003', '\r', '\n', '\0', ' ', '\t');

	/// <summary>回执是否“带 OK”：未配关键字时只要求有回执。</summary>
	private bool HasOkKeyword(string reply)
		=> string.IsNullOrEmpty(cfg.LaserOkKeyword)
			|| reply.Contains(cfg.LaserOkKeyword, StringComparison.OrdinalIgnoreCase);

	/// <summary>回执截断（弹窗/log 不扭成一大段）。</summary>
	private static string Clip(string s, int max = 120)
		=> s.Length <= max ? s : s[..max] + "…";

	/// <summary>按配置拼出三步指令，留空的步骤跳过。</summary>
	private List<LaserStep> BuildSteps(string content)
	{
		var stepMs = cfg.LaserStepTimeoutMs <= 0 ? 800 : cfg.LaserStepTimeoutMs;
		var template = cfg.LaserTemplateName ?? "";
		var steps = new List<LaserStep>(3);

		var init = Render(cfg.LaserInitTemplate, template, content);
		if (init.Length > 0) steps.Add(new LaserStep("初始化模板", init, stepMs));

		var data = Render(cfg.LaserDataTemplate, template, content);
		if (data.Length > 0) steps.Add(new LaserStep("下发数据", data, stepMs));

		var start = Render(cfg.LaserStartTemplate, template, content);
		if (start.Length > 0) steps.Add(new LaserStep("启动打标", start, cfg.LaserTimeoutMs));

		return steps;
	}

	/// <summary>占位渲染：<c>{template}</c> → 模板名，<c>{content}</c> → 打标内容。</summary>
	private static string Render(string template, string templateName, string content)
		=> string.IsNullOrEmpty(template)
			? ""
			: template.Replace("{template}", templateName).Replace("{content}", content);


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

	/// <summary>连通性探测（主页状态显示）。作业中不另建连接，直接报已连接。</summary>
	public async Task<DeviceLinkState> ProbeAsync(CancellationToken ct)
	{
		var endpoint = $"{cfg.ScannerIp}:{cfg.ScannerPort}";
		if (_gate.CurrentCount == 0)
			return DeviceLinkState.Up(endpoint);

		if (_client?.Connected == true && _stream != null)
		{
			IsConnected = true;
			return DeviceLinkState.Up(endpoint);
		}

		return await DeviceProbe.TcpAsync(cfg.ScannerIp, cfg.ScannerPort, cfg.DeviceProbeTimeoutMs, ct);
	}

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

				// 枪回 error 包（默认 ERROR 等关键词）= **没扫上**，交给上层重试，
				// 不能当成"扫上了但内容不对"（那会直接判错码且不重试）。
				if (ThaScanJudge.IsNoReadReply(raw, cfg.ScannerNoReadKeywords))
					return ScanRead.NoRead($"扫码枪回包 {raw.Trim()}（没扫上）");

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

	/// <summary>
	/// 读一包扫码数据。三条收尾路径：
	/// ① 收到终止符 <c>ScannerTerminator</c>（默认 \r）→ 立刻返回；
	/// ② <c>ScannerSilenceMs &gt; 0</c> 时，收到数据后连续这么久没有新字节 → 认为一包发完（枪回 ERROR 不带终止符时靠这条）；
	/// ③ 一直没数据 → 等满 <c>ScannerTimeoutMs</c> 返回空串（上层判"没扫上"并重试）。
	/// </summary>
	private async Task<string> ReadUntilAsync(int timeoutMs, CancellationToken ct)
	{
		var enc = Encoding.GetEncoding(cfg.ScannerEncoding);
		var term = !string.IsNullOrEmpty(cfg.ScannerTerminator) ? cfg.ScannerTerminator : "\r";
		var silenceMs = cfg.ScannerSilenceMs;
		var buf = new byte[512];
		var sb = new StringBuilder();
		using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		totalCts.CancelAfter(timeoutMs);
		using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token, idleCts.Token);
		try
		{
			while (true)
			{
				var n = await _stream!.ReadAsync(buf, linked.Token);
				if (n <= 0) break;
				sb.Append(enc.GetString(buf, 0, n));
				if (sb.ToString().Contains(term, StringComparison.Ordinal)) break;
				// 收到数据了：起/续静默计时（首字节前不限时，由 totalCts 兜总超时）
				if (silenceMs > 0) idleCts.CancelAfter(silenceMs);
			}
		}
		catch (OperationCanceledException)
		{
			// 总超时（一个字节没收到）或静默到期（一包收完）→ 返回已收内容
			if (sb.Length > 0)
				LogMgr.Instance.Debug($"[THA-SCANNER] 静默 {silenceMs}ms 收尾，收到 {sb.Length} 字符");
		}
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

	public Task<DeviceLinkState> ProbeAsync(CancellationToken ct)
		=> Task.FromResult(DeviceLinkState.Up("Mock"));

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

	public Task<DeviceLinkState> ProbeAsync(CancellationToken ct)
		=> Task.FromResult(DeviceLinkState.Up("Mock"));

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

		// 默认：返回打码站刚生成的码（同一件产品流转），无则按当前配置（含分段/批次号）生成一个合法码
		var code = MockDeviceBus.LastMarkedCode;
		if (string.IsNullOrEmpty(code))
			code = ThaCodeGenerator.Generate(cfg, 200);
		if (string.IsNullOrEmpty(code))
			code = ThaCodeGenerator.Format(DateTime.Now, cfg.CodeDateFormat);
		return new ScanRead(true, code, code, null, false);
	}
}
