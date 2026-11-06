using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// ADR-0013 / NFR-REL-01's central guarantee, tested at the boundary where it
/// actually has to hold: the ticket and its Outbox message commit together,
/// or neither does.
/// </summary>
public class TicketCreationOutboxAtomicityTests
{
    private sealed record Fixture(
        TicketCreationAppService Service,
        FakeIntakeRecordRepository IntakeRecords,
        FakeUnitReferenceRepository UnitReferences,
        FakeContactReferenceRepository ContactReferences,
        FakeCategoryRepository Categories,
        FakeDepartmentRepository Departments,
        FakeTicketRepository Tickets,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        FakeOutboxWriter Outbox);

    private static Fixture CreateService()
    {
        var intakeRecords = new FakeIntakeRecordRepository();
        var unitReferences = new FakeUnitReferenceRepository();
        var contactReferences = new FakeContactReferenceRepository();
        var categories = new FakeCategoryRepository();
        var priorities = new FakePriorityRepository();
        var departments = new FakeDepartmentRepository();
        var tickets = new FakeTicketRepository();
        var snapshots = new FakeTicketRequesterSnapshotRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var outbox = new FakeOutboxWriter();
        unitOfWork.OutboxWriter = outbox;

        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var assignmentRules = new FakeRequestTypeAssignmentRuleRepository();
        var workflowSettings = new FakeDepartmentWorkflowSettingsRepository();
        var autoAssignment = new TicketAutoAssignmentService(
            assignmentRules, workflowSettings, new FakeUserDepartmentAssignmentRepository(),
            new FakeTicketAssignmentRepository(), audit);

        var service = new TicketCreationAppService(
            intakeRecords, unitReferences, contactReferences, categories, priorities, departments,
            tickets, snapshots, statusHistory, unitOfWork, audit, outbox, sla.DueDates, TimeProvider.System,
            new FakeRequestTypeRepository(), new FakeTicketInteractionRepository(), autoAssignment,
            new FakeWorkflowTemplateRepository());

        return new Fixture(service, intakeRecords, unitReferences, contactReferences, categories, departments, tickets, audit, unitOfWork, outbox);
    }

    private static async Task<(IntakeRecord Record, Guid AgentId)> SeedUnitRelatedIntakeAsync(FakeIntakeRecordRepository repo)
    {
        var agentId = Guid.NewGuid();
        var record = new IntakeRecord(WellKnownChannels.Phone, "+971500000001", null, isUnitRelated: true, "1204", priorityHint: null, agentId, DateTime.UtcNow);
        await repo.AddAsync(record);
        return (record, agentId);
    }

    [Fact]
    public async Task SuccessfulCreation_CommitsTheTicketAndItsOutboxMessageTogether()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var unit = f.UnitReferences.Seed("CRM-UNIT-1001", "1204");
        var contact = f.ContactReferences.Seed(unit.UnitReferenceId, "CRM-CONTACT-2001", "Ahmed Al-Farsi");

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(
                intake.IntakeRecordId, unit.UnitReferenceId, contact.ContactReferenceId, category.CategoryId, 2, "AC not cooling."));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);

        var message = Assert.Single(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
        Assert.Equal(OutboxEventTypes.TicketCreated, message.EventType);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);

        // The payload carries the ticket's real, database-generated id — the
        // write happens after the ticket insert, inside the same transaction.
        var ticket = Assert.Single(f.Tickets.All);
        var payload = TicketCreatedEventPayload.FromJson(message.Payload);
        Assert.NotNull(payload);
        Assert.Equal(ticket.TicketId, payload!.TicketId);

        // Identifiers only — no customer data is duplicated into an
        // operator-visible infrastructure row.
        Assert.DoesNotContain("AC not cooling", message.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ahmed", message.Payload, StringComparison.OrdinalIgnoreCase);

        // "Notification queued" (ADR-0018), written synchronously in the
        // business transaction.
        var queued = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
        Assert.Equal(message.CorrelationId, queued.CorrelationId);
        // Keyed by the ticket (so Ticket Details can list it) and naming the Outbox message it describes.
        Assert.Equal(NotificationAuditActions.NotificationEntityType, queued.EntityType);
        Assert.Equal(ticket.TicketId.ToString(), queued.EntityId);
        Assert.Contains($"OutboxMessageId={message.OutboxMessageId}", queued.AfterValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the guarantee, and the more important one: a business
    /// transaction that rolls back leaves no Outbox message, so a ticket that
    /// does not exist can never acknowledge anything.
    /// </summary>
    [Fact]
    public async Task RolledBackCreation_LeavesNoOutboxMessage()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        // A concurrent request generated the same ticket number first: the
        // first SaveChanges loses the unique-index race and the whole
        // transaction rolls back.
        f.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, 2, "AC not cooling."));

        Assert.Equal(TicketCreationOutcome.TicketNumberCollision, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);

        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
    }

    /// <summary>A ticket-number collision aborts before the Outbox write is even reached — nothing staged, nothing committed.</summary>
    [Fact]
    public async Task TicketNumberCollision_LeavesNoOutboxMessage()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        f.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, 2, "AC not cooling."));

        Assert.Equal(TicketCreationOutcome.TicketNumberCollision, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
    }

    /// <summary>
    /// FR-NOT-01: "email attempted for every ticket". Business-rule change:
    /// an unverified ticket (no customer match linked at creation) has no
    /// requester snapshot to write to, but the event is still enqueued so
    /// the failure is visible rather than the ticket being silently skipped.
    /// </summary>
    [Fact]
    public async Task UnverifiedTicket_AlsoEnqueuesTheAcknowledgementEvent()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Facilities", "FM");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateAsync(
            agentId, new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, PriorityId: 1, "Lift stuck between floors."));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        var message = Assert.Single(f.Outbox.Committed);
        Assert.Equal(OutboxEventTypes.TicketCreated, message.EventType);
        Assert.Contains(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
    }

    /// <summary>
    /// ADR-0014's enqueue-side dedup: one logical event produces at most one
    /// Outbox message, so two tickets get two messages and a repeated key gets
    /// none.
    /// </summary>
    [Fact]
    public async Task DuplicateIdempotencyKey_ProducesNoSecondMessage()
    {
        var f = CreateService();
        var key = OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, 1, 1);

        var first = await f.Outbox.WriteAsync(OutboxEventTypes.TicketCreated, "{}", Guid.NewGuid(), key, DateTime.UtcNow);
        var second = await f.Outbox.WriteAsync(OutboxEventTypes.TicketCreated, "{}", Guid.NewGuid(), key, DateTime.UtcNow);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(f.Outbox.Staged);
    }
}
