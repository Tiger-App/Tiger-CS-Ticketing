using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Regression: a ticket resolved inside its SLA must be STORED as Met. The resolution row is staged in the same unit of work as the SLA
/// evaluation, and the real repository cannot read a staged row back, so the evaluation used to find "no resolution", treat the deadline
/// as unsatisfied and never mark the ticket Met (it stayed Running). The fake here hides unsaved rows exactly as the database does.
/// Each clock is also checked independently, and the sticky-Breached rule is kept.
/// </summary>
public class SlaMetPersistenceTests
{
    private const int DepartmentId = 2;
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private sealed class Clock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed record Harness(SlaServiceFixture Sla, TicketLifecycleAppService Service, Ticket Ticket, Guid OwnerId);

    private static async Task<Harness> CreateAsync(DateTime now, DateTime frDue, DateTime resDue, bool hideUnsaved = true)
    {
        var sla = new SlaServiceFixture(
            resolutions: new FakeTicketResolutionRepository { HideUnsavedRows = hideUnsaved }, timeProvider: new Clock(now));
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", DepartmentId, unitReferenceId: 10, contactReferenceId: 20, categoryId: 5,
            (byte)PriorityLevel.High, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);
        var owner = Guid.NewGuid();
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);
        sla.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(owner, DepartmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));
        await sla.SlaInstances.AddAsync(TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, (byte)PriorityLevel.High, CreatedAt, frDue, resDue));

        var service = new TicketLifecycleAppService(
            sla.Tickets, sla.Resolutions, sla.StatusHistory, sla.DepartmentAssignments, sla.UnitOfWork, sla.Audit, sla.BreachProcessor, new Clock(now),
            new FakeTicketPendingRecordRepository(), new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(),
            new Notifications.Fakes.FakeOutboxWriter(), new IdentityAndAccess.Fakes.FakeDepartmentRepository(),
            new FakeDepartmentWorkflowSettingsRepository(), new FakeTicketWorkflowEventRepository(),
            new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), sla.DepartmentAssignments,
                new FakeTicketAssignmentRepository(), sla.Audit),
            sla.DueDates,
            new ReopenEligibilityService(sla.StatusHistory, new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(), ReopenPolicy.Default),
            handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());
        return new Harness(sla, service, ticket, owner);
    }

    private static ResolveTicketRequestDto Resolve() => new("Resolved", "Done.", null, null, RowVersion: []);

    [Fact]
    public async Task ResolvedInsideTheSla_IsStoredAsMet_EvenThoughTheNewResolutionRowIsNotYetReadable()
    {
        var h = await CreateAsync(CreatedAt.AddHours(2), CreatedAt.AddHours(3), CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        var result = await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, Resolve());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(SlaState.Met, h.Ticket.SlaState);
        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.False(instance.ResolutionBreached);
        Assert.False(instance.FirstResponseBreached);
    }

    [Fact]
    public async Task ResolvedAfterTheResolutionDeadline_IsBreached_NeverMet()
    {
        var h = await CreateAsync(CreatedAt.AddHours(8), CreatedAt.AddHours(1), CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, Resolve());

        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);
        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.True(instance.ResolutionBreached);
        Assert.False(instance.FirstResponseBreached);   // each clock judged on its own
    }

    [Fact]
    public async Task MissedFirstResponse_ThenResolvedInTime_StaysBreached_ButResolutionIsNotBreached()
    {
        // First response answered after its due time; resolution comfortably inside its own.
        var h = await CreateAsync(CreatedAt.AddHours(2), CreatedAt.AddHours(1), CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(90));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, Resolve());

        var instance = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        Assert.True(instance.FirstResponseBreached);
        Assert.False(instance.ResolutionBreached);
        Assert.Equal(SlaState.Breached, h.Ticket.SlaState);   // sticky ticket-level summary, as documented
    }

    [Fact]
    public async Task ResolvedExactlyOnTheDeadline_IsStillOnTime_AndMet()
    {
        var h = await CreateAsync(CreatedAt.AddHours(6), CreatedAt.AddHours(3), CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, Resolve());

        Assert.Equal(SlaState.Met, h.Ticket.SlaState);
    }

    [Fact]
    public async Task SystemClosure_ResolvedInsideTheSla_SettlesTheResolutionClockAsMet()
    {
        // Genesys "closed by AI" / document-delivery closures Resolve and Close in one step; the ticket is already Closed in memory when the
        // SLA is evaluated, which used to short-circuit and leave the state at Running.
        var h = await CreateAsync(CreatedAt.AddHours(2), CreatedAt.AddHours(3), CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        var result = await h.Service.CloseForDocumentDeliveryAsync(
            Guid.NewGuid(), h.Ticket.TicketId, ResolutionOutcome.Resolved, "Document delivered", "detail");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Closed, h.Ticket.TicketStatus);
        Assert.Equal(SlaState.Met, h.Ticket.SlaState);
    }

    [Fact]
    public async Task SystemClosure_ResolvedAfterTheResolutionDeadline_NeverClaimsMet_AndDoesNotTouchTheClosedTicket()
    {
        // Recording a breach would mutate a Closed ticket (forbidden); the closure only ever settles Met, so a late one stays as it was.
        var h = await CreateAsync(CreatedAt.AddHours(8), CreatedAt.AddHours(1), CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));

        var result = await h.Service.CloseForDocumentDeliveryAsync(
            Guid.NewGuid(), h.Ticket.TicketId, ResolutionOutcome.Resolved, "Document delivered", "detail");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.NotEqual(SlaState.Met, h.Ticket.SlaState);
        Assert.Empty(h.Sla.Escalations.All);
    }

    [Fact]
    public async Task SystemClosure_AsCancelled_DoesNotClaimMet()
    {
        var h = await CreateAsync(CreatedAt.AddHours(2), CreatedAt.AddHours(3), CreatedAt.AddHours(6));

        await h.Service.CloseForDocumentDeliveryAsync(Guid.NewGuid(), h.Ticket.TicketId, ResolutionOutcome.Cancelled, "Delivery failed", "detail");

        Assert.Equal(SlaState.Running, h.Ticket.SlaState);   // unchanged behaviour: only a Resolved outcome settles the SLA
    }

    [Fact]
    public async Task ReopenAfterMet_StartsTheClockAgain()
    {
        var h = await CreateAsync(CreatedAt.AddHours(2), CreatedAt.AddHours(3), CreatedAt.AddHours(6));
        h.Ticket.RecordFirstHumanResponse(CreatedAt.AddMinutes(30));
        await h.Service.ResolveAsync(h.OwnerId, [Roles.DepartmentEmployee], h.Ticket.TicketId, Resolve());
        Assert.Equal(SlaState.Met, h.Ticket.SlaState);

        h.Ticket.RestartSlaClockForReopen();

        Assert.Equal(SlaState.Running, h.Ticket.SlaState);
    }
}
