using System.Text.RegularExpressions;
using AppZC.Configs;
using ZC.EnhanceApp;
using ZC.Mvvm;

namespace AppZC.Services;

/// <summary>
/// 离线授权服务：
///   - 内置两个固定激活码：<c>ZC0000</c> 永久授权、<c>ZC0001</c> 续期 60 天；
///   - 校验通过后将新的到期日期写入 <see cref="AppConfig.LicenseExpiry"/> 并落盘 AppConfig.json5；
///   - 现场无网环境直接复制激活码到弹窗文本框即可续期。
/// </summary>
[AddToIOC(Lifetime = LifetimeType.Singleton)]
[ObservableObject(IncludeAllPartialProperty = true)]
public sealed partial class LicenseService
{
	/// <summary>永久授权激活码。</summary>
	public const string PermanentActivationCode = "ZC0000";

	/// <summary>短期授权激活码（续 60 天）。</summary>
	public const string ShortTermActivationCode = "ZC0001";

	/// <summary>短期授权续期天数。</summary>
	public const int ShortTermDays = 60;

	/// <summary>出厂默认到期日（首装 30 天试用，从首次运行起算；激活后会被覆盖）。</summary>
	private static readonly DateTime DefaultExpiry = new(2026, 10, 15);

	/// <summary>json5 中 LicenseExpiry 行的匹配模式（容忍缩进、日期/字符串值）。</summary>
	private static readonly Regex LicenseLineRegex = new(
		@"^\s*""LicenseExpiry""\s*:\s*(""[^""]*""|[^,\r\n}]+).*$",
		RegexOptions.Compiled | RegexOptions.Multiline);

	[Inject] public AppConfig AppConfig { get; set; } = null!;

	/// <summary>当前授权到期日（含当天仍可用；次日 0 点起算到期）。</summary>
	public partial DateTime ExpiryDate { get; set; }

	/// <summary>当前是否已授权（今天 ≤ 到期日）。</summary>
	public bool IsLicensed => DateTime.Now.Date <= ExpiryDate.Date;

	/// <summary>
	/// 从 AppConfig 加载授权到期日，缺失时回退默认值。
	/// </summary>
	public void LoadFromConfig()
	{
		ExpiryDate = AppConfig.LicenseExpiry == default
			? DefaultExpiry
			: AppConfig.LicenseExpiry;
	}

	/// <summary>
	/// 应用激活码。返回 (是否成功, 失败原因)。空串/格式错误/未匹配均视为失败。
	/// </summary>
	public (bool Ok, string? Error) ApplyActivation(string? code, out DateTime newExpiry)
	{
		newExpiry = ExpiryDate;
		if (string.IsNullOrWhiteSpace(code))
		{
			return (false, "请输入激活码");
		}

		var trimmed = code.Trim();
		if (string.Equals(trimmed, PermanentActivationCode, StringComparison.Ordinal))
		{
			newExpiry = new DateTime(9999, 12, 31);
		}
		else if (string.Equals(trimmed, ShortTermActivationCode, StringComparison.Ordinal))
		{
			var from = ExpiryDate.Date < DateTime.Now.Date ? DateTime.Now.Date : ExpiryDate.Date;
			newExpiry = from.AddDays(ShortTermDays);
		}
		else
		{
			return (false, "激活码无效，请联系供应商获取正确激活码");
		}

		ExpiryDate = newExpiry;
		AppConfig.LicenseExpiry = newExpiry;
		LogMgr.Instance.AddChargeInfo($"软件授权已激活，到期日更新为 {newExpiry:yyyy-MM-dd}");
		return (true, null);
	}

	/// <summary>
	/// 将当前 <see cref="AppConfig.LicenseExpiry"/> 落盘到 AppConfig.json5。
	/// 已存在则原地替换，不存在则在最后一个 <c>}</c> 前插入（保留 json5 注释/格式）。
	/// 找不到配置文件时静默跳过（运行时仍有效，重启需重输）。
	/// </summary>
	public void Save()
	{
		var candidates = new[]
		{
			Path.Combine(Directory.GetCurrentDirectory(), "Config", "AppConfig.json5"),
			Path.Combine(AppContext.BaseDirectory, "Config", "AppConfig.json5"),
		};

		foreach (var path in candidates)
		{
			if (!File.Exists(path)) continue;
			try
			{
				var text = File.ReadAllText(path);
				var newLine = $"  \"LicenseExpiry\": \"{AppConfig.LicenseExpiry:yyyy-MM-dd}\"";
				var updated = LicenseLineRegex.IsMatch(text)
					? LicenseLineRegex.Replace(text, newLine, 1)
					: InsertBeforeClosingBrace(text, newLine + ",");
				File.WriteAllText(path, updated);
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Error($"持久化授权到期日失败: {ex.Message}");
			}
		}
	}

	private static string InsertBeforeClosingBrace(string text, string newLine)
	{
		// 找最后一个顶层 '}'（行首只有空白 + '}'），在其前插入。
		var idx = text.LastIndexOf('}');
		if (idx <= 0) return text;
		var prefix = text[..idx].TrimEnd('\r', '\n');
		var suffix = text[idx..];
		// 如果 prefix 末尾不是换行符，补一个
		var sep = prefix.EndsWith("\n") ? "" : "\n";
		return prefix + sep + newLine + Environment.NewLine + suffix;
	}
}
