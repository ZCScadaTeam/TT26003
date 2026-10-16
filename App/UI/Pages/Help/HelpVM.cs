using System.Collections.ObjectModel;
using AppZC.Services;
using ZC.Mvvm;

namespace AppZC.UI.Pages;

/// <summary>
/// 帮助页 VM（THA26003 三站上位机操作说明）。
/// ReloadByMode 由 HelpPage 挂载时调用（保留原方法契约，内容不再按站别区分）。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
[ObservableObject]
public partial class HelpVM : UiVM<HelpPage>
{
	/// <summary>
	/// 帮助章节列表
	/// </summary>
	public ObservableCollection<HelpSection> Sections { get; set; } = new();

	/// <summary>
	/// 当前选中的章节
	/// </summary>
	public partial HelpSection? SelectedSection { get; set; }

	public HelpVM()
	{
	}

	/// <summary>加载帮助内容（页面挂载时调用）。</summary>
	public void ReloadByMode()
	{
		Sections.Clear();
		LoadSections();
		if (Sections.Count > 0)
			SelectedSection = Sections[0];
	}

	private void LoadSections()
	{
		Sections.Add(new HelpSection
		{
			Title = "系统概述",
			Icon = "🏠",
			Items = new ObservableCollection<HelpItem>
			{
				new() { Title = "系统概述", Content = "本程序是 THA26003 产线的上位机（MES 侧）软件，通过 ModbusTCP（汇川 InovanceTcpNet）与 PLC 交互，承担三个工位：\n• 打码站：按本机时间生成纯时间码（yyMMddHHmmss），经 TCP 下发大族激光打标，回执核对后回写结果\n• 读码站：触发基恩士 SR-1000 扫码，做长度 / 字符集 / 时间窗 / 重码四道校验后回写结果\n• 上传站：PLC 冻结数据区并触发，上位机分段读回全量检测数据，一条产品一条记录落 SQLite" },
				new() { Title = "通讯协议（Rev.2）", Content = "地址一律以 %MW 编程地址表示，实际下发写作 MW&lt;号&gt; 形式（如 %MW10500 → MW10500，与 Modbus 寄存器号 1:1）。\n• 打码站：触发 %MW10500 / 结果 %MW10502 / 内容回写 %MW10420~10459\n• 读码站：触发 %MW10520 / 结果 %MW10522 / 内容回写 %MW10560~10579\n• 上传站：触发 %MW10530（同时冻结数据区）/ 结果 %MW10532（PLC 读到后解冻）\n• 数据区：结果字 %MW10002~10006；上针高 %MW10022~10051；下针高 %MW10122~10151；上位置度 %MW10220~10279；下位置度 %MW10320~10379\n• 握手：%MW20000/20001；心跳：%MW20502（每 0.5 秒写 1）\n• 分段读：FC03 单请求最多 125 个寄存器，数据区按 7 / 30 / 30 / 60 / 60 WORD 分 5 次读" },
				new() { Title = "打码站", Content = "• 上位机按本机时间生成纯时间码（12 位 yyMMddHHmmss）；同一秒已被占用时顺延到下一个整秒，保证唯一\n• 内容同时回写 PLC（%MW10420 起 40WORD），再通过 TCP 下发大族打标机\n• 结果码（%MW10502）：1=OK 2=打标NG 3=回执超时 4=码生成失败 5=MES异常 6=回执内容与下发不一致\n• 回执带内容时会做闭环核对，能抓出模板变量没更新 / 打标机缓存上一件内容这类隐性故障" },
				new() { Title = "读码站", Content = "• 触发扫码枪（基恩士 SR-1000，TCP/IP）读码，校验链：长度 → 全数字 → 可解析为时间 → 时间窗 → 重码比对\n• 时间窗：码内时间与当前时间之差超过配置值（默认 10 分钟）判错码；纯时间码无固定字段，用时间窗替代\n• 结果码（%MW10522）：1=OK 2=未扫到 3=重码 4=错码 5=枪通讯异常 6=MES内部异常\n• 重码比对与之前收到的所有码做比较（SQLite 主键索引，全量比对毫秒级）" },
				new() { Title = "是否复检 / 复检次数", Content = "• 协议地址表中「是否复检」一栏为「暂无」，PLC 不提供该信号，所以监控页该行显示“无地址（MES 自算）”\n• MES 侧用码索引重入次数替代：同一码在在制状态（已打码 / 已读码未上传）下重复经过读码站的次数\n　0 = 未复检（首次读码即通过）→ 是否复检 = 否\n　≥1 = 复检 / 重入 → 是否复检 = 是\n• 若 PLC 后续定义了该地址，把它接进上传站读取即可，复检次数仍作为交叉校验保留" },
				new() { Title = "上传站 / 总结果", Content = "• PLC 置 %MW10530=1 的同时冻结数据区，上位机分段读完后回写 %MW10532，PLC 读到后才解冻\n• 回写是解冻的唯一条件：上位机作业超时（3 秒）也一定会回写，避免数据区被永久锁死导致停线\n• 总结果由 MES 自算：其余结果里没有 NG(2) → OK(1)；有一项 NG → NG(2)\n• 打码 / 读码结果用各自的结果码空间，只有 ==1 才算通过（否则重码 3、错码 4 会被“没有 2 就 OK”误判）" },
				new() { Title = "数据说明", Content = "• 上 / 下针高各 15 点，上 / 下位置度各 15 点（协议表列到 PIN16，但现场仅 15 个有效，PIN16 为预留，不读、不上界面）\n• 数值口径默认 IEEE754 单精度、字序 CDAB；若显示为 1e-38 量级等异常值，请在配置页把「数值口径」切到 FloatAbcd / RawDiv1000\n• 落库为一条产品一条记录（INSERT），不按产品码覆盖，保证重码可追溯" },
				new() { Title = "数据不更新", Content = "1. 检查主页 PLC 状态卡是否显示「已连接」\n2. 在【监控】页确认心跳 %MW20502 的值在变化\n3. 确认 PLC 端是否置位对应站的触发位（打码 %MW10500 / 读码 %MW10520 / 上传 %MW10530）\n4. 上传站注意：PLC 读到 %MW10532 ≠ 0 才会解冻数据区，不回写会一直冻结\n5. 查看【警告】页的操作记录是否有错误信息" },
				new() { Title = "常用操作", Content = "• 主页：三站状态卡 + 产品记录表；「交互结构体监控」按钮进入监控页\n• 监控页：三站信号位与数据区全部寄存器的实时值，每 500ms 刷新\n• 统计页：按时间段 / 班组查询、明细表（101 列）与 Excel / CSV 导出\n• 配置页：PLC 通讯（含汇川系列与地址前缀）、打码站、读码站、码规则 + 数值口径" },
			}
		});

		Sections.Add(new HelpSection
		{
			Title = "登录与权限",
			Icon = "🔐",
			Items = new ObservableCollection<HelpItem>
			{
				new() { Title = "登录方式", Content = "1. 进入【人员】>【身份登入】页面\n2. 在下拉框中选择身份（操作员/技术员/PE/ME/管理员/厂商）\n3. 点击登录即可（本机无 MES 刷卡环境，免密登录）\n4. 登录后页面右上角显示当前身份" },
				new() { Title = "权限说明", Content = "• 操作员：主页、统计、消息、帮助\n• 技术员/PE/ME：操作员权限 + 配置页面\n• 管理员/厂商：所有界面（完全权限）\n• 未登录或操作员身份访问「配置」页签会被拦截并提示" },
				new() { Title = "登出", Content = "在【人员】>【身份登入】页面点击「登出」按钮即可退出当前身份。" }
			}
		});

		Sections.Add(new HelpSection
		{
			Title = "主页面",
			Icon = "📊",
			Items = new ObservableCollection<HelpItem>
			{
				new() { Title = "三站状态卡", Content = "• 打码站 / 读码站 / 上传站 三张卡片，显示各站触发位、最近结果、完成时间与码\n• 结果颜色：OK 绿、NG / 异常 红、未完成 灰\n• 下方产品记录表为上传落库结果（一条产品一行，内存保留最近 200 条）" },
				new() { Title = "PLC 状态卡", Content = "• 连接状态（已连接 / 未连接）\n• 心跳 %MW20502 实时值\n• 累计 OK / NG 数量（本次运行期间的计数）\n• 实时结果字 %MW10002 / 10004 / 10006（导通耐压 / 针高 / 位置度）" },
				new() { Title = "检测历史记录", Content = "• 下方表格显示最近的检测记录（时间、通道、产品码、码等级、各分项结果、复检）\n• 内存最多保留 500 条，程序启动时自动从数据库加载最近 50 条\n• 完整历史请到【统计】查询" },
				new() { Title = "实时消息", Content = "• 页面底部显示最近 5 条采集消息（通道、产品码、总结果）" }
			}
		});

		Sections.Add(new HelpSection
		{
			Title = "监控",
			Icon = "🖥️",
			Items = new ObservableCollection<HelpItem>
			{
				new() { Title = "协议信号监控", Content = "• 显示三站信号位（打码 / 读码 / 上传 触发·结果）、握手心跳，以及数据区全部寄存器\n• 上 / 下针高各 15 点；上 / 下位置度各 15 点（PIN16 为协议预留，不上界面）\n• 页面每 500ms 自动刷新；针高 / 位置度在上传触发时才读取，显示的是最近一次上传的值\n• 用于观察 PLC 交互状态，辅助排查通讯问题" },
			}
		});

		Sections.Add(new HelpSection
		{
			Title = "统计功能",
			Icon = "📈",
			Items = new ObservableCollection<HelpItem>
			{
				new() { Title = "数据概览", Content = "1. 选择统计方式：按小时（最近 24 小时）/ 按班组（最近 14 个班次）/ 按天（最近 30 天）\n2. 选择视图：图表视图 / 表格视图 / 明细视图\n3. 点击查询，显示 OK / NG 数量及良率\n4. 班次定义：白班 8:30~20:30，晚班 20:30~次日 8:30" },
				new() { Title = "数据导出", Content = "• 明细视图可导出 Excel 或 CSV 文件（101 列）\n• 列头顺序：产品码 / 打码结果 / 读码结果 / 总结果 / 导通耐压结果 / 针高结果 / 位置度结果 / 复检次数\n  / 上针高1~15 / 下针高1~15 / 上位置度X、Y各1~15 / 下位置度X、Y各1~15\n  / 打码时间 / 读码时间 / 创建时间（上传）/ 更新时间" }
			}
		});

		Sections.Add(new HelpSection
		{
			Title = "配置管理",
			Icon = "⚙️",
			Items = new ObservableCollection<HelpItem>
			{
				new() { Title = "连接设置", Content = "1. 进入【配置】>【连接设置】（需技术员及以上身份登录）\n2. 配置 PLC IP 地址与端口（ModbusTCP，默认 502）\n3. 点击保存配置\n\n保存后 PLC 采集服务会自动使用新地址重连，无需重启程序。" }
			}
		});

		Sections.Add(new HelpSection
		{
			Title = "常见问题",
			Icon = "❓",
			Items = new ObservableCollection<HelpItem>
			{
				new() { Title = "PLC 连接失败", Content = "1. 检查 PLC 是否通电、网线是否连接\n2. 在【配置】>【连接设置】确认 IP 地址与端口正确\n3. 确认本机与 PLC 在同一网段\n4. 查看操作记录中的错误日志\n5. 程序每 3 秒自动重连，恢复网络后即自动恢复" },
				new() { Title = "数据不更新", Content = "1. 检查主页 PLC 状态卡是否显示「已连接」\n2. 在【监控】页确认心跳 %MW20502 的值在变化\n3. 确认 PLC 端是否置位对应站触发位（打码 %MW10500 / 读码 %MW10520 / 上传 %MW10530）\n4. 查看操作记录是否有错误信息" },
				new() { Title = "数值显示异常", Content = "• 针高/位置度数值异常（如 1e-38 量级）通常为 PLC 端浮点字节序与本程序解析方式不一致，请在配置页把「数值口径」切到 FloatAbcd / RawDiv1000（对应 Tha26003.NumericFormat）" },
				new() { Title = "数据库位置", Content = "本地 SQLite 数据库位于程序目录 AppData/THA26003.db，含 4 张表：\n• ThaProductRecord（产品记录，一条产品一行）\n• ThaCodeIndex（码索引 / 重码比对权威表）\n• ThaMarkLog（打码流水，含大族回执原文）\n• ThaScanLog（读码流水，含否定原因）\n可用 DB Browser for SQLite 等工具直接查看。" }
			}
		});
	}
}

/// <summary>
/// 帮助章节
/// </summary>
public class HelpSection
{
	public string Title { get; set; } = string.Empty;
	public string Icon { get; set; } = string.Empty;
	public ObservableCollection<HelpItem> Items { get; set; } = new();
}

/// <summary>
/// 帮助项目
/// </summary>
public class HelpItem
{
	public string Title { get; set; } = string.Empty;
	public string Content { get; set; } = string.Empty;
}