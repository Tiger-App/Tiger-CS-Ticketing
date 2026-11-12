using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Models;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>One KPI card on the Dashboard. <see cref="Href"/> is the drill-down into the existing ticket queue; emphasis keys map to the fixed palette (critical for breach, gold for attention).</summary>
public sealed record KpiCard(string Label, int Value, string? Emphasis = null, string? Href = null, string? Hint = null, string? Detail = null);

/// <summary>One bar row of a breakdown card: label left, bar centre, count and percentage right. <see cref="Href"/> is null for an informational row (a value not recorded on the ticket), which is not a drill-down.</summary>
public sealed record DashboardBarRow(string Label, int Count, double Percentage, string? Href, string? Key = null, string? Code = null)
{
    /// <summary>The percentage as displayed — one decimal at most, invariant, never NaN.</summary>
    public string PercentageText => Percentage.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The bar's fill width in whole percent, clamped to the track
    /// (percentages already sum to ≤ 100) and rounded to the nearest
    /// point — the exact figure stays in <see cref="PercentageText"/>
    /// beside the bar. A non-finite percentage draws an empty bar rather
    /// than an invalid width.
    /// </summary>
    public int BarWidthPercent =>
        double.IsFinite(Percentage) ? (int)Math.Round(Math.Clamp(Percentage, 0, 100), MidpointRounding.AwayFromZero) : 0;

    /// <summary>
    /// The stylesheet class that draws the fill — <c>bar-fill--w0</c> …
    /// <c>bar-fill--w100</c>, defined in site.css.
    ///
    /// <para>
    /// A class, never an inline <c>style</c> attribute: the app serves a
    /// strict Content-Security-Policy whose <c>style-src 'self'</c> has no
    /// <c>'unsafe-inline'</c>, so a style attribute would be dropped by the
    /// browser and every bar would render at zero width. Keeping the width
    /// in the self-hosted stylesheet also means the bars are correct with
    /// scripts disabled, with no width applied after paint.
    /// </para>
    /// </summary>
    public string BarWidthClass => "bar-fill--w" + BarWidthPercent.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One breakdown card. <see cref="TotalHref"/> links to the queue view of everything the card counted; <see cref="TopRows"/>/<see cref="MoreRows"/> keep long lists compact behind a "View all".</summary>
public sealed record DashboardBarCard(string Title, string Subtitle, IReadOnlyList<DashboardBarRow> Rows, int Total, string TotalHref, string EmptyText)
{
    public const int TopRowCount = 8;

    public IReadOnlyList<DashboardBarRow> TopRows => Rows.Take(TopRowCount).ToList();

    public IReadOnlyList<DashboardBarRow> MoreRows => Rows.Skip(TopRowCount).ToList();
}

/// <summary>
/// The Operational Dashboard (Dashboard Phase 1): the customer search
/// quick action, a filter bar over the existing master data, six KPI
/// cards, six operational breakdown cards and the Recent/Critical list.
/// Every number comes from <c>GET /api/dashboard/overview</c>, which scopes
/// it server-side to the caller's own visible departments and echoes the
/// filters it actually applied — this page never widens, re-filters or
/// computes data of its own; it only renders and links. Each KPI and each
/// bar row links into the existing ticket queue with the matching filters
/// (<see cref="DashboardLinks"/>), never to a page of its own.
/// </summary>
public sealed class DashboardModel(DashboardApiClient dashboardApiClient) : PageModel
{
    // ---- bound filter state (query string) ----
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    public int? DepartmentId { get; private set; }
    public Guid? OwnerEmployeeId { get; private set; }
    public byte? ChannelId { get; private set; }
    public int? RequestTypeId { get; private set; }
    public string? TicketStatus { get; private set; }
    public byte? PriorityId { get; private set; }

