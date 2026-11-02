using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketRepository(TigerCsDbContext dbContext) : ITicketRepository
{
    public Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.FirstOrDefaultAsync(t => t.TicketId == ticketId, cancellationToken);

    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        await dbContext.Tickets.AddAsync(ticket, cancellationToken);

    public Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.CountAsync(t => t.TicketNumber.StartsWith(ticketNumberPrefix), cancellationToken);

    public async Task<TicketQueryResult> SearchAsync(TicketQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = dbContext.Tickets.AsQueryable();

        filtered = filtered.VisibleTo(
            dbContext, query.VisibleDepartmentIds, query.AgentScopeEmployeeId, createdOnly: query.CreatedOnlyScope,
            ownerEmployeeId: query.OwnerVisibilityEmployeeId, includeClaimable: query.IncludeClaimable);

        if (query.DepartmentId is { } departmentId)
        {
            // The Call Center filter of an eligible agent also lists what they could take (it is already inside their scope above).
            filtered = query.DepartmentFilterIncludesClaimable
                ? filtered.Where(TicketQueryFilters.AvailableToTake(dbContext).OrElse(t => t.CurrentDepartmentId == departmentId))
                : filtered.Where(t => t.CurrentDepartmentId == departmentId);
        }

        if (query.AvailableToTakeOnly)
        {
            filtered = filtered.Where(TicketQueryFilters.AvailableToTake(dbContext));
        }

        if (query.CreatedByEmployeeId is { } createdBy)
        {
            filtered = filtered.Where(t => t.CreatedByEmployeeId == createdBy);
        }

        if (query.CategoryId is { } categoryId)
        {
            filtered = filtered.Where(t => t.CategoryId == categoryId);
        }

        if (query.PriorityId is { } priorityId)
        {
            filtered = filtered.Where(t => t.PriorityId == priorityId);
        }

        if (query.TicketStatus is { } ticketStatus)
        {
            filtered = filtered.Where(t => t.TicketStatus == ticketStatus);
        }

        if (query.VerificationStatus is { } verificationStatus)
        {
            filtered = filtered.Where(t => t.VerificationStatus == verificationStatus);
        }

        if (query.OwnerEmployeeId is { } ownerEmployeeId)
        {
            filtered = filtered.Where(t => t.CurrentOwnerEmployeeId == ownerEmployeeId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Free-text search over what a ticket carries about itself: its
            // number and summary, the customer name and unit number it
            // snapshotted (CRM Buyer, external lookup or manual entry), and
            // the phone number captured by the intake it was promoted from. A
            // plain substring match on each — no ranking, no fuzzy matching —
            // and nothing here widens which tickets the caller may see: this
            // predicate only narrows the already-scoped set above.
            var search = query.Search.Trim();

            // A term SHAPED LIKE A PHONE NUMBER is also matched as one, on the
            // number's canonical form, so "+971501234567", "971501234567" and
            // "+971 50 123 4567" find the same ticket whichever way the intake
            // captured it. The gate is LooksLikeNumber, not "has a digit":
            // the unit code "M-401" must keep matching unit numbers, not every
            // caller whose number happens to contain 401. For a term that does
            // pass the gate, the SQL below strips exactly the characters
            // Normalize does, written out because EF Core translates
            // string.Replace to SQL REPLACE() but cannot call the helper.
            var searchIsPhoneShaped = CustomerPhoneNumber.LooksLikeNumber(search);
            var searchDigits = searchIsPhoneShaped ? CustomerPhoneNumber.Normalize(search) : string.Empty;

            filtered = filtered.Where(t =>
                t.TicketNumber.Contains(search)
                || t.RequestSummary.Contains(search)
                || (t.CrmBuyerCustomerName != null && t.CrmBuyerCustomerName.Contains(search))
                || (t.ExternalCustomerName != null && t.ExternalCustomerName.Contains(search))
                || (t.CrmBuyerUnitNumber != null && t.CrmBuyerUnitNumber.Contains(search))
                || (t.ManualUnitNumber != null && t.ManualUnitNumber.Contains(search))
                || dbContext.IntakeRecords.Any(i => i.LinkedTicketId == t.TicketId
                    && (i.PhoneNumber.Contains(search)
                        || (searchIsPhoneShaped && i.PhoneNumber.Trim().Replace("+", "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("/", "").Contains(searchDigits)))));
        }

        // Dashboard drill-down filters (Dashboard Phase 1) — the same shared
        // predicates the dashboard aggregates are computed with, so a KPI
        // and its list never disagree. The time-relative ones evaluate at
        // the instant the application service supplied.
        if (query.ChannelId is { } channelId)
        {
            filtered = filtered.WithOriginatingChannel(dbContext, channelId);
        }

        if (query.RequestTypeId is { } requestTypeId)
        {
            filtered = filtered.Where(t => t.RequestTypeId == requestTypeId);
        }

        if (query.Unclassified)
        {
            filtered = filtered.Where(t => t.RequestTypeId == null);
        }

        if (query.ActiveOnly || query.InDepartmentQueue || query.SlaBreached || query.DueToday || query.BacklogAge is not null)
        {
            filtered = filtered.Active();
        }

        if (query.InDepartmentQueue)
        {
            filtered = filtered.Where(t => t.CurrentOwnerEmployeeId == null);
        }

        if (query.SlaBreached)
        {
            filtered = filtered.Where(t => t.SlaState == SlaState.Breached);
        }

        // Supervisor review: tickets an agent passed up as "unable to resolve" / "needs escalation". Narrows the
        // already department-scoped set above; it can never add a ticket the caller could not otherwise see.
        if (query.Escalated)
        {
            filtered = filtered.Where(t => dbContext.TicketWorkflowEvents.Any(e =>
                e.TicketId == t.TicketId
                && e.EventType == WorkflowEventType.TicketTransferred
                && (e.TransferReasonCode == (byte)TicketTransferReason.UnableToResolve
                    || e.TransferReasonCode == (byte)TicketTransferReason.NeedsEscalation)));
        }

        var nowUtc = query.NowUtc ?? DateTime.UtcNow;
        if (query.DueToday)
        {
            filtered = filtered.DueOnUtcDay(dbContext, nowUtc.Date);
        }

        if (query.BacklogAge is { } bucket)
        {
            filtered = filtered.InBacklogAgeBucket(bucket, nowUtc);
        }

        if (query.PendingApprovalFor is { } approverScope)
        {
            filtered = filtered.WithPendingApprovalActionableBy(dbContext, approverScope);
        }

        if (query.ClosureCategory is { } closureCategory)
        {
            filtered = filtered.InClosureCategory(dbContext, closureCategory, query.ReviewsAreTest);
        }

        if (query.ClosedFromUtc is not null || query.ClosedToUtc is not null)
        {
            filtered = filtered.LatestClosureBetween(dbContext, query.ClosedFromUtc, query.ClosedToUtc);
        }

        if (query.CreatedFromUtc is { } createdFrom)
        {
            filtered = filtered.Where(t => t.CreatedAtUtc >= createdFrom);
        }

        if (query.CreatedToUtc is { } createdTo)
        {
            filtered = filtered.Where(t => t.CreatedAtUtc < createdTo);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        // Sorting by priority has to say where an unclassified ticket goes,
        // because SQL Server sorts NULL first ascending — and ascending is
        // "most urgent first" here (1 = Critical), so the default would put
        // every ticket nobody has judged above every Critical one. An
        // unjudged ticket is not urgent; it sorts last either way, and the
        // secondary key still surfaces the oldest of them first.
        filtered = query.SortBy switch
        {
            TicketSortBy.Priority => query.SortDescending
                ? filtered.OrderBy(t => t.PriorityId == null).ThenByDescending(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc)
                : filtered.OrderBy(t => t.PriorityId == null).ThenBy(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc),
            _ => query.SortDescending
                ? filtered.OrderByDescending(t => t.CreatedAtUtc)
                : filtered.OrderBy(t => t.CreatedAtUtc)
        };

        var items = await filtered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new TicketQueryResult(items, totalCount);
    }

    public void SetRowVersion(Ticket ticket, byte[] rowVersion) =>
        dbContext.Entry(ticket).Property(t => t.RowVersion).OriginalValue = rowVersion;

    public async Task<CustomerHistoryQueryResult> SearchCustomerHistoryAsync(
        CustomerHistoryQuery query, CancellationToken cancellationToken = default)
    {
        IQueryable<Ticket> filtered;
        if (query is { IncludeLinkedExternalIdentity: true, CrmBuyerCustomerId: { } linkedCrmId,
            ExternalSource: { } linkedSource, ExternalCustomerId: { } linkedExternalId })
        {
            filtered = dbContext.Tickets.Where(t => t.CrmBuyerCustomerId == linkedCrmId
                || (t.CustomerVerificationSource == linkedSource && t.ExternalCustomerId == linkedExternalId));
        }
        else if (query.CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            filtered = dbContext.Tickets.Where(t => t.CrmBuyerCustomerId == crmBuyerCustomerId);
        }
        else if (query is { ExternalSource: { } externalSource, ExternalCustomerId: { } externalCustomerId })
        {
            // The persisted external verification identity pair — exact
            // match on both fields, never widened by display name or phone.
            filtered = dbContext.Tickets.Where(t =>
                t.CustomerVerificationSource == externalSource && t.ExternalCustomerId == externalCustomerId);
        }
        else if (query.TicketIds is { Count: > 0 } ticketIds)
        {
            filtered = dbContext.Tickets.Where(t => ticketIds.Contains(t.TicketId));
        }
        else
        {
            return new CustomerHistoryQueryResult([], 0, 0, 0);
        }

        filtered = filtered.VisibleTo(dbContext, query.VisibleDepartmentIds, query.AgentScopeEmployeeId);

        if (query.ExcludeTicketId is { } excludeTicketId)
        {
            filtered = filtered.Where(t => t.TicketId != excludeTicketId);
        }

        if (query.UnitNumber is { } unitNumber)
        {
            // Exact match on the ticket's own unit-number snapshot (CRM Buyer
            // or manual) — the deterministic same-unit rule of Phase E's
            // duplicate awareness; no fuzzy matching.
            filtered = filtered.Where(t => t.CrmBuyerUnitNumber == unitNumber || t.ManualUnitNumber == unitNumber);
        }

        var statusCounts = await filtered
            .GroupBy(t => t.TicketStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var closedCount = statusCounts
            .Where(s => s.Status == TicketStatus.Resolved || s.Status == TicketStatus.Closed)
            .Sum(s => s.Count);
        var totalCount = statusCounts.Sum(s => s.Count);

        var ordered = query.OrderActiveFirst
            ? filtered
                .OrderBy(t => t.TicketStatus == TicketStatus.Resolved || t.TicketStatus == TicketStatus.Closed ? 1 : 0)
                .ThenByDescending(t => t.CreatedAtUtc)
            : filtered.OrderByDescending(t => t.CreatedAtUtc);

        var items = await ordered
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        return new CustomerHistoryQueryResult(items, totalCount, totalCount - closedCount, closedCount);
    }

    public async Task<DashboardSnapshot> GetDashboardSnapshotAsync(
        DashboardSnapshotQuery query, CancellationToken cancellationToken = default)
    {
        var visible = dbContext.Tickets.AsQueryable().VisibleTo(dbContext, query.VisibleDepartmentIds, query.AgentScopeEmployeeId, createdOnly: query.CreatedOnlyScope, ownerEmployeeId: query.CallerEmployeeId);

        // Same "active" set as TicketQueryFilters.Active, legacy
        // PendingThirdParty included — see its remark for why.
        var active = visible.Where(t =>
            t.TicketStatus == TicketStatus.Open
            || t.TicketStatus == TicketStatus.InProgress
            || t.TicketStatus == TicketStatus.PendingCustomer
            || t.TicketStatus == TicketStatus.PendingThirdParty);

        var atRiskUntil = query.NowUtc + query.AtRiskWindow;
        var currentSla = dbContext.TicketSlaInstances.Where(s => s.PeriodEndAtUtc == null);

        var openTickets = await active.CountAsync(cancellationToken);
        var unassigned = await active.CountAsync(t => t.CurrentOwnerEmployeeId == null, cancellationToken);
        var slaBreached = await active.CountAsync(t => t.SlaState == SlaState.Breached, cancellationToken);
        // An unclassified ticket has no priority, so it is neither Critical
        // nor High. Written explicitly rather than leaning on SQL's
        // NULL <= 2 being unknown, so the intent survives a refactor.
        var criticalOrHigh = await active.CountAsync(t => t.PriorityId != null && t.PriorityId <= 2, cancellationToken);
        var pendingCustomer = await visible.CountAsync(t => t.TicketStatus == TicketStatus.PendingCustomer, cancellationToken);
        var reopened = await active.CountAsync(t => t.ReopenCount > 0, cancellationToken);
        var myTickets = await visible.CountAsync(t => t.CurrentOwnerEmployeeId == query.CallerEmployeeId, cancellationToken);

        // At risk: a pending, unbreached deadline on the current SLA period
        // falls due within the window — either clock; a First Response
        // deadline only counts while no first human response is recorded.
        var slaAtRisk = await active
            .Join(currentSla, t => t.TicketId, s => s.TicketId, (t, s) => new { t, s })
            .Where(x =>
                (!x.s.ResolutionBreached && x.s.ResolutionDueAtUtc > query.NowUtc && x.s.ResolutionDueAtUtc <= atRiskUntil)
                || (!x.s.FirstResponseBreached && x.t.FirstHumanResponseAtUtc == null
                    && x.s.FirstResponseDueAtUtc > query.NowUtc && x.s.FirstResponseDueAtUtc <= atRiskUntil))
            .Select(x => x.t.TicketId)
            .Distinct()
            .CountAsync(cancellationToken);

        var resolvedToday = await visible
            .Join(
                dbContext.TicketResolutions.Where(r => r.IsCurrent && r.ResolvedAtUtc >= query.ResolvedTodayStartUtc),
                t => t.TicketId, r => r.TicketId, (t, r) => t.TicketId)
            .Distinct()
            .CountAsync(cancellationToken);

        // Tickets Requiring Attention: breached first, then due-soon, then
        // critical, high, unassigned — a bounded, deterministic ranking over
        // active tickets left-joined to their current SLA period.
        var attentionRows = await active
            .GroupJoin(currentSla, t => t.TicketId, s => s.TicketId, (t, slas) => new { t, slas })
            .SelectMany(x => x.slas.DefaultIfEmpty(), (x, s) => new
            {
                Ticket = x.t,
                SlaDueAtUtc = s != null && !s.ResolutionBreached ? s.ResolutionDueAtUtc : (DateTime?)null
            })
            .Where(x =>
                x.Ticket.SlaState == SlaState.Breached
                || (x.Ticket.PriorityId != null && x.Ticket.PriorityId <= 2)
                || x.Ticket.CurrentOwnerEmployeeId == null
                || (x.SlaDueAtUtc != null && x.SlaDueAtUtc <= atRiskUntil))
            .OrderBy(x =>
                x.Ticket.SlaState == SlaState.Breached ? 0
                : x.SlaDueAtUtc != null && x.SlaDueAtUtc <= atRiskUntil ? 1
                : x.Ticket.PriorityId == 1 ? 2
                : x.Ticket.PriorityId == 2 ? 3
                : 4)
            .ThenBy(x => x.SlaDueAtUtc ?? DateTime.MaxValue)
            // Same NULLs-sort-first trap as the queue: without this an
            // unclassified ticket would tie-break ahead of a Critical one
            // inside the same rank bucket.
            .ThenBy(x => x.Ticket.PriorityId == null)
            .ThenBy(x => x.Ticket.PriorityId)
            .ThenBy(x => x.Ticket.CreatedAtUtc)
            .Take(query.AttentionLimit)
            .ToListAsync(cancellationToken);

        return new DashboardSnapshot(
            openTickets,
            unassigned,
            slaAtRisk,
            slaBreached,
            criticalOrHigh,
            pendingCustomer,
            resolvedToday,
            reopened,
            myTickets,
            attentionRows.Select(x => new DashboardAttentionTicket(x.Ticket, x.SlaDueAtUtc)).ToList());
    }
}
