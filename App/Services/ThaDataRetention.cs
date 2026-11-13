using AppZC.Models.DB;
using SqlSugar;

namespace AppZC.Services;

/// <summary>
/// 本地数据保留期清理（默认 30 天）：产品记录 / 打码流水 / 读码流水 / 码索引 / 每日报表文件。
///
/// 触发点：**程序退出时**先 <see cref="HasExpired"/> 快速检查，有旧数据才弹窗并调用 <see cref="Purge"/>；
/// 没有旧数据直接退出，不打扰操作员。
/// 删除按主键分批（每批 2000 行），避免大事务长时间锁住 SQLite 写锁影响上传站落库。
/// <c>keepDays &lt;= 0</c> 表示永不删除。
/// </summary>
public static class ThaDataRetention
{
	/// <summary>单批删除行数。</summary>
	private const int BatchSize = 2000;

	/// <summary>清理防重入（启动与零点可能撞在一起）。</summary>
	private static readonly SemaphoreSlim Gate = new(1, 1);

	/// <summary>清理结果统计。</summary>
	public readonly record struct PurgeResult(
		int ProductRecords,
		int MarkLogs,
		int ScanLogs,
		int CodeIndexes,
		int ReportFiles)
	{
		public bool IsEmpty => ProductRecords + MarkLogs + ScanLogs + CodeIndexes + ReportFiles == 0;

		public override string ToString()
			=> $"产品记录 {ProductRecords} 行 / 打码流水 {MarkLogs} 行 / 读码流水 {ScanLogs} 行 / 码索引 {CodeIndexes} 行 / 报表文件 {ReportFiles} 个";
	}

	/// <summary>
	/// 退出前快速检查：是否存在超过保留期的数据（库内 4 张表 + 报表文件）。
	/// 只做存在性判断（LIMIT 1 / 文件名解析），不加载实体；<c>keepDays &lt;= 0</c> 恒为 false。
	/// 检查失败时返回 false（宁可正常退出，也不把退出流程卡住）。
	/// </summary>
	public static bool HasExpired(int keepDays, string? reportDirectory)
	{
		if (keepDays <= 0) return false;
		var cutoff = DateTime.Now.AddDays(-keepDays);

		try
		{
			if (ThaDb.IsInitialized)
			{
				using var db = ThaDb.NewClient();
				if (db.Queryable<ThaProductRecord>().Any(x => x.CreateTime < cutoff)) return true;
				if (db.Queryable<ThaMarkLog>().Any(x => x.RequestTime < cutoff)) return true;
				if (db.Queryable<ThaScanLog>().Any(x => x.CreateTime < cutoff)) return true;
				if (db.Queryable<ThaCodeIndexRow>().Any(x => x.MarkTime < cutoff)) return true;
			}
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Warn($"[THA-RETENTION] 退出前检查旧数据失败（视为无需清理）：{ex.Message}");
			return false;
		}

		return HasExpiredReportFile(keepDays, reportDirectory);
	}