    public CurrentUser? Viewer { get; private set; }
    public DashboardOverviewDto? Overview { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public IReadOnlyList<KpiCard> Cards { get; private set; } = [];

    /// <summary>The four closure cards (Reviewed, Not Reviewed, Closed by AI, Closed by Agent), shown as a second row beneath <see cref="Cards"/>.</summary>
    public IReadOnlyList<KpiCard> ClosureCards { get; private set; } = [];
    public IReadOnlyList<DashboardBarCard> BreakdownCards { get; private set; } = [];

    /// <summary>True when the user narrowed anything beyond the defaults — the filter bar then offers "Clear".</summary>
    public bool HasFilters =>
        DateFrom is not null || DateTo is not null || DepartmentId is not null || OwnerEmployeeId is not null
        || ChannelId is not null || RequestTypeId is not null || !string.IsNullOrWhiteSpace(TicketStatus) || PriorityId is not null;

    /// <summary>The effective volume period, as the Api applied it ("Aug 12 – Sep 10, 2026").</summary>
    public string PeriodLabel => Overview is null
        ? string.Empty
        : $"{Overview.Filters.DateFrom:MMM d} – {Overview.Filters.DateTo:MMM d, yyyy}";

    /// <summary>Department name lookup for the request-type picker's groups — from the same picker options the Api returned.</summary>
    public string DepartmentLabel(int? departmentId) =>
        departmentId is { } id && Overview?.FilterOptions.Departments.FirstOrDefault(d => d.Id == id.ToString(CultureInfo.InvariantCulture)) is { } d
            ? d.Label
            : "Other departments";

    public async Task OnGetAsync(
        DateOnly? dateFrom, DateOnly? dateTo, int? departmentId, Guid? ownerEmployeeId,
        byte? channelId, int? requestTypeId, string? ticketStatus, byte? priorityId,
        CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        DateFrom = dateFrom;
        DateTo = dateTo;
        DepartmentId = departmentId;
        OwnerEmployeeId = ownerEmployeeId;
        ChannelId = channelId;
        RequestTypeId = requestTypeId;
        TicketStatus = string.IsNullOrWhiteSpace(ticketStatus) ? null : ticketStatus;
        PriorityId = priorityId;

        var result = await dashboardApiClient.GetOverviewAsync(
            new DashboardOverviewRequestDto(DateFrom, DateTo, DepartmentId, OwnerEmployeeId, ChannelId, RequestTypeId, TicketStatus, PriorityId),
            cancellationToken);
        Outcome = result.Outcome;
        if (!result.IsSuccess || result.Value is null)
        {
            return;
        }

        Overview = result.Value;
        Cards = BuildCards(Overview, Viewer);
        ClosureCards = BuildClosureCards(Overview);
        BreakdownCards = BuildBreakdownCards(Overview);
    }

    /// <summary>The six KPI cards, each a drill-down into the queue. The numbers are already scoped by the Api; card visibility is not an authorization boundary.</summary>
    public static IReadOnlyList<KpiCard> BuildCards(DashboardOverviewDto o, CurrentUser? viewer)
    {
        var f = o.Filters;
        var k = o.Kpis;
        return
        [
            new KpiCard("Open Tickets", k.OpenTickets, null, DashboardLinks.OpenTickets(f), "Active tickets in scope"),
            new KpiCard("My Tickets", k.MyTickets, null,
                viewer is null ? DashboardLinks.OpenTickets(f) : DashboardLinks.MyTickets(f, viewer.EmployeeId), "Tickets currently assigned to you"),
            // Tickets that have a responsible department but no assigned
            // owner sit in that department's queue — never "ownerless".
            new KpiCard("In Department Queue", k.InDepartmentQueue, k.InDepartmentQueue > 0 ? "attention" : null,
                DashboardLinks.InDepartmentQueue(f), "Active, awaiting an assignee"),
            new KpiCard("SLA Breached", k.SlaBreached, k.SlaBreached > 0 ? "critical" : null, DashboardLinks.SlaBreached(f), "Active, SLA state Breached"),
            new KpiCard("Due Today", k.DueToday, k.DueToday > 0 ? "attention" : null, DashboardLinks.DueToday(f), "Resolution due today (UTC day)"),
            new KpiCard("Pending Approval", k.PendingApproval, k.PendingApproval > 0 ? "attention" : null,
                DashboardLinks.PendingApproval(f), "Approvals you can action")
        ];
    }

    /// <summary>
    /// The four closure KPI cards. Unlike the six above they are not
    /// current-state: they always count Closed tickets, by latest closure date within the
    /// dashboard's date range, honouring every other dashboard filter except status, and each links to
    /// the Tickets list filtered to the same category.
    /// </summary>
    public static IReadOnlyList<KpiCard> BuildClosureCards(DashboardOverviewDto o)
    {
        var f = o.Filters;
        var k = o.Kpis;
        // In test mode the review cards evaluate Test-mode reviews (UAT); say so.
        var kind = k.ReviewsAreTestMode ? " (test-mode reviews)" : "";
        return
        [
            new KpiCard("Reviewed Tickets", k.ReviewedTickets, null, DashboardLinks.Reviewed(f), "Closed tickets with customer feedback submitted" + kind,
                // Same tickets, period and scope as the count; the scale's maximum is the review's own.
                "· Average Rating " + (k.AverageReviewRating is { } average
                    ? average.ToString("0.0", CultureInfo.InvariantCulture) + "/" + TigerCS.Domain.Modules.CustomerReviews.TicketReview.MaxRating.ToString(CultureInfo.InvariantCulture)
                    : "—")),
            new KpiCard("Not Reviewed", k.NotReviewedTickets, null, DashboardLinks.NotReviewed(f), "Closed tickets without customer feedback" + kind),
            new KpiCard("Closed by AI", k.ClosedByAiTickets, null, DashboardLinks.ClosedByAi(f), "Closed tickets whose closure was recorded as AI"),
            new KpiCard("Closed by Agent", k.ClosedByAgentTickets, null, DashboardLinks.ClosedByAgent(f), "Closed tickets closed by a person")
        ];
    }

    /// <summary>The six breakdown cards in the approved layout order: Channel, Request Type, Status / Department, Backlog Ageing, Priority.</summary>
    public static IReadOnlyList<DashboardBarCard> BuildBreakdownCards(DashboardOverviewDto o)
    {
        var f = o.Filters;
        var volumeHref = DashboardLinks.VolumeTotal(f);
        var period = $"Tickets created {f.DateFrom:MMM d} – {f.DateTo:MMM d, yyyy}";

        return
        [
            new DashboardBarCard("Volume by Channel", period,
                Rows(o.VolumeByChannel, key => DashboardLinks.Channel(f, key), label => label),
                o.VolumeTotal, volumeHref, "No tickets in this period."),
            new DashboardBarCard("Volume by Request Type", period,
                Rows(o.VolumeByRequestType, key => DashboardLinks.RequestType(f, key), label => label),
                o.VolumeTotal, volumeHref, "No tickets in this period."),
            new DashboardBarCard("Status", period,
                Rows(o.StatusBreakdown, key => DashboardLinks.Status(f, key), TicketDisplay.TicketStatusLabel),
                o.VolumeTotal, volumeHref, "No tickets in this period."),
            new DashboardBarCard("Volume by Department", period,
                Rows(o.VolumeByDepartment, key => DashboardLinks.Department(f, key), label => label),
                o.VolumeTotal, volumeHref, "No tickets in this period."),
            new DashboardBarCard("Open Backlog Ageing", "Current open backlog by age — not limited by the date range",
                Rows(o.BacklogAgeing, key => DashboardLinks.BacklogAge(f, key), TicketDisplay.BacklogAgeLabel),
                o.Kpis.OpenTickets, DashboardLinks.OpenTickets(f), "No open backlog in scope."),
            new DashboardBarCard("Priority", period,
                Rows(o.PriorityBreakdown, key => DashboardLinks.Priority(f, key), label => label),
                o.VolumeTotal, volumeHref, "No tickets in this period.")
        ];
    }

    private static IReadOnlyList<DashboardBarRow> Rows(
        IReadOnlyList<DashboardBreakdownItemDto> items, Func<string, string> hrefForKey, Func<string, string> labelFor) =>
        items.Select(i => new DashboardBarRow(
            // Api labels are master-data names already; status and age
            // bucket labels are enum names the display helpers humanize.
            i.Key is null ? i.Label : labelFor(i.Label),
            i.Count,
            i.Percentage,
            i.Key is null ? null : hrefForKey(i.Key),
            i.Key,
            i.Code)).ToList();
}

/// <summary>
/// Decoration for channels on the Dashboard — a glyph keyed on the channel's stable <c>Code</c> and a colour keyed on its id, never on its
/// display name or position, so a channel keeps the same icon and colour in the volume card, its legend, the stacked bar and the ticket
/// table whatever the filters or the date range. A channel the map does not know (an administrator-added one) gets the generic glyph and
/// the next palette colour for its id; nothing here decides what is shown, only how.
/// </summary>
public static class DashboardChannelStyle
{
    /// <summary>The palette has this many colours (<c>.ch-c1</c> … in site.css); ids beyond it wrap.</summary>
    public const int PaletteSize = 12;

