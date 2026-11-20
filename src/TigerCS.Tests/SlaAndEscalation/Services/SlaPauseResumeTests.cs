using TigerCS.Tests.Ticketing.Fakes;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// SLA pause/resume (ISSUE-018, SLA-Architecture.md §6/§8) driven through the
/// real lifecycle service over in-memory fakes. Calendar: Sat–Thu 08:00–18:00
/// Asia/Dubai (UTC+4), Friday off.
/// </summary>
public class SlaPauseResumeTests
{
    private const int DepartmentId = 2;

    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private sealed class MutableTime(DateTime utcNow) : TimeProvider
    {
        public DateTime Now { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class Harness
    {
        public required SlaServiceFixture Sla { get; init; }
        public required TicketLifecycleAppService Service { get; init; }
        public required MutableTime Time { get; init; }
        public required IdentityAndAccess.Fakes.FakeDepartmentRepository Departments { get; init; }
        public required Guid OwnerId { get; init; }
        public Ticket Ticket { get; set; } = null!;
    }

    private static Harness CreateHarness(DateTime? now = null)
    {
        var time = new MutableTime(now ?? CreatedAt);
        var sla = new SlaServiceFixture(timeProvider: time);
        var departments = new IdentityAndAccess.Fakes.FakeDepartmentRepository();
        departments.AddDepartment("Customer Service", "CS");
        departments.AddDepartment("Handover", "HO");
        departments.AddDepartment("Collections", "COL");

        var requestTypes = new Ticketing.Fakes.FakeRequestTypeRepository();
        var templates = new Ticketing.Fakes.FakeWorkflowTemplateRepository();
        var outbox = new Notifications.Fakes.FakeOutboxWriter();
        sla.UnitOfWork.OutboxWriter = outbox;

        var service = new TicketLifecycleAppService(
            sla.Tickets, sla.Resolutions, sla.StatusHistory, sla.DepartmentAssignments,
            sla.UnitOfWork, sla.Audit, sla.BreachProcessor, time,
            new Ticketing.Fakes.FakeTicketPendingRecordRepository(),
            requestTypes, templates, outbox, departments,
            new Ticketing.Fakes.FakeDepartmentWorkflowSettingsRepository(),
            new Ticketing.Fakes.FakeTicketWorkflowEventRepository(),
            new TicketAutoAssignmentService(
                new Ticketing.Fakes.FakeRequestTypeAssignmentRuleRepository(),
                new Ticketing.Fakes.FakeDepartmentWorkflowSettingsRepository(),
                sla.DepartmentAssignments, new Ticketing.Fakes.FakeTicketAssignmentRepository(), sla.Audit),
            sla.DueDates,
            new ReopenEligibilityService(sla.StatusHistory, requestTypes, templates, ReopenPolicy.Default),
            sla.PauseService, handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        return new Harness
        {
            Sla = sla, Service = service, Time = time, Departments = departments, OwnerId = Guid.NewGuid()
        };
    }

    /// <summary>
    /// An InProgress ticket with an SLA period on a 24/7 basis (so the
    /// arithmetic is plain wall-clock): FR due +1h, Resolution due +6h.
    /// </summary>
    private static async Task<Harness> SeedAsync(
        PriorityLevel priority = PriorityLevel.High,
        bool? pausesOverride = null,
        SlaClockBasis basis = SlaClockBasis.TwentyFourSeven,
        DateTime? now = null)
    {
        var h = CreateHarness(now);
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", DepartmentId, 10, 20, categoryId: 5, (byte)priority, "AC not cooling", CreatedAt);
        await h.Sla.Tickets.AddAsync(ticket);
        ticket.AssignTo(h.OwnerId);
        ticket.ChangeStatus(TicketStatus.InProgress);
        h.Sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(h.OwnerId, DepartmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));

        await h.Sla.SlaInstances.AddAsync(TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, (byte)priority, CreatedAt, CreatedAt.AddHours(1), CreatedAt.AddHours(6),
            new AppliedSlaPolicy(basis, PausesOnPendingCustomerOverride: pausesOverride)));

        h.Ticket = ticket;
        return h;
    }

    private static Task<TicketMutationResult> GoPendingAsync(Harness h) =>
        h.Service.ChangeStatusAsync(
            h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], PendingReason: "Awaiting documents"));

    private static Task<TicketMutationResult> BackToWorkAsync(Harness h) =>
        h.Service.ChangeStatusAsync(
            h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, new ChangeStatusRequestDto("InProgress", []));

    private static Task<TicketMutationResult> ResolveAsync(Harness h) =>
        h.Service.ResolveAsync(
            h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Done.", null, null, RowVersion: []));

    private static TicketSlaInstance Current(Harness h) =>
        h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId).GetAwaiter().GetResult()!;

    // ---- (b) non-Critical Resolution SLA pauses on Pending Customer and resumes on InProgress ----

    [Fact]
    public async Task EnteringPendingCustomer_PausesTheResolutionClock_AndHoldsTheDeadline()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);

        var result = await GoPendingAsync(h);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(SlaState.Paused, h.Ticket.SlaState);
        var pause = Assert.Single(h.Sla.Pauses.All);
        Assert.True(pause.IsOpen);
        Assert.Equal(CreatedAt.AddHours(2), pause.StartedAtUtc);
        Assert.Equal(SlaPauseReason.PendingCustomer, pause.Reason);
        Assert.Equal(CreatedAt.AddHours(6), pause.ResolutionDueBeforeAtUtc);
        Assert.Equal(CreatedAt.AddHours(6), Current(h).ResolutionDueAtUtc);
        Assert.Equal(CreatedAt.AddHours(1), Current(h).FirstResponseDueAtUtc);
    }

    [Fact]
    public async Task ReturningToInProgress_ResumesTheClock_ExtendsTheDeadline_AndReschedulesTheCheck()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);
        h.Time.Now = CreatedAt.AddHours(4);
        h.Sla.Scheduler.Scheduled.Clear();

        var result = await BackToWorkAsync(h);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(SlaState.Running, h.Ticket.SlaState);
        var pause = Assert.Single(h.Sla.Pauses.All);
        Assert.False(pause.IsOpen);
        Assert.Equal(CreatedAt.AddHours(4), pause.ResumedAtUtc);
        Assert.Equal(CreatedAt.AddHours(6), pause.ResolutionDueBeforeAtUtc);
        Assert.Equal(CreatedAt.AddHours(8), pause.ResolutionDueAfterAtUtc);

        // Due extended by the 2h paused; First Response untouched.
        Assert.Equal(CreatedAt.AddHours(8), Current(h).ResolutionDueAtUtc);
        Assert.Equal(CreatedAt.AddHours(1), Current(h).FirstResponseDueAtUtc);

        // The new deadline is what gets scheduled — via the scheduler abstraction.
        var scheduled = Assert.Single(h.Sla.Scheduler.Scheduled);
        Assert.Equal((h.Ticket.TicketId, SlaDeadlineType.Resolution, CreatedAt.AddHours(8)), scheduled);
    }

    [Fact]
    public async Task EveryPauseAndResume_IsAudited_AndRecordedInTheSlaStateHistory()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);
        h.Time.Now = CreatedAt.AddHours(3);
        await BackToWorkAsync(h);

        var pauseAudit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "PauseSla");
        var resumeAudit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "ResumeSla");
        Assert.Equal(h.OwnerId, pauseAudit.ActorEmployeeId);
        Assert.Equal(h.OwnerId, resumeAudit.ActorEmployeeId);
        Assert.Equal(nameof(TicketSlaPausePeriod), pauseAudit.EntityType);
        Assert.Contains("PendingCustomer", pauseAudit.AfterValue);
        Assert.Contains("\"pausedMinutes\":60.00", resumeAudit.AfterValue);
        Assert.Contains(CreatedAt.AddHours(7).ToString("O"), resumeAudit.AfterValue);

        var slaRows = h.Sla.StatusHistory.Added.Where(r => r.Dimension == TicketStatusDimension.SlaState).ToList();
        Assert.Equal(2, slaRows.Count);
        Assert.Equal(((byte)SlaState.Running, (byte)SlaState.Paused), ((byte)slaRows[0].OldValue!, slaRows[0].NewValue));
        Assert.Equal(((byte)SlaState.Paused, (byte)SlaState.Running), ((byte)slaRows[1].OldValue!, slaRows[1].NewValue));
    }

    [Fact]
    public async Task MultiplePauses_EachExtendTheDeadline_AndTheTotalAccumulates()
    {
        var h = await SeedAsync();

        h.Time.Now = CreatedAt.AddHours(1);
        await GoPendingAsync(h);
        h.Time.Now = CreatedAt.AddHours(2);          // paused 1h
        await BackToWorkAsync(h);
        h.Time.Now = CreatedAt.AddHours(3);
        await GoPendingAsync(h);
        h.Time.Now = CreatedAt.AddHours(5);          // paused 2h
        await BackToWorkAsync(h);

        Assert.Equal(2, h.Sla.Pauses.All.Count);
        Assert.Equal(CreatedAt.AddHours(9), Current(h).ResolutionDueAtUtc);   // 6h + 1h + 2h

        var summary = SlaQueryAppService.SummarizePauses(h.Sla.Pauses.All, h.Time.Now);
        Assert.False(summary.IsCurrentlyPaused);
        Assert.Null(summary.CurrentPauseReason);
        Assert.Equal(180, summary.TotalPausedMinutes);
    }

    [Fact]
    public async Task ThePauseSummary_ReportsAnOpenPause_CountedUpToNow()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);

        var summary = SlaQueryAppService.SummarizePauses(h.Sla.Pauses.All, CreatedAt.AddHours(2).AddMinutes(45));

        Assert.True(summary.IsCurrentlyPaused);
        Assert.Equal("Pending Customer", summary.CurrentPauseReason);
        Assert.Equal(45, summary.TotalPausedMinutes);

        var dto = SlaQueryAppService.BuildSummary(h.Ticket, Current(h), summary);
        Assert.Equal("Paused", dto.SlaState);
        Assert.True(dto.IsCurrentlyPaused);
        Assert.Equal("Pending Customer", dto.CurrentPauseReason);
        Assert.Equal(45, dto.TotalPausedMinutesThisPeriod);
    }

    [Fact]
    public async Task BusinessHoursPeriod_ResumeReWalksTheRemainingBusinessTime()
    {
        // The real due-date service opens a High (business-hours) period at Sun 05:00Z.
        var h = CreateHarness();
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0002", DepartmentId, 10, 20, 5, (byte)PriorityLevel.High, "x", CreatedAt);
        await h.Sla.Tickets.AddAsync(ticket);
        ticket.AssignTo(h.OwnerId);
        ticket.ChangeStatus(TicketStatus.InProgress);
        h.Sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(h.OwnerId, DepartmentId, true, CreatedAt, null));
        h.Ticket = ticket;
        var opened = await h.Sla.DueDates.OpenInitialPeriodAsync(ticket, CreatedAt, h.OwnerId, Guid.NewGuid());
        Assert.Equal(new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc), opened.ResolutionDueAtUtc);

        h.Time.Now = new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc);
        await GoPendingAsync(h);
        h.Time.Now = new DateTime(2026, 8, 23, 8, 0, 0, DateTimeKind.Utc);
        await BackToWorkAsync(h);

        // Paused 2h inside working hours -> due moves 2h of business time later.
        Assert.Equal(new DateTime(2026, 8, 25, 11, 0, 0, DateTimeKind.Utc), Current(h).ResolutionDueAtUtc);
    }

    // ---- (a) Critical never pauses ----

    [Fact]
    public async Task CriticalPriority_NeverPauses_EvenInPendingCustomer()
    {
        var h = await SeedAsync(PriorityLevel.Critical);
        h.Time.Now = CreatedAt.AddHours(2);

        var result = await GoPendingAsync(h);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.PendingCustomer, h.Ticket.TicketStatus);
        Assert.Empty(h.Sla.Pauses.All);
        Assert.Equal(SlaState.Running, h.Ticket.SlaState);
        Assert.Equal(CreatedAt.AddHours(6), Current(h).ResolutionDueAtUtc);

        h.Time.Now = CreatedAt.AddHours(4);
        await BackToWorkAsync(h);
        Assert.Equal(CreatedAt.AddHours(6), Current(h).ResolutionDueAtUtc);
        Assert.DoesNotContain(h.Sla.Audit.Entries, e => e.Action is "PauseSla" or "ResumeSla");
    }

    [Fact]
    public async Task CriticalPriority_StillBreachesWhileInPendingCustomer()
    {
        var h = await SeedAsync(PriorityLevel.Critical);
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);

        h.Time.Now = CreatedAt.AddHours(7);
        var outcome = await h.Sla.CreateBreachDetection().CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, outcome);
        Assert.True(Current(h).ResolutionBreached);
    }

    // ---- idempotency ----

    [Fact]
    public async Task RepeatedSyncWhilePaused_DoesNotOpenASecondPause_OrDoubleCount()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);

        var again = await h.Sla.PauseService.SyncAsync(h.Ticket, h.Time.Now.AddHours(1), h.OwnerId, Guid.NewGuid());

        Assert.Equal(SlaPauseOutcome.AlreadyPaused, again.Outcome);
        Assert.Single(h.Sla.Pauses.All);
        Assert.Single(h.Sla.Audit.Entries, e => e.Action == "PauseSla");
    }

    [Fact]
    public async Task DuplicateResume_IsANoOp_AndDoesNotExtendTwice()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);
        h.Time.Now = CreatedAt.AddHours(4);
        await BackToWorkAsync(h);

        var duplicate = await h.Sla.PauseService.SyncAsync(h.Ticket, CreatedAt.AddHours(5), h.OwnerId, Guid.NewGuid());

        Assert.Equal(SlaPauseOutcome.NoChange, duplicate.Outcome);
        Assert.Equal(CreatedAt.AddHours(8), Current(h).ResolutionDueAtUtc);
        Assert.Single(h.Sla.Audit.Entries, e => e.Action == "ResumeSla");
    }

    [Fact]
    public async Task TicketWithNoSlaPeriod_IsLeftAlone()
    {
        var h = CreateHarness();
        var ticket = Ticket.CreateUnclassified("TG-CS-20260823-0003", DepartmentId, "call", CreatedAt);
        await h.Sla.Tickets.AddAsync(ticket);

        var result = await h.Sla.PauseService.SyncAsync(ticket, CreatedAt, null, Guid.NewGuid());

        Assert.Equal(SlaPauseOutcome.NoSlaPeriod, result.Outcome);
        Assert.Empty(h.Sla.Pauses.All);
    }

    // ---- breaches are never erased; checks during a pause do not breach ----

    [Fact]
    public async Task ADeadlineCheckFiringDuringAPause_DoesNotBreach()
    {
        var h = await SeedAsync();
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(10));
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);

        // The job scheduled for the ORIGINAL due time (+6h) fires while paused.
        h.Time.Now = CreatedAt.AddHours(6);
        var detection = h.Sla.CreateBreachDetection();
        var outcome = await detection.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution);

        Assert.Equal(SlaBreachProcessingOutcome.Paused, outcome);
        Assert.False(Current(h).ResolutionBreached);
        Assert.Equal(SlaState.Paused, h.Ticket.SlaState);
        Assert.Empty(h.Sla.Escalations.All);

        // ...and so does the safety sweep, which finds the candidate (the fake does not
        // filter pauses, the processor does) and records nothing.
        var sweep = await detection.SweepAsync();
        Assert.Equal(0, sweep.BreachesRecorded);
        Assert.False(Current(h).ResolutionBreached);
    }

    [Fact]
    public async Task AfterResume_TheOldJobIsHarmless_AndTheExtendedDeadlineStillBreaches()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);
        h.Time.Now = CreatedAt.AddHours(5);
        await BackToWorkAsync(h);                       // due now +9h
        var detection = h.Sla.CreateBreachDetection();

        h.Time.Now = CreatedAt.AddHours(6);             // the old job's moment
        Assert.Equal(SlaBreachProcessingOutcome.NotBreached,
            await detection.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution));
        Assert.False(Current(h).ResolutionBreached);

        h.Time.Now = CreatedAt.AddHours(9);             // the extended deadline
        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded,
            await detection.CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.Resolution));
        Assert.True(Current(h).ResolutionBreached);
    }

    [Fact]
    public async Task GoingPendingAfterTheDeadlineAlreadyPassed_RecordsTheBreach_AndDoesNotPause()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(7);             // 1h past due, no job has run yet

        var result = await GoPendingAsync(h);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.True(Current(h).ResolutionBreached);
        Assert.Empty(h.Sla.Pauses.All);
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);
        Assert.Equal(CreatedAt.AddHours(6), Current(h).ResolutionDueAtUtc);
    }

    [Fact]
    public async Task AFirstResponseBreach_StillHappensDuringAPause_AndTheResolutionPauseSurvivesIt()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddMinutes(30);
        await GoPendingAsync(h);                        // pending BEFORE any human response

        h.Time.Now = CreatedAt.AddHours(2);             // First Response due was +1h
        var outcome = await h.Sla.CreateBreachDetection().CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, outcome);
        Assert.True(Current(h).FirstResponseBreached);
        Assert.False(Current(h).ResolutionBreached);
        Assert.True((await h.Sla.Pauses.GetOpenAsync(h.Ticket.TicketId))!.IsOpen);

        // Resuming extends only Resolution; First Response is not touched and the sticky Breached state stays.
        h.Time.Now = CreatedAt.AddHours(3);
        await BackToWorkAsync(h);
        Assert.Equal(CreatedAt.AddHours(1), Current(h).FirstResponseDueAtUtc);
        Assert.True(Current(h).FirstResponseBreached);
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);
        Assert.Equal(CreatedAt.AddHours(6).AddMinutes(150), Current(h).ResolutionDueAtUtc);
    }

    // ---- (d) a pause open at resolve / close ends at that instant ----

    [Fact]
    public async Task ResolvingFromPendingCustomer_ClosesThePauseAtThatInstant_AndJudgesAgainstTheExtendedDeadline()
    {
        var h = await SeedAsync();
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(10));
        h.Time.Now = CreatedAt.AddHours(5);
        await GoPendingAsync(h);

        // Resolved 4h later (+9h): past the original +6h, within the extended +10h.
        h.Time.Now = CreatedAt.AddHours(9);
        var result = await ResolveAsync(h);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var pause = Assert.Single(h.Sla.Pauses.All);
        Assert.False(pause.IsOpen);
        Assert.True(pause.EndedByResolution);
        Assert.Equal(CreatedAt.AddHours(9), pause.ResumedAtUtc);
        Assert.Equal(CreatedAt.AddHours(10), pause.ResolutionDueAfterAtUtc);
        Assert.Equal(CreatedAt.AddHours(10), Current(h).ResolutionDueAtUtc);
        Assert.False(Current(h).ResolutionBreached);
        Assert.Equal(SlaState.Met, h.Ticket.SlaState);
        Assert.Null(await h.Sla.Pauses.GetOpenAsync(h.Ticket.TicketId));

        // Nothing is rescheduled for a resolved ticket.
        Assert.DoesNotContain(h.Sla.Scheduler.Scheduled, s => s.DueAtUtc == CreatedAt.AddHours(10));
    }

    [Fact]
    public async Task ResolvingFromPendingCustomer_AfterTheExtendedDeadline_StillRecordsTheBreach()
    {
        var h = await SeedAsync();
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(10));
        h.Time.Now = CreatedAt.AddHours(5);
        await GoPendingAsync(h);

        // Paused 1h -> due +7h; resolved at +8h is late even after the extension.
        h.Time.Now = CreatedAt.AddHours(6);
        await h.Service.ResolveAsync(
            h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Done.", null, null, RowVersion: []));
        Assert.Equal(CreatedAt.AddHours(7), Current(h).ResolutionDueAtUtc);
        Assert.False(Current(h).ResolutionBreached);

        var late = await SeedAsync();
        late.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(10));
        late.Time.Now = CreatedAt.AddHours(5);
        await GoPendingAsync(late);
        late.Time.Now = CreatedAt.AddHours(5).AddMinutes(30);
        await BackToWorkAsync(late);                    // due +6.5h
        late.Time.Now = CreatedAt.AddHours(8);
        await ResolveAsync(late);

        Assert.True(Current(late).ResolutionBreached);
        Assert.Equal(SlaState.Breached, late.Ticket.SlaState);
    }

    [Theory]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    public async Task SyncAtResolvedOrClosed_NeverLeavesADanglingPause(TicketStatus terminal)
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);

        // The system-closure path: straight from Pending Customer to a terminal status.
        typeof(Ticket).GetProperty(nameof(Ticket.TicketStatus))!.SetValue(h.Ticket, terminal);
        var result = await h.Sla.PauseService.SyncAsync(h.Ticket, CreatedAt.AddHours(4), actorEmployeeId: null, Guid.NewGuid());

        Assert.Equal(SlaPauseOutcome.ClosedOutByResolution, result.Outcome);
        Assert.Null(await h.Sla.Pauses.GetOpenAsync(h.Ticket.TicketId));
        Assert.True(h.Sla.Pauses.All.Single().EndedByResolution);
        Assert.Equal(SlaState.Running, h.Ticket.SlaState);
    }

    // ---- reopen starts a new Resolution cycle; no pause crosses it ----

    // ---- per-request-type override ----

    [Fact]
    public async Task RequestTypeOverrideFalse_SuppressesThePause()
    {
        var h = await SeedAsync(pausesOverride: false);
        h.Time.Now = CreatedAt.AddHours(2);

        await GoPendingAsync(h);

        Assert.Empty(h.Sla.Pauses.All);
        Assert.Equal(SlaState.Running, h.Ticket.SlaState);
    }

    [Fact]
    public async Task RequestTypeOverrideTrue_PausesLikeTheGlobalRule()
    {
        var h = await SeedAsync(pausesOverride: true);
        h.Time.Now = CreatedAt.AddHours(2);

        await GoPendingAsync(h);

        Assert.Single(h.Sla.Pauses.All);
    }

    [Fact]
    public async Task RequestTypeOverrideTrue_CannotMakeCriticalPause()
    {
        var h = await SeedAsync(PriorityLevel.Critical, pausesOverride: true);
        h.Time.Now = CreatedAt.AddHours(2);

        await GoPendingAsync(h);

        Assert.Empty(h.Sla.Pauses.All);
    }

    // ---- (c) legacy Pending Third Party ----

    [Fact]
    public async Task LegacyPendingThirdParty_PausesAndResumesWithoutCrashing()
    {
        var h = await SeedAsync();
        h.Time.Now = CreatedAt.AddHours(2);

        // A ticket that was already Pending Third Party before the status was retired.
        typeof(Ticket).GetProperty(nameof(Ticket.TicketStatus))!.SetValue(h.Ticket, TicketStatus.PendingThirdParty);
        var paused = await h.Sla.PauseService.SyncAsync(h.Ticket, h.Time.Now, h.OwnerId, Guid.NewGuid());

        Assert.Equal(SlaPauseOutcome.Paused, paused.Outcome);
        Assert.Equal(SlaPauseReason.PendingThirdPartyLegacy, h.Sla.Pauses.All.Single().Reason);
        Assert.Equal(SlaState.Paused, h.Ticket.SlaState);

        // Its one sanctioned exit, back to InProgress, resumes and extends.
        h.Time.Now = CreatedAt.AddHours(3);
        var back = await BackToWorkAsync(h);

        Assert.Equal(TicketMutationOutcome.Success, back.Outcome);
        Assert.Equal(SlaState.Running, h.Ticket.SlaState);
        Assert.Equal(CreatedAt.AddHours(7), Current(h).ResolutionDueAtUtc);
    }

    [Fact]
    public async Task LegacyPendingThirdParty_WithNoRecordedPause_ResumesQuietly()
    {
        // Entered before pause tracking existed: there is no pause row to close,
        // and the legacy exit must neither throw nor invent a retroactive pause.
        var h = await SeedAsync();
        typeof(Ticket).GetProperty(nameof(Ticket.TicketStatus))!.SetValue(h.Ticket, TicketStatus.PendingThirdParty);
        h.Time.Now = CreatedAt.AddHours(3);

        var back = await BackToWorkAsync(h);

        Assert.Equal(TicketMutationOutcome.Success, back.Outcome);
        Assert.Empty(h.Sla.Pauses.All);
        Assert.Equal(CreatedAt.AddHours(6), Current(h).ResolutionDueAtUtc);
    }

    // ---- Critical-tier scheduling is unaffected ----

    [Fact]
    public async Task FirstResponse_CannotPause_OnceAHumanResponseIsRecorded()
    {
        var h = await SeedAsync();
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(10));
        h.Time.Now = CreatedAt.AddHours(2);
        await GoPendingAsync(h);

        // The Resolution clock pauses; the First Response deadline is exactly as it was.
        Assert.Single(h.Sla.Pauses.All);
        Assert.Equal(CreatedAt.AddHours(1), Current(h).FirstResponseDueAtUtc);
        Assert.False(SlaPauseRules.FirstResponseCanPause);
    }
}
