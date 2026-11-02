using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace TigerCS.Web.Models;

/// <summary>
/// The views of the unified Tickets workspace (<c>/Tickets</c>). What used to
/// be four primary-navigation items — Queue, Pending Interactions, My Tickets
/// and Closed — are now tabs of one page, each still backed by the query it
/// always was: the ticket queue endpoint for Queue/My Tickets/Closed (with the
/// owner or status fixed), and the pending-customer-interactions endpoint for
/// Pending Interactions.
/// </summary>
public enum TicketsView
{
    Queue,
    Pending,
    My,
    Closed,
}

/// <summary>Presentation facts about each <see cref="TicketsView"/>: its query-string key, its label and its tab URL.</summary>
public static class TicketsViews
{
    /// <summary>The query-string parameter that selects a view — so a bookmark or a browser refresh lands on the same tab.</summary>
    public const string QueryKey = "view";

    public static readonly IReadOnlyList<TicketsView> All = [TicketsView.Queue, TicketsView.Pending, TicketsView.My, TicketsView.Closed];

    public static string Key(this TicketsView view) => view switch
    {
        TicketsView.Pending => "pending",
        TicketsView.My => "my",
        TicketsView.Closed => "closed",
        _ => "queue",
    };

    public static string Label(this TicketsView view) => view switch
    {
        TicketsView.Pending => "Pending Interactions",
        TicketsView.My => "My Tickets",
        TicketsView.Closed => "Closed",
        _ => "Queue",
    };

    /// <summary>The tab's own URL — a fresh, unfiltered view. Queue is the default, so its URL is the bare workspace path.</summary>
    public static string Href(this TicketsView view) =>
        view == TicketsView.Queue ? "/Tickets" : $"/Tickets?{QueryKey}={view.Key()}";

    /// <summary>True for the views that list tickets through the ticket queue endpoint (everything but Pending Interactions).</summary>
    public static bool IsTicketList(this TicketsView view) => view != TicketsView.Pending;

    public static bool TryParse(string? key, out TicketsView view)
    {
        switch (key?.Trim().ToLowerInvariant())
        {
            case "queue": view = TicketsView.Queue; return true;
            case "pending": view = TicketsView.Pending; return true;
            case "my": view = TicketsView.My; return true;
            case "closed": view = TicketsView.Closed; return true;
            default: view = TicketsView.Queue; return false;
        }
    }

    /// <summary>
    /// Resolves the view for a request. An explicit <c>view=</c> wins; without
    /// one, the pre-consolidation URLs keep meaning what they meant — the old
    /// "My Tickets" link (<c>ownerEmployeeId</c> = the viewer) opens the My
    /// Tickets tab and the old "Closed" link (<c>ticketStatus=Closed</c>) the
    /// Closed tab — so bookmarks and dashboard drill-downs still land on the
    /// right tab. Everything else is the Queue.
    /// </summary>
    public static TicketsView Resolve(string? key, Guid? ownerEmployeeId, Guid? viewerEmployeeId, string? ticketStatus)
    {
        if (TryParse(key, out var explicitView))
        {
            return explicitView;
        }

        if (ownerEmployeeId is not null && viewerEmployeeId is not null && ownerEmployeeId == viewerEmployeeId)
        {
            return TicketsView.My;
        }

        if (string.Equals(ticketStatus, "Closed", StringComparison.OrdinalIgnoreCase))
        {
            return TicketsView.Closed;
        }

        return TicketsView.Queue;
    }
}

/// <summary>
/// Where the agent was in the Tickets workspace before opening a ticket —
/// which tab, which filters, which page — so Ticket Details (and the pages
/// under it) can lead back to exactly that list. Remembered in a small
/// same-site session cookie written by the Tickets page itself, so it
/// survives the details page's own post-redirect-get round trips and a
/// browser refresh, and never leaks into ticket URLs.
/// </summary>
public sealed record TicketsContext(TicketsView View, string Href)
{
    public const string CookieName = "TigerCS.Web.TicketsContext";

    /// <summary>The bare workspace when nothing (valid) is remembered.</summary>
    public static readonly TicketsContext Default = new(TicketsView.Queue, "/Tickets");

    /// <summary>The query-string keys the workspace itself understands — anything else in the cookie is dropped rather than echoed into a link.</summary>
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        TicketsViews.QueryKey,
        "departmentId", "priorityId", "ticketStatus", "verificationStatus", "ownerEmployeeId", "search",
        "sortBy", "sortDir", "page", "pageSize", "channelId", "requestTypeId", "activeOnly", "inDepartmentQueue",
        "slaBreached", "dueToday", "backlogAge", "pendingApproval", "unclassified", "escalated", "closureCategory", "closedFrom", "closedTo", "createdFrom", "createdTo", "sla",
        "mineOnly", "unassignedOnly", "includeResolved", "createdByMe", "availableToTake",
    };

    /// <summary>The cookie value for the current workspace request: its own query string, with the resolved view made explicit.</summary>
    public static string ToCookieValue(TicketsView view, IEnumerable<KeyValuePair<string, StringValues>> query)
    {
        var kept = new List<KeyValuePair<string, StringValues>>
        {
            new(TicketsViews.QueryKey, view.Key()),
        };
        kept.AddRange(query.Where(pair =>
            !string.Equals(pair.Key, TicketsViews.QueryKey, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pair.Key, "handler", StringComparison.OrdinalIgnoreCase)
            && AllowedKeys.Contains(pair.Key)));

        return QueryString.Create(kept).Value?.TrimStart('?') ?? string.Empty;
    }

    /// <summary>Parses a remembered cookie value; anything unparseable or unknown falls back to <see cref="Default"/>.</summary>
    public static TicketsContext FromCookieValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        Dictionary<string, StringValues> parsed;
        try
        {
            parsed = QueryHelpers.ParseQuery(value);
        }
        catch (Exception)
        {
            return Default;
        }

        var view = parsed.TryGetValue(TicketsViews.QueryKey, out var viewValue) && TicketsViews.TryParse(viewValue.ToString(), out var parsedView)
            ? parsedView
            : TicketsView.Queue;

        var kept = parsed
            .Where(pair => AllowedKeys.Contains(pair.Key))
            .Where(pair => pair.Value.Count > 0 && !string.IsNullOrEmpty(pair.Value[0]))
            .Select(pair => new KeyValuePair<string, StringValues>(pair.Key, pair.Value[0]))
            .ToList();

        var query = QueryString.Create(kept);
        return new TicketsContext(view, query.HasValue ? $"/Tickets{query.Value}" : "/Tickets");
    }
}
