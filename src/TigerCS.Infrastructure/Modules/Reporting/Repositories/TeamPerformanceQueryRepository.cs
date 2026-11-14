using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Reporting.Repositories;

/// <summary>
/// The Team Performance report's read model. Every count is a SQL-side
/// aggregate read with <c>AsNoTracking</c>; the records behind a count are
/// produced by the very same predicate the count used, so a number and
/// its list can never disagree.
///
/// <para>
/// The employee set is every active <c>Employees</c> row in every
/// department; roles come from the Identity tables (<c>AspNetRoles</c> /
/// <c>AspNetUserRoles</c>) and departments from <c>Departments</c> /
/// <c>UserDepartmentAssignments</c> — nothing is hardcoded. The activity metrics
/// are read from the append-only history tables — assignments, status
/// history, notes, resolutions, workflow events and customer follow-ups —
/// never from the ticket's current owner, which only says who holds a
/// ticket now.
/// </para>
/// </summary>
public sealed class TeamPerformanceQueryRepository(TigerCsDbContext dbContext) : ITeamPerformanceQueryRepository
{
    // The three projection shapes below are plain classes built with object
    // initializers, not records with constructors: EF Core translates a
    // member-init projection into SQL and can apply set operations (UNION)
    // and further GROUP BY to it, whereas a constructor call is a client
    // projection it refuses to union over.

    /// <summary>A (employee, ticket) pair — the unit "tickets worked" is counted in, after de-duplication across the activity sources.</summary>
    private sealed class ActorTicket
    {
        public Guid EmployeeId { get; init; }
        public long TicketId { get; init; }
    }

    /// <summary>
    /// One SLA event on a clock — a genuine breach, or an on-time achievement — with the employee who held the ticket at the relevant
    /// moment (null when nobody did, including after a release). <see cref="SourceId"/> is the history / resolution / ticket row it came from.
    /// </summary>
    private sealed class SlaMoment
    {
        public long SourceId { get; init; }
        public long TicketId { get; init; }
        public bool IsFirstResponse { get; init; }
        public DateTime AtUtc { get; init; }
        public DateTime? DueAtUtc { get; init; }
        public long? PeriodId { get; init; }
        public Guid? HolderEmployeeId { get; init; }
        public int? HolderDepartmentId { get; init; }
    }

    /// <summary>A record's key before the ticket facts are joined on: the ticket and the metric-specific instant (breach or completion time), null for the two ticket-only metrics.</summary>
    private sealed class RecordKey
    {
        public long TicketId { get; init; }
        public DateTime? Instant { get; init; }
    }

