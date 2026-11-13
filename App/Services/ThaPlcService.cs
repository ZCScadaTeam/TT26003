using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using AppZC.Configs;
using AppZC.Models.DB;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.ModBus;
using HslCommunication.Profinet.Inovance;
using ZC.EnhanceApp;

namespace AppZC.Services;

/// <summary>
/// THA26003 三站作业服务（ModbusTCP / 汇川 InovanceTcpNet + 打码站 + 读码站 + 上传站）。
///
/// 线程模型（关键设计，见方案 §2.1）：
///   ┌ PLC IO 循环（唯一持有 Modbus 客户端，500ms 一轮，全串行）
///   │   1 写心跳 %MW20502 = 1
///   │   2 回写上一轮作业产生的结果（PendingWrites）
///   │   3 一次读 %MW97500 起 41 WORD，拿齐三站触发位
///   │   4 任一触发 == 1 → 立即写 0 复位 → 入作业队列
///   │   5 读结果段刷新实时快照
///   └ 作业消费线程（单消费者串行，每作业带超时）
///       打码 / 读码是秒级操作，绝不能放进 IO 循环，
///       否则 0.5s 心跳停跳、另外两站触发漏采。
///
/// Modbus 客户端只在 IO 循环里被访问 → 不需要锁（ModbusTcpNet 非线程安全）。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class ThaPlcService() : MainTaskService("THA26003-PLC"), IAutoStartTaskService
{
	[Inject] public AppConfig? AppConfig { get; set; }

	private InovanceTcpNet? _client;
	private bool _connected;
	private int _heartbeat;
	private int _reconnectRequestedFlag;
	private long _lastFailLogTicks;
	private readonly SemaphoreSlim _reconnectGate = new(1, 1);

	private volatile ILaserMarker? _laser;
	private volatile IScanner? _scanner;
	private string _laserMode = "";
	private string _scannerMode = "";

	/// <summary>外设连接状态快照（主页显示，由独立探测循环整体替换）。</summary>
	private volatile DeviceLinkSnapshot _links = DeviceLinkSnapshot.Empty;

	/// <summary>打标机 / 扫码枪连接状态（独立探测线程写入，UI 只读）。</summary>
	public DeviceLinkSnapshot Links => _links;

	/// <summary>
	/// 作业队列：**按站各一条**，每站一个独立消费循环 —— 打码机与扫码枪是两台独立设备
	/// （不同 IP/端口，设备层各自的 SemaphoreSlim 只锁自己那条连接），打码与读码可以真并行。
	/// 同一站内仍串行（PLC 一件一件给触发，站内本来就不该并发）。
	/// 上限 4：站内堆积说明逻辑有问题，丢弃并报错。
	/// </summary>
	private static Channel<StationJob> NewJobQueue() => Channel.CreateBounded<StationJob>(
		new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });

	private readonly Channel<StationJob> _markJobs = NewJobQueue();
	private readonly Channel<StationJob> _scanJobs = NewJobQueue();
	private readonly Channel<StationJob> _uploadJobs = NewJobQueue();

	/// <summary>待回写的 Modbus 写操作（作业线程产出 → IO 循环执行）。</summary>
	private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();

	/// <summary>当前在制产品的上下文（打码站建立 → 读码站补充 → 上传站消费后重置）。</summary>
	// ===== 在制上下文：打码站与读码站**各自独立**一份，不再共用一个可变对象 =====
	// 打码与读码现在并行跑，共用 _ctx 会出现「打码B 顶掉 扫码A 的上下文 → 上传落库把 A 的扫码结果记到 B 上」。
	// 拆成两个不可变 record：写用 Volatile.Write（引用赋值原子），上传站用 Interlocked.Exchange
	// 「读并清空」，因此不需要额外加锁，也不会读到一个被并发改写的半边对象。
	/// <summary>打码站产出（上传站消费后清空）。Content = 本件下发给打标机的打标原文（镭码内容），Ok 才有 Code。</summary>
	private sealed record MarkCtx(string? Code, DateTime? MarkTime, string? Content);

	/// <summary>读码站产出（上传站消费后清空）。ScannedCode 为纯码，ScanLevel 为码等级（无等级为 null）。</summary>
	private sealed record ScanCtx(int? ScanResult, DateTime? ScanTime, string? ScannedCode, string? ScanRaw, string? ScanLevel);

	private static readonly MarkCtx EmptyMarkCtx = new(null, null, null);
	private static readonly ScanCtx EmptyScanCtx = new(null, null, null, null, null);

	private MarkCtx _markCtx = EmptyMarkCtx;
	private ScanCtx _scanCtx = EmptyScanCtx;

	/// <summary>累计 OK 数（内存计数，程序重启后清零）。</summary>
	public int TotalOk { get; private set; }

	/// <summary>累计 NG 数。</summary>
	public int TotalNg { get; private set; }

	/// <summary>是否已连接 PLC。</summary>
	public bool IsConnected => _connected;

	/// <summary>只读快照（监控页/主页轮询）。整体替换，无锁。</summary>
	public ThaStationSnapshot Snapshot { get; private set; } = ThaStationSnapshot.Empty;

	/// <summary>上传落库完成事件（作业线程触发，订阅方必须切 UI 线程）。</summary>
	public event Action<ThaProductRecord>? OnProductRecord;

	/// <summary>
	/// 已落库记录被回填/更正的事件（读码晚于落库时补读回填），作业线程触发，
	/// 订阅方按 <see cref="ThaProductRecord.Id"/> 原位刷新行，不要头插新行。
	/// </summary>
	public event Action<ThaProductRecord>? OnProductRecordUpdated;

	/// <summary>
	/// 外设作业告警（打码回执未带 OK / 无回执 / 连接失败等），作业线程触发，
	/// 订阅方（主页）负责切 UI 线程并弹窗提示。
	/// </summary>
	public event Action<ThaDeviceAlarm>? OnDeviceAlarm;

	/// <summary>配置变更通知（配置页保存后调用，下一轮 IO 循环热重连 + 重建外设）。</summary>
	public void NotifyConfigChanged()
	{
		Interlocked.Exchange(ref _reconnectRequestedFlag, 1);
	}

	/// <summary>
	/// 无界面运行（联调 / <c>--simtest</c> 自检用）：等价于框架启动本服务，但不依赖 Avalonia UI。
	/// 需先外部赋好 <see cref="AppConfig"/>。
	/// </summary>
	public Task RunHeadlessAsync(CancellationToken ctk) => Main(ctk);

	/// <summary>不在 OnStart 中连接：连接失败不应挂起服务，重试全部在 IO 循环内进行。</summary>
	protected override ValueTask<Result> OnStart(object? context = null, CancellationToken ctk = default)
		=> default;

	protected override async Task Main(CancellationToken ctk)
	{
		// 三站各一个常驻消费循环：打码 / 读码 / 上传互不阻塞
		var markWorker = Task.Run(() => StationLoopAsync(StationKind.Mark, _markJobs.Reader, ctk), ctk);
		var scanWorker = Task.Run(() => StationLoopAsync(StationKind.Scan, _scanJobs.Reader, ctk), ctk);
		var uploadWorker = Task.Run(() => StationLoopAsync(StationKind.Upload, _uploadJobs.Reader, ctk), ctk);
		// 外设状态探测独立成线程：探测带 1s 级超时，绝不能放进 500ms PLC 轮询循环，
		// 否则 PLC 心跳停跳、触发位漏采。
		var linkProbe = Task.Run(() => LinkProbeLoopAsync(ctk), ctk);
		try
		{
			await IoLoopAsync(ctk);
		}
		finally
		{
			_markJobs.Writer.TryComplete();
			_scanJobs.Writer.TryComplete();
			_uploadJobs.Writer.TryComplete();
			foreach (var w in new[] { markWorker, scanWorker, uploadWorker })
			{
				try { await w; } catch { /* 退出时忽略 */ }
			}
			try { await linkProbe; } catch { /* 退出时忽略 */ }
			try { _client?.ConnectClose(); } catch { }
			(_laser as IDisposable)?.Dispose();
			(_scanner as IDisposable)?.Dispose();
		}
	}

	// ============================================================
	// IO 循环
	// ============================================================

	private async Task IoLoopAsync(CancellationToken ctk)
	{
		while (!ctk.IsCancellationRequested)
		{
			try
			{
				// 单轮 IO 抽成独立方法：失败路径直接 return，**不再用 continue**
				// （continue 会跳过循环尾部的 500ms 延时 → 断线时变成紧密重连风暴）
				await PollOnceAsync(ctk);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				MarkDisconnected(ex.Message);
			}

			try { await Task.Delay(500, ctk); }
			catch (OperationCanceledException) { break; }
		}
	}

	/// <summary>单轮 PLC 交互：心跳 → 回写结果 → 读三站触发 → 入队作业 → 刷新快照。</summary>
	private async Task PollOnceAsync(CancellationToken ctk)
	{
	if (!_connected || Interlocked.CompareExchange(ref _reconnectRequestedFlag, 0, 0) == 1)
	{
		await ReconnectAsync();
		if (!_connected)
		{
			await Task.Delay(3000, ctk);
			return;
		}
	}

	var cfg = AppConfig;
	if (cfg == null)
	{
		await Task.Delay(500, ctk);
		return;
	}

	// 1) 心跳
	var hb = await _client!.WriteAsync(R(Tha26003.MwHeartbeat), (short)1);
	if (!hb.IsSuccess)
	{
		MarkDisconnected(hb.Message);
		return;
	}
	_heartbeat = 1;

	// 2) 回写上一轮作业结果（Modbus 只在本循环访问）
	while (_pendingWrites.TryDequeue(out var pending))
	{
		var w = await _client.WriteAsync(pending.Reg, pending.Values);
		if (!w.IsSuccess)
			LogFailOnce($"[THA-PLC] 结果回写失败 {pending.Reg}：{w.Message}");
	}

	// 3) 一次读信号块（%MW10500~10540）
	var sig = await _client.ReadAsync(R(Tha26003.MwSignalBlockBase), Tha26003.SignalBlockLength);
	if (!sig.IsSuccess || sig.Content == null || sig.Content.Length < Tha26003.SignalBlockLength * 2)
	{
		MarkDisconnected(sig.Message);
		return;
	}

	int markTrig = Tha26003.Word(sig.Content, Tha26003.OffMarkTrigger);
	int markRes = Tha26003.Word(sig.Content, Tha26003.OffMarkResult);
	int scanTrig = Tha26003.Word(sig.Content, Tha26003.OffScanTrigger);
	int scanRes = Tha26003.Word(sig.Content, Tha26003.OffScanResult);
	// 上传（采集）触发位独立在 %MW10700 区 —— PLC 工程师要求：不能与扫码共用一个地址，
	// 因为前面 NG 的产品不扫码但同样要上传，故单独读一小块 10700~10702。
	var uploadSig = await _client.ReadAsync(R(Tha26003.MwUploadBlockBase), Tha26003.UploadBlockLength);
	int unloadTrig = 0, unloadRes = 0;
	if (uploadSig.IsSuccess && uploadSig.Content is { Length: >= Tha26003.UploadBlockLength * 2 })
	{
		unloadTrig = Tha26003.Word(uploadSig.Content, Tha26003.OffUploadTrig);
		unloadRes = Tha26003.Word(uploadSig.Content, Tha26003.OffUploadRes);
	}
	else
	{
		LogFailOnce($"[THA-PLC] 读上传站信号块 {Tha26003.MwUploadBlockBase} 失败：{uploadSig.Message}");
	}

	// 4a) 打码触发（内容一律由 MES 按「码规则 → 产品码分段」生成）
	if (markTrig == 1)
	{
		await WriteNowAsync(R(Tha26003.MwMarkTrigger), 0);
		Enqueue(new StationJob(StationKind.Mark, null));
		LogMgr.Instance.Info("[THA-PLC] 打码触发");
	}

	// 4b) 读码触发：先把 PLC 附带的前站镭码（%MW10580）读回，随作业一起入队 ——
	//     Modbus 连接只在本 IO 循环里用，作业线程不能自己去读。
	if (scanTrig == 1)
	{
		await WriteNowAsync(R(Tha26003.MwScanTrigger), 0);
		var plcCode = cfg.PlcCodeCompareEnabled
			? await ReadPlcCarriedCodeAsync(cfg)
			: null;
		Enqueue(new StationJob(StationKind.Scan, null, plcCode));
		LogMgr.Instance.Info($"[THA-PLC] 读码触发（PLC附带码={plcCode ?? "(未读取/空)"}）");
	}

	// 4c) 上传触发：数据区已被 PLC 冻结，本循环内立刻把全量数据读回，
	//     再交给作业线程解析落库（读操作必须在 IO 循环内完成）。
	if (unloadTrig == 1)
	{
		await WriteNowAsync(R(Tha26003.MwUploadTrigger), 0);
		LogMgr.Instance.Info("[THA-PLC] 上传触发，分段读取数据区");
		var segments = await ReadMeasurementSegmentsAsync();
		if (segments == null)
		{
			// 读失败也必须回写结果 → 否则数据区被永久冻结、产线停线
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.Failed }));
			LogMgr.Instance.Error("[THA-PLC] 上传数据区读取失败，已回写失败码解冻数据区");
			RaiseAlarm("采集失败（上传站）",
				$"原因：分段读取数据区失败（已回写失败码解冻数据区）\n"
				+ $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		}
		else
		{
			Enqueue(new StationJob(StationKind.Upload, segments));
		}
	}

	// 5) 实时快照：结果段 + 信号位
	var res = await _client.ReadAsync(R(Tha26003.SegResultBase), Tha26003.SegResultLen);
	if (res.IsSuccess && res.Content is { Length: >= Tha26003.SegResultLen * 2 })
	{
		UpdateSnapshot(markTrig, markRes, scanTrig, scanRes, unloadTrig, unloadRes,
			Tha26003.Word(res.Content, Tha26003.OffWithstand),
			Tha26003.Word(res.Content, Tha26003.OffPinHeight),
			Tha26003.Word(res.Content, Tha26003.OffPosition));
	}
	else
	{
		UpdateSnapshot(markTrig, markRes, scanTrig, scanRes, unloadTrig, unloadRes, -1, -1, -1);
	}
}

	// ============================================================
	// 外设连接状态探测（主页：扫码枪 / 打标机）
	// ============================================================

	/// <summary>
	/// 周期性探测打标机与扫码枪的连通性，结果写入 <see cref="Links"/> 供主页显示。
	/// 与 PLC 主循环完全解耦：探测失败/超时只影响状态显示，不影响采集与打码读码。
	/// </summary>
	private async Task LinkProbeLoopAsync(CancellationToken ctk)
	{
		var logged = false;
		while (!ctk.IsCancellationRequested)
		{
			var cfg = AppConfig;
			var interval = Math.Clamp(cfg?.DeviceProbeIntervalMs ?? 5000, 1000, 60000);
			try
			{
				if (!logged && cfg != null)
				{
					logged = true;
					LogMgr.Instance.Info($"[THA-LINK] 外设连接状态探测：周期={interval}ms"
						+ $" 超时={Math.Clamp(cfg.DeviceProbeTimeoutMs, 200, 30000)}ms"
						+ $"；打码机={LaserEndpoint(cfg)}，扫码枪={ScannerEndpoint(cfg)}");
				}

				var laserState = await ProbeSafeAsync(_laser, ctk);
				var scannerState = await ProbeSafeAsync(_scanner, ctk);
				_links = new DeviceLinkSnapshot(laserState, scannerState, DateTime.Now);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[THA-LINK] 外设状态探测异常：{ex.Message}");
			}

			try { await Task.Delay(interval, ctk); }
			catch (OperationCanceledException) { break; }
		}
	}

	/// <summary>单设备探测包一层：任何异常都降级为“未连接”，绝不向上抛。</summary>
	private async Task<DeviceLinkState> ProbeSafeAsync(IDeviceLink? device, CancellationToken ctk)
	{
		if (device == null)
			return DeviceLinkState.Unknown();

		var cfg = AppConfig;
		var timeout = Math.Clamp(cfg?.DeviceProbeTimeoutMs ?? 1000, 200, 30000);
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctk);
			cts.CancelAfter(timeout + 500);
			return await device.ProbeAsync(cts.Token);
		}
		catch
		{
			return DeviceLinkState.Down($"{device.Mode}");
		}
	}

	private static string LaserEndpoint(AppConfig? cfg) => cfg == null ? "--" : $"{cfg.LaserIp}:{cfg.LaserPort}";

	private static string ScannerEndpoint(AppConfig? cfg) => cfg == null ? "--" : $"{cfg.ScannerIp}:{cfg.ScannerPort}";

	/// <summary>重连：按当前 AppConfig 新建长连接客户端，并按配置重建外设适配器。</summary>
	private async Task ReconnectAsync()
	{
		await _reconnectGate.WaitAsync();
		try
		{
			var cfg = AppConfig;
			if (cfg == null) return;

			Interlocked.Exchange(ref _reconnectRequestedFlag, 0);

			// 地址映射：%MW 编程地址 → Modbus 寄存器号（%MW97002 等超过 Modbus 16 位地址空间，
			// 必须按现场映射表换算；默认规则 %MW90000~99999 → 寄存器 0~9999）
			var newMap = ThaAddressMap.Create(cfg);
			var mapDesc = newMap.Describe(Tha26003.MwSignalBlockBase) + "；" + newMap.Describe(Tha26003.SegTopPosBase);
			if (mapDesc != _mapDesc)
			{
				_mapDesc = mapDesc;
				LogMgr.Instance.Info($"[THA-PLC] 地址映射：{mapDesc}");
			}
			_map = newMap;

			// 打标码结构：改段配置/清零时刻后日志会自动变，现场一眼就能核对「现在到底按哪套跑」
			var codeDesc = ThaCodeAssembly.Describe(cfg);
			if (codeDesc != _codeDesc)
			{
				_codeDesc = codeDesc;
				LogMgr.Instance.Info($"[THA-CODE] 打标码结构：{codeDesc}");
			}

			// 码等级判定阈值：改配置后重打一行，现场能从日志确认 C 到底判不判 NG
			var gradeDesc = string.IsNullOrWhiteSpace(cfg.CodeGradeThreshold)
				? "不做等级判定（只看格式/PLC码比对/重码）"
				: $"A~{cfg.CodeGradeThreshold.Trim().ToUpperInvariant()} 合格，其余判 NG";
			if (gradeDesc != _gradeDesc)
			{
				_gradeDesc = gradeDesc;
				LogMgr.Instance.Info($"[THA-SCAN] 码等级阈值={gradeDesc}");
			}

			// 外设：模式变化时重建
			if (_laser == null || _laserMode != cfg.LaserMode)
			{
				(_laser as IDisposable)?.Dispose();
				_laser = ThaDevices.CreateLaser(cfg);
				_laserMode = cfg.LaserMode;
				LogMgr.Instance.Info($"[THA-LASER] 使用打标机模式 {_laserMode}（{cfg.LaserIp}:{cfg.LaserPort}）");
			}
			if (_scanner == null || _scannerMode != cfg.ScannerMode)
			{
				(_scanner as IDisposable)?.Dispose();
				_scanner = ThaDevices.CreateScanner(cfg);
				_scannerMode = cfg.ScannerMode;
				LogMgr.Instance.Info($"[THA-SCANNER] 使用扫码枪模式 {_scannerMode}（{cfg.ScannerIp}:{cfg.ScannerPort}）");
			}

			var ip = cfg.PlcIpAddress;
			var port = cfg.PlcPort;
			try
			{
				_client?.ConnectClose();
				// 汇川 InovanceTcpNet（派生自 ModbusTcpNet）：
				//   Series 决定汇川侧的 32 位字序习惯；本项目所有数值字段都是自己按字节拆装的
				//   （整块读 + 手工解析），所以再显式指定 DataFormat 以保持解析口径固定、不受 Series 影响。
				var series = ParseInovanceSeries(cfg.InovanceSeries);
				var client = new InovanceTcpNet(series, ip, port, 1)
				{
					AddressStartWithZero = true, // "10500" 直读 %MW10500
					IsStringReverse = false,     // 字符串区不反转（与现场 ReadInt32 示例一致）
					DataFormat = DataFormat.CDAB,
					ConnectTimeOut = 2000,
					ReceiveTimeOut = 2000,
				};
				client.SetPersistentConnection();
				var r = await client.ConnectServerAsync();
				if (r.IsSuccess)
				{
					_client = client;
					_connected = true;
					LogMgr.Instance.Info($"[THA-PLC] 已连接 {ip}:{port}（InovanceTcpNet/{series}）");
				}
				else
				{
					client.ConnectClose();
					_connected = false;
					_client = null;
					LogFailOnce($"[THA-PLC] 连接失败 {ip}:{port}：{r.Message}");
				}
			}
			catch (Exception ex)
			{
				_connected = false;
				LogFailOnce($"[THA-PLC] 连接异常 {ip}:{port}：{ex.Message}");
			}
		}
		finally
		{
			_reconnectGate.Release();
		}
	}

	/// <summary>IO 循环内的同步写（复位触发位等），失败限流记日志。</summary>
	private async Task WriteNowAsync(string reg, short value)
	{
		try
		{
			if (_client == null) return;
			var r = await _client.WriteAsync(reg, value);
			if (!r.IsSuccess)
				LogFailOnce($"[THA-PLC] 写 {reg} 失败：{r.Message}");
		}
		catch (Exception ex)
		{
			LogFailOnce($"[THA-PLC] 写 {reg} 异常：{ex.Message}");
		}
	}


	/// <summary>
	/// 上传：分段读全量测量数据（%MW10002~10379 共 378 WORD，超 FC03 单请求 125 WORD 上限 → 切 5 段，合计 187 WORD）。
	/// **允许个别段失败** —— 失败的段返回 null，
	/// 对应字段落库为 NULL（与"读到 0"区分），其余段照常落库，不整单丢弃。
	/// </summary>
	private async Task<byte[]?[]> ReadMeasurementSegmentsAsync()
	{
		var a = await ReadWithRetryAsync(R(Tha26003.SegResultBase), Tha26003.SegResultLen);
		var b = await ReadWithRetryAsync(R(Tha26003.SegTopPinBase), Tha26003.SegTopPinLen);
		var c = await ReadWithRetryAsync(R(Tha26003.SegBottomPinBase), Tha26003.SegBottomPinLen);
		var d = await ReadWithRetryAsync(R(Tha26003.SegTopPosBase), Tha26003.SegTopPosLen);
		var e = await ReadWithRetryAsync(R(Tha26003.SegBottomPosBase), Tha26003.SegBottomPosLen);
		if (a == null && b == null && c == null && d == null && e == null)
		{
			LogMgr.Instance.Error("[THA-PLC] 上传全部分段读取失败");
			return new byte[]?[] { null, null, null, null, null }; // 仍交给作业线程落库+回写，避免数据区不解冻
		}
		return new[] { a, b, c, d, e };
	}

	private async Task<byte[]?> ReadWithRetryAsync(string baseAddress, ushort length, int retries = 3)
	{
		for (int attempt = 1; attempt <= retries; attempt++)
		{
			try
			{
				if (_client == null) return null;
				var r = await _client.ReadAsync(baseAddress, length);
				if (r.IsSuccess && r.Content != null && r.Content.Length >= length * 2)
					return r.Content;
				LogMgr.Instance.Warn($"[THA-PLC] 读 {baseAddress}({length}) 失败(第{attempt}次)：{r.Message}");
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[THA-PLC] 读 {baseAddress}({length}) 异常(第{attempt}次)：{ex.Message}");
				MarkDisconnected(ex.Message);
				return null;
			}
		}
		return null;
	}

	// ============================================================
	// 作业消费线程
	// ============================================================

	/// <summary>单站作业消费循环（每站一个，站内串行、站间并行）。</summary>
	private async Task StationLoopAsync(StationKind kind, ChannelReader<StationJob> reader, CancellationToken ctk)
	{
		while (!ctk.IsCancellationRequested)
		{
			StationJob job;
			try
			{
				job = await reader.ReadAsync(ctk);
			}
			catch (Exception)
			{
				break; // 队列关闭或取消
			}

			// 站内串行：这里拿到的时刻 - 触发入队时刻 = 被同一站前一个作业挡住的排队时间。
			// 「触发 → 完成」的墙钟 = 排队 + 自身耗时；现在跨站已并行，不会互相叠加。
			if (job.EnqueuedTicks > 0)
			{
				var waitMs = Environment.TickCount64 - job.EnqueuedTicks;
				if (waitMs >= 50)
					LogMgr.Instance.Warn($"[THA-JOB] {StationName(kind)}排队 {waitMs}ms 才开始执行（本站前一个作业还没结束）");
			}

			try
			{
				switch (kind)
				{
					case StationKind.Mark:
						await RunMarkAsync(job, ctk);
						break;
					case StationKind.Scan:
						await RunScanAsync(job, ctk);
						break;
					default:
						RunUpload(job.Segments!);
						break;
				}
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"[THA-JOB] {StationName(kind)}作业异常：{ex.Message}", ex);
				// 打码异常不写 10502（保持 0），避免 PLC 误判为「已完成」
				var pending = kind switch
				{
					StationKind.Mark => (PendingWrite?)null,
					StationKind.Scan => new PendingWrite(R(Tha26003.MwScanResult), new short[] { (short)ScanResultCode.Ng }),
					_ => new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.Failed }),
				};
				if (pending != null)
					_pendingWrites.Enqueue(pending);
			}
		}
	}

	/// <summary>作业队列里的站别中文名（日志用）。</summary>
	private static string StationName(StationKind kind) => kind switch
	{
		StationKind.Mark => "打码",
		StationKind.Scan => "读码",
		_ => "上传",
	};

	private void Enqueue(StationJob job)
	{
		job = job with { EnqueuedTicks = Environment.TickCount64 };
		var ok = job.Kind switch
		{
			StationKind.Mark => _markJobs.Writer.TryWrite(job),
			StationKind.Scan => _scanJobs.Writer.TryWrite(job),
			_ => _uploadJobs.Writer.TryWrite(job),
		};
		if (!ok)
			LogMgr.Instance.Error($"[THA-PLC] {StationName(job.Kind)}作业队列已满，丢弃作业");
	}

	// ============================================================
	// 作业消费线程
	// ============================================================

	/// <summary>
	/// 读 PLC 附带前站镭码内容（<c>%MW10580</c>，默认 40 WORD = 80 字节 ASCII）。
	/// **只能在 IO 循环线程调用**（Modbus 连接归 IO 循环独占）。读失败返回 null，不抛异常。
	/// </summary>
	private async Task<string?> ReadPlcCarriedCodeAsync(AppConfig cfg)
	{
		var words = (ushort)Math.Clamp(cfg.PlcCodeWords, 1, 120);
		var res = await _client.ReadAsync(R(cfg.PlcCodeAddress), words);
		if (!res.IsSuccess || res.Content is not { Length: > 0 })
		{
			LogFailOnce($"[THA-PLC] 读 PLC 附带镭码 {cfg.PlcCodeAddress} 失败：{res.Message}");
			return null;
		}

		var text = Tha26003.ParseAscii(res.Content, 0, res.Content.Length, Tha26003.ProductCodeSwapWords);
		return string.IsNullOrWhiteSpace(text) ? null : text;
	}

	// ----- 打码站 -----

	private async Task RunMarkAsync(StationJob job, CancellationToken ctk)
	{
		var cfg = AppConfig!;
		var sw = Stopwatch.StartNew();
		var requestTime = DateTime.Now;

		// 1) 取内容：按「码规则 → 产品码分段」拼装（未配置分段则回落纯时间码）
		var content = ThaCodeGenerator.Generate(cfg, cfg.CodeMaxWaitMs);

		if (string.IsNullOrWhiteSpace(content))
		{
			LogMgr.Instance.Error("[THA-MARK] 生成唯一码失败（等待窗口内始终冲突）");
			ThaDb.LogMark(new ThaMarkLog
			{
				Result = (int)MarkResultCode.GenerateFailed,
				Message = "生成唯一码失败",
				RequestTime = requestTime,
				DurationMs = (int)sw.ElapsedMilliseconds,
				LaserMode = _laser?.Mode,
			});
			// 打码未完成 → **不写 %MW10502**（保持 0），只弹窗 + 记流水
			RaiseAlarm("打码失败（大族打标机）：码生成失败",
				$"原因：等待窗口内始终取不到唯一时间码（同秒冲突）\n"
				+ $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			return;
		}

		// 2) 回写打码内容给 PLC（让 PLC 能显示/绑定本件码）
		_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwMarkEcho), Tha26003.EncodeAsciiWords(content, Tha26003.MarkEchoWords)));

		// 3) 下发打标并等回执
		var marker = _laser;
		MarkerResult result;
		if (marker == null)
		{
			result = MarkerResult.Fail(MarkResultCode.InternalError, "打标机适配器未初始化");
		}
		else
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctk);
			cts.CancelAfter(cfg.LaserTimeoutMs + 2000);
			try
			{
				result = await marker.MarkAsync(content, cts.Token);
			}
			catch (OperationCanceledException)
			{
				result = MarkerResult.Fail(MarkResultCode.Timeout, "打标作业超时");
			}
		}

		var responseTime = DateTime.Now;
		sw.Stop();

		// 4) 登记码索引（仅 OK 才登记，保证 NG 的码不会被后续误判为"已打码"）
		if (result.Ok)
		{
			var registered = ThaCodeStore.TryRegisterMarked(content, content, requestTime);
			if (!registered)
			{
				result = MarkerResult.Fail(MarkResultCode.GenerateFailed, "码已被占用（登记冲突）");
			}
			else
			{
				// Mock 扫码枪默认读回这码（模拟同一件产品流转到读码站）
				MockDeviceBus.LastMarkedCode = content;
			}
		}

		// 5) 写打码站上下文（上传站消费）。只放上传落库要用的码 + 主页卡片要显示的时间；
		//    读码站有自己独立的 ScanCtx，两站并行时互不覆盖。
		var doneCode = Tha26003.MarkDoneCode(result.Code);
		Volatile.Write(ref _markCtx, new MarkCtx(result.Ok ? content : null, requestTime, content));

		// 新件开始 → 清掉上一件遗留的读码上下文。
		// 现场时序是「打码 → 采集 → 读码」：上一件的扫码结果如果不在此清，会被本件的上传
		// 当成“本件扫码结果”消费（日志里就是这个现象：B 件落库拿到的却是 A 件扫到的错码）。
		if (result.Ok)
			Volatile.Write(ref _scanCtx, EmptyScanCtx);

		ThaDb.LogMark(new ThaMarkLog
		{
			Content = content,
			EchoContent = result.EchoContent,
			RawReply = Truncate(result.RawReply, 512),
			Message = Truncate(result.Message, 256),
			Result = (int)result.Code,
			DurationMs = (int)sw.ElapsedMilliseconds,
			LaserMode = marker?.Mode,
			RequestTime = requestTime,
			ResponseTime = responseTime,
		});

		// %MW10502 是「打码完成」信号：成功写 1；失败**不写**（PLC 侧保持 0），避免 PLC 把失败当成"已完成"
		if (doneCode != null)
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwMarkResult), new short[] { (short)doneCode.Value }));

		LogMgr.Instance.Info($"[THA-MARK] 内容={content} 结果={Tha26003.MarkReasonText(result.Code)}"
			+ $"，10502={(doneCode?.ToString() ?? "不写（保持0）")} 耗时={sw.ElapsedMilliseconds}ms {result.Message}");

		// 打码未成功（回执没带 OK / 无回执 / 连不上 / 回执不符）→ 弹窗提示操作员（PLC 侧不写完成位）
		if (doneCode == null)
		{
			OnDeviceAlarm?.Invoke(new ThaDeviceAlarm(
				$"打码失败（大族打标机）：{Tha26003.MarkReasonText(result.Code)}",
				$"打标内容：{content}\n"
				+ $"打码完成位：未写（%MW10502 保持 0）\n"
				+ $"原因：{result.Message ?? "未返回原因"}\n"
				+ $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
		}
	}

	// ----- 读码站 -----

	/// <summary>
	/// 读码站：先分清"扫码枪扫上了没"，扫上了再与 PLC 附带前站镭码 <c>%MW10580</c> 比对，
	/// 一致才查库重码。三类 NG（未扫到 / 错码 / 重码）都写 <c>%MW10522=2</c> 并弹窗，
	/// 产品记录的「扫码结果」记 1=OK / 2=NG，细分原因进 <see cref="ThaScanLog"/>。
	/// </summary>
	private async Task RunScanAsync(StationJob job, CancellationToken ctk)
	{
		var cfg = AppConfig!;
		var sw = Stopwatch.StartNew();
		var scanner = _scanner;
		var plcCode = job.PlcCode;          // 触发时刻由 IO 循环读回；未启用比对/未接线 = null
		var gunRaw = (string?)null;         // 枪原始返回（含 error 包），落流水与弹窗用
		var gunCode = (string?)null;        // 归一化后的枪码（纯码，不含码等级）
		var gunLevel = (string?)null;       // 从枪内容里拆出的码等级（000143348000026070:A → A）

		// 扫不上就重试（默认共 3 次）：每次都是「触发→等码」；读到码就停。
		// 枪回 error 包（ERROR 等关键词）在设备层已归一为"没扫上"，同样走重试。
		var maxAttempts = Math.Clamp(cfg.ScannerMaxAttempts, 1, 10);
		var retryDelay = Math.Max(0, cfg.ScannerRetryDelayMs);
		var attempts = 0;
		ScanRead read;
		if (scanner == null)
		{
			read = new ScanRead(false, null, null, "扫码枪适配器未初始化", true);
		}
		else
		{
			read = ScanRead.NoRead("未执行");
			for (var attempt = 1; attempt <= maxAttempts; attempt++)
			{
				attempts = attempt;
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctk);
				cts.CancelAfter(cfg.ScannerTimeoutMs + 1000);
				try
				{
					read = await scanner.ReadAsync(cts.Token);
				}
				catch (OperationCanceledException)
				{
					read = new ScanRead(false, null, null, "读码作业超时", true);
				}

				// 枪回 error 包 = 没扫上（设备层已判一次，这里再兜一次：Mock 与真机口径一致）
				if (read.Ok && ThaScanJudge.IsNoReadReply(read.Raw ?? read.Code, cfg.ScannerNoReadKeywords))
					read = ScanRead.NoRead($"扫码枪回包 {(read.Raw ?? "").Trim()}（没扫上）");

				gunRaw = read.Raw ?? read.Message ?? gunRaw;
				if (read.Ok) break;
				if (attempt >= maxAttempts) break;

				LogMgr.Instance.Warn($"[THA-SCAN] 第 {attempt}/{maxAttempts} 次未扫到码（{read.Message ?? "未扫到"}），"
					+ $"{retryDelay}ms 后重试");
				if (retryDelay > 0)
				{
					try { await Task.Delay(retryDelay, ctk); }
					catch (OperationCanceledException) { break; }
				}
			}
		}

		sw.Stop();
		var reason = "";
		ScanResultCode result;

		if (!read.Ok)
		{
			// ① 没扫上（error 包 / 超时 / 枪异常）
			result = read.CommError ? ScanResultCode.CommError : ScanResultCode.NoRead;
			reason = ThaScanJudge.NoReadDetail(attempts, gunRaw);
			if (read.CommError && !string.IsNullOrWhiteSpace(read.Message))
				reason = read.Message!;
		}
		else
		{
			// 扫上 → 先拆「码 + 码等级」：现场枪回包是合写，形如 000143348000026070:A
			// （正则 ScannerCodePattern；无分隔符时整串为码、等级为空，与改造前一致）
			var parts = ThaScanCode.Parse(ThaCodeValidator.Normalize(read.Code), cfg.ScannerCodePattern);
			gunCode = parts.Code;
			gunLevel = parts.Grade;

			// ② 扫上了 → 与 PLC 附带前站镭码比对（关掉 PlcCodeCompareEnabled 时跳过）
			// PLC 侧同样可能是「码+等级」合写，按同一正则拆完再比纯码
			var plcParts = ThaScanCode.Parse(plcCode, cfg.ScannerCodePattern);
			var v = ThaCodeValidator.Validate(gunCode, cfg);   // ③ 格式校验（长度 / 字符集 / 时间窗）
			if (cfg.PlcCodeCompareEnabled && !ThaScanJudge.CodesMatch(gunCode, plcParts.Code))
			{
				result = ScanResultCode.BadFormat;
				reason = ThaScanJudge.MismatchDetail(gunCode, plcParts.Code);
			}
			else if (!v.Ok)
			{
				result = ScanResultCode.BadFormat;
				reason = v.Reason ?? "格式不符";
			}
			else if (!ThaScanJudge.GradeOk(gunLevel, cfg.CodeGradeThreshold, out var gradeReason))
			{
				// ④ 码等级判定：阈值 CodeGradeThreshold（默认 B）→ A/B 合格，C/D 及更低判 NG
				result = ScanResultCode.BadFormat;
				reason = gradeReason!;
			}
			else
			{
				// ⑤ 查库：重码（已上传/NG冻结/在制超时）与未登记码
				var decision = ThaCodeStore.EvaluateForScan(gunCode, cfg.WipTimeoutHours, cfg.AllowUnregisteredCode);
				result = decision.Result;
				reason = decision.Reason ?? "";

				// ⑥ 补读回填：现场时序是「打码 → 采集落库 → 读码」，本件码先落库、后读码。
				// 命中「已上传」时先试回填（限 LateScanFillSeconds 窗口 + 记录里还没有本码扫码结果），
				// 回填成功 → 本次读码按补读放行（PLC 写 1），并把扫码结果/码等级写回那条记录；
				// 超窗 / 已有本码扫码结果 → 仍是真重码，保持 NG。
				if (result == ScanResultCode.Duplicate)
				{
					var fill = ThaCodeStore.TryFillLateScan(
						gunCode, 1 /* 能走到这里说明本件码读对了，记录里的扫码结果就是 OK(1) */, Truncate(read.Raw ?? gunRaw, 128), gunLevel,
						cfg.ScannerCodePattern, cfg.LateScanFillSeconds);
					if (fill.Filled)
					{
						result = ScanResultCode.Ok;
						reason = $"补读回填(落库后才读到本件码，等级={gunLevel ?? "--"})";

						// 记录的总结果可能从 NG 翻成 OK（或反之）→ 内存计数跟着调，否则主页计数与库不一致
						if (fill.OldOverall == 2 && fill.NewOverall != 2)
						{
							TotalNg = Math.Max(0, TotalNg - 1);
							if (fill.NewOverall == 1) TotalOk++;
						}
						else if (fill.OldOverall != 2 && fill.NewOverall == 2)
						{
							TotalOk = Math.Max(0, TotalOk - 1);
							TotalNg++;
						}

						if (fill.Record != null)
							OnProductRecordUpdated?.Invoke(fill.Record);

						LogMgr.Instance.Info($"[THA-SCAN] 补读回填：码={gunCode} 等级={gunLevel ?? "--"}"
							+ $" 总结果 {Tha26003.ResultText(fill.OldOverall)}→{Tha26003.ResultText(fill.NewOverall)}");
					}
					else if (!string.IsNullOrEmpty(fill.Reason))
					{
						reason += $"（补读回填未生效：{fill.Reason}）";
					}
				}

				if (result == ScanResultCode.Ok && decision.Result == ScanResultCode.Ok)
					ThaCodeStore.MarkScanned(gunCode, DateTime.Now, decision.RetestCount);

				// 复检次数只驱动重码判定，不进产品记录（协议该栏「暂无」）
			}
		}

		// 对外口径：PLC 只认 1=OK / 2=NG（未扫到、错码、重码、枪异常全部 2）
		var plcCodeResult = Tha26003.ScanPlcCode(result);

		// 读码站上下文（上传站消费 + 主页卡片显示）：结果、时间、归一化码、码等级、枪原文
		Volatile.Write(ref _scanCtx, new ScanCtx(
			plcCodeResult,
			DateTime.Now,
			result == ScanResultCode.Ok ? gunCode : null,
			Truncate(read.Raw ?? gunRaw, 128),
			// 等级**不论结果都带下去**：C/D 本身就会被判 NG，若只在 Ok 时才带，
			// 就会出现「因等级判 NG、落库却看不到是哪个等级」——现场排查最需要的就是这个值
			Truncate(gunLevel, 8)));

		ThaDb.LogScan(new ThaScanLog
		{
			CodeRaw = Truncate(read.Raw ?? gunRaw, 128),
			CodeNormalized = Truncate(gunCode, 64),
			CodeLevel = Truncate(gunLevel, 8),
			PlcCode = Truncate(plcCode, 64),
			Result = (int)result,          // 流水表存内部细分值（诊断用），占 PLC 与产品记录用 1/2
			FailReason = Truncate(reason, 256),
			DurationMs = (int)sw.ElapsedMilliseconds,
			ScannerMode = scanner?.Mode,
			CreateTime = DateTime.Now,
		});

		// 读到的码回写 PLC（供 PLC 显示/绑定；PLC 不需要时可忽略该区）—— 回写纯码，不带等级
		if (read.Ok && !string.IsNullOrEmpty(gunCode))
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwScanEcho), Tha26003.EncodeAsciiWords(gunCode!, Tha26003.ScanEchoWords)));

		_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwScanResult), new short[] { (short)plcCodeResult }));
		// 日志压成一行（弹窗才用多行，避免日志断行不好检索）
		var logReason = (reason ?? "").Replace("\r", " ").Replace("\n", " / ").Trim();
		LogMgr.Instance.Info($"[THA-SCAN] 码={gunCode ?? "(无)"} 等级={gunLevel ?? "--"} PLC码={plcCode ?? "(无)"} 结果={Tha26003.ScanReasonText(result)}"
			+ $"（写 PLC={plcCodeResult}） 耗时={sw.ElapsedMilliseconds}ms {logReason}");

		// 读码 NG → 弹窗提示（带细分原因，PLC 侧只看到 2）
		if (plcCodeResult != 1)
		{
			OnDeviceAlarm?.Invoke(new ThaDeviceAlarm(
				$"读码失败（基恩士扫码枪）：{Tha26003.ScanReasonText(result)}",
				$"结果码：2（NG，已写 %MW10522）\n"
				+ $"{reason}\n"
				+ $"尝试次数：{attempts}  耗时：{sw.ElapsedMilliseconds}ms\n"
				+ $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
		}
	}

	/// <summary>
	/// 联调口：直接触发一次扫码枪读码，**只返回枪的原始返回内容**——
	/// 不做长度 / 时间窗 / 重码 / PLC 码比对，不写 PLC，不落库，不发告警。
	/// 供主页读码站卡片「循环 1s 触发扫码」使用；与读码站作业共用设备锁，同一把枪不会并发读。
	/// </summary>
	public async Task<ScanRead> RawScanOnceAsync(CancellationToken ct)
	{
		var scanner = _scanner;
		if (scanner == null)
			return new ScanRead(false, null, null, "扫码枪适配器未初始化", true);

		var timeout = Math.Max(1000, AppConfig?.ScannerTimeoutMs ?? 3000);
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeout + 1000);
		try
		{
			return await scanner.ReadAsync(cts.Token);
		}
		catch (OperationCanceledException)
		{
			return new ScanRead(false, null, null, "读码超时（枪无返回）", true);
		}
		catch (Exception ex)
		{
			return new ScanRead(false, null, null, $"读码异常：{ex.Message}", true);
		}
	}

	// ----- 上传站 -----

	private void RunUpload(byte[]?[] segments)
	{
		// 本件结束即消费：打码/读码上下文**读并清空**（Interlocked.Exchange 原子换新）——
		// 无论成功、数据 NG、重复触发还是异常都要清，否则"打了码但没上传"的残留码
		// 会被下一件（无码）的上传错误认领。
		var mark = Interlocked.Exchange(ref _markCtx, EmptyMarkCtx);
		var scan = Interlocked.Exchange(ref _scanCtx, EmptyScanCtx);
		RunUploadCore(segments, mark, scan);
	}

	private void RunUploadCore(byte[]?[] segments, MarkCtx mark, ScanCtx scan)
	{
		var cfg = AppConfig!;
		var pinFormat = ParsePinHeightFormat(cfg.PinHeightFormat);
		var posFormat = ParsePositionFormat(cfg.PositionFormat);

		var measurement = Tha26003.ParseMeasurement(
			segments[0], segments[1], segments[2], segments[3], segments[4], pinFormat, posFormat);

		// 供监控页展示（引用赋值原子，IO 循环读取安全）
		_lastMeasurement = measurement;

		var markContent = mark.Content;        // 镭码内容（打标原文）：写入「镭码内容」列，并用于码索引 / 防重复
		var markCode = mark.Code;              // 打码成功时的码（码索引状态机用）
		var scanCode = scan.ScannedCode;      // 读码站读到的纯码：**产品码列的唯一来源**

		// 重复上传保护①：同码已“已上传”说明 PLC 重复触发，不能重复插入记录
		if (!string.IsNullOrWhiteSpace(markCode) &&
			ThaCodeStore.GetStatus(markCode) == CodeStatus.Uploaded)
		{
			LogMgr.Instance.Warn($"[THA-UPLOAD] 重复上传触发（码 {markCode} 已上传），不重复落库");
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.Failed }));
			RaiseAlarm("采集失败（上传站）",
				$"原因：重复上传触发（同码已落库，本轮未新增记录）\n"
				+ $"产品码：{markCode}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			return;
		}

		// 重复上传保护②：打码上下文已被上一轮消费（mark.Code 为空）时，①② 靠不住了，
		// 改用「最近 LateScanFillSeconds 内已有相同镭码内容」识别同一件的重复触发
		if (ThaDb.HasRecentMarkContent(markContent, Math.Max(5, cfg.LateScanFillSeconds)))
		{
			LogMgr.Instance.Warn($"[THA-UPLOAD] 重复上传触发（镭码内容 {markContent} 刚落库过），不重复落库");
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.Failed }));
			RaiseAlarm("采集失败（上传站）",
				$"原因：重复上传触发（相同镭码内容刚已落库，本轮未新增记录）\n"
				+ $"镭码内容：{markContent}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			return;
		}

		// 二次防御：解析层（ParseMeasurement/Sanitize）已把 NaN / 无穷 / 哨兵量级转成 NULL，
		// 正常永远不会走到这里。保留它只为防以后放宽 Sanitize 时脏值静默入库 ——
		// 真触发了就按「落库失败」处理（2 + 弹窗），而不是另立一个「数据NG」结果码。
		if (!Tha26003.IsMeasurementSane(measurement))
		{
			LogMgr.Instance.Error("[THA-UPLOAD] 测量数据异常（NaN/未初始化哨兵），不落库");
			ThaDb.LogScan(new ThaScanLog
			{
				CodeNormalized = Truncate(scanCode ?? markCode, 64),
				Result = (int)UploadResultCode.Failed,
				FailReason = "上传测量数据异常",
				CreateTime = DateTime.Now,
			});
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.Failed }));
			RaiseAlarm("采集失败（上传站）",
				$"原因：测量数据异常（NaN / 未初始化哨兵），本件未落库\n"
				+ $"镭码内容：{markContent ?? "(无)"}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			return;
		}

		var record = new ThaProductRecord
		{
			// 产品码 = **读码站扫到的码**（未扫上 / 扫码 NG → null，不拿打码内容凑数）
			ProductCode = string.IsNullOrWhiteSpace(scanCode) ? null : scanCode,
			// 镭码内容 = 本件下发给打标机的打标原文
			MarkContent = Truncate(markContent, 64),
			ScanResult = scan.ScanResult,
			ScanCodeRaw = scan.ScanRaw,
			// 码等级：随扫码成功一起带下来（未扫码 / 内容里没写等级 → null）
			ScanCodeLevel = Truncate(scan.ScanLevel, 8),
			// 是否复检：协议地址表该栏为「暂无」（PLC 不提供）→ 恒为 null。
			// 内部的重入次数只驱动重码判定，不写进记录（PLC 定义该地址后再接）。
			IsRetest = null,
		};
		Tha26003.Apply(record, measurement);

		// 总结果由 MES 自算：四项（耐压/针高/位置度/扫码）无 NG → 1；有 NG → 2；缺项 → null
		record.OverallResult = Tha26003.ComputeOverall(
			record.WithstandResult, record.PinHeightResult, record.PositionResult, record.ScanResult);

		// 上传结果码只看"落库成不成功"：码为空也照样落库 → 算成功，不报失败
		var codeMissing = string.IsNullOrWhiteSpace(scanCode);
		record.UploadResult = (int)UploadResultCode.Ok;

		// 码索引状态机仍按**打码码**推进（上传 = 该码生命周期结束），与产品码列来源无关
		var stored = ThaDb.InsertProductRecord(record, markCode);

		if (stored)
		{
			if (record.OverallResult == 1) TotalOk++;
			else if (record.OverallResult == 2) TotalNg++;
			// 当日报表追加（非阻塞；报表线程按阈值/间隔落盘，失败不影响产线）
			ThaDailyReport.Append(record);
			var handler = OnProductRecord;
			handler?.Invoke(record);
		}

		_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[]
		{
			(short)(stored ? (int)UploadResultCode.Ok : (int)UploadResultCode.Failed),
		}));

		LogMgr.Instance.Info($"[THA-UPLOAD] 产品码={(codeMissing ? "(未扫上→空)" : scanCode)}"
			+ $" 镭码内容={record.MarkContent ?? "(无)"}"
			+ $" 码等级={record.ScanCodeLevel ?? "--"}"
			+ $" 总结果={Tha26003.ResultText(record.OverallResult)} 落库={(stored ? "OK" : "失败")}"
			// 上传来时本件读码上下文还是空的（PLC 采集触发早于读码触发）→ 现场一眼看出“扫码/等级/产品码为何是 --”
			+ (scan.ScanTime == null ? "（本件读码结果尚未到达：采集触发早于读码）" : ""));

		// 落库失败 → 弹窗提示（数据丢了必须让人知道，不能只写日志）
		if (!stored)
		{
			RaiseAlarm("采集失败（上传站）",
				$"原因：数据库写入失败，本件数据未落库\n"
				+ $"镭码内容：{markContent ?? "(无)"}\n"
				+ $"产品码（扫码）：{scanCode ?? "(未扫上)"}\n"
				+ $"总结果：{Tha26003.ResultText(record.OverallResult)}\n"
				+ $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		}
	}

	/// <summary>触发外设/采集告警（作业线程 → 订阅方自己切 UI 线程）。</summary>
	private void RaiseAlarm(string title, string message)
	{
		try
		{
			OnDeviceAlarm?.Invoke(new ThaDeviceAlarm(title, message));
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Warn($"[THA-ALARM] 告警分发失败：{ex.Message}");
		}
	}

	// ============================================================
	// 辅助
	// ============================================================

	/// <summary>
	/// 针高口径：DINT ÷1000，字序默认 CDAB（低字在前，汇川/三菱）。
	/// 兼容旧配置字符串：Int32Div1000（旧默认，高字在前）= Abcd；Int32SwappedDiv1000 = Cdab。
	/// </summary>
	private static PinHeightFormat ParsePinHeightFormat(string s)
	{
		if (Enum.TryParse<PinHeightFormat>(s, true, out var v)) return v;
		return (s ?? "").Trim().ToLowerInvariant() switch
		{
			"int32div1000" => PinHeightFormat.Int32Abcd,          // 旧枚举名（高字在前）
			"int32swappeddiv1000" => PinHeightFormat.Int32Cdab,   // 旧枚举名（低字在前）
			_ => PinHeightFormat.Int32Cdab,
		};
	}

	/// <summary>位置度口径：REAL（默认字序 CDAB）。</summary>
	private static PositionFormat ParsePositionFormat(string s)
		=> Enum.TryParse<PositionFormat>(s, true, out var v) ? v : PositionFormat.FloatCdab;

	/// <summary>汇川 PLC 系列（InovanceTcpNet.Series）：AM / H3U / H5U / Easy / EVO。</summary>
	private static InovanceSeries ParseInovanceSeries(string s)
		=> Enum.TryParse<InovanceSeries>(s, true, out var v) ? v : InovanceSeries.AM;

	private static string? Truncate(string? s, int max)
		=> string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);

	private void MarkDisconnected(string message)
	{
		if (_connected)
		{
			_connected = false;
			LogMgr.Instance.Warn($"[THA-PLC] 连接断开：{message}，3秒后自动重连");
			UpdateSnapshot(0, 0, 0, 0, 0, 0, -1, -1, -1);
		}
	}

	/// <summary>失败日志限流：30 秒最多一条，避免断线刷屏。</summary>
	private void LogFailOnce(string message)
	{
		var now = Environment.TickCount64;
		var last = Interlocked.Read(ref _lastFailLogTicks);
		if (now - last > 30_000)
		{
			Interlocked.Exchange(ref _lastFailLogTicks, now);
			LogMgr.Instance.Warn(message);
		}
	}

	private void UpdateSnapshot(
		int markTrig, int markRes, int scanTrig, int scanRes, int unloadTrig, int unloadRes,
		int withstand, int pinHeight, int position)
	{
		var prev = Snapshot;
		var m = _lastMeasurement;
		// 两站上下文各自 Volatile 读：拿到的要么旧要么新，都是完整的不可变对象
		var markCtx = Volatile.Read(ref _markCtx);
		var scanCtx = Volatile.Read(ref _scanCtx);
		Snapshot = new ThaStationSnapshot(
			_connected,
			DateTime.Now,
			_heartbeat,
			TotalOk,
			TotalNg,
			new StationState(markTrig, markRes, Tha26003.MarkResultText(markRes), markCtx.MarkTime, markCtx.Code ?? ""),
			new StationState(scanTrig, scanRes, Tha26003.ScanResultText(scanRes), scanCtx.ScanTime, scanCtx.ScannedCode ?? scanCtx.ScanRaw ?? ""),
			new StationState(unloadTrig, unloadRes, Tha26003.UploadResultText(unloadRes), prev.Unload.Time, prev.Unload.Detail),
			new MeasurementSnapshot(
				withstand >= 0 ? withstand : prev.Live?.WithstandResult,
				pinHeight >= 0 ? pinHeight : prev.Live?.PinHeightResult,
				position >= 0 ? position : prev.Live?.PositionResult,
				m?.TopPinHeights, m?.BottomPinHeights,
				m?.TopPosX, m?.TopPosY, m?.BottomPosX, m?.BottomPosY));
	}

	/// <summary>最近一次上传解析出的完整测量数据（供监控页展示）。</summary>
	private Tha26003.Measurement? _lastMeasurement;

	/// <summary>%MW 编程地址 → Modbus 寄存器号 的现行映射（随配置重建）。</summary>
	private ThaAddressMap _map = ThaAddressMap.Default;

	/// <summary>上一次打印的地址映射描述（仅在变化时打日志）。</summary>
	private string _mapDesc = "";

	/// <summary>上一次打印的打标码结构描述（仅在变化时打日志）。</summary>
	private string _codeDesc = "";

	/// <summary>上一次打印的码等级阈值描述（仅在变化时打日志）。</summary>
	private string _gradeDesc = "";

	/// <summary>按当前映射把 %MW 编程地址换算为 Modbus 寄存器地址字符串。</summary>
	private string R(int mw) => _map.R(mw);

	private sealed record PendingWrite(string Reg, short[] Values);

	private enum StationKind { Mark, Scan, Upload }

	/// <param name="Kind">站别。</param>
	/// <param name="Segments">上传站数据区分段（其它站为 null）。</param>
	/// <param name="PlcCode">读码站：触发时刻从 <c>%MW10580</c> 读回的前站镭码（未读取/未接线为 null）。</param>
	/// <param name="EnqueuedTicks">入队时刻（<see cref="Environment.TickCount64"/>），用于量"排队等了多久"。</param>
	private sealed record StationJob(StationKind Kind, byte[]?[]? Segments, string? PlcCode = null, long EnqueuedTicks = 0);

}

