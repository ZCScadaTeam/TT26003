using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppZC.Configs;
using ZC.EnhanceApp;
using ZC.Mvvm;

namespace AppZC.Services;

/// <summary>
/// 离线授权服务：
///   - 内置两个固定激活码：<c>ZC0000</c> 永久授权、<c>ZC0001</c> 续期 60 天；
///   - 授权结果落盘到独立授权文件 <c>Config/License.dat</c>（HMAC-SHA256 签名 + 多目录冗余），
///     不再依赖 AppConfig.json5 —— 该文件会被配置页整体重写、被构建拷贝覆盖、被旧实现写坏，
///     旧版本因此每次重启都丢授权；
///   - AppConfig.json5 里的 <c>LicenseExpiry</c> 仅作旧版本兼容与可读备份，回写前会做 JSON 合法性校验，
///     校验不通过宁可不写，绝不破坏配置文件；
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

	/// <summary>授权文件名（签名文本，非 JSON，避免被配置保存逻辑覆盖）。</summary>
	public const string LicenseFileName = "License.dat";

	/// <summary>永久授权上限日（DateTime 可表示的最大日期）。</summary>
	private static readonly DateTime MaxExpiry = new(9999, 12, 31);

	/// <summary>出厂默认到期日（未激活时的试用截止日）。</summary>
	private static readonly DateTime DefaultExpiry = new(2026, 10, 15);

	/// <summary>授权文件签名密钥（只防手改，不做机密用途）。</summary>
	private static readonly byte[] SigningKey =
		SHA256.HashData(Encoding.UTF8.GetBytes("THA26003::AppZC::License::v2::2026"));

	private static readonly UTF8Encoding Utf8NoBom = new(false);

	/// <summary>json5 中 LicenseExpiry 行的匹配模式（容忍缩进、日期/字符串值、行尾逗号与注释）。</summary>
	private static readonly Regex LicenseLineRegex = new(
		@"^(?<indent>[ \t]*)""LicenseExpiry""\s*:\s*(?<raw>""[^""]*""|[^,\r\n}]+)(?<comma>\s*,)?(?<rest>[^\r\n]*)$",
		RegexOptions.Compiled | RegexOptions.Multiline);

	/// <summary>空行压缩（删除损坏行后清理多余空行）。</summary>
	private static readonly Regex MultiBlankLineRegex = new(@"(?:\r?\n)[ \t]*(?:\r?\n)[ \t]*(?:\r?\n)+", RegexOptions.Compiled);

	[Inject] public AppConfig AppConfig { get; set; } = null!;

	/// <summary>当前授权到期日（含当天仍可用；次日 0 点起算到期）。</summary>
	public partial DateTime ExpiryDate { get; set; }

	/// <summary>最近一次生效的激活码（随授权文件一起签名保存，仅用于回看）。</summary>
	public string? ActiveCode { get; private set; }

	/// <summary>当前是否已授权（今天 ≤ 到期日）。</summary>
	public bool IsLicensed => DateTime.Now.Date <= ExpiryDate.Date;

	/// <summary>授权文件候选目录（写入全部，读取时取签名有效且 updated 最新的一份）。</summary>
	public static IReadOnlyList<string> LicenseDirectories { get; } = BuildLicenseDirectories();

	/// <summary>配置文件候选（读取/回写授权行用；不包含源码目录，避免把授权写进工程文件）。</summary>
	public static IReadOnlyList<string> ConfigFileCandidates { get; } = BuildConfigFileCandidates();

	/// <summary>
	/// 从授权文件加载到期日；授权文件缺失时回退到 AppConfig.json5 里的历史值（并迁移落盘），
	/// 再缺失则用出厂默认到期日。
	/// </summary>
	public void LoadFromConfig()
	{
		if (TryLoadPersisted(out var persistedExpiry, out var persistedCode))
		{
			ExpiryDate = persistedExpiry;
			ActiveCode = persistedCode;
			if (AppConfig.LicenseExpiry != ExpiryDate) AppConfig.LicenseExpiry = ExpiryDate;
			return;
		}

		// 旧版本只写了 json5：迁移到独立授权文件，之后 json5 被覆盖/写坏也不会丢授权。
		if (AppConfig.LicenseExpiry != default)
		{
			ExpiryDate = AppConfig.LicenseExpiry;
			Persist(ExpiryDate, null);
			LogMgr.Instance.AddChargeInfo($"[License] 已把 AppConfig.json5 中的到期日 {ExpiryDate:yyyy-MM-dd} 迁移到 {LicenseFileName}");
			return;
		}

		ExpiryDate = DefaultExpiry;
	}

	/// <summary>
	/// 应用激活码。返回 (是否成功, 失败原因)。空串/格式错误/未匹配均视为失败。
	/// 成功时立即落盘（独立授权文件 + json5 备份），重启后保持已激活。
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
			newExpiry = MaxExpiry;
		}
		else if (string.Equals(trimmed, ShortTermActivationCode, StringComparison.Ordinal))
		{
			var from = ExpiryDate.Date < DateTime.Now.Date ? DateTime.Now.Date : ExpiryDate.Date;
			// 已持永久授权时再续期：AddDays 会超出 DateTime 上限，先预判再夹紧到 9999-12-31。
			newExpiry = from > MaxExpiry.AddDays(-ShortTermDays) ? MaxExpiry : from.AddDays(ShortTermDays);
		}
		else
		{
			return (false, "激活码无效，请联系供应商获取正确激活码");
		}

		ExpiryDate = newExpiry;
		ActiveCode = trimmed;
		AppConfig.LicenseExpiry = newExpiry;
		Save();
		LogMgr.Instance.AddChargeInfo($"软件授权已激活，到期日更新为 {newExpiry:yyyy-MM-dd}（已写入 {LicenseFileName}）");
		return (true, null);
	}

	/// <summary>
	/// 落盘当前授权：先写独立授权文件（主依据），再尽力把到期日回写到 AppConfig.json5（可读备份）。
	/// 回写前会校验 JSON 合法性，写成坏文件的情况直接放弃回写。
	/// </summary>
	public void Save()
	{
		Persist(ExpiryDate, ActiveCode);
		TryWriteBackToJson5(ExpiryDate);
	}

	/// <summary>
	/// 启动前自愈：修复旧版本 <see cref="Save"/> 写坏的 AppConfig.json5
	/// （典型症状：LicenseExpiry 行前缺少逗号，导致整个配置解析失败、回退默认值 → 再次要求激活）。
	/// 仅在“文件解析失败且含 LicenseExpiry 行”时动手，修复后仍解析失败则不写。
	/// </summary>
	public static void RepairMalformedConfigFiles()
	{
		foreach (var path in ConfigFileCandidates)
		{
			try
			{
				if (!File.Exists(path)) continue;
				var text = File.ReadAllText(path);
				if (IsValidJson5(text)) continue;

				var match = LicenseLineRegex.Match(text);
				if (!match.Success)
				{
					LogMgr.Instance.Warn($"[License] 配置文件格式错误（与授权行无关，未自动处理）：{path}");
					continue;
				}

				// 先把损坏的授权行整行摘掉 —— 去掉后通常就恢复成合法 json5（尾逗号本来就允许缺）。
				var withoutLine = MultiBlankLineRegex.Replace(LicenseLineRegex.Replace(text, "", 1), m =>
					m.Value.Contains("\r\n") ? "\r\n\r\n" : "\n\n");
				if (!IsValidJson5(withoutLine))
				{
					LogMgr.Instance.Warn($"[License] 配置文件格式错误且无法自动修复：{path}");
					continue;
				}

				var rawDate = match.Groups["raw"].Value.Trim().Trim('"');
				var repaired = DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
					? UpsertLicenseLine(withoutLine, expiry) ?? withoutLine
					: withoutLine;
				if (!IsValidJson5(repaired)) repaired = withoutLine;

				File.WriteAllText(path, repaired, Utf8NoBom);
				LogMgr.Instance.AddChargeInfo($"[License] 已自动修复配置文件（授权行写坏导致配置解析失败）：{path}");
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[License] 修复配置文件失败 {path}: {ex.Message}");
			}
		}
	}

	/// <summary>自检/排障用：读取磁盘上的授权状态，不做任何写入。</summary>
	public static (bool Found, DateTime Expiry, string? Code, string? Path) PeekPersisted()
	{
		var found = TryLoadPersisted(out var expiry, out var code, out var path);
		return (found, expiry, code, path);
	}

	// ============================================================
	// 授权文件读写
	// ============================================================

	private void Persist(DateTime expiry, string? code)
	{
		var expiryText = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var updatedText = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		var payload = BuildPayload(expiryText, code, updatedText);
		var content =
			"# THA26003 软件授权状态文件；请勿手工修改，改动会使签名失效（等同于未激活）。" + Environment.NewLine +
			"# 复制本文件到 Config/ 或 AppData/ 可在另一台机器恢复授权。" + Environment.NewLine +
			$"expiry={expiryText}{Environment.NewLine}" +
			$"code={code ?? ""}{Environment.NewLine}" +
			$"updated={updatedText}{Environment.NewLine}" +
			$"sig={Sign(payload)}{Environment.NewLine}";

		var written = 0;
		foreach (var dir in LicenseDirectories)
		{
			try
			{
				Directory.CreateDirectory(dir);
				File.WriteAllText(Path.Combine(dir, LicenseFileName), content, Utf8NoBom);
				written++;
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[License] 写入授权文件失败 {dir}: {ex.Message}");
			}
		}

		if (written == 0)
		{
			LogMgr.Instance.Error("[License] 授权文件写入失败：所有候选目录都不可写，重启后需要重新激活");
		}
	}

	private static bool TryLoadPersisted(out DateTime expiry, out string? code)
		=> TryLoadPersisted(out expiry, out code, out _);

	private static bool TryLoadPersisted(out DateTime expiry, out string? code, out string? sourcePath)
	{
		expiry = default;
		code = null;
		sourcePath = null;
		var best = DateTime.MinValue;
		var found = false;

		foreach (var dir in LicenseDirectories)
		{
			var path = Path.Combine(dir, LicenseFileName);
			try
			{
				if (!File.Exists(path)) continue;
				var fields = ParseLicenseFile(File.ReadAllText(path));
				if (!fields.TryGetValue("expiry", out var expiryText) ||
				    !fields.TryGetValue("updated", out var updatedText) ||
				    !fields.TryGetValue("sig", out var sigText))
				{
					LogMgr.Instance.Warn($"[License] 授权文件缺少字段，已忽略：{path}");
					continue;
				}

				if (!DateTime.TryParseExact(expiryText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
					    DateTimeStyles.None, out var parsedExpiry))
				{
					LogMgr.Instance.Warn($"[License] 授权文件到期日格式错误，已忽略：{path}");
					continue;
				}

				if (!DateTime.TryParse(updatedText.Trim(), CultureInfo.InvariantCulture,
					    DateTimeStyles.RoundtripKind, out var parsedUpdated))
				{
					parsedUpdated = DateTime.MinValue;
				}

				var parsedCode = fields.TryGetValue("code", out var c) ? c.Trim() : "";
				var payload = BuildPayload(expiryText.Trim(), parsedCode.Length == 0 ? null : parsedCode, updatedText.Trim());
				if (!SignatureEquals(Sign(payload), sigText.Trim()))
				{
					LogMgr.Instance.Warn($"[License] 授权文件签名无效（被手工改过？），已忽略：{path}");
					continue;
				}

				if (found && parsedUpdated <= best) continue;
				best = parsedUpdated;
				expiry = parsedExpiry;
				code = parsedCode.Length == 0 ? null : parsedCode;
				sourcePath = path;
				found = true;
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[License] 读取授权文件失败 {path}: {ex.Message}");
			}
		}

		return found;
	}

	private static Dictionary<string, string> ParseLicenseFile(string text)
	{
		var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#')) continue;
			var idx = line.IndexOf('=');
			if (idx <= 0) continue;
			fields[line[..idx].Trim()] = line[(idx + 1)..];
		}
		return fields;
	}

	private static string BuildPayload(string expiryText, string? code, string updatedText)
		=> $"{expiryText}|{code ?? ""}|{updatedText}";

	private static string Sign(string payload)
		=> Convert.ToHexString(HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes(payload)));

	private static bool SignatureEquals(string left, string right)
		=> CryptographicOperations.FixedTimeEquals(
			Encoding.ASCII.GetBytes(left.ToUpperInvariant()),
			Encoding.ASCII.GetBytes(right.ToUpperInvariant()));

	private static List<string> BuildLicenseDirectories()
	{
		var dirs = new List<string>();
		void Add(string? dir)
		{
			if (string.IsNullOrWhiteSpace(dir)) return;
			var full = Path.GetFullPath(dir);
			if (dirs.Any(d => string.Equals(d, full, StringComparison.OrdinalIgnoreCase))) return;
			dirs.Add(full);
		}

		Add(Path.Combine(AppContext.BaseDirectory, "Config"));
		Add(Path.Combine(AppContext.BaseDirectory, "AppData"));
		Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "THA26003"));
		return dirs;
	}

	private static List<string> BuildConfigFileCandidates()
	{
		var paths = new List<string>();
		void Add(string? path)
		{
			if (string.IsNullOrWhiteSpace(path)) return;
			var full = Path.GetFullPath(path);
			if (paths.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) return;
			paths.Add(full);
		}

		Add(Path.Combine(Directory.GetCurrentDirectory(), "Config", "AppConfig.json5"));
		Add(Path.Combine(AppContext.BaseDirectory, "Config", "AppConfig.json5"));
		return paths;
	}

	// ============================================================
	// json5 回写（仅在能保证文件仍然合法时才写）
	// ============================================================

	private static void TryWriteBackToJson5(DateTime expiry)
	{
		foreach (var path in ConfigFileCandidates)
		{
			try
			{
				if (!File.Exists(path)) continue;
				var text = File.ReadAllText(path);
				var updated = UpsertLicenseLine(text, expiry);
				if (updated is null || updated == text) continue;

				if (!IsValidJson5(updated))
				{
					LogMgr.Instance.Warn($"[License] 放弃回写授权到期日（会让配置失效）：{path}");
					continue;
				}

				File.WriteAllText(path, updated, Utf8NoBom);
			}
			catch (Exception ex)
			{
				LogMgr.Instance.Warn($"[License] 回写授权到期日失败 {path}: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 把 LicenseExpiry 写入 json5 文本：已存在则原地替换值（保留缩进/逗号/行尾注释），
	/// 不存在则在根对象最后一个 <c>}</c> 前插入，并补齐上一行缺失的逗号。
	/// </summary>
	private static string? UpsertLicenseLine(string text, DateTime expiry)
	{
		var expiryText = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var entry = $"\"LicenseExpiry\": \"{expiryText}\"";

		if (LicenseLineRegex.IsMatch(text))
		{
			return LicenseLineRegex.Replace(text, m =>
				$"{m.Groups["indent"].Value}{entry}{m.Groups["comma"].Value}{m.Groups["rest"].Value}", 1);
		}

		var nl = text.Contains("\r\n") ? "\r\n" : "\n";
		var close = text.LastIndexOf('}');
		if (close < 0) return null;
		if (text[(close + 1)..].Trim().Length != 0) return null;

		var head = text[..close].TrimEnd('\r', '\n', ' ', '\t');
		if (head.Length == 0) return null;
		if (!head.EndsWith(',') && !head.EndsWith('{')) head += ",";

		return head + nl + "  " + entry + nl + text[close..];
	}

	private static bool IsValidJson5(string text)
	{
		try
		{
			using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
			{
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});
			return true;
		}
		catch
		{
			return false;
		}
	}
}
