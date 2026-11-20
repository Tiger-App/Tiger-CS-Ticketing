using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Reporting.Abstractions;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Common;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.Reporting.Services;

/// <summary>
/// The CS Manager's Team Performance report: one row per active employee
/// with four counts, the records behind any one count, and the SLA details
/// behind the SLA Breaches count.
///
/// <para>
/// <b>Who is on the report.</b> Every ACTIVE employee in every department
/// (Customer Service, Call Center, Collections, ... — whatever is configured
/// in Identity), except a configured service identity (the Genesys
/// integration account), which is not a person to measure. Departments and
/// roles are read from the database, never listed here. One row per
/// employee however many roles or departments they hold; employees with no
/// activity in the period are included with zeros.
/// </para>
///
/// <para>
/// <b>Period.</b> UAE calendar days, inclusive, defaulting to the last 30
/// days exactly as the Dashboard does
/// (<see cref="DashboardAppService.ResolvePeriod"/>). Currently Assigned is
/// the one current-state figure and ignores the period.
/// </para>
///
/// <para>
/// <b>SLA details.</b> The report only makes the existing SLA behaviour
/// visible: it computes no deadline and decides no breach. Breaches are the
/// ones the SLA engine recorded; this service lists them, with the facts the
/// engine decided them from, and the SLA evaluations that were met or are
/// still pending, so the number on the report can be audited.
/// </para>
///
/// <para>
/// Read-only: nothing here changes state, so no audit entry is written.
/// Authorization (CS Manager / General Manager / Chairman-CEO, plus the
/// System Administrator override) is the Api's policy, not decided here.
/// </para>
/// </summary>
public sealed class TeamPerformanceAppService(
    ITeamPerformanceQueryRepository repository,
    TimeProvider timeProvider,
    IServiceIdentityRegistry? serviceIdentities = null,
    IGenesysOfferHistory? offerHistory = null,
    IEmployeeShiftHistory? shiftHistory = null)
{
    /// <summary>
    /// The Call Center department's code — the same code
    /// <c>WorkflowReferenceData.CallCenterCode</c> seeds. Not used by the
    /// report itself any more (it lists every department alike); kept here
    /// because the routing and Genesys services reference it.
    /// </summary>
    public const string CallCenterDepartmentCode = "CC";

    public async Task<TeamPerformanceReportDto> GetReportAsync(
        TeamPerformanceRequestDto request, CancellationToken cancellationToken = default)
    {
        var today = UaeCalendar.Today(timeProvider);
        var (dateFrom, dateTo) = DashboardAppService.ResolvePeriod(request.DateFrom, request.DateTo, today);

        var everyone = await LoadReportedEmployeesAsync(cancellationToken);
        var roleOptions = RoleOptions(everyone);
        var role = NormalizeRole(request.Role, roleOptions);

        // Filters narrow the rows; the picker options always offer everyone,
        // every configured department and every role, so a manager can move
        // between filters freely.
        var selected = ApplyFilters(everyone, request.EmployeeId, role, request.DepartmentId).ToList();

        var metrics = selected.Count == 0
            ? EmptyMetrics
            : await repository.GetMetricsAsync(
                selected.Select(e => e.EmployeeId).ToList(),
                RangeStartUtc(dateFrom),
                RangeEndUtc(dateTo),
                cancellationToken);

        var slaEvents = selected.Count == 0
            ? new TeamPerformanceSlaEvents([], [])
            : await repository.GetSlaEventsAsync(
                selected.Select(e => e.EmployeeId).ToList(), RangeStartUtc(dateFrom), RangeEndUtc(dateTo), cancellationToken);
        var slaByEmployee = slaEvents.Breaches.Select(e => (e.EmployeeId, Fact: new SlaOutcomeFact(e.TicketId, e.SlaType, true)))
            .Concat(slaEvents.Met.Select(e => (e.EmployeeId, Fact: new SlaOutcomeFact(e.TicketId, e.SlaType, false))))
            .ToLookup(x => x.EmployeeId, x => x.Fact);

        var rows = selected.Select(e =>
        {
            var sla = SlaCounts.Of(slaByEmployee[e.EmployeeId]);
            return new TeamPerformanceRowDto(
                e.EmployeeId,
                e.DisplayName,
                e.Roles,
                e.Departments.Select(d => new TeamPerformanceDepartmentDto(d.DepartmentId, d.Name, d.IsPrimary)).ToList(),
                PrimaryDepartmentOf(e),
                metrics.CurrentlyAssigned.GetValueOrDefault(e.EmployeeId),
                metrics.TicketsWorked.GetValueOrDefault(e.EmployeeId),
                metrics.CompletedFollowUps.GetValueOrDefault(e.EmployeeId),
                sla.TicketsWithBreach, sla.BreachEvents, sla.Evaluated, sla.Met, sla.CompliancePercent);
        }).ToList();

        // The totals' SLA figures come from the same aggregation over every shown employee's cycles together: distinct tickets are distinct
        // across employees, and compliance is the combined Met over the combined Evaluated.
        var overall = SlaCounts.Of(slaByEmployee.SelectMany(g => g));

        return new TeamPerformanceReportDto(
            new TeamPerformanceAppliedFiltersDto(dateFrom, dateTo, request.EmployeeId, role, request.DepartmentId),
            rows,
            new TeamPerformanceTotalsDto(
                rows.Count,
                rows.Sum(r => r.CurrentlyAssigned),
                rows.Sum(r => r.TicketsWorked),
                rows.Sum(r => r.CompletedFollowUps),
                overall.TicketsWithBreach, overall.BreachEvents, overall.Evaluated, overall.Met, overall.CompliancePercent),
            new TeamPerformanceFilterOptionsDto(
                everyone.Select(e => new TeamPerformanceOptionDto(e.EmployeeId.ToString(), e.DisplayName, RolesText(e.Roles))).ToList(),
                await repository.GetDepartmentsAsync(cancellationToken),
                roleOptions));
    }

    public async Task<TeamPerformanceRecordsResult> GetRecordsAsync(
        TeamPerformanceRecordsRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<TeamPerformanceMetric>(request.Metric, ignoreCase: true, out var metric) || !Enum.IsDefined(metric))
        {
            return new TeamPerformanceRecordsResult(TeamPerformanceRecordsOutcome.UnknownMetric);
        }

        // The same eligibility rule as the report: a count can only be
        // opened for an employee who has a row.
        var employee = (await LoadReportedEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.EmployeeId == request.EmployeeId);
        if (employee is null)
        {
            return new TeamPerformanceRecordsResult(TeamPerformanceRecordsOutcome.EmployeeNotEligible);
        }

        var today = UaeCalendar.Today(timeProvider);
        var (dateFrom, dateTo) = DashboardAppService.ResolvePeriod(request.DateFrom, request.DateTo, today);

        var rows = await repository.GetRecordsAsync(
            employee.EmployeeId, metric, RangeStartUtc(dateFrom), RangeEndUtc(dateTo), cancellationToken);

        return new TeamPerformanceRecordsResult(
            TeamPerformanceRecordsOutcome.Success,
            new TeamPerformanceRecordsDto(
                employee.EmployeeId,
                employee.DisplayName,
                employee.Roles,
                metric.ToString(),
                dateFrom,
                dateTo,
                rows.Select(r => new TeamPerformanceRecordDto(
                    r.TicketId, r.TicketNumber, r.RequestSummary, r.TicketStatus.ToString(), r.PriorityId,
                    r.DepartmentName, r.CreatedAtUtc, r.BreachedAtUtc, r.CompletedAtUtc)).ToList()));
    }

    /// <summary>
    /// The SLA evaluations behind one employee's numbers: a summary of every
    /// status, the rules in force, and one sorted page of evaluations. The
    /// employee must have a row under the same role / department filters
    /// as the report, so the details can never be opened for someone the
    /// report would not show.
    /// </summary>
    public async Task<TeamPerformanceSlaDetailsResult> GetSlaDetailsAsync(
        TeamPerformanceSlaDetailsRequestDto request, CancellationToken cancellationToken = default)
    {
        var everyone = await LoadReportedEmployeesAsync(cancellationToken);
        var role = NormalizeRole(request.Role, RoleOptions(everyone));
        var employee = ApplyFilters(everyone, request.EmployeeId, role, request.DepartmentId).FirstOrDefault();
        if (employee is null)
        {
            return new TeamPerformanceSlaDetailsResult(TeamPerformanceSlaDetailsOutcome.EmployeeNotEligible);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var today = UaeCalendar.Today(nowUtc);
        var (dateFrom, dateTo) = DashboardAppService.ResolvePeriod(request.DateFrom, request.DateTo, today);

        var evaluations = (await repository.GetSlaEvaluationsAsync(
                employee.EmployeeId, RangeStartUtc(dateFrom), RangeEndUtc(dateTo), cancellationToken))
            .Select(e => TeamPerformanceSlaPresentation.ToRecord(e, employee, nowUtc))
            .ToList();

        // Same aggregation as the table row (SlaCounts.Of); only Breached and Met are finalized cycles, Pending is shown beside them.
        var counts = SlaCounts.Of(evaluations
            .Where(r => r.Status != TeamPerformanceSlaStatuses.Pending)
            .Select(r => new SlaOutcomeFact(r.TicketId, r.SlaType == "First Response" ? SlaDeadlineType.FirstResponse : SlaDeadlineType.Resolution,
                r.Status == TeamPerformanceSlaStatuses.Breached)));
        var summary = new TeamPerformanceSlaSummaryDto(
            counts.Evaluated, counts.Met, counts.BreachEvents, counts.TicketsWithBreach, counts.FirstResponseBreaches, counts.ResolutionBreaches,
            evaluations.Count(r => r.Status == TeamPerformanceSlaStatuses.Pending), counts.CompliancePercent);

        // Per-clock breakdown: the same SlaCounts aggregation over each clock's finalized cycles, so the rows add up to the summary above.
        var typeBreakdown = TeamPerformanceSlaTypes.All.Select(type =>
        {
            var ofType = evaluations.Where(r => r.SlaType == type).ToList();
            var typeCounts = SlaCounts.Of(ofType
                .Where(r => r.Status != TeamPerformanceSlaStatuses.Pending)
                .Select(r => new SlaOutcomeFact(r.TicketId, type == TeamPerformanceSlaTypes.FirstResponse ? SlaDeadlineType.FirstResponse : SlaDeadlineType.Resolution,
                    r.Status == TeamPerformanceSlaStatuses.Breached)));
            return new TeamPerformanceSlaTypeBreakdownDto(
                type, typeCounts.Evaluated, typeCounts.Met, typeCounts.BreachEvents,
                ofType.Count(r => r.Status == TeamPerformanceSlaStatuses.Pending), typeCounts.CompliancePercent);
        }).ToList();

        // The SLA Type filter narrows the list and the status tab counts; the summary and breakdown above stay whole.
        var typeFilter = TeamPerformanceSlaTypes.Normalize(request.SlaType);
        var ofTypeFilter = typeFilter is null ? evaluations : evaluations.Where(r => r.SlaType == typeFilter).ToList();
        var statusCounts = new TeamPerformanceSlaStatusCountsDto(
            ofTypeFilter.Count,
            ofTypeFilter.Count(r => r.Status == TeamPerformanceSlaStatuses.Breached),
            ofTypeFilter.Count(r => r.Status == TeamPerformanceSlaStatuses.Met),
            ofTypeFilter.Count(r => r.Status == TeamPerformanceSlaStatuses.Pending));

        var statusFilter = TeamPerformanceSlaStatuses.Normalize(request.Status);
        var filtered = statusFilter is null
            ? ofTypeFilter
            : ofTypeFilter.Where(r => r.Status == statusFilter).ToList();

        var sortBy = TeamPerformanceSlaSortKeys.Normalize(request.SortBy);
        var descending = !string.Equals(request.SortDir?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);
        var sorted = TeamPerformanceSlaPresentation.Sort(filtered, sortBy, descending);

        var pageSize = Math.Clamp(request.PageSize <= 0 ? DefaultPageSize : request.PageSize, 1, MaxPageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(sorted.Count / (double)pageSize));
        var page = Math.Clamp(request.Page, 1, totalPages);

        var configuration = await repository.GetSlaConfigurationAsync(cancellationToken);

        // Employee response time is a SEPARATE measurement from the ticket SLA above: nothing computed here feeds back into `evaluations`,
        // `summary`, the counts or the ticket's recorded SLA state.
        var employeeResponses = await BuildEmployeeResponsesAsync(
            employee.EmployeeId, RangeStartUtc(dateFrom), RangeEndUtc(dateTo), cancellationToken);

        return new TeamPerformanceSlaDetailsResult(
            TeamPerformanceSlaDetailsOutcome.Success,
            new TeamPerformanceSlaDetailsDto(
                employee.EmployeeId,
                employee.DisplayName,
                employee.Roles,
                employee.Departments.Select(d => new TeamPerformanceDepartmentDto(d.DepartmentId, d.Name, d.IsPrimary)).ToList(),
                dateFrom,
                dateTo,
                statusFilter,
                sortBy,
                descending ? "desc" : "asc",
                page,
                pageSize,
                sorted.Count,
                totalPages,
                summary,
                TeamPerformanceSlaPresentation.BuildRules(configuration),
                sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                typeFilter,
                typeBreakdown,
                statusCounts,
                EmployeeResponseSummaryOf(employeeResponses),
                employeeResponses));
    }

    /// <summary>
    /// Every Genesys call offer received by THIS employee in the period, classified by <see cref="EmployeeOfferClassifier"/>, plus calls they
    /// are recorded as having connected to for which no offer event exists (shown as Availability Unknown: their answer time is real, but
    /// without the offer there is nothing to measure it from). An offer to another agent on the same ticket never appears here, and another
    /// agent's answer is never attributed to this employee.
    /// </summary>
    private async Task<IReadOnlyList<EmployeeOfferResponseDto>> BuildEmployeeResponsesAsync(
        Guid employeeId, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken)
    {
        var offers = await (offerHistory ?? NoOfferHistory).GetOffersAsync(employeeId, rangeStartUtc, rangeEndUtc, cancellationToken);
        var connected = await repository.GetConnectedCallsAsync(employeeId, rangeStartUtc, rangeEndUtc, cancellationToken);
        var shifts = shiftHistory ?? NoShiftHistory;

        var offeredTickets = offers.Select(o => o.TicketId).ToHashSet();
        var withoutOffer = connected.Where(c => !offeredTickets.Contains(c.TicketId)).ToList();

        var numbers = await repository.GetTicketNumbersAsync(
            offers.Select(o => o.TicketId).Concat(withoutOffer.Select(c => c.TicketId)).Distinct().ToList(), cancellationToken);
        string NumberOf(long ticketId) => numbers.TryGetValue(ticketId, out var n) ? n : $"#{ticketId}";

        var result = new List<EmployeeOfferResponseDto>();
        foreach (var offer in offers)
        {
            var shift = offer.OfferedAtUtc is { } at
                ? await shifts.GetVerdictAsync(employeeId, at, cancellationToken)
                : ShiftVerdict.Unknown;
            var verdict = EmployeeOfferClassifier.Classify(offer, shift);
            var counts = verdict.Outcome is EmployeeOfferOutcomes.Answered or EmployeeOfferOutcomes.NotResponding;
            result.Add(new EmployeeOfferResponseDto(
                offer.TicketId, NumberOf(offer.TicketId), offer.OfferedAtUtc,
                verdict.Outcome == EmployeeOfferOutcomes.Answered ? offer.AnsweredAtUtc : null,
                verdict.ResponseSeconds, FormatSeconds(verdict.ResponseSeconds), verdict.Outcome, verdict.Explanation, counts));
        }

        foreach (var call in withoutOffer)
        {
            var verdict = EmployeeOfferClassifier.Classify(
                new GenesysOfferRecord(call.TicketId, null, null, GenesysOfferOutcome.Answered, call.ConnectedAtUtc), ShiftVerdict.Unknown);
            result.Add(new EmployeeOfferResponseDto(
                call.TicketId, NumberOf(call.TicketId), null, call.ConnectedAtUtc, null, "—", verdict.Outcome, verdict.Explanation, false));
        }

        return result
            .OrderByDescending(r => r.OfferedAtUtc ?? r.AnsweredAtUtc)
            .ThenByDescending(r => r.TicketId)
            .ToList();
    }

    private static EmployeeResponseSummaryDto EmployeeResponseSummaryOf(IReadOnlyList<EmployeeOfferResponseDto> rows)
    {
        var answered = rows.Where(r => r.Outcome == EmployeeOfferOutcomes.Answered && r.ResponseSeconds is not null).ToList();
        double? average = answered.Count == 0 ? null : answered.Average(r => r.ResponseSeconds!.Value);
        return new EmployeeResponseSummaryDto(
            rows.Count,
            answered.Count,
            rows.Count(r => r.Outcome == EmployeeOfferOutcomes.NotResponding),
            rows.Count(r => r.Outcome == EmployeeOfferOutcomes.OutsideShift),
            rows.Count(r => r.Outcome == EmployeeOfferOutcomes.AvailabilityUnknown),
            average,
            FormatSeconds(average),
            rows.Count(r => r.Outcome == EmployeeOfferOutcomes.Declined),
            rows.Count(r => r.Outcome == EmployeeOfferOutcomes.Redirected),
            rows.Count(r => r.Outcome == EmployeeOfferOutcomes.CallerAbandoned));
    }

    /// <summary>"12 s", "1 m 5 s", "1 h 2 m 3 s"; "—" for nothing. Whole seconds, rounded.</summary>
    public static string FormatSeconds(double? seconds)
    {
        if (seconds is null)
        {
            return "—";
        }

        var total = (long)Math.Round(seconds.Value, MidpointRounding.AwayFromZero);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add($"{h} h");
        if (h > 0 || m > 0) parts.Add($"{m} m");
        parts.Add($"{s} s");
        return string.Join(' ', parts);
    }

    private static readonly IGenesysOfferHistory NoOfferHistory = new NoGenesysOfferHistory();
    private static readonly IEmployeeShiftHistory NoShiftHistory = new NoEmployeeShiftHistory();

    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>Met / Evaluated × 100 where Evaluated = Met + breach events, rounded to one decimal; null when nothing was evaluated. Pending is not evaluated.</summary>
    public static decimal? CompliancePercent(int met, int breachEvents) =>
        met + breachEvents == 0 ? null : Math.Round(met * 100m / (met + breachEvents), 1, MidpointRounding.AwayFromZero);

    // ---- employees and filters ----

    /// <summary>Every active employee except a configured service identity, sorted by name.</summary>
    private async Task<List<TeamPerformanceEmployeeRow>> LoadReportedEmployeesAsync(CancellationToken cancellationToken) =>
        (await repository.GetActiveEmployeesAsync(cancellationToken))
            .Where(e => serviceIdentities is null || !serviceIdentities.IsServiceIdentity(e.EmployeeId))
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.EmployeeId)
            .ToList();

    private static IEnumerable<TeamPerformanceEmployeeRow> ApplyFilters(
        IEnumerable<TeamPerformanceEmployeeRow> employees, Guid? employeeId, string? role, int? departmentId) =>
        employees
            .Where(e => employeeId is not { } id || e.EmployeeId == id)
            .Where(e => role is null || e.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
            .Where(e => departmentId is not { } department || PrimaryDepartmentOf(e)?.DepartmentId == department);

    /// <summary>
    /// The employee's Primary Department: the membership flagged <c>IsPrimary</c> in <c>UserDepartmentAssignments</c> (at most one per employee,
    /// enforced by a unique filtered index). Null when none is configured — never the first of the other memberships. The row's Department
    /// column and the Department filter both use this, so an employee with access to several departments is listed once, under their primary.
    /// </summary>
    internal static TeamPerformanceDepartmentDto? PrimaryDepartmentOf(TeamPerformanceEmployeeRow employee) =>
        employee.Departments.Where(d => d.IsPrimary)
            .Select(d => new TeamPerformanceDepartmentDto(d.DepartmentId, d.Name, true))
            .FirstOrDefault();

    private static List<string> RoleOptions(IEnumerable<TeamPerformanceEmployeeRow> employees) =>
        employees.SelectMany(e => e.Roles).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The canonical spelling of a known role, or null when the value is empty or not a role anyone holds.</summary>
    private static string? NormalizeRole(string? value, IReadOnlyList<string> options) =>
        options.FirstOrDefault(r => string.Equals(r, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string RolesText(IReadOnlyList<string> roles) => roles.Count == 0 ? "No role" : string.Join(", ", roles);

    /// <summary>The inclusive start of the first period day (00:00 UAE), as a UTC instant.</summary>
    public static DateTime RangeStartUtc(DateOnly dateFrom) => UaeCalendar.DayStartUtc(dateFrom);

    /// <summary>The exclusive end of the last period day: 00:00 UAE of the day after, as a UTC instant.</summary>
    public static DateTime RangeEndUtc(DateOnly dateTo) => UaeCalendar.DayEndUtc(dateTo);

    private static readonly TeamPerformanceMetricsSnapshot EmptyMetrics = new(
        new Dictionary<Guid, int>(), new Dictionary<Guid, int>(), new Dictionary<Guid, int>());
}
