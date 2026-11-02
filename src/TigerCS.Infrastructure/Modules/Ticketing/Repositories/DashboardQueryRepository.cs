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
public sealed class DashboardQueryRepository(TigerCsDbContext dbContext) : IDashboardQueryRepository
{
    private const string NotRecordedChannelLabel = "No channel recorded";
    private const string NoRequestTypeLabel = "No request type";
    private const string UnclassifiedPriorityLabel = "Not set";

    public async Task<DashboardOverviewSnapshot> GetOverviewAsync(
        DashboardOverviewQuery query, CancellationToken cancellationToken = default)
    {
        // The dimension filters apply to everything; the date range only to
        // the volume breakdowns; "active" only to the backlog side.
        var scoped = dbContext.Tickets.AsNoTracking().VisibleTo(dbContext, query.ScopeDepartmentIds, query.AgentScopeEmployeeId, createdOnly: query.CreatedOnlyScope, ownerEmployeeId: query.OwnerVisibilityEmployeeId);

        if (query.DepartmentId is { } departmentFilterId)
        {
            scoped = scoped.Where(t => t.CurrentDepartmentId == departmentFilterId);
        }

        if (query.OwnerEmployeeId is { } ownerEmployeeId)
        {
            scoped = scoped.Where(t => t.CurrentOwnerEmployeeId == ownerEmployeeId);
        }

        if (query.ChannelId is { } channelId)
        {
            scoped = scoped.WithOriginatingChannel(dbContext, channelId);
        }

        if (query.RequestTypeId is { } requestTypeId)
        {
            scoped = scoped.Where(t => t.RequestTypeId == requestTypeId);
        }

        if (query.PriorityId is { } priorityId)
        {
            scoped = scoped.Where(t => t.PriorityId == priorityId);
        }

        // The four closure KPIs always represent Closed tickets, so they take
        // every filter except the status one.
        var closureBase = scoped;

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
        var closure = new DashboardClosureCounts(
            await closureScope.InClosureCategory(dbContext, TicketClosureCategory.Reviewed, testReviews).CountAsync(cancellationToken),
            await closureScope.InClosureCategory(dbContext, TicketClosureCategory.NotReviewed, testReviews).CountAsync(cancellationToken),
            await closureScope.InClosureCategory(dbContext, TicketClosureCategory.ClosedByAi, testReviews).CountAsync(cancellationToken),
            await closureScope.InClosureCategory(dbContext, TicketClosureCategory.ClosedByAgent, testReviews).CountAsync(cancellationToken));

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
        var channelIds = channelCounts.Where(c => c.Key is not null).Select(c => c.Key!.Value).ToList();
        var channelNames = channelIds.Count == 0
            ? new Dictionary<byte, string>()
            : await dbContext.Channels.AsNoTracking()
                .Where(c => channelIds.Contains(c.ChannelId))
                .ToDictionaryAsync(c => c.ChannelId, c => c.Name, cancellationToken);

        var requestTypeCounts = await volume
            .GroupBy(t => t.RequestTypeId)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var requestTypeIds = requestTypeCounts.Where(c => c.Key is not null).Select(c => c.Key!.Value).ToList();
        var requestTypeNames = requestTypeIds.Count == 0
            ? new Dictionary<int, string>()
            : await dbContext.RequestTypes.AsNoTracking()
                .Where(r => requestTypeIds.Contains(r.RequestTypeId))
                .ToDictionaryAsync(r => r.RequestTypeId, r => r.Name, cancellationToken);

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

        return new DashboardOverviewSnapshot(
            openTickets,
            myTickets,
            inDepartmentQueue,
            slaBreached,
            dueToday,
            pendingApproval,
            volumeTotal,
            Ordered(channelCounts.Select(c => new DashboardCountRow<byte>(
                c.Key, c.Key is { } id ? channelNames.GetValueOrDefault(id, $"Channel {id}") : NotRecordedChannelLabel, c.Count))),
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
            recentRows,
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

    /// <summary>Largest first, ties by label; a "not recorded" row (null key) sorts after every real value of the same count.</summary>
    private static IReadOnlyList<DashboardCountRow<TKey>> Ordered<TKey>(IEnumerable<DashboardCountRow<TKey>> rows) where TKey : struct =>
        rows.OrderByDescending(r => r.Count).ThenBy(r => r.Key is null).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
}
