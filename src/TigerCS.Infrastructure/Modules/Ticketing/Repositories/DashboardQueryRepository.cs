using TigerCS.Application.Modules.Ticketing.Services;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// The Operational Dashboard read model (Dashboard Phase 1). Every number
/// is a SQL-side aggregate over the query's scope — grouped and counted in
/// the database, read with <c>AsNoTracking</c>, display names resolved by
/// set-based joins/lookups (one query per dimension, never one per row).
/// The predicates are the shared <see cref="TicketQueryFilters"/>, the
/// same ones the ticket queue's drill-down filters use.
/// </summary>
public sealed class DashboardQueryRepository(TigerCsDbContext dbContext, ReopenPolicy? reopenPolicy = null, TimeProvider? clock = null) : IDashboardQueryRepository
{
    private const string NotRecordedChannelLabel = "No channel recorded";

    /// <summary>Tickets whose originating channel id matches no Channels row — kept, never dropped, under one honest bucket.</summary>
    private const string OtherChannelLabel = "Other";

    private const string OtherChannelCode = "OTHER";

    /// <summary>How the virtual agent is named in "Last action by" — it is a participant in the conversation, not a person.</summary>
    public const string AiActorLabel = "AI Assistant";
    private const string NoRequestTypeLabel = "No request type";
    private const string UnclassifiedPriorityLabel = "Not set";