	/// <summary>
	/// 删除早于 <paramref name="keepDays"/> 天的本地数据。
	/// <paramref name="progress"/> 用于退出弹窗实时显示当前清理的表/阶段（可空）。
	/// 返回实际删除量；库未初始化或参数为 0 时不做任何事。
	/// </summary>
	public static PurgeResult Purge(int keepDays, string? reportDirectory, IProgress<string>? progress = null)
	{
		if (keepDays <= 0 || !ThaDb.IsInitialized)
			return default;

		if (!Gate.Wait(0))
		{
			LogMgr.Instance.Warn("[THA-RETENTION] 上一次清理仍在执行，本次跳过");
			return default;
		}

		try
		{
			var cutoff = DateTime.Now.AddDays(-keepDays);
			// 独立客户端：清理任务跑在后台线程，不能复用 UI/PLC 线程的 SqlSugarScope 上下文连接
			using var db = ThaDb.NewClient();

			progress?.Report($"正在清理 {keepDays} 天前的产品记录…");
			var products = PurgeProductRecords(db, cutoff);
			progress?.Report($"正在清理打码流水…（产品记录已删 {products} 行）");
			var marks = PurgeMarkLogs(db, cutoff);
			progress?.Report($"正在清理读码流水…（打码流水已删 {marks} 行）");
			var scans = PurgeScanLogs(db, cutoff);
			progress?.Report($"正在清理码索引…（读码流水已删 {scans} 行）");
			var codes = PurgeCodeIndex(db, cutoff);
			progress?.Report($"正在清理过期报表文件…（码索引已删 {codes} 行）");
			var files = PurgeReportFiles(keepDays, reportDirectory);

			progress?.Report($"清理完成：产品记录 {products} 行 / 报表文件 {files} 个");
			var result = new PurgeResult(products, marks, scans, codes, files);

			if (!result.IsEmpty)
				LogMgr.Instance.Info($"[THA-RETENTION] 已清理 {keepDays} 天前的本地数据：{result}");
			return result;
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-RETENTION] 保留期清理失败：{ex.Message}", ex);
			return default;
		}
		finally
		{
			Gate.Release();
		}
	}

	// ============================================================
	// 数据库
	// ============================================================

	private static int PurgeProductRecords(ISqlSugarClient db, DateTime cutoff)
		=> DeleteBatches(
			() => db.Queryable<ThaProductRecord>()
				.Where(x => x.CreateTime < cutoff)
				.Take(BatchSize)
				.Select(x => x.Id)
				.ToList(),
			ids => db.Deleteable<ThaProductRecord>().In(ids).ExecuteCommand());

	private static int PurgeMarkLogs(ISqlSugarClient db, DateTime cutoff)
		=> DeleteBatches(
			() => db.Queryable<ThaMarkLog>()
				.Where(x => x.RequestTime < cutoff)
				.Take(BatchSize)
				.Select(x => x.Id)
				.ToList(),
			ids => db.Deleteable<ThaMarkLog>().In(ids).ExecuteCommand());

	private static int PurgeScanLogs(ISqlSugarClient db, DateTime cutoff)
		=> DeleteBatches(
			() => db.Queryable<ThaScanLog>()
				.Where(x => x.CreateTime < cutoff)
				.Take(BatchSize)
				.Select(x => x.Id)
				.ToList(),
			ids => db.Deleteable<ThaScanLog>().In(ids).ExecuteCommand());

	private static int PurgeCodeIndex(ISqlSugarClient db, DateTime cutoff)
	{
		var total = 0;
		while (true)
		{
			var codes = db.Queryable<ThaCodeIndexRow>()
				.Where(x => x.MarkTime < cutoff)
				.Take(BatchSize)
				.Select(x => x.Code)
				.ToList();
			if (codes.Count == 0) break;

			total += db.Deleteable<ThaCodeIndexRow>().In(codes).ExecuteCommand();
			if (codes.Count < BatchSize) break;
		}
		return total;
	}

	/// <summary>按主键分批删除：先取一批主键，再按主键删，避免一次 DELETE 长时间持有写锁。</summary>
	private static int DeleteBatches(Func<List<long>> takeIds, Func<List<long>, int> delete)
	{
		var total = 0;
		while (true)
		{
			var ids = takeIds();
			if (ids.Count == 0) break;

			total += delete(ids);
			if (ids.Count < BatchSize) break;
		}
		return total;
	}

	// ============================================================
	// 报表文件
	// ============================================================

	private static int PurgeReportFiles(int keepDays, string? reportDirectory)
	{
		if (string.IsNullOrWhiteSpace(reportDirectory) || !Directory.Exists(reportDirectory))
			return 0;

		var cutoffDay = DateTime.Today.AddDays(-keepDays);
		var cutoffTime = DateTime.Now.AddDays(-keepDays);
		var deleted = 0;

		foreach (var file in Directory.EnumerateFiles(reportDirectory, $"{ThaDailyReport.FilePrefix}*{ThaDailyReport.FileExtension}"))
		{
			try
			{
				// 文件名里带日期就按日期判，否则退回文件写入时间（如残留的 .tmp.xlsx）
				var expired = TryParseDay(Path.GetFileName(file), out var day)
					? day < cutoffDay
					: File.GetLastWriteTime(file) < cutoffTime;
				if (!expired) continue;

				File.Delete(file);
				deleted++;
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[THA-RETENTION] 删除过期报表失败（{Path.GetFileName(file)}）：{ex.Message}");
			}
		}

		return deleted;
	}

	/// <summary>是否存在超过保留期的报表文件。</summary>
	private static bool HasExpiredReportFile(int keepDays, string? reportDirectory)
	{
		if (string.IsNullOrWhiteSpace(reportDirectory) || !Directory.Exists(reportDirectory))
			return false;

		var cutoffDay = DateTime.Today.AddDays(-keepDays);
		var cutoffTime = DateTime.Now.AddDays(-keepDays);
		try
		{
			foreach (var file in Directory.EnumerateFiles(reportDirectory,
				$"{ThaDailyReport.FilePrefix}*{ThaDailyReport.FileExtension}"))
			{
				var expired = TryParseDay(Path.GetFileName(file), out var day)
					? day < cutoffDay
					: File.GetLastWriteTime(file) < cutoffTime;
				if (expired) return true;
			}
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Warn($"[THA-RETENTION] 检查过期报表文件失败（视为无需清理）：{ex.Message}");
		}
		return false;
	}

	/// <summary>从 <c>追溯报表_yyyyMMdd.xlsx</c> 解析自然日。</summary>
	private static bool TryParseDay(string fileName, out DateTime day)
	{
		day = default;
		if (!fileName.StartsWith(ThaDailyReport.FilePrefix, StringComparison.Ordinal)) return false;
		var rest = fileName[ThaDailyReport.FilePrefix.Length..];
		if (rest.Length < 8) return false;
		return DateTime.TryParseExact(rest[..8], "yyyyMMdd",
			System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.None, out day);
	}
}
