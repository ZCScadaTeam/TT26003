using SqlSugar;

namespace AppZC.Models.DB;

/// <summary>
/// THA26003 读码过程流水（每次读码一行）。
/// 重码 / 错码 / 未扫到的现场证据：谁、什么时候、读到了什么、为什么判 NG。
/// CodeRaw 保留枪返回的原始串，CodeNormalized 是去掉头尾空白/终止符后的结果。
/// </summary>
[SugarTable("ThaScanLog", TableDescription = "THA26003 读码过程流水")]
[SugarIndex("idx_scan_time", nameof(CreateTime), OrderByType.Desc, false)]
[SugarIndex("idx_scan_code", nameof(CodeNormalized), OrderByType.Asc, false)]
public class ThaScanLog
{
	/// <summary>雪花主键。</summary>
	[SugarColumn(IsPrimaryKey = true)]
	public long Id { get; set; }

	/// <summary>读码枪原始返回（未处理，便于排查杂质字符）。</summary>
	[SugarColumn(ColumnDescription = "枪原始返回", Length = 128, IsNullable = true)]
	public string? CodeRaw { get; set; }

	/// <summary>归一化后的码（去空白/终止符）。</summary>
	[SugarColumn(ColumnDescription = "归一化码", Length = 64, IsNullable = true)]
	public string? CodeNormalized { get; set; }

	/// <summary>
	/// 码等级：从枪回包拆出的等级（<c>000143348000026070:A</c> → A）。
	/// 内容里没写等级 / 正则不匹配 → null。
	/// </summary>
	[SugarColumn(ColumnDescription = "码等级", Length = 8, IsNullable = true)]
	public string? CodeLevel { get; set; }

	/// <summary>PLC 附带前站镭码内容（%MW10580 读回，比对用；未启用比对/未读到为 null）。</summary>
	[SugarColumn(ColumnDescription = "PLC附带前站镭码", Length = 64, IsNullable = true)]
	public string? PlcCode { get; set; }

	/// <summary>结果码：见 <see cref="Services.ScanResultCode"/>。</summary>
	[SugarColumn(ColumnDescription = "结果码 1=OK 3=重码 4=错码 5=枪异常 6=MES异常 7=未扫到")]
	public int Result { get; set; }

	/// <summary>否定原因（OK 时为空），如 "长度不符(11)" / "重码(已上传)" / "时间超窗(前37分钟)"。</summary>
	[SugarColumn(ColumnDescription = "否定原因", Length = 256, IsNullable = true)]
	public string? FailReason { get; set; }

	/// <summary>耗时（毫秒）。</summary>
	[SugarColumn(ColumnDescription = "耗时ms")]
	public int DurationMs { get; set; }

	/// <summary>读码设备模式（TcpTrigger / TcpPush / Mock）。</summary>
	[SugarColumn(ColumnDescription = "设备模式", Length = 16, IsNullable = true)]
	public string? ScannerMode { get; set; }

	/// <summary>创建时间（读码时刻）。</summary>
	[SugarColumn(ColumnDescription = "创建时间")]
	public DateTime CreateTime { get; set; }
}
