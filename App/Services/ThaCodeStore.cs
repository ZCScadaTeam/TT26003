using AppZC.Models.DB;

namespace AppZC.Services;

/// <summary>
/// 码索引访问层（ThaCodeIndex 表）：全量重码比对的唯一权威。
/// 表主键就是码 → 数据库层面硬约束，配合查询做"先查后插"的双保险。
///
/// 状态机：0=已打码 → 1=已读码 → 2=已上传；3=NG 冻结。
/// 读码站判定必须带状态，否则复检产品的自身码会被误判重码（见方案 §4.2）。
///
/// 关于「是否复检」：协议地址表该栏为「暂无」（PLC 不提供），MES 侧用本表的
/// **同一码在在制状态下重复经过读码站的次数**（RetestCount）替代：
///   0 = 未复检（首次读码即通过）→ 是否复检 = 否
///   ≥1 = 复检/重入          → 是否复检 = 是
/// </summary>
public static class ThaCodeStore
{
	/// <summary>
	/// 码索引临界区锁。打码站与读码站现在是**并行**处理（各自独立作业循环），
	/// 「查存在 → 登记」「判重码 → 标记已读」这两段必须先查后改原子化，
	/// 否则扫码可能读到打码刚插入但尚未提交的半状态，把好码判成"未登记"。
	/// 锁只保护码索引，不覆盖打码机/扫码枪的 IO（那是设备层自己的锁）。
	/// </summary>
	private static readonly object Gate = new();

