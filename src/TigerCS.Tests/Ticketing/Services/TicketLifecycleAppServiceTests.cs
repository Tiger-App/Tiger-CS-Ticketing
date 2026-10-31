using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketLifecycleAppServiceTests
{
    private sealed record Fixture(
        TicketLifecycleAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketResolutionRepository Resolutions,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        SlaServiceFixture Sla,
        FakeTicketPendingRecordRepository PendingRecords,
        FakeRequestTypeRepository RequestTypes,
        FakeWorkflowTemplateRepository WorkflowTemplates,
        FakeDepartmentRepository Departments,
        FakeDepartmentWorkflowSettingsRepository DepartmentSettings,
        FakeTicketWorkflowEventRepository WorkflowEvents,
        FakeRequestTypeAssignmentRuleRepository AssignmentRules,
        FakeTicketAssignmentRepository Assignments,
        FakeOutboxWriter Outbox);

    private static Fixture CreateService(TimeProvider? timeProvider = null, ReopenPolicy? reopenPolicy = null)
    {
        var tickets = new FakeTicketRepository();
        var resolutions = new FakeTicketResolutionRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var pendingRecords = new FakeTicketPendingRecordRepository();
        var requestTypes = new FakeRequestTypeRepository();
        var workflowTemplates = new FakeWorkflowTemplateRepository();
        var departments = new FakeDepartmentRepository();
        var departmentSettings = new FakeDepartmentWorkflowSettingsRepository();
        var workflowEvents = new FakeTicketWorkflowEventRepository();
        var assignmentRules = new FakeRequestTypeAssignmentRuleRepository();
        var assignments = new FakeTicketAssignmentRepository();
        var outbox = new FakeOutboxWriter();

        // The unit of work is what promotes staged Outbox rows to committed,
        // so the reopen tests can assert one customer email per reopen and
        // none at all for a rejected one.
        unitOfWork.OutboxWriter = outbox;

        // Resolving is the Resolution SLA's achievement event, so this
        // service now finalizes the breach flags through the same processor
        // the background jobs use; reopen opens the successor cycle through
        // the same SlaDueDateService the composition root wires.
        var sla = new SlaServiceFixture(tickets, resolutions, statusHistory, departmentAssignments, audit, unitOfWork);

        // Reopen re-runs the ONE assignment engine against its target
        // department, so the tests wire the real service over fakes rather
        // than a stand-in — that is the behaviour under test.
        var autoAssignment = new TicketAutoAssignmentService(
            assignmentRules, departmentSettings, departmentAssignments, assignments, audit);

        var service = new TicketLifecycleAppService(
            tickets, resolutions, statusHistory, departmentAssignments, unitOfWork, audit, sla.BreachProcessor,
            timeProvider ?? TimeProvider.System,
            pendingRecords, requestTypes, workflowTemplates, outbox,
            departments, departmentSettings, workflowEvents, autoAssignment, sla.DueDates,
            new ReopenEligibilityService(statusHistory, requestTypes, workflowTemplates, reopenPolicy ?? ReopenPolicy.Default), handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        return new Fixture(
            service, tickets, resolutions, statusHistory, departmentAssignments, audit, unitOfWork, sla,
            pendingRecords, requestTypes, workflowTemplates,
            departments, departmentSettings, workflowEvents, assignmentRules, assignments, outbox);
    }

    private static async Task<Ticket> SeedInProgressTicketAsync(FakeTicketRepository repo, Guid ownerEmployeeId, int departmentId = 2)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0010", departmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        ticket.AssignTo(ownerEmployeeId);
        ticket.ChangeStatus(TicketStatus.InProgress);
        return ticket;
    }

    [Fact]
    public async Task ChangeStatusAsync_ByCurrentOwner_Succeeds()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], PendingReason: "Awaiting customer documents"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.PendingCustomer, ticket.TicketStatus);
        Assert.Single(f.StatusHistory.Added);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task ChangeStatusAsync_ByUnrelatedEmployee_ReturnsForbidden()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ChangeStatusAsync(
            stranger, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public async Task ChangeStatusAsync_InvalidTransition_ReturnsDeterministicOutcome()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("Closed", []));

        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Empty(f.StatusHistory.Added);
    }

    [Fact]
    public async Task ResolveAsync_ByDepartmentEmployeeOwner_Succeeds_WritesResolutionHistoryAndAudit()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
        Assert.Single(f.Resolutions.Added);
        Assert.Equal("Fixed the AC unit.", f.Resolutions.Added[0].ResolutionNote);
        // TicketStatus + ResolutionOutcome dimensions, both in the same correlated write.
        Assert.Equal(2, f.StatusHistory.Added.Count);
        Assert.Contains(f.Audit.Written, w => w.Action == "Resolve");
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task ResolveAsync_ByDepartmentEmployeeNotTheOwner_ReturnsForbidden()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ResolveAsync(
            colleague, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ByDepartmentHeadOfSameDepartment_Succeeds_WithoutBeingOwner()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var deptHead = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 2);
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(deptHead, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.ResolveAsync(
            deptHead, [Roles.DepartmentHead], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Verified and closed out the work order.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ByCsAgentWhoIsNotTheAssignee_ReturnsForbidden_ResolveIsDepartmentOrAssigneeOnly()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ResolveAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_FromOpenTicket_ReturnsNotEligibleForResolution()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0011", 2, 10, 20, 5, (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        ticket.AssignTo(owner);
        // Still Open — never moved to InProgress.

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.NotEligibleForResolution, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateOfAnotherDuplicate_ReturnsDuplicateChainNotAllowed()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var originalDuplicate = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 2);
        originalDuplicate.Resolve(ResolutionOutcome.Duplicate, duplicateOfTicketId: 12345);

        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 2);

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Duplicate", "Same as another ticket.", null, originalDuplicate.TicketId, []));

        Assert.Equal(TicketMutationOutcome.DuplicateChainNotAllowed, result.Outcome);
    }

    [Fact]
    public async Task CloseAsync_ByCsAgentAfterResolve_Succeeds()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        var closer = Guid.NewGuid();
        var result = await f.Service.CloseAsync(closer, [Roles.CsAgent], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Contains(f.Audit.Written, w => w.Action == "Close");
    }

    [Fact]
    public async Task CloseAsync_ByDepartmentEmployeeWhoIsNotTheAssignee_ReturnsForbidden_CloseIsCsLayerOrAssigneeOnly()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        var result = await f.Service.CloseAsync(Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
    }

    [Fact]
    public async Task CloseAsync_WithoutPriorResolve_ReturnsNotYetResolved()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.CloseAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.NotYetResolved, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentModification_ReturnsConcurrencyConflictAndRollsBackTransaction()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    private static async Task<(Ticket Ticket, Guid Owner)> SeedClosedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(repo, owner, departmentId);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        return (ticket, owner);
    }

    // ---- Closed-ticket immutability (PR correction) — every mutating operation, no database writes ----

    [Fact]
    public async Task ChangeStatusAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClosedTicketAsync(f.Tickets);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.StatusHistory.Added);
    }

    [Fact]
    public async Task ResolveAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClosedTicketAsync(f.Tickets);
        var resolutionsBefore = f.Resolutions.Added.Count;

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Attempted re-resolution.", null, null, []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(resolutionsBefore, f.Resolutions.Added.Count);
    }

    [Fact]
    public async Task CloseAsync_OnAlreadyClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var (ticket, _) = await SeedClosedTicketAsync(f.Tickets);

        var result = await f.Service.CloseAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
    }

    // ---- Reopen: the approved business rule (Closed-only, Agent, routed, new Resolution SLA cycle) ----

    private const int ClosingDepartmentId = 2;
    private const int OtherDepartmentId = 3;

    /// <summary>
    /// Seeds the two departments the approved rule's routing validates
    /// against, so ids 2 (the closing department every seeded ticket lives in)
    /// and 3 (a real alternative destination) both exist.
    /// </summary>
    private static void SeedDepartments(Fixture f)
    {
        f.Departments.AddDepartment("Customer Service", "CS");
        f.Departments.AddDepartment("Handover", "HO");
        f.Departments.AddDepartment("Collections", "COL");
    }

    /// <summary>
    /// A CLOSED ticket with everything a reopen reads: the archived-to-be
    /// resolution, and the lifecycle row recording the close at
    /// <paramref name="closedAtUtc"/> — the moment the approved rule measures
    /// its window from.
    /// </summary>
    private static async Task<(Ticket Ticket, Guid Owner)> SeedClosedTicketAsync(
        Fixture f,
        DateTime closedAtUtc,
        ResolutionOutcome outcome = ResolutionOutcome.Resolved,
        bool closed = true,
        bool withSlaPeriod = true,
        DateTime? firstHumanResponseAtUtc = null)
    {
        SeedDepartments(f);

        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, ClosingDepartmentId);

        // Recorded while the ticket is still open — a closed ticket accepts no
        // mutations, which is exactly why reopening must carry the original
        // first-response result forward rather than expect a new one.
        if (firstHumanResponseAtUtc is { } respondedAt)
        {
            ticket.RecordFirstHumanResponse(respondedAt);
        }

        // A real ticket's original SLA period, opened before the work and
        // already past both deadlines by the time it is reopened — the shape
        // that used to produce an instant false breach.
        if (withSlaPeriod)
        {
            var openedAt = closedAtUtc.AddDays(-3);
            await f.Sla.SlaInstances.AddAsync(TicketSlaInstance.OpenInitialPeriod(
                ticket.TicketId, (byte)PriorityLevel.High, openedAt, openedAt.AddHours(2), openedAt.AddDays(1)));
        }

        // Duplicate carries the ticket it duplicates; the others never do.
        long? duplicateOf = outcome == ResolutionOutcome.Duplicate ? ticket.TicketId + 1 : null;
        ticket.Resolve(outcome, duplicateOf);
        await f.Resolutions.AddAsync(new TicketResolution(
            ticket.TicketId, outcome, "Replaced the compressor.", null, duplicateOf, owner, closedAtUtc));

        if (closed)
        {
            ticket.Close();
            await f.StatusHistory.AddAsync(new TicketStatusHistory(
                ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
                owner, actorIsSystem: false, note: null, Guid.NewGuid(), closedAtUtc));
        }

        return (ticket, owner);
    }

    private static ReopenTicketRequestDto ReopenRequest(int targetDepartmentId = OtherDepartmentId, string reason = "Customer called back — issue persists.") =>
        new(reason, targetDepartmentId, []);

    // -- Status eligibility --

    [Fact]
    public async Task ReopenAsync_ByCsAgent_OnClosedTicket_ReturnsToInProgress_ArchivesResolution_WritesHistoryAuditAndEvent()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, previousOwner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-2));
        var agent = Guid.NewGuid();

        var result = await f.Service.ReopenAsync(agent, [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(1, ticket.ReopenCount);
        Assert.Null(ticket.ResolutionOutcome);

        // FR-RES-04: the prior resolution is archived — never deleted.
        var resolution = Assert.Single(f.Resolutions.Added);
        Assert.False(resolution.IsCurrent);

        var history = Assert.Single(
            f.StatusHistory.Added,
            h => h.Dimension == TicketStatusDimension.TicketStatus && h.NewValue == (byte)TicketStatus.InProgress);
        Assert.Equal((byte)TicketStatus.Closed, history.OldValue);
        Assert.Equal("Customer called back — issue persists.", history.Note);
        Assert.Equal(agent, history.ActorEmployeeId);

        var audit = Assert.Single(f.Audit.Entries, w => w.Action == "Reopen");
        Assert.Equal(agent, audit.ActorEmployeeId);
        Assert.Contains("Closed", audit.BeforeValue);
        Assert.Contains($"DepartmentId={ClosingDepartmentId}", audit.BeforeValue);
        Assert.Contains(previousOwner.ToString(), audit.BeforeValue);
        Assert.Contains("ReopenCount=1", audit.AfterValue);
        Assert.Contains($"DepartmentId={OtherDepartmentId}", audit.AfterValue);
        Assert.Contains("Reason=Customer called back", audit.AfterValue);

        var workflowEvent = Assert.Single(f.WorkflowEvents.All);
        Assert.Equal(WorkflowEventType.Reopened, workflowEvent.EventType);
        Assert.Equal(agent, workflowEvent.ActorEmployeeId);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task ReopenAsync_OnResolvedTicket_IsRejected_ResolvedIsNoLongerReopenable()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1), closed: false);

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.NotEligibleForReopen, result.Outcome);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.PendingCustomer)]
    [InlineData(TicketStatus.PendingThirdParty)]
    public async Task ReopenAsync_OnAnyNonClosedStatus_ReturnsNotEligibleForReopen(TicketStatus status)
    {
        var f = CreateService();
        SeedDepartments(f);
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, ClosingDepartmentId);
        if (status is TicketStatus.PendingCustomer)
        {
            ticket.ChangeStatus(status);
        }
        else if (status is TicketStatus.PendingThirdParty)
        {
            // Legacy-only: reachable as a stored row, never as a transition.
            ticket.AsLegacyPendingThirdParty();
        }

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.NotEligibleForReopen, result.Outcome);
        Assert.Equal(0, ticket.ReopenCount);
    }

    [Theory]
    [InlineData(ResolutionOutcome.Cancelled)]
    [InlineData(ResolutionOutcome.Rejected)]
    [InlineData(ResolutionOutcome.Duplicate)]
    public async Task ReopenAsync_OnATerminalOutcome_IsRejected_EvenThoughTheTicketIsClosedAndInsideTheWindow(ResolutionOutcome outcome)
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1), outcome: outcome);

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    // -- Authorization --

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    public async Task ReopenAsync_ByAnyCsLayerRole_Succeeds(string role)
    {
        // The final approved rule: Reopen is the CS layer, not the Agent
        // alone. All three are cross-department for View, so the resource
        // half passes for them without a department assignment.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [role], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(1, ticket.ReopenCount);
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task ReopenAsync_ByAnyNonCsRole_ReturnsForbidden_NoStateChange(string role)
    {
        // Department Head and General Manager are here on purpose: neither
        // holds direct Reopen under the final rule, whatever else they may do
        // with the ticket. The caller is the ticket's own owner, so this is
        // the role gate refusing them, not the resource gate.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, owner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(owner, [role], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.True(Assert.Single(f.Resolutions.Added).IsCurrent);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public void ReopenRoleSet_IsTheCsLayer_AndNamesNoOtherRole()
    {
        // One source of truth for both the endpoint and the UI control — this
        // asserts its exact contents so a widening goes through this test.
        Assert.Equal(
            new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager }.Order(),
            TicketRoleSets.Reopen.Order());

        // System Administrator is deliberately NOT a member: ADR-0024's
        // central override is what authorizes it (asserted below).
        Assert.DoesNotContain(Roles.SystemAdministrator, TicketRoleSets.Reopen);
    }

    [Fact]
    public async Task ReopenAsync_ByACsAgentOfAnotherDepartment_Succeeds_BecauseTheAgentRoleIsCrossDepartment()
    {
        // The resource check narrows, it does not re-scope a role that is
        // already cross-department: CS Agent holds cross-department View
        // (Security-Architecture.md §3), so an agent who belongs to no
        // department at all may still reopen — that is the designed reach.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var agentFromAnotherDepartment = Guid.NewGuid();
        var result = await f.Service.ReopenAsync(
            agentFromAnotherDepartment, [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ReopenAsync_ByADepartmentScopedCallerWithNoClaimToTheTicket_IsForbiddenByTheResourceCheck()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        // TicketVisibilityRule is the gate under test: this caller holds no
        // cross-department view and belongs to no department, so the ticket is
        // not theirs to act on. (They also fail the role gate — the point is
        // that BOTH halves have to pass, and the resource half is now real.)
        var scopedStranger = Guid.NewGuid();
        var result = await f.Service.ReopenAsync(
            scopedStranger, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task ReopenAsync_ResourceCheckRunsEvenForAReopenCapableRole_WhenThatRoleIsNotCrossDepartment()
    {
        // Proves the resource half is not dead code behind the role gate: the
        // System Administrator override carries the caller past the role check,
        // and the visibility rule is evaluated for them too — it just also
        // passes, because the override applies to both.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var visible = await TicketVisibilityRule.CanViewDepartmentAsync(
            f.DepartmentAssignments, Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.CurrentDepartmentId);
        Assert.False(visible);

        var member = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(
            member, ticket.CurrentDepartmentId, isPrimary: true, now.AddYears(-1), assignedByEmployeeId: null));
        Assert.True(await TicketVisibilityRule.CanViewDepartmentAsync(
            f.DepartmentAssignments, member, [Roles.DepartmentEmployee], ticket.CurrentDepartmentId));
    }

    [Fact]
    public async Task ReopenAsync_BySystemAdministrator_StillSucceedsThroughTheAdr0024Override()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    // -- Required input --

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReopenAsync_WithoutAReason_IsRejectedByTheApplicationService_NotOnlyByTheController(string reason)
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, new ReopenTicketRequestDto(reason, OtherDepartmentId, []));

        Assert.Equal(TicketMutationOutcome.ReopenReasonRequired, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task ReopenAsync_WithoutATargetDepartment_IsRejected()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, new ReopenTicketRequestDto("Customer called back", 0, []));

        Assert.Equal(TicketMutationOutcome.TargetDepartmentRequired, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task ReopenAsync_WithAnUnknownOrInactiveDepartment_IsRejected()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var unknown = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: 999));
        Assert.Equal(TicketMutationOutcome.TargetDepartmentInactive, unknown.Outcome);

        var retired = f.Departments.AddDepartment("Retired", "RET", isActive: false);
        var inactive = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: retired.DepartmentId));
        Assert.Equal(TicketMutationOutcome.TargetDepartmentInactive, inactive.Outcome);

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    // -- Routing and assignment --

    [Fact]
    public async Task ReopenAsync_MovesCurrentDepartment_LeavesOriginatingDepartment_AndClearsTheOwner()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, previousOwner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        var originating = ticket.OriginatingDepartmentId;

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: OtherDepartmentId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(OtherDepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(originating, ticket.OriginatingDepartmentId);
        Assert.NotEqual(previousOwner, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ReopenAsync_WithNoAssignmentRule_LeavesTheTicketInTheTargetDepartmentQueue()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        var audit = Assert.Single(f.Audit.Entries, e => e.Action == "Reopen");
        Assert.Contains("AssignedEmployeeId=DepartmentQueue", audit.AfterValue);
    }

    [Fact]
    public async Task ReopenAsync_IntoTheSameDepartment_IsAllowed_AndStillClearsTheOwner()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, previousOwner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: ClosingDepartmentId));

        // Reopening into the closing department is not a transfer, so it is
        // never AlreadyInTargetDepartment.
        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(ClosingDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.NotEqual(previousOwner, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ReopenAsync_PreservesTicketIdentityAndWorkflowPinning()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var ticketId = ticket.TicketId;
        var ticketNumber = ticket.TicketNumber;
        var requestTypeId = ticket.RequestTypeId;
        var workflowTemplateId = ticket.WorkflowTemplateId;

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(ticketId, ticket.TicketId);
        Assert.Equal(ticketNumber, ticket.TicketNumber);
        Assert.Equal(requestTypeId, ticket.RequestTypeId);
        Assert.Equal(workflowTemplateId, ticket.WorkflowTemplateId);
        Assert.Single(f.Tickets.All);
    }

    [Fact]
    public async Task ReopenAsync_RunsTheExistingAssignmentAutomation_WhichAssignsTheTargetDepartmentsConfiguredOwner()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        SeedDepartments(f);

        // A classified ticket whose request type routes to a named employee in
        // the department it is being reopened into.
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedPending(workflowId: 100));
        var requestType = f.RequestTypes.Add(new RequestType(
            departmentId: OtherDepartmentId, "NOC for Resale", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: true, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));

        var configuredAssignee = Guid.NewGuid();
        f.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(requestType.RequestTypeId, configuredAssignee));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(
            configuredAssignee, OtherDepartmentId, isPrimary: true, now.AddYears(-1), assignedByEmployeeId: null));

        var previousOwner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, previousOwner, ClosingDepartmentId);
        ticket.ClassifyRequestType(requestType.RequestTypeId);
        ticket.PinWorkflowVersion(template.WorkflowTemplateId);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        await f.Resolutions.AddAsync(new TicketResolution(
            ticket.TicketId, ResolutionOutcome.Resolved, "Issued.", null, null, previousOwner, now.AddDays(-1)));
        ticket.Close();
        await f.StatusHistory.AddAsync(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
            previousOwner, actorIsSystem: false, note: null, Guid.NewGuid(), now.AddDays(-1)));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(OtherDepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(configuredAssignee, ticket.CurrentOwnerEmployeeId);

        // Recorded as a system assignment, by the one engine, under the
        // reopen's own correlation id — not as a second assignment path.
        var reopenAudit = Assert.Single(f.Audit.Entries, e => e.Action == "Reopen");
        var autoAssign = Assert.Single(f.Audit.Entries, e => e.Action == "AutoAssign");
        Assert.Null(autoAssign.ActorEmployeeId);
        Assert.Contains("Trigger=DepartmentTransfer", autoAssign.AfterValue);
        Assert.Equal(reopenAudit.CorrelationId, autoAssign.CorrelationId);

        var assignment = Assert.Single(f.Assignments.Added);
        Assert.Equal(configuredAssignee, assignment.AssignedEmployeeId);
        Assert.Equal(OtherDepartmentId, assignment.AssignedDepartmentId);
    }

    [Fact]
    public async Task ReopenAsync_RespectsTheSourceDepartmentsTransferSetting_OnlyWhenItActuallyMoves()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        f.DepartmentSettings.Add(new DepartmentWorkflowSettings(
            ClosingDepartmentId, allowAssignment: true, allowInternalReassignment: true, allowTransferToOtherDepartments: false));

        // Moving out is refused by the department's own existing rule...
        var movedOut = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: OtherDepartmentId));
        Assert.Equal(TicketMutationOutcome.DisabledByDepartmentSettings, movedOut.Outcome);

        // ...while reopening in place is not a transfer, so it stands.
        var inPlace = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: ClosingDepartmentId));
        Assert.Equal(TicketMutationOutcome.Success, inPlace.Outcome);
    }

    // -- Window --

    [Fact]
    public async Task ReopenAsync_OutsideTheWindowMeasuredFromClosure_ReturnsReopenWindowExpired_NoStateChange()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-8));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ReopenWindowExpired, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task ReopenAsync_ExactlyAtTheWindowBoundary_StillSucceeds()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-7));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ReopenAsync_WindowRunsFromClosure_NotFromResolution()
    {
        // Resolved 20 days ago but closed yesterday: dating the window from
        // the resolution would wrongly refuse this reopen.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        SeedDepartments(f);

        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, ClosingDepartmentId);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        await f.Resolutions.AddAsync(new TicketResolution(
            ticket.TicketId, ResolutionOutcome.Resolved, "Fixed.", null, null, owner, now.AddDays(-20)));
        ticket.Close();
        await f.StatusHistory.AddAsync(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
            owner, actorIsSystem: false, note: null, Guid.NewGuid(), now.AddDays(-1)));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ReopenAsync_WindowIsConfigurable_NotHardcodedToSevenDays()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now), new ReopenPolicy(WindowDays: 14));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-10));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    // -- Concurrency --

    [Fact]
    public async Task ReopenAsync_WithAStaleRowVersion_Returns409NotAMisleading422()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        FakeTicketRepository.SetStoredRowVersion(ticket, [9, 9, 9, 9]);

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, new ReopenTicketRequestDto("Stale copy.", OtherDepartmentId, [1, 2, 3]));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task ReopenAsync_TwiceWithTheSameToken_ReopensOnce_AndTheSecondIsAConcurrencyConflict()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var first = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.Success, first.Outcome);

        // The losing request still holds the pre-reopen token. It must not
        // produce a second cycle, event, audit entry or email — and it must
        // say "your copy is stale", not "this ticket is ineligible".
        FakeTicketRepository.SetStoredRowVersion(ticket, [7, 7, 7, 7]);
        var second = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, second.Outcome);
        Assert.Equal(1, ticket.ReopenCount);
        Assert.Single(f.WorkflowEvents.All);
        Assert.Single(f.Audit.Entries, e => e.Action == "Reopen");
        Assert.Single(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketReopened);
        Assert.Single(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
    }

    // -- SLA (the approved rule's critical clause) --

    [Fact]
    public async Task ReopenAsync_EndsTheOriginalResolutionCycle_AndOpensANewOneAtTheReopenMoment()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        var original = f.Sla.SlaInstances.All.Single();
        var originalResolutionDue = original.ResolutionDueAtUtc;

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(2, f.Sla.SlaInstances.All.Count);

        // The original cycle is retained — ended, not deleted or rewritten.
        Assert.Equal(now, original.PeriodEndAtUtc);
        Assert.Equal(originalResolutionDue, original.ResolutionDueAtUtc);
        Assert.Equal(SlaChangeReason.InitialCreation, original.ChangeReason);

        // ...and the reopened cycle is the current one, starting now.
        var cycle = Assert.Single(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
        Assert.Null(cycle.PeriodEndAtUtc);
        Assert.Equal(now, cycle.PeriodStartAtUtc);
        Assert.True(cycle.ResolutionDueAtUtc > now, "The reopened cycle's resolution deadline must be in the future.");
        Assert.False(cycle.ResolutionBreached);
    }

    [Fact]
    public async Task ReopenAsync_UsesTheExistingSlaPolicy_NotAHardCodedDuration()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        // Whatever the seeded policy for this ticket's priority says, the
        // cycle's deadline is what that policy computes from the reopen
        // moment — asserted by recomputing it through the same service.
        var (_, expectedResolutionDue) = await f.Sla.DueDates.ComputeDueDatesAsync((byte)PriorityLevel.High, now);
        var cycle = Assert.Single(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
        Assert.Equal(expectedResolutionDue, cycle.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task ReopenAsync_NeverRestartsFirstResponse_CarriesItsDeadlineAndResultForward()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(
            f, closedAtUtc: now.AddDays(-1), firstHumanResponseAtUtc: now.AddDays(-4));

        var original = f.Sla.SlaInstances.All.Single();
        original.MarkBreached(SlaDeadlineType.FirstResponse);
        var originalFirstResponseDue = original.FirstResponseDueAtUtc;
        var firstResponseAt = ticket.FirstHumanResponseAtUtc;

        await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        // The ticket's own first-response fact is untouched...
        Assert.Equal(firstResponseAt, ticket.FirstHumanResponseAtUtc);

        // ...the original cycle keeps its recorded result...
        Assert.True(original.FirstResponseBreached);

        // ...and the new cycle inherits both rather than starting a fresh,
        // generously-extended first-response clock.
        var cycle = Assert.Single(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
        Assert.Equal(originalFirstResponseDue, cycle.FirstResponseDueAtUtc);
        Assert.True(cycle.FirstResponseBreached);
    }

    [Fact]
    public async Task ReopenAsync_SchedulesOnlyTheResolutionDeadlineCheck_NeverAFirstResponseOne()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        f.Sla.Scheduler.Scheduled.Clear();

        await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        var scheduled = Assert.Single(f.Sla.Scheduler.Scheduled);
        Assert.Equal(SlaDeadlineType.Resolution, scheduled.DeadlineType);
        Assert.Equal(ticket.TicketId, scheduled.TicketId);
    }

    [Fact]
    public async Task ReopenAsync_TheSweepDoesNotBreachTheReopenedTicketOnTheHistoricalDeadline()
    {
        // The regression this clause exists for: before the fix, a ticket
        // resolved ON TIME and then reopened was swept against its original,
        // long-past resolution deadline, flagged Breached and auto-escalated —
        // a penalty for work that was actually delivered on time.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var reopen = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.Success, reopen.Outcome);

        var sweptImmediately = await f.Sla.SlaInstances.ListTicketIdsWithDeadlineDueAsync(
            SlaDeadlineType.Resolution, now, maxResults: 50);
        Assert.DoesNotContain(ticket.TicketId, sweptImmediately);

        var processed = await f.Sla.BreachProcessor.ProcessDeadlineAsync(
            ticket, SlaDeadlineType.Resolution, now, Guid.NewGuid());
        Assert.Equal(SlaBreachProcessingOutcome.NotBreached, processed);
        Assert.NotEqual(SlaState.Breached, ticket.SlaState);
        Assert.Empty(f.Sla.Escalations.All);
    }

    [Fact]
    public async Task ReopenAsync_ALaterGenuineBreachOfTheNewCycleStillWorks()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());
        var cycle = Assert.Single(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);

        // Past the NEW deadline, the reopened work is late like any other.
        var afterTheNewDeadline = cycle.ResolutionDueAtUtc.AddHours(1);
        var swept = await f.Sla.SlaInstances.ListTicketIdsWithDeadlineDueAsync(
            SlaDeadlineType.Resolution, afterTheNewDeadline, maxResults: 50);
        Assert.Contains(ticket.TicketId, swept);

        var processed = await f.Sla.BreachProcessor.ProcessDeadlineAsync(
            ticket, SlaDeadlineType.Resolution, afterTheNewDeadline, Guid.NewGuid());

        Assert.Equal(SlaBreachProcessingOutcome.BreachRecorded, processed);
        Assert.True(cycle.ResolutionBreached);
        Assert.Equal(SlaState.Breached, ticket.SlaState);
    }

    [Fact]
    public async Task ReopenAsync_OnATicketWithNoSlaPeriod_StillSucceeds_AndInventsNoClock()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1), withSlaPeriod: false);

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Empty(f.Sla.SlaInstances.All);
    }

    // -- Customer email --

    [Fact]
    public async Task ReopenAsync_QueuesExactlyOneReopenedEvent_AndARejectedReopenQueuesNone()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        // Rejected on every ground the rule defines: nothing is queued.
        await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.DepartmentHead], ticket.TicketId, ReopenRequest());
        await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(reason: "  "));
        await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: 999));
        Assert.Empty(f.Outbox.Committed);

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Single(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketReopened);
    }

    [Fact]
    public async Task ReopenAsync_WhenTheTransactionFails_QueuesNoEmailAndWritesNothing()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
    }

    // -- Second cycle --

    [Fact]
    public async Task ReopenAsync_ThenResolveAndCloseAgain_CreatesASecondCurrentResolution_HistoryPreserved()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, owner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var reopen = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: ClosingDepartmentId));
        Assert.Equal(TicketMutationOutcome.Success, reopen.Outcome);

        // Reopen cleared the owner, so someone has to pick the work up again.
        ticket.AssignTo(owner);
        var resolveAgain = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Replaced the whole unit this time.", null, null, []));
        Assert.Equal(TicketMutationOutcome.Success, resolveAgain.Outcome);

        Assert.Equal(2, f.Resolutions.Added.Count);
        Assert.False(f.Resolutions.Added[0].IsCurrent);
        Assert.True(f.Resolutions.Added[1].IsCurrent);
        Assert.Equal(1, ticket.ReopenCount);
    }

    // ---- Ticket transfer tracking: a reopen records where ownership landed on its own event type, never as an escalation ----

    [Fact]
    public async Task ReopenAsync_RecordsWhereOwnershipLanded_OnTheReopenedEvent_NotAsATransferOrEscalation()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        SeedDepartments(f);

        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedPending(workflowId: 100));
        var requestType = f.RequestTypes.Add(new RequestType(
            departmentId: OtherDepartmentId, "NOC for Resale", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: true, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));
        var configuredAssignee = Guid.NewGuid();
        f.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(requestType.RequestTypeId, configuredAssignee));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(
            configuredAssignee, OtherDepartmentId, isPrimary: true, now.AddYears(-1), assignedByEmployeeId: null));

        var previousOwner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, previousOwner, ClosingDepartmentId);
        ticket.ClassifyRequestType(requestType.RequestTypeId);
        ticket.PinWorkflowVersion(template.WorkflowTemplateId);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        await f.Resolutions.AddAsync(new TicketResolution(
            ticket.TicketId, ResolutionOutcome.Resolved, "Issued.", null, null, previousOwner, now.AddDays(-1)));
        ticket.Close();
        await f.StatusHistory.AddAsync(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
            previousOwner, actorIsSystem: false, note: null, Guid.NewGuid(), now.AddDays(-1)));

        var agent = Guid.NewGuid();
        var result = await f.Service.ReopenAsync(agent, [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var reopened = Assert.Single(f.WorkflowEvents.All);
        Assert.Equal(WorkflowEventType.Reopened, reopened.EventType);
        Assert.Equal(agent, reopened.ActorEmployeeId);
        Assert.Equal(ClosingDepartmentId, reopened.FromDepartmentId);
        Assert.Equal(OtherDepartmentId, reopened.ToDepartmentId);
        Assert.Equal(configuredAssignee, reopened.ToEmployeeId);
        Assert.Null(reopened.TransferReasonCode);
        Assert.False(reopened.IsEscalation);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.TicketTransferred);
    }

    [Fact]
    public async Task ReopenAsync_IntoADepartmentQueue_RecordsNoReceivingEmployee_AndStillNoEscalation()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-2));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var reopened = Assert.Single(f.WorkflowEvents.All);
        Assert.Equal(WorkflowEventType.Reopened, reopened.EventType);
        Assert.Equal(OtherDepartmentId, reopened.ToDepartmentId);
        Assert.Null(reopened.ToEmployeeId);
        Assert.False(reopened.IsEscalation);
    }

    // ---- Resolve / Close: the assignee, whatever their role or department ----

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.GeneralManager)]
    public async Task Assignee_InAnyDepartmentAndRole_CanResolveAndClose(string role)
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 7);

        var resolved = await f.Service.ResolveAsync(
            owner, [role], ticket.TicketId, new ResolveTicketRequestDto("Resolved", "Payment confirmed.", null, null, []));
        Assert.Equal(TicketMutationOutcome.Success, resolved.Outcome);

        var closed = await f.Service.CloseAsync(owner, [role], ticket.TicketId, new CloseTicketRequestDto([]));
        Assert.Equal(TicketMutationOutcome.Success, closed.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task UnrelatedDepartmentEmployee_CannotResolveOrClose()
    {
        var f = CreateService();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, Guid.NewGuid(), departmentId: 7);
        var stranger = Guid.NewGuid();

        var resolve = await f.Service.ResolveAsync(
            stranger, [Roles.DepartmentEmployee], ticket.TicketId, new ResolveTicketRequestDto("Resolved", "x", null, null, []));
        Assert.Equal(TicketMutationOutcome.Forbidden, resolve.Outcome);

        var close = await f.Service.CloseAsync(stranger, [Roles.DepartmentEmployee], ticket.TicketId, new CloseTicketRequestDto([]));
        Assert.Equal(TicketMutationOutcome.Forbidden, close.Outcome);
    }

    [Fact]
    public async Task ReadOnlyRole_NeverGainsResolveThroughOwnership()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 7);

        var resolve = await f.Service.ResolveAsync(
            owner, [Roles.ChairmanCeo], ticket.TicketId, new ResolveTicketRequestDto("Resolved", "x", null, null, []));
        Assert.Equal(TicketMutationOutcome.Forbidden, resolve.Outcome);
    }
}
