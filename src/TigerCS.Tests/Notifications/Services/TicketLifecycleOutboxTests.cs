using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// <see cref="TicketLifecycleAppService"/> records a customer-facing Outbox
/// event for resolve, close and reopen — inside the operation's own
/// transaction, so the event exists exactly when the state change does.
/// Nothing is sent here: delivery is the dispatcher's job.
/// </summary>
public class TicketLifecycleOutboxTests
{
    private sealed record Fixture(
        TicketLifecycleAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketResolutionRepository Resolutions,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        FakeOutboxWriter Outbox,
        FakeDepartmentRepository Departments);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var resolutions = new FakeTicketResolutionRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository { EveryUserIsResponsible = true };
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var outbox = new FakeOutboxWriter();
        unitOfWork.OutboxWriter = outbox;
        var sla = new SlaServiceFixture(tickets, resolutions, statusHistory, departmentAssignments, audit, unitOfWork);
        var departments = new FakeDepartmentRepository();
        var departmentSettings = new FakeDepartmentWorkflowSettingsRepository();

        var service = new TicketLifecycleAppService(
            tickets, resolutions, statusHistory, departmentAssignments, unitOfWork, audit, sla.BreachProcessor,
            TimeProvider.System,
            new FakeTicketPendingRecordRepository(), new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(),
            outbox, departments, departmentSettings, new FakeTicketWorkflowEventRepository(),
            new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), departmentSettings, departmentAssignments,
                new FakeTicketAssignmentRepository(), audit),
            sla.DueDates,
            new ReopenEligibilityService(
                statusHistory, new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(), ReopenPolicy.Default), handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        // Two departments so the seeded tickets' department 2 really exists —
        // Reopen validates its target against the directory.
        departments.AddDepartment("Customer Service", "CS");
        departments.AddDepartment("Handover", "HO");

        return new Fixture(service, tickets, resolutions, audit, unitOfWork, outbox, departments);
    }

    private static async Task<(Ticket Ticket, Guid Owner)> SeedInProgressTicketAsync(Fixture f)
    {
        var owner = Guid.NewGuid();
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0010", 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);
        return (ticket, owner);
    }

    /// <summary>Resolve then close — Reopen is Closed-only, and closing is what writes the lifecycle row its window is measured from.</summary>
    private static async Task<TicketMutationResult> ResolveAndCloseAsync(Fixture f, Ticket ticket, Guid owner)
    {
        await ResolveAsync(f, ticket, owner);
        return await f.Service.CloseAsync(owner, [Roles.CsAgent], ticket.TicketId, new CloseTicketRequestDto([]));
    }

    private static Task<TicketMutationResult> ResolveAsync(Fixture f, Ticket ticket, Guid owner) =>
        f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, []));

    [Fact]
    public async Task Resolve_CommitsATicketResolvedEventWithTheTicket()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedInProgressTicketAsync(f);

        var result = await ResolveAsync(f, ticket, owner);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var message = Assert.Single(f.Outbox.Committed);
        Assert.Equal(OutboxEventTypes.TicketResolved, message.EventType);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        var payload = TicketLifecycleEventPayload.FromJson(message.Payload)!;
        Assert.Equal(ticket.TicketId, payload.TicketId);
        Assert.Equal(0, payload.ReopenCount);
        Assert.Contains(
            OutboxEventTypes.LifecycleIdempotencyKeyFor(OutboxEventTypes.TicketResolved, ticket.TicketId, 1, 0),
            f.Outbox.ReservedKeys);
        Assert.Contains(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationQueued
            && e.AfterValue!.Contains("EventType=TicketResolved", StringComparison.Ordinal));
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task Close_CommitsATicketClosedEvent()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedInProgressTicketAsync(f);
        await ResolveAsync(f, ticket, owner);

        var result = await f.Service.CloseAsync(owner, [Roles.CsAgent], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(
            [OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketClosed],
            f.Outbox.Committed.Select(m => m.EventType).ToArray());
    }

    [Fact]
    public async Task Reopen_CommitsATicketReopenedEventKeyedByTheNewCycle()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedInProgressTicketAsync(f);
        await ResolveAndCloseAsync(f, ticket, owner);

        var result = await f.Service.ReopenAsync(
            owner, [Roles.CsAgent], ticket.TicketId, new ReopenTicketRequestDto("Customer called back", 2, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var reopened = Assert.Single(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketReopened);
        Assert.Equal(1, TicketLifecycleEventPayload.FromJson(reopened.Payload)!.ReopenCount);
        Assert.Contains(
            OutboxEventTypes.LifecycleIdempotencyKeyFor(OutboxEventTypes.TicketReopened, ticket.TicketId, 1, 1),
            f.Outbox.ReservedKeys);
    }

    [Fact]
    public async Task ResolveCloseReopenResolve_ProducesADistinctResolvedEventPerCycle()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedInProgressTicketAsync(f);

        await ResolveAndCloseAsync(f, ticket, owner);
        await f.Service.ReopenAsync(owner, [Roles.CsAgent], ticket.TicketId, new ReopenTicketRequestDto("Again", 2, []));

        // Reopen clears the owner, so the second resolve comes from whoever
        // picks the work up — here the same employee, re-assigned.
        ticket.AssignTo(owner);
        var second = await ResolveAsync(f, ticket, owner);

        Assert.Equal(TicketMutationOutcome.Success, second.Outcome);
        Assert.Equal(2, f.Outbox.Committed.Count(m => m.EventType == OutboxEventTypes.TicketResolved));
        Assert.Equal(
            [
                OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketClosed,
                OutboxEventTypes.TicketReopened, OutboxEventTypes.TicketResolved
            ],
            f.Outbox.Committed.Select(m => m.EventType).ToArray());
    }

    [Fact]
    public async Task ConcurrencyConflictOnResolve_LeavesNoEventBehind()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedInProgressTicketAsync(f);
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await ResolveAsync(f, ticket, owner);

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
        Assert.Empty(f.Outbox.ReservedKeys);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    [Fact]
    public async Task ConcurrencyConflictOnClose_LeavesNoEventBehind()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedInProgressTicketAsync(f);
        await ResolveAsync(f, ticket, owner);
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 2;

        var result = await f.Service.CloseAsync(owner, [Roles.CsAgent], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketClosed);
    }

    [Fact]
    public async Task RejectedTransition_WritesNoEvent()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedInProgressTicketAsync(f);

        // Close before resolve is refused by the domain.
        var result = await f.Service.CloseAsync(owner, [Roles.CsAgent], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.NotYetResolved, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
    }
}
