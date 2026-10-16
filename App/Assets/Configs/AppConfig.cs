using AppZC.Services;
using ZC.CFG;
using ZC.DB;

namespace AppZC.Configs;

public class AppConfig : ConfigBase
{
	/// <summary>
	/// PLC IP 地址
	/// </summary>
	public string PlcIpAddress { get; set; } = "127.0.0.1";

	/// <summary>
	/// PLC 端口
	/// </summary>
	public int PlcPort { get; set; } = 502; // Modbus-TCP 标准端口（欧姆龙 NX Modbus 从站）

	/// <summary>
	/// PLC 厂家/协议（ModbusTcp=标准Modbus-TCP, OmronFins=欧姆龙FINS/TCP, OmronCip=欧姆龙EtherNet/IP, InovanceTcp=汇川Modbus-TCP）
	/// </summary>
	public string PlcBrand { get; set; } = "ModbusTcp";

	/// <summary>
	/// 本地IP地址（MES 本机地址）
	/// </summary>
	public string LocalIpAddress { get; set; } = "192.168.1.84";

	public DatabaseConnectionConfig Database { get; set; } = null!;
	public TaskServiceHostOptions TaskServiceHostOptions { get; set; } = null!;
	public string LogFilePath { get; set; } = @"D:\Logs\DXX";

	/// <summary>
	/// 数据库最大连接池大小（默认 20，防止多连接池叠加占满 PostgreSQL max_connections）
	/// </summary>
	public int DbMaxPoolSize { get; set; } = 20;

	/// <summary>
	/// 数据库最小连接池大小（默认 1）
	/// </summary>
	public int DbMinPoolSize { get; set; } = 1;

	/// <summary>
	/// 数据库单条命令超时秒数（默认 30 秒，防止长查询挂死连接）
	/// </summary>
	public int DbCommandTimeout { get; set; } = 30;

	/// <summary>
	/// 数据库连接超时秒数（默认 10 秒）
	/// </summary>
	public int DbConnectionTimeout { get; set; } = 10;

	/// <summary>
	/// 理论速度（件/小时）
	/// </summary>
	public int TheoreticalSpeed { get; set; } = 10;

	/// <summary>
	/// 重复产品是否仍然上传MES
	/// </summary>
	public bool UploadDuplicateToMes { get; set; } = false;

	/// <summary>
	/// MES TCP 服务器地址（检测系统与 MES 的 jcdata/qxdata 文本协议）
	/// </summary>
	public string MesHost { get; set; } = "127.0.0.1";

	/// <summary>
	/// MES TCP 监听端口
	/// </summary>
	public int MesPort { get; set; } = 5000;

	/// <summary>
	/// MES 客户端本地源端口（固定本机出口端口；默认 3000，0=不绑定随机端口）
	/// </summary>
	public int MesLocalPort { get; set; } = 3000;

	/// <summary>
	/// 站别模式：决定左侧「设备」导航加载哪些设备页（现场两个工位分别部署同一套软件）。
	/// All = 全部设备（开发/调试用）；
	/// WithstandVoltage = 耐压工位（仅 RJ6836H 安规测试仪 + MES 集成）；
	/// PostProcess = 后段工位（光栅位移传感器 + KGK 喷码机 + MES 集成）。
	/// </summary>
	public string StationMode { get; set; } = "All";

	/// <summary>
	/// ZX-EN-T6 控制器 TCP 端口（IP 固定 192.168.1.1~.10，控制器号即拨码；端口按现场约定 64000）
	/// </summary>
	public int SensorControllerPort { get; set; } = 64000;

	// ============================================================
	// THA26003 三站（打码站 / 读码站 / 上传站）配置
	// ============================================================

	// ----- 打码站（大族激光，TCP/IP）。报文内容与流程待定 → 全部配置化。-----

	/// <summary>打标机模式：Tcp=真机 / Mock=模拟器（默认 Mock，脱机即可跑通整链）。</summary>
	public string LaserMode { get; set; } = "Mock";

	/// <summary>打标机 IP。</summary>
	public string LaserIp { get; set; } = "192.168.250.100";

	/// <summary>打标机端口。</summary>
	public int LaserPort { get; set; } = 1024;

	/// <summary>打标指令模板，`{content}` 占位。协议确定后填入，如 "MARK,{content}\r"。留空=协议未落地（判 MES 异常，不假装成功）。</summary>
	public string LaserCommandTemplate { get; set; } = "";

	/// <summary>回执成功关键字（留空=不判关键字，只看是否有回执）。</summary>
	public string LaserOkKeyword { get; set; } = "OK";

	/// <summary>回执终止符（留空=读到任意一段即返回）。</summary>
	public string LaserReplyTerminator { get; set; } = "";

