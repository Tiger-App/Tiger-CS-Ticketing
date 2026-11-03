using Microsoft.AspNetCore.WebUtilities;

namespace TigerCS.Web.Models;

/// <summary>
/// Pager arithmetic and link building for the Tickets workspace. Links are built as a plain query string on purpose:
/// <c>page</c> is the reserved Razor Pages route value, so <c>Url.Page("/Tickets", new { page = 2 })</c> replaces the page number
/// with the page name and the "Next" link ends up pointing at page 1 again.
/// </summary>
public static class TicketsPaging
{
    public static int TotalPages(int totalCount, int pageSize) =>
        totalCount <= 0 || pageSize <= 0 ? 1 : (int)Math.Ceiling(totalCount / (double)pageSize);

    /// <summary>True when the requested page lies past the last page of a non-empty result (the Api answers it with no rows).</summary>
    public static bool OvershootsLastPage(int page, int totalCount, int pageSize, int itemCount) =>
        itemCount == 0 && totalCount > 0 && page > TotalPages(totalCount, pageSize);

    /// <summary>/Tickets plus every non-empty value, URL-encoded; null/empty values are dropped so links stay short.</summary>
    public static string Url(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var query = values
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return QueryHelpers.AddQueryString("/Tickets", query);
    }
}
