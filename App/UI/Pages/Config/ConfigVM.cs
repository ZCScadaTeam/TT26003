using System.Text;
using AppZC.Services;
using Avalonia.Platform.Storage;
using ZC.Mvvm;

namespace AppZC.UI.Pages;

/// <summary>
/// 配置·连接设置页 VM（PLC 通讯 + 打码站 + 读码站 + 码规则）。
/// 保存后通知 ThaPlcService 使用新配置热重连（并重建外设适配器）。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
[ObservableObject]
public partial class ConfigVM : UiVM<ConfigPage>
{
	[Inject] public AppConfig AppConfig { get; set; } = null!;

	// ===== PLC =====
	public partial string PlcIpAddress { get; set; } = "";
	public partial string PlcPort { get; set; } = "";
	public partial string InovanceSeries { get; set; } = "";
	public partial string PlcAddressPrefix { get; set; } = "";

	/// <summary>汇川 PLC 系列下拉项。</summary>
	public string[] InovanceSeriesOptions { get; } = { "AM", "H3U", "H5U", "Easy", "EVO" };

	// ===== 打码站 =====
	public partial string LaserMode { get; set; } = "";
	public partial string LaserIp { get; set; } = "";
	public partial string LaserPort { get; set; } = "";
	public partial string LaserTemplateName { get; set; } = "";
	public partial string LaserInitTemplate { get; set; } = "";
	public partial string LaserDataTemplate { get; set; } = "";
	public partial string LaserStartTemplate { get; set; } = "";
	public partial string LaserStepTimeoutMs { get; set; } = "";
	public partial bool LaserFrameEnabled { get; set; }
	public partial string LaserOkKeyword { get; set; } = "";
	public partial string LaserEchoRegex { get; set; } = "";
	public partial string LaserTimeoutMs { get; set; } = "";

	/// <summary>打码站模式下拉项。</summary>
	public string[] LaserModeOptions { get; } = { "Tcp", "Mock" };

	// ===== 读码站 =====
	public partial string ScannerMode { get; set; } = "";
	public partial string ScannerIp { get; set; } = "";
	public partial string ScannerPort { get; set; } = "";
	public partial string ScannerTriggerCmd { get; set; } = "";
	public partial string ScannerTerminator { get; set; } = "";
	public partial string ScannerTimeoutMs { get; set; } = "";
	public partial string ScannerMaxAttempts { get; set; } = "";
	public partial string ScannerRetryDelayMs { get; set; } = "";
	/// <summary>静默收尾窗口（ms）：0 = 关闭（等终止符或等满超时）。</summary>
	public partial string ScannerSilenceMs { get; set; } = "";
	/// <summary>枪回包命中这些关键词 = 没扫上（走重试），竖线/逗号分隔。</summary>
	public partial string ScannerNoReadKeywords { get; set; } = "";
	/// <summary>扫码内容拆解正则（命名分组 code=产品码 / grade=码等级）。留空=内置默认。</summary>
	public partial string ScannerCodePattern { get; set; } = "";
	/// <summary>码等级合格阈值（默认 B）：A/B 合格，C/D 及更低判扫码 NG；留空=不判定。</summary>
	public partial string CodeGradeThreshold { get; set; } = "";
	/// <summary>补读回填窗口（秒）：读码晚于落库时回填已落库记录；0=关闭。</summary>
	public partial string LateScanFillSeconds { get; set; } = "";

	/// <summary>读码站模式下拉项（Trigger=软触发 / Push=硬触发推送 / Mock=模拟器）。</summary>
	public string[] ScannerModeOptions { get; } = { "Trigger", "Push", "Mock" };

	// ===== 码规则 =====
	/// <summary>产品码分段（按顺序拼成打码内容）；全空 = 纯时间码模式。</summary>
	public System.Collections.ObjectModel.ObservableCollection<CodeSegmentVM> CodeSegments { get; } = new();

	/// <summary>码段总长显示。</summary>
	public partial string CodeTotalLengthText { get; set; } = "当前 0 位";

