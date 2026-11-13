using System.Collections.ObjectModel;
using AppZC.Models.DB;
using AppZC.Services;
using AppZC.UI.Dialogs;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ZC.Mvvm;

namespace AppZC.UI.Pages;

/// <summary>
/// 主页视图模型（THA26003 三站：打码站 / 读码站 / 上传站）。
/// 上部：三站状态卡 + PLC 状态卡（连接/心跳/累计 OK·NG/实时结果字）。
/// 下部：产品记录表（内存最近 200 条，启动时从 SQLite 预载）。
/// 数据来源：ThaPlcService（上传落库事件 + 500ms 快照轮询）。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
[ObservableObject]
public partial class MainVM : UiVM<MainPage>
{
	[Inject] public ThaPlcService? Plc { get; set; }

	// ===== 三站状态卡 =====
	// 地址副标题一律由协议常量推导（UI 不手写地址，避免与协议漂移）
	private static readonly string MarkAddr = $"{Tha26003.MwText(Tha26003.MwMarkTrigger)} / {Tha26003.MwText(Tha26003.MwMarkResult)[3..]}";
	private static readonly string ScanAddr = $"{Tha26003.MwText(Tha26003.MwScanTrigger)} / {Tha26003.MwText(Tha26003.MwScanResult)[3..]}";
	private static readonly string UnloadAddr = $"{Tha26003.MwText(Tha26003.MwUploadTrigger)} / {Tha26003.MwText(Tha26003.MwUploadResult)[3..]}";

	public ThaStationCard MarkStation { get; } = new() { Title = "打码站", Subtitle = MarkAddr };
	public ThaStationCard ScanStation { get; } = new() { Title = "读码站", Subtitle = ScanAddr };
	public ThaStationCard UploadStation { get; } = new() { Title = "上传站", Subtitle = UnloadAddr };

	/// <summary>心跳地址显示文本（如 %MW20502）。</summary>
	public string HeartbeatAddrText { get; } = $"心跳 {Tha26003.MwText(Tha26003.MwHeartbeat)}";

	/// <summary>实时结果字地址显示文本。</summary>
	public string LiveResultAddrText { get; } =
		$"实时结果字（{Tha26003.MwText(Tha26003.SegResultBase + Tha26003.OffWithstand)}"
		+ $"/{Tha26003.MwText(Tha26003.SegResultBase + Tha26003.OffPinHeight)[3..]}"
		+ $"/{Tha26003.MwText(Tha26003.SegResultBase + Tha26003.OffPosition)[3..]}）";

	// ===== PLC 状态卡片 =====
	public partial string PlcStatus { get; set; } = "未连接";
	public partial string PlcStatusColor { get; set; } = "#D32F2F";
	public partial string HeartbeatText { get; set; } = "--";
	public partial string LiveWithstand { get; set; } = "--";
	public partial string LivePinHeight { get; set; } = "--";
	public partial string LivePosition { get; set; } = "--";
	public partial int PlcTotalOk { get; set; }
	public partial int PlcTotalNg { get; set; }

	// ===== 外设连接状态（扫码枪 / 激光打码机，展示口径与 PLC 连接状态一致）=====
	public partial string ScannerLinkText { get; set; } = "--";
	public partial string ScannerLinkColor { get; set; } = "#9E9E9E";
	public partial string LaserLinkText { get; set; } = "--";
	public partial string LaserLinkColor { get; set; } = "#9E9E9E";

	// ===== 读码站联调：循环 1s 触发扫码 =====
	// 只把扫码枪返回的内容显示出来：不校对长度/时间窗/重码、不比对 PLC 码、
	// 不写 %MW10522、不落流水、不弹窗。
	public partial bool LoopScanEnabled { get; set; }
	public partial string LoopScanText { get; set; } = "未启用";

	private CancellationTokenSource? _loopScanCts;

	// ===== 顶部信息 =====
	public partial string CurrentShift { get; set; }
	public partial string CurrentTime { get; set; }

	// ===== 产品记录 =====
	public ObservableCollection<ThaHistoryRow> HistoryRecords { get; } = new();

	private const int MaxHistoryRows = 200;

	// ===== 实时消息条 =====
	public partial string PlcDataReadResult { get; set; } = "";

	private IDisposable? _statusTimer;
	private bool _eventSubscribed;
	private DateTime _lastClockSecond;

	// 告警弹窗：同一时刻只开一个，关闭后 3 秒内不再弹，避免连件失败刷屏
	private bool _alarmDialogOpen;
	private DateTime _alarmCooldownUntil = DateTime.MinValue;

	public MainVM()
	{
		CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
		CurrentShift = GetShiftByTime(DateTime.Now);

		// [Inject] 属性注入在构造完成后才由 IOC 填充，构造期 Plc 为 null → 订阅在首个 500ms 周期兜底执行
		EnsureSubscribed();
		_ = Task.Run(LoadHistoryFromDb);

		_statusTimer = DispatcherTimer.Run(() =>
		{
			RefreshStatus();
			UpdateClock();
			return true;
		}, TimeSpan.FromMilliseconds(500));
	}

