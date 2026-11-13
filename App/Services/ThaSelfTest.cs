using System.Text;
using AppZC.Models.DB;
using MiniExcelLibs;
using SqlSugar;

namespace AppZC.Services;

/// <summary>
/// 无界面自检（<c>AppZC.exe --selftest</c>）。
/// 用真实生产代码跑：纯时间码生成 → 校验链 → 码索引状态机 → 协议解析 → 落库语义 → 结果码映射。
/// 全部通过返回 0，任一失败返回 1。用于改完代码后的回归验证（不需要 PLC / 打标机 / 扫码枪）。
/// </summary>
public static class ThaSelfTest
{
	private static int _pass;
	private static int _fail;
	private static readonly List<string> _testCodes = new();

	public static int Run()
	{
		Console.WriteLine("=== THA26003 自检开始 ===");
		_pass = 0;
		_fail = 0;

		try
		{
			TestAsciiRoundTrip();
			TestCodeFormat();
			TestCodeGeneratorUniqueness();
			TestCodeSegments();
			TestValidator();
			TestCodeIndexStateMachine();
			TestMeasurementParse();
			TestProductRecordInsertSemantics();
			TestOverallResult();
		TestScanJudge();
		TestScanGradeRule();
		TestLateScanFill();
		TestResultTextMapping();
			TestDailyReport();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[FATAL] 自检异常：{ex}");
			_fail++;
		}
		finally
		{
			CleanupTestRows();
		}

		Console.WriteLine($"=== THA26003 自检结束：通过 {_pass} / 失败 {_fail} ===");
		return _fail == 0 ? 0 : 1;
	}

	private static void Check(string name, bool ok, string? detail = null)
	{
		if (ok)
		{
			_pass++;
			Console.WriteLine($"  [PASS] {name}");
		}
		else
		{
			_fail++;
			Console.WriteLine($"  [FAIL] {name}{(detail == null ? "" : " —— " + detail)}");
		}
	}

	// ---------- 1. ASCII 编解码往返 ----------

	private static void TestAsciiRoundTrip()
	{
		Console.WriteLine("[1] ASCII WORD 区编解码往返");
		var words = Tha26003.EncodeAsciiWords("260902135901", Tha26003.MarkEchoWords);
		var bytes = new byte[Tha26003.MarkEchoWords * 2];
		for (int i = 0; i < words.Length; i++)
		{
			bytes[i * 2] = (byte)((words[i] >> 8) & 0xFF);
			bytes[i * 2 + 1] = (byte)(words[i] & 0xFF);
		}
		var back = Tha26003.ParseAscii(bytes, 0, bytes.Length, false);
		Check("40 字符区往返 = 原串", back == "260902135901", $"得到 '{back}'");
		Check("WORD 数 = 40", words.Length == 40, $"得到 {words.Length}");
		Check("首 WORD = '26'", words[0] == (short)(('2' << 8) | '6'), $"得到 0x{words[0]:X4}");
		Check("第 7 WORD 起为 0（补零）", words[6] == 0, $"得到 0x{words[6]:X4}");
	}

	// ---------- 2. 码格式 ----------

	private static void TestCodeFormat()
	{
		Console.WriteLine("[2] 纯时间码格式");
		var t = new DateTime(2026, 9, 2, 13, 59, 1);
		var code = ThaCodeGenerator.Format(t, "yyMMddHHmmss");
		Check("格式 = yyMMddHHmmss", code == "260902135901", $"得到 '{code}'");
		Check("长度 12", code.Length == 12, $"得到 {code.Length}");
		var ms = ThaCodeGenerator.Format(t.AddMilliseconds(7), "yyMMddHHmmssfff");
		Check("毫秒格式长度 15", ms.Length == 15, $"得到 '{ms}'");
	}

	// ---------- 3. 生成器唯一性（同秒冲突顺延） ----------

