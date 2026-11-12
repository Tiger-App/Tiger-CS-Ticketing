using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// The SLA details behind the SLA Breaches number, end to end over real SQL
/// (SQLite in-memory): the Breached evaluations equal the report's count for
/// every employee / period / filter, each is attributed to whoever held the
/// ticket when the breach was recorded, Met and Pending evaluations are
/// listed beside them, compliance excludes Pending, and the list sorts and
/// pages on the server. Nothing here changes an SLA rule: breaches are
/// seeded exactly as the SLA engine records them (an SlaState history row
/// "First Response / Resolution SLA breached (due …)").
/// </summary>
public sealed class TeamPerformanceSlaDetailsTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;

    private readonly DashboardSqliteFixture _db = new();
    private readonly Guid _aminaId;
    private readonly Guid _bilalId;
    private readonly Guid _danaId;

    public TeamPerformanceSlaDetailsTests()
    {
        using var context = _db.CreateContext();
        _aminaId = _db.AddEmployee(context, "Amina Agent");
        _bilalId = _db.AddEmployee(context, "Bilal Caller");
        _danaId = _db.AddEmployee(context, "Dana Quiet");

        context.UserDepartmentAssignments.AddRange(
            new UserDepartmentAssignment(_aminaId, _db.CustomerServiceId, true, Now.AddDays(-50), null),
            new UserDepartmentAssignment(_bilalId, _db.CollectionsId, true, Now.AddDays(-50), null));

        context.SlaPolicies.AddRange(
            new SlaPolicy((byte)PriorityLevel.Critical, 15, 240, SlaClockBasis.TwentyFourSeven, 50m, 1, 1),
            new SlaPolicy((byte)PriorityLevel.Medium, 240, 1800, SlaClockBasis.BusinessHours, 75m, 1, 1));
        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private TeamPerformanceAppService CreateService(TigerCsDbContext context) =>
        new(new TeamPerformanceQueryRepository(context), new DashboardSqliteFixture.FixedTimeProvider(Now));

    private async Task<TeamPerformanceReportDto> ReportAsync(TeamPerformanceRequestDto? request = null)
    {
        using var context = _db.CreateContext();
        return await CreateService(context).GetReportAsync(request ?? new TeamPerformanceRequestDto());
    }

    private async Task<TeamPerformanceSlaDetailsDto> DetailsAsync(Guid employeeId, Func<TeamPerformanceSlaDetailsRequestDto, TeamPerformanceSlaDetailsRequestDto>? configure = null)
    {
        var result = await DetailsResultAsync(employeeId, configure);
        Assert.Equal(TeamPerformanceSlaDetailsOutcome.Success, result.Outcome);
        return result.Details!;
    }

    private async Task<TeamPerformanceSlaDetailsResult> DetailsResultAsync(Guid employeeId, Func<TeamPerformanceSlaDetailsRequestDto, TeamPerformanceSlaDetailsRequestDto>? configure = null)
    {
        using var context = _db.CreateContext();
        var request = new TeamPerformanceSlaDetailsRequestDto(employeeId, PageSize: 100);
        return await CreateService(context).GetSlaDetailsAsync(configure is null ? request : configure(request));
    }

    private async Task<TeamPerformanceRecordsDto> RecordsAsync(Guid employeeId, TeamPerformanceMetric metric)
    {
        using var context = _db.CreateContext();
        var result = await CreateService(context).GetRecordsAsync(new TeamPerformanceRecordsRequestDto(employeeId, metric.ToString()));
        return result.Records!;
    }

    private static void Assign(TigerCsDbContext context, Ticket ticket, Guid employeeId, DateTime atUtc, int? departmentId = null)
    {
        foreach (var current in context.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent))
        {
            current.MarkSuperseded();
        }

        context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, employeeId, departmentId ?? ticket.CurrentDepartmentId, atUtc, null));
        context.SaveChanges();
    }

    /// <summary>An SLA period as the engine opens it: clock start at ticket creation, both deadlines, a policy snapshot.</summary>
    private static TicketSlaInstance AddPeriod(
        TigerCsDbContext context, Ticket ticket, DateTime startUtc, DateTime firstResponseDueUtc, DateTime resolutionDueUtc,
        byte priority = (byte)PriorityLevel.Medium, bool firstResponseBreached = false, bool resolutionBreached = false)
    {
        var period = TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, priority, startUtc, firstResponseDueUtc, resolutionDueUtc,
            new AppliedSlaPolicy(SlaClockBasis.BusinessHours, AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 1800));
        if (firstResponseBreached) period.MarkBreached(SlaDeadlineType.FirstResponse);
        if (resolutionBreached) period.MarkBreached(SlaDeadlineType.Resolution);
        context.TicketSlaInstances.Add(period);
        context.SaveChanges();
        return period;
    }

    /// <summary>The history row <c>SlaBreachProcessor</c> writes when it records a breach.</summary>
    private static void RecordBreach(TigerCsDbContext context, Ticket ticket, SlaDeadlineType type, DateTime dueUtc, DateTime atUtc)
    {
        context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Breached,
            actorEmployeeId: null, actorIsSystem: true, note: $"{type} SLA breached (due {dueUtc:O}).", Guid.NewGuid(), atUtc));
        context.SaveChanges();
    }

    private static void RecordFirstResponse(TigerCsDbContext context, Ticket ticket, DateTime atUtc)
    {
        ticket.RecordFirstHumanResponse(atUtc);
        context.SaveChanges();
    }

    /// <summary>Records a resolution the way a reopen leaves things: any earlier resolution is archived (one current resolution per ticket).</summary>
    private static void RecordResolution(TigerCsDbContext context, Ticket ticket, Guid employeeId, DateTime atUtc)
    {
        foreach (var earlier in context.TicketResolutions.Where(r => r.TicketId == ticket.TicketId && r.IsCurrent))
        {
            earlier.Archive();
        }

        context.SaveChanges();
        context.TicketResolutions.Add(new TicketResolution(ticket.TicketId, ResolutionOutcome.Resolved, "Fixed.", null, null, employeeId, atUtc));
        context.SaveChanges();
    }

    /// <summary>A ticket held by <paramref name="holder"/> from <paramref name="assignedAt"/>, with a Medium period whose First Response was due <paramref name="frDueAfterStart"/> after creation.</summary>
    private Ticket NewTicket(TigerCsDbContext context, Guid holder, DateTime createdAt, DateTime? assignedAt = null, int? departmentId = null, TicketStatus status = TicketStatus.Open)
    {
        var ticket = _db.AddTicket(context, departmentId ?? _db.CustomerServiceId, createdAtUtc: createdAt, owner: holder, status: status);
        Assign(context, ticket, holder, assignedAt ?? createdAt.AddMinutes(5));
        return ticket;
    }

    // ---------------------------------------------------------------
    // The Breached evaluations ARE the report's count
    // ---------------------------------------------------------------

    [Fact]
    public async Task BreachedRecords_EqualTheReportCount_AndEachExplainsWhyItWasCounted()
    {
        long reassignedId, bothId;
        using (var context = _db.CreateContext())
        {
            // 1. First Response breach, still unanswered. Held by Amina throughout.
            var a = NewTicket(context, _aminaId, Now.AddDays(-9));
            var aDue = Now.AddDays(-8);
            AddPeriod(context, a, a.CreatedAtUtc, aDue, Now.AddDays(-2), firstResponseBreached: true);
            RecordBreach(context, a, SlaDeadlineType.FirstResponse, aDue, aDue);

            // 2. Resolution breach; Amina held it then, Bilal holds it now (reassigned after).
            var b = NewTicket(context, _aminaId, Now.AddDays(-12));
            reassignedId = b.TicketId;
            var bDue = Now.AddDays(-6);
            AddPeriod(context, b, b.CreatedAtUtc, Now.AddDays(-11), bDue, resolutionBreached: true);
            RecordBreach(context, b, SlaDeadlineType.Resolution, bDue, bDue);
            Assign(context, b, _bilalId, Now.AddDays(-3));

            // 3. Both clocks breached on one ticket: ONE Breached record, the other event noted.
            var c = NewTicket(context, _aminaId, Now.AddDays(-15));
            bothId = c.TicketId;
            var cFrDue = Now.AddDays(-14);
            var cResDue = Now.AddDays(-7);
            AddPeriod(context, c, c.CreatedAtUtc, cFrDue, cResDue, firstResponseBreached: true, resolutionBreached: true);
            RecordBreach(context, c, SlaDeadlineType.FirstResponse, cFrDue, cFrDue);
            RecordBreach(context, c, SlaDeadlineType.Resolution, cResDue, cResDue);

            // 4. Breached before the period: not counted anywhere.
            var d = NewTicket(context, _aminaId, Now.AddDays(-80));
            AddPeriod(context, d, d.CreatedAtUtc, Now.AddDays(-79), Now.AddDays(-60), resolutionBreached: true);
            RecordBreach(context, d, SlaDeadlineType.Resolution, Now.AddDays(-60), Now.AddDays(-60));

            // 5. Breached while nobody held it, assigned afterwards: attributed to nobody.
            var e = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-10));
            AddPeriod(context, e, e.CreatedAtUtc, Now.AddDays(-9), Now.AddDays(-4), resolutionBreached: true);
            RecordBreach(context, e, SlaDeadlineType.Resolution, Now.AddDays(-4), Now.AddDays(-4));
            Assign(context, e, _aminaId, Now.AddDays(-1));
        }

        var report = await ReportAsync();
        var amina = report.Rows.Single(r => r.EmployeeId == _aminaId);
        var details = await DetailsAsync(_aminaId);

        // Three tickets, four genuine events (the ticket whose two clocks both breached is two events). Table and details say the same.
        Assert.Equal(3, amina.TicketsWithSlaBreach);
        Assert.Equal(4, amina.SlaBreachEvents);
        Assert.Equal(amina.TicketsWithSlaBreach, details.Summary.TicketsWithBreach);
        Assert.Equal(amina.SlaBreachEvents, details.Summary.BreachEvents);
        Assert.Equal(details.Summary.BreachEvents, details.Summary.FirstResponseBreaches + details.Summary.ResolutionBreaches);
        var breached = details.Records.Where(r => r.Status == TeamPerformanceSlaStatuses.Breached).ToList();
        Assert.Equal(amina.SlaBreachEvents, breached.Count);
        Assert.Equal(amina.TicketsWithSlaBreach, breached.Select(r => r.TicketId).Distinct().Count());
        Assert.Equal(2, details.Summary.FirstResponseBreaches);     // tickets a and c
        Assert.Equal(2, details.Summary.ResolutionBreaches);        // tickets b and c

        // Bilal holds ticket 2 now but did not hold it at the deadline.
        Assert.Equal(0, report.Rows.Single(r => r.EmployeeId == _bilalId).TicketsWithSlaBreach);
        Assert.Equal(0, (await DetailsAsync(_bilalId)).Summary.BreachEvents);

        // Each record carries the facts it was decided from.
        var first = Assert.Single(breached, r => r.SlaType == "First Response" && r.TicketId != bothId);
        Assert.Equal("Amina Agent", first.AssignedEmployeeName);
        Assert.Equal("Customer Service", first.DepartmentName);
        Assert.Equal(Now.AddDays(-9), first.SlaStartedAtUtc);
        Assert.Equal(Now.AddDays(-8), first.DueAtUtc);
        Assert.Equal(Now.AddDays(-8), first.EventAtUtc);
        Assert.Null(first.ActualAtUtc);
        Assert.True(first.IsOngoing);
        Assert.Equal((int)(Now - Now.AddDays(-8)).TotalMinutes, first.BreachMinutes);
        Assert.EndsWith("(ongoing)", first.BreachText);
        Assert.Equal(240, first.TargetMinutes);
        Assert.Contains("Business hours", first.TargetText);
        Assert.Contains("Medium priority", first.Policy);
        Assert.Contains("priority SLA policy", first.Policy);
        Assert.Contains("No first human response", first.Explanation);
        Assert.Contains("Amina Agent held the ticket at the deadline", first.Explanation);

        var reassigned = Assert.Single(breached, r => r.TicketId == reassignedId);
        Assert.Equal("Resolution", reassigned.SlaType);
        Assert.Equal("Amina Agent", reassigned.AssignedEmployeeName);

        // The ticket whose clocks both breached: one record per clock.
        var both = breached.Where(r => r.TicketId == bothId).OrderBy(r => r.SlaType).ToList();
        Assert.Equal(["First Response", "Resolution"], both.Select(r => r.SlaType).ToArray());
    }

    [Fact]
    public async Task DateRange_BoundsTheBreachesIdenticallyOnTheReportAndTheDetails()
    {
        var day = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var context = _db.CreateContext())
        {
            // Recorded exactly at the start of Sep 1 (inclusive), the last second of Sep 3 (inclusive), and the first instant of Sep 4 (exclusive).
            foreach (var (name, at) in new[] { ("start", day), ("end", day.AddDays(3).AddSeconds(-1)), ("after", day.AddDays(3)), ("before", day.AddSeconds(-1)) })
            {
                var ticket = NewTicket(context, _aminaId, day.AddDays(-20), day.AddDays(-19));
                AddPeriod(context, ticket, ticket.CreatedAtUtc, day.AddDays(-19), at, resolutionBreached: true);
                RecordBreach(context, ticket, SlaDeadlineType.Resolution, at, at);
                Assert.NotNull(name);
            }
        }

        var from = DateOnly.FromDateTime(day);
        var to = DateOnly.FromDateTime(day.AddDays(2));
        var report = await ReportAsync(new TeamPerformanceRequestDto(DateFrom: from, DateTo: to));
        var details = await DetailsAsync(_aminaId, r => r with { DateFrom = from, DateTo = to });

        Assert.Equal(2, report.Rows.Single(r => r.EmployeeId == _aminaId).SlaBreachEvents);
        Assert.Equal(2, details.Summary.BreachEvents);
        Assert.Equal(from, details.DateFrom);
        Assert.Equal(to, details.DateTo);

        // Widening the range by one day picks up the next one on both.
        var wide = await DetailsAsync(_aminaId, r => r with { DateFrom = from, DateTo = to.AddDays(1) });
        Assert.Equal(3, wide.Summary.BreachEvents);
        Assert.Equal(3, (await ReportAsync(new TeamPerformanceRequestDto(DateFrom: from, DateTo: to.AddDays(1)))).Rows.Single(r => r.EmployeeId == _aminaId).SlaBreachEvents);
    }

    [Fact]
    public async Task BreachedRecords_EqualTheReportCount_ForEveryEmployee_UnderEveryFilter()
    {
        using (var context = _db.CreateContext())
        {
            foreach (var (holder, index) in new[] { _aminaId, _aminaId, _bilalId, _danaId, _bilalId }.Select((h, i) => (h, i)))
            {
                var department = holder == _bilalId ? _db.CollectionsId : _db.CustomerServiceId;
                var ticket = NewTicket(context, holder, Now.AddDays(-20 - index), departmentId: department);
                var due = Now.AddDays(-5 - index);
                AddPeriod(context, ticket, ticket.CreatedAtUtc, ticket.CreatedAtUtc.AddHours(4), due, resolutionBreached: true);
                RecordBreach(context, ticket, SlaDeadlineType.Resolution, due, due);
            }
        }

        var filters = new TeamPerformanceRequestDto[]
        {
            new(),
            new(DepartmentId: _db.CollectionsId),
            new(DepartmentId: _db.CustomerServiceId),
            new(DateFrom: DateOnly.FromDateTime(Now.AddDays(-8)), DateTo: DateOnly.FromDateTime(Now))
        };

        foreach (var filter in filters)
        {
            var report = await ReportAsync(filter);
            Assert.NotEmpty(report.Rows);
            foreach (var row in report.Rows)
            {
                var details = await DetailsAsync(row.EmployeeId, r => r with
                {
                    DateFrom = filter.DateFrom, DateTo = filter.DateTo, DepartmentId = filter.DepartmentId, Status = TeamPerformanceSlaStatuses.Breached
                });

                // The table row and the details are the same aggregation.
                Assert.Equal(row.SlaBreachEvents, details.Summary.BreachEvents);
                Assert.Equal(row.TicketsWithSlaBreach, details.Summary.TicketsWithBreach);
                Assert.Equal(row.SlaMet, details.Summary.Met);
                Assert.Equal(row.SlaEvaluated, details.Summary.Evaluated);
                Assert.Equal(row.SlaCompliancePercent, details.Summary.CompliancePercent);
                Assert.Equal(row.SlaBreachEvents, details.TotalRecords);
                Assert.Equal(row.SlaBreachEvents, details.Records.Count);
            }
        }
    }

    [Fact]
    public async Task ARepeatedBreachedStateRow_IsNotABreach_AndNeverInflatesTheCounts()
    {
        // Regression. Recording the first human response on a ticket that is already Breached writes a Breached -> Breached history row
        // ("First human response recorded ..."). It is bookkeeping, not a missed deadline, and used to be counted as a breach — re-counting
        // a ticket in every later period in which someone answered it.
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-9));
            AddPeriod(context, ticket, ticket.CreatedAtUtc, Now.AddDays(-8), Now.AddDays(-2), firstResponseBreached: true);
            context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                ticket.TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Breached, (byte)SlaState.Breached,
                _aminaId, false, $"First human response recorded (Manual) at {Now.AddDays(-3):O}; entered at {Now.AddDays(-3):O}.", Guid.NewGuid(), Now.AddDays(-3)));
            context.SaveChanges();
        }

        var report = await ReportAsync();
        var row = report.Rows.Single(r => r.EmployeeId == _aminaId);
        var details = await DetailsAsync(_aminaId);

        Assert.Equal((0, 0, 0), (row.TicketsWithSlaBreach, row.SlaBreachEvents, row.SlaEvaluated));
        Assert.Equal(0, report.Totals.TicketsWithSlaBreach);
        Assert.Equal(0, details.Summary.BreachEvents);
        Assert.DoesNotContain(details.Records, r => r.Status == TeamPerformanceSlaStatuses.Breached);
        Assert.Empty((await RecordsAsync(_aminaId, TeamPerformanceMetric.SlaBreaches)).Records);
    }

    // ---------------------------------------------------------------
    // Met, Pending, compliance
    // ---------------------------------------------------------------

    [Fact]
    public async Task Summary_CountsMetBreachedAndPending_AndComplianceExcludesPending()
    {
        using (var context = _db.CreateContext())
        {
            // Met: first response and resolution both on time, recorded by Amina in the period.
            var met = NewTicket(context, _aminaId, Now.AddDays(-10), status: TicketStatus.Resolved);
            var metFrDue = met.CreatedAtUtc.AddHours(4);
            var metResDue = met.CreatedAtUtc.AddDays(3);
            AddPeriod(context, met, met.CreatedAtUtc, metFrDue, metResDue);
            RecordFirstResponse(context, met, met.CreatedAtUtc.AddHours(1));
            RecordResolution(context, met, _aminaId, met.CreatedAtUtc.AddDays(2));

            // Breached: one Resolution breach.
            var late = NewTicket(context, _aminaId, Now.AddDays(-12));
            var lateDue = Now.AddDays(-6);
            AddPeriod(context, late, late.CreatedAtUtc, late.CreatedAtUtc.AddHours(4), lateDue, resolutionBreached: true);
            RecordFirstResponse(context, late, late.CreatedAtUtc.AddHours(1));
            RecordBreach(context, late, SlaDeadlineType.Resolution, lateDue, lateDue);

            // Pending: an open ticket Amina holds now, nothing answered yet, due in the future.
            var open = NewTicket(context, _aminaId, Now.AddHours(-2));
            AddPeriod(context, open, open.CreatedAtUtc, Now.AddHours(2), Now.AddDays(2));
        }

        var details = await DetailsAsync(_aminaId);

        // Met: first response (met), resolution (met), first response (late ticket answered in time).  Breach events: 1.  Pending: first response + resolution of the open ticket.
        Assert.Equal(3, details.Summary.Met);
        Assert.Equal(1, details.Summary.BreachEvents);
        Assert.Equal(1, details.Summary.TicketsWithBreach);
        Assert.Equal(2, details.Summary.Pending);
        Assert.Equal(4, details.Summary.Evaluated);                       // Met + Breach Events; pending is not evaluated
        Assert.Equal(details.Summary.Met + details.Summary.BreachEvents, details.Summary.Evaluated);

        // 3 / 4 = 75.0 — the two Pending clocks are not in the denominator.
        Assert.Equal(75.0m, details.Summary.CompliancePercent);

        var pending = details.Records.Where(r => r.Status == TeamPerformanceSlaStatuses.Pending).ToList();
        Assert.Equal(["First Response", "Resolution"], pending.Select(r => r.SlaType).OrderBy(t => t).ToArray());
        Assert.All(pending, r => Assert.Null(r.ActualAtUtc));
        Assert.All(pending, r => Assert.Null(r.BreachMinutes));

        var metFirstResponse = Assert.Single(details.Records, r => r.Status == TeamPerformanceSlaStatuses.Met && r.SlaType == "First Response" && r.DueAtUtc == Now.AddDays(-10).AddHours(4));
        Assert.Equal(Now.AddDays(-10).AddHours(1), metFirstResponse.ActualAtUtc);
        Assert.Contains("on or before the due time", metFirstResponse.Explanation);
        Assert.Null(metFirstResponse.BreachText);
    }

    [Fact]
    public async Task Compliance_IsNotComputed_WhenNothingHasBeenDecided()
    {
        using (var context = _db.CreateContext())
        {
            var open = NewTicket(context, _aminaId, Now.AddHours(-2));
            AddPeriod(context, open, open.CreatedAtUtc, Now.AddHours(2), Now.AddDays(2));
        }

        var details = await DetailsAsync(_aminaId);
        Assert.Equal(0, details.Summary.Evaluated);
        Assert.Equal(2, details.Summary.Pending);
        Assert.Null(details.Summary.CompliancePercent);

        Assert.Null(TeamPerformanceAppService.CompliancePercent(0, 0));
        Assert.Equal(100m, TeamPerformanceAppService.CompliancePercent(5, 0));
        Assert.Equal(66.7m, TeamPerformanceAppService.CompliancePercent(2, 1));
        Assert.Equal(0m, TeamPerformanceAppService.CompliancePercent(0, 4));
    }

    [Fact]
    public async Task ALateResolution_IsNeverMet_AndAReopenedTicketsFirstResponseIsEvaluatedOnce()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            // Created 20 days ago; first response on time; Resolution breached in the first period; reopened 5 days ago into a new Resolution cycle
            // that carries First Response unchanged.
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-20));
            ticketId = ticket.TicketId;
            var frDue = ticket.CreatedAtUtc.AddHours(4);
            var firstResDue = Now.AddDays(-12);
            var original = AddPeriod(context, ticket, ticket.CreatedAtUtc, frDue, firstResDue, resolutionBreached: true);
            RecordFirstResponse(context, ticket, ticket.CreatedAtUtc.AddHours(1));
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, firstResDue, firstResDue);
            RecordResolution(context, ticket, _aminaId, Now.AddDays(-8)); // late: after the original due

            original.EndPeriod(Now.AddDays(-5));
            context.TicketSlaInstances.Add(TicketSlaInstance.OpenReopenCycle(
                ticket.TicketId, (byte)PriorityLevel.Medium, Now.AddDays(-5), frDue, carriedFirstResponseBreached: false, Now.AddDays(-1),
                new AppliedSlaPolicy(SlaClockBasis.BusinessHours, AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 1800)));
            context.SaveChanges();
            RecordResolution(context, ticket, _aminaId, Now.AddDays(-2)); // in the reopen cycle, before its due
        }

        var details = await DetailsAsync(_aminaId);
        var forTicket = details.Records.Where(r => r.TicketId == ticketId).ToList();

        // First Response: one Met record (the carried clock is not counted twice).
        Assert.Single(forTicket, r => r.SlaType == "First Response" && r.Status == TeamPerformanceSlaStatuses.Met);
        // Resolution of the original period: Breached (its due, not the reopen cycle's).
        var breached = Assert.Single(forTicket, r => r.Status == TeamPerformanceSlaStatuses.Breached);
        Assert.Equal(Now.AddDays(-12), breached.DueAtUtc);
        Assert.Equal(Now.AddDays(-8), breached.ActualAtUtc);
        Assert.Equal(4 * 24 * 60, breached.BreachMinutes);
        // The late resolution (day -8) is not Met; the reopen cycle's on-time resolution (day -2) is.
        var metResolution = Assert.Single(forTicket, r => r.SlaType == "Resolution" && r.Status == TeamPerformanceSlaStatuses.Met);
        Assert.Equal(Now.AddDays(-2), metResolution.ActualAtUtc);
        Assert.Equal(Now.AddDays(-5), metResolution.SlaStartedAtUtc);
        Assert.Equal(3, forTicket.Count);
    }

    [Fact]
    public async Task AnEmployeeIsCreditedWithWhatTheyHeldAtTheMoment_NotWhatTheyHoldNow()
    {
        using (var context = _db.CreateContext())
        {
            // Amina answered on time while she held the ticket; then it moved to Bilal, who resolved it on time.
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-6), status: TicketStatus.Resolved);
            AddPeriod(context, ticket, ticket.CreatedAtUtc, ticket.CreatedAtUtc.AddHours(4), ticket.CreatedAtUtc.AddDays(4));
            RecordFirstResponse(context, ticket, ticket.CreatedAtUtc.AddHours(1));
            Assign(context, ticket, _bilalId, Now.AddDays(-4));
            RecordResolution(context, ticket, _bilalId, Now.AddDays(-3));
        }

        var amina = await DetailsAsync(_aminaId);
        var bilal = await DetailsAsync(_bilalId);

        Assert.Equal(["First Response"], amina.Records.Where(r => r.Status == TeamPerformanceSlaStatuses.Met).Select(r => r.SlaType).ToArray());
        Assert.Equal(["Resolution"], bilal.Records.Where(r => r.Status == TeamPerformanceSlaStatuses.Met).Select(r => r.SlaType).ToArray());
        Assert.All(bilal.Records, r => Assert.Equal("Bilal Caller", r.AssignedEmployeeName));
    }

    // ---------------------------------------------------------------
    // Pagination, sorting, status filter
    // ---------------------------------------------------------------

    private void SeedBreaches(int count)
    {
        using var context = _db.CreateContext();
        for (var i = 0; i < count; i++)
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-25 - i));
            var due = Now.AddDays(-20 + i);          // later i -> later breach
            var actualLate = due.AddHours(i + 1);    // later i -> bigger overshoot
            AddPeriod(context, ticket, ticket.CreatedAtUtc, ticket.CreatedAtUtc.AddHours(4), due, resolutionBreached: true);
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, due, due);
            RecordResolution(context, ticket, _aminaId, actualLate);
        }
    }

    [Fact]
    public async Task Paging_IsServerSide_StableAndClamped()
    {
        SeedBreaches(7);
        var status = TeamPerformanceSlaStatuses.Breached;

        var page1 = await DetailsAsync(_aminaId, r => r with { Status = status, PageSize = 3, Page = 1 });
        var page2 = await DetailsAsync(_aminaId, r => r with { Status = status, PageSize = 3, Page = 2 });
        var page3 = await DetailsAsync(_aminaId, r => r with { Status = status, PageSize = 3, Page = 3 });

        Assert.Equal(7, page1.TotalRecords);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal([3, 3, 1], new[] { page1.Records.Count, page2.Records.Count, page3.Records.Count });

        var all = page1.Records.Concat(page2.Records).Concat(page3.Records).Select(r => r.TicketId).ToList();
        Assert.Equal(7, all.Distinct().Count());   // no row repeated or lost across pages
        Assert.Equal(7, page1.Summary.BreachEvents);   // the summary is for the whole employee, not the page

        // A page past the end shows the last page; a nonsense page size falls back to a sane one.
        var beyond = await DetailsAsync(_aminaId, r => r with { Status = status, PageSize = 3, Page = 99 });
        Assert.Equal(3, beyond.Page);
        Assert.Single(beyond.Records);
        var huge = await DetailsAsync(_aminaId, r => r with { Status = status, PageSize = 100000 });
        Assert.Equal(TeamPerformanceAppService.MaxPageSize, huge.PageSize);
        var zero = await DetailsAsync(_aminaId, r => r with { Status = status, PageSize = 0 });
        Assert.Equal(TeamPerformanceAppService.DefaultPageSize, zero.PageSize);
    }

    [Fact]
    public async Task Sorting_OrdersTheWholeSetBeforePaging_InBothDirections()
    {
        SeedBreaches(7);
        var status = TeamPerformanceSlaStatuses.Breached;

        var byBreachDesc = await DetailsAsync(_aminaId, r => r with { Status = status, SortBy = "breach", SortDir = "desc", PageSize = 3, Page = 1 });
        Assert.Equal("breach", byBreachDesc.SortBy);
        Assert.Equal("desc", byBreachDesc.SortDir);
        Assert.Equal([7 * 60, 6 * 60, 5 * 60], byBreachDesc.Records.Select(r => r.BreachMinutes!.Value).ToArray());

        var byBreachAsc = await DetailsAsync(_aminaId, r => r with { Status = status, SortBy = "breach", SortDir = "asc", PageSize = 3, Page = 1 });
        Assert.Equal([60, 2 * 60, 3 * 60], byBreachAsc.Records.Select(r => r.BreachMinutes!.Value).ToArray());

        // The last page of an ascending sort is the biggest overshoot.
        var lastAsc = await DetailsAsync(_aminaId, r => r with { Status = status, SortBy = "breach", SortDir = "asc", PageSize = 3, Page = 3 });
        Assert.Equal([7 * 60], lastAsc.Records.Select(r => r.BreachMinutes!.Value).ToArray());

        var byTicket = await DetailsAsync(_aminaId, r => r with { Status = status, SortBy = "ticket", SortDir = "asc" });
        var numbers = byTicket.Records.Select(r => r.TicketNumber).ToList();
        Assert.Equal(numbers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), numbers);

        var byDue = await DetailsAsync(_aminaId, r => r with { Status = status, SortBy = "due", SortDir = "desc" });
        var dues = byDue.Records.Select(r => r.DueAtUtc!.Value).ToList();
        Assert.Equal(dues.OrderByDescending(d => d).ToList(), dues);

        // The default is newest breach first; an unknown key falls back to it.
        var byDefault = await DetailsAsync(_aminaId, r => r with { Status = status });
        var unknownKey = await DetailsAsync(_aminaId, r => r with { Status = status, SortBy = "bogus" });
        Assert.Equal("event", byDefault.SortBy);
        Assert.Equal(byDefault.Records.Select(r => r.TicketId), unknownKey.Records.Select(r => r.TicketId));
        var events = byDefault.Records.Select(r => r.EventAtUtc!.Value).ToList();
        Assert.Equal(events.OrderByDescending(e => e).ToList(), events);
    }

    [Fact]
    public async Task StatusFilter_NarrowsTheListOnly_NeverTheSummary()
    {
        using (var context = _db.CreateContext())
        {
            var late = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, late, late.CreatedAtUtc, late.CreatedAtUtc.AddHours(4), Now.AddDays(-6), resolutionBreached: true);
            RecordBreach(context, late, SlaDeadlineType.Resolution, Now.AddDays(-6), Now.AddDays(-6));

            var open = NewTicket(context, _aminaId, Now.AddHours(-2));
            AddPeriod(context, open, open.CreatedAtUtc, Now.AddHours(2), Now.AddDays(2));
        }

        var all = await DetailsAsync(_aminaId);
        var onlyBreached = await DetailsAsync(_aminaId, r => r with { Status = "breached" });
        var onlyPending = await DetailsAsync(_aminaId, r => r with { Status = "PENDING" });

        // late ticket: Resolution breached + First Response still pending; open ticket: First Response + Resolution pending.
        Assert.Equal(4, all.TotalRecords);
        Assert.Equal(1, onlyBreached.TotalRecords);
        Assert.Equal(TeamPerformanceSlaStatuses.Breached, onlyBreached.StatusFilter);
        Assert.Equal(3, onlyPending.TotalRecords);
        Assert.Equal(all.Summary, onlyBreached.Summary);
        Assert.Equal(all.Summary, onlyPending.Summary);
    }

    // ---------------------------------------------------------------
    // Eligibility and the rules block
    // ---------------------------------------------------------------

    [Fact]
    public async Task TheDetailsFollowTheReportsFilters_AnEmployeeTheReportWouldNotShowIsRefused()
    {
        // Amina is in Customer Service, Bilal in Collections.
        Assert.Equal(TeamPerformanceSlaDetailsOutcome.Success,
            (await DetailsResultAsync(_aminaId, r => r with { DepartmentId = _db.CustomerServiceId })).Outcome);
        Assert.Equal(TeamPerformanceSlaDetailsOutcome.EmployeeNotEligible,
            (await DetailsResultAsync(_aminaId, r => r with { DepartmentId = _db.CollectionsId })).Outcome);
        Assert.Equal(TeamPerformanceSlaDetailsOutcome.EmployeeNotEligible,
            (await DetailsResultAsync(Guid.NewGuid())).Outcome);

        // A deactivated employee has no row, so no details.
        Guid retired;
        using (var context = _db.CreateContext())
        {
            retired = _db.AddEmployee(context, "Rami Retired");
            context.Employees.Single(e => e.EmployeeId == retired).Deactivate(Now.AddDays(-1));
            context.SaveChanges();
        }

        Assert.Equal(TeamPerformanceSlaDetailsOutcome.EmployeeNotEligible, (await DetailsResultAsync(retired)).Outcome);
    }

    [Fact]
    public async Task Rules_ExplainTheExistingSlaBehaviour_AndShowTheConfiguredPolicies()
    {
        using (var context = _db.CreateContext())
        {
            var calendar = new BusinessCalendar("UAE", new TimeOnly(8, 0), new TimeOnly(18, 0), "Asia/Dubai", Now.AddDays(-300));
            context.BusinessCalendars.Add(calendar);
            context.SaveChanges();
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                context.BusinessCalendarWorkingDays.Add(new BusinessCalendarWorkingDay(calendar.BusinessCalendarId, day, day != DayOfWeek.Friday));
            }

            context.Holidays.Add(new Holiday(calendar.BusinessCalendarId, new DateOnly(2026, 12, 2), "National Day", _aminaId));
            context.SaveChanges();
        }

        var rules = (await DetailsAsync(_aminaId)).Rules;

        var topics = rules.Rules.Select(r => r.Topic).ToList();
        foreach (var topic in new[] { "When the timer starts", "When the timer stops", "What counts as a first response", "Business hours, weekends and holidays",
                     "Reassignment", "Reopened tickets", "How breaches are attributed to employees" })
        {
            Assert.Contains(topic, topics);
        }

        // The business-hours rule is the live calendar, not copied text.
        var businessHours = rules.Rules.Single(r => r.Topic == "Business hours, weekends and holidays").Text;
        Assert.Contains("08:00–18:00", businessHours);
        Assert.Contains("Asia/Dubai", businessHours);
        Assert.Contains("1 configured holiday", businessHours);
        Assert.DoesNotContain("Friday", businessHours.Split("working days (")[1].Split(')')[0]);

        Assert.Equal(["Critical", "Medium"], rules.Policies.Select(p => p.PriorityName).ToArray());
        Assert.Equal("24/7 calendar time", rules.Policies[0].ClockBasis);
        Assert.Equal(1800, rules.Policies[1].ResolutionTargetMinutes);
        Assert.Equal("UAE", rules.Calendar!.Name);
        Assert.Equal(1, rules.Calendar.HolidayCount);

        // The formula is stated, with Pending excluded.
        Assert.Contains("Pending", TeamPerformanceSlaSummaryDto.Formula);
        Assert.Contains("Evaluated = Met + Breach Events", TeamPerformanceSlaSummaryDto.Formula);
        Assert.Contains("What counts as a breach", topics);
    }
}
