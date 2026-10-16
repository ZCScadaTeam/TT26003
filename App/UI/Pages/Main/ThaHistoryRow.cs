using AppZC.Models.DB;
using AppZC.Services;
using ZC.Mvvm;

namespace AppZC.UI.Pages;

/// <summary>
/// THA26003 产品记录行（主页记录表 / 统计页明细表 / 数据追溯表 共用）。
/// 上/下针高各 15 点；上/下位置度各 15 点（PIN16 为协议预留，不采集）。
/// **未采集的字段一律显示 `--`**（源数据为 NULL，不伪装成 0.000）。
/// </summary>
[ObservableObject]
public partial class ThaHistoryRow
{
	/// <summary>上传时间。</summary>
	public partial string Time { get; set; } = "--";

	/// <summary>产品码。</summary>
	public partial string ProductCode { get; set; } = "";

	/// <summary>打码结果。</summary>
	public partial string MarkResult { get; set; } = "--";

	/// <summary>读码结果。</summary>
	public partial string ReadResult { get; set; } = "--";

	/// <summary>总结果。</summary>
	public partial string Overall { get; set; } = "--";

	/// <summary>导通耐压结果。</summary>
	public partial string Withstand { get; set; } = "--";

	/// <summary>针高结果。</summary>
	public partial string PinHeight { get; set; } = "--";

	/// <summary>位置度结果。</summary>
	public partial string Position { get; set; } = "--";

	/// <summary>是否复检。</summary>
	public partial string IsRetest { get; set; } = "--";

	/// <summary>打码时间。</summary>
	public partial string MarkTime { get; set; } = "--";

	/// <summary>读码时间。</summary>
	public partial string ScanTime { get; set; } = "--";

	/// <summary>上针高 Pin1(mm)。</summary>
	public partial string Top1 { get; set; } = "--";

	/// <summary>上针高 Pin2(mm)。</summary>
	public partial string Top2 { get; set; } = "--";

	/// <summary>上针高 Pin3(mm)。</summary>
	public partial string Top3 { get; set; } = "--";

	/// <summary>上针高 Pin4(mm)。</summary>
	public partial string Top4 { get; set; } = "--";

	/// <summary>上针高 Pin5(mm)。</summary>
	public partial string Top5 { get; set; } = "--";

	/// <summary>上针高 Pin6(mm)。</summary>
	public partial string Top6 { get; set; } = "--";

	/// <summary>上针高 Pin7(mm)。</summary>
	public partial string Top7 { get; set; } = "--";

	/// <summary>上针高 Pin8(mm)。</summary>
	public partial string Top8 { get; set; } = "--";

	/// <summary>上针高 Pin9(mm)。</summary>
	public partial string Top9 { get; set; } = "--";

	/// <summary>上针高 Pin10(mm)。</summary>
	public partial string Top10 { get; set; } = "--";

	/// <summary>上针高 Pin11(mm)。</summary>
	public partial string Top11 { get; set; } = "--";

	/// <summary>上针高 Pin12(mm)。</summary>
	public partial string Top12 { get; set; } = "--";

	/// <summary>上针高 Pin13(mm)。</summary>
	public partial string Top13 { get; set; } = "--";

	/// <summary>上针高 Pin14(mm)。</summary>
	public partial string Top14 { get; set; } = "--";

	/// <summary>上针高 Pin15(mm)。</summary>
	public partial string Top15 { get; set; } = "--";

	/// <summary>下针高 Pin1(mm)。</summary>
	public partial string Bottom1 { get; set; } = "--";

	/// <summary>下针高 Pin2(mm)。</summary>
	public partial string Bottom2 { get; set; } = "--";

	/// <summary>下针高 Pin3(mm)。</summary>
	public partial string Bottom3 { get; set; } = "--";

	/// <summary>下针高 Pin4(mm)。</summary>
	public partial string Bottom4 { get; set; } = "--";

