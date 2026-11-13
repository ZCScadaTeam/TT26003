using AppZC.Services;
using ZC.Mvvm;

namespace AppZC.UI.Pages;

/// <summary>
/// 产品码分段编辑行（配置页「码规则 → 产品码分段」DataGrid 的一行）。
/// 段名 / 取值方式 / 取值或时间格式 / 定长，按顺序拼成打码内容。
/// </summary>
[ObservableObject]
public partial class CodeSegmentVM
{
	public partial string Name { get; set; } = "";

	/// <summary>下拉显示项（Fixed（固定值）/ Time（时间格式）/ Serial（产品批次号））。</summary>
	public partial string ModeDisplay { get; set; } = CodeSegmentModes.AllWithHint[0];

	/// <summary>Fixed=固定值；Time=时间格式（如 HHmmss / yy / dd）。</summary>
	public partial string Value { get; set; } = "";

	/// <summary>定长（不足左补 0，超长保留低位）。</summary>
	public partial string Length { get; set; } = "1";

	/// <summary>取值方式下拉项。</summary>
	public string[] ModeOptions { get; } = CodeSegmentModes.AllWithHint;

	/// <summary>任一项变更时通知宿主刷新"总长/示例"。</summary>
	public event Action? Changed;

	partial void OnNameChanged(string value) => Changed?.Invoke();
	partial void OnModeDisplayChanged(string value) => Changed?.Invoke();
	partial void OnValueChanged(string value) => Changed?.Invoke();
	partial void OnLengthChanged(string value) => Changed?.Invoke();

	/// <summary>转成配置模型（长度非法按 0 处理 = 该段跳过）。</summary>
	public CodeSegment ToModel() => new()
	{
		Name = Name?.Trim() ?? "",
		Mode = CodeSegmentModes.Parse(ModeDisplay),
		Value = Value ?? "",
		Length = int.TryParse(Length?.Trim(), out var n) && n > 0 ? n : 0,
	};

	public static CodeSegmentVM From(CodeSegment seg) => new()
	{
		Name = seg.Name,
		ModeDisplay = CodeSegmentModes.ToDisplay(seg.Mode),
		Value = seg.Value,
		Length = seg.Length.ToString(),
	};
}
