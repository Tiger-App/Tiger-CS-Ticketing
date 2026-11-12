namespace TigerCS.Application.Modules.Reporting.Dto;

// ---- Team Performance report (CS Manager): GET /api/reports/team-performance ----

/// <summary>
/// The Team Performance report's filters, bound from the query string.
/// Every filter is optional. Dates are UTC calendar days (the same "UTC
/// day" convention the Dashboard uses) and default to the last 30 days
/// when both are omitted.
/// </summary>
/// <param name="DateFrom">First UTC calendar day of the period (inclusive).</param>
/// <param name="DateTo">Last UTC calendar day of the period (inclusive).</param>
/// <param name="EmployeeId">Narrow to one active employee.</param>
/// <param name="Role">Narrow to employees holding this Identity role (case-insensitive; an unknown role is ignored).</param>
/// <param name="DepartmentId">Narrow to employees who are members of this department.</param>
public sealed record TeamPerformanceRequestDto(
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    Guid? EmployeeId = null,
    string? Role = null,
    int? DepartmentId = null);

/// <summary>
/// The Team Performance report: one row per active employee in every
/// department (Customer Service, Call Center, Collections, ...), sorted by
/// display name, with the four metrics and a totals row. Employees with no
/// activity in the period are included with zeros — a manager sees the whole
/// organisation, never only the busy part of it.
/// </summary>
/// <param name="Filters">The filters as actually applied — including the defaulted period.</param>
/// <param name="Rows">One row per employee, sorted by display name; never a duplicate row for an employee with several roles or departments.</param>
/// <param name="Totals">The sum of every row shown (after filters).</param>
/// <param name="FilterOptions">The picker options: every active employee, every configured department, and every role an employee holds.</param>
public sealed record TeamPerformanceReportDto(
    TeamPerformanceAppliedFiltersDto Filters,
    IReadOnlyList<TeamPerformanceRowDto> Rows,
    TeamPerformanceTotalsDto Totals,
    TeamPerformanceFilterOptionsDto FilterOptions);

/// <summary>The filters as applied, echoed back so the page can render the effective state (e.g. the defaulted period).</summary>
public sealed record TeamPerformanceAppliedFiltersDto(
    DateOnly DateFrom,
    DateOnly DateTo,
    Guid? EmployeeId,
    string? Role,
    int? DepartmentId);

/// <summary>
/// One employee's row. The metric definitions are the business rules of
/// this report and are restated on the page's help block and in
/// <c>docs/Team-Performance-Report.md</c>.
/// </summary>
/// <param name="EmployeeId">The employee (AspNetUsers.Id / Employees.EmployeeId).</param>
/// <param name="DisplayName">The employee's display name.</param>
/// <param name="Roles">The Identity roles the employee holds, alphabetical (empty when none).</param>
/// <param name="Departments">The employee's department memberships (access), primary first. Unchanged; the report's Department column and filter use <paramref name="PrimaryDepartment"/>.</param>
/// <param name="PrimaryDepartment">The department flagged <c>UserDepartmentAssignments.IsPrimary</c> for this employee; null when none is configured. Never inferred from the other memberships.</param>
/// <param name="CurrentlyAssigned">Current state, not period-bound: tickets whose <c>CurrentOwnerEmployeeId</c> is this employee and whose status is not Closed.</param>
/// <param name="TicketsWorked">Distinct tickets on which the employee performed at least one recorded action within the period (assignment to them, a status change, a note, a resolution, a workflow event, or a customer follow-up assigned to or completed by them). Measured from history, not from current ownership.</param>
/// <param name="CompletedFollowUps">Customer follow-ups (<c>TicketAgentHandoffs</c>) assigned to the employee and completed within the period. Counts work items, not tickets.</param>
/// <param name="TicketsWithSlaBreach">Distinct tickets with at least one genuine SLA breach event (First Response or Resolution) in the period, attributed to this employee. A ticket whose two clocks both breached counts once here and twice in <paramref name="SlaBreachEvents"/>.</param>
/// <param name="SlaBreachEvents">Genuine SLA breach events in the period attributed to this employee: one per (ticket, clock, deadline). "First human response recorded" rows on an already-Breached ticket are not breaches and are not counted. A breach belongs to whoever held the ticket at the breach deadline, not at the moment the scheduler recorded it.</param>
/// <param name="SlaEvaluated">Finalized SLA clock cycles: <paramref name="SlaMet"/> + <paramref name="SlaBreachEvents"/>. Pending and not-applicable clocks are excluded.</param>
/// <param name="SlaMet">Clock cycles achieved on time in the period (first human response, or resolution) by this employee.</param>
/// <param name="SlaCompliancePercent">Met / Evaluated × 100 (one decimal); null when nothing was evaluated.</param>
public sealed record TeamPerformanceRowDto(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<TeamPerformanceDepartmentDto> Departments,
    TeamPerformanceDepartmentDto? PrimaryDepartment,
    int CurrentlyAssigned,
    int TicketsWorked,
    int CompletedFollowUps,
    int TicketsWithSlaBreach,
    int SlaBreachEvents,
    int SlaEvaluated,
    int SlaMet,
    decimal? SlaCompliancePercent);