	private static void TestCodeGeneratorUniqueness()
	{
		Console.WriteLine("[3] 生成器唯一性（同秒冲突 → 等到下一个整秒）");
		if (!ThaDb.IsInitialized)
		{
			Check("数据库已初始化", false, "ThaDb 未初始化，跳过后面的库相关检查");
			return;
		}

		// 占用"当前秒"的码
		var taken = ThaCodeGenerator.Format(DateTime.Now, "yyMMddHHmmss");
		_testCodes.Add(taken);
		ThaCodeStore.TryRegisterMarked(taken, taken, DateTime.Now);
		Check("预占码已登记", ThaCodeStore.Exists(taken), taken);

		var next = ThaCodeGenerator.Generate("yyMMddHHmmss", 3000);

		Check("生成成功", next != null, "返回 null");
		Check("生成的码 ≠ 已占用码", next != taken, $"{next} vs {taken}");
		if (next != null)
		{
			_testCodes.Add(next);
			var takenTime = DateTime.ParseExact(taken, "yyMMddHHmmss", null);
			Check("新码可解析为时间", DateTime.TryParseExact(next, "yyMMddHHmmss", null,
				System.Globalization.DateTimeStyles.None, out var nextTime), next);
			Check("新码时间不早于已占用码（已向后顺延）",
				DateTime.TryParseExact(next, "yyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var nt)
				&& nt >= takenTime, $"{next} vs {taken}");
		}
		Check("已占用码未被覆盖", ThaCodeStore.Exists(taken));

		// 等待窗口内取不到唯一码 → 明确失败（而不是返回重码）
		var baseTime = DateTime.Now;
		for (int i = 0; i < 12; i++)
		{
			var c = ThaCodeGenerator.Format(baseTime.AddSeconds(i), "yyMMddHHmmss");
			if (ThaCodeStore.Exists(c)) continue;
			_testCodes.Add(c);
			ThaCodeStore.TryRegisterMarked(c, c, baseTime);
		}
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var blocked = ThaCodeGenerator.Generate("yyMMddHHmmss", 200);
		sw.Stop();
		Check("连续 12 秒均被占用时 → 等待窗口内返回 null（不返回重码）", blocked == null, $"得到 '{blocked}'，耗时 {sw.ElapsedMilliseconds}ms");
	}

	// ---------- 4. 校验链 ----------

	// ---------- 3.5 产品码分段拼装 ----------

	private static void TestCodeSegments()
	{
		Console.WriteLine("[3.5] 产品码分段拼装（触点元件 24 位 / DDD 年内天序 / 批次号 5 位每天 8 点清零）");

		var cfg = new AppConfig();
		var segs = cfg.CodeSegments ?? new List<CodeSegment>();

		Check("默认分段总长 = 24 位", ThaCodeAssembly.TotalLength(segs) == 24,
			$"{ThaCodeAssembly.TotalLength(segs)}");

		// 启动日志里的一行描述：现场/远程不打开配置页也能确认「现在按哪套分段跑」
		var desc = ThaCodeAssembly.Describe(cfg);
		Check("码结构描述含总长与批次号段",
			desc.Contains("24 位 = ") && desc.Contains("产品批次号(5)") && desc.Contains("08:00 清零"), desc);
		Console.WriteLine($"       {desc}");

		var pureDesc = ThaCodeAssembly.Describe(new AppConfig { CodeSegments = new List<CodeSegment>() });
		Check("分段全空 → 描述为纯时间码（无批次流水）",
			pureDesc.Contains("纯时间码") && pureDesc.Contains("无批次流水"), pureDesc);

		Check("产品批次号段定长 = 5 位",
			segs.Any(x => CodeSegmentModes.Parse(x.Mode) == CodeSegmentModes.Serial && x.Length == 5),
			string.Join(" / ", segs.Select(x => $"{x.Name}:{x.Length}")));

		// 客户邮件里的样例：第 299 天 / 26 年 / 12:00:00 / 批次 00001
		var t = new DateTime(2026, 10, 26, 12, 0, 0);
		var code = ThaCodeAssembly.Build(segs, t, 1);
		var expect = "2" + "299" + "26" + "120000" + "02" + "08" + "00001" + "01" + "1";
		Check("按段拼出 24 位码（与客户字段表一致）", code == expect, $"{code} vs {expect}");

		Check("日期反解（DDD + yy + HHmmss 拼成完整时间）",
			ThaCodeAssembly.TryParseTime(code, segs) is { } dt && dt == new DateTime(2026, 10, 26, 12, 0, 0),
			ThaCodeAssembly.TryParseTime(code, segs)?.ToString("yyyy-MM-dd HH:mm:ss") ?? "null");

		// Serial 段：定长 5，批次号 7 → 00007
		var serialSegs = new List<CodeSegment>
		{
			new() { Name = "前缀", Mode = "Fixed", Value = "A0", Length = 2 },
			new() { Name = "产品批次号", Mode = "Serial", Length = 5 },
		};
		Check("批次号段左补零（7 → 00007）", ThaCodeAssembly.Build(serialSegs, t, 7) == "A000007",
			ThaCodeAssembly.Build(serialSegs, t, 7));

		// 天序混写（yyddd = 年份 + 年内天数）
		var doySegs = new List<CodeSegment> { new() { Name = "yyddd", Mode = "Time", Value = "yyddd", Length = 5 } };
		Check("yyddd 混写 → 26299", ThaCodeAssembly.Build(doySegs, t, 1) == "26299",
			ThaCodeAssembly.Build(doySegs, t, 1));
		Check("yyddd 反解 → 2026-10-26",
			ThaCodeAssembly.TryParseTime("26299", doySegs)?.Date == new DateTime(2026, 10, 26),
			ThaCodeAssembly.TryParseTime("26299", doySegs)?.ToString("yyyy-MM-dd") ?? "null");

		// 超长截断（保留低位）
		var clipSegs = new List<CodeSegment> { new() { Name = "x", Mode = "Fixed", Value = "12345", Length = 3 } };
		Check("超长保留低位", ThaCodeAssembly.Build(clipSegs, t, 1) == "345", ThaCodeAssembly.Build(clipSegs, t, 1));

		// 生产日（批次号清零时刻）口径
		var w1 = ThaProductionDay.Window(new DateTime(2026, 10, 27, 7, 59, 0), 8);
		Check("07:59 仍属前一天生产日（窗口 26 日 08:00 → 27 日 08:00）",
			w1.Start == new DateTime(2026, 10, 26, 8, 0, 0) && w1.End == new DateTime(2026, 10, 27, 8, 0, 0),
			$"{w1.Start:MM-dd HH:mm} → {w1.End:MM-dd HH:mm}");

		var w2 = ThaProductionDay.Window(new DateTime(2026, 10, 27, 8, 0, 0), 8);
		Check("08:00 进入新生产日 → 批次号从 1 重新数",
			w2.Start == new DateTime(2026, 10, 27, 8, 0, 0),
			$"{w2.Start:MM-dd HH:mm} → {w2.End:MM-dd HH:mm}");

		Check("日期段按生产日：27 日 02:00 打码 → 记 26 日",
			ThaProductionDay.Stamp(new DateTime(2026, 10, 27, 2, 0, 0), 8) == new DateTime(2026, 10, 26, 2, 0, 0),
			ThaProductionDay.Stamp(new DateTime(2026, 10, 27, 2, 0, 0), 8).ToString("yyyy-MM-dd HH:mm"));

		Check("清零时刻 = 0 时回到自然日（不偏移）",
			ThaProductionDay.Stamp(new DateTime(2026, 10, 27, 2, 0, 0), 0) == new DateTime(2026, 10, 27, 2, 0, 0));

		Check("时间窗比对：码内日期差整天时折算（00:00-07:59 不误判超窗）",
			Math.Abs(ThaProductionDay.MinutesApart(new DateTime(2026, 10, 27, 2, 5, 0), new DateTime(2026, 10, 26, 2, 0, 0))) <= 10
			&& Math.Abs(ThaProductionDay.MinutesApart(new DateTime(2026, 10, 27, 2, 0, 0), new DateTime(2026, 10, 26, 9, 0, 0))) > 10);

		// 校验链：段模式下按总长 + 时间窗（用"当前生产日"的码，否则会被时间窗正确拒绝）
		var live = ThaCodeAssembly.Build(segs, ThaProductionDay.Stamp(DateTime.Now, cfg.SerialResetHour), 1);
		Check("段模式：24 位全数字通过", ThaCodeValidator.Validate(live, cfg).Ok,
			ThaCodeValidator.Validate(live, cfg).Reason);
		Check("段模式：23 位 → 长度不符",
			!ThaCodeValidator.Validate(live[..23], cfg).Ok
			&& (ThaCodeValidator.Validate(live[..23], cfg).Reason?.Contains("长度不符") ?? false),
			ThaCodeValidator.Validate(live[..23], cfg).Reason);
		Check("段模式：含字母 → 非法字符",
			!ThaCodeValidator.Validate("A" + live[1..], cfg).Ok,
			ThaCodeValidator.Validate("A" + live[1..], cfg).Reason);

		// 时间窗：把时间段改成 2 小时前 → 超窗
		var stale = ThaCodeAssembly.Build(segs, ThaProductionDay.Stamp(DateTime.Now.AddHours(-2), cfg.SerialResetHour), 1);
		var staleV = ThaCodeValidator.Validate(stale, cfg);
		Check("段模式：时间超出时间窗 → 拒绝",
			!staleV.Ok && (staleV.Reason?.Contains("时间超窗") ?? false), staleV.Reason);

		// 分段全空 → 回落纯时间码模式
		var pureCfg = new AppConfig { CodeSegments = new List<CodeSegment>() };
		Check("分段全空 → 回落纯时间码模式",
			!ThaCodeAssembly.IsEnabled(pureCfg)
			&& ThaCodeValidator.Validate(ThaCodeGenerator.Format(DateTime.Now, pureCfg.CodeDateFormat), pureCfg).Ok);
	}

	private static void TestValidator()
	{
		Console.WriteLine("[4] 读码校验链（长度 / 字符集 / 时间 / 时间窗）");
		const string fmt = "yyMMddHHmmss";
		var now = DateTime.Now;

		var ok = ThaCodeValidator.Validate(ThaCodeGenerator.Format(now, fmt), fmt, 12, 10);
		Check("当前时间的码 → 通过", ok.Ok, ok.Reason);

		var empty = ThaCodeValidator.Validate("", fmt, 12, 10);
		Check("空串 → 拒绝", !empty.Ok, empty.Reason);

		var shortLen = ThaCodeValidator.Validate("26090213590", fmt, 12, 10);
		Check("长度 11 → 拒绝（长度不符）", !shortLen.Ok && shortLen.Reason!.Contains("长度"), shortLen.Reason);

		var illegal = ThaCodeValidator.Validate("26090213A901", fmt, 12, 10);
		Check("含字母 → 拒绝（非法字符）", !illegal.Ok && illegal.Reason!.Contains("非法"), illegal.Reason);

		var badTime = ThaCodeValidator.Validate("261302135901", fmt, 12, 10);
		Check("月=13 → 拒绝（时间解析失败）", !badTime.Ok, badTime.Reason);

		var old = ThaCodeValidator.Validate(ThaCodeGenerator.Format(now.AddMinutes(-30), fmt), fmt, 12, 10);
		Check("30 分钟前的码 → 拒绝（时间超窗）", !old.Ok && old.Reason!.Contains("超窗"), old.Reason);

		var oldOk = ThaCodeValidator.Validate(ThaCodeGenerator.Format(now.AddMinutes(-30), fmt), fmt, 12, 0);
		Check("时间窗=0（关闭）时同样输入 → 通过", oldOk.Ok, oldOk.Reason);

		Check("归一化去掉终止符", ThaCodeValidator.Normalize("260902135901\r\n") == "260902135901");

		TestScanCodeParse();
	}

	// ---------- 4.1 扫码内容拆解（码 + 码等级）----------

	private static void TestScanCodeParse()
	{
		Console.WriteLine("[4.1] 扫码内容拆解（正则 → 码 + 码等级）");

		var withGrade = ThaScanCode.Parse("000143348000026070:A", null);
		Check("码:等级 → 码 = 000143348000026070",
			withGrade.Matched && withGrade.Code == "000143348000026070", $"{withGrade.Code}");
		Check("码:等级 → 等级 = A", withGrade.Grade == "A", $"{withGrade.Grade ?? "(null)"}");

		var fullWidth = ThaScanCode.Parse(" 000143348000026070 ： B \r\n", null);
		Check("全角冒号 + 空白 → 仍能拆（等级 B）",
			fullWidth.Code == "000143348000026070" && fullWidth.Grade == "B",
			$"{fullWidth.Code}/{fullWidth.Grade ?? "(null)"}");

		var plain = ThaScanCode.Parse("260902135901", null);
		Check("无分隔符老码 → 整串为码、等级为空",
			plain.Code == "260902135901" && plain.Grade == null, $"{plain.Code}/{plain.Grade ?? "(null)"}");

		var garbage = ThaScanCode.Parse("ERR#42", null);
		Check("不合格式 → 整串兜底当码（交给后续校验判 NG）",
			garbage.Code == "ERR#42" && garbage.Grade == null && !garbage.Matched, garbage.Code);

		var custom = ThaScanCode.Parse("000143348000026070|C", @"^(?<code>\d+)\|(?<grade>[A-Z])$");
		Check("自定义正则生效（|C → 等级 C）",
			custom.Code == "000143348000026070" && custom.Grade == "C", $"{custom.Code}/{custom.Grade ?? "(null)"}");

		var badPattern = ThaScanCode.Parse("000143348000026070:A", "(?<code>[");
		Check("非法正则（配置写错）→ 回落默认，不抛异常且仍能拆",
			badPattern.Code == "000143348000026070" && badPattern.Grade == "A", $"{badPattern.Code}/{badPattern.Grade ?? "(null)"}");

		var empty = ThaScanCode.Parse("   ", null);
		Check("空内容 → 码为空、等级为空", empty.Code == "" && empty.Grade == null);
	}

	// ---------- 5. 码索引状态机 ----------

	private static void TestCodeIndexStateMachine()
	{
		Console.WriteLine("[5] 码索引状态机（复检放行 / 重码 / 在制超时 / 未登记码）");
		if (!ThaDb.IsInitialized)
		{
			Check("数据库已初始化", false, "跳过");
			return;
		}

		const string fmt = "yyMMddHHmmss";
		// 用一个不会撞车的未来秒，避免与前面占用冲突
		var code = ThaCodeGenerator.Format(DateTime.Now.AddSeconds(1), fmt);
		while (ThaCodeStore.Exists(code)) code = ThaCodeGenerator.Format(DateTime.Now.AddSeconds(1), fmt);
		_testCodes.Add(code);

		// 未登记码
		var unreg = ThaCodeStore.EvaluateForScan("999912312359", 2, false);
		Check("未登记码 → 错码", unreg.Result == ScanResultCode.BadFormat, unreg.Reason);
		var unregAllow = ThaCodeStore.EvaluateForScan("999912312359", 2, true);
		Check("未登记码 + 放行开关 → OK", unregAllow.Result == ScanResultCode.Ok, unregAllow.Reason);

		// 登记打码
		Check("登记打码", ThaCodeStore.TryRegisterMarked(code, code, DateTime.Now));
		Check("重复登记同码 → 失败", !ThaCodeStore.TryRegisterMarked(code, code, DateTime.Now));
		Check("状态 = 已打码", ThaCodeStore.GetStatus(code) == CodeStatus.Marked);

		// 第一次读码：状态 0（已打码）→ OK，且**不算复检**（复检次数保持 0）
		var d1 = ThaCodeStore.EvaluateForScan(code, 2, false);
		Check("已打码未读码 → OK（首次读码）", d1.Result == ScanResultCode.Ok, d1.Reason);
		Check("首次读码复检次数 = 0（是否复检=否）", d1.RetestCount == 0, $"得到 {d1.RetestCount}");
		Check("首次读码原因标注正确", d1.Reason?.Contains("首次") == true, d1.Reason);
		ThaCodeStore.MarkScanned(code, DateTime.Now, d1.RetestCount);
		Check("状态 = 已读码", ThaCodeStore.GetStatus(code) == CodeStatus.Scanned);

		// 第二次读码：状态 1（已读码）→ 这才是复检/重入，计数 +1
		var d2 = ThaCodeStore.EvaluateForScan(code, 2, false);
		Check("已读码未上传 → OK（复检放行）", d2.Result == ScanResultCode.Ok, d2.Reason);
		Check("复检次数 +1 = 1（是否复检=是）", d2.RetestCount == 1, $"得到 {d2.RetestCount}");
		Check("复检原因标注正确", d2.Reason?.Contains("复检") == true, d2.Reason);

		// 上传后：已上传 → 重码
		ThaCodeStore.MarkUploaded(code, 123456789L, DateTime.Now);
		Check("状态 = 已上传", ThaCodeStore.GetStatus(code) == CodeStatus.Uploaded);
		var d3 = ThaCodeStore.EvaluateForScan(code, 2, false);
		Check("已上传 → 重码", d3.Result == ScanResultCode.Duplicate, d3.Reason);
		Check("重码原因标注已上传", d3.Reason?.Contains("已上传") == true, d3.Reason);

		// 在制超时：另起一个码，MarkTime 设为 3 小时前
		var wip = ThaCodeGenerator.Format(DateTime.Now.AddMinutes(-181), fmt);
		while (ThaCodeStore.Exists(wip)) wip = wip[..10] + ((int.Parse(wip[10..]) + 1) % 60).ToString("D2");
		_testCodes.Add(wip);
		ThaCodeStore.TryRegisterMarked(wip, wip, DateTime.Now.AddHours(-3));
		var d4 = ThaCodeStore.EvaluateForScan(wip, 2, false);
		Check("在制 3h 超 2h 时限 → 重码", d4.Result == ScanResultCode.Duplicate, d4.Reason);
		Check("重码原因标注在制超时", d4.Reason?.Contains("在制超时") == true, d4.Reason);
	}

	// ---------- 6. 协议解析 ----------

	private static void TestMeasurementParse()
	{
		Console.WriteLine("[6] 数据区分段解析（针高 DINT÷1000 / 位置度 REAL / 各 15 点）");

		var segResult = new byte[Tha26003.SegResultLen * 2];
		WriteWord(segResult, Tha26003.OffWithstand, 1);
		WriteWord(segResult, Tha26003.OffPinHeight, 2);
		WriteWord(segResult, Tha26003.OffPosition, 1);

		var segTopPin = new byte[Tha26003.SegTopPinLen * 2];
		var segBotPin = new byte[Tha26003.SegBottomPinLen * 2];
		// 针高：PLC 侧 DINT（÷1000）+ 默认字序 CDAB（低字在前）—— 2115 → 2.115
		for (int i = 0; i < Tha26003.PinHeightCount; i++)
		{
			WriteInt32LoHi(segTopPin, i * 2, 2115 + i);
			WriteInt32LoHi(segBotPin, i * 2, 3446 + i);
		}

		var segTopPos = new byte[Tha26003.SegTopPosLen * 2];
		var segBotPos = new byte[Tha26003.SegBottomPosLen * 2];
		for (int i = 0; i < Tha26003.PositionCount; i++)
		{
			WriteFloatCdab(segTopPos, i * 4, 0.100f + i * 0.010f);
			WriteFloatCdab(segTopPos, i * 4 + 2, 0.200f + i * 0.010f);
			WriteFloatCdab(segBotPos, i * 4, 1.100f + i * 0.010f);
			WriteFloatCdab(segBotPos, i * 4 + 2, 1.200f + i * 0.010f);
		}

		var m = Tha26003.ParseMeasurement(segResult, segTopPin, segBotPin, segTopPos, segBotPos, PinHeightFormat.Int32Cdab, PositionFormat.FloatCdab);

		Check("结果字 1/2/1", m.WithstandResult == 1 && m.PinHeightResult == 2 && m.PositionResult == 1,
			$"{m.WithstandResult}/{m.PinHeightResult}/{m.PositionResult}");
		Check("上针高 15 点", m.TopPinHeights.Length == 15, $"{m.TopPinHeights.Length}");
		Check("上针高 Pin1 = 2.115（DINT 2115 ÷1000）", Near(m.TopPinHeights[0], 2.115), S(m.TopPinHeights[0]));
		Check("上针高 Pin15 = 2.129", Near(m.TopPinHeights[14], 2.129), S(m.TopPinHeights[14]));
		Check("下针高 Pin1 = 3.446", Near(m.BottomPinHeights[0], 3.446), S(m.BottomPinHeights[0]));
		Check("上位置度 PIN1 X = 0.100 / Y = 0.200", Near(m.TopPosX[0], 0.100) && Near(m.TopPosY[0], 0.200),
			$"{S(m.TopPosX[0])}/{S(m.TopPosY[0])}");
		Check("位置度有效点数 = 15", m.TopPosX.Length == 15 && m.TopPosY.Length == 15 && m.BottomPosX.Length == 15,
			$"{m.TopPosX.Length}/{m.TopPosY.Length}/{m.BottomPosX.Length}");
		Check("上位置度 PIN15 X = 0.240", Near(m.TopPosX[14], 0.240), S(m.TopPosX[14]));
		Check("下位置度 PIN15 Y = 1.340", Near(m.BottomPosY[14], 1.340), S(m.BottomPosY[14]));
		Check("数据合理性检查通过", Tha26003.IsMeasurementSane(m));

		// 整段未读到 → 该组字段全为 null（与"读到 0"区分）
		var mNull = Tha26003.ParseMeasurement(null, null, null, null, null, PinHeightFormat.Int32Cdab, PositionFormat.FloatCdab);
		Check("整段为 null → 结果字与针高全为 null",
			mNull.WithstandResult == null && mNull.PinHeightResult == null && mNull.PositionResult == null
			&& mNull.TopPinHeights.All(v => v == null) && mNull.TopPosX.All(v => v == null),
			"sanity");
		Check("全 null 不算脏数据（不判数据 NG）", Tha26003.IsMeasurementSane(mNull));
		Check("全 null 时总结果无法判定 → null",
			Tha26003.ComputeOverall(null, null, null, null) == null,
			$"{Tha26003.ComputeOverall(null, null, null, null)}");

		// 结果字读到 0（未采集）→ 归一为 null
		var segZero = new byte[Tha26003.SegResultLen * 2];
		var mZero = Tha26003.ParseMeasurement(segZero, segTopPin, segBotPin, segTopPos, segBotPos, PinHeightFormat.Int32Cdab, PositionFormat.FloatCdab);
		Check("结果字读到 0 → 归一为 null（未采集）",
			mZero.WithstandResult == null && mZero.PinHeightResult == null && mZero.PositionResult == null);

		// REAL 段全 0xFF = IEEE754 NaN → 落 NULL
		var dirtyPos = new byte[Tha26003.SegTopPosLen * 2];
		Array.Fill(dirtyPos, (byte)0xFF);
		var m2 = Tha26003.ParseMeasurement(segResult, segTopPin, segBotPin, dirtyPos, segBotPos,
			PinHeightFormat.Int32Cdab, PositionFormat.FloatCdab);
		Check("位置度全 0xFF（REAL NaN）→ 全部落 NULL", m2.TopPosX.All(v => v == null), "sanity");
		Check("NULL 不等于脏值（不判数据 NG，仍落库为 NULL）", Tha26003.IsMeasurementSane(m2));

		// DINT 段全 0xFF = -1 → -0.001（DINT 没有 NaN 语义，'未采集'只能靠整段读失败或结果字判断）
		var dirtyPin = new byte[Tha26003.SegTopPinLen * 2];
		Array.Fill(dirtyPin, (byte)0xFF);
		var m2b = Tha26003.ParseMeasurement(segResult, dirtyPin, segBotPin, segTopPos, segBotPos,
			PinHeightFormat.Int32Cdab, PositionFormat.FloatCdab);
		Check("针高全 0xFF（DINT -1）→ -0.001（DINT 无 NaN，由结果字/整段失败兜底）",
			Near(m2b.TopPinHeights[0], -0.001), S(m2b.TopPinHeights[0]));

		// 针高字序可切：同一组字节，ABCD（高字在前）口径也应能得到真实值
		var abcdPin = new byte[Tha26003.SegTopPinLen * 2];
		WriteInt32HiLo(abcdPin, 0, 2115);
		WriteInt32HiLo(abcdPin, 2, 3446);
		var m4 = Tha26003.ParseMeasurement(segResult, abcdPin, segBotPin, segTopPos, segBotPos,
			PinHeightFormat.Int32Abcd, PositionFormat.FloatCdab);
		Check("高字在前口径：2115 → 2.115", Near(m4.TopPinHeights[0], 2.115, 1e-9), S(m4.TopPinHeights[0]));
		Check("高字在前口径：3446 → 3.446", Near(m4.TopPinHeights[1], 3.446, 1e-9), S(m4.TopPinHeights[1]));

		// 字序选反了会读成什么（证明两个口径确实不同，而不是碰巧都能对）
		var m4b = Tha26003.ParseMeasurement(segResult, abcdPin, segBotPin, segTopPos, segBotPos,
			PinHeightFormat.Int32Cdab, PositionFormat.FloatCdab);
		Check("字序反了会明显错（2115 被当高字 → 138608640/1000）",
			!Near(m4b.TopPinHeights[0], 2.115) && Near(m4b.TopPinHeights[0], 138608.640, 1e-3), S(m4b.TopPinHeights[0]));

		// 位置度口径可切字序（ABCD）
		var abcd = new byte[Tha26003.SegTopPosLen * 2];
		WriteFloatAbcd(abcd, 0, 0.100f);
		var m5 = Tha26003.ParseMeasurement(segResult, segTopPin, segBotPin, abcd, segBotPos,
			PinHeightFormat.Int32Cdab, PositionFormat.FloatAbcd);
		Check("位置度 ABCD 口径：0.100", Near(m5.TopPosX[0], 0.100), S(m5.TopPosX[0]));
	}

	// ---------- 7. 落库语义（一条一记录 + 码状态联动） ----------

	private static void TestProductRecordInsertSemantics()
	{
		Console.WriteLine("[7] 上传落库语义（INSERT 一条一记录 + 码索引联动 + 重复上传保护）");
		if (!ThaDb.IsInitialized)
		{
			Check("数据库已初始化", false, "跳过");
			return;
		}

		var code = "990101" + DateTime.Now.ToString("HHmmss");
		if (ThaCodeStore.Exists(code)) code = "990102" + DateTime.Now.ToString("HHmmss");
		_testCodes.Add(code);

		ThaCodeStore.TryRegisterMarked(code, code, DateTime.Now);

		var rec = new ThaProductRecord
		{
			ProductCode = code,
			MarkContent = code,          // 镭码内容（打标原文）
			OverallResult = 1,
			UploadResult = (int)UploadResultCode.Ok,
			WithstandResult = 1,
			PinHeightResult = 1,
			PositionResult = 1,
			TopPinHeight1 = 2.115,
			BottomPinHeight15 = 3.446,
			TopPinPosX15 = 0.24,
			BottomPinPosY15 = 1.34,
			ScanCodeLevel = "A",
			IsRetest = null,          // PLC 不提供「是否复检」→ null
		};
		rec.TopPinHeight15 = 2.999;

		var stored = ThaDb.InsertProductRecord(rec);
		Check("落库返回成功", stored);
		Check("落库后 Id 已分配", rec.Id > 0, $"{rec.Id}");
		Check("落库后 CreateTime 已填", rec.CreateTime > DateTime.MinValue);

		Check("码索引联动为已上传", ThaCodeStore.GetStatus(code) == CodeStatus.Uploaded);

		// 同码再落一条 → 记录数应为 2（一条一记录，不做 UPSERT 覆盖）
		var rec2 = new ThaProductRecord
		{
			ProductCode = code,
			OverallResult = 2,
		};
		ThaDb.InsertProductRecord(rec2);
		var count = ThaDb.Db.Queryable<ThaProductRecord>().Where(x => x.ProductCode == code).Count();
		Check("同码两条记录并存（不是覆盖）", count == 2, $"实际 {count} 条");

		var row = ThaDb.Db.Queryable<ThaProductRecord>().Where(x => x.ProductCode == code).OrderBy(x => x.Id).First();
		Check("首条数值字段已持久化", Near(row.TopPinHeight1, 2.115, 1e-9) && Near(row.BottomPinHeight15, 3.446, 1e-9),
			$"{S(row.TopPinHeight1)}/{S(row.BottomPinHeight15)}");
		Check("首条位置度字段已持久化", Near(row.TopPinPosX15, 0.24, 1e-9) && Near(row.BottomPinPosY15, 1.34, 1e-9),
			$"{S(row.TopPinPosX15)}/{S(row.BottomPinPosY15)}");
		Check("是否复检落库为 NULL（PLC 未提供）", row.IsRetest == null, $"{row.IsRetest?.ToString() ?? "null"}");
		Check("码等级落库 = A", row.ScanCodeLevel == "A", $"{row.ScanCodeLevel ?? "null"}");
		Check("镭码内容落库 = 产品码（打标原文）", row.MarkContent == code, $"{row.MarkContent ?? "null"}");

		// 产品码来源 = 扫码：未扫上时产品码为空，但码索引仍需按打码码推进到“已上传”
		var markOnly = code + "M";
		_testCodes.Add(markOnly);
		ThaCodeStore.TryRegisterMarked(markOnly, markOnly, DateTime.Now);
		var noScan = new ThaProductRecord
		{
			ProductCode = null,            // 未扫上 → 空
			MarkContent = markOnly,        // 镭码内容（打标原文）
			UploadResult = (int)UploadResultCode.Ok,
		};
		Check("未扫码件可落库（产品码为空）", ThaDb.InsertProductRecord(noScan, markOnly));
		var noScanRow = ThaDb.Db.Queryable<ThaProductRecord>().InSingle(noScan.Id);
		Check("未扫码件产品码落库为 NULL", noScanRow?.ProductCode == null, $"{noScanRow?.ProductCode ?? "null"}");
		Check("未扫码件镭码内容仍保留", noScanRow?.MarkContent == markOnly, $"{noScanRow?.MarkContent ?? "null"}");
		Check("未扫码件码索引仍推进到已上传", ThaCodeStore.GetStatus(markOnly) == CodeStatus.Uploaded,
			$"{ThaCodeStore.GetStatus(markOnly)}");

		// 补读回填要把产品码也补上
		var filled = ThaCodeStore.TryFillLateScan(markOnly, 1, markOnly + ":A", "A", null, 60);
		Check("补读回填后产品码 = 扫码得到的码", filled.Record?.ProductCode == markOnly,
			$"{filled.Record?.ProductCode ?? "null"}");

		// 未采集字段落库应为 NULL，而不是 0
		var recNull = new ThaProductRecord { ProductCode = code + "N", UploadResult = 1 };
		ThaDb.InsertProductRecord(recNull);
		var rowNull = ThaDb.Db.Queryable<ThaProductRecord>().First(x => x.ProductCode == code + "N");
		Check("未采集的针高/位置度落库为 NULL（非 0）",
			rowNull.TopPinHeight1 == null && rowNull.TopPinPosX15 == null && rowNull.WithstandResult == null,
			$"{S(rowNull.TopPinHeight1)}/{S(rowNull.TopPinPosX15)}/{rowNull.WithstandResult}");

		// 流水表
		ThaDb.LogMark(new ThaMarkLog { Content = code, EchoContent = code, Result = 1, RequestTime = DateTime.Now });
		ThaDb.LogScan(new ThaScanLog { CodeNormalized = code, Result = 1, CreateTime = DateTime.Now });
		Check("打码流水可写", ThaDb.Db.Queryable<ThaMarkLog>().Where(x => x.Content == code).Count() >= 1);
		Check("读码流水可写", ThaDb.Db.Queryable<ThaScanLog>().Where(x => x.CodeNormalized == code).Count() >= 1);
	}

	// ---------- 8. 总结果算法 ----------

	private static void TestOverallResult()
	{
		Console.WriteLine("[8] 总结果算法（四项无 NG → 1；任一 NG → 2；测量三项缺项 → null；未扫码不阻塞）");

		Check("四项全 OK → 1", Tha26003.ComputeOverall(1, 1, 1, 1) == 1);
		Check("耐压 NG → 2", Tha26003.ComputeOverall(2, 1, 1, 1) == 2);
		Check("针高 NG → 2", Tha26003.ComputeOverall(1, 2, 1, 1) == 2);
		Check("位置度 NG → 2", Tha26003.ComputeOverall(1, 1, 2, 1) == 2);
		Check("扫码 NG → 2", Tha26003.ComputeOverall(1, 1, 1, 2) == 2);

		Check("结果字未采集(0) → 归一为 NULL（无法判定，不猜 OK）", Tha26003.ComputeOverall(0, 0, 0, 0) == null,
			$"{Tha26003.ComputeOverall(0, 0, 0, 0)}");
		Check("未扫码（扫码 null）但测量三项齐全 → 1（不因未扫码判 NG）",
			Tha26003.ComputeOverall(1, 1, 1, null) == 1,
			$"{Tha26003.ComputeOverall(1, 1, 1, null)}");
		Check("缺一项结果字 → null（无法判定）", Tha26003.ComputeOverall(1, null, 1, 1) == null,
			$"{Tha26003.ComputeOverall(1, null, 1, 1)}");
		Check("全缺 → null", Tha26003.ComputeOverall(null, null, null, null) == null);

		// 多项 NG 仍是 2；只要有一项 NG 不会被"其余都 OK"救回来
		Check("多项 NG 仍为 2", Tha26003.ComputeOverall(2, 2, 1, 2) == 2);
	}

	// ---------- 8b. 读码判定规则（扫上/没扫上 + PLC 附带码比对） ----------

	private static void TestScanJudge()
	{
		Console.WriteLine("[8b] 读码判定：先分扫上/没扫上，再与 %MW10580 附带码比对");

		// ① 枪回包是不是"没扫上"
		Check("枪回 ERROR → 没扫上（要走重试）", ThaScanJudge.IsNoReadReply("ERROR", null));
		Check("枪回 error 小写 → 没扫上", ThaScanJudge.IsNoReadReply("error\r\n", null));
		Check("枪回 NO READ → 没扫上", ThaScanJudge.IsNoReadReply("NO READ\r", null));
		Check("枪回正常码 → 扫上了", !ThaScanJudge.IsNoReadReply("000224143000026210\r", null));
		Check("空回包 → 没扫上", ThaScanJudge.IsNoReadReply("   ", null));
		Check("自定义关键词生效", ThaScanJudge.IsNoReadReply("NOSCAN", "NOSCAN|ERROR"));

		// ② 枪码 vs PLC 附带码
		Check("完全一致 → 匹配", ThaScanJudge.CodesMatch("000224143000026210", "000224143000026210"));
		Check("末尾空白/换行不影响 → 匹配", ThaScanJudge.CodesMatch("000224143000026210 ", "000224143000026210\r\n"));
		Check("不一致 → 不匹配", !ThaScanJudge.CodesMatch("000224143000026210", "000224143000026211"));
		Check("长度不同 → 不匹配", !ThaScanJudge.CodesMatch("000224143000026210", "00022414300002621"));
		Check("PLC 侧为空 → 不匹配（判错码）", !ThaScanJudge.CodesMatch("000224143000026210", null));
		Check("枪码为空 → 不匹配", !ThaScanJudge.CodesMatch(null, "000224143000026210"));

		// ③ 弹窗文案（现场话术）
		var detail = ThaScanJudge.MismatchDetail("000224143000026210", "000224143000026211");
		var lines = detail.Split('\n');
		Check("弹窗第 1 行 = 扫码枪：A", lines.Length >= 3 && lines[0] == "扫码枪：000224143000026210", lines[0]);
		Check("弹窗第 2 行 = PLC搬运获取产品码：B", lines[1] == "PLC搬运获取产品码：000224143000026211", lines[1]);
		Check("弹窗第 3 行 = 不一致", lines[2] == "不一致", lines[2]);
		Check("PLC 侧为空时弹窗显示 (空)", ThaScanJudge.MismatchDetail("000224143000026210", null).Contains("(空)"));

		// ④ 未扫到文案
		var noRead = ThaScanJudge.NoReadDetail(3, "ERROR");
		Check("未扫到文案含次数", noRead.Contains("3 次"), noRead);
		Check("未扫到文案含枪原文", noRead.Contains("ERROR"), noRead);

		// ⑤ 三类 NG 对外都是 2（写 %MW10522）
		Check("未扫到 → PLC 2", Tha26003.ScanPlcCode(ScanResultCode.NoRead) == 2);
		Check("错码 → PLC 2", Tha26003.ScanPlcCode(ScanResultCode.BadFormat) == 2);
		Check("重码 → PLC 2", Tha26003.ScanPlcCode(ScanResultCode.Duplicate) == 2);
		Check("OK → PLC 1", Tha26003.ScanPlcCode(ScanResultCode.Ok) == 1);
		Check("未扫到内部值 7 与 Ng(2) 不再同值", (int)ScanResultCode.NoRead == 7 && (int)ScanResultCode.Ng == 2);
	}

	// ---------- 8c. 码等级阈值判定 ----------

	private static void TestScanGradeRule()
	{
		Console.WriteLine("[8c] 码等级阈值判定（默认 B：A/B 合格，C/D 判 NG）");

		var limit = ThaScanJudge.DefaultCodeGradeThreshold;
		Check("默认阈值 = B", limit == "B", limit);

		Check("A + 阈值 B → 合格", ThaScanJudge.GradeOk("A", "B", out _));
		Check("B + 阈值 B → 合格", ThaScanJudge.GradeOk("B", "B", out _));

		var c = ThaScanJudge.GradeOk("C", "B", out var cReason);
		Check("C + 阈值 B → NG", !c && cReason!.Contains("码等级不符"), cReason);

		var d = ThaScanJudge.GradeOk("D", "B", out var dReason);
		Check("D + 阈值 B → NG", !d && dReason!.Contains("码等级不符"), dReason);

		Check("小写 c → 同 uppercase 判 NG", !ThaScanJudge.GradeOk("c", "B", out _));
		Check("阈值小写 b 也生效（A/B 合格）",
			ThaScanJudge.GradeOk("B", "b", out _) && !ThaScanJudge.GradeOk("C", "b", out _));

		Check("阈值 = C → C 合格、D NG",
			ThaScanJudge.GradeOk("C", "C", out _) && !ThaScanJudge.GradeOk("D", "C", out _));

		Check("阈值留空 → 不做判定（C 也合格）", ThaScanJudge.GradeOk("C", "", out _));
		Check("内容无等级（null）→ 不判 NG", ThaScanJudge.GradeOk(null, "B", out _));

		var bad = ThaScanJudge.GradeOk("3", "B", out var badReason);
		Check("等级非字母（3）→ NG（内容异常）", !bad && badReason!.Contains("码等级非法"), badReason);

		Check("阈值合法性：空 / A / Z 合法，3 非法",
			ThaScanJudge.IsGradeThresholdValid("") && ThaScanJudge.IsGradeThresholdValid("A")
			&& ThaScanJudge.IsGradeThresholdValid("Z") && !ThaScanJudge.IsGradeThresholdValid("3"));

		// 与拆解串起来：枪回包 000143348000026070:C → 等级 C → 阈值 B 下判 NG
		var parts = ThaScanCode.Parse("000143348000026070:C", null);
		Check("整链：码:等级拆解 + 阈值判定（C → NG）",
			parts.Code == "000143348000026070" && !ThaScanJudge.GradeOk(parts.Grade, "B", out _),
			$"{parts.Code}/{parts.Grade}");
	}
	// ---------- 8d. 补读回填（读码晚于采集）----------

	private static void TestLateScanFill()
	{
		Console.WriteLine("[8d] 补读回填（读码晚于采集落库 → 回填扫码结果/码等级）");
		if (!ThaDb.IsInitialized)
		{
			Check("数据库已初始化", false, "跳过");
			return;
		}

		var code = "9903" + DateTime.Now.ToString("HHmmss") + "01";
		_testCodes.Add(code);

		// 模拟现场：采集先落库（扫码结果被上一件的错码污染），随后才读码
		ThaCodeStore.TryRegisterMarked(code, code, DateTime.Now);
		var rec = new ThaProductRecord
		{
			ProductCode = code,
			OverallResult = 2,
			ScanResult = 2,
			ScanCodeRaw = "000134909000026120:F",   // 上一件残值（不是本码）
			WithstandResult = 1,
			PinHeightResult = 1,
			PositionResult = 1,
			UploadResult = (int)UploadResultCode.Ok,
		};
		Check("构造测试记录已落库", ThaDb.InsertProductRecord(rec));

		var fill = ThaCodeStore.TryFillLateScan(code, 1, code + ":A", "A", null, 60);
		Check("窗口内 + 记录里不是本码 → 回填成功", fill.Filled, fill.Reason);
		Check("回填后扫码结果 = OK(1)", fill.Record?.ScanResult == 1, $"{fill.Record?.ScanResult}");
		Check("回填后码等级 = A", fill.Record?.ScanCodeLevel == "A", $"{fill.Record?.ScanCodeLevel ?? "null"}");
		Check("回填后总结果重算 = OK(1)", fill.NewOverall == 1, $"{fill.NewOverall}");

		var row = ThaDb.Db.Queryable<ThaProductRecord>().InSingle(rec.Id);
		Check("回填已写库（不是只改内存）",
			row != null && row.ScanResult == 1 && row.ScanCodeLevel == "A" && row.OverallResult == 1,
			$"{row?.ScanResult}/{row?.ScanCodeLevel ?? "null"}/{row?.OverallResult}");

		var again = ThaCodeStore.TryFillLateScan(code, 1, code + ":A", "A", null, 60);
		Check("同一码再读 → 不再回填（已是本码 → 真重码）",
			!again.Filled && again.Reason == "该记录已有本码扫码结果", again.Reason);

		var off = ThaCodeStore.TryFillLateScan(code, 1, code + ":A", "A", null, 0);
		Check("窗口 = 0（关闭）→ 不回填", !off.Filled && off.Reason!.Contains("已关闭"), off.Reason);

		// 超窗：手工把落库时间推到 10 分钟前
		ThaDb.Db.Updateable<ThaProductRecord>()
			.SetColumns(x => new ThaProductRecord { CreateTime = DateTime.Now.AddMinutes(-10), ScanCodeRaw = null, ScanResult = null })
			.Where(x => x.Id == rec.Id)
			.ExecuteCommand();
		var late = ThaCodeStore.TryFillLateScan(code, 1, code + ":A", "A", null, 60);
		Check("超过回填窗口 → 不回填（保持重码判定）",
			!late.Filled && late.Reason!.Contains("超过补读窗口"), late.Reason);
	}

	// ---------- 9. 结果码映射 ----------

	private static void TestResultTextMapping()
	{
		Console.WriteLine("[9] 结果码文本映射（PLC 侧 1~6 与 UI 显示一致）");
		Check("打码 1→OK", Tha26003.MarkResultText(1) == "OK");
		Check("打码 1→OK / 2→NG", Tha26003.MarkResultText(1) == "OK" && Tha26003.MarkResultText(2) == "NG",
			$"{Tha26003.MarkResultText(1)}/{Tha26003.MarkResultText(2)}");
		Check("10502 只写 1：成功=1，其余一律不写（null，PLC 侧保持 0）",
			Tha26003.MarkDoneCode(MarkResultCode.Ok) == 1
			&& Tha26003.MarkDoneCode(MarkResultCode.LaserNg) == null
			&& Tha26003.MarkDoneCode(MarkResultCode.Timeout) == null
			&& Tha26003.MarkDoneCode(MarkResultCode.GenerateFailed) == null
			&& Tha26003.MarkDoneCode(MarkResultCode.InternalError) == null
			&& Tha26003.MarkDoneCode(MarkResultCode.EchoMismatch) == null,
			$"{Tha26003.MarkDoneCode(MarkResultCode.Ok)?.ToString() ?? "null"}/{Tha26003.MarkDoneCode(MarkResultCode.EchoMismatch)?.ToString() ?? "null"}");
		Check("打码内部细分原因仍可读（弹窗用）", Tha26003.MarkReasonText(MarkResultCode.EchoMismatch) == "回执内容不符",
			Tha26003.MarkReasonText(MarkResultCode.EchoMismatch));
		Check("读码 1→OK", Tha26003.ScanResultText(1) == "OK", Tha26003.ScanResultText(1));
		Check("读码 2→NG", Tha26003.ScanResultText(2) == "NG", Tha26003.ScanResultText(2));
		Check("PLC 只写 1/2：重码/错码/未扫到/枪异常/MES 异常全折算 2",
			Tha26003.ScanPlcCode(ScanResultCode.Ok) == 1
			&& Tha26003.ScanPlcCode(ScanResultCode.NoRead) == 2
			&& Tha26003.ScanPlcCode(ScanResultCode.Duplicate) == 2
			&& Tha26003.ScanPlcCode(ScanResultCode.BadFormat) == 2
			&& Tha26003.ScanPlcCode(ScanResultCode.CommError) == 2
			&& Tha26003.ScanPlcCode(ScanResultCode.InternalError) == 2,
			$"{Tha26003.ScanPlcCode(ScanResultCode.Ok)}/{Tha26003.ScanPlcCode(ScanResultCode.Duplicate)}");
		Check("内部细分原因仍可读（弹窗用）", Tha26003.ScanReasonText(ScanResultCode.Duplicate) == "重码",
			Tha26003.ScanReasonText(ScanResultCode.Duplicate));
		Check("上传 2→失败（只有落库成功/失败两值）", Tha26003.UploadResultText(2) == "失败" && Tha26003.UploadResultText(1) == "OK");
		Check("未完成 0→--", Tha26003.MarkResultText(0) == "--" && Tha26003.ScanResultText(0) == "--");
		Check("结果字 1/2→OK/NG", Tha26003.ResultText(1) == "OK" && Tha26003.ResultText(2) == "NG");
	}

	// ---------- 辅助 ----------

	/// <summary>可空数值近似比较。</summary>
	private static bool Near(double? v, double expected, double tol = 1e-6)
		=> v.HasValue && Math.Abs(v.Value - expected) < tol;

	/// <summary>可空数值显示（断言失败信息用）。</summary>
	private static string S(double? v) => v?.ToString("F6") ?? "null";

	/// <summary>写入 WORD（大端）。</summary>
	private static void WriteWord(byte[] buf, int wordOffset, int value)
	{
		buf[wordOffset * 2] = (byte)((value >> 8) & 0xFF);
		buf[wordOffset * 2 + 1] = (byte)(value & 0xFF);
	}

	/// <summary>写入 DINT（高字在前，Modbus 大端标准）。</summary>
	private static void WriteInt32HiLo(byte[] buf, int wordOffset, int value)
	{
		WriteWord(buf, wordOffset, (value >> 16) & 0xFFFF);
		WriteWord(buf, wordOffset + 1, value & 0xFFFF);
	}

	/// <summary>写入 DINT（低字在前）。</summary>
	private static void WriteInt32LoHi(byte[] buf, int wordOffset, int value)
	{
		WriteWord(buf, wordOffset, value & 0xFFFF);
		WriteWord(buf, wordOffset + 1, (value >> 16) & 0xFFFF);
	}

	/// <summary>写入单精度浮点，字序 ABCD（寄存器 0 = 高 16 位 A,B）。</summary>
	private static void WriteFloatAbcd(byte[] buf, int wordOffset, float value)
	{
		var be = BitConverter.GetBytes(value);
		Array.Reverse(be);   // [A,B,C,D]
		buf[wordOffset * 2] = be[0];
		buf[wordOffset * 2 + 1] = be[1];
		buf[wordOffset * 2 + 2] = be[2];
		buf[wordOffset * 2 + 3] = be[3];
	}

	/// <summary>写入单精度浮点，字序 CDAB（寄存器 0 = 低 16 位，寄存器 1 = 高 16 位）。</summary>
	private static void WriteFloatCdab(byte[] buf, int wordOffset, float value)
	{
		var be = BitConverter.GetBytes(value);          // 本机小端 [D,C,B,A]
		Array.Reverse(be);                              // 大端 [A,B,C,D]
		buf[wordOffset * 2] = be[2];                    // C
		buf[wordOffset * 2 + 1] = be[3];                // D
		buf[wordOffset * 2 + 2] = be[0];                // A
		buf[wordOffset * 2 + 3] = be[1];                // B
	}


	// ---------- 自检 10. 每日追溯报表 / 本地数据保留 ----------

	/// <summary>
	/// 每日报表契约：文件名规则、当日追加、重复不重、零点自动新建、迟到记录重建、保留期删除。
	/// 用独立临时目录，不写生产报表目录。
	/// </summary>
	private static void TestDailyReport()
	{
		Console.WriteLine("[10] 每日追溯报表（自然日文件 / 零点新建 / 追加 / 保留期）");

		var dir = Path.Combine(AppContext.BaseDirectory, "AppData", "_selftest_reports");
		try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 忽略 */ }

		var lateCode = "RPTSELFTEST_LATE";
		try
		{
			// 自检用 2 秒节奏（生产默认 60 秒），下面会验证"不调用 FlushNow 也会自动落盘"
			ThaDailyReport.InitForTest(dir, flushSeconds: 2);

			// 1) 文件名规则
			var sample = Path.GetFileName(ThaDailyReport.FilePathFor(new DateTime(2026, 1, 2, 15, 4, 5)));
			Check("文件名 = 追溯报表_YYYYMMDD.xlsx", sample == "追溯报表_20260102.xlsx", sample);

			// 2) 当日记录追加 + 落盘
			var today = DateTime.Today;
			var recs = new List<ThaProductRecord>();
			for (var i = 0; i < 3; i++)
			{
				var r = new ThaProductRecord
				{
					Id = SnowFlakeSingle.Instance.NextId(),
					ProductCode = $"RPTSELFTEST_{i:D2}",
					OverallResult = 1,
					UploadResult = 1,
					UploadState = 1,
					CreateTime = DateTime.Now,
					UpdateTime = DateTime.Now,
					TopPinHeight1 = 1.234,
				};
				recs.Add(r);
				_testCodes.Add(r.ProductCode!);
				ThaDailyReport.Append(r);
			}

			Check("同步落盘返回成功", ThaDailyReport.FlushNow());
			var todayPath = ThaDailyReport.FilePathFor(today);
			Check("当日报表文件已生成", File.Exists(todayPath), todayPath);

			var rows = MiniExcel.Query(todayPath, useHeaderRow: true).ToList();
			Check("追加 3 条 → 文件 3 行", rows.Count == 3, $"得到 {rows.Count}");
			var first = rows.Count > 0 ? (IDictionary<string, object>)rows[0] : null;
			Check("首行数据 = 首条记录", first?["产品码"]?.ToString() == "RPTSELFTEST_00",
				first?["产品码"]?.ToString() ?? "(空)");

			// useHeaderRow:false 时字典键是 A/B/C…，单元格文本才是表头，需看 Values
			var header = MiniExcel.Query(todayPath, useHeaderRow: false).First() as IDictionary<string, object>;
			var headerValues = header?.Values.Select(v => v?.ToString()).ToList() ?? new List<string?>();
			Check("表头第一列 = 采集时间，且含产品码/镭码内容/总结果/扫码结果",
				headerValues.FirstOrDefault() == "采集时间" && headerValues.Contains("产品码")
				&& headerValues.Contains("镭码内容")
				&& headerValues.Contains("总结果") && headerValues.Contains("扫码结果"),
				string.Join("/", headerValues.Take(7)));
			// 列位置：镭码内容紧跟产品码（导出/报表与界面列顺序一致）
			Check("镭码内容紧跟在产品码右侧",
				headerValues.IndexOf("镭码内容") == headerValues.IndexOf("产品码") + 1,
				$"产品码@{headerValues.IndexOf("产品码")} 镭码内容@{headerValues.IndexOf("镭码内容")}");
			Check("表头不含 记录ID / 上传结果 / 创建时间",
				!headerValues.Contains("记录ID") && !headerValues.Contains("上传结果") && !headerValues.Contains("创建时间"),
				string.Join("/", headerValues.Where(v => v is "记录ID" or "上传结果" or "创建时间")));
			Check("采集时间已填（= 落库时间）",
				!string.IsNullOrWhiteSpace(first?["采集时间"]?.ToString()), first?["采集时间"]?.ToString() ?? "(空)");

			// 3) 重复 Append 幂等（同一雪花 Id 不重复写）
			ThaDailyReport.Append(recs[0]);
			Check("重复 Append 后同步落盘", ThaDailyReport.FlushNow());
			rows = MiniExcel.Query(todayPath, useHeaderRow: true).ToList();
			Check("重复 Append 不产生重复行", rows.Count == 3, $"得到 {rows.Count}");

			// 3b) 定时落盘：不再手动 Flush，等待超过写盘间隔（2 秒）后文件应自动多出一行
			var auto = new ThaProductRecord
			{
				Id = SnowFlakeSingle.Instance.NextId(),
				ProductCode = "RPTSELFTEST_AUTO",
				OverallResult = 1,
				UploadResult = 1,
				UploadState = 1,
				CreateTime = DateTime.Now,
				UpdateTime = DateTime.Now,
			};
			_testCodes.Add(auto.ProductCode!);
			ThaDailyReport.Append(auto);
			Thread.Sleep(2800);
			rows = MiniExcel.Query(todayPath, useHeaderRow: true).ToList();
			Check("定时落盘：间隔到期自动追加（未手动 Flush）", rows.Count == 4, $"得到 {rows.Count}");

			// 4) 零点自动新建次日文件（即使 0 条记录）
			var tomorrow = today.AddDays(1);
			ThaDailyReport.RotateForTest(tomorrow);
			var tomorrowPath = ThaDailyReport.FilePathFor(tomorrow);
			Check("零点自动新建次日文件", File.Exists(tomorrowPath), tomorrowPath);
			var tomorrowRows = MiniExcel.Query(tomorrowPath, useHeaderRow: true).ToList();
			Check("次日文件是表头空表（0 行数据）", tomorrowRows.Count == 0, $"得到 {tomorrowRows.Count}");

			// 5) 非当日迟到记录 → 按库重建该日文件（权威数据在 SQLite）
			if (ThaDb.IsInitialized)
			{
				var late = new ThaProductRecord
				{
					Id = SnowFlakeSingle.Instance.NextId(),
					ProductCode = lateCode,
					OverallResult = 2,
					UploadResult = 1,
					UploadState = 1,
					CreateTime = today.AddDays(-1).AddHours(23),
					UpdateTime = DateTime.Now,
				};
				ThaDb.Db.Insertable(late).ExecuteCommand();
				ThaDailyReport.Append(late);
				Check("迟到记录触发同步落盘", ThaDailyReport.FlushNow());
				var yesterdayPath = ThaDailyReport.FilePathFor(today.AddDays(-1));
				Check("迟到记录所属自然日文件已重建", File.Exists(yesterdayPath), yesterdayPath);
				var yRows = MiniExcel.Query(yesterdayPath, useHeaderRow: true).ToList();
				Check("重建文件包含迟到记录",
					yRows.Any(x => ((IDictionary<string, object>)x)["产品码"]?.ToString() == lateCode),
					$"共 {yRows.Count} 行");
			}

			// 6) 退出前检查 + 清理（过期报表文件删除，当期文件保留）
			Check("保留期设为 0 → HasExpired 恒为 false", !ThaDataRetention.HasExpired(0, dir));
			Check("没有过期数据 → HasExpired=false", !ThaDataRetention.HasExpired(3650, dir));

			var expiredPath = ThaDailyReport.FilePathFor(today.AddDays(-31));
			File.WriteAllText(expiredPath, "stale");
			var keepPath = ThaDailyReport.FilePathFor(today.AddDays(-29));
			File.WriteAllText(keepPath, "keep");
			Check("存在 31 天前的报表 → HasExpired=true", ThaDataRetention.HasExpired(30, dir));

			var progress = new SyncProgress();
			var purge = ThaDataRetention.Purge(30, dir, progress);
			Check("超过 30 天的报表文件被删除", !File.Exists(expiredPath), expiredPath);
			Check("保留期内的报表文件保留", File.Exists(keepPath), keepPath);
			Check("当日报表不受保留期清理影响", File.Exists(todayPath), todayPath);
			Check("清理进度有阶段反馈（弹窗文案来源）", progress.Messages.Count >= 5,
				string.Join(" | ", progress.Messages));

			// 8) 清理完成后再检查：本次清干净了 → HasExpired=false（下次退出不再弹窗）
			Check("清理后无过期数据 → HasExpired=false", !ThaDataRetention.HasExpired(30, dir));

			// 7) 数据库保留期：40 天前的产品记录被删除
			if (ThaDb.IsInitialized)
			{
				var old = new ThaProductRecord
				{
					Id = SnowFlakeSingle.Instance.NextId(),
					ProductCode = "RPTSELFTEST_OLD",
					CreateTime = DateTime.Now.AddDays(-40),
					UpdateTime = DateTime.Now.AddDays(-40),
					UploadState = 1,
				};
				ThaDb.Db.Insertable(old).ExecuteCommand();
				ThaDataRetention.Purge(30, dir);
				var exists = ThaDb.Db.Queryable<ThaProductRecord>().Any(x => x.Id == old.Id);
				Check("40 天前的产品记录被删除", !exists, old.Id.ToString());
			}
		}
		finally
		{
			ThaDailyReport.Shutdown();
			try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
			try
			{
				if (ThaDb.IsInitialized)
					ThaDb.Db.Deleteable<ThaProductRecord>().Where(x => x.ProductCode == lateCode).ExecuteCommand();
			}
			catch { /* 忽略 */ }
		}
	}

