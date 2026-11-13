using System.Globalization;
using AppZC.Configs;

namespace AppZC.Services;

/// <summary>
/// 纯时间码生成器。
/// 格式由 <see cref="Configs.AppConfig.CodeDateFormat"/> 决定，默认 12 位 <c>yyMMddHHmmss</c>，
/// 节拍 &lt;1s 时可切 15 位 <c>yyMMddHHmmssfff</c>（需客户同意，仅改配置）。
///
/// 唯一性保障：生成前查码索引，命中则**等到下一个整秒/毫秒**再取时间，
/// 因此码里的时间戳始终是真实的打码时刻，不伪造顺延；格式也不破。
/// 产线节拍 ≥1.2s 时天然不冲突，此路径不会触发。
/// </summary>
public static class ThaCodeGenerator
{
	/// <summary>按模板格式化时间。</summary>
	public static string Format(DateTime time, string dateFormat)
		=> time.ToString(dateFormat, CultureInfo.InvariantCulture);

	/// <summary>
	/// 按配置生成码：配置了码段（<see cref="AppConfig.CodeSegments"/>）→ 按段拼装；
	/// 否则回落到"整码 = 纯时间码"（<see cref="AppConfig.CodeDateFormat"/>）。
	/// 批次号（Serial 段）按生产日窗口取值（默认每天 8 点清零），日期段是否跟生产日走
	/// 由 <see cref="AppConfig.DateFollowProductionDay"/> 决定。
	/// </summary>
	public static string? Generate(AppConfig cfg, int maxWaitMs = 3000)
	{
		if (!ThaCodeAssembly.IsEnabled(cfg))
			return Generate(cfg.CodeDateFormat, maxWaitMs);

		var segments = cfg.CodeSegments;
		var deadline = Environment.TickCount64 + maxWaitMs;
		while (true)
		{
			var now = DateTime.Now;
			var stamp = cfg.DateFollowProductionDay
				? ThaProductionDay.Stamp(now, cfg.SerialResetHour)
				: now;
			var code = ThaCodeAssembly.Build(segments, stamp, ThaCodeStore.NextDailySerial(cfg.SerialResetHour));
			if (!ThaCodeStore.Exists(code))
				return code;

			if (Environment.TickCount64 >= deadline)
				return null;

			// 撞码：等到下一个整秒（时间/流水都变），避免死循环
			Thread.Sleep(Math.Max(20, 1000 - now.Millisecond + 5));
		}
	}

	/// <summary>
	/// 生成一个尚未被占用的码。
	/// 同秒冲突时循环等待到下一个时间单位，最多等待 <paramref name="maxWaitMs"/> 毫秒。
	/// 返回 null 表示在等待窗口内始终取不到唯一码（上层判 <see cref="MarkResultCode.GenerateFailed"/>）。
	/// </summary>
	public static string? Generate(string dateFormat, int maxWaitMs = 3000)
	{
		var deadline = Environment.TickCount64 + maxWaitMs;
		while (true)
		{
			var now = DateTime.Now;
			var code = Format(now, dateFormat);
			if (!ThaCodeStore.Exists(code))
				return code;

			if (Environment.TickCount64 >= deadline)
				return null;

			// 推进到下一个时间单位：12 位格式等到下一个整秒，15 位格式等到下一个整毫秒
			int sleepMs = dateFormat.EndsWith("fff", StringComparison.Ordinal)
				? 1
				: Math.Max(20, 1000 - now.Millisecond + 5);
			Thread.Sleep(sleepMs);
		}
	}
}

/// <summary>码校验结果。</summary>
/// <param name="Ok">是否通过。</param>
/// <param name="Reason">不通过原因（通过时为 null）。</param>
/// <param name="CodeTime">从码里解析出的时间（解析失败时为 null）。</param>
public sealed record CodeValidation(bool Ok, string? Reason, DateTime? CodeTime);

