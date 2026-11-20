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
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository { EveryUserIsResponsible = true };
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
            assignmentRules, departmentSettings, departmentAssignments, assignments, audit,
            rotationRepository: new FakeAssignmentRotationRepository(), departmentRepository: departments, workflowEventRepository: workflowEvents);

        var service = new TicketLifecycleAppService(
            tickets, resolutions, statusHistory, departmentAssignments, unitOfWork, audit, sla.BreachProcessor,
            timeProvider ?? TimeProvider.System,
            pendingRecords, requestTypes, workflowTemplates, outbox,
            departments, departmentSettings, workflowEvents, autoAssignment, sla.DueDates,
            new ReopenEligibilityService(statusHistory, requestTypes, workflowTemplates, reopenPolicy ?? ReopenPolicy.Default), handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(), ticketAssignmentRepository: assignments);

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
        var (ticket, _) = await SeedClosedTicketAsync(f.Tickets);

        // Closure cleared the owner, so the former owner has no special access any more; a CS Manager probes the closed ticket instead.
        var result = await f.Service.ChangeStatusAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
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
        var (ticket, _) = await SeedClosedTicketAsync(f.Tickets);
        var resolutionsBefore = f.Resolutions.Added.Count;

        // Closure cleared the owner, so nobody is "the assignee" any more; the System Administrator (who may resolve anything) probes immutability.
        var result = await f.Service.ResolveAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId,
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

        // The history a real owned ticket carries: the owner's assignment row, marked not-current once closure ended it.
        var ownerAssignment = new TicketAssignment(ticket.TicketId, owner, ClosingDepartmentId, closedAtUtc.AddDays(-2), assigningActorEmployeeId: null);
        await f.Assignments.AddAsync(ownerAssignment);

        if (closed)
        {
            ticket.Close();
            ownerAssignment.MarkSuperseded();
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

    [Theory]
    [InlineData(ResolutionOutcome.Resolved)]
    [InlineData(ResolutionOutcome.Cancelled)]
    [InlineData(ResolutionOutcome.Rejected)]
    [InlineData(ResolutionOutcome.Duplicate)]
    public async Task ReopenAsync_OnAnyClosedTicket_IsRefused_ClosedIsTerminal_NothingIsWritten(ResolutionOutcome outcome)
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddMinutes(-5), outcome: outcome);   // freshly closed, deep inside any window
        var departmentBefore = ticket.CurrentDepartmentId;
        var ownerBefore = ticket.CurrentOwnerEmployeeId;

        foreach (var role in new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager, Roles.SystemAdministrator })
        {
            var result = await f.Service.ReopenAsync(Guid.NewGuid(), [role], ticket.TicketId, ReopenRequest());

            Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        }

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(departmentBefore, ticket.CurrentDepartmentId);
        Assert.Equal(ownerBefore, ticket.CurrentOwnerEmployeeId);           // ownership untouched
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.Reopened);
    }

    [Theory]
    [InlineData(ResolutionOutcome.Resolved, false)]
    [InlineData(ResolutionOutcome.Cancelled, true)]   // even the chatbot-inactivity closure
    public void ReopenPolicy_NoOutcomeIsEverEligible_SoNoSurfaceOffersAReopen(ResolutionOutcome outcome, bool inactivity)
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(ReopenPolicy.IsReopenableOutcome((byte)outcome, inactivity));
        Assert.False(ReopenPolicy.Default.IsReopenEligible(TicketStatus.Closed, (byte)outcome, now.AddMinutes(-1), now, inactivity));
    }

    // -- Authorization --

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
        // A Department Head of the department has the department-level view; a Department Employee has none (assignment-only visibility).
        Assert.True(await TicketVisibilityRule.CanViewDepartmentAsync(
            f.DepartmentAssignments, member, [Roles.DepartmentHead], ticket.CurrentDepartmentId));
        Assert.False(await TicketVisibilityRule.CanViewDepartmentAsync(
            f.DepartmentAssignments, member, [Roles.DepartmentEmployee], ticket.CurrentDepartmentId));
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

    // -- Routing and assignment --

    // -- Reopen always ends with an eligible CS Agent (previous one kept, otherwise the shared rotation) --

    private static async Task<(Ticket Ticket, Guid PreviousAgent, Guid CallCenterId)> ClosedTicketOwnedByCallCenterAgentAsync(Fixture f, DateTime now, bool previousStillCsAgent)
    {
        var (ticket, owner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        var callCenter = f.Departments.AddDepartment("Call Center", "CC");
        if (previousStillCsAgent)
        {
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(owner, callCenter.DepartmentId, true, now.AddYears(-1), null));
        }

        return (ticket, owner, Guid.Empty);
    }

    // -- Window --

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

    // -- SLA (the approved rule's critical clause) --

    // -- Customer email --

    // -- Second cycle --

    // ---- Ticket transfer tracking: a reopen records where ownership landed on its own event type, never as an escalation ----

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

    // ---- Clear the assignment when a ticket closes (history and the previous owner are preserved) ----

    private static async Task<(Ticket Ticket, Guid Owner, TicketAssignment Row)> SeedOwnedWithAssignmentRowAsync(Fixture f)
    {
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        var row = new TicketAssignment(ticket.TicketId, owner, ticket.CurrentDepartmentId, DateTime.UtcNow.AddHours(-3), assigningActorEmployeeId: null);
        await f.Assignments.AddAsync(row);
        return (ticket, owner, row);
    }

    [Fact]
    public async Task CloseAsync_ClearsTheOwner_EndsTheActiveAssignmentRow_AndKeepsThePreviousOwnerInTheAuditTrail()
    {
        var f = CreateService();
        var (ticket, owner, row) = await SeedOwnedWithAssignmentRowAsync(f);
        await f.Service.ResolveAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);                    // Resolved: the owner is NOT cleared
        Assert.True(row.IsCurrent);

        var result = await f.Service.CloseAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.False(row.IsCurrent);                                           // ended, never deleted
        Assert.Equal(owner, row.AssignedEmployeeId);                           // ...and never edited
        Assert.Contains(f.Assignments.Added, a => a == row);
        var audit = Assert.Single(f.Audit.Entries, w => w.Action == TicketLifecycleAppService.AssignmentClearedOnClosureAuditAction);
        Assert.Contains(owner.ToString(), audit.BeforeValue);                  // the previous owner is preserved
    }

    [Fact]
    public async Task CloseAsync_OnATicketNobodyOwns_WritesNoClearingAuditEntry()
    {
        var f = CreateService();
        var (ticket, _) = await SeedClosedTicketAsync(f.Tickets);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(f.Audit.Entries, w => w.Action == TicketLifecycleAppService.AssignmentClearedOnClosureAuditAction);
    }

    [Fact]
    public async Task OpenInProgressAndResolvedTickets_KeepTheirOwner()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ASystemClosure_ByDocumentDelivery_ClearsAHumanOwner_AndEndsTheirAssignmentRow()
    {
        var f = CreateService();
        var (ticket, owner, row) = await SeedOwnedWithAssignmentRowAsync(f);

        var result = await f.Service.CloseForDocumentDeliveryAsync(
            Guid.NewGuid(), ticket.TicketId, ResolutionOutcome.Resolved, "Document delivered", "doc=1");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.False(row.IsCurrent);
        Assert.Contains(owner.ToString(), Assert.Single(f.Audit.Entries, w => w.Action == TicketLifecycleAppService.AssignmentClearedOnClosureAuditAction).BeforeValue);
    }

    [Fact]
    public async Task AGenesysAiCompletion_ReleasingAnAutomaticOwner_ClosesWithNoOwner_AndEndsTheRow()
    {
        var f = CreateService();
        var (ticket, owner, row) = await SeedOwnedWithAssignmentRowAsync(f);

        var result = await f.Service.CloseForAiCompletionAsync(Guid.NewGuid(), ticket.TicketId, "Closed by AI", "conv=1", releaseAutomaticOwner: true);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.False(row.IsCurrent);
        Assert.Contains(owner.ToString(), Assert.Single(f.Audit.Entries, w => w.Action == TicketLifecycleAppService.AssignmentClearedOnClosureAuditAction).BeforeValue);
    }

    [Fact]
    public async Task ACustomerInactivityClosure_LeavesNoOwner()
    {
        var f = CreateService();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, Guid.NewGuid());
        ticket.ReleaseOwner();                      // the chatbot closes only ownerless tickets

        var result = await f.Service.CloseForCustomerInactivityAsync(Guid.NewGuid(), ticket.TicketId, "Customer silent");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ATransferredTicket_ThatIsThenClosed_HasNoOwner_AndItsHistoryIsIntact()
    {
        var f = CreateService();
        var (ticket, _, row) = await SeedOwnedWithAssignmentRowAsync(f);
        ticket.TransferToDepartment(OtherDepartmentId, preserveOwner: true);
        Assert.NotNull(ticket.CurrentOwnerEmployeeId);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);

        ticket.Close();

        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(OtherDepartmentId, ticket.CurrentDepartmentId);
        Assert.Throws<TicketClosedException>(() => ticket.AssignTo(Guid.NewGuid()));   // a Closed ticket can never be (re)assigned
        Assert.Throws<TicketClosedException>(() => ticket.ReleaseOwner());
        Assert.Single(f.Assignments.Added, a => a == row);
    }

}
