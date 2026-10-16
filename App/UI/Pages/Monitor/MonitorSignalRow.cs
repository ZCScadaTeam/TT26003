using ZC.Mvvm;

namespace AppZC.UI.Pages.Monitor;

/// <summary>
/// 交互结构体监控页的单行信号模型（INV / DIM / Mark 三结构体共用）。
/// 属性沿用项目约定（[ObservableObject] + partial 属性自动通知）。
/// </summary>
[ObservableObject]
public partial class MonitorSignalRow
{
	/// <summary>所属结构体：INV（耐压）/ DIM（尺寸）/ Mark（喷码）。</summary>
	public partial string StructName { get; set; } = "";

	/// <summary>信号名（含机位，如 拍照结果[1]·1A）。</summary>
	public partial string Signal { get; set; } = "";

	/// <summary>PLC 绝对地址（如 D3002）。</summary>
	public partial string Address { get; set; } = "";

	/// <summary>当前值（页面 500ms 自动刷新）。</summary>
	public partial string Value { get; set; } = "--";

	/// <summary>信号说明 / 取值含义。</summary>
	public partial string Description { get; set; } = "";
}