/// <summary>One department membership of an employee on the report.</summary>
public sealed record TeamPerformanceDepartmentDto(int DepartmentId, string Name, bool IsPrimary);

/// <summary>
/// The totals row: every figure is the sum of the rows shown, with two exceptions that are not sums. Tickets worked is summed per employee,
/// so a ticket two agents both worked counts twice — the team's effort, not a ticket count. <see cref="TicketsWithSlaBreach"/> is the number of
/// DISTINCT tickets across the shown employees (a ticket whose First Response breached under one employee and Resolution under another is one
/// ticket). <see cref="SlaCompliancePercent"/> is computed from the combined Met and Evaluated, never an average of the rows' percentages.
/// </summary>
public sealed record TeamPerformanceTotalsDto(
    int Employees,
    int CurrentlyAssigned,
    int TicketsWorked,
    int CompletedFollowUps,
    int TicketsWithSlaBreach,
    int SlaBreachEvents,
    int SlaEvaluated,
    int SlaMet,
    decimal? SlaCompliancePercent);

/// <summary>A filter picker option; <see cref="GroupLabel"/> carries the employee's roles on the employee picker.</summary>
public sealed record TeamPerformanceOptionDto(string Id, string Label, string? GroupLabel = null);

/// <summary>The picker options — every active employee (not only the filtered ones), every configured (active) department, and every role held by at least one of those employees.</summary>
public sealed record TeamPerformanceFilterOptionsDto(
    IReadOnlyList<TeamPerformanceOptionDto> Employees,
    IReadOnlyList<TeamPerformanceOptionDto> Departments,
    IReadOnlyList<string> Roles);

// ---- Records behind one count: GET /api/reports/team-performance/records ----

/// <summary>Which of the four counts a records request opens.</summary>
public enum TeamPerformanceMetric
{
    CurrentlyAssigned,
    TicketsWorked,
    CompletedFollowUps,
    SlaBreaches
}

/// <summary>
/// The records behind one count on the report, for one employee. The
/// period is interpreted exactly as on the report (UTC days, inclusive,
/// defaulted the same way), so the list always matches the number.
/// </summary>
/// <param name="EmployeeId">The employee whose count is being opened.</param>
/// <param name="Metric">One of CurrentlyAssigned, TicketsWorked, CompletedFollowUps, SlaBreaches.</param>
/// <param name="DateFrom">First UTC calendar day of the period (inclusive); ignored for CurrentlyAssigned.</param>
/// <param name="DateTo">Last UTC calendar day of the period (inclusive); ignored for CurrentlyAssigned.</param>
public sealed record TeamPerformanceRecordsRequestDto(
    Guid EmployeeId,
    string Metric,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null);

/// <summary>The records behind one count — the employee, the metric and the period they were computed for, and the ticket rows.</summary>
public sealed record TeamPerformanceRecordsDto(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string Metric,
    DateOnly DateFrom,
    DateOnly DateTo,
    IReadOnlyList<TeamPerformanceRecordDto> Records);

/// <summary>One record: the ticket's compact facts plus, for SLA breaches, when the breach was recorded and, for completed follow-ups, when the follow-up was completed.</summary>
/// <param name="TicketId">Links to Ticket Details.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="RequestSummary">The ticket's one-line summary.</param>
/// <param name="TicketStatus">The ticket's current lifecycle status.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low, or null while Unclassified.</param>
/// <param name="DepartmentName">The department that currently holds the ticket.</param>
/// <param name="CreatedAtUtc">When the ticket was created.</param>
/// <param name="BreachedAtUtc">SlaBreaches only: when the breach was recorded (the earliest breach in the period for this ticket).</param>
/// <param name="CompletedAtUtc">CompletedFollowUps only: when the follow-up was completed.</param>
public sealed record TeamPerformanceRecordDto(
    long TicketId,
    string TicketNumber,
    string RequestSummary,
    string TicketStatus,
    byte? PriorityId,
    string? DepartmentName,
    DateTime CreatedAtUtc,
    DateTime? BreachedAtUtc = null,
    DateTime? CompletedAtUtc = null);

/// <summary>How a records request landed — the controller maps each to a status code; no exception carries a business outcome.</summary>
public enum TeamPerformanceRecordsOutcome
{
    Success,

    /// <summary>The metric name is not one of the four counts.</summary>
    UnknownMetric,

