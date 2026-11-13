namespace AppZC.Services;

/// <summary>
/// 生产日（批次日）时间口径。
///
/// 客户（Susan 邮件）确认的触点元件规则：**产品批次号（5 位）每天早上 8 点清零**，
/// 同一生产日内首件 = 00001、次件 = 00002……所以"生产日"是
/// <c>08:00 → 次日 08:00</c>，不是自然日 <c>00:00 → 24:00</c>。
///
/// 由此派生两条口径（都由同一配置项 <see cref="Configs.AppConfig.SerialResetHour"/> 驱动）：
/// <list type="bullet">
///   <item>批次号（Serial 段）在 08:00 归零，00:00-07:59 打码的产品仍算前一天、流水继续累加；</item>
///   <item>日期段（yy / DDD / MMdd）按"生产日"记：00:00-07:59 打码的产品日期记前一天，
///         这样同一个批次号只对应一个日期，追溯时不会出现"批次 00001 落在两个日期"。</item>
/// </list>
/// 重置时刻可配（0 = 回到自然日零点清零）；日期段是否跟生产日走由
/// <see cref="Configs.AppConfig.DateFollowProductionDay"/> 单独控制。
/// </summary>
public static class ThaProductionDay
{
	/// <summary>默认生产日切换时刻（点）：客户规则 8 点清零。</summary>
	public const int DefaultResetHour = 8;

	/// <summary>把配置里的重置时刻收敛到 0-23（非法值回落 8 点）。</summary>
	public static int Normalize(int resetHour) => resetHour is >= 0 and <= 23 ? resetHour : DefaultResetHour;

	/// <summary>生产日（日期部分）：<paramref name="now"/> 早于重置时刻时属于前一天。</summary>
	public static DateTime DateOf(DateTime now, int resetHour)
		=> now.Hour < Normalize(resetHour) ? now.Date.AddDays(-1) : now.Date;

	/// <summary>生产日时间戳：日期取生产日、时刻保持真实。用于拼码（日期段跟生产日走时）。</summary>
	public static DateTime Stamp(DateTime now, int resetHour) => DateOf(now, resetHour).Add(now.TimeOfDay);

	/// <summary>生产日窗口 [Start, End)：批次号在这个窗口内累加，跨过 End 归零重排。</summary>
	public static (DateTime Start, DateTime End) Window(DateTime now, int resetHour)
	{
		var start = DateOf(now, resetHour).AddHours(Normalize(resetHour));
		return (start, start.AddDays(1));
	}

	/// <summary>当前生产日 / 批次日标签（界面显示用）。</summary>
	public static string Label(DateTime now, int resetHour)
		=> $"{DateOf(now, resetHour):yyyy-MM-dd}（{Normalize(resetHour):00}:00 起）";

	/// <summary>
	/// 时间窗比对用：把"整天的差"折算掉（最多折 1 天）。
	/// 日期段按生产日记时，00:00-07:59 打码的码内日期 = 昨天，与墙钟正好差 1 天（再加几秒节拍），
	/// 直接相减会得到"超窗 24 小时"的误判；折算后只比时刻差。
	/// 2 天以上的差（隔天残留码、外来码）不折算，照常判超窗。
	/// 注：恰好差 1 天且时刻相近（同一钟点）的码会被当成当天码，这类残留码由
	/// ThaCodeStore 的状态机 + 在制超时（WipTimeoutHours）拦，不靠时间窗。
	/// </summary>
	public static double MinutesApart(DateTime now, DateTime stamp)
	{
		var minutes = (now - stamp).TotalMinutes;
		if (Math.Abs(minutes) > 2880) return minutes;                       // 隔 2 天以上：不折算
		if (Math.Abs(minutes) >= 720) minutes -= Math.Sign(minutes) * 1440;  // 差半天以上：按相邻生产日的写法折算
		return minutes;
	}
}