    public async Task<DashboardOverviewSnapshot> GetOverviewAsync(
        DashboardOverviewQuery query, CancellationToken cancellationToken = default)
    {
        // The dimension filters apply to everything; the date range only to
        // the volume breakdowns; "active" only to the backlog side.
        var scoped = dbContext.Tickets.AsNoTracking().VisibleTo(dbContext, query.ScopeDepartmentIds, query.AgentScopeEmployeeId, createdOnly: query.CreatedOnlyScope, ownerEmployeeId: query.OwnerVisibilityEmployeeId,
            inactivityReopenCutoffUtc: TicketQueryFilters.InactivityReopenCutoff(reopenPolicy, clock), includeGenesysIntake: query.IncludeGenesysIntake);

        var closureBase = scoped;
        if (query.DepartmentId is { } departmentFilterId)
        {
            scoped = scoped.Where(t => t.CurrentDepartmentId == departmentFilterId);
            closureBase = closureBase.Where(t => t.CurrentDepartmentId == departmentFilterId);
        }

        // The closure KPIs take the same filters except status and owner: closure clears the current owner, so they judge "assigned to" by the
        // closed ticket's last assignee (AssignedToIncludingClosed), and an employee-scoped caller is narrowed to their own tickets.
        if (query.ClosedAssigneeEmployeeId is { } closedAssignee)
        {
            closureBase = closureBase.AssignedToIncludingClosed(dbContext, closedAssignee);
        }

        if (query.OwnerEmployeeId is { } ownerEmployeeId)
        {
            closureBase = closureBase.AssignedToIncludingClosed(dbContext, ownerEmployeeId);
            scoped = scoped.Where(t => t.CurrentOwnerEmployeeId == ownerEmployeeId);
        }

        if (query.ChannelId is { } channelId)
        {
            scoped = scoped.WithOriginatingChannel(dbContext, channelId);
            closureBase = closureBase.WithOriginatingChannel(dbContext, channelId);
        }

        if (query.RequestTypeId is { } requestTypeId)
        {
            scoped = scoped.Where(t => t.RequestTypeId == requestTypeId);
            closureBase = closureBase.Where(t => t.RequestTypeId == requestTypeId);
        }

        if (query.PriorityId is { } priorityId)
        {
            scoped = scoped.Where(t => t.PriorityId == priorityId);
            closureBase = closureBase.Where(t => t.PriorityId == priorityId);
        }

        // The four closure KPIs always represent Closed tickets, so they take
        // every filter except the status one (closureBase, built above).

        if (query.TicketStatus is { } ticketStatus)
        {
            scoped = scoped.Where(t => t.TicketStatus == ticketStatus);
        }

        var backlog = scoped.Active();
        var volume = scoped.Where(t => t.CreatedAtUtc >= query.RangeStartUtc && t.CreatedAtUtc < query.RangeEndUtc);

        // ---- KPIs (current state) ----
        var openTickets = await backlog.CountAsync(cancellationToken);
        // Every ticket currently assigned to the caller, in any status (Resolved awaiting Close included) — exactly the My Tickets list.
        var myTickets = await scoped.CountAsync(t => t.CurrentOwnerEmployeeId == query.CallerEmployeeId, cancellationToken);
        var inDepartmentQueue = await backlog.CountAsync(t => t.CurrentOwnerEmployeeId == null, cancellationToken);
        var slaBreached = await backlog.CountAsync(t => t.SlaState == SlaState.Breached, cancellationToken);
        var dueToday = await backlog.DueOnUtcDay(dbContext, query.TodayStartUtc).CountAsync(cancellationToken);
        var pendingApproval = await scoped.WithPendingApprovalActionableBy(dbContext, query.ApproverScope).CountAsync(cancellationToken);

        // ---- Closure KPIs (date range applies to the latest closure date) ----
        // Each is a COUNT over the shared InClosureCategory predicate — the
        // one the ticket queue's closureCategory drill-down uses — so a card
        // and its list always agree. Four narrow COUNTs, not a row load.
        var closureScope = closureBase.LatestClosureBetween(dbContext, query.RangeStartUtc, query.RangeEndUtc);
        var testReviews = query.ReviewsAreTest;
        // The average is over the very same Reviewed set as the count: each ticket contributes its one review for the current cycle
        // (unique per ticket/cycle/kind), so a ticket is never weighted twice.
        var reviewedTickets = closureScope.InClosureCategory(dbContext, TicketClosureCategory.Reviewed, testReviews);
        var averageRating = await reviewedTickets
            .Select(t => dbContext.TicketReviews
                .Where(r => r.TicketId == t.TicketId && r.ReopenCycle == t.ReopenCount && r.IsTest == testReviews)
                .Select(r => (double?)r.Rating).FirstOrDefault())
            .AverageAsync(cancellationToken);
        var closure = new DashboardClosureCounts(
            await reviewedTickets.CountAsync(cancellationToken),
            await closureScope.InClosureCategory(dbContext, TicketClosureCategory.NotReviewed, testReviews).CountAsync(cancellationToken),
            await closureScope.InClosureCategory(dbContext, TicketClosureCategory.ClosedByAi, testReviews).CountAsync(cancellationToken),
            await closureScope.InClosureCategory(dbContext, TicketClosureCategory.ClosedByAgent, testReviews).CountAsync(cancellationToken),
            averageRating);

        // ---- Volume breakdowns (date range) ----
        var statusCounts = await volume
            .GroupBy(t => t.TicketStatus)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var volumeTotal = statusCounts.Sum(s => s.Count);

        var channelCounts = await volume
            .GroupJoin(
                dbContext.TicketInteractions.Where(i => i.IsOriginatingInteraction),
                t => t.TicketId, i => i.TicketId, (t, interactions) => new { t, interactions })
            .SelectMany(x => x.interactions.DefaultIfEmpty(), (x, i) => new { ChannelId = i != null ? (byte?)i.ChannelId : null })
            .GroupBy(x => x.ChannelId)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // The full channel catalogue, read once: it names every volume row, drives the zero rows
        // and resolves the channel shown beside each Recent ticket.
        var channelCatalogue = await dbContext.Channels.AsNoTracking()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new ChannelInfo(c.ChannelId, c.Name, c.Code, c.IsActive))
            .ToListAsync(cancellationToken);
        var channelRows = BuildChannelVolumeRows(channelCatalogue, channelCounts.Select(c => (c.Key, c.Count)).ToList());

        var requestTypeCounts = await volume
            .GroupBy(t => t.RequestTypeId)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var requestTypeIds = requestTypeCounts.Where(c => c.Key is not null).Select(c => c.Key!.Value).ToList();
        var requestTypeNames = requestTypeIds.Count == 0
            ? new Dictionary<int, string>()
            : await RequestTypeDisplayNamesAsync(requestTypeIds, cancellationToken);

