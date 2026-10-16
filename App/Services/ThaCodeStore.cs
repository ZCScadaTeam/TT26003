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
	/// <summary>码是否已被占用（打码站生成前的唯一性预检）。</summary>
	public static bool Exists(string code)
	{
		if (!ThaDb.IsInitialized) return false;
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

	/// <summary>登记打码结果（状态 = 已打码）。返回 false 表示数据库中已存在同码。</summary>
	public static bool TryRegisterMarked(string code, string content, DateTime markTime)
	{
		if (!ThaDb.IsInitialized) return false;
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

	/// <summary>登记读码通过（状态 = 已读码）。</summary>
	public static void MarkScanned(string code, DateTime scanTime, int retestCount)
	{
		if (!ThaDb.IsInitialized) return;
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
