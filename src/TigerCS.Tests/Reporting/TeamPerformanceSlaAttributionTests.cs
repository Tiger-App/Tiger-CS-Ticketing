using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using Microsoft.EntityFrameworkCore;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// Regression tests for the SLA numbers' correctness, over real SQL (SQLite): which history rows are genuine breach events, who a breach is
/// attributed to (the holder at the breach DEADLINE — not at the moment a scheduler happened to record it, and nobody after a release),
/// reopen cycles, both clocks breaching, late first responses across periods, and the totals row's de-duplication and combined compliance.
/// SLA timers and business-hour rules are not touched: every deadline here is seeded exactly as the SLA engine stores it.
/// </summary>
public sealed class TeamPerformanceSlaAttributionTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;   // 2026-09-10 12:00 UTC

    private readonly DashboardSqliteFixture _db = new();
    private readonly Guid _aminaId;
    private readonly Guid _bilalId;

    public TeamPerformanceSlaAttributionTests()
    {
        using var context = _db.CreateContext();
        _aminaId = _db.AddEmployee(context, "Amina Agent");
        _bilalId = _db.AddEmployee(context, "Bilal Caller");
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private async Task<TeamPerformanceReportDto> ReportAsync(DateTime? from = null, DateTime? to = null)
    {
        using var context = _db.CreateContext();
        var service = new TeamPerformanceAppService(new TeamPerformanceQueryRepository(context), new DashboardSqliteFixture.FixedTimeProvider(Now));
        return await service.GetReportAsync(new TeamPerformanceRequestDto(
            from is null ? null : DateOnly.FromDateTime(from.Value), to is null ? null : DateOnly.FromDateTime(to.Value)));
    }

    private async Task<TeamPerformanceSlaDetailsDto> DetailsAsync(Guid employeeId)
    {
        using var context = _db.CreateContext();
        var service = new TeamPerformanceAppService(new TeamPerformanceQueryRepository(context), new DashboardSqliteFixture.FixedTimeProvider(Now));
        var result = await service.GetSlaDetailsAsync(new TeamPerformanceSlaDetailsRequestDto(employeeId, PageSize: 100));
        Assert.Equal(TeamPerformanceSlaDetailsOutcome.Success, result.Outcome);
        return result.Details!;
    }

    private static TeamPerformanceRowDto Row(TeamPerformanceReportDto report, Guid id) => report.Rows.Single(r => r.EmployeeId == id);

    private Ticket NewTicket(TigerCsDbContext context, Guid holder, DateTime createdAt)
    {
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: createdAt, owner: holder);
        Assign(context, ticket, holder, createdAt.AddMinutes(5));
        return ticket;
    }

    /// <summary>A reassignment as the services do it: the current row is superseded (the end time is stamped by the DbContext), a new row is added.</summary>
    private static void Assign(TigerCsDbContext context, Ticket ticket, Guid employeeId, DateTime atUtc)
    {
        foreach (var current in context.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent))
        {
            current.MarkSuperseded();
        }

        context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, employeeId, ticket.CurrentDepartmentId, atUtc, null));
        context.SaveChanges();
    }

    /// <summary>A release to the queue: the assignment ends at <paramref name="atUtc"/> and nobody holds the ticket afterwards.</summary>
    private static void Release(TigerCsDbContext context, Ticket ticket, DateTime atUtc)
    {
        foreach (var current in context.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent))
        {
            current.MarkSuperseded(atUtc);
        }

        context.SaveChanges();
    }

    private static TicketSlaInstance AddPeriod(
        TigerCsDbContext context, Ticket ticket, DateTime firstResponseDue, DateTime resolutionDue, bool frBreached = false, bool resBreached = false)
    {
        var period = TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, (byte)PriorityLevel.Medium, ticket.CreatedAtUtc, firstResponseDue, resolutionDue,
            new AppliedSlaPolicy(SlaClockBasis.BusinessHours, AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 1800));
        if (frBreached) period.MarkBreached(SlaDeadlineType.FirstResponse);
        if (resBreached) period.MarkBreached(SlaDeadlineType.Resolution);
        context.TicketSlaInstances.Add(period);
        context.SaveChanges();
        return period;
    }

    /// <summary>The history row SlaBreachProcessor writes — note and state values exactly as the engine writes them.</summary>
    private static void RecordBreach(
        TigerCsDbContext context, Ticket ticket, SlaDeadlineType type, DateTime dueUtc, DateTime recordedAtUtc, bool alreadyBreached = false)
    {
        var old = alreadyBreached ? SlaState.Breached : SlaState.Running;
        context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.SlaState, (byte)old, (byte)SlaState.Breached,
            actorEmployeeId: null, actorIsSystem: true, note: SlaBreachNotesFor(type, dueUtc), Guid.NewGuid(), recordedAtUtc));
        context.SaveChanges();
    }

    private static string SlaBreachNotesFor(SlaDeadlineType type, DateTime due) =>
        TigerCS.Application.Modules.SlaAndEscalation.Services.SlaBreachNotes.For(type, due);

    private static void FirstResponse(TigerCsDbContext context, Ticket ticket, DateTime atUtc)
    {
        ticket.RecordFirstHumanResponse(atUtc);
        context.SaveChanges();
    }

    // ---------------------------------------------------------------
    // Late first responses across reporting periods
    // ---------------------------------------------------------------

    [Fact]
    public async Task ALateFirstResponseInALaterPeriod_IsCountedOnceInThePeriodTheDeadlineWasMissed_NeverAgain()
    {
        var created = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
        var frDue = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, created);
            AddPeriod(context, ticket, frDue, Now.AddDays(20), frBreached: true);
            RecordBreach(context, ticket, SlaDeadlineType.FirstResponse, frDue, frDue);               // the scheduler, at the deadline (August)
            FirstResponse(context, ticket, new DateTime(2026, 9, 5, 11, 0, 0, DateTimeKind.Utc));       // the person answers in September...
            context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(                              // ...which writes a Breached -> Breached bookkeeping row
                ticket.TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Breached, (byte)SlaState.Breached,
                _aminaId, false, "First human response recorded (Manual) at 2026-09-05T11:00:00.0000000Z; entered at 2026-09-05T11:00:00.0000000Z.",
                Guid.NewGuid(), new DateTime(2026, 9, 5, 11, 0, 0, DateTimeKind.Utc)));
            context.SaveChanges();
        }

        var august = Row(await ReportAsync(new DateTime(2026, 8, 1), new DateTime(2026, 8, 31)), _aminaId);
        var september = Row(await ReportAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 10)), _aminaId);

        // August: the breach — one ticket, one event, one evaluated clock, none met.
        Assert.Equal((1, 1, 1, 0), (august.TicketsWithSlaBreach, august.SlaBreachEvents, august.SlaEvaluated, august.SlaMet));
        Assert.Equal(0m, august.SlaCompliancePercent);
        // September: nothing new was missed, and a late answer is not an on-time achievement either.
        Assert.Equal((0, 0, 0, 0), (september.TicketsWithSlaBreach, september.SlaBreachEvents, september.SlaEvaluated, september.SlaMet));
        Assert.Null(september.SlaCompliancePercent);
    }

    // ---------------------------------------------------------------
    // Both clocks breaching
    // ---------------------------------------------------------------

    [Fact]
    public async Task ATicketWhoseTwoClocksBothBreached_IsOneTicketTwoEvents_OneOfEachClock()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-12));
            ticketId = ticket.TicketId;
            var frDue = Now.AddDays(-11);
            var resDue = Now.AddDays(-5);
            AddPeriod(context, ticket, frDue, resDue, frBreached: true, resBreached: true);
            RecordBreach(context, ticket, SlaDeadlineType.FirstResponse, frDue, frDue);
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, resDue, resDue, alreadyBreached: true);   // Breached -> Breached: a genuine second event
        }

        var report = await ReportAsync();
        var row = Row(report, _aminaId);
        var details = await DetailsAsync(_aminaId);

        Assert.Equal((1, 2), (row.TicketsWithSlaBreach, row.SlaBreachEvents));
        Assert.Equal(2, row.SlaEvaluated);                       // Met + Breach Events
        Assert.Equal((1, 2, 1, 1, 0), (details.Summary.TicketsWithBreach, details.Summary.BreachEvents, details.Summary.FirstResponseBreaches,
            details.Summary.ResolutionBreaches, details.Summary.Met));
        Assert.Equal(["First Response", "Resolution"], details.Records.Where(r => r.Status == TeamPerformanceSlaStatuses.Breached)
            .Select(r => r.SlaType).OrderBy(t => t).ToArray());
        Assert.All(details.Records.Where(r => r.Status == TeamPerformanceSlaStatuses.Breached), r => Assert.Equal(ticketId, r.TicketId));
        Assert.Equal((1, 2), (report.Totals.TicketsWithSlaBreach, report.Totals.SlaBreachEvents));
    }

    // ---------------------------------------------------------------
    // Reassignment: the deadline decides, not the moment of recording
    // ---------------------------------------------------------------

    [Fact]
    public async Task ADelayedWrite_DoesNotMoveTheBreachToWhoeverHoldsTheTicketWhenItIsRecorded()
    {
        var resDue = Now.AddDays(-6);
        using (var context = _db.CreateContext())
        {
            // Amina holds it at the deadline. It is reassigned to Bilal a day later; the scheduler only records the breach a day after that.
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, ticket, Now.AddDays(-11), resDue, resBreached: true);
            Assign(context, ticket, _bilalId, resDue.AddDays(1));
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, resDue, recordedAtUtc: resDue.AddDays(2));

            // Control: a deadline that falls AFTER the reassignment is Bilal's.
            var second = NewTicket(context, _aminaId, Now.AddDays(-12));
            var secondDue = Now.AddDays(-5);
            AddPeriod(context, second, Now.AddDays(-11), secondDue, resBreached: true);
            Assign(context, second, _bilalId, secondDue.AddHours(-3));
            RecordBreach(context, second, SlaDeadlineType.Resolution, secondDue, secondDue.AddHours(1));
        }

        var report = await ReportAsync();
        Assert.Equal(1, Row(report, _aminaId).SlaBreachEvents);
        Assert.Equal(1, Row(report, _bilalId).SlaBreachEvents);
        Assert.Equal(2, report.Totals.TicketsWithSlaBreach);

        var amina = Assert.Single((await DetailsAsync(_aminaId)).Records, r => r.Status == TeamPerformanceSlaStatuses.Breached);
        Assert.Equal(resDue, amina.DueAtUtc);
        Assert.Equal(resDue.AddDays(2), amina.EventAtUtc);                  // the deadline and the recording time are both shown, and differ
    }

    // ---------------------------------------------------------------
    // Release before the breach
    // ---------------------------------------------------------------

    [Fact]
    public async Task ATicketReleasedBeforeTheDeadline_BelongsToNobody_EvenIfSomeoneIsAssignedBeforeTheWriteIsRecorded()
    {
        var resDue = Now.AddDays(-6);
        using (var context = _db.CreateContext())
        {
            // Released an hour before the deadline; Bilal is assigned an hour after it; the breach is recorded three hours after it.
            var released = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, released, Now.AddDays(-11), resDue, resBreached: true);
            Release(context, released, resDue.AddHours(-1));
            context.TicketAssignments.Add(new TicketAssignment(released.TicketId, _bilalId, released.CurrentDepartmentId, resDue.AddHours(1), null));
            context.SaveChanges();
            RecordBreach(context, released, SlaDeadlineType.Resolution, resDue, resDue.AddHours(3));

            // Control: released an hour AFTER the deadline (still before it is recorded): Amina held it when it breached.
            var late = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, late, Now.AddDays(-11), resDue, resBreached: true);
            Release(context, late, resDue.AddHours(1));
            RecordBreach(context, late, SlaDeadlineType.Resolution, resDue, resDue.AddHours(3));
        }

        var report = await ReportAsync();

        Assert.Equal(1, Row(report, _aminaId).SlaBreachEvents);       // only the second ticket
        Assert.Equal(0, Row(report, _bilalId).SlaBreachEvents);       // who was assigned after the deadline, before the write, gets nothing
        Assert.Equal(1, report.Totals.TicketsWithSlaBreach);
    }

    [Fact]
    public async Task EveryReassignmentOrReleaseStampsTheMomentTheAssignmentEnded_OnSave()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-3));
            ticketId = ticket.TicketId;
            Assign(context, ticket, _bilalId, Now.AddDays(-2));      // supersedes Amina's row without naming a time
        }

        using (var context = _db.CreateContext())
        {
            var rows = context.TicketAssignments.Where(a => a.TicketId == ticketId).OrderBy(a => a.AssignedAtUtc).ToList();
            Assert.Equal(2, rows.Count);
            Assert.False(rows[0].IsCurrent);
            Assert.NotNull(rows[0].SupersededAtUtc);                 // stamped by the DbContext
            Assert.True(rows[1].IsCurrent);
            Assert.Null(rows[1].SupersededAtUtc);                    // the current assignment has no end

            // An explicit time is kept, not overwritten by the save time.
            var explicitEnd = Now.AddDays(-1);
            rows[1].MarkSuperseded(explicitEnd);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            Assert.Equal(Now.AddDays(-1), context.TicketAssignments.Single(a => a.TicketId == ticketId && a.AssignedEmployeeId == _bilalId).SupersededAtUtc);
        }
    }

    // ---------------------------------------------------------------
    // Reopened tickets
    // ---------------------------------------------------------------

    [Fact]
    public async Task AReopenedTicketsSecondCycleBreach_IsASeparateEventAttributedToItsOwnHolder_AndTheTotalCountsTheTicketOnce()
    {
        var firstDue = Now.AddDays(-20);
        var secondDue = Now.AddDays(-10);
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-30));
            var original = AddPeriod(context, ticket, Now.AddDays(-29), firstDue, resBreached: true);
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, firstDue, firstDue);           // cycle 1 breaches while Amina holds it
            Assign(context, ticket, _bilalId, Now.AddDays(-15));                                     // reassigned...

            original.EndPeriod(Now.AddDays(-14));                                                    // ...then reopened into a new Resolution cycle
            var cycle = TicketSlaInstance.OpenReopenCycle(
                ticket.TicketId, (byte)PriorityLevel.Medium, Now.AddDays(-14), original.FirstResponseDueAtUtc, carriedFirstResponseBreached: false, secondDue,
                new AppliedSlaPolicy(SlaClockBasis.BusinessHours, AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 1800));
            cycle.MarkBreached(SlaDeadlineType.Resolution);
            context.TicketSlaInstances.Add(cycle);
            context.SaveChanges();
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, secondDue, secondDue, alreadyBreached: true);   // cycle 2 breaches while Bilal holds it
        }

        var report = await ReportAsync();

        Assert.Equal((1, 1), (Row(report, _aminaId).TicketsWithSlaBreach, Row(report, _aminaId).SlaBreachEvents));
        Assert.Equal((1, 1), (Row(report, _bilalId).TicketsWithSlaBreach, Row(report, _bilalId).SlaBreachEvents));
        // Two events, two employees — but one ticket, counted once in the total.
        Assert.Equal((1, 2), (report.Totals.TicketsWithSlaBreach, report.Totals.SlaBreachEvents));

        var bilal = Assert.Single((await DetailsAsync(_bilalId)).Records, r => r.Status == TeamPerformanceSlaStatuses.Breached);
        Assert.Equal(secondDue, bilal.DueAtUtc);                                                     // the new cycle's deadline, not the original's
        Assert.Equal(Now.AddDays(-14), bilal.SlaStartedAtUtc);
    }

    // ---------------------------------------------------------------
    // Totals: combined compliance, not an average
    // ---------------------------------------------------------------

    [Fact]
    public async Task TheTotalsRow_UsesCombinedMetOverCombinedEvaluated_NotTheAverageOfRowPercentages()
    {
        using (var context = _db.CreateContext())
        {
            // Amina: 1 met, 0 breached  -> 100 %.   Bilal: 1 met, 3 breached -> 25 %.   Combined: 2 / 5 = 40 % (an average of rows would say 62.5 %).
            var amina = NewTicket(context, _aminaId, Now.AddDays(-5));
            AddPeriod(context, amina, Now.AddDays(-5).AddHours(4), Now.AddDays(5));
            FirstResponse(context, amina, Now.AddDays(-5).AddHours(1));

            var bilalMet = NewTicket(context, _bilalId, Now.AddDays(-5));
            AddPeriod(context, bilalMet, Now.AddDays(-5).AddHours(4), Now.AddDays(5));
            FirstResponse(context, bilalMet, Now.AddDays(-5).AddHours(2));

            for (var i = 0; i < 3; i++)
            {
                var late = NewTicket(context, _bilalId, Now.AddDays(-20 - i));
                var due = Now.AddDays(-10 - i);
                AddPeriod(context, late, late.CreatedAtUtc.AddHours(4), due, resBreached: true);
                RecordBreach(context, late, SlaDeadlineType.Resolution, due, due);
            }
        }

        var report = await ReportAsync();
        var a = Row(report, _aminaId);
        var b = Row(report, _bilalId);

        Assert.Equal((1, 1, 100m), (a.SlaMet, a.SlaEvaluated, a.SlaCompliancePercent));
        Assert.Equal((1, 4, 25m), (b.SlaMet, b.SlaEvaluated, b.SlaCompliancePercent));
        Assert.Equal((2, 5), (report.Totals.SlaMet, report.Totals.SlaEvaluated));
        Assert.Equal(40m, report.Totals.SlaCompliancePercent);
        Assert.Equal(3, report.Totals.SlaBreachEvents);
        Assert.Equal(report.Rows.Sum(r => r.SlaBreachEvents), report.Totals.SlaBreachEvents);
        Assert.Equal(report.Rows.Sum(r => r.SlaEvaluated), report.Totals.SlaEvaluated);
    }

    [Fact]
    public async Task TheTotalsRow_CountsATicketOnce_WhenTwoEmployeesShareItsBreaches()
    {
        using (var context = _db.CreateContext())
        {
            // First Response breached under Amina; the ticket moves to Bilal, whose Resolution clock breaches: two events, two employees, one ticket.
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-12));
            var frDue = Now.AddDays(-11);
            var resDue = Now.AddDays(-4);
            AddPeriod(context, ticket, frDue, resDue, frBreached: true, resBreached: true);
            RecordBreach(context, ticket, SlaDeadlineType.FirstResponse, frDue, frDue);
            Assign(context, ticket, _bilalId, Now.AddDays(-8));
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, resDue, resDue, alreadyBreached: true);
        }

        var report = await ReportAsync();

        Assert.Equal((1, 1), (Row(report, _aminaId).TicketsWithSlaBreach, Row(report, _aminaId).SlaBreachEvents));
        Assert.Equal((1, 1), (Row(report, _bilalId).TicketsWithSlaBreach, Row(report, _bilalId).SlaBreachEvents));
        Assert.Equal(2, report.Rows.Sum(r => r.TicketsWithSlaBreach));      // summing the rows would double count it...
        Assert.Equal(1, report.Totals.TicketsWithSlaBreach);                // ...the total does not
        Assert.Equal(2, report.Totals.SlaBreachEvents);
    }

    // ---------------------------------------------------------------
    // SupersededAtUtc: every ending path, preserved once set, correct interval
    // ---------------------------------------------------------------

    [Fact]
    public async Task EveryServicePathEndsAnAssignmentThroughTheTrackedRepositoryRow_SoTheDbContextStampsIt()
    {
        // Release, reassignment, transfer, closure, unassignment ... all end an assignment the same way: take the current row from the
        // repository (GetCurrentAsync -> a TRACKED entity), call MarkSuperseded(), save. The hook on SaveChanges stamps the end time.
        long ticketId;
        using (var context = _db.CreateContext())
        {
            ticketId = NewTicket(context, _aminaId, Now.AddDays(-3)).TicketId;
        }

        var before = DateTime.UtcNow;
        using (var context = _db.CreateContext())
        {
            var current = await new TicketAssignmentRepository(context).GetCurrentAsync(ticketId);
            Assert.NotNull(current);
            Assert.Equal(EntityState.Unchanged, context.Entry(current!).State);     // tracked, so a change is seen and stamped
            current!.MarkSuperseded();                                              // what every ending path calls
            await context.SaveChangesAsync();
        }

        using (var context = _db.CreateContext())
        {
            var row = context.TicketAssignments.Single(a => a.TicketId == ticketId);
            Assert.False(row.IsCurrent);
            Assert.NotNull(row.SupersededAtUtc);
            Assert.InRange(row.SupersededAtUtc!.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        }
    }

    [Fact]
    public async Task OnceSupersededAtUtcIsSet_NoLaterCallOrSaveChangesIt()
    {
        long ticketId;
        var firstEnd = Now.AddDays(-2);
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-3));
            ticketId = ticket.TicketId;
            Release(context, ticket, firstEnd);                                     // explicit time
        }

        using (var context = _db.CreateContext())
        {
            var row = context.TicketAssignments.Single(a => a.TicketId == ticketId);
            Assert.Equal(firstEnd, row.SupersededAtUtc);

            row.MarkSuperseded();                                                   // a second call, no time
            row.MarkSuperseded(Now.AddDays(-1));                                    // a second call, a different time
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            // The original moment survives every later call and every later save.
            Assert.Equal(firstEnd, context.TicketAssignments.Single(a => a.TicketId == ticketId).SupersededAtUtc);
        }
    }

    [Fact]
    public async Task TheAssignmentInterval_IsHalfOpen_ADeadlineAtTheEndBelongsToTheNextHolderOrNobody()
    {
        var due = Now.AddDays(-6);
        using (var context = _db.CreateContext())
        {
            // Amina's assignment ends exactly AT the deadline and nobody follows: at the deadline instant nobody holds it.
            var endedAtDeadline = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, endedAtDeadline, Now.AddDays(-11), due, resBreached: true);
            Release(context, endedAtDeadline, due);
            RecordBreach(context, endedAtDeadline, SlaDeadlineType.Resolution, due, due.AddHours(2));

            // Bilal is assigned exactly AT the deadline (Amina's ended then): the deadline belongs to Bilal.
            var handedOverAtDeadline = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, handedOverAtDeadline, Now.AddDays(-11), due, resBreached: true);
            foreach (var current in context.TicketAssignments.Where(a => a.TicketId == handedOverAtDeadline.TicketId && a.IsCurrent))
            {
                current.MarkSuperseded(due);
            }

            context.TicketAssignments.Add(new TicketAssignment(handedOverAtDeadline.TicketId, _bilalId, handedOverAtDeadline.CurrentDepartmentId, due, null));
            context.SaveChanges();
            RecordBreach(context, handedOverAtDeadline, SlaDeadlineType.Resolution, due, due.AddHours(2));

            // Assigned one tick AFTER the deadline: it was still Amina's at the deadline.
            var afterDeadline = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, afterDeadline, Now.AddDays(-11), due, resBreached: true);
            foreach (var current in context.TicketAssignments.Where(a => a.TicketId == afterDeadline.TicketId && a.IsCurrent))
            {
                current.MarkSuperseded(due.AddTicks(1));
            }

            context.TicketAssignments.Add(new TicketAssignment(afterDeadline.TicketId, _bilalId, afterDeadline.CurrentDepartmentId, due.AddTicks(1), null));
            context.SaveChanges();
            RecordBreach(context, afterDeadline, SlaDeadlineType.Resolution, due, due.AddHours(2));
        }

        var report = await ReportAsync();
        Assert.Equal(1, Row(report, _aminaId).SlaBreachEvents);      // only the "after" ticket
        Assert.Equal(1, Row(report, _bilalId).SlaBreachEvents);      // only the "handed over at the deadline" ticket
        Assert.Equal(2, report.Totals.SlaBreachEvents);              // the "ended at the deadline" ticket belongs to nobody
    }

    [Fact]
    public async Task ARowThatIsCurrentIsNeverTreatedAsEnded_EvenWithAStaleEndTime()
    {
        // The Call Center backlog Rollback script flips a retired row back to IsCurrent = 1 with SQL, leaving any end time in place.
        var due = Now.AddDays(-6);
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, ticket, Now.AddDays(-11), due, resBreached: true);
            Release(context, ticket, due.AddHours(-1));                              // ended before the deadline...
            context.Database.ExecuteSqlRaw("UPDATE TicketAssignments SET IsCurrent = 1 WHERE TicketId = {0}", ticket.TicketId);   // ...then re-activated
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, due, due.AddHours(2));
        }

        Assert.Equal(1, Row(await ReportAsync(), _aminaId).SlaBreachEvents);
    }

    // ---------------------------------------------------------------
    // Evaluated = unique clock cycles
    // ---------------------------------------------------------------

    [Fact]
    public async Task ADuplicateHistoryRowForTheSameDeadline_IsOneBreachEvent()
    {
        var due = Now.AddDays(-6);
        using (var context = _db.CreateContext())
        {
            var ticket = NewTicket(context, _aminaId, Now.AddDays(-12));
            AddPeriod(context, ticket, Now.AddDays(-11), due, resBreached: true);
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, due, due);
            RecordBreach(context, ticket, SlaDeadlineType.Resolution, due, due.AddMinutes(3), alreadyBreached: true);   // a stray repeat of the SAME deadline
        }

        var row = Row(await ReportAsync(), _aminaId);
        var details = await DetailsAsync(_aminaId);

        Assert.Equal((1, 1, 1), (row.TicketsWithSlaBreach, row.SlaBreachEvents, row.SlaEvaluated));
        Assert.Single(details.Records, r => r.Status == TeamPerformanceSlaStatuses.Breached);
    }

    [Fact]
    public async Task EveryEvaluatedCycleIsUnique_NoCycleIsBothMetAndBreached_AndEvaluatedEqualsTheDistinctCycles()
    {
        using (var context = _db.CreateContext())
        {
            // Ticket 1: First Response answered late (breach) and Resolution recorded late (breach).
            var late = NewTicket(context, _aminaId, Now.AddDays(-25));
            var lateFrDue = late.CreatedAtUtc.AddHours(4);
            var lateResDue = Now.AddDays(-15);
            AddPeriod(context, late, lateFrDue, lateResDue, frBreached: true, resBreached: true);
            RecordBreach(context, late, SlaDeadlineType.FirstResponse, lateFrDue, lateFrDue);
            FirstResponse(context, late, lateFrDue.AddDays(3));                                          // answered, but late
            RecordBreach(context, late, SlaDeadlineType.Resolution, lateResDue, lateResDue, alreadyBreached: true);
            context.TicketResolutions.Add(new TicketResolution(late.TicketId, ResolutionOutcome.Resolved, "Fixed.", null, null, _aminaId, lateResDue.AddDays(1)));
            context.SaveChanges();

            // Ticket 2: both clocks on time.
            var good = NewTicket(context, _aminaId, Now.AddDays(-20));
            AddPeriod(context, good, good.CreatedAtUtc.AddHours(4), Now.AddDays(-10));
            FirstResponse(context, good, good.CreatedAtUtc.AddHours(1));
            context.TicketResolutions.Add(new TicketResolution(good.TicketId, ResolutionOutcome.Resolved, "Fixed.", null, null, _aminaId, Now.AddDays(-12)));
            context.SaveChanges();

            // Ticket 3: reopened — cycle 1 breached, cycle 2 resolved on time; First Response on time and carried.
            var reopened = NewTicket(context, _aminaId, Now.AddDays(-28));
            var original = AddPeriod(context, reopened, reopened.CreatedAtUtc.AddHours(4), Now.AddDays(-22), resBreached: true);
            FirstResponse(context, reopened, reopened.CreatedAtUtc.AddHours(1));
            RecordBreach(context, reopened, SlaDeadlineType.Resolution, Now.AddDays(-22), Now.AddDays(-22));
            original.EndPeriod(Now.AddDays(-21));
            context.TicketSlaInstances.Add(TicketSlaInstance.OpenReopenCycle(
                reopened.TicketId, (byte)PriorityLevel.Medium, Now.AddDays(-21), original.FirstResponseDueAtUtc, carriedFirstResponseBreached: false, Now.AddDays(-14),
                new AppliedSlaPolicy(SlaClockBasis.BusinessHours, AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 1800)));
            context.SaveChanges();
            context.TicketResolutions.Add(new TicketResolution(reopened.TicketId, ResolutionOutcome.Resolved, "Fixed again.", null, null, _aminaId, Now.AddDays(-16)));
            context.SaveChanges();
        }

        var details = await DetailsAsync(_aminaId);
        var cycles = details.Records.Where(r => r.Status != TeamPerformanceSlaStatuses.Pending).ToList();

        // A cycle is (ticket, clock, deadline). Each appears once, and under exactly one status.
        var keys = cycles.Select(r => (r.TicketId, r.SlaType, r.DueAtUtc)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());

        // Ticket 1: 2 breaches.  Ticket 2: 2 met.  Ticket 3: First Response met (once, not per period), cycle 1 breached, cycle 2 met.
        Assert.Equal((2 + 0 + 1, 0 + 2 + 2), (details.Summary.BreachEvents, details.Summary.Met));
        Assert.Equal(details.Summary.Met + details.Summary.BreachEvents, details.Summary.Evaluated);
        Assert.Equal(cycles.Count, details.Summary.Evaluated);
        Assert.Equal(7, details.Summary.Evaluated);
        Assert.Equal(1, cycles.Count(r => r.SlaType == "First Response" && r.Status == TeamPerformanceSlaStatuses.Met && r.TicketId == cycles.Single(c => c.SlaType == "Resolution" && c.Status == TeamPerformanceSlaStatuses.Breached && c.DueAtUtc == Now.AddDays(-22)).TicketId));
    }

    // ---------------------------------------------------------------
    // The repeated-run guarantee holds in the database, not only in the code
    // ---------------------------------------------------------------

    [Fact]
    public async Task TwoWorkersRacingTheSameBreach_CannotBothCommit_TheUniqueIdempotencyKeyRejectsTheSecond()
    {
        var key = TigerCS.Application.Modules.SlaAndEscalation.Abstractions.SlaBreachIdempotencyKey.For(
            ticketId: 1, SlaDeadlineType.Resolution, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));

        using var first = _db.CreateContext();
        using var second = _db.CreateContext();

        // Both read "not seen yet" before either has committed...
        Assert.True(await new IdempotencyRecordStore(first).TryReserveAsync(key, TigerCS.Domain.Infrastructure.IdempotencyScopes.SlaBreachCheck, Now));
        Assert.True(await new IdempotencyRecordStore(second).TryReserveAsync(key, TigerCS.Domain.Infrastructure.IdempotencyScopes.SlaBreachCheck, Now));

        // ...but only one can commit; the other's whole transaction (flag, history row, escalation) is rolled back.
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        // And a worker arriving later is told it was already processed.
        using var third = _db.CreateContext();
        Assert.False(await new IdempotencyRecordStore(third).TryReserveAsync(key, TigerCS.Domain.Infrastructure.IdempotencyScopes.SlaBreachCheck, Now.AddMinutes(1)));
    }
}
