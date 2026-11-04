using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Recording the First Human Response (MVP-API-Contracts.md §5.2, ISSUE-019)
/// — the only event that satisfies the First Response SLA, and the point at
/// which a late one is finalized as a breach.
/// </summary>
public class SlaFirstResponseAppServiceTests
{
    private const int TicketDepartmentId = 2;
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstResponseDue = CreatedAt.AddHours(1);
    private static readonly DateTime ResolutionDue = CreatedAt.AddHours(24);

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private sealed record Harness(
        SlaServiceFixture Sla, SlaFirstResponseAppService Service, Ticket Ticket, Guid OwnerId, FakeTimeProvider Clock);

    private static async Task<Harness> CreateAsync(DateTime nowUtc, bool openSlaPeriod = true)
    {
        var clock = new FakeTimeProvider(nowUtc);
        var sla = new SlaServiceFixture(timeProvider: clock);

        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", TicketDepartmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)PriorityLevel.High, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);

        var ownerId = Guid.NewGuid();
        ticket.AssignTo(ownerId);
        ticket.ChangeStatus(TicketStatus.InProgress);

        if (openSlaPeriod)
        {
            await sla.SlaInstances.AddAsync(TicketSlaInstance.OpenInitialPeriod(
                ticket.TicketId, (byte)PriorityLevel.High, CreatedAt, FirstResponseDue, ResolutionDue));
        }

        return new Harness(sla, sla.CreateFirstResponseService(), ticket, ownerId, clock);
    }

    private static RecordFirstResponseRequestDto Request(DateTime? occurredAtUtc = null, string source = "Manual") =>
        new(source, occurredAtUtc, RowVersion: []);

    // -----------------------------------------------------------------
    // Recording, on time
    // -----------------------------------------------------------------

