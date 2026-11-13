using System.Globalization;
using System.Text;
using AppZC.Configs;

namespace AppZC.Services;

/// <summary>
/// 产品码分段（Hella DMC 等"按位拼装"的码规则）。
/// 每段：名称 / 取值方式 / 取值（固定值或时间格式）/ 定长。
/// 各段按顺序拼接 → 不足 <see cref="Length"/> 左补 0，超长保留低位（右侧）。
/// </summary>
public class CodeSegment
{
	/// <summary>段名（配置页显示，仅注释用，不参与组码）。</summary>
	public string Name { get; set; } = "";

	/// <summary>取值方式：Fixed=固定值 / Time=按时间格式取 / Serial=产品批次号（当天流水）。</summary>
	public string Mode { get; set; } = "Fixed";

	/// <summary>Fixed=固定值原文；Time=日期格式串（如 HHmmss / yy / dd / MMdd / yyddd / DDD）。</summary>
	public string Value { get; set; } = "";

	/// <summary>定长（字符数）。不足左补 0，超长保留低位。</summary>
	public int Length { get; set; } = 1;

	public CodeSegment Clone() => new() { Name = Name, Mode = Mode, Value = Value, Length = Length };
}

/// <summary>码段取值方式。</summary>
public static class CodeSegmentModes
{
	public const string Fixed = "Fixed";
	public const string Time = "Time";
	public const string Serial = "Serial";

	public static readonly string[] All = { Fixed, Time, Serial };

	/// <summary>配置页下拉项（带中文说明）。</summary>
	public static readonly string[] AllWithHint = { "Fixed（固定值）", "Time（时间格式）", "Serial（产品批次号/当天流水）" };

	public static string Parse(string? display)
		=> display switch
		{
			null or "" => Fixed,
			_ when display.StartsWith("Time", StringComparison.OrdinalIgnoreCase) => Time,
			_ when display.StartsWith("Serial", StringComparison.OrdinalIgnoreCase) => Serial,
			_ => Fixed,
		};

	public static string ToDisplay(string? mode)
		=> Parse(mode) switch
		{
			Time => AllWithHint[1],
			Serial => AllWithHint[2],
			_ => AllWithHint[0],
		};
}

/// <summary>
/// 码段拼装：把配置里的段拼成最终打码内容（产品码），并提供反解（校验时间窗用）。
/// 未配置段时回落到旧的"整码 = 纯时间码"模式。
/// </summary>
public static class ThaCodeAssembly
{
	/// <summary>年内天序令牌（DDD / ddd，3 位 001-366）。</summary>
	public const string DayOfYearToken = "DDD";

	/// <summary>启用段拼装？（配置了至少一个有效段）</summary>
	public static bool IsEnabled(AppConfig cfg) => cfg.CodeSegments is { Count: > 0 } list && Effective(list).Any();

	/// <summary>有效段：长度 &gt; 0，且 Fixed/Time 必须有取值。</summary>
	public static IEnumerable<CodeSegment> Effective(IEnumerable<CodeSegment> segments)
		=> segments.Where(s => s != null && s.Length > 0
			&& (CodeSegmentModes.Parse(s.Mode) == CodeSegmentModes.Serial || !string.IsNullOrEmpty(s.Value)));

	/// <summary>码总长（= 各有效段定长之和）。未配置段时返回 0。</summary>
	public static int TotalLength(IEnumerable<CodeSegment> segments)
		=> Effective(segments).Sum(s => s.Length);

	/// <summary>
	/// 按段拼出最终码。<paramref name="serial"/> 供 Serial 段（产品批次号）使用。
	/// <paramref name="now"/> 是拼码用的时间戳：日期段跟生产日走时传
	/// <see cref="ThaProductionDay.Stamp"/>（00:00-07:59 记前一天），否则直接传实际时间。
	/// </summary>
	public static string Build(IReadOnlyList<CodeSegment> segments, DateTime now, int serial)
	{
		var sb = new StringBuilder(32);
		foreach (var seg in Effective(segments))
		{
			var raw = CodeSegmentModes.Parse(seg.Mode) switch
			{
				CodeSegmentModes.Time => FormatTime(now, seg.Value),
				CodeSegmentModes.Serial => Math.Max(0, serial).ToString(CultureInfo.InvariantCulture),
				_ => seg.Value ?? "",
			};
			sb.Append(Fix(raw, seg.Length));
		}
		return sb.ToString();
	}

