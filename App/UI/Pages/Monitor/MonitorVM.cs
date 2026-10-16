using System.Collections.ObjectModel;
using Avalonia.Threading;
using AppZC.Services;
using ZC.Mvvm;

namespace AppZC.UI.Pages.Monitor;

/// <summary>
/// THA26003 协议监控页 VM。
/// 显示三站信号位（打码/读码/上传 触发·结果）、握手心跳，以及数据区全部寄存器的实时值
/// （数据来自 ThaPlcService 快照，500ms 刷新）。
/// 生命周期：由 MonitorPage 的视觉树挂载/卸载事件调用 StartAutoRefresh / StopAutoRefresh。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
[ObservableObject]
public partial class MonitorVM : UiVM<MonitorPage>
{
	[Inject] public ThaPlcService? Plc { get; set; }

	// ===== 顶部状态 =====
	public partial string PlcStatus { get; set; } = "未连接";
	public partial string LastRefreshTime { get; set; } = "--";

	/// <summary>信号行集合（构造时静态建表，定时器只更新 Value）。</summary>
	public ObservableCollection<MonitorSignalRow> Signals { get; } = new();

	/// <summary>行值求值委托：快照 → 该行显示值。</summary>
	private readonly Dictionary<MonitorSignalRow, Func<ThaStationSnapshot, string>> _valueGetters = new();

	private IDisposable? _timer;

	public MonitorVM()
	{
		BuildSignals();
	}

	// ===== 生命周期 =====

	public void StartAutoRefresh()
	{
		RefreshFromSnapshot();
		_timer ??= DispatcherTimer.Run(OnRefreshTick, TimeSpan.FromMilliseconds(500));
	}

	public void StopAutoRefresh()
	{
		_timer?.Dispose();
		_timer = null;
	}

	private bool OnRefreshTick()
	{
		RefreshFromSnapshot();
		return true;
	}

	/// <summary>从快照刷新全部信号行（值变化才赋值，避免上百行通知风暴）。</summary>
	private void RefreshFromSnapshot()
	{
		var plc = Plc;
		if (plc == null) return;

		var snap = plc.Snapshot;
		PlcStatus = snap.IsConnected ? "已连接" : "未连接";
		LastRefreshTime = DateTime.Now.ToString("HH:mm:ss.fff");

		foreach (var (row, getter) in _valueGetters)
		{
			var v = getter(snap);
			if (!string.Equals(row.Value, v, StringComparison.Ordinal))
				row.Value = v;
		}
	}

	// ===== 信号表构建（THA26003 协议地址表）=====

	private void AddSignal(string structName, string signal, string address, string description,
		Func<ThaStationSnapshot, string> getter)
	{
		var row = new MonitorSignalRow
		{
			StructName = structName,
			Signal = signal,
			Address = address,
			Description = description,
		};
		Signals.Add(row);
		_valueGetters[row] = getter;
	}

	private static string ResultText(int v) => v switch { 1 => "OK", 2 => "NG", _ => "--" };

	/// <summary>数值显示：null（未采集）一律 "--"，不伪装成 0.000。</summary>
	private static string F(double?[]? arr, int index)
		=> arr == null || index >= arr.Length || arr[index] == null ? "--" : arr[index]!.Value.ToString("F3");