	/// <summary>码段示例（按当前时间拼一次）。</summary>
	public partial string CodePreviewText { get; set; } = "示例：--";

	public partial string CodeDateFormat { get; set; } = "";
	public partial string CodeLength { get; set; } = "";
	public partial string CodeTimeWindowMinutes { get; set; } = "";
	public partial string WipTimeoutHours { get; set; } = "";
	public partial bool AllowUnregisteredCode { get; set; }

	/// <summary>产品批次号（Serial 段）清零时刻（点，0-23）。默认 8 = 每天早上 8 点清零。</summary>
	public partial string SerialResetHour { get; set; } = "";

	/// <summary>日期段（yy / DDD / MMdd）是否按生产日记（00:00-07:59 打码的产品日期记前一天）。</summary>
	public partial bool DateFollowProductionDay { get; set; } = true;

	// 清零时刻 / 日期口径一改，示例码跟着变（不然预览和实际打码不一致）
	partial void OnSerialResetHourChanged(string value) => RefreshCodePreview();
	partial void OnDateFollowProductionDayChanged(bool value) => RefreshCodePreview();

	// ===== 数值口径 =====
	public partial string PinHeightFormat { get; set; } = "";
	public partial string PositionFormat { get; set; } = "";

	/// <summary>针高口径下拉项（PLC 侧 DINT，÷1000 显示）。</summary>
	public string[] PinHeightFormatOptions { get; } = { "Int32Cdab", "Int32Abcd" };

	/// <summary>位置度口径下拉项（PLC 侧 REAL）。</summary>
	public string[] PositionFormatOptions { get; } = { "FloatCdab", "FloatAbcd" };

	// ===== 追溯报表 / 数据保留 =====
	public partial bool DailyReportEnabled { get; set; } = true;
	public partial string DailyReportDirectory { get; set; } = "";
	public partial string DailyReportFlushSeconds { get; set; } = "";
	public partial string DataRetentionDays { get; set; } = "";

	public ConfigVM()
	{
		_ = Task.Run(async () =>
		{
			await Task.Delay(100);
			await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(LoadConfig);
		});
	}

	private void LoadConfig()
	{
		PlcIpAddress = AppConfig.PlcIpAddress;
		PlcPort = AppConfig.PlcPort.ToString();
		InovanceSeries = AppConfig.InovanceSeries;
		PlcAddressPrefix = AppConfig.PlcAddressPrefix;

		LaserMode = AppConfig.LaserMode;
		LaserIp = AppConfig.LaserIp;
		LaserPort = AppConfig.LaserPort.ToString();
		LaserTemplateName = AppConfig.LaserTemplateName;
		LaserInitTemplate = AppConfig.LaserInitTemplate;
		LaserDataTemplate = AppConfig.LaserDataTemplate;
		LaserStartTemplate = AppConfig.LaserStartTemplate;
		LaserStepTimeoutMs = AppConfig.LaserStepTimeoutMs.ToString();
		LaserFrameEnabled = AppConfig.LaserFrameEnabled;
		LaserOkKeyword = AppConfig.LaserOkKeyword;
		LaserEchoRegex = AppConfig.LaserEchoRegex;
		LaserTimeoutMs = AppConfig.LaserTimeoutMs.ToString();

		ScannerMode = AppConfig.ScannerMode;
		ScannerIp = AppConfig.ScannerIp;
		ScannerPort = AppConfig.ScannerPort.ToString();
		ScannerTriggerCmd = AppConfig.ScannerTriggerCmd;
		// 终止符是实际控制字符（默认 \r），单行输入框直接显示 CR 会把行高撑开 → 显示层转义
		ScannerTerminator = ToEditableText(AppConfig.ScannerTerminator);
		ScannerTimeoutMs = AppConfig.ScannerTimeoutMs.ToString();
		ScannerMaxAttempts = AppConfig.ScannerMaxAttempts.ToString();
		ScannerRetryDelayMs = AppConfig.ScannerRetryDelayMs.ToString();
		ScannerSilenceMs = AppConfig.ScannerSilenceMs.ToString();
		ScannerNoReadKeywords = AppConfig.ScannerNoReadKeywords;
		ScannerCodePattern = AppConfig.ScannerCodePattern;
		CodeGradeThreshold = AppConfig.CodeGradeThreshold;
		LateScanFillSeconds = AppConfig.LateScanFillSeconds.ToString();

		CodeSegments.Clear();
		foreach (var seg in AppConfig.CodeSegments ?? new List<CodeSegment>())
		{
			var vm = CodeSegmentVM.From(seg);
			vm.Changed += RefreshCodePreview;
			CodeSegments.Add(vm);
		}
		RefreshCodePreview();

		CodeDateFormat = AppConfig.CodeDateFormat;
		CodeLength = AppConfig.CodeLength.ToString();
		CodeTimeWindowMinutes = AppConfig.CodeTimeWindowMinutes.ToString();
		SerialResetHour = AppConfig.SerialResetHour.ToString();
		DateFollowProductionDay = AppConfig.DateFollowProductionDay;
		WipTimeoutHours = AppConfig.WipTimeoutHours.ToString();
		AllowUnregisteredCode = AppConfig.AllowUnregisteredCode;

		PinHeightFormat = AppConfig.PinHeightFormat;
		PositionFormat = AppConfig.PositionFormat;

		DailyReportEnabled = AppConfig.DailyReportEnabled;
		DailyReportDirectory = AppConfig.DailyReportDirectory;
		DailyReportFlushSeconds = AppConfig.DailyReportFlushSeconds.ToString();
		DataRetentionDays = AppConfig.DataRetentionDays.ToString();
	}

