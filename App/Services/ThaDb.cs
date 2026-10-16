using SqlSugar;
using AppZC.Models.DB;
using ZC.DB;

namespace AppZC.Services;

/// <summary>
/// THA26003 本地 SQLite 数据库入口（AppData/THA26003.db）。
/// 由 App.OnInitialize 在 StartUi 前调用 <see cref="Init"/> 完成目录/连接串/CodeFirst 建表。
///
/// 与 26001 的关键差异：**一条产品一条记录（INSERT），绝不按产品码 UPSERT**。
/// 26001 的"同码覆盖"语义会把读码站刚判出的重码覆盖成一条 → 重码检测形同虚设。
/// 复检靠 ThaCodeIndex 状态机 + RetestCount 区分，不靠覆盖。
/// </summary>
public static class ThaDb
{
	/// <summary>SQLite 连接（SqlSugarScope，线程安全，支持并发读）。</summary>
	public static SqlSugarScope Db { get; private set; } = null!;

	/// <summary>数据库文件绝对路径。</summary>
	public static string DbPath { get; private set; } = "";

	/// <summary>是否已初始化成功。</summary>
	public static bool IsInitialized { get; private set; }

	/// <summary>
	/// 初始化本地 SQLite：确保 AppData 目录存在、规范化连接串（回写 AppConfig）、
	/// 建 SqlSugarScope 并 CodeFirst 建表（幂等）。
	/// </summary>
	public static void Init(DatabaseConnectionConfig cfg)
	{
		var dir = Path.Combine(AppContext.BaseDirectory, "AppData");
		Directory.CreateDirectory(dir);
		DbPath = Path.Combine(dir, "THA26003.db");

		// 强制 SQLite + 本地文件路径（回写配置，保证 UseDatabase 与 ThaDb 读同一库文件）
		cfg.DbType = DatabaseType.Sqlite;
		cfg.ConnectionString = $"DataSource={DbPath}";

		Db = new SqlSugarScope(new ConnectionConfig
		{
			DbType = SqlSugar.DbType.Sqlite,
			ConnectionString = cfg.ConnectionString,
			IsAutoCloseConnection = true,
			InitKeyType = InitKeyType.Attribute,
		});

		// WAL：单写多读，降低 PLC 落库与 UI 查询的读写锁冲突
		Db.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
		Db.Ado.ExecuteCommand("PRAGMA synchronous=NORMAL;");
		Db.Ado.ExecuteCommand("PRAGMA cache_size=-64000;");
		Db.Ado.ExecuteCommand("PRAGMA temp_store=MEMORY;");
		Db.Ado.ExecuteCommand("PRAGMA mmap_size=268435456;");

		// 旧 schema 守卫：本版把采集字段全部改为可空、去掉 RetestCount/PIN16 列。
		// SQLite 无法直接把 NOT NULL 列改可空，也不支持 DROP COLUMN（旧版本），
		// 因此检测到老表结构时把它**改名保留**（不删数据），再让 CodeFirst 建新表。
		EnsureSchemaVersion();

		// CodeFirst 建表（幂等，已存在则补列）
		Db.CodeFirst.InitTables<ThaProductRecord>();
		Db.CodeFirst.InitTables<ThaCodeIndexRow>();
		Db.CodeFirst.InitTables<ThaMarkLog>();
		Db.CodeFirst.InitTables<ThaScanLog>();

		// 复合覆盖索引：让统计页按时间段 + 结果查，无需回表
		Db.Ado.ExecuteCommand(
			"CREATE INDEX IF NOT EXISTS idx_tha_create_result ON ThaProductRecord (CreateTime, OverallResult);");
		Db.Ado.ExecuteCommand(
			"CREATE INDEX IF NOT EXISTS idx_tha_create_id ON ThaProductRecord (CreateTime, Id);");

		Db.Ado.ExecuteCommand("PRAGMA optimize;");

		IsInitialized = true;
		LogMgr.Instance.Info($"[THA-DB] 本地 SQLite 初始化完成：{DbPath}");
	}