    /// <summary>The employee is not on the report (inactive, a service identity, or excluded by the role / department filter) — there is no row to open.</summary>
    EmployeeNotEligible
}

/// <summary>The records call's result: the outcome and, on success, the records.</summary>
public sealed record TeamPerformanceRecordsResult(TeamPerformanceRecordsOutcome Outcome, TeamPerformanceRecordsDto? Records = null);


// ---- SLA details behind the SLA Breaches count: GET /api/reports/team-performance/sla-details ----

/// <summary>The status of one SLA evaluation.</summary>
public static class TeamPerformanceSlaStatuses
{
    public const string Met = "Met";
    public const string Breached = "Breached";
    public const string Pending = "Pending";

    public static readonly IReadOnlyList<string> All = [Met, Breached, Pending];

    /// <summary>Case-insensitive match to one status, or null when the value is not one (meaning "all").</summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The columns the SLA details list can be sorted by (the <c>sortBy</c> values).</summary>
public static class TeamPerformanceSlaSortKeys
{
    public const string Event = "event";
    public const string Ticket = "ticket";
    public const string Department = "department";
    public const string SlaType = "type";
    public const string Created = "created";
    public const string Started = "started";
    public const string Target = "target";
    public const string Due = "due";
    public const string Actual = "actual";
    public const string Status = "status";
    public const string Breach = "breach";
    public const string Policy = "policy";

