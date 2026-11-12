using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Reporting.Abstractions;

/// <summary>
/// The Team Performance report's reads. Every count is a set-based
/// aggregate computed in the database over the employee set the
/// application service hands in; the repository never decides who is
/// reported — it answers "who is active, in which departments and roles" and
/// "what did these employees do in this window".
/// </summary>
public interface ITeamPerformanceQueryRepository
{
    /// <summary>
    /// Every ACTIVE employee (<c>Employees.DeactivatedAtUtc IS NULL</c>) in
    /// every department, with their Identity roles (<c>AspNetUserRoles</c>)
    /// and department memberships. One entry per employee however many
    /// roles or departments they hold. Nothing about departments or roles
    /// is hardcoded: both come from the tables.
    /// </summary>
    Task<IReadOnlyList<TeamPerformanceEmployeeRow>> GetActiveEmployeesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every configured, active department (<c>Departments.IsActive</c>), whether or not anyone is currently a member — the Department filter offers all of them.</summary>
    Task<IReadOnlyList<TeamPerformanceOptionDto>> GetDepartmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>The three activity metrics for the given employees over [<paramref name="rangeStartUtc"/>, <paramref name="rangeEndUtc"/>). An employee absent from a dictionary has a count of zero.</summary>
    Task<TeamPerformanceMetricsSnapshot> GetMetricsAsync(
        IReadOnlyCollection<Guid> employeeIds, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default);

