using AppZC.Models.DB;

namespace AppZC.Services;

/// <summary>
/// 报表/导出统一行模型（列头、列宽由 MiniExcel 特性控制）。
///
/// 单一来源：统计页手工导出（DateVM）与每日自动报表（<see cref="ThaDailyReport"/>）共用本类型，
/// 避免两处各维护 100+ 列导致列顺序漂移。
/// 未采集值一律导出空/`--`，不把 null 伪装成 0.000。
/// </summary>
public sealed class ThaExportRow
{
	/// <summary>采集时间（= 记录落库时刻，报表第一列）。</summary>
	[MiniExcelLibs.Attributes.ExcelColumnName("采集时间")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(20)]
	public string 采集时间 { get; set; } = "";
	/// <summary>产品码（列宽自适应内容）。</summary>
	[MiniExcelLibs.Attributes.ExcelColumnName("产品码")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(20)]
	public string 产品码 { get; set; } = "";
	/// <summary>镭码内容（本件下发给打标机的打标原文，未打码为空）。</summary>
	[MiniExcelLibs.Attributes.ExcelColumnName("镭码内容")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(24)]
	public string 镭码内容 { get; set; } = "";
	/// <summary>码等级（从枪回包拆出，如 A；未采集 / 无等级为空）。</summary>
	[MiniExcelLibs.Attributes.ExcelColumnName("码等级")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(10)]
	public string 码等级 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("总结果")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(14)]
	public string 总结果 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("扫码结果")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(14)]
	public string 扫码结果 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("导通耐压结果")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(14)]
	public string 导通耐压结果 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("针高结果")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(14)]
	public string 针高结果 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("位置度结果")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(14)]
	public string 位置度结果 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("是否复检")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(14)]
	public string 是否复检 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高1")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高1 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高2")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高2 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高3")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高3 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高4")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高4 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高5")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高5 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高6")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高6 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高7")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高7 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高8")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高8 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高9")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高9 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高10")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高10 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高11")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高11 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高12")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高12 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高13")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高13 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高14")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高14 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上针高15")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上针高15 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高1")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高1 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高2")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高2 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高3")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高3 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高4")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高4 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高5")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高5 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高6")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高6 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高7")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高7 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高8")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高8 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高9")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高9 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高10")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高10 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高11")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高11 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高12")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高12 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高13")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高13 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高14")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高14 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下针高15")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下针高15 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X1")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X1 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y1")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y1 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X2")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X2 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y2")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y2 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X3")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X3 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y3")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y3 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X4")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X4 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y4")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y4 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X5")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X5 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y5")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y5 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X6")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X6 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y6")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y6 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X7")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X7 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y7")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y7 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X8")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X8 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y8")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y8 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X9")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X9 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y9")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y9 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X10")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X10 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y10")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y10 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X11")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X11 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y11")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y11 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X12")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X12 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y12")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y12 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X13")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X13 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y13")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y13 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X14")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X14 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y14")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y14 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度X15")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度X15 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("上位置度Y15")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 上位置度Y15 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X1")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X1 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y1")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y1 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X2")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X2 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y2")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y2 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X3")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X3 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y3")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y3 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X4")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X4 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y4")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y4 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X5")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X5 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y5")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y5 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X6")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X6 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y6")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y6 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X7")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X7 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y7")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y7 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X8")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X8 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y8")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y8 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X9")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X9 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y9")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y9 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X10")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X10 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y10")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y10 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X11")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X11 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y11")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y11 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X12")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X12 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y12")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y12 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X13")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X13 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y13")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y13 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X14")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X14 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y14")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y14 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度X15")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度X15 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("下位置度Y15")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(12)]
	public string 下位置度Y15 { get; set; } = "";
	[MiniExcelLibs.Attributes.ExcelColumnName("更新时间")]
	[MiniExcelLibs.Attributes.ExcelColumnWidth(20)]
	public string 更新时间 { get; set; } = "";

	/// <summary>产品记录 → 报表行（唯一映射入口）。</summary>
	public static ThaExportRow From(ThaProductRecord r) => new()
	{
		采集时间 = r.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
		产品码 = r.ProductCode ?? "",
		镭码内容 = r.MarkContent ?? "",
		码等级 = r.ScanCodeLevel ?? "",
		总结果 = Tha26003.ResultText(r.OverallResult),
		扫码结果 = Tha26003.ResultText(r.ScanResult),
		导通耐压结果 = Tha26003.ResultText(r.WithstandResult),
		针高结果 = Tha26003.ResultText(r.PinHeightResult),
		位置度结果 = Tha26003.ResultText(r.PositionResult),
		是否复检 = r.IsRetest switch { 1 => "是", 2 => "否", _ => "" },
		上针高1 = F(r.TopPinHeight1),
		上针高2 = F(r.TopPinHeight2),
		上针高3 = F(r.TopPinHeight3),
		上针高4 = F(r.TopPinHeight4),
		上针高5 = F(r.TopPinHeight5),
		上针高6 = F(r.TopPinHeight6),
		上针高7 = F(r.TopPinHeight7),
		上针高8 = F(r.TopPinHeight8),
		上针高9 = F(r.TopPinHeight9),
		上针高10 = F(r.TopPinHeight10),
		上针高11 = F(r.TopPinHeight11),
		上针高12 = F(r.TopPinHeight12),
		上针高13 = F(r.TopPinHeight13),
		上针高14 = F(r.TopPinHeight14),
		上针高15 = F(r.TopPinHeight15),
		下针高1 = F(r.BottomPinHeight1),
		下针高2 = F(r.BottomPinHeight2),
		下针高3 = F(r.BottomPinHeight3),
		下针高4 = F(r.BottomPinHeight4),
		下针高5 = F(r.BottomPinHeight5),
		下针高6 = F(r.BottomPinHeight6),
		下针高7 = F(r.BottomPinHeight7),
		下针高8 = F(r.BottomPinHeight8),
		下针高9 = F(r.BottomPinHeight9),
		下针高10 = F(r.BottomPinHeight10),
		下针高11 = F(r.BottomPinHeight11),
		下针高12 = F(r.BottomPinHeight12),
		下针高13 = F(r.BottomPinHeight13),
		下针高14 = F(r.BottomPinHeight14),
		下针高15 = F(r.BottomPinHeight15),
		上位置度X1 = F(r.TopPinPosX1),
		上位置度Y1 = F(r.TopPinPosY1),
		上位置度X2 = F(r.TopPinPosX2),
		上位置度Y2 = F(r.TopPinPosY2),
		上位置度X3 = F(r.TopPinPosX3),
		上位置度Y3 = F(r.TopPinPosY3),
		上位置度X4 = F(r.TopPinPosX4),
		上位置度Y4 = F(r.TopPinPosY4),
		上位置度X5 = F(r.TopPinPosX5),
		上位置度Y5 = F(r.TopPinPosY5),
		上位置度X6 = F(r.TopPinPosX6),
		上位置度Y6 = F(r.TopPinPosY6),
		上位置度X7 = F(r.TopPinPosX7),
		上位置度Y7 = F(r.TopPinPosY7),
		上位置度X8 = F(r.TopPinPosX8),
		上位置度Y8 = F(r.TopPinPosY8),
		上位置度X9 = F(r.TopPinPosX9),
		上位置度Y9 = F(r.TopPinPosY9),
		上位置度X10 = F(r.TopPinPosX10),
		上位置度Y10 = F(r.TopPinPosY10),
		上位置度X11 = F(r.TopPinPosX11),
		上位置度Y11 = F(r.TopPinPosY11),
		上位置度X12 = F(r.TopPinPosX12),
		上位置度Y12 = F(r.TopPinPosY12),
		上位置度X13 = F(r.TopPinPosX13),
		上位置度Y13 = F(r.TopPinPosY13),
		上位置度X14 = F(r.TopPinPosX14),
		上位置度Y14 = F(r.TopPinPosY14),
		上位置度X15 = F(r.TopPinPosX15),
		上位置度Y15 = F(r.TopPinPosY15),
		下位置度X1 = F(r.BottomPinPosX1),
		下位置度Y1 = F(r.BottomPinPosY1),
		下位置度X2 = F(r.BottomPinPosX2),
		下位置度Y2 = F(r.BottomPinPosY2),
		下位置度X3 = F(r.BottomPinPosX3),
		下位置度Y3 = F(r.BottomPinPosY3),
		下位置度X4 = F(r.BottomPinPosX4),
		下位置度Y4 = F(r.BottomPinPosY4),
		下位置度X5 = F(r.BottomPinPosX5),
		下位置度Y5 = F(r.BottomPinPosY5),
		下位置度X6 = F(r.BottomPinPosX6),
		下位置度Y6 = F(r.BottomPinPosY6),
		下位置度X7 = F(r.BottomPinPosX7),
		下位置度Y7 = F(r.BottomPinPosY7),
		下位置度X8 = F(r.BottomPinPosX8),
		下位置度Y8 = F(r.BottomPinPosY8),
		下位置度X9 = F(r.BottomPinPosX9),
		下位置度Y9 = F(r.BottomPinPosY9),
		下位置度X10 = F(r.BottomPinPosX10),
		下位置度Y10 = F(r.BottomPinPosY10),
		下位置度X11 = F(r.BottomPinPosX11),
		下位置度Y11 = F(r.BottomPinPosY11),
		下位置度X12 = F(r.BottomPinPosX12),
		下位置度Y12 = F(r.BottomPinPosY12),
		下位置度X13 = F(r.BottomPinPosX13),
		下位置度Y13 = F(r.BottomPinPosY13),
		下位置度X14 = F(r.BottomPinPosX14),
		下位置度Y14 = F(r.BottomPinPosY14),
		下位置度X15 = F(r.BottomPinPosX15),
		下位置度Y15 = F(r.BottomPinPosY15),
		更新时间 = r.UpdateTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
	};

	/// <summary>结果字显示：1=OK 2=NG 其余 --。</summary>
	public static string ResultText(int? v) => v switch { 1 => "OK", 2 => "NG", _ => "--" };

	/// <summary>数值显示：null（未采集）一律 --，不伪装成 0.000。</summary>
	public static string F(double? v) => v?.ToString("F3") ?? "--";
}
