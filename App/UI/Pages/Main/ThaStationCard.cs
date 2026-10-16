using AppZC.Services;
using ZC.Mvvm;

namespace AppZC.UI.Pages;

/// <summary>
/// 主页工位状态卡（打码站 / 读码站 / 上传站）。
/// 由 MainVM 每 500ms 从 ThaPlcService 快照刷新；字段级比较，值不变不触发属性通知。
/// </summary>
[ObservableObject]
public partial class ThaStationCard
{
	/// <summary>工位名（打码站 / 读码站 / 上传站）。</summary>
	public partial string Title { get; set; } = "";

	/// <summary>工位副标题（设备与地址提示）。</summary>
	public partial string Subtitle { get; set; } = "";

	/// <summary>触发位显示（%MW97500 等）。</summary>
	public partial string TriggerText { get; set; } = "--";

	/// <summary>最近结果文本。</summary>
	public partial string ResultText { get; set; } = "--";

	/// <summary>结果颜色。</summary>
	public partial string ResultColor { get; set; } = "#9E9E9E";

	/// <summary>最近完成时间。</summary>
	public partial string TimeText { get; set; } = "--";

	/// <summary>明细（码 / 原因）。</summary>
	public partial string Detail { get; set; } = "--";

	/// <summary>按快照刷新（<paramref name="okCode"/> 为"成功"的结果码）。</summary>
	public void UpdateFrom(StationState s, int okCode, string address)
	{
		var v = s.Trigger.ToString();
		if (TriggerText != v) TriggerText = v;

		var r = string.IsNullOrEmpty(s.Text) ? "--" : s.Text;
		if (ResultText != r)
		{
			ResultText = r;
			ResultColor = s.Result switch
			{
				var x when x == okCode => "#2E7D32",
				0 => "#9E9E9E",
				_ => "#D32F2F",
			};
		}

		v = s.Time?.ToString("MM-dd HH:mm:ss") ?? "--";
		if (TimeText != v) TimeText = v;

		v = string.IsNullOrWhiteSpace(s.Detail) ? "--" : s.Detail;
		if (Detail != v) Detail = v;

		v = address;
		if (Subtitle != v) Subtitle = v;
	}
}
