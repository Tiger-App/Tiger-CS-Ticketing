using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications.Services;
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
/// Every lifecycle operation records exactly the notification events the customer (and, internally, the assignee) should hear about —
/// inside the operation's own transaction, keyed so that retries and repeats never produce a second message, and with nothing sent inline.
/// </summary>
public class TicketNotificationEventsTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    private sealed record LifecycleFixture(
        TicketLifecycleAppService Service, FakeTicketRepository Tickets, FakeTicketResolutionRepository Resolutions,
        FakeAuditEntryWriter Audit, FakeTicketingUnitOfWork UnitOfWork, FakeOutboxWriter Outbox, FakeTicketAssignmentRepository Assignments);

    private static LifecycleFixture CreateLifecycle()
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
        var assignments = new FakeTicketAssignmentRepository();

        var service = new TicketLifecycleAppService(
            tickets, resolutions, statusHistory, departmentAssignments, unitOfWork, audit, sla.BreachProcessor,
            TimeProvider.System,
            new FakeTicketPendingRecordRepository(), new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(),
            outbox, departments, departmentSettings, new FakeTicketWorkflowEventRepository(),
            new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), departmentSettings, departmentAssignments, assignments, audit, outboxWriter: outbox),
            sla.DueDates,
            new ReopenEligibilityService(
                statusHistory, new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(), ReopenPolicy.Default),
            handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(), ticketAssignmentRepository: assignments);

        departments.AddDepartment("Customer Service", "CS");
        departments.AddDepartment("Handover", "HO");

        return new LifecycleFixture(service, tickets, resolutions, audit, unitOfWork, outbox, assignments);
    }

    private static async Task<Ticket> SeedTicketAsync(FakeTicketRepository tickets, Guid? owner = null, TicketStatus status = TicketStatus.Open)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20261106-0001", 2, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", Now);
        await tickets.AddAsync(ticket);
        if (owner is { } o)
        {
            ticket.AssignTo(o);
        }

        if (status == TicketStatus.InProgress)
        {
            ticket.ChangeStatus(TicketStatus.InProgress);
        }

        return ticket;
    }

    private static string[] Events(FakeOutboxWriter outbox) => [.. outbox.Committed.Select(m => m.EventType)];

    // ---------------------------------------------------------------- status changes

    [Fact]
    public async Task ChangeStatus_ToInProgress_QueuesTheInProgressEventWithTheTicket()
    {
        var f = CreateLifecycle();
        var owner = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, owner);

        var result = await f.Service.ChangeStatusAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var message = Assert.Single(f.Outbox.Committed);
        Assert.Equal(OutboxEventTypes.TicketInProgress, message.EventType);
        var payload = TicketStatusChangedEventPayload.FromJson(message.Payload)!;
        Assert.Equal(ticket.TicketId, payload.TicketId);
        Assert.Equal((byte)TicketStatus.Open, payload.PreviousStatus);
        Assert.Equal((byte)TicketStatus.InProgress, payload.NewStatus);
        Assert.DoesNotContain("AC not cooling", message.Payload, StringComparison.OrdinalIgnoreCase);

        // Queued is audited against the ticket, so Ticket Details can show it, and names the Outbox message.
        var queued = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
        Assert.Equal(NotificationAuditActions.NotificationEntityType, queued.EntityType);
        Assert.Equal(ticket.TicketId.ToString(), queued.EntityId);
        Assert.Contains($"OutboxMessageId={message.OutboxMessageId}", queued.AfterValue, StringComparison.Ordinal);
        Assert.Equal(message.CorrelationId, queued.CorrelationId);
    }

    [Fact]
    public async Task ChangeStatus_ToPendingCustomer_QueuesTheWaitingForCustomerEvent()
    {
        var f = CreateLifecycle();
        var owner = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, owner, TicketStatus.InProgress);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("PendingCustomer", [], "Waiting for the tenancy contract"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var message = Assert.Single(f.Outbox.Committed);
        Assert.Equal(OutboxEventTypes.TicketPendingCustomer, message.EventType);
        // The agent's reason is never serialised into the Outbox row.
        Assert.DoesNotContain("tenancy", message.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeStatus_BackToInProgressAfterWaiting_QueuesADistinctInProgressEvent()
    {
        var f = CreateLifecycle();
        var owner = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, owner, TicketStatus.InProgress);
        await f.Service.ChangeStatusAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("PendingCustomer", [], "Need documents"));

        var result = await f.Service.ChangeStatusAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal([OutboxEventTypes.TicketPendingCustomer, OutboxEventTypes.TicketInProgress], Events(f.Outbox));
        Assert.Equal(2, f.Outbox.ReservedKeys.Count);
    }

    [Fact]
    public async Task ChangeStatus_ConcurrencyConflict_LeavesNoEventBehind()
    {
        var f = CreateLifecycle();
        var owner = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, owner);
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.ChangeStatusAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.ReservedKeys);
    }

    [Fact]
    public async Task ChangeStatus_RejectedTransition_QueuesNothing()
    {
        var f = CreateLifecycle();
        var owner = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, owner, TicketStatus.InProgress);

        // InProgress → InProgress is not a transition; a repeated API call is refused before anything is written.
        var result = await f.Service.ChangeStatusAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
    }

    // ---------------------------------------------------------------- resolution and closure

    [Fact]
    public async Task Resolve_WithCancelledOutcome_QueuesTheResolvedEvent_WhichTheHandlerRendersAsCancelled()
    {
        var f = CreateLifecycle();
        var owner = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, owner, TicketStatus.InProgress);

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new ResolveTicketRequestDto("Cancelled", "Customer withdrew the request.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal([OutboxEventTypes.TicketResolved], Events(f.Outbox));
    }

    [Fact]
    public async Task HumanResolveThenClose_QueuesOneEventPerOperation()
    {
        var f = CreateLifecycle();
        var owner = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, owner, TicketStatus.InProgress);

        await f.Service.ResolveAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new ResolveTicketRequestDto("Resolved", "Fixed.", null, null, []));
        await f.Service.CloseAsync(owner, [Roles.CsAgent], ticket.TicketId, new CloseTicketRequestDto([]));

        // Two distinct operations, two events; whether both become emails is decided at dispatch (the Resolved email is skipped when
        // the ticket is already Closed by then — see CustomerLifecycleEmailHandlerTests).
        Assert.Equal([OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketClosed], Events(f.Outbox));
    }

    [Fact]
    public async Task AiCompletionClosure_QueuesOnlyTheClosedEvent()
    {
        var f = CreateLifecycle();
        var ticket = await SeedTicketAsync(f.Tickets);

        var result = await f.Service.CloseForAiCompletionAsync(Guid.NewGuid(), ticket.TicketId, "Closed by AI", "ConversationId=conv-1");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        // One write, one email: Resolved → Closed in the same transaction is told to the customer once, as "closed (resolved)".
        Assert.Equal([OutboxEventTypes.TicketClosed], Events(f.Outbox));
    }

    [Fact]
    public async Task DocumentDeliveryClosure_QueuesOnlyTheClosedEvent()
    {
        var f = CreateLifecycle();
        var ticket = await SeedTicketAsync(f.Tickets);

        var result = await f.Service.CloseForDocumentDeliveryAsync(
            Guid.NewGuid(), ticket.TicketId, ResolutionOutcome.Resolved, "Document delivered", "DocumentType=NOC");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal([OutboxEventTypes.TicketClosed], Events(f.Outbox));
    }

    [Fact]
    public async Task CustomerInactivityClosure_QueuesOnlyTheClosedEvent()
    {
        var f = CreateLifecycle();
        var ticket = await SeedTicketAsync(f.Tickets);

        var result = await f.Service.CloseForCustomerInactivityAsync(Guid.NewGuid(), ticket.TicketId, "Closed – customer inactive");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal([OutboxEventTypes.TicketClosed], Events(f.Outbox));
        Assert.True(Assert.Single(f.Resolutions.Added).ClosedForCustomerInactivity);
    }

    [Fact]
    public async Task AiOnlyTimeoutClosure_QueuesOnlyTheClosedEvent_MarkedAsTheTimeLimitClosure()
    {
        var f = CreateLifecycle();
        var ticket = await SeedTicketAsync(f.Tickets);

        var result = await f.Service.CloseForAiOnlyTimeoutAsync(Guid.NewGuid(), ticket.TicketId, "AI Only – Auto Closed After 18 Hours", "Handling=AI");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal([OutboxEventTypes.TicketClosed], Events(f.Outbox));
        Assert.Equal(ClosureKinds.AiOnlyTimeout, TicketLifecycleEventPayload.FromJson(f.Outbox.Committed[0].Payload)!.ClosureKind);
        Assert.Contains(
            OutboxEventTypes.LifecycleIdempotencyKeyFor(OutboxEventTypes.TicketClosed, ticket.TicketId, 1, 0), f.Outbox.ReservedKeys);
    }

    [Fact]
    public async Task AiOnlyTimeoutClosure_RetriedForTheSameTicket_QueuesNothingTwice()
    {
        var f = CreateLifecycle();
        var ticket = await SeedTicketAsync(f.Tickets);
        await f.Service.CloseForAiOnlyTimeoutAsync(Guid.NewGuid(), ticket.TicketId, "AI Only – Auto Closed After 18 Hours", "Handling=AI");

        var again = await f.Service.CloseForAiOnlyTimeoutAsync(Guid.NewGuid(), ticket.TicketId, "AI Only – Auto Closed After 18 Hours", "Handling=AI");

        Assert.Equal(TicketMutationOutcome.TicketClosed, again.Outcome);
        Assert.Single(f.Outbox.Committed);
    }

    [Fact]
    public async Task EveryClosureRule_NamesItselfInTheClosedEvent()
    {
        var f = CreateLifecycle();
        var ai = await SeedTicketAsync(f.Tickets);
        await f.Service.CloseForAiCompletionAsync(Guid.NewGuid(), ai.TicketId, "Closed by AI", "ConversationId=c");
        var inactive = Ticket.CreateVerified("TG-CS-20261106-0003", 2, 10, 20, 5, 2, "x", Now);
        await f.Tickets.AddAsync(inactive);
        await f.Service.CloseForCustomerInactivityAsync(Guid.NewGuid(), inactive.TicketId, "No reply");

        Assert.Equal(
            [ClosureKinds.AiCompletion, ClosureKinds.CustomerInactivity],
            f.Outbox.Committed.Select(m => TicketLifecycleEventPayload.FromJson(m.Payload)!.ClosureKind!).ToArray());
    }

    [Fact]
    public async Task SystemClosure_RepeatedForAClosedTicket_QueuesNothingTwice()
    {
        var f = CreateLifecycle();
        var ticket = await SeedTicketAsync(f.Tickets);
        await f.Service.CloseForAiCompletionAsync(Guid.NewGuid(), ticket.TicketId, "Closed by AI", "ConversationId=conv-1");

        // A duplicate Genesys "closedByAi" event: refused before any write.
        var again = await f.Service.CloseForAiCompletionAsync(Guid.NewGuid(), ticket.TicketId, "Closed by AI", "ConversationId=conv-1");

        Assert.Equal(TicketMutationOutcome.TicketClosed, again.Outcome);
        Assert.Single(f.Outbox.Committed);
    }

    // ---------------------------------------------------------------- assignment (manual)

    private sealed record AssignmentFixture(
        TicketAssignmentAppService Service, FakeTicketRepository Tickets, FakeAuditEntryWriter Audit, FakeTicketingUnitOfWork UnitOfWork,
        FakeOutboxWriter Outbox, FakeUserDepartmentAssignmentRepository Members)
    {
        /// <summary>Makes the employee an active member of department 2, the seeded tickets' department.</summary>
        public Guid Member()
        {
            var employee = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(employee, 2, true, DateTime.UtcNow, null));
            return employee;
        }
    }

    private static AssignmentFixture CreateAssignment()
    {
        var tickets = new FakeTicketRepository();
        var assignments = new FakeTicketAssignmentRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository { EveryUserIsResponsible = true };
        var departments = new FakeDepartmentRepository();
        departments.AddDepartment("Customer Service", "CS");
        departments.AddDepartment("Handover", "HO");
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var outbox = new FakeOutboxWriter();
        unitOfWork.OutboxWriter = outbox;
        var settings = new FakeDepartmentWorkflowSettingsRepository();
        var auto = new TicketAutoAssignmentService(new FakeRequestTypeAssignmentRuleRepository(), settings, departmentAssignments, assignments, audit, outboxWriter: outbox);

        var service = new TicketAssignmentAppService(
            tickets, assignments, departmentAssignments, departments, unitOfWork, audit, TimeProvider.System, settings, auto,
            workflowEventRepository: new FakeTicketWorkflowEventRepository(), outboxWriter: outbox);

        return new AssignmentFixture(service, tickets, audit, unitOfWork, outbox, departmentAssignments);
    }

    [Fact]
    public async Task ManualAssign_FirstOwner_QueuesTheCustomerEventAndTheAgentEvent()
    {
        var f = CreateAssignment();
        var manager = Guid.NewGuid();
        var agent = f.Member();
        var ticket = await SeedTicketAsync(f.Tickets);

        var result = await f.Service.AssignAsync(manager, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(agent, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal([OutboxEventTypes.TicketAssigned, OutboxEventTypes.TicketAssignedToAgent], Events(f.Outbox));

        var customer = TicketAssignedEventPayload.FromJson(f.Outbox.Committed[0].Payload)!;
        Assert.Equal(agent, customer.AssignedEmployeeId);
        Assert.Null(customer.PreviousOwnerEmployeeId);
        Assert.Equal(manager, customer.ActorEmployeeId);
        Assert.Equal((byte)TicketStatus.Open, customer.TicketStatus);

        var internalQueued = Assert.Single(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationQueued && e.AfterValue!.Contains("EventType=TicketAssignedToAgent", StringComparison.Ordinal));
        Assert.Contains("Audience=Internal", internalQueued.AfterValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualReassign_ToAnotherAgent_QueuesOnlyTheAgentEvent()
    {
        var f = CreateAssignment();
        var manager = Guid.NewGuid();
        var first = f.Member();
        var second = f.Member();
        var ticket = await SeedTicketAsync(f.Tickets, first, TicketStatus.InProgress);

        var result = await f.Service.AssignAsync(
            manager, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(second, [], TicketTransferReason.UnableToResolve, "Balancing"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        // Internal routing: the customer still has "someone on it"; only the new assignee is told.
        var message = Assert.Single(f.Outbox.Committed);
        Assert.Equal(OutboxEventTypes.TicketAssignedToAgent, message.EventType);
        var payload = TicketAssignedEventPayload.FromJson(message.Payload)!;
        Assert.Equal(second, payload.AssignedEmployeeId);
        Assert.Equal(first, payload.PreviousOwnerEmployeeId);
    }

    [Fact]
    public async Task ManualAssign_ToTheCurrentOwnerAgain_IsRejected_AndQueuesNothing()
    {
        var f = CreateAssignment();
        var manager = Guid.NewGuid();
        var agent = f.Member();
        var ticket = await SeedTicketAsync(f.Tickets, agent);

        var result = await f.Service.AssignAsync(manager, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(agent, []));

        // Reassigning to the person who already owns the ticket is refused with a clear outcome; nothing is written or notified.
        Assert.Equal(TicketMutationOutcome.AlreadyAssignedToEmployee, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
    }

    [Fact]
    public async Task ManualAssign_ConcurrencyConflict_LeavesNoEventBehind()
    {
        var f = CreateAssignment();
        var ticket = await SeedTicketAsync(f.Tickets);
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.AssignAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(f.Member(), []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.ReservedKeys);
    }

    // ---------------------------------------------------------------- the writer's own rules

    private static (TicketNotificationEvents Writer, FakeOutboxWriter Outbox, FakeAuditEntryWriter Audit) CreateWriter()
    {
        var outbox = new FakeOutboxWriter();
        var audit = new FakeAuditEntryWriter();
        return (new TicketNotificationEvents(outbox, audit), outbox, audit);
    }

    private static Ticket NewTicket(DateTime createdAt) =>
        Ticket.CreateVerified("TG-CS-20261106-0002", 1, 10, 20, categoryId: 5, priorityId: 2, "Lift noise", createdAt);

    private static void GiveId(Ticket ticket, long id) =>
        typeof(Ticket).GetProperty(nameof(Ticket.TicketId))!.SetValue(ticket, id);

    [Fact]
    public async Task Assignment_InTheCreationTransaction_TellsOnlyTheAgent()
    {
        var (writer, outbox, _) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);

        // Same instant as CreatedAtUtc = the creation transaction. The acknowledgement already says the request is being looked after.
        await writer.QueueAssignedAsync(ticket, previousOwnerEmployeeId: null, agent, actorEmployeeId: null, Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Equal([OutboxEventTypes.TicketAssignedToAgent], outbox.Staged.Select(m => m.EventType).ToArray());
    }

    [Fact]
    public async Task SelfAssignment_TellsOnlyTheCustomer()
    {
        var (writer, outbox, _) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);

        await writer.QueueAssignedAsync(ticket, previousOwnerEmployeeId: null, agent, actorEmployeeId: agent, Guid.NewGuid(), Now.AddMinutes(5), CancellationToken.None);

        Assert.Equal([OutboxEventTypes.TicketAssigned], outbox.Staged.Select(m => m.EventType).ToArray());
    }

    [Fact]
    public async Task Assignment_OnAResolvedTicket_TellsOnlyTheAgent()
    {
        var (writer, outbox, _) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        var next = Guid.NewGuid();

        await writer.QueueAssignedAsync(ticket, previousOwnerEmployeeId: null, next, actorEmployeeId: Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(5), CancellationToken.None);

        Assert.Equal([OutboxEventTypes.TicketAssignedToAgent], outbox.Staged.Select(m => m.EventType).ToArray());
    }

    [Fact]
    public async Task Assignment_UnsavedTicket_RecordsNothing()
    {
        var (writer, outbox, _) = CreateWriter();
        var ticket = NewTicket(Now);

        await writer.QueueAssignedAsync(ticket, null, Guid.NewGuid(), null, Guid.NewGuid(), Now.AddMinutes(5), CancellationToken.None);

        Assert.Empty(outbox.Staged);
    }

    [Fact]
    public async Task StatusChange_ToAStatusTheCustomerIsNotToldAbout_RecordsNothing()
    {
        var (writer, outbox, _) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);

        await writer.QueueStatusChangedAsync(ticket, TicketStatus.PendingThirdParty, TicketStatus.Open, null, Guid.NewGuid(), Now, CancellationToken.None);
        await writer.QueueStatusChangedAsync(ticket, TicketStatus.InProgress, TicketStatus.InProgress, null, Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Empty(outbox.Staged);
    }

    [Fact]
    public async Task SameOccurrence_RecordedTwice_ProducesOneMessage()
    {
        var (writer, outbox, audit) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);
        var correlation = Guid.NewGuid();

        await writer.QueueStatusChangedAsync(ticket, TicketStatus.Open, TicketStatus.InProgress, null, correlation, Now.AddMinutes(1), CancellationToken.None);
        await writer.QueueStatusChangedAsync(ticket, TicketStatus.Open, TicketStatus.InProgress, null, correlation, Now.AddMinutes(1), CancellationToken.None);

        Assert.Single(outbox.Staged);
        Assert.Single(audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
    }

    private static TicketInteraction Interaction(long ticketId, long id, Guid? handledBy = null)
    {
        var interaction = TicketInteraction.CreateFromGenesys(
            ticketId, WellKnownChannels.Website, "+971500000001", "conv-" + id, null, null, null, null, null, Now, "inbound", Now,
            isOriginatingInteraction: true, customerName: "Layla", customerEmail: "layla@example.com");
        typeof(TicketInteraction).GetProperty(nameof(TicketInteraction.TicketInteractionId))!.SetValue(interaction, id);
        if (handledBy is { } h)
        {
            typeof(TicketInteraction).GetProperty(nameof(TicketInteraction.HandledByUserId))!.SetValue(interaction, h);
        }

        return interaction;
    }

    private static TicketInteractionMessage CustomerMessage(long interactionId, int sequence) =>
        new(interactionId, sequence, InteractionMessageSender.Customer, "Layla", null, Now.AddMinutes(sequence), $"message {sequence}");

    [Fact]
    public async Task CustomerReply_OnATicketAPersonIsWorking_TellsTheOwnerOnce()
    {
        var (writer, outbox, audit) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);
        var owner = Guid.NewGuid();
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);
        var interaction = Interaction(42, 7);

        await writer.QueueCustomerReplyAsync(ticket, interaction, [CustomerMessage(7, 3), CustomerMessage(7, 4)], null, Guid.NewGuid(), Now, CancellationToken.None);
        // The same transcript delivered again (a duplicate Genesys event) lands on the same position.
        await writer.QueueCustomerReplyAsync(ticket, interaction, [CustomerMessage(7, 3), CustomerMessage(7, 4)], null, Guid.NewGuid(), Now, CancellationToken.None);

        var message = Assert.Single(outbox.Staged);
        Assert.Equal(OutboxEventTypes.CustomerReplyReceived, message.EventType);
        var payload = CustomerReplyEventPayload.FromJson(message.Payload)!;
        Assert.Equal(owner, payload.AssignedEmployeeId);
        Assert.Equal(2, payload.MessageCount);
        Assert.Equal(4, payload.LatestSequence);
        Assert.DoesNotContain("message 3", message.Payload, StringComparison.Ordinal);
        Assert.Single(audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
    }

    [Fact]
    public async Task CustomerReply_WhileTheAiStillHandlesAnOpenTicket_TellsNobody()
    {
        var (writer, outbox, _) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);
        ticket.AssignTo(Guid.NewGuid()); // an automatic pre-assignment nobody has acted on; the ticket is still Open

        await writer.QueueCustomerReplyAsync(ticket, Interaction(42, 7), [CustomerMessage(7, 1)], null, Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Empty(outbox.Staged);
    }

    [Fact]
    public async Task CustomerReply_OnAnUnownedOrClosedTicket_TellsNobody()
    {
        var (writer, outbox, _) = CreateWriter();
        var unowned = NewTicket(Now);
        GiveId(unowned, 42);
        await writer.QueueCustomerReplyAsync(unowned, Interaction(42, 7, Guid.NewGuid()), [CustomerMessage(7, 1)], null, Guid.NewGuid(), Now, CancellationToken.None);

        var closed = NewTicket(Now);
        GiveId(closed, 43);
        closed.CloseForAiCompletion();
        await writer.QueueCustomerReplyAsync(closed, Interaction(43, 8, Guid.NewGuid()), [CustomerMessage(8, 1)], null, Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Empty(outbox.Staged);
    }

    [Fact]
    public async Task CustomerReply_OnAnOpenTicketAHumanAgentHolds_TellsTheOwner()
    {
        var (writer, outbox, _) = CreateWriter();
        var ticket = NewTicket(Now);
        GiveId(ticket, 42);
        var owner = Guid.NewGuid();
        ticket.AssignTo(owner);

        await writer.QueueCustomerReplyAsync(ticket, Interaction(42, 7, handledBy: owner), [CustomerMessage(7, 1)], null, Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Single(outbox.Staged, m => m.EventType == OutboxEventTypes.CustomerReplyReceived);
    }
}