	/// <summary>配置页示例（用当前时间 + 流水 1，日期段按生产日口径）。</summary>
	public static string Preview(IReadOnlyList<CodeSegment> segments,
		int resetHour = ThaProductionDay.DefaultResetHour, bool dateFollowProductionDay = true)
		=> TotalLength(segments) == 0
			? ""
			: Build(segments,
				dateFollowProductionDay ? ThaProductionDay.Stamp(DateTime.Now, resetHour) : DateTime.Now,
				1);

	/// <summary>
	/// 当前生效的打标码结构（一行说清「到底按哪套分段跑」—— 启动日志用）：
	/// <c>24 位 = 供应商编码(1)+生产日期/天(3)+...+原材料编号(1)；批次号 08:00 清零；日期按生产日；示例 229926120000020800001011</c>。
	/// 未配置分段时说明走纯时间码（无批次流水）。
	/// </summary>
	public static string Describe(AppConfig cfg)
	{
		if (!IsEnabled(cfg))
		{
			return $"纯时间码 {cfg.CodeDateFormat}（期望 {cfg.CodeLength} 位，无批次流水）"
				+ $"；示例 {ThaCodeGenerator.Format(DateTime.Now, cfg.CodeDateFormat)}";
		}

		var list = Effective(cfg.CodeSegments!).ToList();
		var sb = new StringBuilder(160);
		sb.Append($"{TotalLength(list)} 位 = ");
		sb.Append(string.Join("+", list.Select(s => $"{s.Name}({s.Length})")));
		sb.Append($"；批次号 {ThaProductionDay.Normalize(cfg.SerialResetHour):00}:00 清零");
		sb.Append(cfg.DateFollowProductionDay ? "；日期按生产日" : "；日期按自然日");

		var sample = Preview(list, cfg.SerialResetHour, cfg.DateFollowProductionDay);
		if (sample.Length > 0)
			sb.Append($"；示例 {sample}");
		return sb.ToString();
	}

	/// <summary>各段在码里的布局（供反解定位）。</summary>
	public static List<(CodeSegment Segment, int Offset, int Length)> Layout(IReadOnlyList<CodeSegment> segments)
	{
		var list = new List<(CodeSegment, int, int)>();
		var offset = 0;
		foreach (var seg in Effective(segments))
		{
			list.Add((seg, offset, seg.Length));
			offset += seg.Length;
		}
		return list;
	}

	/// <summary>
	/// 格式串里是否含年内天序令牌。
	/// 汽车 DMC 惯例里 ddd 就是年内天数（299 = 第 299 天），
	/// 所以本程序把 DDD / ddd 一律按天序处理，不再按 .NET 的"星期缩写"解释。
	/// </summary>
	public static bool HasDayOfYear(string? format)
		=> !string.IsNullOrEmpty(format)
			&& (format.Contains("DDD", StringComparison.Ordinal) || format.Contains("ddd", StringComparison.Ordinal));

	/// <summary>
	/// 从码里反解时间：把各 Time 段拼起来（年份 / 年内天序 / 月日 / 时刻可以分散在不同段）。
	/// 支持 DDD（001-366）、yy / yyyy、MMdd / MM / dd、HHmmss / HHmmssfff，也支持混在
	/// 一段里的写法（如 yyddd）。只有时刻的写法取离当前时间最近的那一天。
	/// 解析不出来返回 null（上层跳过时间窗校验，不误判）。
	/// </summary>
	public static DateTime? TryParseTime(string code, IReadOnlyList<CodeSegment> segments)
	{
		int? year = null, month = null, day = null, dayOfYear = null;
		TimeSpan? tod = null;
		var any = false;

		foreach (var (seg, offset, len) in Layout(segments))
		{
			if (CodeSegmentModes.Parse(seg.Mode) != CodeSegmentModes.Time) continue;
			if (len <= 0 || code.Length < offset + len) continue;

			var fmt = (seg.Value ?? "").Trim();
			if (fmt.Length == 0) continue;

			var slice = code.Substring(offset, len);
			if (TryParseTimeSlice(slice, fmt, ref year, ref month, ref day, ref dayOfYear, ref tod))
				any = true;
		}

		return any ? Compose(DateTime.Now, year, month, day, dayOfYear, tod) : null;
	}

