using System.Linq.Expressions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// The ticket predicates the ticket queue (<see cref="TicketRepository.SearchAsync"/>)
/// and the Operational Dashboard (<see cref="DashboardQueryRepository"/>)
/// share, so a dashboard number and its drill-down list are computed by
/// the very same SQL. Every method composes onto an <see cref="IQueryable{T}"/>
/// — nothing here materializes rows.
/// </summary>
internal static class TicketQueryFilters
{
    /// <summary>The department-visibility scope: null = no restriction, otherwise the ticket's CURRENT department must be one of them.</summary>
    public static IQueryable<Ticket> InScope(this IQueryable<Ticket> tickets, IReadOnlyCollection<int>? departmentIds) =>
        departmentIds is null ? tickets : tickets.Where(t => departmentIds.Contains(t.CurrentDepartmentId));

    /// <summary>
    /// The full visibility scope as one predicate — the set-based form of <c>TicketVisibilityRule.CanViewTicketAsync</c>; keep them in step.
    /// With no <paramref name="agentEmployeeId"/> this is exactly <see cref="InScope"/>. With one (a restricted CS Agent) a ticket is
    /// visible when it is <b>currently assigned</b> to that employee; or a department employee holds it and that employee is its
    /// <b>responsible CS Agent</b> (follow-up and closure); or - only for a caller whose other role grants it - it sits in one of
    /// <paramref name="departmentIds"/>. Never a ticket assigned to another CS Agent, never an unassigned one (the Take Ownership pool is
    /// added only by <paramref name="includeClaimable"/>, an explicit request), and never because the employee once created or handled it.
    /// A null/empty department set grants nothing here: department or queue membership alone never shows a CS Agent a ticket.
    /// </summary>
    /// <remarks>
    /// <paramref name="createdOnly"/> (the Genesys integration account) replaces all of the above with <c>CreatedByEmployeeId == agent</c>.
    /// </remarks>
    public static IQueryable<Ticket> VisibleTo(
        this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, IReadOnlyCollection<int>? departmentIds, Guid? agentEmployeeId,
        bool createdOnly = false, Guid? ownerEmployeeId = null, bool includeClaimable = false)
    {
        if (agentEmployeeId is not { } agentId)
        {
            // A department-scoped employee ALWAYS sees the tickets currently assigned to them, whatever department those sit in now
            // (My Tickets). It adds only their own tickets — never anyone else's. Set-based twin of TicketVisibilityRule's owner rule.
            return departmentIds is not null && ownerEmployeeId is { } ownerId
                ? tickets.Where(t => departmentIds.Contains(t.CurrentDepartmentId) || t.CurrentOwnerEmployeeId == ownerId)
                : tickets.InScope(departmentIds);
        }

        if (createdOnly)
        {
            // The Genesys integration account: only the tickets it created. Ownership and handled interactions grant nothing, so a manually
            // created ticket is never exposed because the account touched it.
            return tickets.Where(t => t.CreatedByEmployeeId == agentId);
        }

        var departments = departmentIds?.ToList() ?? [];
        var csAgentRole = Roles.CsAgent;

        // Set-based twin of TicketVisibilityRule.CanFollowDepartmentWorkAsync. A department employee (an owner who is not a CS Agent) is
        // working the ticket, and the caller is its responsible CS Agent: the employee on the most recent CS-Agent assignment row, or - when no
        // CS Agent was ever assigned - the ticket's creator. Correlated EXISTS only, so a ticket is still one row.
        Expression<Func<Ticket, bool>> followsDepartmentWork = t =>
            t.CurrentOwnerEmployeeId != null
            && t.CurrentOwnerEmployeeId != agentId
            && !dbContext.UserRoles.Any(ur => ur.UserId == t.CurrentOwnerEmployeeId
                && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.Name == csAgentRole))
            && (dbContext.TicketAssignments.Any(a => a.TicketId == t.TicketId
                    && a.AssignedEmployeeId == agentId
                    && !dbContext.TicketAssignments.Any(later => later.TicketId == t.TicketId
                        && later.TicketAssignmentId > a.TicketAssignmentId
                        && dbContext.UserRoles.Any(ur => ur.UserId == later.AssignedEmployeeId
                            && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.Name == csAgentRole))))
                || (t.CreatedByEmployeeId == agentId
                    && !dbContext.TicketAssignments.Any(a => a.TicketId == t.TicketId
                        && dbContext.UserRoles.Any(ur => ur.UserId == a.AssignedEmployeeId
                            && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.Name == csAgentRole)))));

        Expression<Func<Ticket, bool>> visible = ((Expression<Func<Ticket, bool>>)(t => t.CurrentOwnerEmployeeId == agentId))
            .OrElse(followsDepartmentWork)
            .OrElse(t => departments.Contains(t.CurrentDepartmentId));

        // Call Center agents also SEE (read-only) the unassigned human-handling tickets they may take ownership of. This only widens the
        // list for a caller the application service has already proven eligible; the same predicate gates the Take Ownership action.
        return tickets.Where(includeClaimable ? visible.OrElse(AvailableToTake(dbContext)) : visible);
    }

    /// <summary>
    /// THE definition of a ticket a Call Center agent may take ownership of - used by the list scope, the "available to take" filter and the
    /// single-ticket check, so what is listed, what is openable and what can be claimed can never drift apart. It mirrors the held-back rules of
    /// the Call Center backlog distribution (<c>Preview/Execute_TigerCS_PROD_CallCenterBacklogDistribution</c>) so a manual claim and the planned
    /// bulk assignment never disagree about which tickets are theirs:
    /// <list type="bullet">
    /// <item>active lifecycle only (Open, InProgress, PendingCustomer, legacy PendingThirdParty) - Resolved and Closed are excluded;</item>
    /// <item>nobody owns it and it has no current assignment row - a ticket owned by another employee is never claimable;</item>
    /// <item>no pending approval (<see cref="HasPendingApproval"/>) - moving it would pull it out from under its approvers;</item>
    /// <item>not an active AI conversation (<see cref="HasActiveAiConversation"/>) - moving it would switch off AI closure / chatbot inactivity.</item>
    /// </list>
    /// </summary>
    public static Expression<Func<Ticket, bool>> AvailableToTake(TigerCsDbContext dbContext)
    {
        var pendingApproval = HasPendingApproval(dbContext);
        var activeAi = HasActiveAiConversation(dbContext);
        var notPending = pendingApproval.Not();
        var notAi = activeAi.Not();
        Expression<Func<Ticket, bool>> core = t =>
            (t.TicketStatus == TicketStatus.Open
                || t.TicketStatus == TicketStatus.InProgress
                || t.TicketStatus == TicketStatus.PendingCustomer
                || t.TicketStatus == TicketStatus.PendingThirdParty)
            && t.CurrentOwnerEmployeeId == null
            && !dbContext.TicketAssignments.Any(a => a.TicketId == t.TicketId && a.IsCurrent);
        return core.AndAlso(notPending).AndAlso(notAi);
    }

    /// <summary>The ticket has an approval waiting for a decision (<c>TicketApprovals.Status = Pending</c>).</summary>
    public static Expression<Func<Ticket, bool>> HasPendingApproval(TigerCsDbContext dbContext) =>
        t => dbContext.TicketApprovals.Any(a => a.TicketId == t.TicketId && a.Status == ApprovalStatus.Pending);

    /// <summary>
    /// An ACTIVE AI conversation, exactly as the backlog distribution defines it: a Genesys-sourced interaction that is not ended, not closed for
    /// inactivity, needs no human follow-up and was not taken by a person - and no interaction on the ticket is flagged for human follow-up, and
    /// there is no open human handoff (Waiting / Assigned / InProgress). Anything that needs or already has a person is human work.
    /// </summary>
    public static Expression<Func<Ticket, bool>> HasActiveAiConversation(TigerCsDbContext dbContext) =>
        t => dbContext.TicketInteractions.Any(i =>
                i.TicketId == t.TicketId
                && i.Source == InteractionContextSource.Genesys
                && i.EndedAtUtc == null
                && i.InactivityClosedAtUtc == null
                && !i.RequiresHumanFollowUp
                && i.HandledByUserId == null)
            && !dbContext.TicketInteractions.Any(i => i.TicketId == t.TicketId && i.RequiresHumanFollowUp)
            && !dbContext.TicketAgentHandoffs.Any(h => h.TicketId == t.TicketId
                && (h.Status == AgentHandoffStatus.WaitingForAgent
                    || h.Status == AgentHandoffStatus.Assigned
                    || h.Status == AgentHandoffStatus.InProgress));

    /// <summary>
    /// "Active" throughout: the non-terminal lifecycle statuses — Open,
    /// InProgress, PendingCustomer, and the legacy PendingThirdParty.
    /// Resolved and Closed are the terminal ones.
    ///
    /// <para>
    /// <b>The legacy status stays in this set deliberately.</b> It is retired
    /// as a target, not as a state: a ticket that was already PendingThirdParty
    /// is unfinished work, and dropping it from "active" would hide it from
    /// every queue and KPI — the one place someone would go to return it to
    /// InProgress.
    /// </para>
    /// </summary>
    public static IQueryable<Ticket> Active(this IQueryable<Ticket> tickets) =>
        tickets.Where(t =>
            t.TicketStatus == TicketStatus.Open
            || t.TicketStatus == TicketStatus.InProgress
            || t.TicketStatus == TicketStatus.PendingCustomer
            || t.TicketStatus == TicketStatus.PendingThirdParty);

    /// <summary>Tickets whose ORIGINATING interaction (the channel the ticket entered the system on; at most one per ticket) arrived on <paramref name="channelId"/>. Later interactions on other channels never change it.</summary>
    public static IQueryable<Ticket> WithOriginatingChannel(this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, byte channelId) =>
        tickets.Where(t => dbContext.TicketInteractions.Any(i =>
            i.TicketId == t.TicketId && i.IsOriginatingInteraction && i.ChannelId == channelId));

    /// <summary>Tickets in one Open Backlog Ageing bucket at <paramref name="nowUtc"/> — the explicit half-open boundaries of <see cref="BacklogAgeBoundaries"/>, applied as CreatedAtUtc comparisons.</summary>
    public static IQueryable<Ticket> InBacklogAgeBucket(this IQueryable<Ticket> tickets, BacklogAgeBucket bucket, DateTime nowUtc)
    {
        var (createdAfter, createdAtOrBefore) = BacklogAgeBoundaries.CreatedAtWindow(bucket, nowUtc);
        if (createdAfter is { } after)
        {
            tickets = tickets.Where(t => t.CreatedAtUtc > after);
        }

        if (createdAtOrBefore is { } atOrBefore)
        {
            tickets = tickets.Where(t => t.CreatedAtUtc <= atOrBefore);
        }

        return tickets;
    }

    /// <summary>
    /// Closed tickets in one Dashboard closure KPI. Written as correlated
    /// <c>EXISTS</c> predicates, so each ticket is counted once however many
    /// reviews, invitations or audit rows it has — never a join that could
    /// multiply rows.
    /// </summary>
    public static IQueryable<Ticket> InClosureCategory(
        this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, TicketClosureCategory category, bool reviewsAreTest = false)
    {
        var closed = tickets.Where(t => t.TicketStatus == TicketStatus.Closed);
        return category switch
        {
            // A submitted review for the ticket's current cycle (a reopen starts
            // a new cycle that needs its own review). Which kind counts follows
            // the notification mode: real reviews in production, Test-mode ones
            // in UAT — so UAT can validate the cards while production
            // satisfaction statistics (which only read IsTest = false) are
            // untouched either way.
            TicketClosureCategory.Reviewed => closed.Where(t => dbContext.TicketReviews.Any(r =>
                r.TicketId == t.TicketId && r.ReopenCycle == t.ReopenCount && r.IsTest == reviewsAreTest)),
            TicketClosureCategory.NotReviewed => closed.Where(t => !dbContext.TicketReviews.Any(r =>
                r.TicketId == t.TicketId && r.ReopenCycle == t.ReopenCount && r.IsTest == reviewsAreTest)),
            TicketClosureCategory.ClosedByAi => closed.Where(LatestClosureIs(dbContext, TicketClosureAuditActions.ClosedByAi)),
            TicketClosureCategory.ClosedByAgent => closed.Where(LatestClosureIs(dbContext, TicketClosureAuditActions.ClosedByAgent)),
            _ => closed.Where(_ => false)
        };
    }

    /// <summary>
    /// Closed tickets whose <b>latest closure</b> — the most recent time the
    /// ticket entered Closed, per the append-only status history — falls in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>). A reopened ticket
    /// closed again is dated by its last closure.
    /// </summary>
    public static IQueryable<Ticket> LatestClosureBetween(
        this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, DateTime? fromUtc, DateTime? toUtc)
    {
        tickets = tickets.Where(t => t.TicketStatus == TicketStatus.Closed);
        if (fromUtc is { } from)
        {
            tickets = tickets.Where(t => dbContext.TicketStatusHistoryEntries
                .Where(h => h.TicketId == t.TicketId && h.Dimension == TicketStatusDimension.TicketStatus && h.NewValue == (byte)TicketStatus.Closed)
                .Max(h => (DateTime?)h.OccurredAtUtc) >= from);
        }

        if (toUtc is { } to)
        {
            tickets = tickets.Where(t => dbContext.TicketStatusHistoryEntries
                .Where(h => h.TicketId == t.TicketId && h.Dimension == TicketStatusDimension.TicketStatus && h.NewValue == (byte)TicketStatus.Closed)
                .Max(h => (DateTime?)h.OccurredAtUtc) < to);
        }

        return tickets;
    }

    /// <summary>
    /// The ticket's most recent closing audit entry (highest id — the audit
    /// trail is append-only) has this action. A reopened ticket closed again
    /// is judged by its last closure; an inactivity or document-delivery
    /// closure is the latest closure of its own, matching neither AI nor agent.
    /// </summary>
    private static Expression<Func<Ticket, bool>> LatestClosureIs(TigerCsDbContext dbContext, string action)
    {
        var closingActions = TicketClosureAuditActions.AllClosures.ToList();
        return t => dbContext.AuditEntries.Any(a =>
            a.EntityType == TicketClosureAuditActions.EntityType
            && a.EntityId == t.TicketId.ToString()
            && a.Action == action
            && !dbContext.AuditEntries.Any(later =>
                later.EntityType == TicketClosureAuditActions.EntityType
                && later.EntityId == a.EntityId
                && later.AuditEntryId > a.AuditEntryId
                && closingActions.Contains(later.Action)));
    }

    /// <summary>The current SLA period of every ticket — the one row per ticket with no period end.</summary>
    public static IQueryable<Domain.Modules.SlaAndEscalation.TicketSlaInstance> CurrentSlaPeriods(TigerCsDbContext dbContext) =>
        dbContext.TicketSlaInstances.Where(s => s.PeriodEndAtUtc == null);

    /// <summary>
    /// Tickets whose current SLA period's resolution deadline falls within
    /// the UTC calendar day starting at <paramref name="dayStartUtc"/> and
    /// has not already breached. The resolution clock is the ticket's
    /// applicable due date (the same deadline the attention/recent lists
    /// show); a breached clock is "SLA Breached", not "due today".
    /// </summary>
    public static IQueryable<Ticket> DueOnUtcDay(this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, DateTime dayStartUtc)
    {
        var dayEndUtc = dayStartUtc.AddDays(1);
        return tickets.Where(t => CurrentSlaPeriods(dbContext).Any(s =>
            s.TicketId == t.TicketId
            && !s.ResolutionBreached
            && s.ResolutionDueAtUtc >= dayStartUtc
            && s.ResolutionDueAtUtc < dayEndUtc));
    }

    /// <summary>Tickets carrying at least one Pending approval that <paramref name="scope"/>'s caller is authorized to action.</summary>
    public static IQueryable<Ticket> WithPendingApprovalActionableBy(
        this IQueryable<Ticket> tickets, TigerCsDbContext dbContext, ApprovalApproverScope scope)
    {
        var actionable = dbContext.TicketApprovals
            .Where(a => a.Status == ApprovalStatus.Pending)
            .Where(ActionableBy(scope))
            .Select(a => a.TicketId);

        return tickets.Where(t => actionable.Contains(t.TicketId));
    }

    /// <summary>
    /// The set-based form of <c>TicketApprovalAppService.IsAuthorizedApproverAsync</c>
    /// (see <see cref="ApprovalApproverScope"/> for the rule, target kind by
    /// target kind). Written as one expression so it translates to a single
    /// WHERE clause; the override short-circuits to "every Pending approval"
    /// exactly as <c>AuthorizationGate</c> would answer per record.
    /// </summary>
    public static Expression<Func<TicketApproval, bool>> ActionableBy(ApprovalApproverScope scope)
    {
        if (scope.Override)
        {
            return _ => true;
        }

        var callerId = scope.CallerEmployeeId;
        var roles = scope.CallerRoles.ToList();
        var departmentIds = scope.MemberDepartmentIds.ToList();
        var holdsDefaultRole = scope.HoldsDepartmentTargetDefaultApproverRole;

        return a =>
            (a.TargetKind == Domain.Modules.WorkflowConfiguration.ApprovalTargetKind.Employee
                && a.TargetEmployeeId == callerId)
            || (a.TargetKind == Domain.Modules.WorkflowConfiguration.ApprovalTargetKind.Role
                && a.TargetRoleName != null && roles.Contains(a.TargetRoleName))
            || (a.TargetKind == Domain.Modules.WorkflowConfiguration.ApprovalTargetKind.Department
                && a.TargetDepartmentId != null && departmentIds.Contains(a.TargetDepartmentId.Value)
                && ((a.TargetRoleName == null && holdsDefaultRole)
                    || (a.TargetRoleName != null && roles.Contains(a.TargetRoleName))));
    }
}
