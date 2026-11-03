using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

public sealed record TicketQueueRow(TicketSummaryDto Ticket, string? DepartmentName, string? OwnerName);

/// <summary>One row of the Pending Interactions view, with the department and channel resolved to names.</summary>
public sealed record PendingInteractionRow(AgentHandoffDto Handoff, string? DepartmentName, string? ChannelName);

/// <summary>
/// The unified Tickets workspace: one page, four views — Queue, Pending
/// Interactions, My Tickets and Closed — selected by <c>?view=</c> (see
/// <see cref="TicketsView"/>). Queue, My Tickets and Closed are the same
/// ticket-queue query as ever (<c>GET api/tickets</c>), My Tickets with the
/// owner fixed to the viewer and Closed with the status fixed to Closed;
/// Pending Interactions is the same agent work list as ever
/// (<c>GET api/pending-customer-interactions</c>) with its Start/Complete/
/// Cancel actions. No ticket rule or permission moved: every view calls the
/// endpoint its former page called, and the Api still decides.
/// </summary>
public sealed class TicketsModel(
    TicketsApiClient ticketsApiClient,
    TicketNameResolver nameResolver,
    ChannelsApiClient? channelsApiClient = null,
    RequestTypesApiClient? requestTypesApiClient = null,
    PendingCustomerInteractionsApiClient? handoffsApiClient = null,
    UsersApiClient? usersApiClient = null) : PageModel
{
    // ---- which view ----
    public TicketsView View { get; private set; } = TicketsView.Queue;

    /// <summary>Whether the signed-in user may create tickets (the Api's own role set for POST api/tickets) — decides only whether the "+ New Ticket" action is shown.</summary>
    public bool CanCreateTicket { get; private set; }

    // ---- bound filter state (query string) ----
    public int? DepartmentId { get; set; }
    public byte? PriorityId { get; set; }
    public string? TicketStatus { get; set; }
    public string? VerificationStatus { get; set; }
    public Guid? OwnerEmployeeId { get; set; }
    public string? Search { get; set; }
    public string? SortBy { get; set; }
    public string? SortDir { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // ---- Dashboard drill-down filters (Dashboard Phase 1) — bound from the
    // query string and passed straight through to the same queue endpoint;
    // the Api evaluates each one with the exact predicate the dashboard
    // counted with. The SLA and Assignee filter controls on the page set
    // the same parameters (slaBreached/dueToday and ownerEmployeeId/
    // inDepartmentQueue), so a drill-down and a hand-picked filter are one
    // and the same state. ----
    public byte? ChannelId { get; set; }
    public int? RequestTypeId { get; set; }
    public bool ActiveOnly { get; set; }
    public bool InDepartmentQueue { get; set; }

    /// <summary>"Available to take": only the unassigned, human-handling tickets a Call Center agent may take ownership of (the queue endpoint's <c>availableToTake</c> filter).</summary>
    public bool AvailableToTake { get; set; }

    /// <summary>
    /// Whether the "Available to take" filter is offered: a CS Agent whose ticket list is the narrow per-ticket one (everyone else already sees,
    /// or has no claim on, the pool). The Api decides what the filter returns; for a non-agent it can only narrow.
    /// </summary>
    public bool OffersAvailableToTakeFilter => View == TicketsView.Queue && TicketVisibilityRule.IsRestrictedCsAgent(Viewer?.Roles ?? []);
    public bool SlaBreached { get; set; }
    public bool DueToday { get; set; }
    public string? BacklogAge { get; set; }
    public bool PendingApproval { get; set; }

    /// <summary>One Dashboard closure KPI (Reviewed, NotReviewed, ClosedByAi, ClosedByAgent) — passed straight to the queue endpoint's <c>closureCategory</c> filter.</summary>
    public string? ClosureCategory { get; set; }

    /// <summary>The latest-closure date range of the Dashboard closure KPIs (UTC calendar days, inclusive).</summary>
    public DateOnly? ClosedFrom { get; set; }
    public DateOnly? ClosedTo { get; set; }

    /// <summary>Only tickets with no request type — the work queue for classifying them from Ticket Details. Raises no handoff.</summary>
    public bool Unclassified { get; set; }

    /// <summary>"Created by Me": only tickets the viewer created — including ones that have moved to another department, which a CS Agent can still open read-only. Narrows the viewer's own scope; never widens it.</summary>
    public bool CreatedByMe { get; set; }

    /// <summary>Supervisor review: only tickets an agent escalated as Unable to Resolve / Needs Escalation. Offered, and accepted by the Api, only for supervisory roles.</summary>
    public bool Escalated { get; set; }
    public DateOnly? CreatedFrom { get; set; }
    public DateOnly? CreatedTo { get; set; }

    /// <summary>The value of the page's SLA filter control, derived from the underlying slaBreached/dueToday flags.</summary>
    public string SlaFilterValue => SlaBreached ? "breached" : DueToday ? "dueToday" : string.Empty;

    /// <summary>The value of the page's Assignee filter control, derived from ownerEmployeeId/inDepartmentQueue.</summary>
    public string AssigneeFilterValue => InDepartmentQueue ? "queue" : OwnerEmployeeId?.ToString() ?? string.Empty;

    /// <summary>The department whose members the Assignee filter offers: the selected department filter, or the viewer's only department. Null when no single department is in scope.</summary>
    public int? AssigneeDepartmentId { get; private set; }

    /// <summary>The Assignee filter's options — the active members of <see cref="AssigneeDepartmentId"/>; empty when there is none or the directory call failed.</summary>
    public IReadOnlyList<DepartmentUserDto> AssigneeOptions { get; private set; } = [];

    /// <summary>True when any dashboard drill-down criterion is active — the page then shows what it is filtering by, with a way to clear it.</summary>
    public bool HasDrilldown =>
        ChannelId is not null || RequestTypeId is not null || ActiveOnly || InDepartmentQueue || SlaBreached
        || DueToday || BacklogAge is not null || PendingApproval || Unclassified || Escalated || ClosureCategory is not null || ClosedFrom is not null || ClosedTo is not null || CreatedFrom is not null || CreatedTo is not null;

    /// <summary>The channel/request-type names behind the drill-down ids, resolved from the same directories the New Ticket wizard reads — never a raw id on screen.</summary>
    public string? ChannelName { get; private set; }
    public string? RequestTypeName { get; private set; }

    /// <summary>The human-readable drill-down criteria, for the "Filtered from Dashboard" line.</summary>
    public IReadOnlyList<string> DrilldownLabels
    {
        get
        {
            var labels = new List<string>();
            if (ActiveOnly) labels.Add("Open tickets");
            if (InDepartmentQueue) labels.Add("In Department Queue");
            if (SlaBreached) labels.Add("SLA breached");
            if (DueToday) labels.Add("Due today");
            if (PendingApproval) labels.Add("Pending my approval");
            if (Unclassified) labels.Add("Unclassified (no request type)");
            if (Escalated) labels.Add("Escalated (unable to resolve / needs escalation)");
            if (ClosureCategory is { } closure) labels.Add(TicketDisplay.ClosureCategoryLabel(closure));
            if (ClosedFrom is not null || ClosedTo is not null)
            {
                labels.Add($"Closed {ClosedFrom?.ToString("MMM d, yyyy") ?? "…"} – {ClosedTo?.ToString("MMM d, yyyy") ?? "…"}");
            }
            if (BacklogAge is { } age) labels.Add($"Backlog age: {TicketDisplay.BacklogAgeLabel(age)}");
            if (ChannelId is not null) labels.Add(ChannelName is null ? "Channel filter" : $"Channel: {ChannelName}");
            if (RequestTypeId is not null) labels.Add(RequestTypeName is null ? "Request type filter" : $"Request type: {RequestTypeName}");
            if (CreatedFrom is not null || CreatedTo is not null)
            {
                labels.Add($"Created {CreatedFrom?.ToString("MMM d, yyyy") ?? "…"} – {CreatedTo?.ToString("MMM d, yyyy") ?? "…"}");
            }

            return labels;
        }
    }

    public ApiOutcome Outcome { get; private set; } = ApiOutcome.Success;

    public IReadOnlyList<TicketQueueRow> Rows { get; private set; } = [];

    public int TotalCount { get; private set; }

    public int TotalPages => TicketsPaging.TotalPages(TotalCount, PageSize);

    public TicketNameResolver NameResolver => nameResolver;

    public CurrentUser? Viewer { get; private set; }

    // Small, secondary stat counts — each a real, separately-filtered TotalCount from the same queue endpoint.
    public int? OpenCount { get; private set; }
    public int? InProgressCount { get; private set; }
    public int? PendingCustomerCount { get; private set; }
    public int? ClosedCount { get; private set; }

    // ---- tab counts: each tab's own default total, from the query that tab runs ----
    /// <summary>Every ticket the viewer may see (the Queue tab's unfiltered total).</summary>
    public int? QueueCount { get; private set; }

    /// <summary>Interactions still waiting for anyone to take them — the number the Pending Interactions page always led with.</summary>
    public int? PendingCount { get; private set; }

    /// <summary>Tickets the viewer owns, in any status (what the old "My Tickets" link listed).</summary>
    public int? MyCount { get; private set; }

    public int? TabCount(TicketsView view) => view switch
    {
        TicketsView.Pending => PendingCount,
        TicketsView.My => MyCount,
        TicketsView.Closed => ClosedCount,
        _ => QueueCount,
    };

    // ---- Pending Interactions view state ----
    public bool MineOnly { get; set; }
    public bool UnassignedOnly { get; set; }
    public bool IncludeResolved { get; set; }
    public ApiOutcome PendingOutcome { get; private set; } = ApiOutcome.Success;
    public IReadOnlyList<PendingInteractionRow> PendingRows { get; private set; } = [];
    public IReadOnlyList<ChannelDto> Channels { get; private set; } = [];
    public int PendingTotalCount { get; private set; }
    public int PendingTotalPages => TicketsPaging.TotalPages(PendingTotalCount, PageSize);

    /// <summary>Set when a Start/Complete/Cancel action failed — carried across the redirect and shown above the list rather than swallowed.</summary>
    [TempData]
    public string? PendingActionError { get; set; }

    /// <summary>The route values that keep the Pending Interactions filters and page when one of its action forms posts.</summary>
    public IDictionary<string, string> PendingRouteValues
    {
        get
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { [TicketsViews.QueryKey] = TicketsView.Pending.Key() };
            if (ChannelId is { } channelId) values["channelId"] = channelId.ToString();
            if (DepartmentId is { } departmentId) values["departmentId"] = departmentId.ToString();
            if (MineOnly) values["mineOnly"] = "true";
            if (UnassignedOnly) values["unassignedOnly"] = "true";
            if (IncludeResolved) values["includeResolved"] = "true";
            if (PageNumber > 1) values["page"] = PageNumber.ToString();
            if (PageSize != 20) values["pageSize"] = PageSize.ToString();
            return values;
        }
    }

    // NOTE: the page-number parameter is bound with [FromQuery(Name = "page")] on purpose. "page" is also the reserved Razor Pages
    // route value (the page name, "/Tickets"), so a plain `int page` binds from the route first and never sees ?page=2 — and
    // Url.Page("/Tickets", new { page = 2 }) overwrites it with the page name. Both made Next/Prev a no-op. Links are therefore built
    // with TicketsPaging.Url (a plain query string), never Url.Page.
    public async Task OnGetAsync(
        int? departmentId, byte? priorityId, string? ticketStatus, string? verificationStatus,
        Guid? ownerEmployeeId, string? search, string? sortBy, string? sortDir, [FromQuery(Name = "page")] int pageNumber, int pageSize,
        CancellationToken cancellationToken,
        byte? channelId = null, int? requestTypeId = null, bool activeOnly = false, bool inDepartmentQueue = false,
        bool slaBreached = false, bool dueToday = false, string? backlogAge = null, bool pendingApproval = false,
        DateOnly? createdFrom = null, DateOnly? createdTo = null, bool unclassified = false, bool escalated = false, string? closureCategory = null, DateOnly? closedFrom = null, DateOnly? closedTo = null,
        string? view = null, string? sla = null, string? assignee = null,
        bool mineOnly = false, bool unassignedOnly = false, bool includeResolved = false, bool createdByMe = false,
        bool availableToTake = false)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        CanCreateTicket = TicketCreationPolicy.AppliesTo(Viewer);

        // The page's own filter controls express the SLA and Assignee
        // choices through the queue endpoint's existing parameters.
        switch (sla)
        {
            case "breached": slaBreached = true; break;
            case "dueToday": dueToday = true; break;
        }

        if (assignee == "queue")
        {
            inDepartmentQueue = true;
        }
        else if (Guid.TryParse(assignee, out var assigneeId))
        {
            ownerEmployeeId = assigneeId;
        }

        View = TicketsViews.Resolve(view, ownerEmployeeId, Viewer?.EmployeeId, ticketStatus);

        // Each view's fixed predicate is exactly what its former navigation
        // link carried: My Tickets = ownerEmployeeId of the viewer, Closed =
        // ticketStatus=Closed.
        if (View == TicketsView.My && Viewer is not null)
        {
            ownerEmployeeId = Viewer.EmployeeId;
        }

        if (View == TicketsView.Closed)
        {
            ticketStatus = "Closed";
        }

        DepartmentId = departmentId;
        PriorityId = priorityId;
        TicketStatus = ticketStatus;
        VerificationStatus = verificationStatus;
        OwnerEmployeeId = ownerEmployeeId;
        Search = search;
        SortBy = sortBy;
        SortDir = sortDir;
        PageNumber = pageNumber < 1 ? 1 : pageNumber;
        PageSize = pageSize is < 1 or > 100 ? 20 : pageSize;
        ChannelId = channelId;
        RequestTypeId = requestTypeId;
        ActiveOnly = activeOnly;
        InDepartmentQueue = inDepartmentQueue;
        AvailableToTake = availableToTake && View == TicketsView.Queue;
        SlaBreached = slaBreached;
        DueToday = dueToday;
        BacklogAge = backlogAge;
        PendingApproval = pendingApproval;
        Unclassified = unclassified;
        CreatedByMe = createdByMe;
        // Only a reviewer may filter by it; anyone else's value is dropped here, and the Api refuses it independently.
        Escalated = escalated && TicketActions.CanReviewEscalations(Viewer?.Roles);
        ClosureCategory = Enum.TryParse<TicketClosureCategory>(closureCategory, ignoreCase: true, out var parsedClosure) && Enum.IsDefined(parsedClosure)
            ? parsedClosure.ToString()
            : null;
        ClosedFrom = closedFrom;
        ClosedTo = closedTo;
        CreatedFrom = createdFrom;
        CreatedTo = createdTo;
        MineOnly = mineOnly;
        UnassignedOnly = unassignedOnly;
        IncludeResolved = includeResolved;

        RememberContext();

        // Own memberships drive the "my departments" filter; the directory
        // puts a name on every row's department, member or not.
        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var statsTask = LoadStatsAsync(cancellationToken);
        var tabCountsTask = LoadTabCountsAsync(cancellationToken);

        if (View == TicketsView.Pending)
        {
            await LoadPendingAsync(cancellationToken);
        }
        else
        {
            await LoadTicketListAsync(cancellationToken);
        }

        await statsTask;
        await tabCountsTask;
    }

    // ---- Pending Interactions actions: the same three Api calls the
    // former page made, then back to the very same filtered view (post →
    // redirect → get, so a refresh never repeats the action). ----

    public async Task<IActionResult> OnPostStartAsync(long handoffId, CancellationToken cancellationToken)
    {
        if (handoffsApiClient is null) return RedirectToPendingView();
        var result = await handoffsApiClient.StartAsync(handoffId, cancellationToken);
        return AfterPendingAction(result.IsSuccess ? null : "The interaction could not be started.");
    }

    public async Task<IActionResult> OnPostCompleteAsync(long handoffId, string? resolutionNote, CancellationToken cancellationToken)
    {
        if (handoffsApiClient is null) return RedirectToPendingView();
        var result = await handoffsApiClient.CompleteAsync(
            handoffId, new CompleteAgentHandoffRequestDto(resolutionNote), cancellationToken);
        return AfterPendingAction(result.IsSuccess ? null : "The interaction could not be completed.");
    }

    public async Task<IActionResult> OnPostCancelAsync(long handoffId, string? reason, CancellationToken cancellationToken)
    {
        if (handoffsApiClient is null) return RedirectToPendingView();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return AfterPendingAction("A reason is required to cancel pending customer work.");
        }

        var result = await handoffsApiClient.CancelAsync(
            handoffId, new CancelAgentHandoffRequestDto(reason), cancellationToken);
        return AfterPendingAction(result.IsSuccess ? null : "The interaction could not be cancelled.");
    }

    private IActionResult AfterPendingAction(string? error)
    {
        PendingActionError = error;
        return RedirectToPendingView();
    }

    /// <summary>Back to the Pending Interactions view with whatever filters and page the action form carried — never anywhere else.</summary>
    private IActionResult RedirectToPendingView()
    {
        var query = new List<KeyValuePair<string, StringValues>>
        {
            new(TicketsViews.QueryKey, TicketsView.Pending.Key()),
        };
        query.AddRange(Request.Query.Where(pair =>
            !string.Equals(pair.Key, TicketsViews.QueryKey, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pair.Key, "handler", StringComparison.OrdinalIgnoreCase)));

        return LocalRedirect($"/Tickets{QueryString.Create(query)}");
    }

    /// <summary>Remembers this view, its filters and its page for the Ticket Details breadcrumb and back links.</summary>
    private void RememberContext()
    {
        Response.Cookies.Append(
            TicketsContext.CookieName,
            TicketsContext.ToCookieValue(View, Request.Query),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = Request.IsHttps,
                IsEssential = true,
                Path = "/",
            });
    }

    private async Task LoadTicketListAsync(CancellationToken cancellationToken)
    {
        var drilldownNamesTask = LoadDrilldownNamesAsync(cancellationToken);
        var assigneeOptionsTask = LoadAssigneeOptionsAsync(cancellationToken);

        var request = new TicketListRequestDto(
            DepartmentId, null, PriorityId, TicketStatus, VerificationStatus, OwnerEmployeeId,
            Search, SortBy, SortDir, PageNumber, PageSize,
            ChannelId, RequestTypeId,
            ActiveOnly ? true : null, InDepartmentQueue ? true : null, SlaBreached ? true : null, DueToday ? true : null,
            BacklogAge, PendingApproval ? true : null, CreatedFrom, CreatedTo, Unclassified ? true : null, ClosureCategory, ClosedFrom, ClosedTo,
            CreatedByMe ? true : null, Escalated ? true : null, AvailableToTake ? true : null);

        var result = await ticketsApiClient.GetQueueAsync(request, cancellationToken);

        // A bookmarked/stale page past the end (e.g. after tickets closed): show the last page instead of an empty list with a dead pager.
        if (result.IsSuccess && result.Value is { } overshoot && TicketsPaging.OvershootsLastPage(PageNumber, overshoot.TotalCount, PageSize, overshoot.Items.Count))
        {
            PageNumber = TicketsPaging.TotalPages(overshoot.TotalCount, PageSize);
            result = await ticketsApiClient.GetQueueAsync(request with { Page = PageNumber }, cancellationToken);
        }

        Outcome = result.Outcome;

        if (result.IsSuccess && result.Value is not null)
        {
            TotalCount = result.Value.TotalCount;
            Rows = await BuildRowsAsync(result.Value.Items, cancellationToken);
        }

        await drilldownNamesTask;
        await assigneeOptionsTask;
    }

    /// <summary>The Pending Interactions list — the former page's load, unchanged in what it asks the Api.</summary>
    private async Task LoadPendingAsync(CancellationToken cancellationToken)
    {
        if (handoffsApiClient is null)
        {
            PendingOutcome = ApiOutcome.Unreachable;
            return;
        }

        var channelsTask = channelsApiClient?.GetChannelsAsync(activeOnly: false, cancellationToken);

        var pendingRequest = new AgentHandoffListRequestDto(
            DepartmentId, ChannelId,
            MineOnly ? Viewer?.EmployeeId : null,
            UnassignedOnly, IncludeResolved, PageNumber, PageSize);
        var result = await handoffsApiClient.ListAsync(pendingRequest, cancellationToken);

        if (result.IsSuccess && result.Value is { } overshoot && TicketsPaging.OvershootsLastPage(PageNumber, overshoot.TotalCount, PageSize, overshoot.Items.Count))
        {
            PageNumber = TicketsPaging.TotalPages(overshoot.TotalCount, PageSize);
            result = await handoffsApiClient.ListAsync(pendingRequest with { Page = PageNumber }, cancellationToken);
        }

        PendingOutcome = result.Outcome;

        if (channelsTask is not null)
        {
            var channelsResult = await channelsTask;
            Channels = channelsResult.IsSuccess ? channelsResult.Value ?? [] : [];
        }

        if (result.IsSuccess && result.Value is not null)
        {
            PendingTotalCount = result.Value.TotalCount;
            PendingRows = result.Value.Items
                .Select(h => new PendingInteractionRow(
                    h,
                    nameResolver.TryGetDepartmentName(h.DepartmentId),
                    Channels.FirstOrDefault(c => c.ChannelId == h.ChannelId)?.Name))
                .ToList();
        }
    }

    /// <summary>Best-effort names for the channel/request-type drill-down chips; a failed directory call only degrades the chip's wording.</summary>
    private async Task LoadDrilldownNamesAsync(CancellationToken cancellationToken)
    {
        if (ChannelId is { } channelId && channelsApiClient is not null)
        {
            var channels = await channelsApiClient.GetChannelsAsync(activeOnly: false, cancellationToken);
            ChannelName = channels.IsSuccess ? channels.Value?.FirstOrDefault(c => c.ChannelId == channelId)?.Name : null;
        }

        if (RequestTypeId is { } requestTypeId && requestTypesApiClient is not null)
        {
            var requestTypes = await requestTypesApiClient.GetOptionsAsync(null, cancellationToken);
            RequestTypeName = requestTypes.IsSuccess ? requestTypes.Value?.FirstOrDefault(r => r.RequestTypeId == requestTypeId)?.Name : null;
        }
    }

    /// <summary>The Assignee filter's member list — only when exactly one department is in scope, and never on My Tickets (where the assignee is fixed).</summary>
    private async Task LoadAssigneeOptionsAsync(CancellationToken cancellationToken)
    {
        if (View == TicketsView.My || usersApiClient is null)
        {
            return;
        }

        AssigneeDepartmentId = DepartmentId
            ?? (nameResolver.OwnDepartments.Count == 1 ? nameResolver.OwnDepartments.First().DepartmentId : null);
        if (AssigneeDepartmentId is not { } departmentId)
        {
            return;
        }

        var members = await usersApiClient.GetDepartmentUsersAsync(departmentId, 1, 100, cancellationToken);
        AssigneeOptions = members.IsSuccess ? members.Value?.Items.ToList() ?? [] : [];
    }

    private async Task LoadStatsAsync(CancellationToken cancellationToken)
    {
        var open = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "Open", null, null, null, null, null, 1, 1), cancellationToken);
        var inProgress = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "InProgress", null, null, null, null, null, 1, 1), cancellationToken);
        var pendingCustomer = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "PendingCustomer", null, null, null, null, null, 1, 1), cancellationToken);
        var closed = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, "Closed", null, null, null, null, null, 1, 1), cancellationToken);

        await Task.WhenAll(open, inProgress, pendingCustomer, closed);

        OpenCount = open.Result.IsSuccess ? open.Result.Value?.TotalCount : null;
        InProgressCount = inProgress.Result.IsSuccess ? inProgress.Result.Value?.TotalCount : null;
        PendingCustomerCount = pendingCustomer.Result.IsSuccess ? pendingCustomer.Result.Value?.TotalCount : null;
        ClosedCount = closed.Result.IsSuccess ? closed.Result.Value?.TotalCount : null;
    }

    /// <summary>Each tab's count is that tab's own query at page size 1 — the same endpoint and predicate the tab lists with, so a badge and its list never disagree. A failed count just shows no badge.</summary>
    private async Task LoadTabCountsAsync(CancellationToken cancellationToken)
    {
        var queue = ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 1), cancellationToken);
        var mine = Viewer is null
            ? null
            : ticketsApiClient.GetQueueAsync(new TicketListRequestDto(null, null, null, null, null, Viewer.EmployeeId, null, null, null, 1, 1), cancellationToken);
        var pending = handoffsApiClient?.ListAsync(new AgentHandoffListRequestDto(UnassignedOnly: true, Page: 1, PageSize: 1), cancellationToken);

        var queueResult = await queue;
        QueueCount = queueResult.IsSuccess ? queueResult.Value?.TotalCount : null;

        if (mine is not null)
        {
            var mineResult = await mine;
            MyCount = mineResult.IsSuccess ? mineResult.Value?.TotalCount : null;
        }

        if (pending is not null)
        {
            var pendingResult = await pending;
            PendingCount = pendingResult.IsSuccess ? pendingResult.Value?.TotalCount : null;
        }
    }

    private async Task<IReadOnlyList<TicketQueueRow>> BuildRowsAsync(IReadOnlyList<TicketSummaryDto> items, CancellationToken cancellationToken)
    {
        var rows = new List<TicketQueueRow>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var ticket = items[i];
            var departmentName = nameResolver.TryGetDepartmentName(ticket.CurrentDepartmentId);
            var ownerName = ticket.CurrentOwnerEmployeeId is Guid ownerId
                ? await nameResolver.ResolveOwnerNameAsync(ticket.CurrentDepartmentId, ownerId, cancellationToken)
                : null;
            rows.Add(new TicketQueueRow(ticket, departmentName, ownerName));
        }

        return rows;
    }
}