	/// <summary>码是否已被占用（打码站生成前的唯一性预检）。</summary>
	public static bool Exists(string code)
	{
		if (!ThaDb.IsInitialized) return false;
		lock (Gate)
		{
			try
			{
				return ThaDb.Db.Queryable<ThaCodeIndexRow>().Any(x => x.Code == code);
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"[THA-CODE] 查询码是否存在失败: {ex.Message}", ex);
				return false;
			}
		}
	}

	/// <summary>登记打码结果（状态 = 已打码）。返回 false 表示数据库中已存在同码。</summary>
	public static bool TryRegisterMarked(string code, string content, DateTime markTime)
	{
		if (!ThaDb.IsInitialized) return false;
		lock (Gate)
		{
			try
			{
				if (Exists(code)) return false;
				ThaDb.Db.Insertable(new ThaCodeIndexRow
				{
					Code = code,
					Status = (int)CodeStatus.Marked,
					Content = content,
					MarkTime = markTime,
					RetestCount = 0,
				}).ExecuteCommand();
				return true;
			}
			catch (Exception ex)
			{
				// 唯一主键冲突也会走到这里：说明并发下有同码插入，按失败处理
				LogMgr.Instance.Error($"[THA-CODE] 登记打码记录失败 {code}: {ex.Message}", ex);
				return false;
			}
		}
	}

	/// <summary>读码站判定结果。</summary>
	public sealed record ScanDecision(ScanResultCode Result, string? Reason, int RetestCount);

	/// <summary>
	/// 读码站重码/复检判定（核心逻辑）。
	/// - 无记录：未登记码 → 默认判错码；<paramref name="allowUnregistered"/> 为 true 时放行（兼容外来码）
	/// - 状态 0/1 且在制未超时 → OK，复算次数 +1（复检/重入）
	/// - 状态 0/1 但在制已超时 → 重码（防卡死的僵尸码被复用）
	/// - 状态 2（已上传）/3（NG 冻结） → 重码
	/// </summary>
	public static ScanDecision EvaluateForScan(string code, int wipTimeoutHours, bool allowUnregistered)
	{
		if (!ThaDb.IsInitialized)
			return new ScanDecision(ScanResultCode.InternalError, "数据库未初始化", 0);

		lock (Gate)
		{
			try
			{
				var row = ThaDb.Db.Queryable<ThaCodeIndexRow>().First(x => x.Code == code);
				if (row == null)
				{
					return allowUnregistered
						? new ScanDecision(ScanResultCode.Ok, "未登记码（配置放行）", 0)
						: new ScanDecision(ScanResultCode.BadFormat, "未登记码", 0);
				}

				if (row.Status is (int)CodeStatus.Uploaded or (int)CodeStatus.NgLocked)
				{
					var why = row.Status == (int)CodeStatus.Uploaded ? "已上传" : "NG冻结";
					return new ScanDecision(ScanResultCode.Duplicate, $"重码({why})", row.RetestCount);
				}

				if (wipTimeoutHours > 0 &&
					(DateTime.Now - row.MarkTime).TotalHours > wipTimeoutHours)
				{
					return new ScanDecision(ScanResultCode.Duplicate,
						$"重码(在制超时>{wipTimeoutHours}h)", row.RetestCount);
				}

				// 复检计数：只有"同一码已读过一次又再读"才算复检/重入。
				// 首次读码（状态=已打码）计数保持 0 —— 复检次数 = 0 即"是否复检 = 否"。
				var isReentry = row.Status == (int)CodeStatus.Scanned;
				var retest = isReentry ? row.RetestCount + 1 : row.RetestCount;
				var reason = isReentry
					? $"复检/重入(第 {retest} 次，已读码未上传)"
					: "首次读码(已打码未读码)";
				return new ScanDecision(ScanResultCode.Ok, reason, retest);
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"[THA-CODE] 读码判定失败 {code}: {ex.Message}", ex);
				return new ScanDecision(ScanResultCode.InternalError, ex.Message, 0);
			}
		}
		}


	/// <summary>
	/// 当天流水号（产品批次号 / Serial 码段用）：**本生产日窗口内**已登记的打码条数 + 1，从 1 开始。
	/// 生产日窗口由 <paramref name="resetHour"/> 决定：默认每天 8 点清零（客户确认的触点元件规则），
	/// 即 00:00-07:59 打码的产品仍算前一天、流水继续累加，08:00 第一件重新从 1 开始。
	/// 基于已落库的码索引，所以进程重启后仍然单调；作业线程只有一个消费者，无并发问题。
	/// </summary>
	public static int NextDailySerial(int resetHour = ThaProductionDay.DefaultResetHour)
	{
		if (!ThaDb.IsInitialized) return 1;
		lock (Gate)
		{
			try
			{
				var (start, end) = ThaProductionDay.Window(DateTime.Now, resetHour);
				var count = ThaDb.Db.Queryable<ThaCodeIndexRow>()
					.Count(x => x.MarkTime >= start && x.MarkTime < end);
				return count + 1;
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[THA-CODE] 取当天流水失败（按 1 处理）：{ex.Message}");
				return 1;
			}
		}
		}


	/// <summary>补读回填结果。</summary>
	/// <param name="Filled">是否已回填（true = 本次读码是“补读”，可放行不判重码）。</param>
	/// <param name="OldOverall">回填前记录里的总结果（供调用方调增减计数）。</param>
	/// <param name="NewOverall">回填后的总结果。</param>
	/// <param name="Record">回填后的完整记录（供 UI 刷行 / 事件）。</param>
	/// <param name="Reason">不可回填的原因（Filled=false 时可用于日志）。</param>
	public sealed record LateScanFillResult(bool Filled, int? OldOverall, int? NewOverall, ThaProductRecord? Record, string? Reason);

	/// <summary>
	/// 补读回填：现场时序是「打码 → 采集落库 → 读码」（读码稳定晚于采集 ~1.5s），
	/// 落库那一刻读码上下文还是空的，所以产品记录的扫码结果/码等级是 null 或上一件的残值。
	/// 本方法让读码站读到结果后，把本码对应的那条记录回填并重算总结果。
	///
	/// 只有同时满足下列条件才回填（否则交回原重码判定，不当补读）：
	/// <list type="number">
	/// <item>码索引状态 = 已上传，且带 ProductId；</item>
	/// <item>该记录的落库时间在 <paramref name="windowSeconds"/> 秒窗口内（大窗口才能防真重码）；</item>
	/// <item>该记录现有的扫码原文**不属于本码**（为空，或拆出的码 ≠ 本码）—— 已经是本码就不得再填。</item>
	/// </list>
	/// 回填只改扫码结果/扫码原文/码等级/总结果/更新时间，不动产品码与测量值。
	/// </summary>
	public static LateScanFillResult TryFillLateScan(
		string code,
		int? scanResult,
		string? scanRaw,
		string? codeLevel,
		string? codePattern,
		int windowSeconds)
	{
		var none = new LateScanFillResult(false, null, null, null, null);
		if (!ThaDb.IsInitialized) return none with { Reason = "数据库未初始化" };
		if (windowSeconds <= 0) return none with { Reason = "补读回填已关闭(LateScanFillSeconds=0)" };
		if (string.IsNullOrWhiteSpace(code)) return none with { Reason = "码为空" };

		lock (Gate)
		{
			try
			{
				var idx = ThaDb.Db.Queryable<ThaCodeIndexRow>().First(x => x.Code == code);
				if (idx == null) return none with { Reason = "无码索引" };
				if (idx.Status != (int)CodeStatus.Uploaded) return none with { Reason = "码状态不是已上传" };
				if (idx.ProductId is not { } productId) return none with { Reason = "码索引未回填 ProductId" };

				var rec = ThaDb.Db.Queryable<ThaProductRecord>().InSingle(productId);
				if (rec == null) return none with { Reason = "产品记录不存在" };

				var age = (DateTime.Now - rec.CreateTime).TotalSeconds;
				if (age > windowSeconds)
					return none with { Reason = $"超过补读窗口({age:F0}s>{windowSeconds}s)" };

				// 该记录的扫码原文已属于本码 → 已有真实扫码结果，不得再填（再读就是真重码）
				var currentRaw = ThaScanCode.Parse(ThaCodeValidator.Normalize(rec.ScanCodeRaw), codePattern).Code;
				if (string.Equals(currentRaw, code, StringComparison.Ordinal))
					return none with { Reason = "该记录已有本码扫码结果" };

				var oldOverall = rec.OverallResult;
				var newOverall = Tha26003.ComputeOverall(
					rec.WithstandResult, rec.PinHeightResult, rec.PositionResult, scanResult);

				// 产品码也要回填：产品码的唯一来源是扫码，落库时未扫上就为空，补读后要补上
				var newProductCode = string.IsNullOrWhiteSpace(rec.ProductCode) ? code : rec.ProductCode;

				ThaDb.Db.Updateable<ThaProductRecord>()
					.SetColumns(x => new ThaProductRecord
					{
						ProductCode = newProductCode,
						ScanResult = scanResult,
						ScanCodeRaw = scanRaw,
						ScanCodeLevel = codeLevel,
						OverallResult = newOverall,
						UpdateTime = DateTime.Now,
					})
					.Where(x => x.Id == productId)
					.ExecuteCommand();

				rec.ProductCode = newProductCode;
				rec.ScanResult = scanResult;
				rec.ScanCodeRaw = scanRaw;
				rec.ScanCodeLevel = codeLevel;
				rec.OverallResult = newOverall;
				rec.UpdateTime = DateTime.Now;

				return new LateScanFillResult(true, oldOverall, newOverall, rec, null);
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"[THA-CODE] 补读回填失败 {code}: {ex.Message}", ex);
				return none with { Reason = $"回填异常：{ex.Message}" };
			}
		}
	}

	/// <summary>登记读码通过（状态 = 已读码）。</summary>
	public static void MarkScanned(string code, DateTime scanTime, int retestCount)
	{
		if (!ThaDb.IsInitialized) return;
		lock (Gate)
		{
			try
			{
				ThaDb.Db.Updateable<ThaCodeIndexRow>()
					.SetColumns(x => new ThaCodeIndexRow
					{
						Status = (int)CodeStatus.Scanned,
						ScanTime = scanTime,
						RetestCount = retestCount,
					})
					.Where(x => x.Code == code)
					.ExecuteCommand();
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"[THA-CODE] 登记读码结果失败 {code}: {ex.Message}", ex);
			}
		}
		}


	/// <summary>登记上传完成（状态 = 已上传 + 回填产品记录 Id）。</summary>
	public static void MarkUploaded(string code, long productId, DateTime unloadTime)
	{
		if (!ThaDb.IsInitialized) return;
		try
		{
			ThaDb.Db.Updateable<ThaCodeIndexRow>()
				.SetColumns(x => new ThaCodeIndexRow
				{
					Status = (int)CodeStatus.Uploaded,
					ProductId = productId,
					UploadTime = unloadTime,
				})
				.Where(x => x.Code == code)
				.ExecuteCommand();
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-CODE] 登记上传结果失败 {code}: {ex.Message}", ex);
		}
	}

	/// <summary>补登记一个未登记码（上传时配置为允许接收外来码时使用）。</summary>
	public static void RegisterExternal(string code, DateTime time)
	{
		if (!ThaDb.IsInitialized) return;
		try
		{
			if (Exists(code)) return;
			ThaDb.Db.Insertable(new ThaCodeIndexRow
			{
				Code = code,
				Status = (int)CodeStatus.Scanned,
				Content = null,
				MarkTime = time,
				ScanTime = time,
			}).ExecuteCommand();
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-CODE] 补登记外部码失败 {code}: {ex.Message}", ex);
		}
	}

	/// <summary>查状态（上传站用于识别"已上传"的重复触发）。</summary>
	public static CodeStatus? GetStatus(string code)
	{
		if (!ThaDb.IsInitialized) return null;
		try
		{
			var row = ThaDb.Db.Queryable<ThaCodeIndexRow>().First(x => x.Code == code);
			return row == null ? null : (CodeStatus)row.Status;
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-CODE] 查询码状态失败 {code}: {ex.Message}", ex);
			return null;
		}
	}
}