	/// <summary>订阅上传落库事件与外设告警（幂等）。</summary>
	private void EnsureSubscribed()
	{
		if (_eventSubscribed || Plc == null) return;
		_eventSubscribed = true;
		Plc.OnProductRecord += OnProductRecord;
		Plc.OnProductRecordUpdated += OnProductRecordUpdated;
		Plc.OnDeviceAlarm += OnDeviceAlarm;
		LogMgr.Instance.Info("[MainVM] 已订阅上传落库事件与外设告警");
	}

	/// <summary>外设告警回调（作业线程）：切 UI 线程后写消息条 + 弹窗提示。</summary>
	private void OnDeviceAlarm(ThaDeviceAlarm alarm)
	{
		LogMgr.Instance.Warn($"[MainVM] 外设告警：{alarm.Title}");
		Dispatcher.UIThread.Post(() =>
		{
			PushMessage($"[{DateTime.Now:HH:mm:ss}] ⚠ {alarm.Title}");
			_ = ShowAlarmDialogAsync(alarm);
		});
	}

	/// <summary>弹窗提示（同一时刻只开一个，关闭后冷却 3 秒）。</summary>
	private async Task ShowAlarmDialogAsync(ThaDeviceAlarm alarm)
	{
		if (_alarmDialogOpen || DateTime.Now < _alarmCooldownUntil) return;

		var mainWindow = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
			? desktop.MainWindow
			: null;
		if (mainWindow == null) return;

		_alarmDialogOpen = true;
		try
		{
			await new MessageDialog(alarm.Title, alarm.Message).ShowDialog(mainWindow);
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[MainVM] 告警弹窗失败：{ex.Message}", ex);
		}
		finally
		{
			_alarmDialogOpen = false;
			_alarmCooldownUntil = DateTime.Now.AddSeconds(3);
		}
	}

	/// <summary>上传落库回调（作业线程）：切 UI 线程头插记录。</summary>
	private void OnProductRecord(ThaProductRecord record)
	{
		Dispatcher.UIThread.Post(() =>
		{
			HistoryRecords.Insert(0, ThaHistoryRow.From(record));
			while (HistoryRecords.Count > MaxHistoryRows)
				HistoryRecords.RemoveAt(HistoryRecords.Count - 1);

			PlcTotalOk = Plc?.TotalOk ?? 0;
			PlcTotalNg = Plc?.TotalNg ?? 0;

			var line = $"[{DateTime.Now:HH:mm:ss}] 采集 {record.ProductCode ?? "(无码)"} → {Tha26003.ResultText(record.OverallResult)}";
			PushMessage(line);
		});
	}

	/// <summary>
	/// 已落库记录被补读回填（读码晚于采集）→ 按 <see cref="ThaProductRecord.Id"/> 原位替换那一行，
	/// 不头插新行（否则同一个产品码会出现两行，看起来就像“重复入库”）。
	/// </summary>
	private void OnProductRecordUpdated(ThaProductRecord record)
	{
		Dispatcher.UIThread.Post(() =>
		{
			for (var i = 0; i < HistoryRecords.Count; i++)
			{
				if (HistoryRecords[i].RecordId != record.Id) continue;
				HistoryRecords[i] = ThaHistoryRow.From(record);
				PlcTotalOk = Plc?.TotalOk ?? 0;
				PlcTotalNg = Plc?.TotalNg ?? 0;
				PushMessage($"[{DateTime.Now:HH:mm:ss}] 补读回填 {record.ProductCode ?? "(无码)"} → 扫码 {Tha26003.ResultText(record.ScanResult)} / 等级 {record.ScanCodeLevel ?? "--"} / 总结果 {Tha26003.ResultText(record.OverallResult)}");
				return;
			}
		});
	}

