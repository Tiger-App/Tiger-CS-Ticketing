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

    /// <summary>A refused reopen writes nothing at all: same status, owner, department, resolution, SLA, assignments, events, audit and outbox.</summary>
    private static void AssertReopenWroteNothing(Fixture f, Ticket ticket, Guid? ownerBefore, int departmentBefore, int resolutionsBefore = 1)
    {
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.NotNull(ticket.ResolutionOutcome);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(ownerBefore, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(departmentBefore, ticket.CurrentDepartmentId);
        Assert.Equal(resolutionsBefore, f.Resolutions.Added.Count);
        Assert.All(f.Resolutions.Added, r => Assert.True(r.IsCurrent));
        Assert.DoesNotContain(f.Assignments.Added, a => a.IsCurrent);
        Assert.DoesNotContain(f.StatusHistory.Added, h => h.OldValue == (byte)TicketStatus.Closed);
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action is "Reopen" or "AutoAssign");
        Assert.Empty(f.WorkflowEvents.All);
        Assert.Empty(f.Outbox.Committed);
        Assert.DoesNotContain(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task ReopenAsync_ByCsAgent_OnClosedTicket_IsRefused_ClosedIsTerminal_NothingArchivedOrWritten()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-2));
        var agent = Guid.NewGuid();
        var ownerBefore = ticket.CurrentOwnerEmployeeId;
        var historyBefore = f.StatusHistory.Added.Count;

        var result = await f.Service.ReopenAsync(agent, [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);   // the closure outcome is kept

        // FR-RES-04 used to archive the resolution on reopen; now the resolution stays the current one, untouched.
        Assert.True(Assert.Single(f.Resolutions.Added).IsCurrent);
        Assert.Equal(historyBefore, f.StatusHistory.Added.Count);
        Assert.DoesNotContain(f.Audit.Entries, w => w.Action == "Reopen");
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.Reopened);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        AssertReopenWroteNothing(f, ticket, ownerBefore, ClosingDepartmentId);
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
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    public async Task ReopenAsync_ByAnyCsLayerRole_IsRefused_ClosedIsTerminal(string role)
    {
        // The role and resource gates still PASS for the CS layer (all three are cross-department for View), so the answer is the terminal-state
        // refusal - not Forbidden - and nothing changes.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [role], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        AssertReopenWroteNothing(f, ticket, ownerBefore: null, ClosingDepartmentId);
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
    public async Task ReopenAsync_ByACsAgentOfAnotherDepartment_IsRefusedAsTerminal_NotForbidden_BecauseTheAgentRoleIsCrossDepartment()
    {
        // The resource check narrows, it does not re-scope a role that is already cross-department: CS Agent holds cross-department View
        // (Security-Architecture.md section 3), so an agent who belongs to no department at all passes it - and then meets the terminal rule.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var agentFromAnotherDepartment = Guid.NewGuid();
        var result = await f.Service.ReopenAsync(
            agentFromAnotherDepartment, [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        AssertReopenWroteNothing(f, ticket, ownerBefore: null, ClosingDepartmentId);
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

    [Fact]
    public async Task ReopenAsync_BySystemAdministrator_PassesTheAdr0024Override_ButIsStillRefused_ClosedIsTerminal()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        AssertReopenWroteNothing(f, ticket, ownerBefore: null, ClosingDepartmentId);
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
    public async Task ReopenAsync_WithAnUnknownOrInactiveDepartment_IsRefusedAsTerminal_BeforeTheDepartmentIsEvenLookedAt()
    {
        // TargetDepartmentInactive is no longer reachable for a Closed ticket: the terminal-state refusal runs before routing validation, so both an unknown
        // and a deactivated target department get the same answer and nothing is written.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var unknown = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: 999));
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, unknown.Outcome);

        var retired = f.Departments.AddDepartment("Retired", "RET", isActive: false);
        var inactive = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: retired.DepartmentId));
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, inactive.Outcome);

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        AssertReopenWroteNothing(f, ticket, ownerBefore: null, ClosingDepartmentId);
    }

    // -- Routing and assignment --

    [Fact]
    public async Task ReopenAsync_DoesNotMoveCurrentDepartment_LeavesOriginatingDepartment_AndTheOwnerUntouched()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, previousOwner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        var originating = ticket.OriginatingDepartmentId;
        var ownerBefore = ticket.CurrentOwnerEmployeeId;

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: OtherDepartmentId));

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(ClosingDepartmentId, ticket.CurrentDepartmentId);               // not moved to OtherDepartmentId
        Assert.Equal(originating, ticket.OriginatingDepartmentId);
        Assert.Equal(ownerBefore, ticket.CurrentOwnerEmployeeId);
        AssertReopenWroteNothing(f, ticket, ownerBefore, ClosingDepartmentId);
    }

    [Fact]
    public async Task ReopenAsync_WithNoAssignmentRule_IsRefused_AndLeavesTheTicketInItsDepartmentWithNoQueueAssignmentAudit()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "Reopen");
        Assert.DoesNotContain(f.Audit.Entries, e => e.AfterValue is not null && e.AfterValue.Contains("AssignedEmployeeId=DepartmentQueue", StringComparison.Ordinal));
        AssertReopenWroteNothing(f, ticket, ownerBefore: null, ClosingDepartmentId);
    }

    [Fact]
    public async Task ReopenAsync_IntoTheSameDepartment_IsRefused_ClosedIsTerminal()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: ClosingDepartmentId));

        // Naming the closing department used to be a legitimate reopen (never AlreadyInTargetDepartment); now it is the same terminal refusal.
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(ClosingDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        AssertReopenWroteNothing(f, ticket, ownerBefore: null, ClosingDepartmentId);
    }

    [Fact]
    public async Task ReopenAsync_PreservesTicketIdentityAndWorkflowPinning_BecauseNothingChanges()
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

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(ticketId, ticket.TicketId);
        Assert.Equal(ticketNumber, ticket.TicketNumber);
        Assert.Equal(requestTypeId, ticket.RequestTypeId);
        Assert.Equal(workflowTemplateId, ticket.WorkflowTemplateId);
        Assert.Single(f.Tickets.All);                                               // no copy / successor ticket either
    }

    [Fact]
    public async Task ReopenAsync_DoesNotRunTheAssignmentAutomation_SoTheTargetDepartmentsConfiguredOwnerIsNeverAssigned()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        SeedDepartments(f);

        // A classified ticket whose request type routes to a named employee in the department a reopen would have targeted.
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

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(ClosingDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.NotEqual(configuredAssignee, ticket.CurrentOwnerEmployeeId);

        // No system assignment under a reopen's correlation id: the one engine never ran.
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action is "Reopen" or "AutoAssign");
        Assert.Empty(f.Assignments.Added);
        Assert.Empty(f.WorkflowEvents.All);
    }

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

    [Fact]
    public async Task ReopenAsync_DoesNotReturnTheTicketToThePreviousCsAgent_EvenWhenStillEligible_AndEvenIntoADepartmentWithNoStaff()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, previousAgent, _) = await ClosedTicketOwnedByCallCenterAgentAsync(f, now, previousStillCsAgent: true);
        var otherAgent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(otherAgent, 4, true, now.AddYears(-1), null));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(OtherDepartmentId));

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(ClosingDepartmentId, ticket.CurrentDepartmentId);              // the department did not follow the reopen request...
        Assert.Null(ticket.CurrentOwnerEmployeeId);                                 // ...and the previous CS Agent was not restored
        Assert.NotEqual(previousAgent, ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign");
        Assert.DoesNotContain(f.Assignments.Added, a => a.IsCurrent);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    [Fact]
    public async Task ReopenAsync_WhenThePreviousAgentIsGone_DoesNotTakeTheNextAgentOfTheSharedRotation()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, previousAgent, _) = await ClosedTicketOwnedByCallCenterAgentAsync(f, now, previousStillCsAgent: false);
        var callCenterId = (await f.Departments.ListAsync(activeOnly: true)).Single(d => d.Code == "CC").DepartmentId;
        var current = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(current, callCenterId, true, now.AddYears(-1), null));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(OtherDepartmentId));

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.NotEqual(previousAgent, ticket.CurrentOwnerEmployeeId);
        Assert.NotEqual(current, ticket.CurrentOwnerEmployeeId);                    // the rotation is never consulted
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(ClosingDepartmentId, ticket.CurrentDepartmentId);
        Assert.DoesNotContain(f.Assignments.Added, a => a.IsCurrent);
    }

    [Fact]
    public async Task ReopenAsync_WithNoEligibleAgent_IsStillTheSameTerminalRefusal_AndRecordsNoAssignmentFailure()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _, _) = await ClosedTicketOwnedByCallCenterAgentAsync(f, now, previousStillCsAgent: false);

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(OtherDepartmentId));

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    [Fact]
    public async Task ReopenAsync_TheSourceDepartmentsTransferSetting_NoLongerMatters_BothMovingOutAndReopeningInPlaceAreRefusedAsTerminal()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        f.DepartmentSettings.Add(new DepartmentWorkflowSettings(
            ClosingDepartmentId, allowAssignment: true, allowInternalReassignment: true, allowTransferToOtherDepartments: false));

        // Moving out used to be refused by the department's own rule (DisabledByDepartmentSettings); the terminal rule is evaluated first.
        var movedOut = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: OtherDepartmentId));
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, movedOut.Outcome);

        // Reopening in place used to stand; now it is refused as well.
        var inPlace = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: ClosingDepartmentId));
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, inPlace.Outcome);
        AssertReopenWroteNothing(f, ticket, ownerBefore: null, ClosingDepartmentId);
    }

    // -- Window (the window no longer matters: inside and outside it are refused alike) --

    [Fact]
    public async Task ReopenAsync_OutsideTheWindowMeasuredFromClosure_IsRefusedAsTerminal_NotAsWindowExpired_NoStateChange()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-8));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.NotEqual(TicketMutationOutcome.ReopenWindowExpired, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task ReopenAsync_ExactlyAtTheWindowBoundary_IsRefused_ClosedIsTerminal()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-7));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task ReopenAsync_WindowRunsFromClosureNotFromResolution_ButBothInsideAndOutsideAreRefused()
    {
        // Resolved 20 days ago but closed yesterday: dating the window from the resolution used to wrongly refuse this reopen, and a closure
        // 20 days ago would have expired it. Neither matters now.
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

        var inside = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, inside.Outcome);

        // The display rule agrees at any distance from the closure: inside the window and 30 days later alike, never eligible.
        Assert.False(ReopenPolicy.Default.IsReopenEligible(TicketStatus.Closed, (byte)ResolutionOutcome.Resolved, now.AddDays(-1), now));
        Assert.False(ReopenPolicy.Default.IsReopenEligible(TicketStatus.Closed, (byte)ResolutionOutcome.Resolved, now.AddDays(-1), now.AddDays(30)));
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task ReopenAsync_WindowIsConfigurable_ButAnyConfiguredWindowIsRefused_ClosedIsTerminal()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now), new ReopenPolicy(WindowDays: 14));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-10));   // inside the configured 14 days, outside the default 7

        var inside = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, inside.Outcome);

        var wide = CreateService(new FakeTimeProvider(now), new ReopenPolicy(WindowDays: 3650));
        var (recent, _) = await SeedClosedTicketAsync(wide, closedAtUtc: now.AddMinutes(-1));
        var widest = await wide.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], recent.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, widest.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(TicketStatus.Closed, recent.TicketStatus);
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
    public async Task ReopenAsync_TwiceWithTheSameToken_IsRefusedTwice_AndAStaleTokenIsStillAConcurrencyConflict()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var first = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, first.Outcome);

        // A request still holding an old token says "your copy is stale" (409) - checked before the terminal rule - not "this ticket is ineligible".
        FakeTicketRepository.SetStoredRowVersion(ticket, [7, 7, 7, 7]);
        var second = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, second.Outcome);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.Reopened);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "Reopen");
        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketReopened);
        Assert.DoesNotContain(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
    }

    // -- SLA (the approved rule's critical clause) --

    [Fact]
    public async Task ReopenAsync_DoesNotEndTheOriginalResolutionCycle_AndOpensNoNewOne()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        var original = f.Sla.SlaInstances.All.Single();
        var originalResolutionDue = original.ResolutionDueAtUtc;
        var periodEndBefore = original.PeriodEndAtUtc;
        var slaStateBefore = ticket.SlaState;

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Single(f.Sla.SlaInstances.All);

        // The original cycle is exactly as it was - not ended at the reopen moment, not rewritten.
        Assert.Equal(periodEndBefore, original.PeriodEndAtUtc);
        Assert.NotEqual(now, original.PeriodEndAtUtc);
        Assert.Equal(originalResolutionDue, original.ResolutionDueAtUtc);
        Assert.Equal(SlaChangeReason.InitialCreation, original.ChangeReason);
        Assert.Equal(slaStateBefore, ticket.SlaState);
        Assert.DoesNotContain(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
    }

    [Fact]
    public async Task ReopenAsync_ComputesNoSlaDeadline_SoTheSlaPolicyIsNeverConsultedForAReopenedCycle()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        var instancesBefore = f.Sla.SlaInstances.All.Count;

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        // Whatever the seeded policy would have computed from the reopen moment, no cycle carrying it exists.
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        var (_, expectedResolutionDue) = await f.Sla.DueDates.ComputeDueDatesAsync((byte)PriorityLevel.High, now);
        Assert.Equal(instancesBefore, f.Sla.SlaInstances.All.Count);
        Assert.DoesNotContain(f.Sla.SlaInstances.All, i => i.ResolutionDueAtUtc == expectedResolutionDue && i.ChangeReason == SlaChangeReason.Reopen);
    }

    [Fact]
    public async Task ReopenAsync_LeavesFirstResponseAlone_ItsDeadlineAndResultStayOnTheOriginalCycle()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(
            f, closedAtUtc: now.AddDays(-1), firstHumanResponseAtUtc: now.AddDays(-4));

        var original = f.Sla.SlaInstances.All.Single();
        original.MarkBreached(SlaDeadlineType.FirstResponse);
        var originalFirstResponseDue = original.FirstResponseDueAtUtc;
        var firstResponseAt = ticket.FirstHumanResponseAtUtc;

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);

        // The ticket's own first-response fact is untouched, the original cycle keeps its recorded result and deadline, and no successor cycle exists.
        Assert.Equal(firstResponseAt, ticket.FirstHumanResponseAtUtc);
        Assert.True(original.FirstResponseBreached);
        Assert.Equal(originalFirstResponseDue, original.FirstResponseDueAtUtc);
        Assert.Single(f.Sla.SlaInstances.All);
    }

    [Fact]
    public async Task ReopenAsync_SchedulesNoSlaDeadlineCheckAtAll()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        f.Sla.Scheduler.Scheduled.Clear();

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Empty(f.Sla.Scheduler.Scheduled);
    }

    [Fact]
    public async Task ReopenAsync_TheSweepHasNothingNewToBreach_AClosedTicketKeepsItsSlaResult()
    {
        // The old regression guard (a ticket resolved ON TIME and then reopened must not be swept against its historical deadline) is moot - there is no
        // reopened ticket - but the Closed ticket must come out of a refused reopen with its SLA result exactly as it was: no breach, no escalation.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        var slaStateBefore = ticket.SlaState;

        var reopen = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, reopen.Outcome);

        Assert.Equal(slaStateBefore, ticket.SlaState);
        Assert.NotEqual(SlaState.Breached, ticket.SlaState);
        Assert.Empty(f.Sla.Escalations.All);
        Assert.Single(f.Sla.SlaInstances.All);
    }

    [Fact]
    public async Task ReopenAsync_NoNewCycleExistsToBreachLater_TheOriginalPeriodIsTheOnlyOne()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.DoesNotContain(f.Sla.SlaInstances.All, i => i.ChangeReason == SlaChangeReason.Reopen);
        var only = f.Sla.SlaInstances.All.Single();
        Assert.Equal(SlaChangeReason.InitialCreation, only.ChangeReason);
        Assert.False(only.ResolutionBreached);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task ReopenAsync_OnATicketWithNoSlaPeriod_IsRefused_AndInventsNoClock()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1), withSlaPeriod: false);

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Empty(f.Sla.SlaInstances.All);
    }

    // -- Customer email --

    [Fact]
    public async Task ReopenAsync_QueuesNoReopenedEvent_NotForARefusedRoleReasonOrDepartment_AndNotForTheTerminalRefusalEither()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        // Refused on every ground that is still checked before the terminal rule: nothing is queued.
        var forbidden = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.DepartmentHead], ticket.TicketId, ReopenRequest());
        Assert.Equal(TicketMutationOutcome.Forbidden, forbidden.Outcome);
        var noReason = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(reason: "  "));
        Assert.Equal(TicketMutationOutcome.ReopenReasonRequired, noReason.Outcome);
        Assert.Empty(f.Outbox.Committed);

        // An unknown department used to be rejected by routing validation; the terminal rule answers first. Still nothing queued.
        var unknownDepartment = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: 999));
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, unknownDepartment.Outcome);
        Assert.Empty(f.Outbox.Committed);

        // ...and the fully valid request that used to queue exactly one TicketReopened event queues none.
        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketReopened);
    }

    [Fact]
    public async Task ReopenAsync_WhenTheTransactionWouldFail_IsRefusedBeforeAnyTransaction_QueuesNoEmailAndWritesNothing()
    {
        // The refusal happens before a transaction is begun, so a failing unit of work is never reached: the answer is the terminal refusal, not a
        // ConcurrencyConflict, and nothing is queued or written either way.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
    }

    // -- Second cycle --

    [Fact]
    public async Task ReopenAsync_ThenResolveAgain_IsNotPossible_NoSecondResolution_ReopenCountStaysZero()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, owner) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-1));

        var reopen = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(targetDepartmentId: ClosingDepartmentId));
        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, reopen.Outcome);

        // Without a reopen there is no second cycle: a resolve on the Closed ticket is refused, and the one resolution stays current.
        var resolveAgain = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Replaced the whole unit this time.", null, null, []));
        Assert.NotEqual(TicketMutationOutcome.Success, resolveAgain.Outcome);

        Assert.Single(f.Resolutions.Added);
        Assert.True(f.Resolutions.Added[0].IsCurrent);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    // ---- Ticket transfer tracking: a refused reopen records no ownership move on any event type, and never an escalation ----

    [Fact]
    public async Task ReopenAsync_RecordsNoOwnershipLanding_NoReopenedEvent_NoTransferOrEscalation()
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

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Empty(f.WorkflowEvents.All);   // not a Reopened event, not a TicketTransferred one, not an escalation
        Assert.Equal(ClosingDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ReopenAsync_IntoADepartmentQueue_RecordsNoReceivingEmployee_AndNoReopenedEventAtAll()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, _) = await SeedClosedTicketAsync(f, closedAtUtc: now.AddDays(-2));

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest());

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.Reopened);
        Assert.Empty(f.WorkflowEvents.All);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.IsEscalation);
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

    [Fact]
    public async Task AClosedTicket_ThatIsReopened_IsNotReturnedToThePreviousAgent_TheAssignmentHistoryIsLeftAlone()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new FakeTimeProvider(now));
        var (ticket, previousAgent, _) = await ClosedTicketOwnedByCallCenterAgentAsync(f, now, previousStillCsAgent: true);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                              // closed: nobody owns it
        var assignmentsBefore = f.Assignments.Added.Count;

        var result = await f.Service.ReopenAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, ReopenRequest(OtherDepartmentId));

        Assert.Equal(TicketMutationOutcome.ResolutionOutcomeNotReopenable, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                              // the previous agent is NOT restored from the history
        Assert.Equal(assignmentsBefore, f.Assignments.Added.Count);
        Assert.DoesNotContain(f.Assignments.Added, a => a.IsCurrent && a.AssignedEmployeeId == previousAgent);
        Assert.DoesNotContain(f.Audit.Entries, w => w.Action == "Reopen");       // no audit naming who held it before
    }
}