	/// <summary>
	/// 旧 schema 守卫：若已有 ThaProductRecord 表但缺少本版新增的 IsRetest 列，
	/// 说明是"字段非空 + 带 RetestCount/PIN16"的老结构 → 整表改名为
	/// <c>ThaProductRecord_legacy_yyyyMMddHHmmss</c> 保留，再交由 CodeFirst 建新表。
	/// 项目尚未上线，无生产数据；改名而非删除，出问题可人工比对。
	/// </summary>
	private static void EnsureSchemaVersion()
	{
		try
		{
			var dt = Db.Ado.GetDataTable("PRAGMA table_info(ThaProductRecord)");
			if (dt.Rows.Count == 0)
				return; // 全新库

			var names = dt.Rows.Cast<System.Data.DataRow>()
				.Select(r => r["name"]?.ToString() ?? "")
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			if (names.Contains("IsRetest"))
				return; // 已是新结构

			var legacy = $"ThaProductRecord_legacy_{DateTime.Now:yyyyMMddHHmmss}";
			Db.Ado.ExecuteCommand("PRAGMA wal_checkpoint(TRUNCATE);");
			Db.Ado.ExecuteCommand($"ALTER TABLE \"ThaProductRecord\" RENAME TO \"{legacy}\"");
			LogMgr.Instance.Warn($"[THA-DB] 检测到旧表结构（采集字段非空/含 RetestCount），已改名保留为 {legacy}，本次将重建新表");
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-DB] schema 版本检查失败（将按现有结构继续）：{ex.Message}", ex);
		}
	}

	/// <summary>
	/// 上传落库：一条产品一条记录。
	/// 事务内完成 INSERT 产品记录 + 更新码索引状态（已上传 + 回填 ProductId），
	/// 保证"产品记录"与"码状态"不会一个成功一个失败。
	/// </summary>
	public static bool InsertProductRecord(ThaProductRecord record)
	{
		if (!IsInitialized) return false;
		try
		{
			record.Id = SnowFlakeSingle.Instance.NextId();
			record.CreateTime = DateTime.Now;
			record.UpdateTime = record.CreateTime;

			Db.Ado.BeginTran();
			try
			{
				Db.Insertable(record).ExecuteCommand();
				if (!string.IsNullOrWhiteSpace(record.ProductCode))
				{
					Db.Updateable<ThaCodeIndexRow>()
						.SetColumns(x => new ThaCodeIndexRow
						{
							Status = (int)CodeStatus.Uploaded,
							ProductId = record.Id,
							UploadTime = record.CreateTime,
						})
						.Where(x => x.Code == record.ProductCode)
						.ExecuteCommand();
				}
				Db.Ado.CommitTran();
			}
			catch
			{
				Db.Ado.RollbackTran();
				throw;
			}
			return true;
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-DB] 产品记录落库失败: {ex.Message}", ex);
			return false;
		}
	}

	/// <summary>打码过程流水落库（失败不影响业务主流程）。</summary>
	public static void LogMark(ThaMarkLog log)
	{
		if (!IsInitialized) return;
		try
		{
			log.Id = SnowFlakeSingle.Instance.NextId();
			Db.Insertable(log).ExecuteCommand();
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-DB] 打码流水落库失败: {ex.Message}", ex);
		}
	}

	/// <summary>读码过程流水落库（失败不影响业务主流程）。</summary>
	public static void LogScan(ThaScanLog log)
	{
		if (!IsInitialized) return;
		try
		{
			log.Id = SnowFlakeSingle.Instance.NextId();
			Db.Insertable(log).ExecuteCommand();
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-DB] 读码流水落库失败: {ex.Message}", ex);
		}
	}

	/// <summary>最近 N 条产品记录（主页历史表预载）。</summary>
	public static List<ThaProductRecord> RecentRecords(int take)
	{
		if (!IsInitialized) return new List<ThaProductRecord>();
		try
		{
			return Db.Queryable<ThaProductRecord>().OrderByDescending(r => r.Id).Take(take).ToList();
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"[THA-DB] 查询最近记录失败: {ex.Message}", ex);
			return new List<ThaProductRecord>();
		}
	}
}