    /// <summary>The records behind one employee's count, computed with exactly the predicate the count used.</summary>
    Task<IReadOnlyList<TeamPerformanceRecordRow>> GetRecordsAsync(
        Guid employeeId, TeamPerformanceMetric metric, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// The finalized SLA clock cycles attributed to the given employees in the window: genuine breach events and on-time achievements.
    /// This is the ONE source of every SLA number — the table, the records behind it and the employee details are all derived from it, so
    /// they cannot disagree.
    /// </summary>
    Task<TeamPerformanceSlaEvents> GetSlaEventsAsync(
        IReadOnlyCollection<Guid> employeeIds, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every SLA evaluation attributed to one employee for the window, of
    /// all three statuses: the Breached and Met ones are exactly the
    /// <see cref="GetSlaEventsAsync"/> events, enriched with the facts they were decided from, plus the Pending clocks.
    /// </summary>
    Task<IReadOnlyList<TeamPerformanceSlaEvaluationRow>> GetSlaEvaluationsAsync(
        Guid employeeId, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default);

    /// <summary>The SLA configuration read from the database, for the details view's rules explanation.</summary>
    Task<TeamPerformanceSlaConfiguration> GetSlaConfigurationAsync(CancellationToken cancellationToken = default);
}

/// <summary>One active employee with their roles (alphabetical) and department memberships (primary first).</summary>
public sealed record TeamPerformanceEmployeeRow(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<TeamPerformanceDepartmentRow> Departments);

/// <summary>One department membership: id, display name, the department code (the Call Center department is recognised by its code) and whether it is the primary membership.</summary>
public sealed record TeamPerformanceDepartmentRow(int DepartmentId, string Name, string Code, bool IsPrimary);

/// <summary>The three activity metric counts keyed by employee. Missing key = zero. SLA numbers come from <see cref="TeamPerformanceSlaEvents"/>.</summary>
public sealed record TeamPerformanceMetricsSnapshot(
    IReadOnlyDictionary<Guid, int> CurrentlyAssigned,
    IReadOnlyDictionary<Guid, int> TicketsWorked,
    IReadOnlyDictionary<Guid, int> CompletedFollowUps);

/// <summary>
/// One finalized SLA clock cycle attributed to an employee: a breach event (at the moment it was recorded; <paramref name="DueAtUtc"/> is
/// the deadline) or an on-time achievement (at the moment the action happened).
/// </summary>
/// <param name="EmployeeId">The employee who held the ticket at the breach deadline / at the achievement.</param>
/// <param name="TicketId">The ticket.</param>
/// <param name="SlaType">Which clock.</param>
/// <param name="AtUtc">Breach recorded at, or achieved at.</param>
/// <param name="DueAtUtc">The clock's due time, when its SLA period is known.</param>
/// <param name="HolderDepartmentId">The department of the holder's assignment.</param>
/// <param name="PeriodId">The SLA period the achievement belongs to (Met only).</param>
/// <param name="SourceId">The history / resolution / ticket row it came from.</param>
public sealed record TeamPerformanceSlaEventRow(
    Guid EmployeeId,
    long TicketId,
    SlaDeadlineType SlaType,
    DateTime AtUtc,
    DateTime? DueAtUtc,
    int? HolderDepartmentId,
    long? PeriodId,
    long SourceId);

/// <summary>The breach events and the on-time achievements of a set of employees in a window. Evaluated = Met + Breaches.</summary>
public sealed record TeamPerformanceSlaEvents(
    IReadOnlyList<TeamPerformanceSlaEventRow> Breaches,
    IReadOnlyList<TeamPerformanceSlaEventRow> Met);

/// <summary>One record row with the ticket's facts resolved in-query, plus the metric-specific instant (breach or completion time).</summary>
public sealed record TeamPerformanceRecordRow(
    long TicketId,
    string TicketNumber,
    string RequestSummary,
    TicketStatus TicketStatus,
    byte? PriorityId,
    string? DepartmentName,
    DateTime CreatedAtUtc,
    DateTime? BreachedAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>Whether an evaluation was breached, met, or is still open.</summary>
public enum TeamPerformanceSlaEvaluationKind
{
    Breached,
    Met,
    Pending
}

/// <summary>
/// One SLA evaluation with the facts it was decided from. The repository
/// states what the data says; the application service turns it into
/// words, durations and the status explanation.
/// </summary>
/// <param name="Kind">Breached, Met or Pending.</param>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="TicketStatus">The ticket's current status.</param>
/// <param name="TicketCreatedAtUtc">When the ticket was created.</param>
/// <param name="DepartmentName">The department the responsible employee was assigned in at the moment (the ticket's department for Pending).</param>
/// <param name="SlaType">The clock.</param>
/// <param name="SlaStartedAtUtc">When this clock started (the first period for First Response, which is never restarted; the period in force for Resolution).</param>
/// <param name="TargetMinutes">The applied target for this clock, or the priority policy's when the period predates the snapshot.</param>
/// <param name="ClockBasis">The basis this clock's due moment was computed on, or null when unknown.</param>
/// <param name="DueAtUtc">The deadline the system holds for this clock (pauses already added).</param>
/// <param name="ActualAtUtc">The first human response / the resolution, when it has happened.</param>
/// <param name="EventAtUtc">Breach recorded at (Breached), or achieved at (Met); null for Pending.</param>
/// <param name="PriorityId">The priority of the SLA period, or null when the ticket has none.</param>
/// <param name="PriorityName">That priority's name.</param>
/// <param name="RequestTypeSlaPolicyId">The request-type row that governed the dates, when one did.</param>
/// <param name="RequestTypeName">That row's request type name.</param>
/// <param name="RequestTypeSlaNote">Why a configured request-type SLA was not applied, or null.</param>
/// <param name="PausedMinutes">Resolution only: whole minutes of closed pause periods in the SLA period (already inside the due time).</param>
/// <param name="ClockPausedNow">Resolution only: a pause is open now.</param>
public sealed record TeamPerformanceSlaEvaluationRow(
    TeamPerformanceSlaEvaluationKind Kind,
    long TicketId,
    string TicketNumber,
    TicketStatus TicketStatus,
    DateTime TicketCreatedAtUtc,
    string? DepartmentName,
    SlaDeadlineType SlaType,
    DateTime? SlaStartedAtUtc,
    int? TargetMinutes,
    SlaClockBasis? ClockBasis,
    DateTime? DueAtUtc,
    DateTime? ActualAtUtc,
    DateTime? EventAtUtc,
    byte? PriorityId,
    string? PriorityName,
    int? RequestTypeSlaPolicyId,
    string? RequestTypeName,
    string? RequestTypeSlaNote,
    int PausedMinutes,
    bool ClockPausedNow);

/// <summary>The SLA configuration the explanation refers to.</summary>
public sealed record TeamPerformanceSlaConfiguration(
    IReadOnlyList<TeamPerformanceSlaPolicyRow> Policies,
    TeamPerformanceSlaCalendarRow? Calendar);

/// <summary>One <c>SlaPolicies</c> row with its priority name.</summary>
public sealed record TeamPerformanceSlaPolicyRow(
    byte PriorityId, string PriorityName, int FirstResponseTargetMinutes, int ResolutionTargetMinutes, SlaClockBasis ClockBasis, bool IsActive);

/// <summary>The active <c>BusinessCalendar</c>: window, working days and how many holidays are configured.</summary>
public sealed record TeamPerformanceSlaCalendarRow(
    string Name, string TimeZone, TimeOnly StartLocal, TimeOnly EndLocal, IReadOnlyList<DayOfWeek> WorkingDays, int HolidayCount);
