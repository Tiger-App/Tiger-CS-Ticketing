using TigerCS.Domain.Common;

namespace TigerCS.Web.Models;

/// <summary>
/// UTC to UAE (Asia/Dubai) for display. Replaces <c>DateTime.ToLocalTime()</c>, which converts to the WEB SERVER's zone — wrong on a UTC host, and
/// different on every machine. The stored value stays UTC; only the rendered wall-clock time changes.
/// </summary>
public static class UaeTimeExtensions
{
    public static DateTime ToUae(this DateTime utc) => UaeCalendar.ToUae(utc);
}