	/// <summary>下针高 Pin5(mm)。</summary>
	public partial string Bottom5 { get; set; } = "--";

	/// <summary>下针高 Pin6(mm)。</summary>
	public partial string Bottom6 { get; set; } = "--";

	/// <summary>下针高 Pin7(mm)。</summary>
	public partial string Bottom7 { get; set; } = "--";

	/// <summary>下针高 Pin8(mm)。</summary>
	public partial string Bottom8 { get; set; } = "--";

	/// <summary>下针高 Pin9(mm)。</summary>
	public partial string Bottom9 { get; set; } = "--";

	/// <summary>下针高 Pin10(mm)。</summary>
	public partial string Bottom10 { get; set; } = "--";

	/// <summary>下针高 Pin11(mm)。</summary>
	public partial string Bottom11 { get; set; } = "--";

	/// <summary>下针高 Pin12(mm)。</summary>
	public partial string Bottom12 { get; set; } = "--";

	/// <summary>下针高 Pin13(mm)。</summary>
	public partial string Bottom13 { get; set; } = "--";

	/// <summary>下针高 Pin14(mm)。</summary>
	public partial string Bottom14 { get; set; } = "--";

	/// <summary>下针高 Pin15(mm)。</summary>
	public partial string Bottom15 { get; set; } = "--";

	/// <summary>上位置度 PIN1_X。</summary>
	public partial string TopPosX1 { get; set; } = "--";

	/// <summary>上位置度 PIN2_X。</summary>
	public partial string TopPosX2 { get; set; } = "--";

	/// <summary>上位置度 PIN3_X。</summary>
	public partial string TopPosX3 { get; set; } = "--";

	/// <summary>上位置度 PIN4_X。</summary>
	public partial string TopPosX4 { get; set; } = "--";

	/// <summary>上位置度 PIN5_X。</summary>
	public partial string TopPosX5 { get; set; } = "--";

	/// <summary>上位置度 PIN6_X。</summary>
	public partial string TopPosX6 { get; set; } = "--";

	/// <summary>上位置度 PIN7_X。</summary>
	public partial string TopPosX7 { get; set; } = "--";

	/// <summary>上位置度 PIN8_X。</summary>
	public partial string TopPosX8 { get; set; } = "--";

	/// <summary>上位置度 PIN9_X。</summary>
	public partial string TopPosX9 { get; set; } = "--";

	/// <summary>上位置度 PIN10_X。</summary>
	public partial string TopPosX10 { get; set; } = "--";

	/// <summary>上位置度 PIN11_X。</summary>
	public partial string TopPosX11 { get; set; } = "--";

	/// <summary>上位置度 PIN12_X。</summary>
	public partial string TopPosX12 { get; set; } = "--";

	/// <summary>上位置度 PIN13_X。</summary>
	public partial string TopPosX13 { get; set; } = "--";

	/// <summary>上位置度 PIN14_X。</summary>
	public partial string TopPosX14 { get; set; } = "--";

	/// <summary>上位置度 PIN15_X。</summary>
	public partial string TopPosX15 { get; set; } = "--";

	/// <summary>上位置度 PIN1_Y。</summary>
	public partial string TopPosY1 { get; set; } = "--";

	/// <summary>上位置度 PIN2_Y。</summary>
	public partial string TopPosY2 { get; set; } = "--";

	/// <summary>上位置度 PIN3_Y。</summary>
	public partial string TopPosY3 { get; set; } = "--";

	/// <summary>上位置度 PIN4_Y。</summary>
	public partial string TopPosY4 { get; set; } = "--";

	/// <summary>上位置度 PIN5_Y。</summary>
	public partial string TopPosY5 { get; set; } = "--";

	/// <summary>上位置度 PIN6_Y。</summary>
	public partial string TopPosY6 { get; set; } = "--";

	/// <summary>上位置度 PIN7_Y。</summary>
	public partial string TopPosY7 { get; set; } = "--";