/// <summary>外设作业告警（供主页弹窗）。</summary>
/// <param name="Title">弹窗标题。</param>
/// <param name="Message">弹窗正文。</param>
public sealed record ThaDeviceAlarm(string Title, string Message);

/// <summary>单站状态（供 UI 只读展示）。</summary>
/// <param name="Trigger">触发位当前值。</param>
/// <param name="Result">结果码当前值。</param>
/// <param name="Text">结果码文本。</param>
/// <param name="Time">该站最近一次完成时间。</param>
/// <param name="Detail">明细（码 / 原因）。</param>
public sealed record StationState(int Trigger, int Result, string Text, DateTime? Time, string Detail);

/// <summary>测量结果快照：结果字 + 最近一次上传的完整测量数组（未上传时为 null）。</summary>
public sealed record MeasurementSnapshot(
	int? WithstandResult,
	int? PinHeightResult,
	int? PositionResult,
	double?[]? TopPinHeights,
	double?[]? BottomPinHeights,
	double?[]? TopPosX,
	double?[]? TopPosY,
	double?[]? BottomPosX,
	double?[]? BottomPosY);

/// <summary>PLC 整体快照（不可变，UI 轮询只读）。</summary>
public sealed record ThaStationSnapshot(
	bool IsConnected,
	DateTime UpdateTime,
	int Heartbeat,
	int TotalOk,
	int TotalNg,
	StationState Mark,
	StationState Scan,
	StationState Unload,
	MeasurementSnapshot? Live)
{
	public static readonly ThaStationSnapshot Empty = new(
		false, DateTime.MinValue, 0, 0, 0,
		new StationState(0, 0, "--", null, ""),
		new StationState(0, 0, "--", null, ""),
		new StationState(0, 0, "--", null, ""),
		null);
}
