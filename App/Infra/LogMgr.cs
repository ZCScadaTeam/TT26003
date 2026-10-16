using NLog;
using NLog.Config;
using System.Collections.Concurrent;
using ZC.Mvvm;
using ILogger = NLog.ILogger;

namespace AppZC.Services;

/// <summary>
/// 日志级别枚举
/// </summary>
public enum LogLvl
{
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

/// <summary>
/// 日志结构体
/// </summary>
public struct LogStruct
{
    public ILogger Logger;
    public string Message;
    public LogLvl Level;
    public Exception? Exception;

    public LogStruct(string message, LogLvl level, ILogger logger, Exception? exception = null)
    {
        Message = message;
        Level = level;
        Logger = logger;
        Exception = exception;
    }
}

/// <summary>
/// 日志管理器 - 单例模式
/// 日志按类别分类：
/// 1. DebugLog - 总日志（Debug/Info/Error都放一起）
/// 2. SQLLog - 数据库日志
/// 3. MesLog - MES通信日志
/// 4. ChargeLog - 参数变更日志
/// 5. AlarmLog - 报警信息日志
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
public class LogMgr
{
    private static readonly Lazy<LogMgr> _instance = new(() => new LogMgr());
    public static LogMgr Instance => _instance.Value;

    private readonly ILogger _debugLog;      // 总日志（Debug/Info/Error都放一起）
    private readonly ILogger _sqlLog;        // 数据库日志
    private readonly ILogger _mesLog;        // MES通信日志
    private readonly ILogger _chargeLog;     // 参数变更日志
    private readonly ILogger _alarmLog;      // 报警信息日志
    
    private readonly ConcurrentQueue<LogStruct> _logQueue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _monitorTask;

    public bool IsInitialized { get; private set; }

    private LogMgr()
    {
        // 加载NLog配置
        LoadNLogConfiguration();
        
        _debugLog = LogManager.GetLogger("Debug");
        _sqlLog = LogManager.GetLogger("SQLLog");
        _mesLog = LogManager.GetLogger("MesLog");
        _chargeLog = LogManager.GetLogger("ChargeLog");
        _alarmLog = LogManager.GetLogger("AlarmLog");
        
        _logQueue = new ConcurrentQueue<LogStruct>();
        _cts = new CancellationTokenSource();
        
        // 启动日志监控线程
        _monitorTask = Task.Run(() => MonitorLogQueue(_cts.Token));
        
        IsInitialized = true;
        
        // 直接写入初始化日志（文件路径使用系统时间，可以立即解析）
        _debugLog.Info("日志管理器初始化成功");
    }
    
    /// <summary>
    /// 获取当前时间（优先使用NTP时间）
    /// </summary>
    private DateTime GetCurrentTime()
    {
        return DateTime.Now;
    }
    
    /// <summary>
    /// 加载NLog配置
    /// </summary>
    private void LoadNLogConfiguration()
    {
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Configs", "NLog.config");
            
            Console.WriteLine($"[LogMgr] 配置文件路径: {configPath}");
            Console.WriteLine($"[LogMgr] 配置文件存在: {File.Exists(configPath)}");
            
            if (File.Exists(configPath))
            {
                LogManager.Configuration = new XmlLoggingConfiguration(configPath);
                Console.WriteLine($"[LogMgr] NLog配置加载成功");
                
                // 检查配置
                var config = LogManager.Configuration;
                if (config != null)
                {
                    Console.WriteLine($"[LogMgr] 目标数量: {config.AllTargets.Count}");
                    Console.WriteLine($"[LogMgr] 规则数量: {config.LoggingRules.Count}");
                }
            }
            else
            {
                Console.WriteLine($"[LogMgr] 警告: 配置文件不存在，使用默认配置");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LogMgr] 加载NLog配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 监控日志队列并异步写入
    /// </summary>
    private async Task MonitorLogQueue(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_logQueue.IsEmpty)
                {
                    await Task.Delay(100, token);
                    continue;
                }

                if (_logQueue.TryDequeue(out var logStruct))
                {
                    WriteLog(logStruct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"日志写入异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 实际写入日志
    /// </summary>
    private void WriteLog(LogStruct logStruct)
    {
        switch (logStruct.Level)
        {
            case LogLvl.Debug:
                logStruct.Logger.Debug(logStruct.Exception, logStruct.Message);
                break;
            case LogLvl.Info:
                logStruct.Logger.Info(logStruct.Exception, logStruct.Message);
                break;
            case LogLvl.Warn:
                logStruct.Logger.Warn(logStruct.Exception, logStruct.Message);
                break;
            case LogLvl.Error:
                logStruct.Logger.Error(logStruct.Exception, logStruct.Message);
                break;
            case LogLvl.Fatal:
                logStruct.Logger.Fatal(logStruct.Exception, logStruct.Message);
                break;
        }
    }

    #region 通用日志方法（Debug/Info/Error都放到DebugLog）

    /// <summary>
    /// 记录调试信息
    /// </summary>
    public void Debug(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Debug, _debugLog));
    }

    /// <summary>
    /// 记录普通信息
    /// </summary>
    public void Info(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Info, _debugLog));
    }