	/// <summary>从回执中提取打标内容的正则（第 1 个捕获组为内容）。留空=不做闭环内容核对（降级为只判回执成功）。</summary>
	public string LaserEchoRegex { get; set; } = "";

	/// <summary>打标机通信编码。</summary>
	public string LaserEncoding { get; set; } = "ASCII";

	/// <summary>等回执超时（毫秒）。</summary>
	public int LaserTimeoutMs { get; set; } = 8000;

	/// <summary>连接打标机超时（毫秒）。</summary>
	public int LaserConnectTimeoutMs { get; set; } = 3000;

	/// <summary>是否保持长连接。</summary>
	public bool LaserKeepAlive { get; set; } = true;

	/// <summary>Mock 打标机模拟耗时（毫秒）。</summary>
	public int LaserMockDelayMs { get; set; } = 200;

	/// <summary>打标内容来源：Auto=纯时间码自生成 / Plc=用 %MW97540 PLC 附带内容（非空时）。</summary>
	public string MarkContentSource { get; set; } = "Auto";

	// ----- 码规则（纯时间码）-----

	/// <summary>打标内容格式（= 码格式）。默认 12 位纯时间码；节拍 &lt;1s 且客户同意时可切 "yyMMddHHmmssfff"。</summary>
	public string CodeDateFormat { get; set; } = "yyMMddHHmmss";

	/// <summary>码期望长度（读码站长度校验）。</summary>
	public int CodeLength { get; set; } = 12;

	/// <summary>读码站时间窗校验（分钟）：码内时间与当前时间差值超过此值判错码。纯时间码下替代"固定字段比对"。</summary>
	public int CodeTimeWindowMinutes { get; set; } = 10;

	/// <summary>重码比对范围：All=全量 / Today=当天 / Shift=当前班次（默认全量）。</summary>
	public string DuplicateCheckScope { get; set; } = "All";

	/// <summary>在制超时（小时）：已打码/已读码但超时未上传的码，同码再读判重码（防僵尸码被复用）。0=不判。</summary>
	public int WipTimeoutHours { get; set; } = 2;

	/// <summary>是否放行未登记码（未经打码站的码）。</summary>
	public bool AllowUnregisteredCode { get; set; } = false;

	/// <summary>生成唯一码的最大等待毫秒（同秒冲突时等下一个整秒）。</summary>
	public int CodeMaxWaitMs { get; set; } = 3000;

	// ----- 读码站（基恩士 SR-1000，TCP/IP）。指令与数据格式待定 → 全部配置化。-----

	/// <summary>扫码枪模式：Trigger=软触发（MES 发指令收数据）/ Push=硬触发（枪推数据）/ Mock=模拟器。</summary>
	public string ScannerMode { get; set; } = "Mock";

	/// <summary>扫码枪 IP。</summary>
	public string ScannerIp { get; set; } = "192.168.250.101";

	/// <summary>扫码枪端口。</summary>
	public int ScannerPort { get; set; } = 9004;

	/// <summary>触发指令（协议确定后填入，如 "LON"）。留空=协议未落地。</summary>
	public string ScannerTriggerCmd { get; set; } = "";

	/// <summary>停止指令（触发后收尾，可空）。</summary>
	public string ScannerStopCmd { get; set; } = "";

	/// <summary>指令结束符。</summary>
	public string ScannerCommandEnd { get; set; } = "\r";

	/// <summary>数据终止符。</summary>
	public string ScannerTerminator { get; set; } = "\r";

	/// <summary>数据提取正则（第 1 个捕获组为码）。留空=按终止符切分。</summary>
	public string ScannerDataRegex { get; set; } = "";

	/// <summary>扫码枪通信编码。</summary>
	public string ScannerEncoding { get; set; } = "ASCII";

	/// <summary>等读码数据超时（毫秒）。</summary>
	public int ScannerTimeoutMs { get; set; } = 5000;

	/// <summary>连接扫码枪超时（毫秒）。</summary>
	public int ScannerConnectTimeoutMs { get; set; } = 3000;

	/// <summary>是否保持长连接。</summary>
	public bool ScannerKeepAlive { get; set; } = true;

	/// <summary>Mock 扫码枪模拟耗时（毫秒）。</summary>
	public int ScannerMockDelayMs { get; set; } = 200;

	// ----- 地址映射（%MW 编程地址 → Modbus 寄存器号）-----

