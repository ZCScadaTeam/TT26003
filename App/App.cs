using System.Text.Json;
using AppZC.Configs;
using AppZC.Services;
using Newtonsoft.Json;
using NLog;
using ZC.EnhanceApp;
using JsonSerializer = System.Text.Json.JsonSerializer;

// 最早执行：禁用 Windows 控制台「快速编辑模式」，避免双击选中控制台巨量日志文本时
// Console.WriteLine / NLog ColoredConsole 写入阻塞调用线程导致软件卡死（Ctrl+C 才恢复）。
ConsoleHelper.DisableQuickEditMode();

var gridPanelChildDefinitions = new GridPanelChildDefinitions("100,*,100");
JsonConvert.DefaultSettings = () =>
{
	var settings = new JsonSerializerSettings();
	settings.Converters.Add(new AnyNewtonsoftJsonConverter());
	return settings;
};

// 初始化日志管理器（会自动加载NLog.config配置）
var logMgr = LogMgr.Instance;
var nlogLogger = LogManager.GetCurrentClassLogger();

AppCore.UnhandledException += (sender, eventArgs) =>
{
	var ex = eventArgs.ExceptionObject as Exception;
	nlogLogger.Fatal(ex, "发生未处理异常! {obj} -> {err} CLR:{}", sender, ex?.Message, eventArgs.IsTerminating);
	logMgr.Fatal($"发生未处理异常: {ex?.Message}", ex);
};

// 演示模式：不再连接数据库/PLC/MES，保留配置加载（工位模式等 UI 配置仍从 json5 读取）
var config = new AppConfig
{
	Database = new ZC.DB.DatabaseConnectionConfig
	{ DbType = ZC.DB.DatabaseType.PostgreSQL, ConnectionString = "Host=127.0.0.1;Port=5432;Database=DXX;Username=postgres;Password=postgres;" },
	TaskServiceHostOptions = new TaskServiceHostOptions { MaxRemainStartCount = 1 }
};
// 始终从独立配置目录加载（框架原生支持指定路径，且能正确解析 json5 注释）。
// 注意：框架在文件缺失时会【抛异常】（不是返回 null），因此必须用 try/catch 接住，再兜底到 STJ 解析。
var builtInDefaults = config;
AppConfig? loadedConfig = null;
try
{
	loadedConfig = AppCore.LoadConfig<AppConfig>("Config/AppConfig.json5", builtInDefaults);
}
catch (Exception ex)
{
	LogMgr.Instance.Error($"框架加载 AppConfig 失败，将改用兜底解析：{ex.Message}");
}
config = loadedConfig ?? LoadAppConfigFromDedicatedFolder() ?? builtInDefaults;

/// <summary>
/// 兜底加载：当框架默认搜索未命中时，从独立配置目录 Config/AppConfig.json5 读取。
/// 该文件为 json5（含 // 注释与尾部逗号），使用 System.Text.Json 并开启注释跳过/尾部逗号兼容。
/// </summary>
AppConfig? LoadAppConfigFromDedicatedFolder()
{
	var candidates = new[]
	{
		Path.Combine(Directory.GetCurrentDirectory(), "Config", "AppConfig.json5"),
		Path.Combine(AppContext.BaseDirectory, "Config", "AppConfig.json5"),
		Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "App", "Config", "AppConfig.json5"),
	};
	foreach (var p in candidates)
	{
		try
		{
			if (!File.Exists(p)) continue;
			var json = File.ReadAllText(p);
			var opts = new JsonSerializerOptions
			{
				ReadCommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			};
			var cfg = JsonSerializer.Deserialize<AppConfig>(json, opts);
			if (cfg is not null) return cfg;
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"兜底加载 AppConfig 失败: {ex.Message}");
		}
	}
	return null;
}
logMgr.Info("启动（THA26003 三站业务版：本地 SQLite + PLC ModbusTCP + 打码/读码/上传）");

// 本地 SQLite 初始化：建目录、规范化绝对路径连接串（回写 AppConfig）、CodeFirst 建表。
// 必须在 UseDatabase 之前执行，保证框架数据库管线与 ThaDb 读同一库文件。
try { ThaDb.Init(config.Database); }
catch (Exception ex)
{
	logMgr.Error($"本地 SQLite 初始化失败（UI 可继续运行，采集/统计不可用）：{ex.Message}", ex);
}