    /// <summary>
    /// 记录警告信息
    /// </summary>
    public void Warn(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Warn, _debugLog));
    }

    /// <summary>
    /// 记录错误信息
    /// </summary>
    public void Error(string message, Exception? ex = null)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Error, _debugLog, ex));
    }

    /// <summary>
    /// 记录致命错误
    /// </summary>
    public void Fatal(string message, Exception? ex = null)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Fatal, _debugLog, ex));
    }
    
    /// <summary>
    /// 同步写入日志（立即写入，不经过队列）- 用于调试
    /// </summary>
    public void DebugSync(string message)
    {
        var msg = $"{GetCurrentTime():yy/MM/dd HH:mm:ss.fff} {message}";
        _debugLog.Debug(msg);
    }
    
    /// <summary>
    /// 同步写入Info日志（立即写入，不经过队列）- 用于调试
    /// </summary>
    public void InfoSync(string message)
    {
        var msg = $"{GetCurrentTime():yy/MM/dd HH:mm:ss.fff} {message}";
        _debugLog.Info(msg);
    }

    #endregion

    #region 数据库日志（SQLLog）

    /// <summary>
    /// 记录SQL日志
    /// </summary>
    public void AddSQLLog(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Debug, _sqlLog));
    }
    
    /// <summary>
    /// 记录SQL执行前的日志
    /// </summary>
    public void AddSQLExecuting(string sql)
    {
        _logQueue.Enqueue(new LogStruct($"Executing SQL: {sql}", LogLvl.Debug, _sqlLog));
    }
    
    /// <summary>
    /// 记录SQL执行后的日志
    /// </summary>
    public void AddSQLExecuted(string sql, long elapsedMs = 0)
    {
        var message = elapsedMs > 0 
            ? $"Executed SQL ({elapsedMs}ms): {sql}" 
            : $"Executed SQL: {sql}";
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Debug, _sqlLog));
    }
    
    /// <summary>
    /// 记录SQL错误
    /// </summary>
    public void AddSQLError(string sql, Exception ex)
    {
        _logQueue.Enqueue(new LogStruct($"SQL Error: {sql}", LogLvl.Error, _sqlLog, ex));
    }

    #endregion

    #region MES日志（MesLog）

    /// <summary>
    /// 记录MES调试信息
    /// </summary>
    public void AddMesDebug(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Debug, _mesLog));
    }

    /// <summary>
    /// 记录MES信息
    /// </summary>
    public void AddMesInfo(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Info, _mesLog));
    }

    /// <summary>
    /// 记录MES错误
    /// </summary>
    public void AddMesError(string message, Exception? ex = null)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Error, _mesLog, ex));
    }

    #endregion

    #region 参数变更日志（ChargeLog）

    /// <summary>
    /// 记录参数变更
    /// </summary>
    public void AddChargeInfo(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Info, _chargeLog));
    }
    
    /// <summary>
    /// 记录参数变更（带旧值和新值）
    /// </summary>
    public void LogParamChange(string paramName, string oldValue, string newValue)
    {
        var message = $"参数变更: {paramName.PadRight(30)} {oldValue.PadRight(30)} --> {newValue}";
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Info, _chargeLog));
    }

    #endregion

    #region 报警信息日志（AlarmLog）

    /// <summary>
    /// 记录报警信息
    /// </summary>
    public void AddAlarmInfo(string message)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Warn, _alarmLog));
    }
    
    /// <summary>
    /// 记录报警错误
    /// </summary>
    public void AddAlarmError(string message, Exception? ex = null)
    {
        _logQueue.Enqueue(new LogStruct(message, LogLvl.Error, _alarmLog, ex));
    }

    #endregion

    /// <summary>
    /// 关闭日志管理器
    /// </summary>
    public void Shutdown()
    {
        Info("日志管理器正在关闭...");
        _cts.Cancel();
        
        // 等待队列清空
        var timeout = TimeSpan.FromSeconds(5);
        var startTime = GetCurrentTime();
        while (!_logQueue.IsEmpty && GetCurrentTime() - startTime < timeout)
        {
            Thread.Sleep(100);
        }
        
        _monitorTask.Wait(1000);
        LogManager.Shutdown();
    }
}