	private void PushMessage(string line)
	{
		PlcDataReadResult = line + "\n" + PlcDataReadResult;
		var lines = PlcDataReadResult.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length > 5)
			PlcDataReadResult = string.Join("\n", lines.Take(5));
	}

	/// <summary>从 SQLite 预载最近 200 条记录。</summary>
	private void LoadHistoryFromDb()
	{
		try
		{
			if (!ThaDb.IsInitialized) return;
			var rows = ThaDb.RecentRecords(MaxHistoryRows).Select(ThaHistoryRow.From).ToList();
			Dispatcher.UIThread.Post(() =>
			{
				foreach (var row in rows)
					HistoryRecords.Add(row);
			});
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[MainVM] 预载记录失败：{ex.Message}", ex);
		}
	}

	partial void OnLoopScanEnabledChanged(bool value)
	{
		if (value) StartLoopScan();
		else StopLoopScan();
	}

	/// <summary>启动循环扫码（幂等：先停旧循环）。</summary>
	private void StartLoopScan()
	{
		StopLoopScan();
		var cts = new CancellationTokenSource();
		_loopScanCts = cts;
		LoopScanText = "启动中…";
		LogMgr.Instance.Info("[MainVM] 启用读码站循环扫码（1s，仅显示枪返回不去校对）");
		_ = Task.Run(() => LoopScanAsync(cts.Token));
	}

	/// <summary>停止循环扫码。</summary>
	private void StopLoopScan()
	{
		var cts = Interlocked.Exchange(ref _loopScanCts, null);
		if (cts == null) return;
		cts.Cancel();
		LoopScanText = "未启用";
		LogMgr.Instance.Info("[MainVM] 停用读码站循环扫码");
	}

	/// <summary>循环体（后台线程）：触发一次 → 显示枪返回 → 等 1s。不做任何判定。</summary>
	private async Task LoopScanAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			var plc = Plc;
			if (plc == null)
			{
				try { await Task.Delay(1000, ct); }
				catch (OperationCanceledException) { break; }
				continue;
			}

			ScanRead read;
			try
			{
				read = await plc.RawScanOnceAsync(ct);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				read = new ScanRead(false, null, null, $"读码异常：{ex.Message}", true);
			}

			var line = $"[{DateTime.Now:HH:mm:ss}] {FormatScanRaw(read)}";
			Dispatcher.UIThread.Post(() =>
			{
				if (_loopScanCts != null) LoopScanText = line;
			});

			try { await Task.Delay(1000, ct); }
			catch (OperationCanceledException) { break; }
		}
	}

	/// <summary>把一次读码结果压成一行原文（有原文用原文，没有才用异常描述）。</summary>
	private static string FormatScanRaw(ScanRead read)
	{
		var body = (read.Raw ?? read.Code ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
		if (body.Length == 0) body = (read.Message ?? "(空返回)").Replace("\r", " ").Replace("\n", " ").Trim();
		if (body.Length > 200) body = body[..200] + "…";
		return body;
	}

	/// <summary>500ms 刷新三站状态与 PLC 状态。</summary>
	private void RefreshStatus()
	{
		var plc = Plc;
		if (plc == null) return;
		EnsureSubscribed();

		var snap = plc.Snapshot;

		PlcStatus = snap.IsConnected ? "已连接" : "未连接";
		PlcStatusColor = snap.IsConnected ? "#2E7D32" : "#D32F2F";
		HeartbeatText = snap.IsConnected ? snap.Heartbeat.ToString() : "--";
		PlcTotalOk = snap.TotalOk;
		PlcTotalNg = snap.TotalNg;

		// 外设状态：由 ThaPlcService 的独立探测线程写入，与 PLC 连接状态无关
		var links = plc.Links;
		ScannerLinkText = links.Scanner.Text;
		ScannerLinkColor = LinkColor(links.Scanner.Status);
		LaserLinkText = links.Laser.Text;
		LaserLinkColor = LinkColor(links.Laser.Status);

		MarkStation.UpdateFrom(snap.Mark, (int)MarkResultCode.Ok, MarkAddr);
		ScanStation.UpdateFrom(snap.Scan, (int)ScanResultCode.Ok, ScanAddr);
		UploadStation.UpdateFrom(snap.Unload, (int)UploadResultCode.Ok, UnloadAddr);

		var live = snap.Live;
		LiveWithstand = Tha26003.ResultText(live?.WithstandResult ?? 0);
		LivePinHeight = Tha26003.ResultText(live?.PinHeightResult ?? 0);
		LivePosition = Tha26003.ResultText(live?.PositionResult ?? 0);
	}

	/// <summary>时钟/班次刷新（秒级去抖）。</summary>
	private void UpdateClock()
	{
		var now = DateTime.Now;
		if (now.Second == _lastClockSecond.Second && now.Minute == _lastClockSecond.Minute && now.Hour == _lastClockSecond.Hour)
			return;
		_lastClockSecond = now;
		CurrentTime = now.ToString("yyyy-MM-dd HH:mm:ss");
		CurrentShift = GetShiftByTime(now);
	}

	/// <summary>进入交互结构体监控页。</summary>
	[RelayCommand]
	private void OpenMonitor() => AppVM.NavigateToMonitor();

	private static string GetShiftByTime(DateTime now)
		=> (now.Hour >= 8 && now.Hour < 20) ? "早班" : "晚班";

	/// <summary>外设连接状态→颜色（与 PLC 连接状态同口径：绿=已连接 / 红=未连接 / 灰=未探测）。</summary>
	private static string LinkColor(DeviceLinkStatus status) => status switch
	{
		DeviceLinkStatus.Up => "#2E7D32",
		DeviceLinkStatus.Down => "#D32F2F",
		_ => "#9E9E9E",
	};
}
