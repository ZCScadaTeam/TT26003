using System.Text;
using AppZC.Models.DB;

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
			TestValidator();
			TestCodeIndexStateMachine();
			TestMeasurementParse();
			TestProductRecordInsertSemantics();
			TestOverallResult();
			TestResultTextMapping();
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
		// 针高：PLC 侧 DINT（÷1000）—— 2115 → 2.115
		for (int i = 0; i < Tha26003.PinHeightCount; i++)
		{
			WriteInt32HiLo(segTopPin, i * 2, 2115 + i);
			WriteInt32HiLo(segBotPin, i * 2, 3446 + i);
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

		var m = Tha26003.ParseMeasurement(segResult, segTopPin, segBotPin, segTopPos, segBotPos, PinHeightFormat.Int32Div1000, PositionFormat.FloatCdab);

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
		var mNull = Tha26003.ParseMeasurement(null, null, null, null, null, PinHeightFormat.Int32Div1000, PositionFormat.FloatCdab);
		Check("整段为 null → 结果字与针高全为 null",
			mNull.WithstandResult == null && mNull.PinHeightResult == null && mNull.PositionResult == null
			&& mNull.TopPinHeights.All(v => v == null) && mNull.TopPosX.All(v => v == null),
			"sanity");
		Check("全 null 不算脏数据（不判数据 NG）", Tha26003.IsMeasurementSane(mNull));
		Check("全 null 时总结果无法判定 → null",
			Tha26003.ComputeOverall(1, 1, null, null, null) == null,
			$"{Tha26003.ComputeOverall(1, 1, null, null, null)}");

		// 结果字读到 0（未采集）→ 归一为 null
		var segZero = new byte[Tha26003.SegResultLen * 2];
		var mZero = Tha26003.ParseMeasurement(segZero, segTopPin, segBotPin, segTopPos, segBotPos, PinHeightFormat.Int32Div1000, PositionFormat.FloatCdab);
		Check("结果字读到 0 → 归一为 null（未采集）",
			mZero.WithstandResult == null && mZero.PinHeightResult == null && mZero.PositionResult == null);

		// REAL 段全 0xFF = IEEE754 NaN → 落 NULL
		var dirtyPos = new byte[Tha26003.SegTopPosLen * 2];
		Array.Fill(dirtyPos, (byte)0xFF);
		var m2 = Tha26003.ParseMeasurement(segResult, segTopPin, segBotPin, dirtyPos, segBotPos,
			PinHeightFormat.Int32Div1000, PositionFormat.FloatCdab);
		Check("位置度全 0xFF（REAL NaN）→ 全部落 NULL", m2.TopPosX.All(v => v == null), "sanity");
		Check("NULL 不等于脏值（不判数据 NG，仍落库为 NULL）", Tha26003.IsMeasurementSane(m2));

		// DINT 段全 0xFF = -1 → -0.001（DINT 没有 NaN 语义，'未采集'只能靠整段读失败或结果字判断）
		var dirtyPin = new byte[Tha26003.SegTopPinLen * 2];
		Array.Fill(dirtyPin, (byte)0xFF);
		var m2b = Tha26003.ParseMeasurement(segResult, dirtyPin, segBotPin, segTopPos, segBotPos,
			PinHeightFormat.Int32Div1000, PositionFormat.FloatCdab);
		Check("针高全 0xFF（DINT -1）→ -0.001（DINT 无 NaN，由结果字/整段失败兜底）",
			Near(m2b.TopPinHeights[0], -0.001), S(m2b.TopPinHeights[0]));

		// 针高口径可切字序（低字在前）
		var swapped = new byte[Tha26003.SegTopPinLen * 2];
		WriteInt32LoHi(swapped, 0, 2115);
		WriteInt32LoHi(swapped, 2, 3446);
		var m4 = Tha26003.ParseMeasurement(segResult, swapped, segBotPin, segTopPos, segBotPos,
			PinHeightFormat.Int32SwappedDiv1000, PositionFormat.FloatCdab);
		Check("低字在前口径：2115 → 2.115", Near(m4.TopPinHeights[0], 2.115, 1e-9), S(m4.TopPinHeights[0]));
		Check("低字在前口径：3446 → 3.446", Near(m4.TopPinHeights[1], 3.446, 1e-9), S(m4.TopPinHeights[1]));

		// 位置度口径可切字序（ABCD）
		var abcd = new byte[Tha26003.SegTopPosLen * 2];
		WriteFloatAbcd(abcd, 0, 0.100f);
		var m5 = Tha26003.ParseMeasurement(segResult, segTopPin, segBotPin, abcd, segBotPos,
			PinHeightFormat.Int32Div1000, PositionFormat.FloatAbcd);
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
			MarkResult = (int)MarkResultCode.Ok,
			ReadResult = (int)ScanResultCode.Ok,
			MarkTime = DateTime.Now,
			ScanTime = DateTime.Now,
			OverallResult = 1,
			UploadResult = (int)UploadResultCode.Ok,
			WithstandResult = 1,
			PinHeightResult = 1,
			PositionResult = 1,
			TopPinHeight1 = 2.115,
			BottomPinHeight15 = 3.446,
			TopPinPosX15 = 0.24,
			BottomPinPosY15 = 1.34,
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
			MarkResult = (int)MarkResultCode.Ok,
			ReadResult = (int)ScanResultCode.Duplicate,
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

		// 未采集字段落库应为 NULL，而不是 0
		var recNull = new ThaProductRecord { ProductCode = code + "N", MarkResult = 1 };
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
		Console.WriteLine("[8] 总结果算法（其余结果里没有 NG(2) → OK(1)）");

		// 全 OK → OK
		Check("全 OK → 1",
			Tha26003.ComputeOverall(1, 1, 1, 1, 1) == 1);

		// 任一结果字为 2 → NG
		Check("耐压 NG → 2", Tha26003.ComputeOverall(1, 1, 2, 1, 1) == 2);
		Check("针高 NG → 2", Tha26003.ComputeOverall(1, 1, 1, 2, 1) == 2);
		Check("位置度 NG → 2", Tha26003.ComputeOverall(1, 1, 1, 1, 2) == 2);

		// 打码/读码非 OK → NG（重码 3 / 错码 4 / 枪异常 5 不能被"没有 2 就 OK"放过）
		Check("打码 NG(2) → 2", Tha26003.ComputeOverall(2, 1, 1, 1, 1) == 2);
		Check("读码 未扫到(2) → 2", Tha26003.ComputeOverall(1, 2, 1, 1, 1) == 2);
		Check("读码 重码(3) → 2", Tha26003.ComputeOverall(1, 3, 1, 1, 1) == 2);
		Check("读码 错码(4) → 2", Tha26003.ComputeOverall(1, 4, 1, 1, 1) == 2);
		Check("打码 回执不符(6) → 2", Tha26003.ComputeOverall(6, 1, 1, 1, 1) == 2);

		// 未采集/未赋值（0）不算 NG → 按"没有 2 就 OK"口径
		Check("结果字未采集(0) → 归一为 NULL（无法判定，不猜 OK）", Tha26003.ComputeOverall(1, 1, 0, 0, 0) == null,
			$"{Tha26003.ComputeOverall(1, 1, 0, 0, 0)}");
	}

	// ---------- 9. 结果码映射 ----------

	private static void TestResultTextMapping()
	{
		Console.WriteLine("[9] 结果码文本映射（PLC 侧 1~6 与 UI 显示一致）");
		Check("打码 1→OK", Tha26003.MarkResultText(1) == "OK");
		Check("打码 6→回执不符", Tha26003.MarkResultText(6) == "回执不符", Tha26003.MarkResultText(6));
		Check("读码 2→未扫到", Tha26003.ScanResultText(2) == "未扫到", Tha26003.ScanResultText(2));
		Check("读码 3→重码", Tha26003.ScanResultText(3) == "重码");
		Check("读码 4→错码", Tha26003.ScanResultText(4) == "错码");
		Check("上传 3→失败", Tha26003.UploadResultText(3) == "失败");
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
