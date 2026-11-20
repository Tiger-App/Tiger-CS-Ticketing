using System.Globalization;

namespace TigerCS.Web.Models;

/// <summary>
/// <b>The one place a stored UTC instant becomes a displayed time.</b> TigerCS stores every timestamp in UTC; the interface shows every timestamp in Dubai time
/// (Asia/Dubai, UTC+4 all year - no daylight saving), whatever time zone the web server or the viewer's machine is set to. Nothing in the Web project may call
/// <c>DateTime.ToLocalTime()</c> / <c>DateTime.Now</c> for display: that depends on the host (a UTC host showed UTC, a Dubai host showed Dubai, and adding four hours
/// on top of either was a second conversion). A test (<c>DubaiTimeDisplayTests</c>) fails the build if one comes back.
///
/// <para>
/// The conversion is applied ONCE, here, to a UTC value. A value that is already Dubai time must never be passed in again. Pure, no I/O, culture-invariant.
/// </para>
/// </summary>
public static class DubaiTime
{
    /// <summary>Dubai's offset from UTC. Constant: the UAE has no daylight saving time.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(4);

    /// <summary>The label to show beside a Dubai time.</summary>
    public const string ZoneLabel = "Asia/Dubai (UTC+4)";

    /// <summary>
    /// The Dubai wall-clock time of a UTC instant (kind <see cref="DateTimeKind.Unspecified"/>: it is a wall-clock reading, not an instant). A <c>Local</c> value is
    /// first converted to its real instant; an <c>Unspecified</c> one is read as UTC, which is how every stored timestamp is meant (EF Core returns UTC columns as
    /// <c>Unspecified</c>).
    /// </summary>
    public static DateTime FromUtc(DateTime utc)
    {
        var instant = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateTime.SpecifyKind(instant + Offset, DateTimeKind.Unspecified);
    }

    /// <summary>Formats a UTC instant as Dubai time with a .NET format string (invariant culture), e.g. <c>"d MMM yyyy HH:mm"</c>.</summary>
    public static string Format(DateTime utc, string format) => FromUtc(utc).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Same for an optional instant; null stays null (the view prints nothing).</summary>
    public static string? Format(DateTime? utc, string format) => utc is { } value ? Format(value, format) : null;

    /// <summary>Today's calendar date in Dubai, for "is this today" tests - never the server's date.</summary>
    public static DateTime TodayFrom(DateTime nowUtc) => FromUtc(nowUtc).Date;

    /// <summary>True when the UTC instant falls on the same Dubai calendar day as <paramref name="nowUtc"/>.</summary>
    public static bool IsSameDubaiDay(DateTime utc, DateTime nowUtc) => FromUtc(utc).Date == TodayFrom(nowUtc);
}
