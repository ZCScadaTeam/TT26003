using SqlSugar;

namespace AppZC.Models.DB;

/// <summary>
/// THA26003 产品检测记录（SQLite 本地库 ThaProductRecord 表）。
/// 一条产品一行：打码站生成码 → 读码站校验 → 上传站汇总落库（INSERT，绝不 UPSERT）。
///
/// **所有采集字段一律可空（nullable）**：PLC 没给该值 / 该段读失败时存 NULL，
/// 与"读到了 0"区分开。UI 一律显示 `--`，不把 null 伪装成 0.000。
///
/// 点数口径：上/下针高各 15 点；上/下位置度各 15 点（PIN16 为协议预留，不采集、不建列）。
/// 结果码：MarkResult 见 <see cref="Services.MarkResultCode"/>、ReadResult 见 <see cref="Services.ScanResultCode"/>、
/// UploadResult 见 <see cref="Services.UploadResultCode"/>；结果字 1=OK 2=NG。
/// </summary>
[SugarTable("ThaProductRecord", TableDescription = "THA26003 产品检测记录")]
[SugarIndex("idx_tha_create_time", nameof(CreateTime), OrderByType.Asc, false)]
[SugarIndex("idx_tha_product_code", nameof(ProductCode), OrderByType.Asc, false)]
public class ThaProductRecord
{
	/// <summary>雪花主键（SnowFlakeSingle.Instance.NextId()）。</summary>
	[SugarColumn(IsPrimaryKey = true)]
	public long Id { get; set; }

	/// <summary>产品码：打码站生成的 12 位纯时间码（yyMMddHHmmss）。未打码/打码失败时为 null。</summary>
	[SugarColumn(ColumnDescription = "产品码（12 位纯时间码）", Length = 32, IsNullable = true)]
	public string? ProductCode { get; set; }

	// ===== 三站结果（nullable：该站未执行/未采集时为 null）=====

	/// <summary>打码站结果码：1=OK 2=打标机NG 3=回执超时 4=码生成失败 5=MES异常 6=回执内容不符。</summary>
	[SugarColumn(ColumnDescription = "打码结果码", IsNullable = true)]
	public int? MarkResult { get; set; }

	/// <summary>读码站结果码：1=OK 2=未扫到 3=重码 4=错码 5=枪通讯异常 6=MES异常。</summary>
	[SugarColumn(ColumnDescription = "读码结果码", IsNullable = true)]
	public int? ReadResult { get; set; }

	/// <summary>上传结果码：1=落库 OK 2=数据校验 NG 3=落库失败/超时。</summary>
	[SugarColumn(ColumnDescription = "上传结果码", IsNullable = true)]
	public int? UploadResult { get; set; }

	// ===== PLC 检测结果字（nullable：未采集 / 读到非 1/2 的值时为 null）=====

	/// <summary>总结果（MES 自算）：其余结果无 NG 且齐全 → 1；有 NG → 2；数据不齐 → null。</summary>
	[SugarColumn(ColumnDescription = "总结果（MES自算） 1=OK 2=NG", IsNullable = true)]
	public int? OverallResult { get; set; }

	/// <summary>导通耐压结果：1=OK 2=NG。</summary>
	[SugarColumn(ColumnDescription = "导通耐压结果 1=OK 2=NG", IsNullable = true)]
	public int? WithstandResult { get; set; }

	/// <summary>针高结果：1=OK 2=NG。</summary>
	[SugarColumn(ColumnDescription = "针高结果 1=OK 2=NG", IsNullable = true)]
	public int? PinHeightResult { get; set; }

	/// <summary>位置度结果：1=OK 2=NG。</summary>
	[SugarColumn(ColumnDescription = "位置度结果 1=OK 2=NG", IsNullable = true)]
	public int? PositionResult { get; set; }

	// ===== 复检与追溯 =====

	/// <summary>
	/// 是否复检：1=是 2=否；**协议地址表该栏为「暂无」（PLC 不提供）→ 当前恒为 null**。
	/// 系统内部仍用码索引的重入次数驱动重码判定，但不写进本列；
	/// PLC 后续定义该地址后，把它接进上传站读取即可。
	/// </summary>
	[SugarColumn(ColumnDescription = "是否复检 1=是 2=否（PLC未提供→null）", IsNullable = true)]
	public int? IsRetest { get; set; }

