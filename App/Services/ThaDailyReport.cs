using System.Threading.Channels;
using AppZC.Configs;
using AppZC.Models.DB;
using MiniExcelLibs;

namespace AppZC.Services;

/// <summary>
/// 每日追溯报表（自然日 Excel）：<c>追溯报表_yyyyMMdd.xlsx</c>。
///
/// 语义：
/// • 自然日分文件：文件名按 <see cref="FilePathFor"/> 规则生成，零点自动新建次日文件（哪怕 0 条记录也建表头）。
/// • 追加写入：上传站每落库一条产品记录（<see cref="Append"/>）就进入报表队列，由单写线程
///   **每 <see cref="AppConfig.DailyReportFlushSeconds"/> 秒（默认 60 秒）** 落盘一次。
///   落盘 = 整份当日文件重写（先写 <c>*.tmp.xlsx</c> 再原子替换），调用方看到的始终是一份完整文件。
/// • 数据权威在 SQLite：进程重启 / 崩溃后按 <c>CreateTime</c> 回查当天全部记录重写文件，不会漏也不会重复
///   （按雪花 Id 单调递增去重）。非当日迟到记录同样从库里整份重建该日文件。
/// • 零点结算：先把前一天完整落盘，再新建当日文件。
/// • 保留期清理不在这里做：旧数据在**程序退出时**检查并清理（见 <see cref="ThaDataRetention"/>）。
///
/// 为什么整份重写而不是行级 append：xlsx 是 zip，行级追加需要重写 sheet XML，成本与整份重写同阶；
/// 而 MiniExcel 实测 25k 行 × 104 列约 0.9s，按行数阈值攒批后单日累计重写成本 &lt; 30s，换来的是
/// 「文件任何时刻都是完整可打开的 xlsx」，不会出现半截文件。
/// </summary>
public static class ThaDailyReport
{
	/// <summary>文件名前缀。</summary>
	public const string FilePrefix = "追溯报表_";

	/// <summary>文件名后缀。</summary>
	public const string FileExtension = ".xlsx";

	/// <summary>默认报表目录（未配置 / 配置为空时使用）。</summary>
	public const string DefaultDirectory = @"D:\追溯数据";

	/// <summary>工作表名。</summary>
	private const string SheetName = "追溯";

	// ----- 以下字段在 Init 后只读 -----
	private static bool _enabled;
	private static string _directory = "";
	private static int _flushSeconds = 60;
	private static bool _skipStartupReconcile;

	// ----- 队列/线程 -----
	private static Channel<object>? _queue;
	private static CancellationTokenSource? _cts;
	private static Task? _worker;

	// ----- 以下字段只由写线程访问（测试钩子除外）-----
	private static DateTime _day = DateTime.MinValue;
	private static readonly List<ThaProductRecord> _dayRows = new();
	private static readonly HashSet<DateTime> _rebuildDays = new();
	private static long _lastId;
	private static int _writtenCount;
	private static bool _dirty;
	private static DateTime _lastFlushUtc = DateTime.UtcNow;
	private static DateTime _lastErrorLogUtc = DateTime.MinValue;
	private static int _writeFailures;

	/// <summary>一次刷新请求（Shutdown / 自检 / 手动触发时用）。</summary>
	private sealed record FlushRequest(TaskCompletionSource<bool> Tcs);

	/// <summary>报表是否已启动。</summary>
	public static bool IsRunning => _worker is { IsCompleted: false };

	/// <summary>报表输出目录（绝对路径）。</summary>
	public static string ReportDirectory => _directory;

	/// <summary>当前自然日的报表文件绝对路径。</summary>
	public static string CurrentFilePath => FilePathFor(DateTime.Today);

	/// <summary>当日已写入文件的行数（最近一次成功落盘时的行数）。</summary>
	public static int WrittenRows => Volatile.Read(ref _writtenCount);

