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

	private ILaserMarker? _laser;
	private IScanner? _scanner;
	private string _laserMode = "";
	private string _scannerMode = "";

	/// <summary>作业队列（打码/读码/上传）。上限 8：产线是顺序流转，堆积说明逻辑有问题。</summary>
	private readonly Channel<StationJob> _jobs = Channel.CreateBounded<StationJob>(
		new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });

	/// <summary>待回写的 Modbus 写操作（作业线程产出 → IO 循环执行）。</summary>
	private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();

	/// <summary>当前在制产品的上下文（打码站建立 → 读码站补充 → 上传站消费后重置）。</summary>
	private ProductContext _ctx = new();

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
		var worker = Task.Run(() => WorkerLoopAsync(ctk), ctk);
		try
		{
			await IoLoopAsync(ctk);
		}
		finally
		{
			_jobs.Writer.TryComplete();
			try { await worker; } catch { /* 退出时忽略 */ }
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

	// 4a) 打码触发
	if (markTrig == 1)
	{
		await WriteNowAsync(R(Tha26003.MwMarkTrigger), 0);
		string? plcContent = null;
		if (string.Equals(cfg.MarkContentSource, "Plc", StringComparison.OrdinalIgnoreCase))
			plcContent = await ReadPlcContentAsync();
		Enqueue(new StationJob(StationKind.Mark, plcContent, null));
		LogMgr.Instance.Info($"[THA-PLC] 打码触发（内容来源={(plcContent is { Length: > 0 } ? "PLC" : "MES 自生成")}）");
	}

	// 4b) 读码触发
	if (scanTrig == 1)
	{
		await WriteNowAsync(R(Tha26003.MwScanTrigger), 0);
		Enqueue(new StationJob(StationKind.Scan, null, null));
		LogMgr.Instance.Info("[THA-PLC] 读码触发");
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
		}
		else
		{
			Enqueue(new StationJob(StationKind.Upload, null, segments));
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

	/// <summary>读 PLC 附带内容区（%MW97540 起 20 WORD = 40 字符）。空串表示未使用。</summary>
	private async Task<string?> ReadPlcContentAsync()
	{
		try
		{
			if (_client == null) return null;
			var r = await _client.ReadAsync(R(Tha26003.MwPlcContent), Tha26003.PlcContentWords);
			if (!r.IsSuccess || r.Content == null) return null;
			var s = Tha26003.ParseAscii(r.Content, 0, Tha26003.PlcContentWords * 2, false);
			return string.IsNullOrWhiteSpace(s) ? null : s;
		}
		catch (Exception ex)
		{
			LogFailOnce($"[THA-PLC] 读 PLC 附带内容失败：{ex.Message}");
			return null;
		}
	}

	/// <summary>上传：分段读全量测量数据（FC03 ≤125 WORD，数据区跨度 479 WORD 必须拆分）。</summary>
	/// <summary>
	/// 上传：分段读全量测量数据。**允许个别段失败** —— 失败的段返回 null，
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

	private void Enqueue(StationJob job)
	{
		if (!_jobs.Writer.TryWrite(job))
			LogMgr.Instance.Error($"[THA-PLC] 作业队列已满，丢弃作业 {job.Kind}");
	}

	// ============================================================
	// 作业消费线程
	// ============================================================

	private async Task WorkerLoopAsync(CancellationToken ctk)
	{
		while (!ctk.IsCancellationRequested)
		{
			StationJob job;
			try
			{
				job = await _jobs.Reader.ReadAsync(ctk);
			}
			catch (Exception)
			{
				break; // 队列关闭或取消
			}

			try
			{
				switch (job.Kind)
				{
					case StationKind.Mark:
						await RunMarkAsync(job, ctk);
						break;
					case StationKind.Scan:
						await RunScanAsync(ctk);
						break;
					case StationKind.Upload:
						RunUpload(job.Segments!);
						break;
				}
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"[THA-JOB] {job.Kind} 作业异常：{ex.Message}", ex);
				_pendingWrites.Enqueue(job.Kind switch
				{
					StationKind.Mark => new PendingWrite(R(Tha26003.MwMarkResult), new short[] { (short)MarkResultCode.InternalError }),
					StationKind.Scan => new PendingWrite(R(Tha26003.MwScanResult), new short[] { (short)ScanResultCode.InternalError }),
					_ => new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.Failed }),
				});
			}
		}
	}

	// ----- 打码站 -----

	private async Task RunMarkAsync(StationJob job, CancellationToken ctk)
	{
		var cfg = AppConfig!;
		var sw = Stopwatch.StartNew();
		var requestTime = DateTime.Now;

		// 1) 取内容：PLC 指定优先，否则纯时间码自生成
		var content = job.PlcContent;
		if (string.IsNullOrWhiteSpace(content))
			content = ThaCodeGenerator.Generate(cfg.CodeDateFormat, cfg.CodeMaxWaitMs);

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
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwMarkResult), new short[] { (short)MarkResultCode.GenerateFailed }));
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

		// 5) 建立本件在制上下文（供读码站/上传站串联）
		_ctx = new ProductContext
		{
			Code = result.Ok ? content : null,
			MarkResult = (int)result.Code,
			MarkTime = requestTime,
			ReadResult = (int)ScanResultCode.NotDone,
		};

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

		_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwMarkResult), new short[] { (short)result.Code }));
		LogMgr.Instance.Info($"[THA-MARK] 内容={content} 结果={Tha26003.MarkResultText((int)result.Code)} 耗时={sw.ElapsedMilliseconds}ms {result.Message}");
	}

	// ----- 读码站 -----

	private async Task RunScanAsync(CancellationToken ctk)
	{
		var cfg = AppConfig!;
		var sw = Stopwatch.StartNew();
		var scanner = _scanner;

		ScanRead read;
		if (scanner == null)
		{
			read = new ScanRead(false, null, null, "扫码枪适配器未初始化", true);
		}
		else
		{
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
		}

		sw.Stop();
		var reason = "";
		ScanResultCode result;

		if (!read.Ok)
		{
			result = read.CommError ? ScanResultCode.CommError : ScanResultCode.NoRead;
			reason = read.Message ?? "未扫到码";
		}
		else
		{
			var normalized = ThaCodeValidator.Normalize(read.Code);
			var v = ThaCodeValidator.Validate(normalized, cfg.CodeDateFormat, cfg.CodeLength, cfg.CodeTimeWindowMinutes);
			if (!v.Ok)
			{
				result = ScanResultCode.BadFormat;
				reason = v.Reason ?? "格式不符";
			}
			else
			{
				var decision = ThaCodeStore.EvaluateForScan(normalized, cfg.WipTimeoutHours, cfg.AllowUnregisteredCode);
				result = decision.Result;
				reason = decision.Reason ?? "";
				if (result == ScanResultCode.Ok)
					ThaCodeStore.MarkScanned(normalized, DateTime.Now, decision.RetestCount);

				// 更新在制上下文（读码站读到的码覆盖打码内容，便于发现"打错内容/串料"）
				_ctx.ReadResult = (int)result;
				_ctx.ScanTime = DateTime.Now;
				_ctx.ScanRaw = Truncate(read.Raw, 128);
				_ctx.RetestCount = decision.RetestCount;
				if (result == ScanResultCode.Ok)
					_ctx.ScannedCode = normalized;
			}
		}

		ThaDb.LogScan(new ThaScanLog
		{
			CodeRaw = Truncate(read.Raw, 128),
			CodeNormalized = Truncate(read.Code, 64),
			Result = (int)result,
			FailReason = Truncate(reason, 256),
			DurationMs = (int)sw.ElapsedMilliseconds,
			ScannerMode = scanner?.Mode,
			CreateTime = DateTime.Now,
		});

		// 读到的码回写 PLC（供 PLC 显示/绑定；PLC 不需要时可忽略该区）
		if (read.Ok && !string.IsNullOrEmpty(read.Code))
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwScanEcho), Tha26003.EncodeAsciiWords(read.Code!, Tha26003.ScanEchoWords)));

		_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwScanResult), new short[] { (short)result }));
		LogMgr.Instance.Info($"[THA-SCAN] 码={read.Code ?? "(无)"} 结果={Tha26003.ScanResultText((int)result)} 耗时={sw.ElapsedMilliseconds}ms {reason}");
	}

	// ----- 上传站 -----

	private void RunUpload(byte[]?[] segments)
	{
		var cfg = AppConfig!;
		var pinFormat = ParsePinHeightFormat(cfg.PinHeightFormat);
		var posFormat = ParsePositionFormat(cfg.PositionFormat);

		var measurement = Tha26003.ParseMeasurement(
			segments[0], segments[1], segments[2], segments[3], segments[4], pinFormat, posFormat);

		// 供监控页展示（引用赋值原子，IO 循环读取安全）
		_lastMeasurement = measurement;

		var code = _ctx.Code;
		var codeForRecord = code;

		// 重复上传保护：同码已"已上传"说明 PLC 重复触发，不能重复插入记录
		if (!string.IsNullOrWhiteSpace(codeForRecord) &&
			ThaCodeStore.GetStatus(codeForRecord) == CodeStatus.Uploaded)
		{
			LogMgr.Instance.Warn($"[THA-UNLOAD] 重复上传触发（码 {codeForRecord} 已上传），不重复落库");
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.DataInvalid }));
			return;
		}

		// 数据合理性：NaN / 1e38 之类的**脏值**判数据 NG 且不落库；
		// null（该段未读到）不算脏，按"未采集"落 NULL。
		if (!Tha26003.IsMeasurementSane(measurement))
		{
			LogMgr.Instance.Error("[THA-UNLOAD] 测量数据异常（NaN/未初始化哨兵），不落库");
			ThaDb.LogScan(new ThaScanLog
			{
				CodeNormalized = codeForRecord,
				Result = (int)UploadResultCode.DataInvalid,
				FailReason = "上传测量数据异常",
				CreateTime = DateTime.Now,
			});
			_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[] { (short)UploadResultCode.DataInvalid }));
			return;
		}

		var record = new ThaProductRecord
		{
			ProductCode = string.IsNullOrWhiteSpace(codeForRecord) ? null : codeForRecord,
			MarkResult = _ctx.MarkResult,
			ReadResult = _ctx.ReadResult,
			MarkTime = _ctx.MarkTime,
			ScanTime = _ctx.ScanTime,
			ScanCodeRaw = _ctx.ScanRaw,
			// 是否复检：协议地址表该栏为「暂无」（PLC 不提供）→ 恒为 null。
			// 内部的重入次数只驱动重码判定，不写进记录（PLC 定义该地址后再接）。
			IsRetest = null,
		};
		Tha26003.Apply(record, measurement);

		// 总结果由 MES 自算：其余结果里没有 NG(2) → OK(1)
		record.OverallResult = Tha26003.ComputeOverall(
			record.MarkResult, record.ReadResult,
			record.WithstandResult, record.PinHeightResult, record.PositionResult);

		// 码为空 = 数据不完整：仍然落库（不丢数据），但结果码判数据 NG 让 PLC 知情
		// 码为空 = 数据不完整 → 结果码判 2（数据 NG）但**仍然落库**（不丢数据）
		var codeMissing = string.IsNullOrWhiteSpace(codeForRecord);
		record.UploadResult = (int)(codeMissing ? UploadResultCode.DataInvalid : UploadResultCode.Ok);

		var stored = ThaDb.InsertProductRecord(record);

		if (stored)
		{
			if (record.OverallResult == 1) TotalOk++; else TotalNg++;
			var handler = OnProductRecord;
			handler?.Invoke(record);
		}

		_pendingWrites.Enqueue(new PendingWrite(R(Tha26003.MwUploadResult), new short[]
		{
			(short)(stored ? record.UploadResult : (int)UploadResultCode.Failed),
		}));

		LogMgr.Instance.Info($"[THA-UNLOAD] 码={(codeMissing ? "(无)" : codeForRecord)} 总结果={Tha26003.ResultText(record.OverallResult)} 落库={(stored ? "OK" : "失败")}");

		// 本件结束，重置在制上下文
		_ctx = new ProductContext();
	}

	// ============================================================
	// 辅助
	// ============================================================

	/// <summary>针高口径：DINT÷1000（默认高字在前）。</summary>
	private static PinHeightFormat ParsePinHeightFormat(string s)
		=> Enum.TryParse<PinHeightFormat>(s, true, out var v) ? v : PinHeightFormat.Int32Div1000;

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
		Snapshot = new ThaStationSnapshot(
			_connected,
			DateTime.Now,
			_heartbeat,
			TotalOk,
			TotalNg,
			new StationState(markTrig, markRes, Tha26003.MarkResultText(markRes), _ctx.MarkTime, _ctx.Code ?? ""),
			new StationState(scanTrig, scanRes, Tha26003.ScanResultText(scanRes), _ctx.ScanTime, _ctx.ScannedCode ?? _ctx.ScanRaw ?? ""),
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

	/// <summary>按当前映射把 %MW 编程地址换算为 Modbus 寄存器地址字符串。</summary>
	private string R(int mw) => _map.R(mw);

	private sealed record PendingWrite(string Reg, short[] Values);

	private enum StationKind { Mark, Scan, Upload }

	private sealed record StationJob(StationKind Kind, string? PlcContent, byte[]?[]? Segments);

	/// <summary>本件在制上下文（打码站建立 → 读码站补充 → 上传站消费后重置）。</summary>
	private sealed class ProductContext
	{
		public string? Code { get; set; }
		public string? ScannedCode { get; set; }
		public int MarkResult { get; set; }
		public int ReadResult { get; set; }
		public DateTime? MarkTime { get; set; }
		public DateTime? ScanTime { get; set; }
		public string? ScanRaw { get; set; }
		public int RetestCount { get; set; }
	}
}

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