	private void BuildSignals()
	{
		// 地址字符串一律由 Tha26003 常量推导，杜绝 UI 手写地址与协议不同步
		AddSignal("公共", "上位机心跳", Tha26003.MwText(Tha26003.MwHeartbeat),
			"上位机每 0.5s 写 1，告知 PLC 在线",
			s => s.Heartbeat.ToString());
		AddSignal("公共", "采集启动", Tha26003.MwText(Tha26003.MwStart) + "/" + (Tha26003.MwStart + 1),
			"PLC 置 20001=1 → 上位机开始采集并回写 0",
			s => s.IsConnected ? "已启动" : "--");

		// ===== 打码站 =====
		AddSignal("打码站", "打码触发", Tha26003.MwText(Tha26003.MwMarkTrigger),
			"PLC 写 1 → 上位机生成内容并下发打标机；上位机收到即清 0",
			s => s.Mark.Trigger.ToString());
		AddSignal("打码站", "打码结果", Tha26003.MwText(Tha26003.MwMarkResult),
			"1=OK 2=打标NG 3=回执超时 4=生成失败 5=MES异常 6=回执不符",
			s => s.Mark.Result == 0 ? "--" : $"{s.Mark.Result} ({s.Mark.Text})");
		AddSignal("打码站", "打码内容回写", Tha26003.MwRangeText(Tha26003.MwMarkEcho, Tha26003.MarkEchoWords),
			"上位机回写给 PLC 的最终打标内容（40WORD）",
			s => string.IsNullOrWhiteSpace(s.Mark.Detail) ? "--" : s.Mark.Detail);
		AddSignal("打码站", "PLC附带内容", Tha26003.MwRangeText(Tha26003.MwPlcContent, Tha26003.PlcContentWords),
			"待定：非空时用作打标内容（20WORD）",
			_ => "--");

		// ===== 读码站 =====
		AddSignal("读码站", "读码触发", Tha26003.MwText(Tha26003.MwScanTrigger),
			"PLC 写 1 → 上位机触发扫码枪（基恩士 SR-1000）；上位机收到即清 0",
			s => s.Scan.Trigger.ToString());
		AddSignal("读码站", "读码结果", Tha26003.MwText(Tha26003.MwScanResult),
			"1=OK 2=未扫到 3=重码 4=错码 5=枪异常 6=MES异常",
			s => s.Scan.Result == 0 ? "--" : $"{s.Scan.Result} ({s.Scan.Text})");
		AddSignal("读码站", "读码内容回写", Tha26003.MwRangeText(Tha26003.MwScanEcho, Tha26003.ScanEchoWords),
			"上位机回写给 PLC 的实际读到的码（20WORD）",
			s => string.IsNullOrWhiteSpace(s.Scan.Detail) ? "--" : s.Scan.Detail);

		// ===== 上传站 =====
		AddSignal("上传站", "上传触发", Tha26003.MwText(Tha26003.MwUploadTrigger),
			"PLC 写 1（同时冻结数据区）→ 上位机抓取全量信息",
			s => s.Unload.Trigger.ToString());
		AddSignal("上传站", "上传结果", Tha26003.MwText(Tha26003.MwUploadResult),
			"1=落库OK 2=数据NG 3=失败/超时；PLC 读到后解冻数据区",
			s => s.Unload.Result == 0 ? "--" : $"{s.Unload.Result} ({s.Unload.Text})");

		// ===== 数据区·结果字 =====
		AddSignal("数据区", "导通耐压结果", Tha26003.MwText(Tha26003.SegResultBase + Tha26003.OffWithstand),
			"1=OK 2=NG", s => ResultText(s.Live?.WithstandResult ?? 0));
		AddSignal("数据区", "针高结果", Tha26003.MwText(Tha26003.SegResultBase + Tha26003.OffPinHeight),
			"1=OK 2=NG", s => ResultText(s.Live?.PinHeightResult ?? 0));
		AddSignal("数据区", "位置度结果", Tha26003.MwText(Tha26003.SegResultBase + Tha26003.OffPosition),
			"1=OK 2=NG", s => ResultText(s.Live?.PositionResult ?? 0));
		AddSignal("数据区", "总结果", "MES 自算",
			"其余结果无 NG(2) → OK(1)；有一项 NG → NG(2)；数据不齐 → --",
			s => Tha26003.ResultText(Tha26003.ComputeOverall(null, null,
				s.Live?.WithstandResult, s.Live?.PinHeightResult, s.Live?.PositionResult)));
		AddSignal("数据区", "是否复检", "无地址（PLC 未提供）",
			"协议地址表此栏为「暂无」→ 记录里恒为 NULL（显示 --）；PLC 定义该地址后接入上传站读取",
			_ => "--");

		// ===== 数据区·上针高 Pin1~15 =====
		for (int i = 0; i < Tha26003.PinHeightCount; i++)
		{
			int idx = i;
			AddSignal("上针高", $"上针高 Pin{i + 1}", Tha26003.MwText(Tha26003.SegTopPinBase + i * 2),
				"2WORD 数值，上传时读取",
				s => F(s.Live?.TopPinHeights, idx));
		}

		// ===== 数据区·下针高 Pin1~15 =====
		for (int i = 0; i < Tha26003.PinHeightCount; i++)
		{
			int idx = i;
			AddSignal("下针高", $"下针高 Pin{i + 1}", Tha26003.MwText(Tha26003.SegBottomPinBase + i * 2),
				"2WORD 数值，上传时读取",
				s => F(s.Live?.BottomPinHeights, idx));
		}

		// ===== 数据区·上位置度 PIN1~15 X/Y =====
		for (int i = 0; i < Tha26003.PositionCount; i++)
		{
			int idx = i;
			AddSignal("上位置度", $"上位置度 PIN{i + 1}_X", Tha26003.MwText(Tha26003.SegTopPosBase + i * 4),
				"2WORD 数值，上传时读取", s => F(s.Live?.TopPosX, idx));
			AddSignal("上位置度", $"上位置度 PIN{i + 1}_Y", Tha26003.MwText(Tha26003.SegTopPosBase + i * 4 + 2),
				"2WORD 数值，上传时读取", s => F(s.Live?.TopPosY, idx));
		}

		// ===== 数据区·下位置度 PIN1~15 X/Y =====
		for (int i = 0; i < Tha26003.PositionCount; i++)
		{
			int idx = i;
			AddSignal("下位置度", $"下位置度 PIN{i + 1}_X", Tha26003.MwText(Tha26003.SegBottomPosBase + i * 4),
				"2WORD 数值，上传时读取", s => F(s.Live?.BottomPosX, idx));
			AddSignal("下位置度", $"下位置度 PIN{i + 1}_Y", Tha26003.MwText(Tha26003.SegBottomPosBase + i * 4 + 2),
				"2WORD 数值，上传时读取", s => F(s.Live?.BottomPosY, idx));
		}
	}
}