	/// <summary>保存配置（校验 → 回写 AppConfig → 落 json5 → 通知热重连）。</summary>
	public async Task @SaveConfig()
	{
		try
		{
			if (!TryPort(PlcPort, out var plcPort, "PLC"))
				return;
			if (!TryPort(LaserPort, out var laserPort, "打标机"))
				return;
			if (!TryPort(ScannerPort, out var scannerPort, "扫码枪"))
				return;
			if (string.IsNullOrWhiteSpace(PlcIpAddress) || string.IsNullOrWhiteSpace(LaserIp) || string.IsNullOrWhiteSpace(ScannerIp))
			{
				_ = ShowNotification("IP 地址不能为空", UiMessageType.Error);
				return;
			}
			if (!TryInt(LaserTimeoutMs, 100, 60000, "打标超时", out var laserTimeout))
				return;
			if (!TryInt(LaserStepTimeoutMs, 100, 60000, "分步回执超时", out var laserStepTimeout))
				return;
			if (!TryInt(ScannerTimeoutMs, 100, 60000, "读码超时", out var scannerTimeout))
				return;
			if (!TryInt(ScannerMaxAttempts, 1, 10, "扫码尝试次数", out var scannerAttempts))
				return;
			if (!TryInt(ScannerRetryDelayMs, 0, 5000, "扫码重试间隔", out var scannerRetryDelay))
				return;
			if (!TryInt(ScannerSilenceMs, 0, 5000, "静默收尾窗口", out var scannerSilence))
				return;
			if (!TryInt(CodeLength, 1, 64, "码长度", out var codeLength))
				return;
			if (!TryInt(CodeTimeWindowMinutes, 0, 1440, "时间窗", out var timeWindow))
				return;
			if (!TryInt(SerialResetHour, 0, 23, "批次号清零时刻", out var resetHour))
				return;
			if (!TryInt(WipTimeoutHours, 0, 720, "在制超时", out var wipTimeout))
				return;
			if (string.IsNullOrWhiteSpace(CodeDateFormat))
			{
				_ = ShowNotification("码格式不能为空", UiMessageType.Error);
				return;
			}
			if (!TryInt(DailyReportFlushSeconds, 1, 86400, "报表落盘间隔(秒)", out var flushSeconds))
				return;
			if (!TryInt(DataRetentionDays, 0, 36500, "数据保留天数", out var retentionDays))
				return;
			if (DailyReportEnabled && !string.IsNullOrWhiteSpace(DailyReportDirectory))
			{
				try
				{
					System.IO.Directory.CreateDirectory(System.IO.Path.GetFullPath(
						Environment.ExpandEnvironmentVariables(DailyReportDirectory.Trim()),
						AppContext.BaseDirectory));
				}
				catch (Exception ex)
				{
					_ = ShowNotification($"报表目录不可用：{ex.Message}", UiMessageType.Error);
					return;
				}
			}

			AppConfig.PlcIpAddress = PlcIpAddress.Trim();
			AppConfig.PlcPort = plcPort;
			AppConfig.InovanceSeries = InovanceSeries;
			AppConfig.PlcAddressPrefix = PlcAddressPrefix;

			AppConfig.LaserMode = LaserMode;
			AppConfig.LaserIp = LaserIp.Trim();
			AppConfig.LaserPort = laserPort;
			AppConfig.LaserTemplateName = LaserTemplateName.Trim();
			AppConfig.LaserInitTemplate = LaserInitTemplate;
			AppConfig.LaserDataTemplate = LaserDataTemplate;
			AppConfig.LaserStartTemplate = LaserStartTemplate;
			AppConfig.LaserStepTimeoutMs = laserStepTimeout;
			AppConfig.LaserFrameEnabled = LaserFrameEnabled;
			AppConfig.LaserOkKeyword = LaserOkKeyword;
			AppConfig.LaserEchoRegex = LaserEchoRegex;
			AppConfig.LaserTimeoutMs = laserTimeout;

			AppConfig.ScannerMode = ScannerMode;
			AppConfig.ScannerIp = ScannerIp.Trim();
			AppConfig.ScannerPort = scannerPort;
			AppConfig.ScannerTriggerCmd = ScannerTriggerCmd;
			AppConfig.ScannerTerminator = FromEditableText(ScannerTerminator);
			AppConfig.ScannerTimeoutMs = scannerTimeout;
			AppConfig.ScannerMaxAttempts = scannerAttempts;
			AppConfig.ScannerRetryDelayMs = scannerRetryDelay;
			AppConfig.ScannerSilenceMs = scannerSilence;
			AppConfig.ScannerNoReadKeywords = string.IsNullOrWhiteSpace(ScannerNoReadKeywords)
				? ThaScanJudge.DefaultNoReadKeywords
				: ScannerNoReadKeywords.Trim();
			AppConfig.ScannerCodePattern = (ScannerCodePattern ?? "").Trim();
			// 码等级阈值：空 = 不判定；否则取首字母大写；非法值（非 A-Z）回落默认 B
			var gradeLimit = (CodeGradeThreshold ?? "").Trim().ToUpperInvariant();
			if (gradeLimit.Length > 0 && !char.IsAsciiLetter(gradeLimit[0]))
				gradeLimit = ThaScanJudge.DefaultCodeGradeThreshold;
			AppConfig.CodeGradeThreshold = gradeLimit.Length == 0 ? "" : gradeLimit[..1];
			if (int.TryParse(LateScanFillSeconds, out var lateFill) && lateFill >= 0)
				AppConfig.LateScanFillSeconds = Math.Min(lateFill, 3600);

			AppConfig.CodeSegments = CodeSegments.Select(x => x.ToModel()).ToList();

			AppConfig.CodeDateFormat = CodeDateFormat.Trim();
			AppConfig.CodeLength = codeLength;
			AppConfig.CodeTimeWindowMinutes = timeWindow;
			AppConfig.SerialResetHour = resetHour;
			AppConfig.DateFollowProductionDay = DateFollowProductionDay;
			AppConfig.WipTimeoutHours = wipTimeout;
			AppConfig.AllowUnregisteredCode = AllowUnregisteredCode;

			AppConfig.PinHeightFormat = PinHeightFormat;
			AppConfig.PositionFormat = PositionFormat;

			AppConfig.DailyReportEnabled = DailyReportEnabled;
			AppConfig.DailyReportDirectory = DailyReportDirectory.Trim();
			AppConfig.DailyReportFlushSeconds = flushSeconds;
			AppConfig.DataRetentionDays = retentionDays;

			await SaveConfigToFile();

			App.Current.IOC.GetOrNull<ThaPlcService>()?.NotifyConfigChanged();

			// 报表目录/开关可能变了：停掉旧写线程（会先把当天数据落盘），按新配置重启。
			// 保留期清理不在这里做——统一放到程序退出时（有旧数据才弹窗）。
			try
			{
				ThaDailyReport.Shutdown();
				ThaDailyReport.Init(AppConfig);
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"报表服务按新配置重启失败（配置已保存，重启程序后生效）：{ex.Message}", ex);
			}

			_ = ShowNotification("配置保存成功，PLC 服务将使用新配置重连（并重建外设）", UiMessageType.Success);
			LogMgr.Instance.AddChargeInfo("系统配置已保存");
		}
		catch (Exception ex)
		{
			_ = ShowNotification($"保存配置失败：{ex.Message}", UiMessageType.Error);
			LogMgr.Instance.Error($"保存配置失败：{ex.Message}");
		}
	}

	private bool TryPort(string text, out int port, string label)
	{
		port = 0;
		if (!int.TryParse(text, out port) || port < 1 || port > 65535)
		{
			_ = ShowNotification($"{label}端口号无效，请输入 1-65535", UiMessageType.Error);
			return false;
		}
		return true;
	}

	private bool TryInt(string text, int min, int max, string label, out int value)
	{
		value = 0;
		if (!int.TryParse(text, out value) || value < min || value > max)
		{
			_ = ShowNotification($"{label}无效，请输入 {min}-{max}", UiMessageType.Error);
			return false;
		}
		return true;
	}

	/// <summary>保存配置到文件（json5，双写输出目录与源码目录）。</summary>
	private async Task SaveConfigToFile()
	{
		var configPath = "Config/AppConfig.json5";

		// 码段数组（json5 片段）：每行一段
		var codeSegmentsJson = string.Join(",\n", (AppConfig.CodeSegments ?? new List<CodeSegment>())
			.Select(x => $"    {{ \"Name\": \"{Escape(x.Name)}\", \"Mode\": \"{Escape(x.Mode)}\", \"Value\": \"{Escape(x.Value)}\", \"Length\": {x.Length} }}"));

		try
		{
			var connEscaped = AppConfig.Database.ConnectionString.Replace("\\", "\\\\").Replace("\"", "\\\"");
			var json5Content = $@"{{
  ""Database"": {{
    ""DbType"": ""{AppConfig.Database.DbType}"",
    ""ConnectionString"": ""{connEscaped}"",
    ""IsAutoCloseConnection"": true
  }},

  // ===== PLC 通讯（THA26003 ModbusTCP）=====
  ""PlcIpAddress"": ""{AppConfig.PlcIpAddress}"",
  ""PlcPort"": {AppConfig.PlcPort},
  // 汇川 PLC 系列（InovanceTcpNet.Series）：AM / H3U / H5U / Easy / EVO
  ""InovanceSeries"": ""{AppConfig.InovanceSeries}"",
  // 设备地址前缀（汇川软元件名，默认 M；设为空串 = 纯数字 Modbus 地址）
  ""PlcAddressPrefix"": ""{AppConfig.PlcAddressPrefix}"",
  // Modbus 地址映射规则（Rev.2 地址全部 ≤65535，默认留空 = 直传）
  ""ModbusAddressRules"": [],

  // ===== 打码站（大族激光 TCP/IP）=====
  ""LaserMode"": ""{AppConfig.LaserMode}"",
  ""LaserIp"": ""{AppConfig.LaserIp}"",
  ""LaserPort"": {AppConfig.LaserPort},
  ""LaserTemplateName"": ""{Escape(AppConfig.LaserTemplateName)}"",
  ""LaserInitTemplate"": ""{Escape(AppConfig.LaserInitTemplate)}"",
  ""LaserDataTemplate"": ""{Escape(AppConfig.LaserDataTemplate)}"",
  ""LaserStartTemplate"": ""{Escape(AppConfig.LaserStartTemplate)}"",
  ""LaserCommandEnd"": ""{Escape(AppConfig.LaserCommandEnd)}"",
  ""LaserStepTimeoutMs"": {AppConfig.LaserStepTimeoutMs},
  ""LaserFrameEnabled"": {(AppConfig.LaserFrameEnabled ? "true" : "false")},
  ""LaserOkKeyword"": ""{AppConfig.LaserOkKeyword}"",
  ""LaserEchoRegex"": ""{Escape(AppConfig.LaserEchoRegex)}"",
  ""LaserReplyTerminator"": ""{Escape(AppConfig.LaserReplyTerminator)}"",
  ""LaserEncoding"": ""{AppConfig.LaserEncoding}"",
  ""LaserTimeoutMs"": {AppConfig.LaserTimeoutMs},
  ""LaserConnectTimeoutMs"": {AppConfig.LaserConnectTimeoutMs},
  ""LaserKeepAlive"": {(AppConfig.LaserKeepAlive ? "true" : "false")},
  ""LaserMockDelayMs"": {AppConfig.LaserMockDelayMs},

  // ===== 码规则（分段拼装 / 纯时间码）=====
  // 产品码分段（按顺序拼接）：Mode = Fixed 固定值 / Time 时间格式 / Serial 产品批次号
  ""CodeSegments"": [
{codeSegmentsJson}
  ],
  ""CodeDateFormat"": ""{AppConfig.CodeDateFormat}"",
  ""CodeLength"": {AppConfig.CodeLength},
  ""CodeTimeWindowMinutes"": {AppConfig.CodeTimeWindowMinutes},
  // 产品批次号（Serial 段）清零时刻：默认 8 = 每天早上 8 点清零（客户确认）
  ""SerialResetHour"": {AppConfig.SerialResetHour},
  // 日期段（yy / DDD / MMdd）是否按生产日记（00:00-07:59 记前一天）
  ""DateFollowProductionDay"": {(AppConfig.DateFollowProductionDay ? "true" : "false")},
  ""DuplicateCheckScope"": ""{AppConfig.DuplicateCheckScope}"",
  ""WipTimeoutHours"": {AppConfig.WipTimeoutHours},
  ""AllowUnregisteredCode"": {(AppConfig.AllowUnregisteredCode ? "true" : "false")},
  ""CodeMaxWaitMs"": {AppConfig.CodeMaxWaitMs},

  // ===== 读码站（基恩士 SR-1000 TCP/IP）=====
  ""ScannerMode"": ""{AppConfig.ScannerMode}"",
  ""ScannerIp"": ""{AppConfig.ScannerIp}"",
  ""ScannerPort"": {AppConfig.ScannerPort},
  ""ScannerTriggerCmd"": ""{Escape(AppConfig.ScannerTriggerCmd)}"",
  ""ScannerStopCmd"": ""{Escape(AppConfig.ScannerStopCmd)}"",
  ""ScannerCommandEnd"": ""{Escape(AppConfig.ScannerCommandEnd)}"",
  ""ScannerTerminator"": ""{Escape(AppConfig.ScannerTerminator)}"",
  ""ScannerDataRegex"": ""{Escape(AppConfig.ScannerDataRegex)}"",
  ""ScannerEncoding"": ""{AppConfig.ScannerEncoding}"",
  ""ScannerTimeoutMs"": {AppConfig.ScannerTimeoutMs},
  ""ScannerMaxAttempts"": {AppConfig.ScannerMaxAttempts},
  ""ScannerRetryDelayMs"": {AppConfig.ScannerRetryDelayMs},
  ""ScannerSilenceMs"": {AppConfig.ScannerSilenceMs},
  ""ScannerNoReadKeywords"": ""{Escape(AppConfig.ScannerNoReadKeywords)}"",
  ""ScannerCodePattern"": ""{Escape(AppConfig.ScannerCodePattern)}"",
  ""CodeGradeThreshold"": ""{AppConfig.CodeGradeThreshold}"",
  ""LateScanFillSeconds"": {AppConfig.LateScanFillSeconds},
  ""ScannerConnectTimeoutMs"": {AppConfig.ScannerConnectTimeoutMs},
  ""ScannerKeepAlive"": {(AppConfig.ScannerKeepAlive ? "true" : "false")},
  ""ScannerMockDelayMs"": {AppConfig.ScannerMockDelayMs},

  // ===== 数值口径 =====
  // 针高：PLC 侧 DINT（整数，÷1000 显示）
  ""PinHeightFormat"": ""{AppConfig.PinHeightFormat}"",
  // 位置度：PLC 侧 REAL（浮点）
  ""PositionFormat"": ""{AppConfig.PositionFormat}"",

  ""TaskServiceHostOptions"": {{
    ""MaxRemainStartCount"": -1,
    ""EnableAutoRestart"": true,
    ""IsUseThreadLongRunningMode"": false
  }},

  ""GarnetServer"": {{
    ""Port"": 1234,
    ""Address"": ""127.0.0.1"",
    ""MemorySize"": ""2g""
  }},

  ""LogFilePath"": ""{Escape(AppConfig.LogFilePath)}"",

  // ===== 每日追溯报表 / 本地数据保留 =====
  ""DailyReportEnabled"": {(AppConfig.DailyReportEnabled ? "true" : "false")},
  ""DailyReportDirectory"": ""{Escape(AppConfig.DailyReportDirectory)}"",
  ""DailyReportFlushSeconds"": {AppConfig.DailyReportFlushSeconds},
  ""DataRetentionDays"": {AppConfig.DataRetentionDays},

  // ===== 软件授权（由激活窗口自动写入，请勿手改；独立授权文件 Config/License.dat 优先）=====
  ""LicenseExpiry"": ""{AppConfig.LicenseExpiry:yyyy-MM-dd}""
}}";

			await System.IO.File.WriteAllTextAsync(configPath, json5Content);

			var sourceConfigPath = System.IO.Path.Combine(
				System.IO.Directory.GetCurrentDirectory(),
				"..", "..", "..",
				"App", "Config", "AppConfig.json5"
			);

			if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(sourceConfigPath)))
			{
				await System.IO.File.WriteAllTextAsync(sourceConfigPath, json5Content);
			}
		}
		catch (Exception ex)
		{
			LogMgr.Instance.Error($"保存配置文件失败：{ex.Message}\n{ex.StackTrace}");
			throw;
		}
	}

	private static string Escape(string? s)
		=> (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

	/// <summary>
	/// 控制字符 → 可读转义文本（\r / \n / \t / \\），用于单行输入框显示。
	/// 目的：终止符这类字段的真实值是 CR/LF，直接绑定到 TextBox 会把行高撑开。
	/// </summary>
	private static string ToEditableText(string? value)
	{
		var text = value ?? "";
		if (text.IndexOfAny(['\r', '\n', '\t', '\\']) < 0) return text;

		var sb = new StringBuilder(text.Length + 8);
		foreach (var ch in text)
		{
			switch (ch)
			{
				case '\\': sb.Append("\\\\"); break;
				case '\r': sb.Append("\\r"); break;
				case '\n': sb.Append("\\n"); break;
				case '\t': sb.Append("\\t"); break;
				default: sb.Append(ch); break;
			}
		}
		return sb.ToString();
	}

	/// <summary>
	/// 可读转义文本 → 控制字符（<see cref="ToEditableText"/> 的逆过程）。
	/// 未知转义（如 \x）原样保留，不做猜测。
	/// </summary>
	private static string FromEditableText(string? value)
	{
		var text = value ?? "";
		if (text.IndexOf('\\') < 0) return text;

		var sb = new StringBuilder(text.Length);
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '\\' && i + 1 < text.Length)
			{
				switch (text[i + 1])
				{
					case 'r': sb.Append('\r'); i++; continue;
					case 'n': sb.Append('\n'); i++; continue;
					case 't': sb.Append('\t'); i++; continue;
					case '\\': sb.Append('\\'); i++; continue;
				}
			}
			sb.Append(text[i]);
		}
		return sb.ToString();
	}

	/// <summary>报表默认目录（仅作界面提示）。</summary>
	public string DefaultReportDirectoryHint => AppZC.Services.ThaDailyReport.DefaultDirectory;

	/// <summary>选择报表输出目录（文件夹选择器）。</summary>
	public async Task @BrowseReportDirectory()
	{
		var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
			Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
				? desktop.MainWindow
				: null;
		if (topLevel == null)
		{
			_ = ShowNotification("无法获取主窗口", UiMessageType.Error);
			return;
		}

		var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			Title = "选择追溯报表输出目录",
			AllowMultiple = false,
		});
		if (folders.Count == 0) return;
		DailyReportDirectory = folders[0].Path.LocalPath;
	}

	/// <summary>刷新"总长/示例"（段内容变更时调用）。</summary>
	private void RefreshCodePreview()
	{
		var models = CodeSegments.Select(x => x.ToModel()).ToList();
		var total = ThaCodeAssembly.TotalLength(models);
		var resetHour = int.TryParse(SerialResetHour, out var h) ? h : ThaProductionDay.DefaultResetHour;
		CodeTotalLengthText = total == 0 ? "当前 0 位（用纯时间码）" : $"当前共 {total} 位";
		CodePreviewText = total == 0 ? "示例：--" : $"示例：{ThaCodeAssembly.Preview(models, resetHour, DateFollowProductionDay)}";
	}

	/// <summary>新增一个空白码段。</summary>
	[RelayCommand]
	private void AddCodeSegment()
	{
		var vm = new CodeSegmentVM { Name = $"段{CodeSegments.Count + 1}", ModeDisplay = CodeSegmentModes.AllWithHint[0], Value = "0", Length = "1" };
		vm.Changed += RefreshCodePreview;
		CodeSegments.Add(vm);
		RefreshCodePreview();
	}

	/// <summary>删除最后一段（界面里选中行删除由 SelectedCodeSegment 驱动）。</summary>
	[RelayCommand]
	private void RemoveCodeSegment(CodeSegmentVM? row)
	{
		var target = row ?? SelectedCodeSegment;
		if (target == null) return;
		target.Changed -= RefreshCodePreview;
		CodeSegments.Remove(target);
		RefreshCodePreview();
	}

	/// <summary>选中行（DataGrid 双向绑定）。</summary>
	public partial CodeSegmentVM? SelectedCodeSegment { get; set; }

	/// <summary>上移选中段。</summary>
	[RelayCommand]
	private void MoveCodeSegmentUp()
	{
		var row = SelectedCodeSegment;
		if (row == null) return;
		var i = CodeSegments.IndexOf(row);
		if (i <= 0) return;
		CodeSegments.Move(i, i - 1);
		SelectedCodeSegment = row;
		RefreshCodePreview();
	}

	/// <summary>下移选中段。</summary>
	[RelayCommand]
	private void MoveCodeSegmentDown()
	{
		var row = SelectedCodeSegment;
		if (row == null) return;
		var i = CodeSegments.IndexOf(row);
		if (i < 0 || i >= CodeSegments.Count - 1) return;
		CodeSegments.Move(i, i + 1);
		SelectedCodeSegment = row;
		RefreshCodePreview();
	}

	/// <summary>重置配置。</summary>
	public void @ResetConfig()
	{
		LoadConfig();
		_ = ShowNotification("配置已重置", UiMessageType.Success);
	}
}
