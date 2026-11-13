using System.Collections.ObjectModel;
using System.Linq;
using AppZC.Models.DB;
using AppZC.Services;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using ZC.Mvvm;
using AppZC.UI.Ext;

namespace AppZC.UI.Pages;

/// <summary>
/// 统计·数据概览页 VM（THA26003 SQLite 本地库）。
/// 图表：按小时/班组/天的 OK/NG 计数；表格：选定日期白/晚班小时统计；
/// 明细：选定时间区间全部检测记录（倒序，全量 UI 显示 + DataGrid 虚拟化），可导出 Excel/CSV（列头按约定顺序）。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
public partial class DateVM : UiVM<DatePage>
{
	private IDisposable? _chartThemeSub;

	private static async Task UiMarshalAsync(Action uiAction)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			uiAction();
			return;
		}

		await Dispatcher.UIThread.InvokeAsync(uiAction);
	}

	/// <summary>
	/// 时间范围类型
	/// </summary>
	public partial string SelectedTimeRangeTypeStr { get; set; } = "按小时";

	/// <summary>
	/// 图表系列数据
	/// </summary>
	public partial ObservableCollection<ISeries> Series { get; set; } = [];

	/// <summary>
	/// X轴标签
	/// </summary>
	public partial ObservableCollection<string> XLabels { get; set; } = [];

	/// <summary>
	/// X轴配置
	/// </summary>
	public Axis[] XAxes { get; set; } =
	[
		new Axis
		{
			Labels = null, // 将在运行时绑定
			LabelsRotation = 45,
			TextSize = 14,
			LabelsPaint = ChartTheme.TextPaint(14),
			SeparatorsPaint = ChartTheme.SeparatorPaint()
		}
	];

	/// <summary>
	/// Y轴配置
	/// </summary>
	public Axis[] YAxes { get; set; } =
	[
		new Axis
		{
			TextSize = 14,
			LabelsPaint = ChartTheme.TextPaint(14),
			SeparatorsPaint = ChartTheme.SeparatorPaint(),
			MinLimit = 0
		}
	];

	/// <summary>
	/// 图例文字画笔（支持中文）
	/// </summary>
	public partial SolidColorPaint LegendTextPaint { get; set; } = ChartTheme.TextPaint(14);

	/// <summary>
	/// Tooltip文字画笔（支持中文）
	/// </summary>
	public partial SolidColorPaint TooltipTextPaint { get; set; } = ChartTheme.TextPaint(14);

	/// <summary>
	/// Tooltip背景画笔
	/// </summary>
	public partial SolidColorPaint TooltipBackgroundPaint { get; set; } = ChartTheme.TooltipBgPaint();

	/// <summary>
	/// 是否显示图表视图
	/// </summary>
	public partial bool ShowChartView { get; set; }

	/// <summary>
	/// 是否显示表格视图
	/// </summary>
	public partial bool ShowTableView { get; set; }

	/// <summary>
	/// 是否显示详细数据视图
	/// </summary>
	public partial bool ShowDetailView { get; set; }

	/// <summary>
	/// 是否显示数据追溯视图（按产品码精确查询检测记录）
	/// </summary>
	public partial bool ShowTraceView { get; set; }

	/// <summary>追溯查询的产品码输入。</summary>
	public partial string TraceProductCode { get; set; } = string.Empty;

	/// <summary>追溯查询结果（0 或 1 条记录，完整字段展示）。</summary>
	public ObservableCollection<ThaHistoryRow> TraceResult { get; } = new();

	/// <summary>
	/// 表格视图选择的日期
	/// </summary>
	public partial DateTimeOffset? SelectedDate { get; set; }

	/// <summary>明细查询区间起始日（含当天）。默认今天。区间变更不自动查询，点「查询明细」触发。</summary>
	public partial DateTimeOffset? DetailStartDate { get; set; } = new DateTimeOffset(DateTime.Today);

	/// <summary>明细查询区间结束日（含当天全天；查询用 [end, end+1)）。默认今天。</summary>
	public partial DateTimeOffset? DetailEndDate { get; set; } = new DateTimeOffset(DateTime.Today);

	/// <summary>明细加载中（按钮禁用 + 防重入）。</summary>
	public partial bool IsDetailLoading { get; set; }

	/// <summary>导出中（按钮禁用 + 防重入）。</summary>
	public partial bool IsExporting { get; set; }

	/// <summary>当前明细区间与条数提示，如 "2026-09-01 ~ 2026-09-08，共 378 条"。</summary>
	public partial string DetailRangeText { get; set; } = "";

	/// <summary>
	/// 白班表格数据（固定12小时，8:30-20:30）
	/// </summary>
	public ObservableCollection<HourlyStatisticsData> DayShiftTableData { get; set; } = new();

	/// <summary>
	/// 晚班表格数据（固定12小时，20:30-8:30）
	/// </summary>
	public ObservableCollection<HourlyStatisticsData> NightShiftTableData { get; set; } = new();

	/// <summary>明细数据（区间查询结果，倒序）。每次查询整体替换（单次 PropertyChanged），不逐条 Add，
	/// 配合 DataGrid 默认虚拟化只渲染可视行。</summary>
	public partial IReadOnlyList<ThaHistoryRow> DetailData { get; set; } = [];

	/// <summary>上次明细查询的原始记录（导出复用，所见即所得，不二次查库）。</summary>
	private List<ThaProductRecord> _detailRecords = [];

	/// <summary>上次明细查询区间 [Start, EndExclusive)（导出文件名与同源校验）。</summary>
	private (DateTime Start, DateTime EndExclusive)? _lastDetailRange;

	/// <summary>明细查询跨度上限（天），防误选全库。</summary>
	private const int MaxDetailRangeDays = 180;

	public DateVM()
	{
		ShowChartView = true;
		ShowTableView = false;
		ShowDetailView = false;

		// 默认选择今天
		SelectedDate = new DateTimeOffset(DateTime.Today);

		InitializeTableData();

		// 延迟加载（明细走区间查询，默认区间 = 今天）
		_ = Task.Run(async () =>
		{
			await Task.Delay(100).ConfigureAwait(false);
			await LoadTableDataForDayAsync(DateTime.Today).ConfigureAwait(false);
			await QueryDetail().ConfigureAwait(false);
		});

		// 订阅主题切换，实时刷新图表文字颜色（浅色模式变黑字）
		_chartThemeSub = ChartTheme.WhenThemeChanges(this, ApplyChartTheme);
	}

	/// <summary>
	/// 主题切换时刷新图表画笔（浅色模式文字变黑，深色模式文字变浅）
	/// </summary>
	private void ApplyChartTheme()
	{
		if (XAxes is not null)
			foreach (var ax in XAxes)
			{
				ax.LabelsPaint = ChartTheme.TextPaint((float)(ax.TextSize > 0 ? ax.TextSize : 14));
				ax.SeparatorsPaint = ChartTheme.SeparatorPaint();
			}
		if (YAxes is not null)
			foreach (var ax in YAxes)
			{
				ax.LabelsPaint = ChartTheme.TextPaint((float)(ax.TextSize > 0 ? ax.TextSize : 14));
				ax.SeparatorsPaint = ChartTheme.SeparatorPaint();
			}

		LegendTextPaint = ChartTheme.TextPaint(14);
		TooltipTextPaint = ChartTheme.TextPaint(14);
		TooltipBackgroundPaint = ChartTheme.TooltipBgPaint();
	}

	/// <summary>
	/// 日期改变时自动刷新表格数据（白/晚班小时统计为单日语义）。
	/// 明细不受此触发：明细走区间选择 + 手动「查询明细」，避免选日期中途产生垃圾查询。
	/// </summary>
	partial void OnSelectedDateChanged(DateTimeOffset? value)
	{
		if (!value.HasValue)
			return;
		var day = value.Value.Date;
		_ = Task.Run(async () =>
		{
			await LoadTableDataForDayAsync(day).ConfigureAwait(false);
		});
	}

	/// <summary>
	/// 初始化表格数据（固定时间段）
	/// </summary>
	private void InitializeTableData()
	{
		// 白班：8:30-20:30（12个小时）
		for (int i = 0; i < 12; i++)
		{
			int startHour = 8 + i;
			int endHour = 9 + i;
			DayShiftTableData.Add(new HourlyStatisticsData
			{
				TimeRange = $"{startHour:D2}:30-{endHour:D2}:30"
			});
		}

		// 晚班：20:30-8:30（12个小时）
		for (int i = 0; i < 12; i++)
		{
			int startHour = (20 + i) % 24;
			int endHour = (21 + i) % 24;
			NightShiftTableData.Add(new HourlyStatisticsData
			{
				TimeRange = $"{startHour:D2}:30-{endHour:D2}:30"
			});
		}
	}

	/// <summary>
	/// 视图切换时确保只有一个视图显示
	/// </summary>
	partial void OnShowChartViewChanged(bool value)
	{
		if (value)
		{
			ShowTableView = false;
			ShowDetailView = false;
			ShowTraceView = false;
		}
	}

	/// <summary>
	/// 视图切换时确保只有一个视图显示
	/// </summary>
	partial void OnShowTableViewChanged(bool value)
	{
		if (value)
		{
			ShowChartView = false;
			ShowDetailView = false;
			ShowTraceView = false;
		}
	}

	/// <summary>
	/// 视图切换时确保只有一个视图显示
	/// </summary>
	partial void OnShowDetailViewChanged(bool value)
	{
		if (value)
		{
			ShowChartView = false;
			ShowTableView = false;
			ShowTraceView = false;
		}
	}

	/// <summary>
	/// 数据追溯视图与其他视图互斥
	/// </summary>
	partial void OnShowTraceViewChanged(bool value)
	{
		if (value)
		{
			ShowChartView = false;
			ShowTableView = false;
			ShowDetailView = false;
		}
	}

	/// <summary>
	/// 追溯查询：按产品码精确查询检测记录（同码仅一份，含全部测量字段）
	/// </summary>
	public async Task @TraceQuery()
	{
		var code = TraceProductCode?.Trim() ?? string.Empty;
		if (code.Length == 0)
		{
			await UiMarshalAsync(() => _ = ShowNotification("请输入产品码", UiMessageType.Warning));
			return;
		}

		var records = await Task.Run(() =>
		{
			if (!ThaDb.IsInitialized) return new List<ThaProductRecord>();
			return ThaDb.Db.Queryable<ThaProductRecord>()
				.Where(r => r.ProductCode == code)
				.ToList();
		}).ConfigureAwait(false);

		await UiMarshalAsync(() =>
		{
			TraceResult.Clear();
			foreach (var r in records)
				TraceResult.Add(ThaHistoryRow.From(r));

			_ = TraceResult.Count > 0
				? ShowNotification($"追溯成功：已定位产品码 {code} 的检测记录", UiMessageType.Success)
				: ShowNotification($"未找到产品码 {code} 的检测记录", UiMessageType.Warning);
		});
	}

	/// <summary>
	/// 查询数据（SQLite）：按当前可见视图派发，一个按钮只跑当前视图需要的查询。
	/// 图表视图 → 按统计方式刷新 OK/NG 图表；表格视图 → 单日白/晚班；
	/// 详细数据视图 → 区间明细（<see cref="QueryDetail"/> 自带结果提示）。
	/// 数据追溯有独立的「追溯查询」按钮，全局查询按钮在该视图隐藏。
	/// </summary>
	public async Task @Query()
	{
		try
		{
			if (ShowDetailView)
			{
				await QueryDetail();
				return;
			}

			if (ShowTableView)
			{
				await UiMarshalAsync(() =>
				{
					DayShiftTableData.Clear();
					NightShiftTableData.Clear();
					InitializeTableData();
				});

				var day = SelectedDate?.Date ?? DateTime.Today;
				await LoadTableDataForDayAsync(day);
				_ = ShowNotification("查询成功！", UiMessageType.Success);
				return;
			}

			// 图表视图（默认）
			await LoadChartAsync();
			_ = ShowNotification("查询成功！", UiMessageType.Success);
		}
		catch (Exception ex)
		{
			_ = ShowNotification($"查询失败！{ex.Message}", UiMessageType.Error);
			LogMgr.Instance.Error($"数据概览查询失败：{ex.Message}", ex);
		}
	}

	/// <summary>按当前统计方式刷新图表（OK/NG 计数）。统计方式下拉变更与图表视图查询共用。</summary>
	private async Task LoadChartAsync()
	{
		switch (SelectedTimeRangeTypeStr)
		{
			case "按小时":
				await LoadDataByHour();
				break;
			case "按班组":
				await LoadDataByShift();
				break;
			case "按天":
				await LoadDataByDay();
				break;
		}

		await UiMarshalAsync(() => XAxes[0].Labels = XLabels.ToArray());
	}

	/// <summary>查询 SQLite 中 [start,end) 区间的 (时间, 结果) 明细，供内存分组。总结果可空（未采集）。</summary>
	private static List<(DateTime Time, int? Result)> QueryRange(DateTime start, DateTime end)
	{
		if (!ThaDb.IsInitialized) return new List<(DateTime, int?)>();
		return ThaDb.Db.Queryable<ThaProductRecord>()
			.Where(r => r.CreateTime >= start && r.CreateTime < end)
			.Select(r => new { r.CreateTime, r.OverallResult })
			.ToList()
			.Select(x => (x.CreateTime, x.OverallResult))
			.ToList();
	}

	/// <summary>
	/// 按小时加载数据（最近 24 个整点段，对齐到 30 分钟）
	/// </summary>
	private async Task LoadDataByHour()
	{
		var now = DateTime.Now;
		var segmentStart = now.Minute >= 30
			? now.Date.AddHours(now.Hour).AddMinutes(30)
			: now.Date.AddHours(now.Hour - 1).AddMinutes(30);
		var start = segmentStart.AddHours(-23);
		var end = segmentStart.AddHours(1);

		var records = await Task.Run(() => QueryRange(start, end)).ConfigureAwait(false);

		var labels = new List<string>();
		var okData = new List<int>();
		var ngData = new List<int>();
		for (int i = 0; i < 24; i++)
		{
			var hs = start.AddHours(i);
			var he = hs.AddHours(1);
			labels.Add(hs.Date != now.Date ? $"{hs:MM-dd HH:mm}-{he:HH:mm}" : $"{hs:HH:mm}-{he:HH:mm}");
			okData.Add(records.Count(r => r.Time >= hs && r.Time < he && r.Result == 1));
			ngData.Add(records.Count(r => r.Time >= hs && r.Time < he && r.Result == 2));
		}

		await ApplyChartAsync(labels, okData, ngData);
	}

	/// <summary>
	/// 按班组加载数据（最近一周，14 个班组）
	/// </summary>
	private async Task LoadDataByShift()
	{
		var ranges = ShiftRanges(14);
		var minStart = ranges.Min(r => r.Start);
		var maxEnd = ranges.Max(r => r.End);

		var records = await Task.Run(() => QueryRange(minStart, maxEnd)).ConfigureAwait(false);

		var labels = new List<string>();
		var okData = new List<int>();
		var ngData = new List<int>();
		foreach (var (label, s, e) in ranges)
		{
			labels.Add(label);
			okData.Add(records.Count(r => r.Time >= s && r.Time < e && r.Result == 1));
			ngData.Add(records.Count(r => r.Time >= s && r.Time < e && r.Result == 2));
		}

		await ApplyChartAsync(labels, okData, ngData);
	}

	/// <summary>
	/// 按天加载数据（最近 30 天）
	/// </summary>
	private async Task LoadDataByDay()
	{
		var now = DateTime.Now;
		var start = now.AddDays(-29).Date;
		var end = now.Date.AddDays(1);

		var records = await Task.Run(() => QueryRange(start, end)).ConfigureAwait(false);

		var labels = new List<string>();
		var okData = new List<int>();
		var ngData = new List<int>();
		for (int i = 29; i >= 0; i--)
		{
			var ds = now.AddDays(-i).Date;
			var de = ds.AddDays(1);
			labels.Add(ds.ToString("MM-dd"));
			okData.Add(records.Count(r => r.Time >= ds && r.Time < de && r.Result == 1));
			ngData.Add(records.Count(r => r.Time >= ds && r.Time < de && r.Result == 2));
		}

		await ApplyChartAsync(labels, okData, ngData);
	}

	/// <summary>把 OK/NG 序列写入图表（UI 线程）。</summary>
	private async Task ApplyChartAsync(List<string> labels, List<int> okData, List<int> ngData)
	{
		await UiMarshalAsync(() =>
		{
			Series.Clear();
			XLabels.Clear();
			foreach (var label in labels)
				XLabels.Add(label);

			Series.Add(new ColumnSeries<int>
			{
				Name = "OK",
				Values = okData,
				Fill = new SolidColorPaint(SKColor.Parse("#4CAF50")),
				MaxBarWidth = 20
			});

			Series.Add(new ColumnSeries<int>
			{
				Name = "NG",
				Values = ngData,
				Fill = new SolidColorPaint(SKColor.Parse("#F44336")),
				MaxBarWidth = 20
			});
		});
	}

	/// <summary>最近 n 个班组区间（当前班次在前）。</summary>
	private static List<(string Label, DateTime Start, DateTime End)> ShiftRanges(int count)
	{
		var now = DateTime.Now;
		var isMorningShift = now.Hour >= 8 && now.Hour < 20;
		var ranges = new List<(string, DateTime, DateTime)>();
		for (int i = count - 1; i >= 0; i--)
		{
			var daysBack = i / 2;
			var isCurrentMorning = isMorningShift ? i % 2 == 0 : i % 2 == 1;
			var targetDate = now.Date.AddDays(-daysBack);
			if (isCurrentMorning)
			{
				var start = targetDate.AddHours(8).AddMinutes(30);
				ranges.Add(($"{targetDate:MM-dd}早班", start, start.AddHours(12)));
			}
			else
			{
				var prev = targetDate.AddDays(-1);
				var start = prev.AddHours(20).AddMinutes(30);
				ranges.Add(($"{prev:MM-dd}晚班", start, start.AddHours(12)));
			}
		}
		return ranges;
	}

	/// <summary>
	/// 加载选定日期白/晚班小时统计（SQLite 查询 + 内存分组）
	/// </summary>
	private async Task LoadTableDataForDayAsync(DateTime selectedDay)
	{
		var dayShiftStart = selectedDay.AddHours(8).AddMinutes(30);
		var nightShiftStart = selectedDay.AddHours(20).AddMinutes(30);
		var rangeEnd = nightShiftStart.AddHours(12);

		var records = await Task.Run(() => QueryRange(dayShiftStart, rangeEnd)).ConfigureAwait(false);

		var dayOk = new int[12];
		var dayNg = new int[12];
		var nightOk = new int[12];
		var nightNg = new int[12];
		for (int i = 0; i < 12; i++)
		{
			var hs = dayShiftStart.AddHours(i);
			var he = hs.AddHours(1);
			dayOk[i] = records.Count(r => r.Time >= hs && r.Time < he && r.Result == 1);
			dayNg[i] = records.Count(r => r.Time >= hs && r.Time < he && r.Result == 2);

			var ns = nightShiftStart.AddHours(i);
			var ne = ns.AddHours(1);
			nightOk[i] = records.Count(r => r.Time >= ns && r.Time < ne && r.Result == 1);
			nightNg[i] = records.Count(r => r.Time >= ns && r.Time < ne && r.Result == 2);
		}

		await UiMarshalAsync(() =>
		{
			for (var i = 0; i < 12 && i < DayShiftTableData.Count; i++)
			{
				DayShiftTableData[i].产品出料OK = dayOk[i];
				DayShiftTableData[i].产品出料NG = dayNg[i];
			}
			for (var i = 0; i < 12 && i < NightShiftTableData.Count; i++)
			{
				NightShiftTableData[i].产品出料OK = nightOk[i];
				NightShiftTableData[i].产品出料NG = nightNg[i];
			}
		});
	}

	/// <summary>
	/// 统计方式改变时只刷新图表（表格/明细不受影响）
	/// </summary>
	partial void OnSelectedTimeRangeTypeStrChanged(string value)
	{
		_ = LoadChartAsync();
	}

	/// <summary>
	/// 按时间区间加载明细（倒序，全量，无条数上限）：
	/// 区间 [start.Date, end.Date+1)，端点日含当天全天；跨度上限 <see cref="MaxDetailRangeDays"/> 天。
	/// 区间变更不自动查询，由 UI「查询明细」按钮触发（<c>QueryDetailCommand</c>）。
	/// 结果缓存到 <see cref="_detailRecords"/> 供导出复用（所见即所得，不二次查库）。
	/// </summary>
	public async Task @QueryDetail()
	{
		if (IsDetailLoading) return;

		var startDay = DetailStartDate?.Date ?? DateTime.Today;
		var endDay = DetailEndDate?.Date ?? startDay;

		if (endDay < startDay)
		{
			await UiMarshalAsync(() => _ = ShowNotification("开始日期不能晚于结束日期", UiMessageType.Warning));
			return;
		}
		if ((endDay - startDay).TotalDays + 1 > MaxDetailRangeDays)
		{
			await UiMarshalAsync(() => _ = ShowNotification($"查询跨度不能超过 {MaxDetailRangeDays} 天", UiMessageType.Warning));
			return;
		}

		// 本方法可能由构造函数里的 Task.Run 在后台线程调用，所有绑定属性写入必须经 UiMarshalAsync
		await UiMarshalAsync(() => IsDetailLoading = true);
		try
		{
			var endExclusive = endDay.AddDays(1);

			// 区间查询走 idx_tha_create_id (CreateTime, Id)：CreateTime 范围扫描 + 天然倒序，无结果集排序
			var records = await Task.Run(() =>
			{
				if (!ThaDb.IsInitialized) return new List<ThaProductRecord>();
				return ThaDb.Db.Queryable<ThaProductRecord>()
					.Where(r => r.CreateTime >= startDay && r.CreateTime < endExclusive)
					.OrderByDescending(r => r.CreateTime)
					.ToList();
			}).ConfigureAwait(false);

			// 投影同样在后台线程完成（单行 ~76 个显示字符串，全量时不能在 UI 线程做）
			var rows = await Task.Run(() => records.Select(ThaHistoryRow.From).ToList()).ConfigureAwait(false);

			_detailRecords = records;
			_lastDetailRange = (startDay, endExclusive);

			await UiMarshalAsync(() =>
			{
				DetailData = rows;  // 整体替换：单次 PropertyChanged，DataGrid 虚拟化只渲染可视行
				DetailRangeText = $"{startDay:yyyy-MM-dd} ~ {endDay:yyyy-MM-dd}，共 {rows.Count} 条";
				_ = ShowNotification($"查询成功：共 {rows.Count} 条", UiMessageType.Success);
			});
		}
		catch (Exception ex)
		{
			await UiMarshalAsync(() =>
			{
				_ = ShowNotification($"查询失败！{ex.Message}", UiMessageType.Error);
				LogMgr.Instance.Error($"明细查询失败：{ex.Message}", ex);
			});
		}
		finally
		{
			await UiMarshalAsync(() => IsDetailLoading = false);
		}
	}

	// ============================================================
	// 导出（Excel / CSV，列头按约定顺序）
	// ============================================================

	/// <summary>导出数据投影：统一走 <see cref="ThaExportRow.From"/>（与每日自动报表同一列定义）。</summary>
	private List<ThaExportRow> ProjectExportData()
		=> _detailRecords.Select(ThaExportRow.From).ToList();

	/// <summary>
	/// 导出产品出料详细数据（Excel）
	/// </summary>
	public async Task @ExportDetail()
	{
		await ExportAsync("xlsx", excelType: null);
	}

	/// <summary>
	/// 导出产品出料详细数据（CSV）
	/// </summary>
	public async Task @ExportDetailCsv()
	{
		await ExportAsync("csv", excelType: MiniExcelLibs.ExcelType.CSV);
	}

	private async Task ExportAsync(string ext, MiniExcelLibs.ExcelType? excelType)
	{
		if (IsExporting) return;
		if (_detailRecords.Count == 0 || _lastDetailRange == null)
		{
			_ = ShowNotification("请先查询明细数据", UiMessageType.Warning);
			return;
		}

		IsExporting = true;
		try
		{
			// 同源导出：复用上次明细查询结果（所见即所得），不二次查库
			var exportData = await Task.Run(ProjectExportData).ConfigureAwait(true);
			if (exportData.Count == 0)
			{
				_ = ShowNotification("没有数据可导出", UiMessageType.Warning);
				return;
			}

			var title = excelType == MiniExcelLibs.ExcelType.CSV ? "CSV" : "Excel";
			var (rangeStart, rangeEndExclusive) = _lastDetailRange.Value;
			var saveDialog = new FilePickerSaveOptions
			{
				Title = $"导出检测数据为{title}",
				DefaultExtension = ext,
				SuggestedFileName = $"检测数据_{rangeStart:yyyyMMdd}-{rangeEndExclusive.AddDays(-1):yyyyMMdd}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}",
				FileTypeChoices = new[]
				{
					new FilePickerFileType($"{title}文件") { Patterns = new[] { $"*.{ext}" } }
				}
			};

			var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
				Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
					? desktop.MainWindow
					: null;

			if (topLevel == null)
			{
				_ = ShowNotification("无法获取主窗口", UiMessageType.Error);
				return;
			}

			var file = await topLevel.StorageProvider.SaveFilePickerAsync(saveDialog);
			if (file == null)
			{
				return; // 用户取消
			}

			using (var stream = await file.OpenWriteAsync())
			{
				if (excelType == MiniExcelLibs.ExcelType.CSV)
				{
					var configuration = new MiniExcelLibs.Csv.CsvConfiguration
					{
						StreamWriterFunc = (s) => new System.IO.StreamWriter(s, new System.Text.UTF8Encoding(true))
					};
					await MiniExcelLibs.MiniExcel.SaveAsAsync(stream, exportData,
						excelType: MiniExcelLibs.ExcelType.CSV,
						configuration: configuration);
				}
				else
				{
					await MiniExcelLibs.MiniExcel.SaveAsAsync(stream, exportData);
				}
			}

			_ = ShowNotification($"导出成功：共 {exportData.Count} 条", UiMessageType.Success);
			LogMgr.Instance.Info($"检测数据已导出为{title}：{file.Path.LocalPath}，共 {exportData.Count} 条");
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"导出检测数据失败：{ex.Message}");
			_ = ShowNotification($"导出失败！{ex.Message}", UiMessageType.Error);
		}
		finally
		{
			IsExporting = false;
		}
	}
}

/// <summary>
/// 每小时统计数据（用于固定时间段表格显示）
/// </summary>
[ObservableObject]
public partial class HourlyStatisticsData
{
	/// <summary>
	/// 时间段（如 "08:30-09:30"）
	/// </summary>
	public partial string TimeRange { get; set; } = string.Empty;

	/// <summary>
	/// OK 数量
	/// </summary>
	public partial int 产品出料OK { get; set; }

	/// <summary>
	/// NG 数量
	/// </summary>
	public partial int 产品出料NG { get; set; }

	/// <summary>
	/// 总数
	/// </summary>
	public int 产品出料总数 => 产品出料OK + 产品出料NG;

	/// <summary>
	/// 良率（百分比）
	/// </summary>
	public string 产品出料良率
	{
		get
		{
			if (产品出料总数 == 0)
			{
				return "0.0";
			}
			return $"{产品出料OK * 100.0 / 产品出料总数:F1}";
		}
	}
}