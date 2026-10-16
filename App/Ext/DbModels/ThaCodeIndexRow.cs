using SqlSugar;

namespace AppZC.Models.DB;

/// <summary>
/// THA26003 码索引表：全量重码比对的唯一权威。
/// Code 为主键 → 数据库层面硬约束，任何并发/竞态下都不可能存在两条同码。
/// 状态机：0=已打码 → 1=已读码 → 2=已上传；3=NG 冻结（判 NG 报废的码不得复用）。
/// 读码站重码判定必须带状态，否则复检产品的自身码会被误判为重码（详见方案 §4.2）。
/// </summary>
[SugarTable("ThaCodeIndex", TableDescription = "THA26003 码索引（重码比对权威表）")]
[SugarIndex("idx_code_status", nameof(Status), OrderByType.Asc, nameof(MarkTime), OrderByType.Asc, false)]
public class ThaCodeIndexRow
{
	/// <summary>码本身（纯时间码 12 位），主键 = 唯一硬约束。</summary>
	[SugarColumn(IsPrimaryKey = true, Length = 32, ColumnDescription = "码")]
	public string Code { get; set; } = "";

	/// <summary>状态：0=已打码 1=已读码 2=已上传 3=NG冻结。</summary>
	[SugarColumn(ColumnDescription = "状态 0=已打码 1=已读码 2=已上传 3=NG冻结")]
	public int Status { get; set; }

	/// <summary>关联的产品记录 Id（上传落库后回填）。</summary>
	[SugarColumn(ColumnDescription = "关联产品记录Id", IsNullable = true)]
	public long? ProductId { get; set; }

	/// <summary>打标内容（= 码，冗余留档，便于排查"码与内容不一致"）。</summary>
	[SugarColumn(ColumnDescription = "打标内容", Length = 64, IsNullable = true)]
	public string? Content { get; set; }

	/// <summary>打码时间。</summary>
	[SugarColumn(ColumnDescription = "打码时间")]
	public DateTime MarkTime { get; set; }

	/// <summary>读码时间。</summary>
	[SugarColumn(ColumnDescription = "读码时间", IsNullable = true)]
	public DateTime? ScanTime { get; set; }

	/// <summary>上传时间。</summary>
	[SugarColumn(ColumnDescription = "上传时间", IsNullable = true)]
	public DateTime? UploadTime { get; set; }

	/// <summary>复检次数。</summary>
	[SugarColumn(ColumnDescription = "复检次数")]
	public int RetestCount { get; set; }
}
