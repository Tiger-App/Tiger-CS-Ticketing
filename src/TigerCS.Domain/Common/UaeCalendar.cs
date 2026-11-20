namespace TigerCS.Domain.Common;

/// <summary>
/// The UAE business calendar. Every timestamp TigerCS stores is UTC and stays UTC; what a user calls "today", "this month" or "the 3rd" is a UAE
/// calendar day (Asia/Dubai, UTC+04:00 all year, no daylight saving). A UAE day [00:00, 24:00) is the UTC range [prev day 20:00, day 20:00), so
/// this type turns UAE calendar days into half-open UTC instants for SQL predicates (range comparisons on the UTC column, so indexes stay usable)
/// and turns a UTC instant into its UAE calendar date for in-memory bucketing and labels.
/// </summary>
/// <remarks>
/// The zone is always Asia/Dubai: never the host's local zone, so a UTC server, a UAE-hosted server and a developer laptop agree. The IANA id is tried
/// first, then the Windows id (<c>Arabian Standard Time</c>), then a fixed +04:00 zone — the UAE has no daylight saving, so all three are identical.
/// </remarks>
public static class UaeCalendar
{
    public const string IanaId = "Asia/Dubai";
    public const string WindowsId = "Arabian Standard Time";

    /// <summary>The UAE's fixed offset from UTC.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(4);

    /// <summary>"Asia/Dubai (UTC+4)" — the label every UAE-time display and export names its zone with.</summary>
    public const string ZoneLabel = "Asia/Dubai (UTC+4)";

    public static TimeZoneInfo Zone { get; } = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { IanaId, WindowsId })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone(IanaId, Offset, "Asia/Dubai", "Asia/Dubai");
    }

    /// <summary>A UTC instant as UAE wall-clock time (Kind Unspecified). An Unspecified input is taken to be UTC, as every stored value is.</summary>
    public static DateTime ToUae(DateTime utc) => DateTime.SpecifyKind(AsUtc(utc) + Zone.GetUtcOffset(AsUtc(utc)), DateTimeKind.Unspecified);

    /// <summary>The UAE calendar date a UTC instant falls on: 2026-11-19 20:00:00Z is 2026-11-20.</summary>
    public static DateOnly ToUaeDate(DateTime utc) => DateOnly.FromDateTime(ToUae(utc));

    /// <summary>Today's UAE calendar date at the given UTC instant.</summary>
    public static DateOnly Today(DateTime nowUtc) => ToUaeDate(nowUtc);

    /// <summary>Today's UAE calendar date on the given clock.</summary>
    public static DateOnly Today(TimeProvider timeProvider) => ToUaeDate(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>The UTC instant at which a UAE calendar day begins (00:00 UAE): 2026-11-20 is 2026-11-19 20:00:00Z.</summary>
    public static DateTime DayStartUtc(DateOnly day) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Zone),
            DateTimeKind.Utc);

    /// <summary>The UTC instant at which a UAE calendar day ends, exclusive: the start of the next UAE day.</summary>
    public static DateTime DayEndUtc(DateOnly day) => DayStartUtc(day.AddDays(1));

    /// <summary>Null-propagating <see cref="DayStartUtc(DateOnly)"/> for an optional "from" filter.</summary>
    public static DateTime? DayStartUtcOrNull(DateOnly? day) => day is { } d ? DayStartUtc(d) : null;

    /// <summary>Null-propagating <see cref="DayEndUtc(DateOnly)"/> for an optional inclusive "to" filter (exclusive UTC end).</summary>
    public static DateTime? DayEndUtcOrNull(DateOnly? day) => day is { } d ? DayEndUtc(d) : null;

    /// <summary>[from 00:00 UAE, the end of <paramref name="to"/>) as a half-open UTC range — an inclusive UAE date range as SQL wants it.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) DayRangeUtc(DateOnly from, DateOnly to) => (DayStartUtc(from), DayEndUtc(to));

    /// <summary>The UTC range of the UAE calendar day containing <paramref name="nowUtc"/>.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) TodayRangeUtc(DateTime nowUtc)
    {
        var today = Today(nowUtc);
        return (DayStartUtc(today), DayEndUtc(today));
    }

    /// <summary>The UTC range of a whole UAE calendar month: the 1st 00:00 UAE up to (excluding) the 1st of the next month.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) MonthRangeUtc(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return (DayStartUtc(first), DayStartUtc(first.AddMonths(1)));
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