    public async Task<IReadOnlyList<TeamPerformanceEmployeeRow>> GetActiveEmployeesAsync(CancellationToken cancellationToken = default)
    {
        var active = dbContext.Employees.AsNoTracking().Where(e => e.DeactivatedAtUtc == null);

        var employees = await active
            .Select(e => new { e.EmployeeId, e.DisplayName })
            .ToListAsync(cancellationToken);

        if (employees.Count == 0)
        {
            return [];
        }

        var roles = await dbContext.UserRoles.AsNoTracking()
            .Join(dbContext.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .Join(active, x => x.UserId, e => e.EmployeeId, (x, _) => x)
            .ToListAsync(cancellationToken);

        var rolesByEmployee = roles
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Name!).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

        var memberships = await dbContext.UserDepartmentAssignments.AsNoTracking()
            .Join(active, a => a.EmployeeId, e => e.EmployeeId, (a, _) => a)
            .Join(dbContext.Departments.AsNoTracking(), a => a.DepartmentId, d => d.DepartmentId,
                (a, d) => new { a.EmployeeId, d.DepartmentId, d.Name, d.Code, a.IsPrimary })
            .ToListAsync(cancellationToken);

        var membershipsByEmployee = memberships
            .GroupBy(m => m.EmployeeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TeamPerformanceDepartmentRow>)g
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new TeamPerformanceDepartmentRow(m.DepartmentId, m.Name, m.Code, m.IsPrimary))
                    .ToList());

        return employees
            .Select(e => new TeamPerformanceEmployeeRow(
                e.EmployeeId, e.DisplayName,
                rolesByEmployee.GetValueOrDefault(e.EmployeeId, []),
                membershipsByEmployee.GetValueOrDefault(e.EmployeeId, [])))
            .ToList();
    }

    public async Task<IReadOnlyList<TeamPerformanceOptionDto>> GetDepartmentsAsync(CancellationToken cancellationToken = default)
    {
        // Every active department, plus any department that is the primary department of an active employee (even if the department
        // itself has since been deactivated), so an employee's primary department is always selectable.
        var departments = await dbContext.Departments.AsNoTracking()
            .Where(d => d.IsActive || dbContext.UserDepartmentAssignments.Any(a =>
                a.DepartmentId == d.DepartmentId && a.IsPrimary
                && dbContext.Employees.Any(e => e.EmployeeId == a.EmployeeId && e.DeactivatedAtUtc == null)))
            .Select(d => new { d.DepartmentId, d.Name })
            .ToListAsync(cancellationToken);

        return departments
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new TeamPerformanceOptionDto(d.DepartmentId.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Name))
            .ToList();
    }

    public async Task<TeamPerformanceMetricsSnapshot> GetMetricsAsync(
        IReadOnlyCollection<Guid> employeeIds, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default)
    {
        var ids = employeeIds.ToList();

        var currentlyAssigned = await CurrentlyAssigned(ids)
            .GroupBy(t => t.CurrentOwnerEmployeeId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var ticketsWorked = await WorkedPairs(ids, rangeStartUtc, rangeEndUtc)
            .GroupBy(x => x.EmployeeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var completedFollowUps = await CompletedFollowUps(ids, rangeStartUtc, rangeEndUtc)
            .GroupBy(h => h.AssignedEmployeeId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return new TeamPerformanceMetricsSnapshot(currentlyAssigned, ticketsWorked, completedFollowUps);
    }

    public async Task<IReadOnlyList<TeamPerformanceRecordRow>> GetRecordsAsync(
        Guid employeeId, TeamPerformanceMetric metric, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default)
    {
        List<Guid> one = [employeeId];

        return metric switch
        {
            TeamPerformanceMetric.CurrentlyAssigned => await WithTicketFacts(
                    CurrentlyAssigned(one).Select(t => new RecordKey { TicketId = t.TicketId, Instant = null }))
                .ToListAsync(cancellationToken),

            TeamPerformanceMetric.TicketsWorked => await WithTicketFacts(
                    WorkedPairs(one, rangeStartUtc, rangeEndUtc).Select(p => new RecordKey { TicketId = p.TicketId, Instant = null }))
                .ToListAsync(cancellationToken),

            // One row per completed follow-up — the count is of work items,
            // so a ticket with two completed follow-ups appears twice, each
            // with its own completion time.
            TeamPerformanceMetric.CompletedFollowUps => await WithTicketFacts(
                    CompletedFollowUps(one, rangeStartUtc, rangeEndUtc).Select(h => new RecordKey { TicketId = h.TicketId, Instant = h.CompletedAtUtc }),
                    completedAt: true)
                .ToListAsync(cancellationToken),

            // Distinct tickets with at least one genuine breach event in the period attributed to this employee, each with the earliest one.
            TeamPerformanceMetric.SlaBreaches => await WithTicketFacts(
                    BreachMoments(rangeStartUtc, rangeEndUtc)
                        .Where(b => b.HolderEmployeeId == employeeId)
                        .GroupBy(b => b.TicketId)
                        .Select(g => new RecordKey { TicketId = g.Key, Instant = g.Min(b => b.AtUtc) }),
                    breachedAt: true)
                .ToListAsync(cancellationToken),

            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown Team Performance metric.")
        };
    }

    // ---- The shared predicates: each count and its records use exactly one of these. ----

    /// <summary>Current state: tickets the employee holds now that are not Closed.</summary>
    private IQueryable<Ticket> CurrentlyAssigned(List<Guid> ids) =>
        dbContext.Tickets.AsNoTracking()
            .Where(t => t.CurrentOwnerEmployeeId != null
                && ids.Contains(t.CurrentOwnerEmployeeId!.Value)
                && t.TicketStatus != TicketStatus.Closed);

    /// <summary>
    /// Distinct (employee, ticket) pairs with at least one recorded action
    /// in the window, across every activity source: an assignment to the
    /// employee, a lifecycle-dimension change they made, a note they wrote,
    /// a resolution they recorded, a workflow event they raised, or a
    /// customer follow-up assigned to or completed by them in the window.
    /// </summary>
    private IQueryable<ActorTicket> WorkedPairs(List<Guid> ids, DateTime start, DateTime end)
    {
        var assignments = dbContext.TicketAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.AssignedEmployeeId) && a.AssignedAtUtc >= start && a.AssignedAtUtc < end)
            .Select(a => new ActorTicket { EmployeeId = a.AssignedEmployeeId, TicketId = a.TicketId });

        var statusChanges = dbContext.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.ActorEmployeeId != null && ids.Contains(h.ActorEmployeeId!.Value) && h.OccurredAtUtc >= start && h.OccurredAtUtc < end)
            .Select(h => new ActorTicket { EmployeeId = h.ActorEmployeeId!.Value, TicketId = h.TicketId });

        var notes = dbContext.TicketNotes.AsNoTracking()
            .Where(n => ids.Contains(n.AuthorEmployeeId) && n.CreatedAtUtc >= start && n.CreatedAtUtc < end)
            .Select(n => new ActorTicket { EmployeeId = n.AuthorEmployeeId, TicketId = n.TicketId });

        var resolutions = dbContext.TicketResolutions.AsNoTracking()
            .Where(r => ids.Contains(r.ResolvingEmployeeId) && r.ResolvedAtUtc >= start && r.ResolvedAtUtc < end)
            .Select(r => new ActorTicket { EmployeeId = r.ResolvingEmployeeId, TicketId = r.TicketId });

        var workflowEvents = dbContext.TicketWorkflowEvents.AsNoTracking()
            .Where(e => e.ActorEmployeeId != null && ids.Contains(e.ActorEmployeeId!.Value) && e.OccurredAtUtc >= start && e.OccurredAtUtc < end)
            .Select(e => new ActorTicket { EmployeeId = e.ActorEmployeeId!.Value, TicketId = e.TicketId });

        var handoffs = dbContext.TicketAgentHandoffs.AsNoTracking()
            .Where(h => h.AssignedEmployeeId != null && ids.Contains(h.AssignedEmployeeId!.Value)
                && ((h.AssignedAtUtc != null && h.AssignedAtUtc >= start && h.AssignedAtUtc < end)
                    || (h.CompletedAtUtc != null && h.CompletedAtUtc >= start && h.CompletedAtUtc < end)))
            .Select(h => new ActorTicket { EmployeeId = h.AssignedEmployeeId!.Value, TicketId = h.TicketId });

        // Union (not Concat) is the de-duplication: one pair however many
        // actions the employee took on the ticket.
        return assignments
            .Union(statusChanges)
            .Union(notes)
            .Union(resolutions)
            .Union(workflowEvents)
            .Union(handoffs);
    }

    /// <summary>Customer follow-ups assigned to the employee and completed in the window.</summary>
    private IQueryable<TicketAgentHandoff> CompletedFollowUps(List<Guid> ids, DateTime start, DateTime end) =>
        dbContext.TicketAgentHandoffs.AsNoTracking()
            .Where(h => h.Status == AgentHandoffStatus.Completed
                && h.AssignedEmployeeId != null && ids.Contains(h.AssignedEmployeeId!.Value)
                && h.CompletedAtUtc != null && h.CompletedAtUtc >= start && h.CompletedAtUtc < end);

    private const byte BreachedStateValue = (byte)SlaState.Breached;

    /// <summary>
    /// The genuine SLA breach events recorded in the window: <c>SlaState</c> history rows that SlaBreachProcessor wrote for a clock
    /// (First Response or Resolution — recognised by the processor's own note prefixes, shared as <see cref="SlaBreachNotes"/>). Other rows
    /// that merely carry the Breached state — "First human response recorded" on a ticket already Breached — are not breaches and are not
    /// counted. A second clock breaching on an already-Breached ticket IS one (Breached→Breached with its own note).
    ///
    /// <para>
    /// Each event is attributed to whoever held the ticket at the breach DEADLINE (the due time of that clock's SLA period), not at the
    /// moment the scheduler happened to record it: a delayed write cannot move the breach to a later holder. The holder is the latest
    /// assignment at or before the deadline, unless that assignment had already ended (released to a queue, closed) by then — then nobody.
    /// An assignment holds the ticket over [AssignedAtUtc, SupersededAtUtc): a deadline exactly at its end belongs to the next holder (or nobody), and a row that
    /// is current now was never ended whatever its SupersededAtUtc says (the Call Center backlog Rollback script flips an old row back to current).
    /// With no SLA period to read the deadline from, the recorded moment is used.
    /// </para>
    /// </summary>
    private IQueryable<SlaMoment> BreachMoments(DateTime start, DateTime end) =>
        dbContext.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.Dimension == TicketStatusDimension.SlaState && h.NewValue == BreachedStateValue
                && h.OccurredAtUtc >= start && h.OccurredAtUtc < end
                && h.Note != null
                && (h.Note.StartsWith(SlaBreachNotes.FirstResponsePrefix) || h.Note.StartsWith(SlaBreachNotes.ResolutionPrefix)))
            .Select(h => new
            {
                Row = h,
                IsFirstResponse = h.Note!.StartsWith(SlaBreachNotes.FirstResponsePrefix),
                Due = dbContext.TicketSlaInstances
                    .Where(i => i.TicketId == h.TicketId && i.PeriodStartAtUtc <= h.OccurredAtUtc
                        && (i.PeriodEndAtUtc == null || h.OccurredAtUtc < i.PeriodEndAtUtc))
                    .OrderByDescending(i => i.PeriodStartAtUtc)
                    .Select(i => h.Note!.StartsWith(SlaBreachNotes.FirstResponsePrefix) ? i.FirstResponseDueAtUtc : i.ResolutionDueAtUtc)
                    .Select(d => (DateTime?)d)
                    .FirstOrDefault()
            })
            .Select(x => new
            {
                x.Row,
                x.IsFirstResponse,
                x.Due,
                // The moment the breach happened: the deadline, never later than the moment it was recorded.
                Deadline = x.Due != null && x.Due < x.Row.OccurredAtUtc ? x.Due.Value : x.Row.OccurredAtUtc
            })
            .Select(x => new SlaMoment
            {
                SourceId = x.Row.TicketStatusHistoryId,
                TicketId = x.Row.TicketId,
                IsFirstResponse = x.IsFirstResponse,
                AtUtc = x.Row.OccurredAtUtc,
                DueAtUtc = x.Due,
                HolderEmployeeId = dbContext.TicketAssignments
                    .Where(a => a.TicketId == x.Row.TicketId && a.AssignedAtUtc <= x.Deadline)
                    .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.TicketAssignmentId)
                    .Select(a => a.IsCurrent || a.SupersededAtUtc == null || a.SupersededAtUtc > x.Deadline ? (Guid?)a.AssignedEmployeeId : null)
                    .FirstOrDefault(),
                HolderDepartmentId = dbContext.TicketAssignments
                    .Where(a => a.TicketId == x.Row.TicketId && a.AssignedAtUtc <= x.Deadline)
                    .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.TicketAssignmentId)
                    .Select(a => (int?)a.AssignedDepartmentId)
                    .FirstOrDefault()
            });

    /// <summary>First Response achieved on time in the window: the first human response is on or before the due time and no breach was flagged on that clock. Holder at the moment of the response.</summary>
    private IQueryable<SlaMoment> FirstResponseMetMoments(DateTime start, DateTime end) =>
        dbContext.Tickets.AsNoTracking()
            .Where(t => t.FirstHumanResponseAtUtc != null && t.FirstHumanResponseAtUtc >= start && t.FirstHumanResponseAtUtc < end)
            .Select(t => new
            {
                t.TicketId,
                At = t.FirstHumanResponseAtUtc!.Value,
                // First Response is carried across periods, so the latest period holds its due time and breach flag.
                Due = dbContext.TicketSlaInstances.Where(i => i.TicketId == t.TicketId)
                    .OrderByDescending(i => i.PeriodStartAtUtc).ThenByDescending(i => i.TicketSlaInstanceId)
                    .Select(i => (DateTime?)i.FirstResponseDueAtUtc).FirstOrDefault(),
                Breached = dbContext.TicketSlaInstances.Where(i => i.TicketId == t.TicketId)
                    .OrderByDescending(i => i.PeriodStartAtUtc).ThenByDescending(i => i.TicketSlaInstanceId)
                    .Select(i => (bool?)i.FirstResponseBreached).FirstOrDefault(),
                PeriodId = dbContext.TicketSlaInstances.Where(i => i.TicketId == t.TicketId)
                    .OrderByDescending(i => i.PeriodStartAtUtc).ThenByDescending(i => i.TicketSlaInstanceId)
                    .Select(i => (long?)i.TicketSlaInstanceId).FirstOrDefault()
            })
            .Where(x => x.Due != null && x.Breached == false && x.At <= x.Due)
            .Select(x => new SlaMoment
            {
                SourceId = x.TicketId,
                TicketId = x.TicketId,
                IsFirstResponse = true,
                AtUtc = x.At,
                DueAtUtc = x.Due,
                PeriodId = x.PeriodId,
                HolderEmployeeId = dbContext.TicketAssignments
                    .Where(a => a.TicketId == x.TicketId && a.AssignedAtUtc <= x.At)
                    .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.TicketAssignmentId)
                    .Select(a => a.IsCurrent || a.SupersededAtUtc == null || a.SupersededAtUtc > x.At ? (Guid?)a.AssignedEmployeeId : null)
                    .FirstOrDefault(),
                HolderDepartmentId = dbContext.TicketAssignments
                    .Where(a => a.TicketId == x.TicketId && a.AssignedAtUtc <= x.At)
                    .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.TicketAssignmentId)
                    .Select(a => (int?)a.AssignedDepartmentId)
                    .FirstOrDefault()
            });

    /// <summary>Resolution recorded on time in the window: inside the SLA period in force, on or before that period's due time, with no breach flagged on that period. Holder at the moment of the resolution. A period can appear more than once if it has several resolution rows; the caller keeps the earliest.</summary>
    private IQueryable<SlaMoment> ResolutionMetMoments(DateTime start, DateTime end) =>
        dbContext.TicketResolutions.AsNoTracking()
            .Where(r => r.ResolvedAtUtc >= start && r.ResolvedAtUtc < end)
            .Join(dbContext.TicketSlaInstances.AsNoTracking(), r => r.TicketId, i => i.TicketId, (r, i) => new { Resolution = r, Period = i })
            .Where(x => x.Period.PeriodStartAtUtc <= x.Resolution.ResolvedAtUtc
                && (x.Period.PeriodEndAtUtc == null || x.Resolution.ResolvedAtUtc < x.Period.PeriodEndAtUtc)
                && !x.Period.ResolutionBreached && x.Resolution.ResolvedAtUtc <= x.Period.ResolutionDueAtUtc)
            .Select(x => new SlaMoment
            {
                SourceId = x.Resolution.TicketResolutionId,
                TicketId = x.Resolution.TicketId,
                IsFirstResponse = false,
                AtUtc = x.Resolution.ResolvedAtUtc,
                DueAtUtc = x.Period.ResolutionDueAtUtc,
                PeriodId = x.Period.TicketSlaInstanceId,
                HolderEmployeeId = dbContext.TicketAssignments
                    .Where(a => a.TicketId == x.Resolution.TicketId && a.AssignedAtUtc <= x.Resolution.ResolvedAtUtc)
                    .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.TicketAssignmentId)
                    .Select(a => a.IsCurrent || a.SupersededAtUtc == null || a.SupersededAtUtc > x.Resolution.ResolvedAtUtc ? (Guid?)a.AssignedEmployeeId : null)
                    .FirstOrDefault(),
                HolderDepartmentId = dbContext.TicketAssignments
                    .Where(a => a.TicketId == x.Resolution.TicketId && a.AssignedAtUtc <= x.Resolution.ResolvedAtUtc)
                    .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.TicketAssignmentId)
                    .Select(a => (int?)a.AssignedDepartmentId)
                    .FirstOrDefault()
            });

    public async Task<TeamPerformanceSlaEvents> GetSlaEventsAsync(
        IReadOnlyCollection<Guid> employeeIds, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default)
    {
        var ids = employeeIds.ToList();
        if (ids.Count == 0)
        {
            return new TeamPerformanceSlaEvents([], []);
        }

        var breaches = await BreachMoments(rangeStartUtc, rangeEndUtc)
            .Where(m => m.HolderEmployeeId != null && ids.Contains(m.HolderEmployeeId!.Value))
            .ToListAsync(cancellationToken);
        var firstResponses = await FirstResponseMetMoments(rangeStartUtc, rangeEndUtc)
            .Where(m => m.HolderEmployeeId != null && ids.Contains(m.HolderEmployeeId!.Value))
            .ToListAsync(cancellationToken);
        var resolutions = await ResolutionMetMoments(rangeStartUtc, rangeEndUtc)
            .Where(m => m.HolderEmployeeId != null && ids.Contains(m.HolderEmployeeId!.Value))
            .ToListAsync(cancellationToken);

        TeamPerformanceSlaEventRow ToRow(SlaMoment m) => new(
            m.HolderEmployeeId!.Value, m.TicketId, m.IsFirstResponse ? SlaDeadlineType.FirstResponse : SlaDeadlineType.Resolution,
            m.AtUtc, m.DueAtUtc, m.HolderDepartmentId, m.PeriodId, m.SourceId);

        // One event per (ticket, clock, deadline): the SLA engine's idempotency already guarantees it; this keeps a stray repeat from counting twice.
        var breachRows = breaches
            .GroupBy(m => (m.TicketId, m.IsFirstResponse, Deadline: m.DueAtUtc ?? m.AtUtc))
            .Select(g => g.OrderBy(m => m.SourceId).First())
            .Select(ToRow)
            .ToList();

        // One Resolution evaluation per SLA period (the first resolution in it); one First Response per ticket.
        var metRows = firstResponses.Select(ToRow)
            .Concat(resolutions.GroupBy(m => (m.TicketId, m.PeriodId)).Select(g => g.OrderBy(m => m.AtUtc).ThenBy(m => m.SourceId).First()).Select(ToRow))
            .ToList();

        return new TeamPerformanceSlaEvents(breachRows, metRows);
    }

    /// <summary>
    /// Joins record keys to the ticket's facts and its current department's
    /// name, in one query, newest first: by the metric's own instant when it
    /// has one (breach or completion time), otherwise by ticket creation.
    /// Ordering happens before the final projection so it is SQL, not a
    /// client sort.
    /// </summary>
    private IQueryable<TeamPerformanceRecordRow> WithTicketFacts(IQueryable<RecordKey> keys, bool breachedAt = false, bool completedAt = false)
    {
        var byInstant = breachedAt || completedAt;
        return keys
            .Join(dbContext.Tickets.AsNoTracking(), k => k.TicketId, t => t.TicketId, (k, t) => new { k.Instant, Ticket = t })
            .GroupJoin(dbContext.Departments.AsNoTracking(), x => x.Ticket.CurrentDepartmentId, d => d.DepartmentId, (x, departments) => new { x, departments })
            .SelectMany(y => y.departments.DefaultIfEmpty(), (y, d) => new { y.x.Instant, y.x.Ticket, DepartmentName = d != null ? d.Name : null })
            .OrderByDescending(x => byInstant ? x.Instant : x.Ticket.CreatedAtUtc)
            .ThenByDescending(x => x.Ticket.TicketId)
            .Select(x => new TeamPerformanceRecordRow(
                x.Ticket.TicketId,
                x.Ticket.TicketNumber,
                x.Ticket.RequestSummary,
                x.Ticket.TicketStatus,
                x.Ticket.PriorityId,
                x.DepartmentName,
                x.Ticket.CreatedAtUtc,
                breachedAt ? x.Instant : null,
                completedAt ? x.Instant : null));
    }

    // ---- SLA evaluations behind the counts. ----

    private sealed class TicketFacts
    {
        public long TicketId { get; init; }
        public string TicketNumber { get; init; } = string.Empty;
        public TicketStatus TicketStatus { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public int CurrentDepartmentId { get; init; }
        public DateTime? FirstHumanResponseAtUtc { get; init; }
    }

    private sealed class ResolutionFacts
    {
        public long TicketId { get; init; }
        public DateTime ResolvedAtUtc { get; init; }
    }

    private sealed record RequestTypeSlaPolicyFacts(bool HasFirstResponse);

    public async Task<IReadOnlyList<TeamPerformanceSlaEvaluationRow>> GetSlaEvaluationsAsync(
        Guid employeeId, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default)
    {
        List<Guid> one = [employeeId];

        // Breached and Met come from the very method the table's counts come from, so a count and its list cannot disagree.
        var events = await GetSlaEventsAsync(one, rangeStartUtc, rangeEndUtc, cancellationToken);

        // Pending — clocks still open on the tickets the employee holds now (the same set as Currently Assigned).
        var openTicketIds = await CurrentlyAssigned(one).Select(t => t.TicketId).ToListAsync(cancellationToken);

        var ticketIds = events.Breaches.Select(e => e.TicketId)
            .Concat(events.Met.Select(e => e.TicketId))
            .Concat(openTicketIds)
            .Distinct().ToList();
        if (ticketIds.Count == 0)
        {
            return [];
        }

        var tickets = (await dbContext.Tickets.AsNoTracking()
                .Where(t => ticketIds.Contains(t.TicketId))
                .Select(t => new TicketFacts
                {
                    TicketId = t.TicketId,
                    TicketNumber = t.TicketNumber,
                    TicketStatus = t.TicketStatus,
                    CreatedAtUtc = t.CreatedAtUtc,
                    CurrentDepartmentId = t.CurrentDepartmentId,
                    FirstHumanResponseAtUtc = t.FirstHumanResponseAtUtc
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.TicketId);

        var instancesByTicket = (await dbContext.TicketSlaInstances.AsNoTracking()
                .Where(i => ticketIds.Contains(i.TicketId))
                .ToListAsync(cancellationToken))
            .GroupBy(i => i.TicketId)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.PeriodStartAtUtc).ThenBy(i => i.TicketSlaInstanceId).ToList());

        var resolutionsByTicket = (await dbContext.TicketResolutions.AsNoTracking()
                .Where(r => ticketIds.Contains(r.TicketId))
                .Select(r => new ResolutionFacts { TicketId = r.TicketId, ResolvedAtUtc = r.ResolvedAtUtc })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.TicketId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ResolvedAtUtc).ToList());

        var instanceIds = instancesByTicket.Values.SelectMany(v => v).Select(i => i.TicketSlaInstanceId).ToList();
        var pauses = (await dbContext.TicketSlaPausePeriods.AsNoTracking()
                .Where(p => instanceIds.Contains(p.TicketSlaInstanceId))
                .Select(p => new { p.TicketSlaInstanceId, p.StartedAtUtc, p.ResumedAtUtc })
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.TicketSlaInstanceId)
            .ToDictionary(
                g => g.Key,
                g => (Minutes: (int)g.Where(p => p.ResumedAtUtc != null).Sum(p => (p.ResumedAtUtc!.Value - p.StartedAtUtc).TotalMinutes),
                      Open: g.Any(p => p.ResumedAtUtc == null)));

        var departmentNames = await dbContext.Departments.AsNoTracking()
            .ToDictionaryAsync(d => d.DepartmentId, d => d.Name, cancellationToken);
        var priorityNames = await dbContext.Priorities.AsNoTracking()
            .ToDictionaryAsync(p => p.PriorityId, p => p.Name, cancellationToken);
        var policies = await dbContext.SlaPolicies.AsNoTracking().ToDictionaryAsync(p => p.PriorityId, cancellationToken);

        var requestTypePolicyIds = instancesByTicket.Values.SelectMany(v => v)
            .Where(i => i.RequestTypeSlaPolicyId != null).Select(i => i.RequestTypeSlaPolicyId!.Value).Distinct().ToList();
        var requestTypePolicies = requestTypePolicyIds.Count == 0
            ? new Dictionary<int, (RequestTypeSlaPolicyFacts Policy, string Name)>()
            : (await dbContext.RequestTypeSlaPolicies.AsNoTracking()
                    .Where(p => requestTypePolicyIds.Contains(p.RequestTypeSlaPolicyId))
                    .Join(dbContext.RequestTypes.AsNoTracking(), p => p.RequestTypeId, r => r.RequestTypeId,
                        (p, r) => new { p.RequestTypeSlaPolicyId, HasFirstResponse = p.FirstResponseTargetValue != null || p.FirstResponseMaximumValue != null, r.Name })
                    .ToListAsync(cancellationToken))
                .ToDictionary(x => x.RequestTypeSlaPolicyId, x => (Policy: new RequestTypeSlaPolicyFacts(x.HasFirstResponse), Name: x.Name));

        string? DepartmentName(int? departmentId, TicketFacts ticket) =>
            departmentNames.GetValueOrDefault(departmentId ?? ticket.CurrentDepartmentId);

        // The period in force at a moment.
        static TicketSlaInstance? Covering(List<TicketSlaInstance>? periods, DateTime at) =>
            periods?.LastOrDefault(i => i.PeriodStartAtUtc <= at && (i.PeriodEndAtUtc == null || at < i.PeriodEndAtUtc));

        DateTime? ResolutionIn(long ticketId, TicketSlaInstance period) =>
            resolutionsByTicket.GetValueOrDefault(ticketId)?
                .Where(r => r.ResolvedAtUtc >= period.PeriodStartAtUtc && (period.PeriodEndAtUtc == null || r.ResolvedAtUtc < period.PeriodEndAtUtc))
                .Select(r => (DateTime?)r.ResolvedAtUtc)
                .FirstOrDefault();

        TeamPerformanceSlaEvaluationRow Build(
            TeamPerformanceSlaEvaluationKind kind, TicketFacts ticket, int? holderDepartmentId, SlaDeadlineType type,
            TicketSlaInstance? period, List<TicketSlaInstance>? allPeriods, DateTime? eventAt)
        {
            policies.TryGetValue(period?.PriorityId ?? 0, out var priorityPolicy);
            var rt = period?.RequestTypeSlaPolicyId is { } rtId && requestTypePolicies.TryGetValue(rtId, out var found) ? found : default;
            var pause = period is not null && pauses.TryGetValue(period.TicketSlaInstanceId, out var p) ? p : (Minutes: 0, Open: false);

            DateTime? start = null, due = null, actual = null;
            int? target = null;
            SlaClockBasis? basis = null;
            if (period is not null)
            {
                if (type == SlaDeadlineType.FirstResponse)
                {
                    // First Response is carried, never restarted, so it runs from the first period's clock start.
                    start = allPeriods![0].PeriodStartAtUtc;
                    due = period.FirstResponseDueAtUtc;
                    actual = ticket.FirstHumanResponseAtUtc;
                    target = period.AppliedFirstResponseTargetMinutes ?? priorityPolicy?.FirstResponseTargetMinutes;
                    basis = rt.Policy is { HasFirstResponse: true } ? period.ResolutionClockBasis ?? priorityPolicy?.ClockBasis : priorityPolicy?.ClockBasis;
                }
                else
                {
                    start = period.PeriodStartAtUtc;
                    due = period.ResolutionDueAtUtc;
                    actual = ResolutionIn(ticket.TicketId, period);
                    target = period.AppliedResolutionTargetMinutes ?? priorityPolicy?.ResolutionTargetMinutes;
                    basis = period.ResolutionClockBasis ?? priorityPolicy?.ClockBasis;
                }
            }

            return new TeamPerformanceSlaEvaluationRow(
                kind, ticket.TicketId, ticket.TicketNumber, ticket.TicketStatus, ticket.CreatedAtUtc,
                DepartmentName(holderDepartmentId, ticket), type, start, target, basis, due, actual, eventAt,
                period?.PriorityId, period is null ? null : priorityNames.GetValueOrDefault(period.PriorityId),
                period?.RequestTypeSlaPolicyId, rt.Name, period?.RequestTypeSlaNote,
                type == SlaDeadlineType.Resolution ? pause.Minutes : 0,
                type == SlaDeadlineType.Resolution && pause.Open);
        }

        var rows = new List<TeamPerformanceSlaEvaluationRow>();

        // Breached: one row per breach event, on the SLA period whose deadline it was.
        foreach (var e in events.Breaches)
        {
            instancesByTicket.TryGetValue(e.TicketId, out var periods);
            rows.Add(Build(TeamPerformanceSlaEvaluationKind.Breached, tickets[e.TicketId], e.HolderDepartmentId, e.SlaType,
                Covering(periods, e.AtUtc), periods, e.AtUtc));
        }

        // Met: on-time First Response (the latest period carries it) and Resolution (the period it was recorded in).
        foreach (var e in events.Met)
        {
            instancesByTicket.TryGetValue(e.TicketId, out var periods);
            var period = periods?.FirstOrDefault(i => i.TicketSlaInstanceId == e.PeriodId);
            rows.Add(Build(TeamPerformanceSlaEvaluationKind.Met, tickets[e.TicketId], e.HolderDepartmentId, e.SlaType, period, periods, e.AtUtc));
        }

        // Pending: open clocks of the tickets held now.
        foreach (var ticketId in openTicketIds)
        {
            if (!instancesByTicket.TryGetValue(ticketId, out var periods) || periods.Count == 0)
            {
                continue;
            }

            var ticket = tickets[ticketId];
            var current = periods[^1];
            if (current.PeriodEndAtUtc is not null || current.PeriodStartAtUtc >= rangeEndUtc)
            {
                continue;
            }

            if (ticket.FirstHumanResponseAtUtc is null && !current.FirstResponseBreached)
            {
                rows.Add(Build(TeamPerformanceSlaEvaluationKind.Pending, ticket, null, SlaDeadlineType.FirstResponse, current, periods, null));
            }

            if (!current.ResolutionBreached && ResolutionIn(ticketId, current) is null)
            {
                rows.Add(Build(TeamPerformanceSlaEvaluationKind.Pending, ticket, null, SlaDeadlineType.Resolution, current, periods, null));
            }
        }

        return rows;
    }

    public async Task<TeamPerformanceSlaConfiguration> GetSlaConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var policies = await dbContext.SlaPolicies.AsNoTracking()
            .Join(dbContext.Priorities.AsNoTracking(), p => p.PriorityId, pr => pr.PriorityId,
                (p, pr) => new { p.PriorityId, pr.Name, pr.DisplayOrder, p.FirstResponseTargetMinutes, p.ResolutionTargetMinutes, p.ClockBasis, p.IsActive })
            .ToListAsync(cancellationToken);

        var calendar = await dbContext.BusinessCalendars.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.EffectiveFromUtc)
            .FirstOrDefaultAsync(cancellationToken);

        TeamPerformanceSlaCalendarRow? calendarRow = null;
        if (calendar is not null)
        {
            var workingDays = await dbContext.BusinessCalendarWorkingDays.AsNoTracking()
                .Where(d => d.BusinessCalendarId == calendar.BusinessCalendarId && d.IsWorkingDay)
                .Select(d => d.DayOfWeek)
                .ToListAsync(cancellationToken);
            var holidays = await dbContext.Holidays.AsNoTracking()
                .CountAsync(h => h.BusinessCalendarId == calendar.BusinessCalendarId, cancellationToken);

            calendarRow = new TeamPerformanceSlaCalendarRow(
                calendar.Name, calendar.TimeZone, calendar.BusinessDayStartLocal, calendar.BusinessDayEndLocal,
                workingDays.Select(d => (DayOfWeek)d).OrderBy(d => ((int)d + 6) % 7).ToList(), holidays);
        }

        return new TeamPerformanceSlaConfiguration(
            policies.OrderBy(p => p.DisplayOrder)
                .Select(p => new TeamPerformanceSlaPolicyRow(p.PriorityId, p.Name, p.FirstResponseTargetMinutes, p.ResolutionTargetMinutes, p.ClockBasis, p.IsActive))
                .ToList(),
            calendarRow);
    }
}