	/// <summary>
	/// Modbus 地址映射规则（现场保险机制，默认为空 = 直传）。
	/// 协议 Rev.2 已把全部地址压到 %MW10560 以内（均 ≤65535），可直接作 Modbus 寄存器号；
	/// 若现场 PLC 的 Modbus 映射表另有偏移，在此加规则即可，代码不用改。
	/// </summary>
	public List<ModbusAddressRule> ModbusAddressRules { get; set; } = new();

	/// <summary>
	/// 设备地址前缀。默认 "MW"：汇川 InovanceTcpNet 只接受软元件名（实测 M10500 可用，
	/// D/R 与纯数字地址均解析失败），且 M 软元件号 == Modbus 寄存器号（1:1）。
	/// 若改用 ModbusTcpNet 直连，把本项设为空串即输出纯数字地址。
	/// </summary>
	public string PlcAddressPrefix { get; set; } = "MW";

	/// <summary>
	/// 汇川 PLC 系列（InovanceTcpNet.Series）：AM / H3U / H5U / Easy / EVO。
	/// 现场确认 PLC 型号后填对应值；默认 AM。
	/// </summary>
	public string InovanceSeries { get; set; } = "AM";

	// ----- 数值口径 -----

	/// <summary>针高口径：PLC 侧为 DINT（32 位整数），÷1000 显示。Int32Div1000（高字在前，默认）/ Int32SwappedDiv1000。</summary>
	public string PinHeightFormat { get; set; } = "Int32Div1000";

	/// <summary>位置度口径：PLC 侧为 REAL（32 位浮点）。FloatCdab（默认）/ FloatAbcd。</summary>
	public string PositionFormat { get; set; } = "FloatCdab";

	/// <summary>
	/// KGK 喷码机1 IP 地址（喷码工位1，历史字段保留）
	/// </summary>
	public string Mark1IpAddress { get; set; } = "192.168.250.223";

	/// <summary>
	/// KGK 喷码机2 IP 地址（喷码工位2）
	/// </summary>
	public string Mark2IpAddress { get; set; } = "192.168.250.224";

	/// <summary>
	/// KGK 喷码机 TCP 端口（两台相同，现场约定 1024）
	/// </summary>
	public int MarkPort { get; set; } = 1024;

	/// <summary>
	/// 安全门（机器门）PLC 信号地址：M 区位（如 "M100.0"，按位读）或 D 区字（如 "D3100"，0/1）。
	/// 空字符串/未配置 = 门联锁功能不启用（不读门信号、不报警、不禁止测试）。
	/// 门开电平 = 1（门开时信号为 1；常开触点）。
	/// </summary>
	public string? SafetyDoorAddress { get; set; }

	/// <summary>
	/// 传感器组内离群过滤开关：开启后与组中值偏差超过 <see cref="SensorOutlierMaxDeviationMm"/>mm 的传感器丢弃后再参与计算
	/// </summary>
	public bool EnableSensorOutlierFilter { get; set; } = true;

	/// <summary>传感器组内离群过滤最大偏差（mm），默认 2.0；与组中值偏差超过该值判离群丢弃。</summary>
	public double SensorOutlierMaxDeviationMm { get; set; } = 2.0;

	/// <summary>耐压电流异常判定阈值（mA）：耐压AC/直耐DC 实测电流 ≤ 此值判测量异常（表格"完成·异常"、PLC 回位 3、落库 NG）。默认 0.02；2026-08-18 改为配置页可设置。</summary>
	public double WithstandCurrentAbnormalMa { get; set; } = 0.02;

	/// <summary>各机位电流异常判定阈值（mA），索引 1-8 对应机位 1A/1B/2A/2B/3A/3B/4A/4B（索引 0 不用）。
	/// 值 &gt; 0 时该机位用此值；值 ≤ 0（未设置/旧配置）时回退用全局 <see cref="WithstandCurrentAbnormalMa"/>。2026-08-18 新增。</summary>
	public double[] WithstandCurrentAbnormalMaBySlot { get; set; } = new double[9];

	/// <summary>
	/// 耐压工位「测试模式加后缀」开关（仅测试用）：勾选后，入库(ProductTestRecords)时自动给产品码追加递增后缀 (#001/#002…)，
	/// 使同一真实产品码在统计页能区分出多条记录，便于测试回溯。不影响 PLC 回写与主页表格显示的原生产品码。
	/// 默认 false（生产环境保持覆盖语义，不追加）。
	/// </summary>
	public bool WithstandTestSuffixEnabled { get; set; } = false;

	/// <summary>
	/// 软件授权到期日（yyyy-MM-dd，含当天仍可用；次日 0 点起算到期）。
	/// 通过 LicenseService 激活 ZC0001/ZC0000 后回写。默认 2026-10-15。
	/// </summary>
	public DateTime LicenseExpiry { get; set; } = new(2026, 10, 15);
}
