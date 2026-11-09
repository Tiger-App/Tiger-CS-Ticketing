using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerReviews.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Notifications.Fakes;

/// <summary>
/// Wires the real notification handlers, the real <see cref="CustomerEmailSender"/>
/// façade and the real <see cref="OutboxDispatcher"/> over in-memory fakes,
/// with the <see cref="FakeEmailSender"/> as the only provider boundary —
/// so no test ever touches SMTP.
/// </summary>
public sealed class NotificationServiceFixture
{
    public FakeOutboxMessageRepository OutboxMessages { get; } = new();
    public FakeNotificationRepository Notifications { get; } = new();
    public FakeNotificationsUnitOfWork UnitOfWork { get; } = new();
    public FakeEmailSender Email { get; } = new();
    public FakeTicketRepository Tickets { get; } = new();
    public FakeTicketRequesterSnapshotRepository Snapshots { get; } = new();
    public FakeTicketInteractionRepository Interactions { get; } = new();
    public FakeTicketResolutionRepository Resolutions { get; } = new();
    public FakeRequestTypeRepository RequestTypes { get; } = new();
    public FakeCategoryRepository Categories { get; } = new();
    public FakeDepartmentRepository Departments { get; } = new();
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTicketSlaInstanceRepository SlaInstances { get; } = new();
    public OutboxDispatchPolicy Policy { get; set; } = OutboxDispatchPolicy.Default;
    public CustomerNotificationPolicy NotificationPolicy { get; set; } = CustomerNotificationPolicy.EnabledDefault;
    public FakeTicketReviewRepository Reviews { get; } = new();

    /// <summary>Production by default — the pre-existing customer behaviour; tests switch to Test mode by replacing it.</summary>
    public CustomerNotificationModePolicy ModePolicy { get; set; } = CustomerNotificationModePolicy.ProductionDefault(PublicBaseUrl);

    public const string PublicBaseUrl = "https://tickets.example.com";