// 自检模式（无界面）：dotnet run --project App -- --selftest
// 用真实生产代码跑生成/校验/状态机/解析/落库全套回归，不需要 PLC 与外设。
if (args.Contains("--selftest"))
{
	var exitCode = ThaSelfTest.Run();
	LogManager.Shutdown();
	return exitCode;
}

// 端到端联调自检（无界面）：dotnet run --project App -- --simtest [秒数]
// 需先启动 PLC 模拟器：dotnet run --project Tools/ModbusSim -- --auto
if (args.Contains("--simtest"))
{
	var seconds = 25;
	var port = 502;
	var idx = Array.IndexOf(args, "--simtest");
	if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var s)) seconds = s;
	if (idx >= 0 && idx + 2 < args.Length && int.TryParse(args[idx + 2], out var pt)) port = pt;
	// 联调默认指向本机模拟器（502 常被 HslCommunicationDemo 占用时用第 3 个参数换端口）
	config.PlcIpAddress = "127.0.0.1";
	config.PlcPort = port;
	var exitCode = await ThaSimTest.RunAsync(config, seconds, port);
	LogManager.Shutdown();
	return exitCode;
}

var app = new App(config).UseLogger().UseDatabase(config.Database).UseUi(AvaApp.StartAsync)
	.AddToIOC(typeof(App).Assembly.GetTypes(), RegistrationMode.Override);

app.TaskServiceManager.ServiceLifecycleEvents.OnEvent += [Inject](serv, status) =>
{
	var message = $"[T-SERVICE] '{serv.Name}' is {status} | {serv.LifecycleError} {serv.TaskError}";
	Console.WriteLine(message);
	logMgr.Info(message);
};

// 注册应用退出事件处理
Console.CancelKeyPress += async (sender, e) =>
{
	e.Cancel = true; // 取消默认的退出行为
	logMgr.Info("检测到退出信号，开始清理资源...");
	await CleanupBeforeExitAsync(app);
	Environment.Exit(0);
};

// 注册AppDomain退出事件
AppDomain.CurrentDomain.ProcessExit += async (sender, e) =>
{
	logMgr.Info("应用程序正在退出，开始清理资源...");
	await CleanupBeforeExitAsync(app);
};

await app.Initialize().UnwarpAsync(errThrow: false, success: _ => { }, onError: r =>
{
	logMgr.Error($"应用初始化失败: {r.Message}");
});
await app.Start().UnwarpAsync(errThrow: false, success: _ => { }, onError: r =>
{
	logMgr.Error($"应用启动失败: {r.Message}");
});
while (true) await Task.Delay(1000);

// 应用退出前的清理方法
static async Task CleanupBeforeExitAsync(App app)
{
	try
	{
		LogMgr.Instance.Info("资源清理完成");
	}
	catch (Exception ex)
	{
		LogMgr.Instance.Error($"清理资源时发生错误: {ex.Message}", ex);
	}
}

public sealed class App(AppConfig config) : AppCore<App, AppConfig>(config)
{
	protected override void OnObjectContainerInitialize(IObjectContainer ioc)
	{
		ioc.AddSingleton<ITaskServiceHostOptions>(creator: _ => Config.TaskServiceHostOptions);
		base.OnObjectContainerInitialize(ioc);
	}

	protected override async ValueTask<Result> OnInitialize(object? context = null, CancellationToken ctk = default)
	{
		// 业务逻辑已移除：不再等待数据库连接、不再建表/补列、不再预创建设备 VM。
		// 直接启动 UI 壳。
		await StartUi();
		if (context is Func<Task> task) await task();
		IOC.GetOrNull<IAppStartUpVM>()?.SetProgress(40, 500);
		return default;
	}

	protected override async ValueTask<Result> OnStart(object? context = null, CancellationToken ctk = default)
	{
		var splashVM = IOC.GetOrNull<IAppStartUpVM>();

		await StartTaskServices();
		splashVM?.SetProgress(100, 500);
		return default;
	}
}

public static partial class Program
{
	static Program()
	{
		// AppDomain.CurrentDomain.AssemblyResolve += DllInit.OnAssemblyResolve;
	}

	public static AppBuilder BuildAvaloniaApp() => AvaApp.Build();
}