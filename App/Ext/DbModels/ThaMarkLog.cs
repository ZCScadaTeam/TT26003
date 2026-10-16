using SqlSugar;

namespace AppZC.Models.DB;

/// <summary>
/// THA26003 打码过程流水（每次打码一行，含大族回执原文）。
/// 产品表只存最终结论，NG/超时/回执内容不符的现场证据全在这张表。
/// Content 与 EchoContent 分列存放：前者是 MES 下发的内容，后者是从大族回执里提取到的内容，
/// 两者不一致即"打错内容"（模板变量未更新 / 打标机缓存了上一件内容 / 切错配方）。
/// </summary>
[SugarTable("ThaMarkLog", TableDescription = "THA26003 打码过程流水")]
[SugarIndex("idx_mark_time", nameof(RequestTime), OrderByType.Desc, false)]
public class ThaMarkLog
{
	/// <summary>雪花主键。</summary>
	[SugarColumn(IsPrimaryKey = true)]
	public long Id { get; set; }

	/// <summary>MES 下发的打标内容（12 位纯时间码）。</summary>
	[SugarColumn(ColumnDescription = "下发内容", Length = 64, IsNullable = true)]
	public string? Content { get; set; }

	/// <summary>从大族回执中提取到的内容（提取失败时为空）。</summary>
	[SugarColumn(ColumnDescription = "回执内容", Length = 64, IsNullable = true)]
	public string? EchoContent { get; set; }

	/// <summary>大族回执原文（完整留档）。</summary>
	[SugarColumn(ColumnDescription = "回执原文", Length = 512, IsNullable = true)]
	public string? RawReply { get; set; }

	/// <summary>失败/异常描述（成功时为空）。</summary>
	[SugarColumn(ColumnDescription = "说明", Length = 256, IsNullable = true)]
	public string? Message { get; set; }

	/// <summary>结果码：见 <see cref="Services.MarkResultCode"/>。</summary>
	[SugarColumn(ColumnDescription = "结果码 1=OK 2=NG 3=超时 4=生成失败 5=MES异常 6=回执不符")]
	public int Result { get; set; }

	/// <summary>耗时（毫秒）。</summary>
	[SugarColumn(ColumnDescription = "耗时ms")]
	public int DurationMs { get; set; }

	/// <summary>打码设备模式（Tcp / Mock）。</summary>
	[SugarColumn(ColumnDescription = "设备模式", Length = 16, IsNullable = true)]
	public string? LaserMode { get; set; }

	/// <summary>请求时刻。</summary>
	[SugarColumn(ColumnDescription = "请求时刻")]
	public DateTime RequestTime { get; set; }

	/// <summary>回执时刻。</summary>
	[SugarColumn(ColumnDescription = "回执时刻", IsNullable = true)]
	public DateTime? ResponseTime { get; set; }
}
