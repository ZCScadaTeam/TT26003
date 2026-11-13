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

	/// <summary>打标指令模板：大族打标三步流程，留空的步骤自动跳过；三步全空 = 协议未落地（判 MES 异常）。</summary>
	public string LaserTemplateName { get; set; } = "11";

	/// <summary>① 初始化模板指令，<c>{template}</c> 为模板文件名占位（如 <c>$Initialize_11</c>）。</summary>
	public string LaserInitTemplate { get; set; } = "$Initialize_{template}";

	/// <summary>② 下发数据指令，<c>{content}</c> 为打标内容占位（如 <c>$Data_260902135901</c>）。</summary>
	public string LaserDataTemplate { get; set; } = "$Data_{content}";

	/// <summary>③ 启动打标指令，无占位（如 <c>$MarkStart_</c>）。</summary>
	public string LaserStartTemplate { get; set; } = "$MarkStart_";

	/// <summary>指令结束符（追加在每条指令末尾，需要 \r\n 就填），默认空。帧模式下它在 0x03 之前。</summary>
	public string LaserCommandEnd { get; set; } = "";

	/// <summary>
	/// 是否按大族标准帧收发：发送 0x02 + ASCII 指令 + 0x03，接收按 0x03 截断并剔掉头尾。
	/// 默认 true；现场设备不要求帧时才关。
	/// </summary>
	public bool LaserFrameEnabled { get; set; } = true;

	/// <summary>①② 两步等回执超时（毫秒），默认 800；③ 启动打标用 <see cref="LaserTimeoutMs"/> 等打标完成回执。</summary>
	public int LaserStepTimeoutMs { get; set; } = 800;

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


	// ----- 码规则（纯时间码）-----

	/// <summary>打标内容格式（= 码格式）。默认 12 位纯时间码；节拍 &lt;1s 且客户同意时可切 "yyMMddHHmmssfff"。</summary>
	public string CodeDateFormat { get; set; } = "yyMMddHHmmss";

	/// <summary>码期望长度（读码站长度校验）。配置了 <see cref="CodeSegments"/> 时以各段定长之和为准。</summary>
	public int CodeLength { get; set; } = 12;

	/// <summary>
	/// 产品码（打码内容）分段模板：按顺序拼接，默认按客户确认的触点元件 24 位结构预置。
	/// 每段 = 名称 + 取值方式（Fixed 固定值 / Time 时间格式 / Serial 产品批次号）+ 取值或格式 + 定长。
	/// **全部清空 = 回到纯时间码模式**（整码 = CodeDateFormat）。
	/// </summary>
	public List<CodeSegment> CodeSegments { get; set; } = DefaultCodeSegments();

	/// <summary>
	/// 默认码段（按客户邮件里的字段表定，合计 24 位）：
	/// 供应商编码1 / 生产日期天3 / 生产年份2 / 生产时间6 / 海拉零件号2 / 图纸版本2 / 产品批次号5 / 穴号2 / 原材料编号1。
	/// 示例：<c>229926120000020800001011</c>（第 299 天 / 26 年 / 12:00:00 / 批次 00001）。
	/// </summary>
	public static List<CodeSegment> DefaultCodeSegments() => new()
	{
		new() { Name = "供应商编码", Mode = "Fixed", Value = "2", Length = 1 },
		new() { Name = "生产日期/天", Mode = "Time", Value = "DDD", Length = 3 },
		new() { Name = "生产年份", Mode = "Time", Value = "yy", Length = 2 },
		new() { Name = "生产时间", Mode = "Time", Value = "HHmmss", Length = 6 },
		new() { Name = "海拉零件号", Mode = "Fixed", Value = "02", Length = 2 },
		new() { Name = "图纸版本", Mode = "Fixed", Value = "08", Length = 2 },
		new() { Name = "产品批次号", Mode = "Serial", Value = "", Length = 5 },
		new() { Name = "穴号", Mode = "Fixed", Value = "01", Length = 2 },
		new() { Name = "原材料编号", Mode = "Fixed", Value = "1", Length = 1 },
	};

	/// <summary>
	/// 生产日（批次日）切换时刻（点，0-23）。默认 8 = **每天早上 8 点产品批次号清零**（客户确认），
	/// 即 00:00-07:59 打码的产品仍算前一天、批次号继续累加，08:00 第一件重新从 00001 开始。
	/// 0 = 回到自然日零点清零。
	/// </summary>
	public int SerialResetHour { get; set; } = ThaProductionDay.DefaultResetHour;

	/// <summary>
	/// 日期段（yy / DDD / MMdd）是否按生产日记：true（默认）= 00:00-07:59 打码的产品日期记前一天，
	/// 同一批次号只对应一个日期；false = 日期段用墙钟实际日期（批次号仍按 SerialResetHour 清零）。
	/// </summary>
	public bool DateFollowProductionDay { get; set; } = true;

	/// <summary>读码站时间窗校验（分钟）：码内时间与当前时间差值超过此值判错码。纯时间码下替代"固定字段比对"。日期段按生产日记时整天的差会被折算，不按 24 小时判超窗。</summary>
	public int CodeTimeWindowMinutes { get; set; } = 10;

	/// <summary>重码比对范围：All=全量 / Today=当天 / Shift=当前班次（默认全量）。</summary>
	public string DuplicateCheckScope { get; set; } = "All";

	/// <summary>在制超时（小时）：已打码/已读码但超时未上传的码，同码再读判重码（防僵尸码被复用）。0=不判。</summary>
	public int WipTimeoutHours { get; set; } = 2;

	/// <summary>
	/// 读码站是否把枪码与 PLC 附带前站镭码（<see cref="PlcCodeAddress"/>）做一致性比对。
	/// true（默认）= 不一致直接判错码；false = 跳过比对，只做格式 + 库校验（现场 10580 未接线时的退路）。
	/// </summary>
	public bool PlcCodeCompareEnabled { get; set; } = true;

	/// <summary>PLC 附带前站镭码内容地址（默认 %MW10580，40 WORD = 80 字节 ASCII）。</summary>
	public int PlcCodeAddress { get; set; } = Tha26003.MwPlcCode;

	/// <summary>该区 WORD 数（协议表：40WORD，长度不够可增加）。</summary>
	public int PlcCodeWords { get; set; } = Tha26003.PlcCodeWords;

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

	/// <summary>
	/// 扫码内容拆解正则：命名分组 <c>code</c> = 产品码、<c>grade</c> = 码等级。
	/// 现场枪回包是合写形式（<c>000143348000026070:A</c>）→ 码 000143348000026070 / 等级 A。
	/// 留空=用内置默认 <c>^(?&lt;code&gt;[0-9]{4,32})(?:\s*[:：]\s*(?&lt;grade&gt;[A-Za-z0-9]{1,8}))?$</c>；
	/// 没有分隔符（老码）时整串为码、等级为空。
	/// 注意：若同时配了 <see cref="ScannerDataRegex"/>，要让它整体匹配「码+等级」，否则等级在设备层就被截掉了。
	/// </summary>
	public string ScannerCodePattern { get; set; } = "";

	/// <summary>
	/// 码等级合格阈值（默认 <c>B</c>）：等级按字母序 A&lt;B&lt;C&lt;D…，**不高于阈值即合格**
	/// （B = A / B 合格，C / D 及更低 → 扫码结果判 NG，原因写「码等级不符(C＞阈值B)」）。
	/// 留空 = 不做等级判定（只拆等级落库）；枪回包里没写等级时不判 NG。
	/// </summary>
	public string CodeGradeThreshold { get; set; } = ThaScanJudge.DefaultCodeGradeThreshold;

	/// <summary>
	/// 补读回填窗口（秒）。现场时序是「打码 → 采集落库 → 读码」（读码晚 ~1.5s），
	/// 落库时读码上下文为空，记录里的扫码结果/码等级是 null 或上一件残值。
	/// 读码读到本件码时，若该码的记录在这么多秒内落库、且记录里还没有本码扫码结果，
	/// 就把扫码结果 / 码等级 / 总结果回填到那条记录，本次读码按“补读”放行（不判重码）。
	/// **默认 60**；0 = 关闭补读回填（读码晚到一律判重码）。
	/// </summary>
	public int LateScanFillSeconds { get; set; } = 60;

	/// <summary>扫码枪通信编码。</summary>
	public string ScannerEncoding { get; set; } = "ASCII";

	/// <summary>等读码数据超时（毫秒）。</summary>
	public int ScannerTimeoutMs { get; set; } = 5000;

	/// <summary>
	/// 静默收尾窗口（毫秒）：收到首批数据后，连续这么多毫秒没有新字节就认为一包发完了（不等终止符）。
	/// **默认 0 = 关闭** —— 保持「等终止符 ScannerTerminator 或等满 ScannerTimeoutMs」的原始语义，
	/// 不为了赶节拍缩短扫码等待。需要时可配 250 让不带终止符的 error 包提前收尾。
	/// </summary>
	public int ScannerSilenceMs { get; set; } = 0;

	/// <summary>
	/// 枪回包命中这些关键词 = **没扫上**（去重试），而不是"扫上了但内容不对"。
	/// 逗号/竖线分隔，不区分大小写；留空用默认值 <c>ERROR|NORED|NO READ|NORESULT|FAIL</c>。
	/// </summary>
	public string ScannerNoReadKeywords { get; set; } = ThaScanJudge.DefaultNoReadKeywords;

	/// <summary>连接扫码枪超时（毫秒）。</summary>
	public int ScannerConnectTimeoutMs { get; set; } = 3000;

	/// <summary>是否保持长连接。</summary>
	public bool ScannerKeepAlive { get; set; } = true;

	/// <summary>Mock 扫码枪模拟耗时（毫秒）。</summary>
	public int ScannerMockDelayMs { get; set; } = 200;

	/// <summary>扫不上时的最多尝试次数（含首次），默认 3：3 次都读不到 → 写 PLC 2（NG）+ 弹窗。</summary>
	public int ScannerMaxAttempts { get; set; } = 3;

	/// <summary>两次扫码尝试之间的间隔（毫秒），默认 200：给枪回到待触发态的时间。</summary>
	public int ScannerRetryDelayMs { get; set; } = 200;

	// ----- 外设连接状态（主页显示扫码枪 / 打码机，口径同 PLC 连接状态）-----

	/// <summary>
	/// 外设连接状态探测周期（毫秒），默认 5000。
	/// 主页每周期对打码机/扫码枪各做一次 TCP 连通性探测；适配器已有长连接时直接复用状态，不另建连接。
	/// </summary>
	public int DeviceProbeIntervalMs { get; set; } = 5000;

	/// <summary>外设状态探测单次连接超时（毫秒），默认 1000。</summary>
	public int DeviceProbeTimeoutMs { get; set; } = 1000;

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

	/// <summary>针高口径：PLC 侧为 DINT（32 位有符号整数），÷1000 显示。Int32Cdab（低字在前，默认）/ Int32Abcd（高字在前）。</summary>
	public string PinHeightFormat { get; set; } = "Int32Cdab";

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

	// ============================================================
	// 每日追溯报表 / 本地数据保留
	// ============================================================

	/// <summary>
	/// 是否启用每日追溯报表（自然日一个文件，文件名 追溯报表_yyyyMMdd.xlsx，零点自动新建，当日记录自动追加）。
	/// 关闭后只保留数据保留期清理。
	/// </summary>
	public bool DailyReportEnabled { get; set; } = true;

	/// <summary>
	/// 追溯报表输出目录。默认 <c>D:\追溯数据</c>（留空同样回落到该目录）。
	/// 支持环境变量（%D%\Reports）与相对路径（相对程序目录）；目录不存在会自动创建。
	/// </summary>
	public string DailyReportDirectory { get; set; } = @"D:\追溯数据";

	/// <summary>报表落盘间隔（秒）：固定节奏，默认 60 秒写一次当日报表。</summary>
	public int DailyReportFlushSeconds { get; set; } = 60;

	/// <summary>
	/// 本地数据保留天数：产品记录 / 打码流水 / 读码流水 / 码索引 / 每日报表文件超过该天数，
	/// 在程序退出时检查并清理（有旧数据才弹窗）。0 = 永不删除。默认 30。
	/// </summary>
	public int DataRetentionDays { get; set; } = 30;
}