	/// <summary>打码完成时间。</summary>
	[SugarColumn(ColumnDescription = "打码时间", IsNullable = true)]
	public DateTime? MarkTime { get; set; }

	/// <summary>读码时间。</summary>
	[SugarColumn(ColumnDescription = "读码时间", IsNullable = true)]
	public DateTime? ScanTime { get; set; }

	/// <summary>读码枪返回的原始串（未归一化，便于排查杂质字符）。</summary>
	[SugarColumn(ColumnDescription = "读码枪原始返回", Length = 128, IsNullable = true)]
	public string? ScanCodeRaw { get; set; }

	/// <summary>上传结果码：0=未上传 1=已上传 2=上传失败。</summary>
	[SugarColumn(ColumnDescription = "上传状态 0=未传 1=已传 2=失败")]
	public int UploadState { get; set; }

	/// <summary>上传时间。</summary>
	[SugarColumn(ColumnDescription = "上传时间", IsNullable = true)]
	public DateTime? UploadTime { get; set; }

	/// <summary>创建时间（= 上传落库时间）。</summary>
	[SugarColumn(ColumnDescription = "创建时间（上传时间）")]
	public DateTime CreateTime { get; set; }

	/// <summary>更新时间。</summary>
	[SugarColumn(ColumnDescription = "更新时间", IsNullable = true)]
	public DateTime? UpdateTime { get; set; }

	// ===== 上针高 Pin1~15（%MW10022~10051，2WORD 步进；未采集为 null）=====