    [Fact]
    public async Task Owner_CanRecordTheFirstHumanResponse()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal(CreatedAt.AddMinutes(20), h.Ticket.FirstHumanResponseAtUtc);
        Assert.Equal(CreatedAt.AddMinutes(20), result.Response!.FirstHumanResponseAtUtc);
    }

    /// <summary>The summary returned carries the real stored due dates, not placeholders.</summary>
    [Fact]
    public async Task RecordingReturnsTheTicketsRealSlaSummary()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(FirstResponseDue, result.Response!.FirstResponseDueAtUtc);
        Assert.Equal(ResolutionDue, result.Response.ResolutionDueAtUtc);
        Assert.False(result.Response.FirstResponseBreached);

        // MVP-Implementation-Backlog.md §0.2 — pause/resume is not built, and
        // the approved §5.1 shape reports so honestly.
        Assert.False(result.Response.IsCurrentlyPaused);
        Assert.Null(result.Response.CurrentPauseReason);
        Assert.Equal(0, result.Response.TotalPausedMinutesThisPeriod);
    }

    /// <summary>An explicit moment overrides server "now" — the field a Genesys-driven call would supply (§5.2).</summary>
    [Fact]
    public async Task AnExplicitOccurredAt_IsUsedInsteadOfServerNow()
    {
        var h = await CreateAsync(CreatedAt.AddHours(3));
        var actualEngagement = CreatedAt.AddMinutes(10);

        await h.Service.RecordAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request(actualEngagement));

        Assert.Equal(actualEngagement, h.Ticket.FirstHumanResponseAtUtc);

        // Recorded three hours late but engaged inside the target, so the
        // deadline was met — the engagement moment is what counts.
        Assert.False((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.FirstResponseBreached);
    }

    /// <summary>SLA-Architecture.md §16 Example E — a call answered before the ticket existed still satisfies the SLA.</summary>
    [Fact]
    public async Task AGenesysCallAnswerBeforeTicketCreation_SatisfiesTheDeadline()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(2));

        var result = await h.Service.RecordAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId,
            Request(CreatedAt.AddMinutes(-2), source: "GenesysCallAnswer"));

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal(CreatedAt.AddMinutes(-2), h.Ticket.FirstHumanResponseAtUtc);
        Assert.False((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.FirstResponseBreached);
    }

    /// <summary>Backdating guard: a hand-recorded response cannot predate the ticket (only a Genesys call answer may).</summary>
    [Fact]
    public async Task AManualResponseBeforeTicketCreation_IsRejected_AndRecordsNothing()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(30));

        var result = await h.Service.RecordAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request(CreatedAt.AddMinutes(-1)));

        Assert.Equal(SlaOperationOutcome.InvalidRequest, result.Outcome);
        Assert.Null(h.Ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task AResponseInTheFuture_IsRejected_AndRecordsNothing()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(30));

        var result = await h.Service.RecordAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request(CreatedAt.AddHours(5)));

        Assert.Equal(SlaOperationOutcome.InvalidRequest, result.Outcome);
        Assert.Null(h.Ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task AnUnknownSource_IsRejected()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request(source: "SmokeSignal"));

        Assert.Equal(SlaOperationOutcome.InvalidRequest, result.Outcome);
        Assert.Null(h.Ticket.FirstHumanResponseAtUtc);
    }

    // -----------------------------------------------------------------
    // Write-once (MVP-ERD.md §2.10, MVP-API-Contracts.md §5.2's 409)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ASecondRecording_IsRefusedAndDoesNotOverwriteTheFirst()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));
        await h.Service.RecordAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        var second = await h.Service.RecordAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request(CreatedAt.AddMinutes(1)));

        Assert.Equal(SlaOperationOutcome.FirstResponseAlreadyRecorded, second.Outcome);
        Assert.Equal(CreatedAt.AddMinutes(20), h.Ticket.FirstHumanResponseAtUtc);
    }

    // -----------------------------------------------------------------
    // A late response finalizes the breach (§5.2's "Domain events" note)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ALateResponse_FinalizesTheBreachAndRaisesAutomaticLevel2()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(30));

        var result = await h.Service.RecordAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.True((await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!.FirstResponseBreached);
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);

        var escalation = Assert.Single(h.Sla.Escalations.All);
        Assert.Equal(EscalationTriggerType.AutoBreach, escalation.TriggerType);
        Assert.Equal((byte)EscalationLevel.Level2, escalation.Level);
    }

    /// <summary>
    /// The response path and the background-job path share one idempotency
    /// key, so a response arriving moments after a job already flagged the
    /// breach records nothing twice.
    /// </summary>
    [Fact]
    public async Task ALateResponseAfterTheJobAlreadyFlaggedIt_RecordsNothingTwice()
    {
        var h = await CreateAsync(FirstResponseDue.AddMinutes(30));

        // The scheduled job gets there first.
        await h.Sla.CreateBreachDetection().CheckDeadlineAsync(h.Ticket.TicketId, SlaDeadlineType.FirstResponse);
        var escalationsAfterJob = h.Sla.Escalations.All.Count;
        var breachAuditsAfterJob = h.Sla.Audit.Entries.Count(e => e.Action == "DetectSlaBreach");

        await h.Service.RecordAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(escalationsAfterJob, h.Sla.Escalations.All.Count);
        Assert.Equal(breachAuditsAfterJob, h.Sla.Audit.Entries.Count(e => e.Action == "DetectSlaBreach"));
        Assert.Single(h.Sla.Escalations.All);
    }

    [Fact]
    public async Task AnOnTimeResponse_RaisesNoEscalation()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        await h.Service.RecordAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Empty(h.Sla.Escalations.All);
        Assert.NotEqual(SlaState.Breached, h.Ticket.SlaState);
    }

    // -----------------------------------------------------------------
    // Authorization matrix (§5.2's "Agent and above")
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    public async Task SupervisoryRoles_MayRecordOnAnotherAgentsTicket(string role)
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(Guid.NewGuid(), [role], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    /// <summary>Chairman/CEO (and Reporting User) are read-only: they cannot record a first response, even on a ticket they own.</summary>
    [Theory]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task ReadOnlyRoles_AreRefused(string role)
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(Guid.NewGuid(), [role], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
    }

    /// <summary>A Department Head may record for a ticket in a department they belong to, and not otherwise.</summary>
    [Fact]
    public async Task DepartmentHead_IsScopedToTheirOwnDepartment()
    {
        var head = Guid.NewGuid();

        var inside = await CreateAsync(CreatedAt.AddMinutes(20));
        inside.Sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(head, TicketDepartmentId, true, CreatedAt, null));
        var permitted = await inside.Service.RecordAsync(head, [Roles.DepartmentHead], inside.Ticket.TicketId, Request());

        var outside = await CreateAsync(CreatedAt.AddMinutes(20));
        outside.Sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(head, TicketDepartmentId + 99, true, CreatedAt, null));
        var refused = await outside.Service.RecordAsync(head, [Roles.DepartmentHead], outside.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, permitted.Outcome);
        Assert.Equal(SlaOperationOutcome.Forbidden, refused.Outcome);
    }

    /// <summary>An agent who neither owns the ticket nor holds a supervisory role is refused.</summary>
    [Fact]
    public async Task AnUnrelatedAgent_MayNotRecord()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(Guid.NewGuid(), [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        Assert.Null(h.Ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task ReportingUser_MayNotRecord()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(Guid.NewGuid(), [Roles.ReportingUser], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
    }

    /// <summary>ADR-0024's central override reaches this endpoint without <c>SlaRoleSets</c> naming the role.</summary>
    [Fact]
    public async Task SystemAdministrator_MayRecordThroughTheCentralOverride()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.DoesNotContain(Roles.SystemAdministrator, SlaRoleSets.RecordFirstResponse);
    }

    // -----------------------------------------------------------------
    // Business rules that must remain enforced
    // -----------------------------------------------------------------

    [Fact]
    public async Task ClosedTicket_RejectsFirstResponseRecording()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));
        h.Ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        h.Ticket.Close();

        var result = await h.Service.RecordAsync(h.OwnerId, [Roles.CsManager], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.TicketClosed, result.Outcome);
        Assert.Null(h.Ticket.FirstHumanResponseAtUtc);
        Assert.Equal(0, h.Sla.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task UnknownTicket_ReturnsNotFound()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));

        var result = await h.Service.RecordAsync(h.OwnerId, [Roles.CsManager], ticketId: 9999, Request());

        Assert.Equal(SlaOperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task LostConcurrencyRace_IsReportedAsAConflict()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));
        h.Sla.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await h.Service.RecordAsync(h.OwnerId, [Roles.CsManager], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(0, h.Sla.UnitOfWork.TransactionsCommitted);
    }

    /// <summary>A provisional ticket has no SLA period yet, so there is nothing to breach — but the response itself is still recordable.</summary>
    [Fact]
    public async Task TicketWithoutAnSlaPeriod_StillRecordsTheResponse()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20), openSlaPeriod: false);

        var result = await h.Service.RecordAsync(h.OwnerId, [Roles.CsManager], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.NotNull(h.Ticket.FirstHumanResponseAtUtc);
        Assert.Null(result.Response!.FirstResponseDueAtUtc);
        Assert.Empty(h.Sla.Escalations.All);
    }

    // -----------------------------------------------------------------
    // Audit (ADR-0018)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Recording_WritesAnAuditEntryAttributedToTheActor()
    {
        var h = await CreateAsync(CreatedAt.AddMinutes(20));
        var actorId = Guid.NewGuid();

        await h.Service.RecordAsync(actorId, [Roles.CsManager], h.Ticket.TicketId, Request());

        var audit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "RecordFirstResponse");
        Assert.Equal(actorId, audit.ActorEmployeeId);
        Assert.Contains("Manual", audit.AfterValue!, StringComparison.Ordinal);
        Assert.Contains("null", audit.BeforeValue!, StringComparison.Ordinal);
    }
}