        // Operational responsibility is the CURRENT department, never the
        // originating one.
        var departmentCounts = await volume
            .GroupBy(t => t.CurrentDepartmentId)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var departmentIds = departmentCounts.Select(c => c.Key).ToList();
        var departmentNames = departmentIds.Count == 0
            ? new Dictionary<int, string>()
            : await dbContext.Departments.AsNoTracking()
                .Where(d => departmentIds.Contains(d.DepartmentId))
                .ToDictionaryAsync(d => d.DepartmentId, d => d.Name, cancellationToken);

        var priorityCounts = await volume
            .GroupBy(t => t.PriorityId)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var priorityNames = await dbContext.Priorities.AsNoTracking()
            .OrderBy(p => p.DisplayOrder)
            .ToDictionaryAsync(p => p.PriorityId, p => p.Name, cancellationToken);

        // ---- Open Backlog Ageing (current state, explicit half-open buckets) ----
        // age < 24h ⇔ CreatedAt > now-24h; 24h ≤ age < 72h ⇔ now-72h < CreatedAt ≤ now-24h; …
        var underOneDay = query.NowUtc - BacklogAgeBoundaries.OneDay;
        var underThreeDays = query.NowUtc - BacklogAgeBoundaries.ThreeDays;
        var underSevenDays = query.NowUtc - BacklogAgeBoundaries.SevenDays;
        var ageingCounts = await backlog
            .GroupBy(t =>
                t.CreatedAtUtc > underOneDay ? (int)BacklogAgeBucket.Under24Hours
                : t.CreatedAtUtc > underThreeDays ? (int)BacklogAgeBucket.OneToThreeDays
                : t.CreatedAtUtc > underSevenDays ? (int)BacklogAgeBucket.ThreeToSevenDays
                : (int)BacklogAgeBucket.OverSevenDays)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var ageingByBucket = ageingCounts.ToDictionary(a => (BacklogAgeBucket)a.Key, a => a.Count);

        // ---- Tickets Requiring Attention (current state): the newest active
        // tickets in scope, CreatedAt DESC applied before the limit — names
        // joined in-query.
        var atRiskUntil = query.NowUtc + query.AtRiskWindow;
        var recentRows = await backlog
            .GroupJoin(TicketQueryFilters.CurrentSlaPeriods(dbContext), t => t.TicketId, s => s.TicketId, (t, slas) => new { t, slas })
            .SelectMany(x => x.slas.DefaultIfEmpty(), (x, s) => new
            {
                Ticket = x.t,
                SlaDueAtUtc = s != null && !s.ResolutionBreached ? s.ResolutionDueAtUtc : (DateTime?)null
            })
            .GroupJoin(dbContext.Departments, x => x.Ticket.CurrentDepartmentId, d => d.DepartmentId, (x, departments) => new { x, departments })
            .SelectMany(y => y.departments.DefaultIfEmpty(), (y, d) => new { y.x.Ticket, y.x.SlaDueAtUtc, DepartmentName = d != null ? d.Name : null, DepartmentCode = d != null ? d.Code : null })
            .GroupJoin(dbContext.RequestTypes, x => x.Ticket.RequestTypeId, r => (int?)r.RequestTypeId, (x, requestTypes) => new { x, requestTypes })
            .SelectMany(y => y.requestTypes.DefaultIfEmpty(), (y, r) => new { y.x.Ticket, y.x.SlaDueAtUtc, y.x.DepartmentName, y.x.DepartmentCode, RequestTypeName = r != null ? r.Name : null })
            .GroupJoin(dbContext.Employees, x => x.Ticket.CurrentOwnerEmployeeId, e => (Guid?)e.EmployeeId, (x, employees) => new { x, employees })
            .SelectMany(y => y.employees.DefaultIfEmpty(), (y, e) => new
            {
                y.x.Ticket,
                y.x.SlaDueAtUtc,
                y.x.DepartmentName,
                y.x.DepartmentCode,
                y.x.RequestTypeName,
                OwnerName = e != null ? e.DisplayName : null
            })
            .OrderByDescending(x => x.Ticket.CreatedAtUtc)
            .ThenByDescending(x => x.Ticket.TicketId)
            .Take(query.RecentLimit)
            .Select(x => new DashboardRecentTicketRow(
                x.Ticket.TicketId,
                x.Ticket.TicketNumber,
                x.Ticket.CrmBuyerCustomerName,
                x.Ticket.CrmBuyerUnitNumber ?? x.Ticket.ManualUnitNumber,
                x.Ticket.CrmBuyerProjectName ?? x.Ticket.ManualProjectName,
                x.Ticket.CurrentDepartmentId,
                x.DepartmentName,
                x.Ticket.RequestTypeId,
                x.RequestTypeName,
                x.Ticket.PriorityId,
                x.Ticket.TicketStatus,
                x.Ticket.SlaState,
                x.SlaDueAtUtc,
                x.Ticket.CurrentOwnerEmployeeId,
                x.OwnerName,
                x.Ticket.CreatedAtUtc,
                x.DepartmentCode))
            .ToListAsync(cancellationToken);