	[SugarColumn(ColumnDescription = "上针高 Pin1", IsNullable = true)]  public double? TopPinHeight1  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin2", IsNullable = true)]  public double? TopPinHeight2  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin3", IsNullable = true)]  public double? TopPinHeight3  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin4", IsNullable = true)]  public double? TopPinHeight4  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin5", IsNullable = true)]  public double? TopPinHeight5  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin6", IsNullable = true)]  public double? TopPinHeight6  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin7", IsNullable = true)]  public double? TopPinHeight7  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin8", IsNullable = true)]  public double? TopPinHeight8  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin9", IsNullable = true)]  public double? TopPinHeight9  { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin10", IsNullable = true)] public double? TopPinHeight10 { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin11", IsNullable = true)] public double? TopPinHeight11 { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin12", IsNullable = true)] public double? TopPinHeight12 { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin13", IsNullable = true)] public double? TopPinHeight13 { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin14", IsNullable = true)] public double? TopPinHeight14 { get; set; }
	[SugarColumn(ColumnDescription = "上针高 Pin15", IsNullable = true)] public double? TopPinHeight15 { get; set; }

	// ===== 下针高 Pin1~15（%MW10122~10151）=====

	[SugarColumn(ColumnDescription = "下针高 Pin1", IsNullable = true)]  public double? BottomPinHeight1  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin2", IsNullable = true)]  public double? BottomPinHeight2  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin3", IsNullable = true)]  public double? BottomPinHeight3  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin4", IsNullable = true)]  public double? BottomPinHeight4  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin5", IsNullable = true)]  public double? BottomPinHeight5  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin6", IsNullable = true)]  public double? BottomPinHeight6  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin7", IsNullable = true)]  public double? BottomPinHeight7  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin8", IsNullable = true)]  public double? BottomPinHeight8  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin9", IsNullable = true)]  public double? BottomPinHeight9  { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin10", IsNullable = true)] public double? BottomPinHeight10 { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin11", IsNullable = true)] public double? BottomPinHeight11 { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin12", IsNullable = true)] public double? BottomPinHeight12 { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin13", IsNullable = true)] public double? BottomPinHeight13 { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin14", IsNullable = true)] public double? BottomPinHeight14 { get; set; }
	[SugarColumn(ColumnDescription = "下针高 Pin15", IsNullable = true)] public double? BottomPinHeight15 { get; set; }

	// ===== 上针位置度 PIN1~15 X/Y（%MW10220~10279）=====

	[SugarColumn(ColumnDescription = "上位置度 PIN1_X", IsNullable = true)]  public double? TopPinPosX1  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN1_Y", IsNullable = true)]  public double? TopPinPosY1  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN2_X", IsNullable = true)]  public double? TopPinPosX2  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN2_Y", IsNullable = true)]  public double? TopPinPosY2  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN3_X", IsNullable = true)]  public double? TopPinPosX3  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN3_Y", IsNullable = true)]  public double? TopPinPosY3  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN4_X", IsNullable = true)]  public double? TopPinPosX4  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN4_Y", IsNullable = true)]  public double? TopPinPosY4  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN5_X", IsNullable = true)]  public double? TopPinPosX5  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN5_Y", IsNullable = true)]  public double? TopPinPosY5  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN6_X", IsNullable = true)]  public double? TopPinPosX6  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN6_Y", IsNullable = true)]  public double? TopPinPosY6  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN7_X", IsNullable = true)]  public double? TopPinPosX7  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN7_Y", IsNullable = true)]  public double? TopPinPosY7  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN8_X", IsNullable = true)]  public double? TopPinPosX8  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN8_Y", IsNullable = true)]  public double? TopPinPosY8  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN9_X", IsNullable = true)]  public double? TopPinPosX9  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN9_Y", IsNullable = true)]  public double? TopPinPosY9  { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN10_X", IsNullable = true)] public double? TopPinPosX10 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN10_Y", IsNullable = true)] public double? TopPinPosY10 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN11_X", IsNullable = true)] public double? TopPinPosX11 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN11_Y", IsNullable = true)] public double? TopPinPosY11 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN12_X", IsNullable = true)] public double? TopPinPosX12 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN12_Y", IsNullable = true)] public double? TopPinPosY12 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN13_X", IsNullable = true)] public double? TopPinPosX13 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN13_Y", IsNullable = true)] public double? TopPinPosY13 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN14_X", IsNullable = true)] public double? TopPinPosX14 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN14_Y", IsNullable = true)] public double? TopPinPosY14 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN15_X", IsNullable = true)] public double? TopPinPosX15 { get; set; }
	[SugarColumn(ColumnDescription = "上位置度 PIN15_Y", IsNullable = true)] public double? TopPinPosY15 { get; set; }

	// ===== 下针位置度 PIN1~15 X/Y（%MW10320~10379）=====

	[SugarColumn(ColumnDescription = "下位置度 PIN1_X", IsNullable = true)]  public double? BottomPinPosX1  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN1_Y", IsNullable = true)]  public double? BottomPinPosY1  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN2_X", IsNullable = true)]  public double? BottomPinPosX2  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN2_Y", IsNullable = true)]  public double? BottomPinPosY2  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN3_X", IsNullable = true)]  public double? BottomPinPosX3  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN3_Y", IsNullable = true)]  public double? BottomPinPosY3  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN4_X", IsNullable = true)]  public double? BottomPinPosX4  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN4_Y", IsNullable = true)]  public double? BottomPinPosY4  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN5_X", IsNullable = true)]  public double? BottomPinPosX5  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN5_Y", IsNullable = true)]  public double? BottomPinPosY5  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN6_X", IsNullable = true)]  public double? BottomPinPosX6  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN6_Y", IsNullable = true)]  public double? BottomPinPosY6  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN7_X", IsNullable = true)]  public double? BottomPinPosX7  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN7_Y", IsNullable = true)]  public double? BottomPinPosY7  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN8_X", IsNullable = true)]  public double? BottomPinPosX8  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN8_Y", IsNullable = true)]  public double? BottomPinPosY8  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN9_X", IsNullable = true)]  public double? BottomPinPosX9  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN9_Y", IsNullable = true)]  public double? BottomPinPosY9  { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN10_X", IsNullable = true)] public double? BottomPinPosX10 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN10_Y", IsNullable = true)] public double? BottomPinPosY10 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN11_X", IsNullable = true)] public double? BottomPinPosX11 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN11_Y", IsNullable = true)] public double? BottomPinPosY11 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN12_X", IsNullable = true)] public double? BottomPinPosX12 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN12_Y", IsNullable = true)] public double? BottomPinPosY12 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN13_X", IsNullable = true)] public double? BottomPinPosX13 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN13_Y", IsNullable = true)] public double? BottomPinPosY13 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN14_X", IsNullable = true)] public double? BottomPinPosX14 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN14_Y", IsNullable = true)] public double? BottomPinPosY14 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN15_X", IsNullable = true)] public double? BottomPinPosX15 { get; set; }
	[SugarColumn(ColumnDescription = "下位置度 PIN15_Y", IsNullable = true)] public double? BottomPinPosY15 { get; set; }
}