	/// <summary>上位置度 PIN8_Y。</summary>
	public partial string TopPosY8 { get; set; } = "--";

	/// <summary>上位置度 PIN9_Y。</summary>
	public partial string TopPosY9 { get; set; } = "--";

	/// <summary>上位置度 PIN10_Y。</summary>
	public partial string TopPosY10 { get; set; } = "--";

	/// <summary>上位置度 PIN11_Y。</summary>
	public partial string TopPosY11 { get; set; } = "--";

	/// <summary>上位置度 PIN12_Y。</summary>
	public partial string TopPosY12 { get; set; } = "--";

	/// <summary>上位置度 PIN13_Y。</summary>
	public partial string TopPosY13 { get; set; } = "--";

	/// <summary>上位置度 PIN14_Y。</summary>
	public partial string TopPosY14 { get; set; } = "--";

	/// <summary>上位置度 PIN15_Y。</summary>
	public partial string TopPosY15 { get; set; } = "--";

	/// <summary>下位置度 PIN1_X。</summary>
	public partial string BottomPosX1 { get; set; } = "--";

	/// <summary>下位置度 PIN2_X。</summary>
	public partial string BottomPosX2 { get; set; } = "--";

	/// <summary>下位置度 PIN3_X。</summary>
	public partial string BottomPosX3 { get; set; } = "--";

	/// <summary>下位置度 PIN4_X。</summary>
	public partial string BottomPosX4 { get; set; } = "--";

	/// <summary>下位置度 PIN5_X。</summary>
	public partial string BottomPosX5 { get; set; } = "--";

	/// <summary>下位置度 PIN6_X。</summary>
	public partial string BottomPosX6 { get; set; } = "--";

	/// <summary>下位置度 PIN7_X。</summary>
	public partial string BottomPosX7 { get; set; } = "--";

	/// <summary>下位置度 PIN8_X。</summary>
	public partial string BottomPosX8 { get; set; } = "--";

	/// <summary>下位置度 PIN9_X。</summary>
	public partial string BottomPosX9 { get; set; } = "--";

	/// <summary>下位置度 PIN10_X。</summary>
	public partial string BottomPosX10 { get; set; } = "--";

	/// <summary>下位置度 PIN11_X。</summary>
	public partial string BottomPosX11 { get; set; } = "--";

	/// <summary>下位置度 PIN12_X。</summary>
	public partial string BottomPosX12 { get; set; } = "--";

	/// <summary>下位置度 PIN13_X。</summary>
	public partial string BottomPosX13 { get; set; } = "--";

	/// <summary>下位置度 PIN14_X。</summary>
	public partial string BottomPosX14 { get; set; } = "--";

	/// <summary>下位置度 PIN15_X。</summary>
	public partial string BottomPosX15 { get; set; } = "--";

	/// <summary>下位置度 PIN1_Y。</summary>
	public partial string BottomPosY1 { get; set; } = "--";

	/// <summary>下位置度 PIN2_Y。</summary>
	public partial string BottomPosY2 { get; set; } = "--";

	/// <summary>下位置度 PIN3_Y。</summary>
	public partial string BottomPosY3 { get; set; } = "--";

	/// <summary>下位置度 PIN4_Y。</summary>
	public partial string BottomPosY4 { get; set; } = "--";

	/// <summary>下位置度 PIN5_Y。</summary>
	public partial string BottomPosY5 { get; set; } = "--";

	/// <summary>下位置度 PIN6_Y。</summary>
	public partial string BottomPosY6 { get; set; } = "--";

	/// <summary>下位置度 PIN7_Y。</summary>
	public partial string BottomPosY7 { get; set; } = "--";

	/// <summary>下位置度 PIN8_Y。</summary>
	public partial string BottomPosY8 { get; set; } = "--";

	/// <summary>下位置度 PIN9_Y。</summary>
	public partial string BottomPosY9 { get; set; } = "--";