/// <summary>
/// 读码站码校验链（按顺序短路）：
///   1 空串 → 未扫到；2 非法字符；3 长度不符；4 无法解析为合法时间（纯时间码）；5 时间窗超限。
/// 分段模式下段里没有可反解的时间时跳过第 4/5 步，不误判。
/// 用"码内时间与当前时间的差"替代固定字段比对，
/// 能识别上一班残留码 / 其它设备打的码 / 人工码，比固定字段更准。
/// </summary>
public static class ThaCodeValidator
{
	/// <summary>归一化：去首尾空白与常见终止符/不可打印字符。</summary>
	public static string Normalize(string? raw)
	{
		if (string.IsNullOrEmpty(raw)) return "";
		var sb = new System.Text.StringBuilder(raw.Length);
		foreach (var c in raw)
		{
			if (c >= 0x21 && c <= 0x7E) sb.Append(c);
		}
		return sb.ToString();
	}

	/// <summary>按当前配置校验码（配置了码段时按段的总长与时间段校验）。</summary>
	public static CodeValidation Validate(string code, AppConfig cfg)
	{
		if (!ThaCodeAssembly.IsEnabled(cfg))
			return Validate(code, cfg.CodeDateFormat, cfg.CodeLength, cfg.CodeTimeWindowMinutes);

		var expect = ThaCodeAssembly.TotalLength(cfg.CodeSegments);
		var v = Validate(code, "", expect, 0);   // 先只做空码/长度/字符集
		if (!v.Ok) return v;

		var codeTime = ThaCodeAssembly.TryParseTime(code, cfg.CodeSegments);
		if (codeTime == null)
		{
			// 段里没有可反解的时间（或格式不匹配）→ 跳过时间窗校验，不误判
			return new CodeValidation(true, null, null);
		}

		if (cfg.CodeTimeWindowMinutes > 0)
		{
			// 日期段按生产日书写时，码内日期与墙钟可能正好差 1 天 → 折算整天的差只比时刻
			var diff = ThaProductionDay.MinutesApart(DateTime.Now, codeTime.Value);
			if (Math.Abs(diff) > cfg.CodeTimeWindowMinutes)
			{
				var dir = diff >= 0 ? "前" : "后";
				var abs = Math.Abs(diff);
				var human = abs < 60 ? $"{abs:F0}分钟" : $"{abs / 60:F1}小时";
				return new CodeValidation(false, $"时间超窗({dir}{human})", codeTime);
			}
		}
		return new CodeValidation(true, null, codeTime);
	}

	/// <summary>按配置校验码。</summary>
	public static CodeValidation Validate(string code, string dateFormat, int expectLength, int timeWindowMinutes)
	{
		if (string.IsNullOrWhiteSpace(code))
			return new CodeValidation(false, "空码", null);

		if (expectLength > 0 && code.Length != expectLength)
			return new CodeValidation(false, $"长度不符({code.Length}≠{expectLength})", null);

		foreach (var c in code)
		{
			if (c < '0' || c > '9')
				return new CodeValidation(false, $"非法字符('{c}')", null);
		}

		if (string.IsNullOrEmpty(dateFormat))
			return new CodeValidation(true, null, null);   // 段模式下不按整码解析时间

		if (!DateTime.TryParseExact(code, dateFormat, CultureInfo.InvariantCulture,
				DateTimeStyles.None, out var codeTime))
			return new CodeValidation(false, $"无法解析为时间({dateFormat})", null);

		if (timeWindowMinutes > 0)
		{
			var diff = (DateTime.Now - codeTime).TotalMinutes;
			if (Math.Abs(diff) > timeWindowMinutes)
			{
				var dir = diff >= 0 ? "前" : "后";
				var abs = Math.Abs(diff);
				var human = abs < 60 ? $"{abs:F0}分钟" : $"{abs / 60:F1}小时";
				return new CodeValidation(false, $"时间超窗({dir}{human})", codeTime);
			}
		}

		return new CodeValidation(true, null, codeTime);
	}
}