	// ---------- 反解：段 → 时间分量 ----------

	private static bool TryParseTimeSlice(string slice, string fmt,
		ref int? year, ref int? month, ref int? day, ref int? dayOfYear, ref TimeSpan? tod)
	{
		// 先写进局部量，整段解析成功才提交，避免解析失败污染其它段的结果
		int? lYear = year, lMonth = month, lDay = day, lDoy = dayOfYear;
		var lTod = tod;
		var pos = 0;
		var runStart = -1;
		var any = false;

		for (var i = 0; i < fmt.Length; i++)
		{
			if (IsDayOfYearTokenAt(fmt, i))
			{
				if (!FlushRun(fmt, runStart, i, slice, ref pos, ref any, ref lYear, ref lMonth, ref lDay, ref lTod))
					return false;

				if (pos + 3 > slice.Length) return false;
				if (!int.TryParse(slice.AsSpan(pos, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var doy)
					|| doy < 1 || doy > 366)
					return false;

				lDoy = doy;
				pos += 3;
				any = true;
				runStart = -1;
				i += 2;
			}
			else if (runStart < 0)
			{
				runStart = i;
			}
		}

		if (runStart >= 0
			&& !FlushRun(fmt, runStart, fmt.Length, slice, ref pos, ref any, ref lYear, ref lMonth, ref lDay, ref lTod))
			return false;

		if (!any) return false;

		year = lYear;
		month = lMonth;
		day = lDay;
		dayOfYear = lDoy;
		tod = lTod;
		return true;
	}

	/// <summary>把 [start,end) 这段"非天序"格式连同对应位数的码值交给 DateTime 解析。</summary>
	private static bool FlushRun(string fmt, int start, int end, string slice, ref int pos, ref bool any,
		ref int? year, ref int? month, ref int? day, ref TimeSpan? tod)
	{
		if (start < 0 || end <= start) return true;

		var sub = fmt.Substring(start, end - start);
		if (pos + sub.Length > slice.Length) return false;

		var text = slice.Substring(pos, sub.Length);
		pos += sub.Length;

		if (!ApplyFields(text, sub, ref year, ref month, ref day, ref tod)) return false;
		any = true;
		return true;
	}

	private static bool ApplyFields(string text, string fmt,
		ref int? year, ref int? month, ref int? day, ref TimeSpan? tod)
	{
		var hasYear = fmt.IndexOf('y') >= 0 || fmt.IndexOf('Y') >= 0;
		var hasMonth = fmt.IndexOf('M') >= 0;
		var hasDay = fmt.IndexOf('d') >= 0 || fmt.IndexOf('D') >= 0;
		var hasHour = fmt.IndexOf('H') >= 0 || fmt.IndexOf('h') >= 0;
		var hasMinute = fmt.IndexOf('m') >= 0;
		var hasSecond = fmt.IndexOf('s') >= 0;
		if (!(hasYear || hasMonth || hasDay || hasHour || hasMinute || hasSecond)) return false;

		if (!DateTime.TryParseExact(text, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
			return false;

		if (hasYear) year = dt.Year;
		if (hasMonth) month = dt.Month;
		if (hasDay) day = dt.Day;
		if (hasHour || hasMinute || hasSecond) tod = dt.TimeOfDay;
		return true;
	}

	/// <summary>把各分量组合成具体时间（缺年份时取离当前时间最近的那一年/那一天）。</summary>
	private static DateTime? Compose(DateTime now, int? year, int? month, int? day, int? dayOfYear, TimeSpan? tod)
	{
		DateTime? date = null;

		if (dayOfYear is >= 1 and <= 366)
		{
			var doy = dayOfYear.Value;
			date = year.HasValue
				? DayOfYearDate(year.Value, doy)
				: NearestYear(now, y => DayOfYearDate(y, doy));
		}
		else if (month.HasValue || day.HasValue)
		{
			var m = month ?? 1;
			var d = day ?? 1;
			date = year.HasValue
				? ClampDate(year.Value, m, d)
				: NearestYear(now, y => ClampDate(y, m, d));
		}
		else if (year.HasValue)
		{
			date = new DateTime(year.Value, 1, 1);
		}

		if (date == null)
		{
			if (!tod.HasValue) return null;

			// 只有时刻：取今天 / 昨天 / 明天里离 now 最近的那一天
			var candidate = now.Date.Add(tod.Value);
			if ((candidate - now).TotalHours > 12) candidate = candidate.AddDays(-1);
			else if ((now - candidate).TotalHours > 12) candidate = candidate.AddDays(1);
			return candidate;
		}

		return date.Value.Date + (tod ?? TimeSpan.Zero);
	}

	private static DateTime? DayOfYearDate(int year, int dayOfYear)
	{
		if (year is < 1900 or > 2999) return null;
		var dt = new DateTime(year, 1, 1).AddDays(dayOfYear - 1);
		return dt.Year == year ? dt : null;   // 非闰年 366 → 溢出到次年，按非法日期处理
	}

	private static DateTime? ClampDate(int year, int month, int day)
	{
		if (year is < 1900 or > 2999) return null;
		var m = Math.Clamp(month, 1, 12);
		var d = Math.Clamp(day, 1, DateTime.DaysInMonth(year, m));
		return new DateTime(year, m, d);
	}

	private static DateTime? NearestYear(DateTime now, Func<int, DateTime?> build)
	{
		DateTime? best = null;
		var bestDays = double.MaxValue;
		for (var y = now.Year - 1; y <= now.Year + 1; y++)
		{
			var candidate = build(y);
			if (candidate == null) continue;

			var days = Math.Abs((candidate.Value - now).TotalDays);
			if (days >= bestDays) continue;
			bestDays = days;
			best = candidate;
		}
		return best;
	}

	// ---------- 拼码：时间分量 → 段 ----------

	private static bool IsDayOfYearTokenAt(string fmt, int i)
		=> i + 3 <= fmt.Length
			&& (fmt[i] == 'D' || fmt[i] == 'd')
			&& fmt[i + 1] == fmt[i] && fmt[i + 2] == fmt[i];

	private static string FormatTime(DateTime now, string? format)
	{
		var fmt = string.IsNullOrWhiteSpace(format) ? "yyMMddHHmmss" : format.Trim();
		try
		{
			if (!HasDayOfYear(fmt))
				return now.ToString(fmt, CultureInfo.InvariantCulture);

			// 含年内天序：DDD/ddd 单独算，其余片段交回 DateTime 格式化（支持 yyddd 这种混写）
			var sb = new StringBuilder(32);
			var rest = new StringBuilder();

			void FlushRest()
			{
				if (rest.Length == 0) return;
				sb.Append(now.ToString(rest.ToString(), CultureInfo.InvariantCulture));
				rest.Clear();
			}

			for (var i = 0; i < fmt.Length; i++)
			{
				if (IsDayOfYearTokenAt(fmt, i))
				{
					FlushRest();
					sb.Append(now.DayOfYear.ToString("000", CultureInfo.InvariantCulture));
					i += 2;
					continue;
				}
				rest.Append(fmt[i]);
			}
			FlushRest();

			return sb.ToString();
		}
		catch (FormatException)
		{
			return now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
		}
	}

	/// <summary>定长处理：不足左补 0，超长保留低位（右侧）。</summary>
	private static string Fix(string raw, int length)
	{
		if (length <= 0) return "";
		var s = raw ?? "";
		if (s.Length == length) return s;
		if (s.Length > length) return s[^length..];
		return s.PadLeft(length, '0');
	}
}
