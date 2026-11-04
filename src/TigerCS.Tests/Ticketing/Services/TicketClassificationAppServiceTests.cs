using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The Unclassified → Classified transition.
///
/// <para>
/// A ticket created from a Genesys inquiry exists before anyone has read the
/// request: its department is known, its category is not. These prove the
/// four things that state depends on — the same ticket is classified in
/// place, the classification is write-once, a category may not silently
/// re-route the ticket, and the SLA clock starts here rather than against
/// the provisional priority the ticket was created with.
/// </para>
/// </summary>
public class TicketClassificationAppServiceTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Classification happens 15 minutes after the inquiry arrived — the gap the SLA-start rule is about.</summary>
    private static readonly DateTime ClassifiedAt = new(2026, 9, 10, 8, 15, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        TicketClassificationAppService Service,
        FakeTicketRepository Tickets,
        FakeCategoryRepository Categories,
        FakePriorityRepository Priorities,
        FakeRequestTypeRepository RequestTypes,
        FakeWorkflowTemplateRepository WorkflowTemplates,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        SlaServiceFixture Sla);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var categories = new FakeCategoryRepository();
        var priorities = new FakePriorityRepository();
        var requestTypes = new FakeRequestTypeRepository();
        var workflowTemplates = new FakeWorkflowTemplateRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository { EveryUserIsResponsible = true };
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var service = new TicketClassificationAppService(
            tickets, categories, priorities, requestTypes, workflowTemplates, departmentAssignments,
            statusHistory, unitOfWork, audit, sla.DueDates, new FakeTimeProvider(ClassifiedAt), handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        return new Fixture(
            service, tickets, categories, priorities, requestTypes, workflowTemplates,
            departmentAssignments, statusHistory, audit, unitOfWork, sla);
    }

    /// <summary>
    /// Exactly what ingestion produces: department resolved, nothing else
    /// decided — no category, no request type, no priority — and deliberately
    /// no SLA period.
    /// </summary>
    private static async Task<Ticket> SeedUnclassifiedTicketAsync(Fixture f, int departmentId = 2)
    {
        var ticket = Ticket.CreateUnclassified(
            "TG-CS-20260910-0001", departmentId,
            "Customer asked about an NOC", CreatedAt);
        await f.Tickets.AddAsync(ticket);
        // A CS Agent works only the tickets assigned to them: the classifying agent owns it (department classification keeps the owner).
        ticket.AssignTo(Guid.NewGuid());
        return ticket;
    }

    private static ClassifyTicketRequestDto Request(int categoryId, byte priorityId = (byte)PriorityLevel.High, int? requestTypeId = null) =>
        new(categoryId, priorityId, requestTypeId, RowVersion: [1, 2, 3, 4, 5, 6, 7, 8]);

    // ---- The transition itself ----

    [Fact]
    public async Task ClassifyAsync_UpdatesTheSameTicket_AndNeverCreatesASecondOne()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId, "NOC Request");

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);

        // The load-bearing assertion: one ticket, before and after. A
        // customer who started one conversation keeps one ticket, with its
        // number, department and history intact.
        var stored = Assert.Single(f.Tickets.All);
        Assert.Equal(ticket.TicketId, stored.TicketId);
        Assert.Equal("TG-CS-20260910-0001", stored.TicketNumber);
        Assert.Equal(CreatedAt, stored.CreatedAtUtc);

        Assert.True(stored.IsClassified);
        Assert.Equal(category.CategoryId, stored.CategoryId);
        // The ticket's first real priority — it had none until now.
        Assert.Equal((byte)PriorityLevel.High, stored.PriorityId);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
        Assert.Contains(f.Audit.Written, w => w.Action == "ClassifyTicket" && w.EntityType == "Ticket");
    }

    [Fact]
    public async Task ClassifyAsync_IsWhereTheSlaClockStarts_ForATicketThatNeverHadOne()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        // While unclassified the ticket deliberately had no SLA period: the
        // policy is chosen by priority, and it had no priority at all.
        Assert.Null(ticket.PriorityId);
        Assert.Equal(SlaState.NotApplicable, ticket.SlaState);
        Assert.Empty(f.Sla.SlaInstances.All);

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(SlaState.Running, ticket.SlaState);

        var instance = Assert.Single(f.Sla.SlaInstances.All);
        Assert.Equal(ticket.TicketId, instance.TicketId);
        // Selected from the agent's real priority — the only one this ticket
        // has ever had.
        Assert.Equal((byte)PriorityLevel.High, instance.PriorityId);

        // The dimension genuinely moved, so it is on the history like every
        // other dimension change.
        Assert.Contains(f.StatusHistory.Added, h =>
            h.Dimension == TicketStatusDimension.SlaState
            && h.OldValue == (byte)SlaState.NotApplicable
            && h.NewValue == (byte)SlaState.Running);
    }

    [Fact]
    public async Task ClassifyAsync_StartsTheBusinessSlaAtTheClassificationMoment_NotRetroactivelyAtCreation()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        var instance = Assert.Single(f.Sla.SlaInstances.All);
        // An SLA policy is a commitment to resolve a KNOWN request within a
        // target. Between 08:00 and 08:15 nobody knew the request, the
        // priority, or therefore the policy — so there was no commitment to
        // measure, and backdating would charge those 15 minutes against a
        // target chosen after the fact.
        Assert.Equal(ClassifiedAt, instance.PeriodStartAtUtc);
        Assert.NotEqual(ticket.CreatedAtUtc, instance.PeriodStartAtUtc);
        Assert.True(instance.ResolutionDueAtUtc > ClassifiedAt);
    }

    [Fact]
    public async Task FirstResponse_StaysMeasurableBeforeClassification_AndClassifyingDoesNotDisturbIt()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        // 08:03 — the agent replies to the customer while the ticket is still
        // Unclassified. FirstHumanResponseAtUtc lives on the TICKET, not on
        // an SLA period, so "how fast did the customer reach a human" is
        // recorded with its real timestamp even though no SLA period exists.
        var respondedAt = CreatedAt.AddMinutes(3);
        ticket.RecordFirstHumanResponse(respondedAt);
        Assert.Equal(respondedAt, ticket.FirstHumanResponseAtUtc);
        Assert.Empty(f.Sla.SlaInstances.All);

        await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        // Classification opens the business/resolution clock and leaves the
        // already-measured human response exactly as it was.
        Assert.Equal(respondedAt, ticket.FirstHumanResponseAtUtc);
        Assert.Equal(ClassifiedAt, Assert.Single(f.Sla.SlaInstances.All).PeriodStartAtUtc);
    }

    [Fact]
    public async Task ClassifyAsync_WithARequestType_PinsThePublishedWorkflowVersion()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedPending(workflowId: 100));
        var requestType = f.RequestTypes.Add(new RequestType(
            ticket.CurrentDepartmentId, "NOC Request", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId,
            Request(category.CategoryId, requestTypeId: requestType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(requestType.RequestTypeId, ticket.RequestTypeId);
        // Pinned exactly as a ticket classified at creation would have been.
        Assert.Equal(template.WorkflowTemplateId, ticket.WorkflowTemplateId);
    }

    // ---- What classification refuses ----

    [Fact]
    public async Task ClassifyAsync_ATicketThatAlreadyHasACategory_IsRefused_ToEveryoneExceptItsAssignedCsAgent()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260910-0002", departmentId: 2, categoryId: 7,
            priorityId: (byte)PriorityLevel.Medium, "AC not cooling", CreatedAt);
        await f.Tickets.AddAsync(ticket);
        ticket.AssignTo(Guid.NewGuid());   // a CS Agent works only the tickets assigned to them
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        // Anyone but the CS Agent the ticket is assigned to (who may correct it - see DefaultClassificationHumanHandledTests) is refused.
        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.AlreadyClassified, result.Outcome);
        Assert.Equal(7, ticket.CategoryId);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Empty(f.Sla.SlaInstances.All);
    }

    /// <summary>MVP-API-Contracts.md section 3.4 / ISSUE-023: a bare priority decrease is never written; it must go through an approved downgrade request.</summary>
    [Fact]
    public async Task ClassifyAsync_APriorityDecreaseOnAClassifiedTicket_IsRefusedAsDowngradeRequiresApproval()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260910-0003", departmentId: 2, categoryId: 7,
            priorityId: (byte)PriorityLevel.High, "AC not cooling", CreatedAt);
        await f.Tickets.AddAsync(ticket);
        ticket.AssignTo(Guid.NewGuid());   // a CS Agent works only the tickets assigned to them
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId,
            Request(category.CategoryId, (byte)PriorityLevel.Low));

        Assert.Equal(TicketMutationOutcome.DowngradeRequiresApproval, result.Outcome);
        Assert.Equal((byte)PriorityLevel.High, ticket.PriorityId);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task ClassifyAsync_ACategoryFromAnotherDepartment_IsRefused_BecauseItWouldReRouteTheTicket()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f, departmentId: 2);
        // The department was settled by the inquiry, before the ticket
        // existed. Classification names the request; it does not transfer.
        var foreignCategory = f.Categories.Seed(departmentId: 9, name: "Leasing Renewal");

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(foreignCategory.CategoryId));

        Assert.Equal(TicketMutationOutcome.CategoryDepartmentMismatch, result.Outcome);
        Assert.False(ticket.IsClassified);
        Assert.Null(ticket.PriorityId);
        Assert.Equal(2, ticket.CurrentDepartmentId);
        Assert.Equal(SlaState.NotApplicable, ticket.SlaState);
    }

    [Fact]
    public async Task ClassifyAsync_AnInactiveCategory_IsRefused()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var retired = f.Categories.Seed(ticket.CurrentDepartmentId, "Retired", isActive: false);

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(retired.CategoryId));

        Assert.Equal(TicketMutationOutcome.CategoryNotFound, result.Outcome);
        Assert.False(ticket.IsClassified);
    }

    [Fact]
    public async Task ClassifyAsync_APriorityThatDoesNotExist_IsRefused_BeforeAnySlaPolicyIsSelected()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId, priorityId: 99));

        Assert.Equal(TicketMutationOutcome.PriorityNotFound, result.Outcome);
        Assert.False(ticket.IsClassified);
        Assert.Empty(f.Sla.SlaInstances.All);
    }

    [Fact]
    public async Task ClassifyAsync_AClosedTicket_IsRefused()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();

        var result = await f.Service.ClassifyAsync(
            ticket.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.False(ticket.IsClassified);
    }

    [Fact]
    public async Task ClassifyAsync_ByAnEmployeeOutsideTheTicketsDepartment_IsForbidden()
    {
        var f = CreateService();
        var ticket = await SeedUnclassifiedTicketAsync(f);
        var category = f.Categories.Seed(ticket.CurrentDepartmentId);

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.TicketId, Request(category.CategoryId));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.False(ticket.IsClassified);
    }

    [Fact]
    public async Task ClassifyAsync_AnUnknownTicket_IsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticketId: 4242, Request(1));

        Assert.Equal(TicketMutationOutcome.NotFound, result.Outcome);
    }
}