    private const string S = "stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\"";

    /// <summary>The colour class for a channel id (<c>ch-c1</c> … <c>ch-c12</c>); <c>ch-other</c> for the unmapped / not-recorded rows, which have no id.</summary>
    public static string ColorClass(string? key) =>
        int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? "ch-c" + (((id - 1) % PaletteSize) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "ch-other";

    /// <summary>Inner SVG markup (24x24 viewBox, currentColor strokes) for a channel code.</summary>
    public static string Icon(string? code) => (code ?? string.Empty).ToUpperInvariant() switch
    {
        "PHONE" => $"<path d=\"M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2Z\" {S}/>",
        "WHATSAPP" => $"<path d=\"M12 3a9 9 0 0 0-7.7 13.6L3 21l4.5-1.2A9 9 0 1 0 12 3Z\" {S}/><path d=\"M9 9c0 3.3 2.7 6 6 6l1.2-1.6-2-1-.9.8a3.6 3.6 0 0 1-1.6-1.6l.8-.9-1-2L9 9Z\" {S}/>",
        "LIVE_CHAT" => $"<path d=\"M4 5h16v11H9l-5 4V5Z\" {S}/><path d=\"M8.5 10.5h.01M12 10.5h.01M15.5 10.5h.01\" {S}/>",
        "WEBSITE" => $"<circle cx=\"12\" cy=\"12\" r=\"9\" {S}/><path d=\"M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18\" {S}/>",
        "MOBILE_APP" => $"<rect x=\"7\" y=\"2.5\" width=\"10\" height=\"19\" rx=\"2.2\" {S}/><path d=\"M11 18.5h2\" {S}/>",
        "INSTAGRAM" => $"<rect x=\"4\" y=\"4\" width=\"16\" height=\"16\" rx=\"5\" {S}/><circle cx=\"12\" cy=\"12\" r=\"3.6\" {S}/><path d=\"M16.8 7.2h.01\" {S}/>",
        "FACEBOOK" => $"<path d=\"M14 8h2V5h-2.5A3.5 3.5 0 0 0 10 8.5V11H8v3h2v6h3v-6h2.2l.6-3H13V8.5c0-.3.3-.5 1-.5Z\" {S}/>",
        "SOCIAL_DM" => $"<path d=\"M21 3 3 10l7 3 3 7 8-17Z\" {S}/><path d=\"M10 13l11-10\" {S}/>",
        "WALK_IN_KIOSK" => $"<path d=\"M3 21V9l9-6 9 6v12\" {S}/><path d=\"M9 21v-6h6v6\" {S}/>",
        _ => $"<path d=\"M5 12h.01M12 12h.01M19 12h.01\" {S}/>",
    };
}