	/// <summary>同步进度收集器（自检无 UI 同步上下文，Progress&lt;T&gt; 回调可能来不及执行）。</summary>
	private sealed class SyncProgress : IProgress<string>
	{
		public List<string> Messages { get; } = new();

		public void Report(string value)
		{
			lock (Messages) Messages.Add(value);
		}
	}

	/// <summary>清理自检写入的测试数据，避免污染现场库。</summary>
	private static void CleanupTestRows()
	{
		if (!ThaDb.IsInitialized || _testCodes.Count == 0) return;
		try
		{
			foreach (var c in _testCodes.Distinct())
			{
				ThaDb.Db.Deleteable<ThaProductRecord>().Where(x => x.ProductCode == c).ExecuteCommand();
				ThaDb.Db.Deleteable<ThaCodeIndexRow>().Where(x => x.Code == c).ExecuteCommand();
				ThaDb.Db.Deleteable<ThaMarkLog>().Where(x => x.Content == c).ExecuteCommand();
				ThaDb.Db.Deleteable<ThaScanLog>().Where(x => x.CodeNormalized == c).ExecuteCommand();
			}
			Console.WriteLine($"  已清理自检测试数据 {_testCodes.Distinct().Count()} 条");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"  清理测试数据失败（不影响结论）：{ex.Message}");
		}
	}
}
