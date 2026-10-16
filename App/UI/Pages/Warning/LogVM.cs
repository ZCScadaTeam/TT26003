using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using AppZC.Services;
using ZC.Mvvm;

namespace AppZC.UI.Pages.Warning;

[AddToIOC(Lifetime = LifetimeType.Singleton)]
[ObservableObject]
public partial class LogVM : UiVM<LogPage>
{
	/// <summary>
	/// 日志列表
	/// </summary>
	public ObservableCollection<DebugLogData> DebugLogs { get; set; } = new();
	
	/// <summary>
	/// 开始日期
	/// </summary>
	public partial DateTimeOffset StartDate { get; set; }
	
	/// <summary>
	/// 结束日期
	/// </summary>
	public partial DateTimeOffset EndDate { get; set; }
	
	public LogVM()
	{
		// 默认查询最近7天
		EndDate = DateTimeOffset.Now;
		StartDate = DateTimeOffset.Now.AddDays(-6);
		
		// 延迟加载，确保依赖注入完成
		_ = Task.Run(async () =>
		{
			await Task.Delay(100);
			LoadDebugLogs();
		});
	}
	
	/// <summary>
	/// 查询按钮
	/// </summary>
	public void @Query()
	{
		LoadDebugLogs();
	}
	
	/// <summary>
	/// 加载调试日志
	/// </summary>
	public void LoadDebugLogs()
	{
		try
		{
			var startDate = StartDate.DateTime.Date;
			var endDate = EndDate.DateTime.Date.AddDays(1); // 包含结束日期当天
			
			var tempList = new List<DebugLogData>();
			
			// 遍历日期范围内的所有日志文件
			for (var date = startDate; date < endDate; date = date.AddDays(1))
			{
				var logPath = GetDebugLogPath(date);
				
				if (!File.Exists(logPath))
				{
					continue;
				}
				
				// 读取日志文件
				var lines = File.ReadAllLines(logPath);
				
				// 解析日志行
				// 格式：260109 10:15:23.456 I [Debug]: 应用程序启动
				// 匹配所有类型的日志：[Debug]、[SQLLog]、[MesLog]、[ChargeLog]、[AlarmLog]等
				var logPattern = @"^(\d{6})\s+(\d{2}:\d{2}:\d{2}\.\d{3})\s+([DIWEF])\s+\[(.+?)\]:\s+(.+)$";
				var regex = new Regex(logPattern);
				
				foreach (var line in lines)
				{
					var match = regex.Match(line);
					if (match.Success)
					{
						var dateStr = match.Groups[1].Value;
						var timeStr = match.Groups[2].Value;
						var level = match.Groups[3].Value;
						var logType = match.Groups[4].Value;  // Debug、SQLLog、MesLog等
						var message = match.Groups[5].Value;
						
						// 解析日期时间
						var year = 2000 + int.Parse(dateStr.Substring(0, 2));
						var month = int.Parse(dateStr.Substring(2, 2));
						var day = int.Parse(dateStr.Substring(4, 2));
						var timeParts = timeStr.Split(':');
						var hour = int.Parse(timeParts[0]);
						var minute = int.Parse(timeParts[1]);
						var secondParts = timeParts[2].Split('.');
						var second = int.Parse(secondParts[0]);
						var millisecond = int.Parse(secondParts[1]);
						
						var logTime = new DateTime(year, month, day, hour, minute, second, millisecond);
						
						// 解析日志级别
						var logLevel = level switch
						{
							"D" => "调试",
							"I" => "信息",
							"W" => "警告",
							"E" => "错误",
							"F" => "致命",
							_ => "未知"
						};
						
						tempList.Add(new DebugLogData
						{
							LogTime = logTime,
							LogLevel = logLevel,
							LogType = logType,
							Message = message
						});
					}
				}
			}
			
			// 按时间倒序排序
			tempList = tempList.OrderByDescending(l => l.LogTime).ToList();
			
			// 在UI线程上更新集合
			Avalonia.Threading.Dispatcher.UIThread.Post(() =>
			{
				DebugLogs.Clear();
				foreach (var log in tempList)
				{
					DebugLogs.Add(log);
				}
			});
			
			_ = ShowNotification($"已加载 {tempList.Count} 条日志", UiMessageType.Success);
		}
		catch (Exception ex)
		{
			_ = ShowNotification($"加载日志失败：{ex.Message}", UiMessageType.Error);
			LogMgr.Instance.Error($"加载日志失败：{ex.Message}", ex);
		}
	}
	
	/// <summary>
	/// 获取调试日志文件路径
	/// </summary>
	private static string GetDebugLogPath(DateTime date)
	{
		var yearMonth = date.ToString("yyyy-MM");
		var fileName = $"DebugLog_{date:yy-MM-dd}.log";
		return Path.Combine(@"D:\Logs\DXX", yearMonth, "DebugLog", fileName);
	}
}

/// <summary>
/// 调试日志数据
/// </summary>
public class DebugLogData
{
	/// <summary>
	/// 日志时间
	/// </summary>
	public DateTime LogTime { get; set; }
	
	/// <summary>
	/// 日志时间显示
	/// </summary>
	public string LogTimeDisplay => LogTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
	
	/// <summary>
	/// 日志级别
	/// </summary>
	public string LogLevel { get; set; } = string.Empty;
	
	/// <summary>
	/// 日志类型（Debug、SQLLog、MesLog等）
	/// </summary>
	public string LogType { get; set; } = string.Empty;
	
	/// <summary>
	/// 日志消息
	/// </summary>
	public string Message { get; set; } = string.Empty;
}