    public static readonly IReadOnlyList<string> All =
        [Event, Ticket, Department, SlaType, Created, Started, Target, Due, Actual, Status, Breach, Policy];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(k => string.Equals(k, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Event;
}

/// <summary>
/// The SLA details behind one employee's SLA Breaches number. The period,
/// employee, role and department mean exactly what they mean on the report,
/// and the Breached evaluations are produced by the very predicate the
/// report's count uses, so the two always agree.
/// </summary>
/// <param name="EmployeeId">The employee whose SLA evaluations are listed.</param>
/// <param name="DateFrom">First UTC calendar day of the period (inclusive); defaults like the report.</param>
/// <param name="DateTo">Last UTC calendar day of the period (inclusive).</param>
/// <param name="Role">The report's Role filter, if applied — the employee must satisfy it.</param>
/// <param name="DepartmentId">The report's Department filter, if applied — the employee must satisfy it.</param>
/// <param name="Status">Narrow the list to <c>Met</c>, <c>Breached</c> or <c>Pending</c>; omitted = all. The summary always covers every status.</param>
/// <param name="SortBy">One of <see cref="TeamPerformanceSlaSortKeys.All"/>; default <c>event</c> (breach / achievement time).</param>
/// <param name="SortDir"><c>asc</c> or <c>desc</c> (default <c>desc</c>).</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page (1–100, default 25).</param>
public sealed record TeamPerformanceSlaDetailsRequestDto(
    Guid EmployeeId,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    string? Role = null,
    int? DepartmentId = null,
    string? Status = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>The SLA details page: summary, rules, and one page of evaluations.</summary>
public sealed record TeamPerformanceSlaDetailsDto(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<TeamPerformanceDepartmentDto> Departments,
    DateOnly DateFrom,
    DateOnly DateTo,
    string? StatusFilter,
    string SortBy,
    string SortDir,
    int Page,
    int PageSize,
    int TotalRecords,
    int TotalPages,
    TeamPerformanceSlaSummaryDto Summary,
    TeamPerformanceSlaRulesDto Rules,
    IReadOnlyList<TeamPerformanceSlaRecordDto> Records);

/// <summary>
/// The SLA counts for the employee and period, derived by the same aggregation as the main table's row. Evaluated = Met + BreachEvents
/// (finalized clock cycles); Pending clocks are shown for context but are not evaluated. <see cref="CompliancePercent"/> =
/// Met / Evaluated × 100; null when nothing was evaluated.
/// </summary>
/// <param name="Evaluated">Met + BreachEvents.</param>
/// <param name="Met">Clock cycles achieved on time in the period.</param>
/// <param name="BreachEvents">Genuine breach events = <see cref="FirstResponseBreaches"/> + <see cref="ResolutionBreaches"/>.</param>
/// <param name="TicketsWithBreach">Distinct tickets with at least one breach event.</param>
/// <param name="FirstResponseBreaches">First Response breach events.</param>
/// <param name="ResolutionBreaches">Resolution breach events.</param>
/// <param name="Pending">Open clocks on tickets the employee holds now (not evaluated).</param>
/// <param name="CompliancePercent">Met / Evaluated × 100, one decimal.</param>
public sealed record TeamPerformanceSlaSummaryDto(
    int Evaluated,
    int Met,
    int BreachEvents,
    int TicketsWithBreach,
    int FirstResponseBreaches,
    int ResolutionBreaches,
    int Pending,
    decimal? CompliancePercent)
{
    public const string Formula = "SLA Compliance % = (Met / Evaluated) × 100, where Evaluated = Met + Breach Events. Pending clocks are not evaluated and are excluded.";
}

/// <summary>
/// One SLA evaluation: a single SLA clock cycle of a single ticket attributed
/// to the employee — a breach event, an on-time achievement, or a clock still
/// pending. A ticket whose two clocks both breached is two Breached records
/// (two breach events) but one ticket in "Tickets with SLA breach".
/// </summary>
/// <param name="TicketId">Links to Ticket Details.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="DepartmentName">The department responsible: the one the responsible employee was assigned in at the moment (falling back to the ticket's department).</param>
/// <param name="SlaType"><c>First Response</c> or <c>Resolution</c>.</param>
/// <param name="TicketCreatedAtUtc">When the ticket was created.</param>
/// <param name="SlaStartedAtUtc">When this SLA clock started (the SLA period's clock-start moment).</param>
/// <param name="TargetMinutes">The configured SLA duration in minutes (business minutes when the clock basis is Business hours); null when it was not recorded.</param>
/// <param name="TargetText">The target in readable form, with the clock basis, e.g. "4 h · Business hours".</param>
/// <param name="DueAtUtc">The deadline the system holds for this clock (pauses already added). For a breach it is the moment the breach happened, and who held the ticket then is who it is attributed to.</param>
/// <param name="ActualAtUtc">The relevant action time: the first human response (First Response) or the resolution (Resolution); null when it has not happened.</param>
/// <param name="Status"><c>Met</c>, <c>Breached</c> or <c>Pending</c>.</param>
/// <param name="EventAtUtc">The breach as recorded by the SLA engine, or the on-time achievement; null for Pending.</param>
/// <param name="BreachMinutes">Whole minutes past the due moment (actual − due, or — while still unachieved — now − due); null unless Breached.</param>
/// <param name="BreachText">The same, readable ("3 h 20 m"), with " (ongoing)" when the action has not happened yet.</param>
/// <param name="IsOngoing">Breached with no actual action yet.</param>
/// <param name="AssignedEmployeeId">The employee responsible (the one the report attributes it to).</param>
/// <param name="AssignedEmployeeName">That employee's display name.</param>
/// <param name="Policy">The SLA rule applied, in words: priority, request-type policy when one governed it, targets and clock basis.</param>
/// <param name="Explanation">Why this evaluation is counted under this status and employee.</param>
public sealed record TeamPerformanceSlaRecordDto(
    long TicketId,
    string TicketNumber,
    string? DepartmentName,
    string SlaType,
    DateTime TicketCreatedAtUtc,
    DateTime? SlaStartedAtUtc,
    int? TargetMinutes,
    string TargetText,
    DateTime? DueAtUtc,
    DateTime? ActualAtUtc,
    string Status,
    DateTime? EventAtUtc,
    int? BreachMinutes,
    string? BreachText,
    bool IsOngoing,
    Guid AssignedEmployeeId,
    string AssignedEmployeeName,
    string Policy,
    string Explanation);

/// <summary>The SLA rules in force, read from the system: the explanatory statements and the live configuration they refer to.</summary>
public sealed record TeamPerformanceSlaRulesDto(
    IReadOnlyList<TeamPerformanceSlaRuleDto> Rules,
    IReadOnlyList<TeamPerformanceSlaPolicyDto> Policies,
    TeamPerformanceSlaCalendarDto? Calendar);

/// <summary>One rule statement: a topic and what the system does.</summary>
public sealed record TeamPerformanceSlaRuleDto(string Topic, string Text);

/// <summary>One per-priority SLA policy row as configured (<c>SlaPolicies</c>).</summary>
public sealed record TeamPerformanceSlaPolicyDto(
    string PriorityName,
    int FirstResponseTargetMinutes,
    int ResolutionTargetMinutes,
    string ClockBasis,
    bool IsActive);

/// <summary>The active business calendar (<c>BusinessCalendars</c> / <c>Holidays</c>).</summary>
public sealed record TeamPerformanceSlaCalendarDto(
    string Name,
    string TimeZone,
    string BusinessDayStartLocal,
    string BusinessDayEndLocal,
    IReadOnlyList<string> WorkingDays,
    int HolidayCount);

/// <summary>How an SLA details request landed.</summary>
public enum TeamPerformanceSlaDetailsOutcome
{
    Success,

    /// <summary>The employee is not on the report (inactive, a service identity, or excluded by the role / department filter).</summary>
    EmployeeNotEligible
}

/// <summary>The SLA details call's result.</summary>
public sealed record TeamPerformanceSlaDetailsResult(TeamPerformanceSlaDetailsOutcome Outcome, TeamPerformanceSlaDetailsDto? Details = null);
