using System.Text.RegularExpressions;

namespace AppZC.Services;

/// <summary>
/// 扫码内容拆解结果。
/// </summary>
/// <param name="Code">产品码（纯码，不含等级）。解析失败时为整串原文。</param>
/// <param name="Grade">码等级（A / B / C…）；内容里没写等级、或解析失败时为 null。</param>
/// <param name="Matched">是否命中解析正则（false = 走的是"整串当码"兜底）。</param>
public readonly record struct ScanCodeParts(string Code, string? Grade, bool Matched)
{
	/// <summary>空内容。</summary>
	public static readonly ScanCodeParts Empty = new("", null, false);
}

/// <summary>
/// 扫码内容 →「产品码 + 码等级」拆解（纯函数，可单测）。
///
/// 现场枪回包是<b>码和等级合写</b>：<c>000143348000026070:A</c>
/// → 码 <c>000143348000026070</c>、等级 <c>A</c>。
/// 正则由 <see cref="Configs.AppConfig.ScannerCodePattern"/> 配置，命名分组固定为
/// <c>code</c>（码）与 <c>grade</c>（等级），默认 <see cref="DefaultPattern"/>。
///
/// 兜底口径（保证改造前后行为连续）：
/// <list type="bullet">
/// <item>内容里没有分隔符（老码 / 未打等级）→ 整串即码，Grade = null；</item>
/// <item>正则配错 / 内容完全不合格式 → 整串当码（Matched=false），
/// 交给后面的长度 / 字符集 / 时间窗校验去判 NG，不在这里吞掉内容；</item>
/// <item>等级大小写原样保留，不改写（避免把有意义的等级改坏）。</item>
/// </list>
/// </summary>
public static class ThaScanCode
{
	/// <summary>
	/// 默认解析正则：码 = 4~32 位数字；可选 <c>:</c> / <c>：</c> 后跟 1~8 位等级（字母或数字）。
	/// </summary>
	public const string DefaultPattern = @"^(?<code>[0-9]{4,32})(?:\s*[:：]\s*(?<grade>[A-Za-z0-9]{1,8}))?$";

	private static readonly Regex DefaultRegex =
		new(DefaultPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

	private static readonly object Gate = new();
	private static string _cachedPattern = DefaultPattern;
	private static Regex _cachedRegex = DefaultRegex;

	/// <summary>
	/// 拆解扫码内容。<paramref name="pattern"/> 为空 → 用 <see cref="DefaultPattern"/>；
	/// 配了非法正则 → 同样回落默认正则（不抛异常打断读码站）。
	/// </summary>
	public static ScanCodeParts Parse(string? text, string? pattern)
	{
		var s = (text ?? "").Trim();
		if (s.Length == 0) return ScanCodeParts.Empty;

		var m = Resolve(pattern).Match(s);
		if (m.Success)
		{
			var code = m.Groups["code"].Success ? m.Groups["code"].Value.Trim() : m.Value.Trim();
			if (code.Length > 0)
			{
				var grade = m.Groups["grade"].Success ? m.Groups["grade"].Value.Trim() : null;
				return new ScanCodeParts(code, string.IsNullOrEmpty(grade) ? null : grade, true);
			}
		}

		// 兜底：正则不认 → 整串当码，等级为空（后续校验链照常判 NG）
		return new ScanCodeParts(s, null, false);
	}

	private static Regex Resolve(string? pattern)
	{
		var raw = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern!.Trim();
		lock (Gate)
		{
			if (string.Equals(_cachedPattern, raw, StringComparison.Ordinal))
				return _cachedRegex;

			try
			{
				_cachedRegex = new Regex(raw, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
				_cachedPattern = raw;
			}
			catch (ArgumentException)
			{
				// 配置里写了非法正则：回落默认，不让读码站整站挂掉
				_cachedRegex = DefaultRegex;
				_cachedPattern = raw;
				LogMgr.Instance.Warn($"[THA-SCAN] ScannerCodePattern 非法（{raw}），本次改用默认正则");
			}
			return _cachedRegex;
		}
	}
}
