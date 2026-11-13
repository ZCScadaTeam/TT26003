using System;

namespace AppZC.Services;

/// <summary>
/// 读码站判定规则（纯函数，可单测；不依赖设备与数据库）。
///
/// 现场口径（2026-10 起）：
/// 1. **先分清扫码枪"扫上了没"** —— 枪回包命中 <c>ScannerNoReadKeywords</c>（默认 ERROR）或干脆没内容 = 没扫上 → 重试；
///    重试用尽 → 写 <c>%MW10522=2</c>，弹窗「扫码枪未扫到码」。
/// 2. **扫上了 → 与 PLC 附带前站镭码 <c>%MW10580</c> 比对** —— 不一致 → <c>%MW10522=2</c>，弹窗三行
///    「扫码枪：A / PLC搬运获取产品码：B / 不一致」。
/// 3. **一致 → 查库重码** —— 已上传 / NG冻结 / 在制超时 → <c>%MW10522=2</c>，弹窗「重码」。
/// 4. 全部通过 → <c>%MW10522=1</c>。
///
/// 产品记录里的「扫码结果」只有 1=OK / 2=NG（三类 NG 都记 2），细分原因写读码流水表。
/// </summary>
public static class ThaScanJudge
{
	/// <summary>默认的"没扫上"关键词（不区分大小写，逗号/竖线分隔）。</summary>
	public const string DefaultNoReadKeywords = "ERROR|NORED|NO READ|NORESULT|FAIL";

	/// <summary>默认码等级合格阈值：B → A / B 合格，C / D 及更低判 NG。</summary>
	public const string DefaultCodeGradeThreshold = "B";

	/// <summary>
	/// 枪回包是不是"没扫上"（error 包）。
	/// 命中关键词即视为没扫上 → 应重试，而不是当成"扫上了但内容不对"。
	/// </summary>
	public static bool IsNoReadReply(string? raw, string? keywords)
	{
		if (string.IsNullOrWhiteSpace(raw)) return true;

		var kws = SplitKeywords(keywords);
		if (kws.Length == 0) return false;

		var text = raw.Trim();
		foreach (var kw in kws)
		{
			if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	/// <summary>
	/// 枪码与 PLC 附带码是否一致。
	/// 两边任一为空 → 不一致（PLC 没给码时同样判错码，弹窗会显示 PLC 侧为空）。
	/// </summary>
	public static bool CodesMatch(string? gunCode, string? plcCode)
	{
		var a = (gunCode ?? "").Trim();
		var b = (plcCode ?? "").Trim();
		if (a.Length == 0 || b.Length == 0) return false;
		return string.Equals(a, b, StringComparison.Ordinal);
	}

	/// <summary>
	/// 不一致时的原因文本（弹窗正文，三行，现场照抄运维话术）。
	/// <code>
	/// 扫码枪：000133153000026220
	/// PLC搬运获取产品码：000133153000026221
	/// 不一致
	/// </code>
	/// </summary>
	public static string MismatchDetail(string? gunCode, string? plcCode)
		=> $"扫码枪：{(string.IsNullOrWhiteSpace(gunCode) ? "(空)" : gunCode.Trim())}\n"
			+ $"PLC搬运获取产品码：{(string.IsNullOrWhiteSpace(plcCode) ? "(空)" : plcCode.Trim())}\n"
			+ "不一致";

	/// <summary>未扫到码的原因文本（弹窗正文）。</summary>
	public static string NoReadDetail(int attempts, string? gunRaw)
	{
		var text = $"扫码枪未扫到码（共尝试 {attempts} 次）";
		if (!string.IsNullOrWhiteSpace(gunRaw))
			text += $"\n枪返回原文：{Safe(gunRaw)}";
		return text;
	}

	/// <summary>
	/// 码等级是否合格。默认阈值 <see cref="DefaultCodeGradeThreshold"/> = B：A / B 合格，C / D / … 判 NG。
	///
	/// 口径：
	/// <list type="bullet">
	/// <item>阈值留空 = 不做等级判定（只拆等级落库，不影响结果）；</item>
	/// <item>内容里没写等级（枪回包无 <c>:A</c>）= 不判 NG，保持改造前行为；</item>
	/// <item>等级按字母序比：A &lt; B &lt; C &lt; D…，**不高于阈值即合格**（默认阈值 B → A/B 过，C/D/E 不过）；</item>
	/// <item>等级不是字母（如数字 / 符号）= 扫码内容异常 → NG。</item>
	/// </list>
	/// </summary>
	/// <param name="grade">从扫码内容里拆出的等级（可为 null）。</param>
	/// <param name="threshold">配置的合格阈值（如 B；空 = 不判定）。</param>
	/// <param name="reason">不合格原因（合格时为 null）。</param>
	public static bool GradeOk(string? grade, string? threshold, out string? reason)
	{
		reason = null;

		var limit = FirstChar(threshold);
		if (limit == '\0')
			return true;              // 阈值未配 → 不做等级判定

		var g = FirstChar(grade);
		if (g == '\0')
			return true;              // 内容里没写等级 → 不判 NG（向后兼容：无需改枪也不改变现有判结果）

		if (g < 'A' || g > 'Z')
		{
			reason = $"码等级非法({grade!.Trim()})";
			return false;
		}

		if (g > limit)
		{
			reason = $"码等级不符({g}＞阈值{limit})";
			return false;
		}

		return true;
	}

	/// <summary>
	/// 等级阀值是否合法（配置页/自检用）：空（不判定）或 A~Z 单字母。
	/// </summary>
	public static bool IsGradeThresholdValid(string? threshold)
	{
		var c = FirstChar(threshold);
		return c == '\0' || (c >= 'A' && c <= 'Z');
	}

	/// <summary>取首个非空白字符并转大写（用于等级/阈值比较）；全空返回 <c>\0</c>。</summary>
	private static char FirstChar(string? s)
	{
		if (string.IsNullOrWhiteSpace(s)) return '\0';
		foreach (var c in s)
		{
			if (char.IsWhiteSpace(c)) continue;
			return char.ToUpperInvariant(c);
		}
		return '\0';
	}

	private static string[] SplitKeywords(string? keywords)
	{
		var src = string.IsNullOrWhiteSpace(keywords) ? DefaultNoReadKeywords : keywords!;
		return src.Split(['|', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static string Safe(string s, int max = 60)
	{
		var one = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "");
		return one.Length <= max ? one : one[..max] + "…";
	}
}