        var recentTickets = await EnrichRecentTicketsAsync(recentRows, channelCatalogue, cancellationToken);

        return new DashboardOverviewSnapshot(
            openTickets,
            myTickets,
            inDepartmentQueue,
            slaBreached,
            dueToday,
            pendingApproval,
            volumeTotal,
            Ordered(channelRows),
            Ordered(requestTypeCounts.Select(c => new DashboardCountRow<int>(
                c.Key, c.Key is { } id ? requestTypeNames.GetValueOrDefault(id, $"Request type {id}") : NoRequestTypeLabel, c.Count))),
            Ordered(departmentCounts.Select(c => new DashboardCountRow<int>(
                c.Key, departmentNames.GetValueOrDefault(c.Key, $"Department {c.Key}"), c.Count))),
            Ordered(statusCounts.Select(c => new DashboardCountRow<TicketStatus>(c.Key, c.Key.ToString(), c.Count))),
            Ordered(priorityCounts.Select(c => new DashboardCountRow<byte>(
                c.Key, c.Key is { } id ? priorityNames.GetValueOrDefault(id, $"Priority {id}") : UnclassifiedPriorityLabel, c.Count))),
            // Every bucket is always present, in age order, so the widget
            // shape is stable even when a bucket is empty.
            Enum.GetValues<BacklogAgeBucket>()
                .Select(b => new DashboardCountRow<BacklogAgeBucket>(b, b.ToString(), ageingByBucket.GetValueOrDefault(b)))
                .ToList(),
            recentTickets,
            closure);
    }

    public async Task<DashboardFilterOptionsSnapshot> GetFilterOptionsAsync(
        IReadOnlyCollection<int>? scopeDepartmentIds,
        IReadOnlyCollection<int>? agentDepartmentIds,
        IReadOnlyCollection<int>? requestTypeDepartmentIds,
        CancellationToken cancellationToken = default)
    {
        // Departments: the caller's visible ones (active or not, so a member
        // of a deactivated department still sees its own numbers), or every
        // active department for a cross-department role.
        var departmentsQuery = dbContext.Departments.AsNoTracking();
        departmentsQuery = scopeDepartmentIds is null
            ? departmentsQuery.Where(d => d.IsActive)
            : departmentsQuery.Where(d => scopeDepartmentIds.Contains(d.DepartmentId));
        var departments = await departmentsQuery
            .OrderBy(d => d.Name)
            .Select(d => new DashboardOptionRow<int>(d.DepartmentId, d.Name, null, d.IsActive))
            .ToListAsync(cancellationToken);

        // Agents: active employees assigned to the agent scope — the same
        // membership rule the Assign endpoint enforces for a ticket's owner.
        var assignments = dbContext.UserDepartmentAssignments.AsNoTracking();
        if (agentDepartmentIds is not null)
        {
            assignments = assignments.Where(a => agentDepartmentIds.Contains(a.DepartmentId));
        }

        var agents = await assignments
            .Join(dbContext.Employees.AsNoTracking().Where(e => e.DeactivatedAtUtc == null),
                a => a.EmployeeId, e => e.EmployeeId, (a, e) => new { e.EmployeeId, e.DisplayName })
            .Distinct()
            .OrderBy(e => e.DisplayName)
            .Select(e => new DashboardOptionRow<Guid>(e.EmployeeId, e.DisplayName, null, true))
            .ToListAsync(cancellationToken);

        // Channels: retired ones included (inactive), so historical volume
        // on a retired channel stays filterable; the picker marks them.
        var channels = await dbContext.Channels.AsNoTracking()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new DashboardOptionRow<byte>(c.ChannelId, c.Name, null, c.IsActive))
            .ToListAsync(cancellationToken);

        var requestTypesQuery = dbContext.RequestTypes.AsNoTracking().Where(r => r.IsActive);
        if (requestTypeDepartmentIds is not null)
        {
            requestTypesQuery = requestTypesQuery.Where(r => requestTypeDepartmentIds.Contains(r.DepartmentId));
        }

        var requestTypes = await requestTypesQuery
            .OrderBy(r => r.DepartmentId).ThenBy(r => r.Name)
            .Select(r => new DashboardOptionRow<int>(r.RequestTypeId, r.Name, r.DepartmentId, r.IsActive))
            .ToListAsync(cancellationToken);

        var priorities = await dbContext.Priorities.AsNoTracking()
            .OrderBy(p => p.DisplayOrder)
            .Select(p => new DashboardOptionRow<byte>(p.PriorityId, p.Name, null, true))
            .ToListAsync(cancellationToken);

        return new DashboardFilterOptionsSnapshot(departments, agents, channels, requestTypes, priorities);
    }

    /// <summary>
    /// The label each counted request type is shown under. A name is the request type's own, except where the
    /// same name is configured for more than one request type anywhere in the catalogue (several departments each
    /// have a "General"): those read "Name — Department", the department being the one the request type belongs to
    /// (<c>RequestTypes.DepartmentId</c> → <c>Departments</c>), so the rows stay tellable apart. Every request type
    /// keeps its own id and its own row — nothing is merged by name — and the drill-down still filters on that id.
    /// A request type whose department cannot be resolved keeps its plain name; no department is ever guessed.
    /// Judged against the whole catalogue rather than the rows on screen, so a label does not change with the
    /// date range or the filters.
    /// </summary>
    private async Task<Dictionary<int, string>> RequestTypeDisplayNamesAsync(
        IReadOnlyCollection<int> requestTypeIds, CancellationToken cancellationToken)
    {
        var catalogue = await dbContext.RequestTypes.AsNoTracking()
            .Select(r => new { r.RequestTypeId, r.Name, r.DepartmentId })
            .ToListAsync(cancellationToken);
        var duplicatedNames = catalogue
            .GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var departmentNames = duplicatedNames.Count == 0
            ? new Dictionary<int, string>()
            : await dbContext.Departments.AsNoTracking()
                .ToDictionaryAsync(d => d.DepartmentId, d => d.Name, cancellationToken);

        return catalogue
            .Where(r => requestTypeIds.Contains(r.RequestTypeId))
            .ToDictionary(
                r => r.RequestTypeId,
                r => duplicatedNames.Contains(r.Name.Trim()) && departmentNames.TryGetValue(r.DepartmentId, out var department)
                    ? $"{r.Name} — {department}"
                    : r.Name);
    }

    private sealed record ChannelInfo(byte ChannelId, string Name, string Code, bool IsActive);

    /// <summary>One candidate "last action" of a ticket: when it happened and who did it (an employee id, or an already-known display name such as the virtual agent).</summary>
    private sealed class ActionRow
    {
        public long TicketId { get; init; }
        public DateTime AtUtc { get; init; }
        public Guid? EmployeeId { get; init; }
        public string? Name { get; init; }
    }

    /// <summary>
    /// Volume by originating channel, built from the whole Channels catalogue so every channel is
    /// listed: active channels always (zero when idle), retired channels only while they still
    /// carry tickets in the range. Channels sharing a display name are merged into one row, ids
    /// that match no catalogue row are summed into a single "Other" row, and tickets with no
    /// originating interaction stay in "No channel recorded" — so the rows always add up to
    /// every ticket counted and nothing is dropped or double-counted.
    /// </summary>
    private static IReadOnlyList<DashboardCountRow<byte>> BuildChannelVolumeRows(
        IReadOnlyList<ChannelInfo> catalogue, IReadOnlyList<(byte? Key, int Count)> counts)
    {
        var known = catalogue.Select(c => c.ChannelId).ToHashSet();
        var countById = new Dictionary<byte, int>();
        var unmapped = 0;
        var unrecorded = 0;
        foreach (var (key, count) in counts)
        {
            if (key is not { } id)
            {
                unrecorded += count;
            }
            else if (known.Contains(id))
            {
                countById[id] = countById.GetValueOrDefault(id) + count;
            }
            else
            {
                unmapped += count;
            }
        }

        var rows = new List<DashboardCountRow<byte>>();
        foreach (var sameName in catalogue
            .Where(c => c.IsActive || countById.GetValueOrDefault(c.ChannelId) > 0)
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var representative = sameName.OrderByDescending(c => c.IsActive).ThenBy(c => c.ChannelId).First();
            rows.Add(new DashboardCountRow<byte>(
                representative.ChannelId, representative.Name, sameName.Sum(c => countById.GetValueOrDefault(c.ChannelId)), representative.Code));
        }

        if (unmapped > 0)
        {
            rows.Add(new DashboardCountRow<byte>(null, OtherChannelLabel, unmapped, OtherChannelCode));
        }

        if (unrecorded > 0)
        {
            rows.Add(new DashboardCountRow<byte>(null, NotRecordedChannelLabel, unrecorded));
        }

        return rows;
    }

    /// <summary>Adds each Recent ticket's originating channel and last meaningful actor — a fixed handful of set-based queries for all rows together, never one per ticket.</summary>
    private async Task<IReadOnlyList<DashboardRecentTicketRow>> EnrichRecentTicketsAsync(
        IReadOnlyList<DashboardRecentTicketRow> rows, IReadOnlyList<ChannelInfo> channels, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var ticketIds = rows.Select(r => r.TicketId).ToList();
        var originating = await dbContext.TicketInteractions.AsNoTracking()
            .Where(i => i.IsOriginatingInteraction && ticketIds.Contains(i.TicketId))
            .OrderBy(i => i.TicketInteractionId)
            .Select(i => new { i.TicketId, i.ChannelId })
            .ToListAsync(cancellationToken);
        var channelByTicket = originating.GroupBy(o => o.TicketId).ToDictionary(g => g.Key, g => g.First().ChannelId);
        var channelById = channels.ToDictionary(c => c.ChannelId);
        var lastActions = await LastMeaningfulActionsAsync(ticketIds, cancellationToken);

        return rows.Select(r =>
        {
            byte? channelId = channelByTicket.TryGetValue(r.TicketId, out var id) ? id : null;
            var channel = channelId is { } cid && channelById.TryGetValue(cid, out var info) ? info : null;
            return r with
            {
                ChannelId = channel?.ChannelId,
                ChannelName = channelId is null ? null : channel?.Name ?? OtherChannelLabel,
                ChannelCode = channelId is null ? null : channel?.Code ?? OtherChannelCode,
                LastActionByName = lastActions.GetValueOrDefault(r.TicketId)
            };
        }).ToList();
    }

    /// <summary>
    /// Who performed the most recent meaningful action on each ticket, read from the structured
    /// activity stores the ticket's own Activity timeline is built from: a status change, an
    /// assignment, a workflow event (transfer, approval, reclassification, handoff…), a ticket
    /// note, and the conversation's human-agent and virtual-agent replies. Only actions with a
    /// recorded actor count — system-driven rows (no actor, <c>ActorIsSystem</c>, SLA/escalation
    /// housekeeping, automatic routing, customer and platform transcript lines) are skipped, so a
    /// background job can never be shown as the last handler, and the ticket's creator or current
    /// owner is never assumed. The virtual agent reads "AI Assistant". One query per store for all
    /// tickets together; tickets with no qualifying action are simply absent.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, string>> LastMeaningfulActionsAsync(
        IReadOnlyList<long> ticketIds, CancellationToken cancellationToken)
    {
        var candidates = new List<ActionRow>();

        candidates.AddRange(await dbContext.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => ticketIds.Contains(h.TicketId) && h.Dimension == TicketStatusDimension.TicketStatus
                && h.ActorEmployeeId != null && !h.ActorIsSystem)
            .GroupBy(h => h.TicketId)
            .Select(g => g.OrderByDescending(h => h.OccurredAtUtc).ThenByDescending(h => h.TicketStatusHistoryId)
                .Select(h => new ActionRow { TicketId = h.TicketId, AtUtc = h.OccurredAtUtc, EmployeeId = h.ActorEmployeeId })
                .First())
            .ToListAsync(cancellationToken));

        candidates.AddRange(await dbContext.TicketAssignments.AsNoTracking()
            .Where(a => ticketIds.Contains(a.TicketId) && a.AssigningActorEmployeeId != null)
            .GroupBy(a => a.TicketId)
            .Select(g => g.OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.TicketAssignmentId)
                .Select(a => new ActionRow { TicketId = a.TicketId, AtUtc = a.AssignedAtUtc, EmployeeId = a.AssigningActorEmployeeId })
                .First())
            .ToListAsync(cancellationToken));

        candidates.AddRange(await dbContext.TicketWorkflowEvents.AsNoTracking()
            .Where(e => ticketIds.Contains(e.TicketId) && e.ActorEmployeeId != null)
            .GroupBy(e => e.TicketId)
            .Select(g => g.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.TicketWorkflowEventId)
                .Select(e => new ActionRow { TicketId = e.TicketId, AtUtc = e.OccurredAtUtc, EmployeeId = e.ActorEmployeeId })
                .First())
            .ToListAsync(cancellationToken));

        candidates.AddRange(await dbContext.TicketNotes.AsNoTracking()
            .Where(n => ticketIds.Contains(n.TicketId))
            .GroupBy(n => n.TicketId)
            .Select(g => g.OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.TicketNoteId)
                .Select(n => new ActionRow { TicketId = n.TicketId, AtUtc = n.CreatedAtUtc, EmployeeId = n.AuthorEmployeeId })
                .First())
            .ToListAsync(cancellationToken));

        candidates.AddRange(await dbContext.TicketInteractionMessages.AsNoTracking()
            .Join(dbContext.TicketInteractions.AsNoTracking().Where(i => ticketIds.Contains(i.TicketId)),
                m => m.TicketInteractionId, i => i.TicketInteractionId, (m, i) => new { i.TicketId, Message = m })
            .Where(x => x.Message.Sender == InteractionMessageSender.VirtualAgent
                || (x.Message.Sender == InteractionMessageSender.HumanAgent && x.Message.SenderName != null && x.Message.SenderName != ""))
            .GroupBy(x => x.TicketId)
            .Select(g => g.OrderByDescending(x => x.Message.SentAtUtc).ThenByDescending(x => x.Message.TicketInteractionMessageId)
                .Select(x => new ActionRow
                {
                    TicketId = x.TicketId,
                    AtUtc = x.Message.SentAtUtc,
                    Name = x.Message.Sender == InteractionMessageSender.VirtualAgent ? AiActorLabel : x.Message.SenderName
                })
                .First())
            .ToListAsync(cancellationToken));

        var employeeIds = candidates.Where(c => c.EmployeeId is not null).Select(c => c.EmployeeId!.Value).Distinct().ToList();
        var employeeNames = employeeIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.Employees.AsNoTracking()
                .Where(e => employeeIds.Contains(e.EmployeeId))
                .ToDictionaryAsync(e => e.EmployeeId, e => e.DisplayName, cancellationToken);

        var result = new Dictionary<long, string>();
        foreach (var ticketGroup in candidates.GroupBy(c => c.TicketId))
        {
            // The newest candidate whose actor can actually be named: an unresolvable actor is never replaced by a guess.
            var name = ticketGroup
                .OrderByDescending(c => c.AtUtc)
                .Select(c => !string.IsNullOrWhiteSpace(c.Name) ? c.Name
                    : c.EmployeeId is { } id && employeeNames.TryGetValue(id, out var display) && !string.IsNullOrWhiteSpace(display) ? display
                    : null)
                .FirstOrDefault(n => n is not null);
            if (name is not null)
            {
                result[ticketGroup.Key] = name;
            }
        }

        return result;
    }

    /// <summary>Largest first, ties by label; a "not recorded" row (null key) sorts after every real value of the same count.</summary>
    private static IReadOnlyList<DashboardCountRow<TKey>> Ordered<TKey>(IEnumerable<DashboardCountRow<TKey>> rows) where TKey : struct =>
        rows.OrderByDescending(r => r.Count).ThenBy(r => r.Key is null).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
}
