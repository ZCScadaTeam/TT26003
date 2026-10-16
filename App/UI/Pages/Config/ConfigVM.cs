using AppZC.Services;
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
	public partial string LaserCommandTemplate { get; set; } = "";
	public partial string LaserOkKeyword { get; set; } = "";
	public partial string LaserEchoRegex { get; set; } = "";
	public partial string LaserTimeoutMs { get; set; } = "";
	public partial string MarkContentSource { get; set; } = "";

	/// <summary>打码站模式下拉项。</summary>
	public string[] LaserModeOptions { get; } = { "Tcp", "Mock" };

	/// <summary>打码内容来源下拉项。</summary>
	public string[] MarkContentSourceOptions { get; } = { "Auto", "Plc" };

	// ===== 读码站 =====
	public partial string ScannerMode { get; set; } = "";
	public partial string ScannerIp { get; set; } = "";
	public partial string ScannerPort { get; set; } = "";
	public partial string ScannerTriggerCmd { get; set; } = "";
	public partial string ScannerTerminator { get; set; } = "";
	public partial string ScannerTimeoutMs { get; set; } = "";

	/// <summary>读码站模式下拉项（Trigger=软触发 / Push=硬触发推送 / Mock=模拟器）。</summary>
	public string[] ScannerModeOptions { get; } = { "Trigger", "Push", "Mock" };

	// ===== 码规则 =====
	public partial string CodeDateFormat { get; set; } = "";
	public partial string CodeLength { get; set; } = "";
	public partial string CodeTimeWindowMinutes { get; set; } = "";
	public partial string WipTimeoutHours { get; set; } = "";
	public partial bool AllowUnregisteredCode { get; set; }

	// ===== 数值口径 =====
	public partial string PinHeightFormat { get; set; } = "";
	public partial string PositionFormat { get; set; } = "";

	/// <summary>针高口径下拉项（PLC 侧 DINT，÷1000 显示）。</summary>
	public string[] PinHeightFormatOptions { get; } = { "Int32Div1000", "Int32SwappedDiv1000" };

	/// <summary>位置度口径下拉项（PLC 侧 REAL）。</summary>
	public string[] PositionFormatOptions { get; } = { "FloatCdab", "FloatAbcd" };

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
		LaserCommandTemplate = AppConfig.LaserCommandTemplate;
		LaserOkKeyword = AppConfig.LaserOkKeyword;
		LaserEchoRegex = AppConfig.LaserEchoRegex;
		LaserTimeoutMs = AppConfig.LaserTimeoutMs.ToString();
		MarkContentSource = AppConfig.MarkContentSource;

		ScannerMode = AppConfig.ScannerMode;
		ScannerIp = AppConfig.ScannerIp;
		ScannerPort = AppConfig.ScannerPort.ToString();
		ScannerTriggerCmd = AppConfig.ScannerTriggerCmd;
		ScannerTerminator = AppConfig.ScannerTerminator;
		ScannerTimeoutMs = AppConfig.ScannerTimeoutMs.ToString();

		CodeDateFormat = AppConfig.CodeDateFormat;
		CodeLength = AppConfig.CodeLength.ToString();
		CodeTimeWindowMinutes = AppConfig.CodeTimeWindowMinutes.ToString();
		WipTimeoutHours = AppConfig.WipTimeoutHours.ToString();
		AllowUnregisteredCode = AppConfig.AllowUnregisteredCode;

		PinHeightFormat = AppConfig.PinHeightFormat;
		PositionFormat = AppConfig.PositionFormat;
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
			if (!TryInt(ScannerTimeoutMs, 100, 60000, "读码超时", out var scannerTimeout))
				return;
			if (!TryInt(CodeLength, 1, 64, "码长度", out var codeLength))
				return;
			if (!TryInt(CodeTimeWindowMinutes, 0, 1440, "时间窗", out var timeWindow))
				return;
			if (!TryInt(WipTimeoutHours, 0, 720, "在制超时", out var wipTimeout))
				return;
			if (string.IsNullOrWhiteSpace(CodeDateFormat))
			{
				_ = ShowNotification("码格式不能为空", UiMessageType.Error);
				return;
			}

			AppConfig.PlcIpAddress = PlcIpAddress.Trim();
			AppConfig.PlcPort = plcPort;
			AppConfig.InovanceSeries = InovanceSeries;
			AppConfig.PlcAddressPrefix = PlcAddressPrefix;

			AppConfig.LaserMode = LaserMode;
			AppConfig.LaserIp = LaserIp.Trim();
			AppConfig.LaserPort = laserPort;
			AppConfig.LaserCommandTemplate = LaserCommandTemplate;
			AppConfig.LaserOkKeyword = LaserOkKeyword;
			AppConfig.LaserEchoRegex = LaserEchoRegex;
			AppConfig.LaserTimeoutMs = laserTimeout;
			AppConfig.MarkContentSource = MarkContentSource;

			AppConfig.ScannerMode = ScannerMode;
			AppConfig.ScannerIp = ScannerIp.Trim();
			AppConfig.ScannerPort = scannerPort;
			AppConfig.ScannerTriggerCmd = ScannerTriggerCmd;
			AppConfig.ScannerTerminator = ScannerTerminator;
			AppConfig.ScannerTimeoutMs = scannerTimeout;

			AppConfig.CodeDateFormat = CodeDateFormat.Trim();
			AppConfig.CodeLength = codeLength;
			AppConfig.CodeTimeWindowMinutes = timeWindow;
			AppConfig.WipTimeoutHours = wipTimeout;
			AppConfig.AllowUnregisteredCode = AllowUnregisteredCode;

			AppConfig.PinHeightFormat = PinHeightFormat;
			AppConfig.PositionFormat = PositionFormat;

			await SaveConfigToFile();

			App.Current.IOC.GetOrNull<ThaPlcService>()?.NotifyConfigChanged();

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
  ""LaserCommandTemplate"": ""{Escape(AppConfig.LaserCommandTemplate)}"",
  ""LaserOkKeyword"": ""{AppConfig.LaserOkKeyword}"",
  ""LaserEchoRegex"": ""{Escape(AppConfig.LaserEchoRegex)}"",
  ""LaserReplyTerminator"": ""{Escape(AppConfig.LaserReplyTerminator)}"",
  ""LaserEncoding"": ""{AppConfig.LaserEncoding}"",
  ""LaserTimeoutMs"": {AppConfig.LaserTimeoutMs},
  ""LaserConnectTimeoutMs"": {AppConfig.LaserConnectTimeoutMs},
  ""LaserKeepAlive"": {(AppConfig.LaserKeepAlive ? "true" : "false")},
  ""LaserMockDelayMs"": {AppConfig.LaserMockDelayMs},
  ""MarkContentSource"": ""{AppConfig.MarkContentSource}"",

  // ===== 码规则（纯时间码）=====
  ""CodeDateFormat"": ""{AppConfig.CodeDateFormat}"",
  ""CodeLength"": {AppConfig.CodeLength},
  ""CodeTimeWindowMinutes"": {AppConfig.CodeTimeWindowMinutes},
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

  ""LogFilePath"": ""{Escape(AppConfig.LogFilePath)}""
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

	/// <summary>重置配置。</summary>
	public void @ResetConfig()
	{
		LoadConfig();
		_ = ShowNotification("配置已重置", UiMessageType.Success);
	}
}