	/// <summary>
	/// 初始化并启动每日报表写盘线程。应在 <see cref="ThaDb.Init"/> 之后调用（报表回查依赖本地库）。
	/// 报表关闭（<see cref="AppConfig.DailyReportEnabled"/>=false）时不启动写线程，只保留目录解析。
	/// </summary>
	public static void Init(AppConfig cfg)
	{
		if (_worker != null) return;
		_skipStartupReconcile = false;
		_enabled = cfg.DailyReportEnabled;
		_flushSeconds = Math.Clamp(cfg.DailyReportFlushSeconds, 1, 86_400);

		var configured = string.IsNullOrWhiteSpace(cfg.DailyReportDirectory)
			? DefaultDirectory
			: Environment.ExpandEnvironmentVariables(cfg.DailyReportDirectory.Trim());
		try
		{
			_directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), AppContext.BaseDirectory);
			Directory.CreateDirectory(_directory);
		}
		catch (Exception ex)
		{
			_directory = Path.Combine(AppContext.BaseDirectory, "AppData", "Reports");
			Directory.CreateDirectory(_directory);
			LogMgr.Instance.Error($"[THA-REPORT] 报表目录 '{configured}' 不可用（{ex.Message}），回退到 {_directory}");
		}

		if (!_enabled)
		{
			LogMgr.Instance.Warn("[THA-REPORT] 每日报表已关闭（DailyReportEnabled=false），不生成报表；旧数据仍在退出时清理");
			return;
		}

		_queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false,
		});
		_cts = new CancellationTokenSource();
		_worker = Task.Run(() => WorkerLoopAsync(_cts.Token));

		LogMgr.Instance.Info($"[THA-REPORT] 每日追溯报表已启动：目录={_directory}，"
			+ $"每 {_flushSeconds} 秒落盘一次，当前文件={CurrentFilePath}");
	}

	/// <summary>
	/// 生产线程调用：产品记录已落库后追加进当日报表（非阻塞，失败只记日志不影响产线）。
	/// 同一记录重复 Append 会被雪花 Id 单调性挡掉。
	/// </summary>
	public static void Append(ThaProductRecord record)
	{
		if (!_enabled || _queue == null) return;
		try { _queue.Writer.TryWrite(record); }
		catch (Exception ex) { LogMgr.Instance.Error($"[THA-REPORT] 报表入队失败：{ex.Message}"); }
	}

	/// <summary>
	/// 立即落盘（阻塞到写线程处理完）。返回 true 表示当日文件已是最新（或无可写内容），false 表示写盘失败。
	/// 退出前清理、自检、以及需要「写完再给结果」的场景使用。
	/// </summary>
	public static bool FlushNow(int timeoutMs = 10_000)
	{
		var queue = _queue;
		if (!_enabled || queue == null) return true;
		if (_worker is not { IsCompleted: false }) return false;

		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!queue.Writer.TryWrite(new FlushRequest(tcs))) return false;
		try
		{
			if (!tcs.Task.Wait(timeoutMs)) return false;
			return tcs.Task.Result;
		}
		catch (AggregateException)
		{
			return false;
		}
	}

	/// <summary>停止写线程（退出前调用，会先把队列里剩余记录与未落盘内容写完）。</summary>
	public static void Shutdown()
	{
		var worker = _worker;
		if (worker == null) return;
		try
		{
			_cts?.Cancel();
			worker.Wait(5_000);
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Warn($"[THA-REPORT] 停止报表线程时出现异常：{ex.Message}");
		}
		finally
		{
			_queue?.Writer.TryComplete();
			_cts?.Dispose();
			_worker = null;
			_cts = null;
			_queue = null;
		}
	}

	/// <summary>自然日 → 报表文件绝对路径：<c>{目录}/追溯报表_yyyyMMdd.xlsx</c>。</summary>
	public static string FilePathFor(DateTime day)
		=> Path.Combine(_directory, $"{FilePrefix}{day:yyyyMMdd}{FileExtension}");

	// ============================================================
	// 写线程
	// ============================================================

	private static async Task WorkerLoopAsync(CancellationToken ct)
	{
		if (!_skipStartupReconcile)
		{
			try
			{
				ReconcileOnStartup();
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"[THA-REPORT] 启动补写失败：{ex.Message}", ex);
			}
		}

		try
		{
			while (!ct.IsCancellationRequested)
			{
				try { await Task.Delay(500, ct).ConfigureAwait(false); }
				catch (OperationCanceledException) { break; }

				try { ProcessBatch(DrainQueue(), forceFlush: false); }
				catch (Exception ex) { LogErrorThrottled($"[THA-REPORT] 报表处理异常：{ex.Message}", ex); }
			}
		}
		finally
		{
			// 退出前把队列里剩余记录与未落盘内容写完（最后一份文件必须完整）
			try { ProcessBatch(DrainQueue(), forceFlush: true); }
			catch (Exception ex) { LogMgr.Instance.Error($"[THA-REPORT] 退出前落盘失败：{ex.Message}", ex); }
		}
	}

	private static List<object> DrainQueue()
	{
		var list = new List<object>();
		var queue = _queue;
		if (queue == null) return list;
		while (queue.Reader.TryRead(out var item)) list.Add(item);
		return list;
	}

	private static void ProcessBatch(List<object> batch, bool forceFlush)
	{
		var records = new List<ThaProductRecord>();
		var flushes = new List<FlushRequest>();
		var rotates = new List<RotateRequest>();
		foreach (var item in batch)
		{
			switch (item)
			{
				case ThaProductRecord r: records.Add(r); break;
				case FlushRequest f: flushes.Add(f); break;
				case RotateRequest q: rotates.Add(q); break;
			}
		}

		if (rotates.Count > 0)
		{
			foreach (var q in rotates)
			{
				if (q.Day.Date != _day) Rotate(q.Day.Date);
				q.Tcs.TrySetResult(true);
			}
		}

		if (records.Count > 0) Ingest(records);

		// 零点：结算前一天 + 新建当天文件
		var today = DateTime.Today;
		if (_day == DateTime.MinValue) _day = today;
		if (_day != today) Rotate(today);

		// 非当日迟到记录：从库里整份重建该日文件（权威数据在 SQLite）
		if (_rebuildDays.Count > 0)
		{
			foreach (var day in _rebuildDays) RebuildDayFromDb(day);
			_rebuildDays.Clear();
		}

		var force = forceFlush || flushes.Count > 0;
		var idleSeconds = (DateTime.UtcNow - _lastFlushUtc).TotalSeconds;

		// 节奏：固定每 _flushSeconds 秒写一次（默认 60 秒）；退出/自检走 force 立即落盘
		var needFlush = _dirty && (force || idleSeconds >= _flushSeconds);
		var ok = true;
		if (needFlush) ok = WriteDayFile(_day);

		foreach (var f in flushes)
			f.Tcs.TrySetResult(ok);
	}

	/// <summary>把新记录并入当日内存集（按 Id 单调去重）；跨日记录交给整份重建。</summary>
	private static void Ingest(List<ThaProductRecord> records)
	{
		foreach (var r in records)
		{
			var day = r.CreateTime.Date;
			if (_day == DateTime.MinValue) _day = day;
			if (day != _day)
			{
				// 零点前后在途 / 补录：该日文件从库里重建，避免乱序插入
				_rebuildDays.Add(day);
				continue;
			}
			if (r.Id <= _lastId) continue; // 幂等：重启补扫 / 重复订阅
			_dayRows.Add(r);
			_lastId = r.Id;
			_dirty = true;
		}
	}

	/// <summary>跨日切换：结算前一天 → 新建当天（含 0 条时的表头文件）→ 触发保留期清理。</summary>
	private static void Rotate(DateTime newDay)
	{
		var previous = _day;
		var settledRows = _dayRows.Count;
		if (previous != DateTime.MinValue)
		{
			// 先落内存里的（快），再从库里补洞（覆盖"记录没走 Append"的极端情况）
			if (_dirty) WriteDayFile(previous);
			ReconcileDayFromDb(previous, onlyIfShorter: true);
		}

		// 注意：不清 _rebuildDays。零点前后可能在途记录已把"昨天"塞进来，
		// 这里清掉会让那天的补齐丢在一次换日里。
		_day = newDay;
		_dayRows.Clear();
		_lastId = 0;
		Volatile.Write(ref _writtenCount, 0);
		_dirty = false;

		LoadDayFromDb(newDay);
		WriteDayFile(newDay); // 零点自动新建当日报表（即使当天还没有记录）
		_lastFlushUtc = DateTime.UtcNow;

		LogMgr.Instance.Info($"[THA-REPORT] 零点换日：{previous:yyyy-MM-dd} 已结算（{settledRows} 行），"
			+ $"已新建 {Path.GetFileName(FilePathFor(newDay))}");

		// 保留期清理不在零点执行：改为程序退出时检查（有旧数据才弹窗清理）
	}

	// ============================================================
	// 文件写入
	// ============================================================

	/// <summary>写当日文件（原子替换）。返回 false 表示写失败（文件被占用等），保持 _dirty 等下次重试。</summary>
	private static bool WriteDayFile(DateTime day)
	{
		if (!_enabled) return true;
		if (day != _day) return false;

		var ok = TryWriteRowsFile(day, _dayRows);
		if (ok)
		{
			Volatile.Write(ref _writtenCount, _dayRows.Count);
			_dirty = false;
			_lastFlushUtc = DateTime.UtcNow;
			_writeFailures = 0;
		}
		else
		{
			_writeFailures++;
		}
		return ok;
	}

	/// <summary>把一组记录整份写成 <paramref name="day"/> 的报表文件（先写临时文件，再原子替换）。</summary>
	private static bool TryWriteRowsFile(DateTime day, IReadOnlyList<ThaProductRecord> rows)
	{
		var path = FilePathFor(day);
		var tmp = path + ".tmp.xlsx";
		try
		{
			Directory.CreateDirectory(_directory);
			var exportRows = new List<ThaExportRow>(rows.Count);
			foreach (var r in rows) exportRows.Add(ThaExportRow.From(r));

			MiniExcel.SaveAs(tmp, exportRows, overwriteFile: true, sheetName: SheetName);
			File.Move(tmp, path, overwrite: true);
			return true;
		}
		catch (Exception ex)
		{
			// 最常见：用户在 Excel 里开着这份报表 → 替换失败。保留 dirty，下次继续重试。
			LogErrorThrottled($"[THA-REPORT] 写报表失败（{Path.GetFileName(path)}，将重试）：{ex.Message}", ex);
			return false;
		}
		finally
		{
			try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
		}
	}

	// ============================================================
	// 启动补写 / 数据回查
	// ============================================================

	/// <summary>启动补写：当天全量重建；昨天在文件缺失/行数不足时重建（覆盖零点前后断电、崩溃的空洞）。</summary>
	private static void ReconcileOnStartup()
	{
		var today = DateTime.Today;
		_day = today;
		_dayRows.Clear();
		_lastId = 0;

		LoadDayFromDb(today);
		if (!WriteDayFile(today) && _writeFailures > 0)
			LogMgr.Instance.Warn($"[THA-REPORT] 当日报表首次写入失败，将在后续周期重试：{FilePathFor(today)}");

		ReconcileDayFromDb(today.AddDays(-1), onlyIfShorter: true);
		_lastFlushUtc = DateTime.UtcNow;
	}

	/// <summary>从 SQLite 载入某自然日的全部记录（按 Id 升序，Id 即写入顺序）。</summary>
	private static List<ThaProductRecord> QueryDayFromDb(DateTime day)
	{
		if (!ThaDb.IsInitialized) return new List<ThaProductRecord>();
		var start = day.Date;
		var end = start.AddDays(1);
		try
		{
			// 独立客户端：写线程不能与 UI / PLC 线程共用 SqlSugarScope 的上下文连接
			using var db = ThaDb.NewClient();
			return db.Queryable<ThaProductRecord>()
				.Where(x => x.CreateTime >= start && x.CreateTime < end)
				.OrderBy(x => x.Id)
				.ToList();
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-REPORT] 回查 {day:yyyy-MM-dd} 记录失败：{ex.Message}", ex);
			return new List<ThaProductRecord>();
		}
	}

	private static void LoadDayFromDb(DateTime day)
	{
		var rows = QueryDayFromDb(day);
		_dayRows.Clear();
		_dayRows.AddRange(rows);
		_lastId = rows.Count == 0 ? 0 : rows[^1].Id;
		_dirty = rows.Count > 0;
	}

	/// <summary>用库里的数据重建某一天的文件（非当日迟到记录 / 启动补齐昨天）。</summary>
	private static void RebuildDayFromDb(DateTime day)
	{
		if (day == _day)
		{
			LoadDayFromDb(day);
			WriteDayFile(day);
			return;
		}

		var rows = QueryDayFromDb(day);
		if (rows.Count == 0) return;
		if (TryWriteRowsFile(day, rows))
			LogMgr.Instance.Info($"[THA-REPORT] 已按库内数据重建 {day:yyyy-MM-dd} 报表（{rows.Count} 行）");
	}

	/// <summary>
	/// 启动补齐指定日期：仅当文件缺失或文件行数少于库内行数时重建。
	/// 文件损坏（无法读取）同样触发重建。
	/// </summary>
	private static void ReconcileDayFromDb(DateTime day, bool onlyIfShorter)
	{
		if (!ThaDb.IsInitialized) return;
		var rows = QueryDayFromDb(day);
		if (rows.Count == 0) return;

		var path = FilePathFor(day);
		if (onlyIfShorter && File.Exists(path))
		{
			try
			{
				var existing = MiniExcel.Query(path, useHeaderRow: true).Count();
				if (existing >= rows.Count) return;
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[THA-REPORT] {Path.GetFileName(path)} 读取失败（将重建）：{ex.Message}");
			}
		}

		if (TryWriteRowsFile(day, rows))
			LogMgr.Instance.Info($"[THA-REPORT] 已补齐 {day:yyyy-MM-dd} 报表：{rows.Count} 行 → {Path.GetFileName(path)}");
	}

	private static void LogErrorThrottled(string message, Exception? ex = null)
	{
		var now = DateTime.UtcNow;
		if ((now - _lastErrorLogUtc).TotalSeconds < 30) return;
		_lastErrorLogUtc = now;
		LogMgr.Instance.Warn(message);
		if (ex != null && _writeFailures == 0) LogMgr.Instance.Error(ex.ToString());
	}

	// ============================================================
	// 自检钩子（--selftest 用，不参与生产逻辑）
	// ============================================================

	/// <summary>自检：用独立目录启动报表线程，不触发保留期清理。</summary>
	internal static void InitForTest(string directory, int flushSeconds = 3_600)
	{
		Shutdown();
		_directory = Path.GetFullPath(directory);
		Directory.CreateDirectory(_directory);
		_flushSeconds = flushSeconds;
		_enabled = true;
		_worker = null;
		_queue = Channel.CreateUnbounded<object>();
		_cts = new CancellationTokenSource();
		_day = DateTime.Today;
		_skipStartupReconcile = true;
		_dayRows.Clear();
		_rebuildDays.Clear();
		_lastId = 0;
		_writtenCount = 0;
		_dirty = false;
		_worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
	}

	/// <summary>自检：模拟零点换日（把写线程的“当前自然日”切到指定日期）。</summary>
	internal static void RotateForTest(DateTime day)
	{
		var queue = _queue;
		if (queue == null) return;
		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		// 借 FlushRequest 让写线程执行换日（写线程内调用 Rotate 保证状态单线程）
		queue.Writer.TryWrite(new RotateRequest(day, tcs));
		tcs.Task.Wait(5_000);
	}

	private sealed record RotateRequest(DateTime Day, TaskCompletionSource<bool> Tcs);
}