	/// <summary>下位置度 PIN10_Y。</summary>
	public partial string BottomPosY10 { get; set; } = "--";

	/// <summary>下位置度 PIN11_Y。</summary>
	public partial string BottomPosY11 { get; set; } = "--";

	/// <summary>下位置度 PIN12_Y。</summary>
	public partial string BottomPosY12 { get; set; } = "--";

	/// <summary>下位置度 PIN13_Y。</summary>
	public partial string BottomPosY13 { get; set; } = "--";

	/// <summary>下位置度 PIN14_Y。</summary>
	public partial string BottomPosY14 { get; set; } = "--";

	/// <summary>下位置度 PIN15_Y。</summary>
	public partial string BottomPosY15 { get; set; } = "--";

	/// <summary>总结果颜色（OK 绿 / NG 红 / 未判定灰）。</summary>
	public partial string OverallColor { get; set; } = "#9E9E9E";

	/// <summary>从数据库记录构建行。</summary>
	public static ThaHistoryRow From(ThaProductRecord r)
	{
		var row = new ThaHistoryRow
		{
			Time = r.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
			ProductCode = r.ProductCode ?? "",
			MarkResult = Tha26003.MarkResultText(r.MarkResult),
			ReadResult = Tha26003.ScanResultText(r.ReadResult),
			Overall = Tha26003.ResultText(r.OverallResult),
			Withstand = Tha26003.ResultText(r.WithstandResult),
			PinHeight = Tha26003.ResultText(r.PinHeightResult),
			Position = Tha26003.ResultText(r.PositionResult),
			IsRetest = r.IsRetest switch { 1 => "是", 2 => "否", _ => "--" },
			MarkTime = r.MarkTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "--",
			ScanTime = r.ScanTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "--",
			OverallColor = r.OverallResult switch { 1 => "#2E7D32", 2 => "#D32F2F", _ => "#9E9E9E" },
		};
		row.Top1 = F(r.TopPinHeight1);
		row.Top2 = F(r.TopPinHeight2);
		row.Top3 = F(r.TopPinHeight3);
		row.Top4 = F(r.TopPinHeight4);
		row.Top5 = F(r.TopPinHeight5);
		row.Top6 = F(r.TopPinHeight6);
		row.Top7 = F(r.TopPinHeight7);
		row.Top8 = F(r.TopPinHeight8);
		row.Top9 = F(r.TopPinHeight9);
		row.Top10 = F(r.TopPinHeight10);
		row.Top11 = F(r.TopPinHeight11);
		row.Top12 = F(r.TopPinHeight12);
		row.Top13 = F(r.TopPinHeight13);
		row.Top14 = F(r.TopPinHeight14);
		row.Top15 = F(r.TopPinHeight15);
		row.Bottom1 = F(r.BottomPinHeight1);
		row.Bottom2 = F(r.BottomPinHeight2);
		row.Bottom3 = F(r.BottomPinHeight3);
		row.Bottom4 = F(r.BottomPinHeight4);
		row.Bottom5 = F(r.BottomPinHeight5);
		row.Bottom6 = F(r.BottomPinHeight6);
		row.Bottom7 = F(r.BottomPinHeight7);
		row.Bottom8 = F(r.BottomPinHeight8);
		row.Bottom9 = F(r.BottomPinHeight9);
		row.Bottom10 = F(r.BottomPinHeight10);
		row.Bottom11 = F(r.BottomPinHeight11);
		row.Bottom12 = F(r.BottomPinHeight12);
		row.Bottom13 = F(r.BottomPinHeight13);
		row.Bottom14 = F(r.BottomPinHeight14);
		row.Bottom15 = F(r.BottomPinHeight15);
		row.TopPosX1 = F(r.TopPinPosX1);
		row.TopPosY1 = F(r.TopPinPosY1);
		row.TopPosX2 = F(r.TopPinPosX2);
		row.TopPosY2 = F(r.TopPinPosY2);
		row.TopPosX3 = F(r.TopPinPosX3);
		row.TopPosY3 = F(r.TopPinPosY3);
		row.TopPosX4 = F(r.TopPinPosX4);
		row.TopPosY4 = F(r.TopPinPosY4);
		row.TopPosX5 = F(r.TopPinPosX5);
		row.TopPosY5 = F(r.TopPinPosY5);
		row.TopPosX6 = F(r.TopPinPosX6);
		row.TopPosY6 = F(r.TopPinPosY6);
		row.TopPosX7 = F(r.TopPinPosX7);
		row.TopPosY7 = F(r.TopPinPosY7);
		row.TopPosX8 = F(r.TopPinPosX8);
		row.TopPosY8 = F(r.TopPinPosY8);
		row.TopPosX9 = F(r.TopPinPosX9);
		row.TopPosY9 = F(r.TopPinPosY9);
		row.TopPosX10 = F(r.TopPinPosX10);
		row.TopPosY10 = F(r.TopPinPosY10);
		row.TopPosX11 = F(r.TopPinPosX11);
		row.TopPosY11 = F(r.TopPinPosY11);
		row.TopPosX12 = F(r.TopPinPosX12);
		row.TopPosY12 = F(r.TopPinPosY12);
		row.TopPosX13 = F(r.TopPinPosX13);
		row.TopPosY13 = F(r.TopPinPosY13);
		row.TopPosX14 = F(r.TopPinPosX14);
		row.TopPosY14 = F(r.TopPinPosY14);
		row.TopPosX15 = F(r.TopPinPosX15);
		row.TopPosY15 = F(r.TopPinPosY15);
		row.BottomPosX1 = F(r.BottomPinPosX1);
		row.BottomPosY1 = F(r.BottomPinPosY1);
		row.BottomPosX2 = F(r.BottomPinPosX2);
		row.BottomPosY2 = F(r.BottomPinPosY2);
		row.BottomPosX3 = F(r.BottomPinPosX3);
		row.BottomPosY3 = F(r.BottomPinPosY3);
		row.BottomPosX4 = F(r.BottomPinPosX4);
		row.BottomPosY4 = F(r.BottomPinPosY4);
		row.BottomPosX5 = F(r.BottomPinPosX5);
		row.BottomPosY5 = F(r.BottomPinPosY5);
		row.BottomPosX6 = F(r.BottomPinPosX6);
		row.BottomPosY6 = F(r.BottomPinPosY6);
		row.BottomPosX7 = F(r.BottomPinPosX7);
		row.BottomPosY7 = F(r.BottomPinPosY7);
		row.BottomPosX8 = F(r.BottomPinPosX8);
		row.BottomPosY8 = F(r.BottomPinPosY8);
		row.BottomPosX9 = F(r.BottomPinPosX9);
		row.BottomPosY9 = F(r.BottomPinPosY9);
		row.BottomPosX10 = F(r.BottomPinPosX10);
		row.BottomPosY10 = F(r.BottomPinPosY10);
		row.BottomPosX11 = F(r.BottomPinPosX11);
		row.BottomPosY11 = F(r.BottomPinPosY11);
		row.BottomPosX12 = F(r.BottomPinPosX12);
		row.BottomPosY12 = F(r.BottomPinPosY12);
		row.BottomPosX13 = F(r.BottomPinPosX13);
		row.BottomPosY13 = F(r.BottomPinPosY13);
		row.BottomPosX14 = F(r.BottomPinPosX14);
		row.BottomPosY14 = F(r.BottomPinPosY14);
		row.BottomPosX15 = F(r.BottomPinPosX15);
		row.BottomPosY15 = F(r.BottomPinPosY15);
		return row;
	}

	/// <summary>数值显示：null（未采集）一律 `--`，不伪装成 0.000。</summary>
	private static string F(double? v) => v?.ToString("F3") ?? "--";
}
