using System.Collections.ObjectModel;
using AppZC.Models.DB;
using AppZC.Services;
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

	/// <summary>订阅上传落库事件（幂等）。</summary>
	private void EnsureSubscribed()
	{
		if (_eventSubscribed || Plc == null) return;
		_eventSubscribed = true;
		Plc.OnProductRecord += OnProductRecord;
		LogMgr.Instance.Info("[MainVM] 已订阅上传落库事件");
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

			var line = $"[{DateTime.Now:HH:mm:ss}] 上传 {record.ProductCode ?? "(无码)"} → {Tha26003.ResultText(record.OverallResult)}"
				+ $"（打码{Tha26003.MarkResultText(record.MarkResult)}/读码{Tha26003.ScanResultText(record.ReadResult)}）";
			PushMessage(line);
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
}