    public FakeTimeProvider Time { get; } = new(new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc));

    public FakeCustomerContactDetailRepository ContactDetails { get; } = new();

    /// <summary>The same resolver production registers: snapshot, Genesys interaction, external snapshot and agent-entered contact details.</summary>
    public CustomerContactResolver CreateContactResolver() => new(Snapshots, Interactions, ContactDetails);

    /// <summary>
    /// A manually entered customer, as the New Ticket wizard creates one: an unverified ticket whose only trace of the customer is the
    /// originating local interaction's phone number. Any email lives solely in the saved contact details for that phone.
    /// </summary>
    public async Task<Ticket> SeedManualCustomerTicketAsync(
        string phone = "+971 50 777 0001", string? savedEmail = "manual.customer@example.com", string ticketNumber = "TG-HO-20261109-0001")
    {
        var department = Departments.AddDepartment("Handover", "HO");
        var ticket = Ticket.CreateUnverified(
            ticketNumber, department.DepartmentId, categoryId: 1, priorityId: 2, "Handover inspection.", Time.GetUtcNow().UtcDateTime,
            manualProjectName: "Tiger Tower", manualUnitNumber: "T-1204");
        await Tickets.AddAsync(ticket);
        await Interactions.AddAsync(TicketInteraction.CreateLocal(
            ticket.TicketId, WellKnownChannels.Phone, phone, Time.GetUtcNow().UtcDateTime, isOriginatingInteraction: true));
        ContactDetails.Save(phone, "Manual Customer", savedEmail);
        return ticket;
    }

    /// <summary>A PACT-verified ticket: the external customer's email is snapshotted on the ticket itself.</summary>
    public async Task<Ticket> SeedExternalCustomerTicketAsync(string? externalEmail, string ticketNumber = "TG-CS-20261109-0002")
    {
        var department = Departments.AddDepartment("Customer Service", "CS");
        var ticket = Ticket.CreateFromExternalLookup(
            ticketNumber, department.DepartmentId, "Pact", "T-77", "U-5", "Tiger Tower", "T-1204", categoryId: 1, priorityId: 2,
            "Service charge enquiry.", Time.GetUtcNow().UtcDateTime, "Omar Saleh", externalEmail);
        await Tickets.AddAsync(ticket);
        return ticket;
    }

    /// <summary>The recording provider wrapped in the real delivery guard — what every production sender sits behind.</summary>
    public IEmailSender CreateGuardedProvider() =>
        new CustomerDeliveryGuardEmailSender(Email, ModePolicy, NullLogger<CustomerDeliveryGuardEmailSender>.Instance);

    public ICustomerEmailSender CreateCustomerEmailSender() =>
        new CustomerEmailSender(CreateGuardedProvider(), NotificationPolicy, ModePolicy, NullLogger<CustomerEmailSender>.Instance);

    public CustomerTicketSubjectResolver CreateSubjectResolver() => new(RequestTypes, Categories);

    public TicketReviewInvitationService CreateInvitationService() =>
        new(Reviews, ModePolicy, Time, NullLogger<TicketReviewInvitationService>.Instance);

    public TicketReviewAppService CreateReviewService() => new(Reviews, Tickets, Time);

    public TicketAcknowledgementHandler CreateHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time,
        ModePolicy, RequestTypes, Categories);

    public TicketResolvedNotificationHandler CreateResolvedHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time,
        ModePolicy, Resolutions, CreateSubjectResolver(), CreateInvitationService());

    public TicketClosedNotificationHandler CreateClosedHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time,
        ModePolicy, Resolutions, CreateSubjectResolver(), CreateInvitationService());

    public TicketReopenedNotificationHandler CreateReopenedHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time, ModePolicy,
        CreateSubjectResolver());

    public TicketAssignedNotificationHandler CreateAssignedHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time, ModePolicy,
        CreateSubjectResolver());

    public TicketInProgressNotificationHandler CreateInProgressHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time, ModePolicy,
        CreateSubjectResolver());

    public FakeTicketPendingRecordRepository PendingRecords { get; } = new();

    public TicketPendingCustomerNotificationHandler CreatePendingCustomerHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time, ModePolicy,
        CreateSubjectResolver(), PendingRecords);

    /// <summary>Staff accounts (email addresses) and the employee directory the internal handlers read.</summary>
    public TigerCS.Tests.Administration.Services.FakeUserAccountManager Accounts { get; } = new();

    public TigerCS.Tests.IdentityAndAccess.Fakes.FakeEmployeeRepository Employees { get; } = new();

    public AgentAssignedNotificationHandler CreateAgentAssignedHandler() => new(
        Tickets, Notifications, CreateGuardedProvider(), Accounts, Employees, Departments, CreateContactResolver(), CreateSubjectResolver(),
        Audit, NotificationPolicy, ModePolicy, Time);

    public CustomerReplyNotificationHandler CreateCustomerReplyHandler() => new(
        Tickets, Notifications, CreateGuardedProvider(), Accounts, Employees, Departments, CreateContactResolver(), CreateSubjectResolver(),
        Audit, NotificationPolicy, ModePolicy, Time);

    /// <summary>Queues a <c>TicketAssigned</c> / <c>TicketAssignedToAgent</c> message as the Outbox writer would.</summary>
    public OutboxMessage EnqueueAssigned(
        string eventType, Ticket ticket, Guid assignedEmployeeId, Guid? previousOwnerEmployeeId = null, Guid? actorEmployeeId = null,
        DateTime? occurredAtUtc = null)
    {
        var occurred = occurredAtUtc ?? Time.GetUtcNow().UtcDateTime;
        var payload = new TicketAssignedEventPayload(
            ticket.TicketId, 1, ticket.ReopenCount, assignedEmployeeId, previousOwnerEmployeeId, actorEmployeeId, (byte)ticket.TicketStatus,
            ticket.CurrentDepartmentId);
        var record = new IdempotencyRecord(
            OutboxEventTypes.OccurrenceIdempotencyKeyFor(eventType, ticket.TicketId, 1, ticket.ReopenCount, Guid.NewGuid().ToString("N")),
            IdempotencyScopes.OutboxDispatch, occurred);
        var message = new OutboxMessage(Guid.NewGuid(), eventType, payload.ToJson(), Guid.NewGuid(), record, occurred);
        OutboxMessages.Messages.Add(message);
        return message;
    }

    /// <summary>Queues a <c>TicketInProgress</c> / <c>TicketPendingCustomer</c> message as the Outbox writer would.</summary>
    public OutboxMessage EnqueueStatusChanged(Ticket ticket, TicketStatus previous, TicketStatus next, DateTime? occurredAtUtc = null)
    {
        var eventType = next == TicketStatus.PendingCustomer ? OutboxEventTypes.TicketPendingCustomer : OutboxEventTypes.TicketInProgress;
        var occurred = occurredAtUtc ?? Time.GetUtcNow().UtcDateTime;
        var payload = new TicketStatusChangedEventPayload(ticket.TicketId, 1, ticket.ReopenCount, (byte)previous, (byte)next);
        var record = new IdempotencyRecord(
            OutboxEventTypes.OccurrenceIdempotencyKeyFor(eventType, ticket.TicketId, 1, ticket.ReopenCount, Guid.NewGuid().ToString("N")),
            IdempotencyScopes.OutboxDispatch, occurred);
        var message = new OutboxMessage(Guid.NewGuid(), eventType, payload.ToJson(), Guid.NewGuid(), record, occurred);
        OutboxMessages.Messages.Add(message);
        return message;
    }

    /// <summary>Queues a <c>CustomerReplyReceived</c> message as the Outbox writer would.</summary>
    public OutboxMessage EnqueueCustomerReply(Ticket ticket, long interactionId, Guid assignedEmployeeId, int messageCount = 1, int latestSequence = 1)
    {
        var occurred = Time.GetUtcNow().UtcDateTime;
        var payload = new CustomerReplyEventPayload(ticket.TicketId, 1, interactionId, assignedEmployeeId, messageCount, latestSequence, occurred);
        var record = new IdempotencyRecord(
            OutboxEventTypes.OccurrenceIdempotencyKeyFor(
                OutboxEventTypes.CustomerReplyReceived, ticket.TicketId, 1, ticket.ReopenCount, $"interaction{interactionId}:seq{latestSequence}"),
            IdempotencyScopes.OutboxDispatch, occurred);
        var message = new OutboxMessage(Guid.NewGuid(), OutboxEventTypes.CustomerReplyReceived, payload.ToJson(), Guid.NewGuid(), record, occurred);
        OutboxMessages.Messages.Add(message);
        return message;
    }

    public OutboxDispatcher CreateDispatcher(params IOutboxEventHandler[] handlers)
    {
        UnitOfWork.Track(OutboxMessages.Messages);

        return new OutboxDispatcher(
            OutboxMessages,
            handlers.Length > 0 ? handlers : [CreateHandler()],
            UnitOfWork,
            Audit,
            Policy,
            Time,
            NullLogger<OutboxDispatcher>.Instance);
    }

    public async Task<Ticket> SeedVerifiedTicketAsync(string contactChannel = "requester@example.com", string ticketNumber = "TG-CS-20260822-0001")
    {
        var department = Departments.AddDepartment("Customer Service", "CS");
        var ticket = Ticket.CreateVerified(
            ticketNumber, department.DepartmentId, unitReferenceId: 1, contactReferenceId: 1,
            categoryId: 1, priorityId: 2, "AC unit not cooling.", Time.GetUtcNow().UtcDateTime);
        await Tickets.AddAsync(ticket);

        await Snapshots.AddAsync(new TicketRequesterSnapshot(
            ticket.TicketId, "1204", "Tiger Tower A", "Tower A", "Residential",
            "Ahmed Al-Farsi", contactChannel, Time.GetUtcNow().UtcDateTime));

        return ticket;
    }

    public async Task<Ticket> SeedUnverifiedTicketAsync(string ticketNumber = "TG-CS-20260822-0002")
    {
        var department = Departments.AddDepartment("Facilities", "FM");
        var ticket = Ticket.CreateUnverified(
            ticketNumber, department.DepartmentId, categoryId: 1, priorityId: 1,
            "Lift stuck between floors.", Time.GetUtcNow().UtcDateTime);
        await Tickets.AddAsync(ticket);
        return ticket;
    }

    /// <summary>A Genesys-sourced ticket: no CRM snapshot, the customer email lives on the originating interaction.</summary>
    public async Task<Ticket> SeedGenesysTicketAsync(string? customerEmail, string? customerName = "Fatima Al-Mansoori", string ticketNumber = "TG-CS-20260822-0003")
    {
        var department = Departments.AddDepartment("Customer Service", "CS");
        var ticket = Ticket.CreateUnclassified(
            ticketNumber, department.DepartmentId, "Enquiry via Genesys.", Time.GetUtcNow().UtcDateTime, null, null);
        await Tickets.AddAsync(ticket);

        await Interactions.AddAsync(TicketInteraction.CreateFromGenesys(
            ticket.TicketId, WellKnownChannels.Phone, "+971500000001", "conv-" + Guid.NewGuid().ToString("N"),
            calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
            interactionStartedAtUtc: null, direction: "inbound", Time.GetUtcNow().UtcDateTime,
            isOriginatingInteraction: true, customerName: customerName, customerEmail: customerEmail));

        return ticket;
    }

    /// <summary>Moves a seeded ticket to Resolved with a current resolution row, the state the resolved/closed handlers read.</summary>
    public async Task<TicketResolution> ResolveAsync(Ticket ticket, ResolutionOutcome outcome = ResolutionOutcome.Resolved, string note = "Technician replaced the thermostat.")
    {
        if (ticket.CurrentOwnerEmployeeId is null)
        {
            ticket.AssignTo(Guid.NewGuid());
        }

        if (ticket.TicketStatus == TicketStatus.Open)
        {
            ticket.ChangeStatus(TicketStatus.InProgress);
        }

        ticket.Resolve(outcome, outcome == ResolutionOutcome.Duplicate ? 1 : null);
        var resolution = new TicketResolution(
            ticket.TicketId, outcome, note, reasonCode: null,
            outcome == ResolutionOutcome.Duplicate ? 1 : null, Guid.NewGuid(), Time.GetUtcNow().UtcDateTime);
        await Resolutions.AddAsync(resolution);
        return resolution;
    }

    public OutboxMessage EnqueueTicketCreated(long ticketId, Guid? correlationId = null, DateTime? occurredAtUtc = null)
    {
        var occurred = occurredAtUtc ?? Time.GetUtcNow().UtcDateTime;
        var record = new IdempotencyRecord(
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, ticketId, OutboxEventTypes.TicketCreatedVersion),
            IdempotencyScopes.OutboxDispatch,
            occurred);

        var message = new OutboxMessage(
            Guid.NewGuid(),
            OutboxEventTypes.TicketCreated,
            new TicketCreatedEventPayload(ticketId, OutboxEventTypes.TicketCreatedVersion).ToJson(),
            correlationId ?? Guid.NewGuid(),
            record,
            occurred);

        OutboxMessages.Messages.Add(message);
        return message;
    }

    public OutboxMessage EnqueueLifecycleEvent(
        string eventType, long ticketId, int reopenCount = 0, Guid? correlationId = null, DateTime? occurredAtUtc = null, string? closureKind = null)
    {
        var occurred = occurredAtUtc ?? Time.GetUtcNow().UtcDateTime;
        var record = new IdempotencyRecord(
            OutboxEventTypes.LifecycleIdempotencyKeyFor(eventType, ticketId, 1, reopenCount),
            IdempotencyScopes.OutboxDispatch,
            occurred);

        var message = new OutboxMessage(
            Guid.NewGuid(),
            eventType,
            new TicketLifecycleEventPayload(ticketId, 1, reopenCount, closureKind).ToJson(),
            correlationId ?? Guid.NewGuid(),
            record,
            occurred);

        OutboxMessages.Messages.Add(message);
        return message;
    }
}

public sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = new(utcNow, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}
