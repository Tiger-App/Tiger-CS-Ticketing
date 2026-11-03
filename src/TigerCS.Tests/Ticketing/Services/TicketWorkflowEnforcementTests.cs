using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Workflow/Automation phase 2 enforcement over the existing lifecycle:
/// structured pending (required reason, kinds, resume), request-type
/// capability gates that only ever narrow, and Reopen staying governed by
/// the existing ReopenPolicy.
/// </summary>
public class TicketWorkflowEnforcementTests
{
    private sealed record Fixture(
        TicketLifecycleAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketResolutionRepository Resolutions,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        FakeTicketPendingRecordRepository PendingRecords,
        FakeRequestTypeRepository RequestTypes,
        FakeWorkflowTemplateRepository WorkflowTemplates);

    private static Fixture CreateService(TimeProvider? timeProvider = null)
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

        var sla = new SlaServiceFixture(tickets, resolutions, statusHistory, departmentAssignments, audit, unitOfWork);
        var departments = new FakeDepartmentRepository();
        var departmentSettings = new FakeDepartmentWorkflowSettingsRepository();

        // The directory Reopen validates its target department against.
        departments.AddDepartment("Customer Service", "CS");
        departments.AddDepartment("Handover", "HO");

        var service = new TicketLifecycleAppService(
            tickets, resolutions, statusHistory, departmentAssignments, unitOfWork, audit, sla.BreachProcessor,
            timeProvider ?? TimeProvider.System,
            pendingRecords, requestTypes, workflowTemplates, new Notifications.Fakes.FakeOutboxWriter(),
            departments, departmentSettings, new FakeTicketWorkflowEventRepository(),
            new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), departmentSettings, departmentAssignments,
                new FakeTicketAssignmentRepository(), audit),
            sla.DueDates,
            new ReopenEligibilityService(statusHistory, requestTypes, workflowTemplates, ReopenPolicy.Default), handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        return new Fixture(
            service, tickets, resolutions, statusHistory, audit, unitOfWork, pendingRecords, requestTypes, workflowTemplates);
    }

    /// <summary>An InProgress ticket classified with a request type whose template is the seeded "With Pending" shape, narrowed by the given flags.</summary>
    private static async Task<(Ticket Ticket, Guid Owner)> SeedClassifiedInProgressTicketAsync(
        Fixture f, bool allowPendingCustomer, bool allowPendingInternal, bool allowReopen = true)
    {
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedPending(workflowId: 100));
        var requestType = f.RequestTypes.Add(new RequestType(
            departmentId: 2, "NOC for Resale", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: true, allowPendingCustomer, allowPendingInternal, allowReopen));

        var owner = Guid.NewGuid();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260904-0001", 2, categoryId: 5, (byte)PriorityLevel.Medium, "NOC request", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        ticket.ClassifyRequestType(requestType.RequestTypeId);
        ticket.PinWorkflowVersion(template.WorkflowTemplateId);
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);
        return (ticket, owner);
    }

    // ---- Required pending reason ----

    [Fact]
    public async Task PendingWithoutAReason_IsRejected_NoStateChange()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, true, true);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("PendingCustomer", []));

        Assert.Equal(TicketMutationOutcome.PendingReasonRequired, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Empty(f.PendingRecords.All);
    }

    // ---- Structured pending + resume ----

    [Fact]
    public async Task PendingCustomer_WritesAStructuredRecord_AndResumeClosesIt()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, true, true);

        var pendingResult = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], "Missing title deed copy"));

        Assert.Equal(TicketMutationOutcome.Success, pendingResult.Outcome);
        var record = Assert.Single(f.PendingRecords.All);
        Assert.Equal(PendingKind.Customer, record.Kind);
        Assert.Equal("Missing title deed copy", record.Reason);
        Assert.Equal(TicketStatus.InProgress, record.PreviousStatus);
        Assert.Equal(owner, record.StartedByEmployeeId);
        Assert.Null(record.ResumedAtUtc);

        // The status-history row carries the reason as its note, under the
        // same correlation id as the pending record — one auditable event.
        var historyRow = Assert.Single(f.StatusHistory.Added);
        Assert.Equal("Missing title deed copy", historyRow.Note);
        Assert.Equal(record.CorrelationId, historyRow.CorrelationId);

        var resumeResult = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.Success, resumeResult.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.NotNull(record.ResumedAtUtc);
        Assert.Equal(owner, record.ResumedByEmployeeId);
        Assert.NotNull(record.PausedDuration);
    }

    // ---- The retired PendingThirdParty, enforced at the Api boundary ----

    [Theory]
    [InlineData("PendingThirdParty")]
    [InlineData("pendingthirdparty")]
    [InlineData("PENDINGTHIRDPARTY")]
    public async Task PendingThirdParty_IsRefusedAsATarget_WhateverTheCasing(string target)
    {
        // The status parses (the enum value is deliberately still there for
        // historical rows), and the request type allows every pending kind it
        // ever could. It is still refused — hiding the option in the UI is not
        // the enforcement, the domain rule is.
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, allowPendingCustomer: true, allowPendingInternal: true);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto(target, [], "Awaiting Accounting approval"));

        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Empty(f.PendingRecords.All);
        Assert.Empty(f.StatusHistory.Added);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task PendingThirdParty_WithNoReason_IsRefusedAsInvalid_NotAsAMissingReason()
    {
        // The failure must name the real problem: the transition does not
        // exist. "A pending reason is required" would imply it becomes
        // available once one is supplied.
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, true, true);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("PendingThirdParty", []));

        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, result.Outcome);
    }

    [Fact]
    public async Task LegacyPendingThirdPartyTicket_ReturnsToInProgress_AndItsOpenPendingRecordIsResumed()
    {
        // The historical shape, end to end: a ticket that went PendingThirdParty
        // before the status was retired, with the open pending record that went
        // with it. Its escape must work and must not leave the record dangling.
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, true, true);
        ticket.AsLegacyPendingThirdParty();

        var legacyRecord = new TicketPendingRecord(
            ticket.TicketId, PendingKind.InternalOrThirdParty, "Awaiting Accounting approval",
            TicketStatus.InProgress, owner, new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), Guid.NewGuid());
        await f.PendingRecords.AddAsync(legacyRecord);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);

        Assert.NotNull(legacyRecord.ResumedAtUtc);
        Assert.Equal(owner, legacyRecord.ResumedByEmployeeId);
        Assert.NotNull(legacyRecord.PausedDuration);

        // History records the real transition out of the real historical
        // status — nothing is rewritten.
        var historyRow = Assert.Single(f.StatusHistory.Added);
        Assert.Equal((byte)TicketStatus.PendingThirdParty, historyRow.OldValue);
        Assert.Equal((byte)TicketStatus.InProgress, historyRow.NewValue);
    }

    [Fact]
    public async Task LegacyPendingThirdPartyTicket_CannotBeSentBackIntoTheRetiredStatus()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, true, true);
        ticket.AsLegacyPendingThirdParty();

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingThirdParty", [], "Still waiting"));

        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, result.Outcome);
        Assert.Equal(TicketStatus.PendingThirdParty, ticket.TicketStatus);
    }

    [Fact]
    public async Task ResolvingOutOfPending_ClosesTheOpenPendingRecord()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, true, true);

        await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], "Awaiting payment"));

        var resolveResult = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Payment received; NOC issued.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Success, resolveResult.Outcome);
        var record = Assert.Single(f.PendingRecords.All);
        Assert.NotNull(record.ResumedAtUtc);
        Assert.Equal(owner, record.ResumedByEmployeeId);
    }

    // ---- Capability gates (narrowing only) ----

    [Fact]
    public async Task RequestTypeForbiddingPendingCustomer_RejectsTheTransition()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, allowPendingCustomer: false, allowPendingInternal: true);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], "Missing documents"));

        Assert.Equal(TicketMutationOutcome.NotAllowedForRequestType, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Empty(f.PendingRecords.All);

        // With PendingThirdParty retired, PendingCustomer is the only pending
        // kind there is: forbidding it leaves the ticket with no pending
        // target at all, rather than falling back to the other one.
        var internalResult = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingThirdParty", [], "Awaiting maintenance"));
        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, internalResult.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public async Task CanGoPendingInternal_NoLongerGatesAnything()
    {
        // The deprecated capability is still computed and still stored, but a
        // request type that switches it ON changes nothing: the transition it
        // used to gate does not exist any more.
        var f = CreateService();
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, allowPendingCustomer: true, allowPendingInternal: true);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingThirdParty", [], "Awaiting Accounting approval"));

        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, result.Outcome);

        // ...and PendingCustomer is still gated by its own flag, unaffected.
        var customerResult = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], "Missing documents"));
        Assert.Equal(TicketMutationOutcome.Success, customerResult.Outcome);
    }

    [Fact]
    public async Task TicketWithoutRequestType_KeepsThePrePhase2Behavior()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260904-0002", 2, 5, (byte)PriorityLevel.Medium, "Legacy ticket", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);

        // No capability gate applies — but the structured-pending reason is
        // still required for every ticket.
        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], "Awaiting customer response"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Single(f.PendingRecords.All);
    }

    // ---- Reopen: capability gate + existing ReopenPolicy stays final ----

    /// <summary>
    /// A CLOSED classified ticket, with the lifecycle row the reopen window is
    /// measured from written at <paramref name="closedAtUtc"/> — the approved
    /// rule reopens from Closed only, and dates the window from closure.
    /// </summary>
    private static async Task<Ticket> SeedClosedClassifiedTicketAsync(Fixture f, bool allowReopen, DateTime closedAtUtc)
    {
        var (ticket, owner) = await SeedClassifiedInProgressTicketAsync(f, true, true, allowReopen);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        await f.Resolutions.AddAsync(new TicketResolution(
            ticket.TicketId, ResolutionOutcome.Resolved, "Issued.", null, null, owner, closedAtUtc));
        ticket.Close();
        await f.StatusHistory.AddAsync(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
            owner, actorIsSystem: false, note: null, Guid.NewGuid(), closedAtUtc));
        return ticket;
    }

    [Fact]
    public async Task RequestTypeDisablingReopen_RejectsReopen_EvenInsideTheWindow()
    {
        var f = CreateService();
        var ticket = await SeedClosedClassifiedTicketAsync(f, allowReopen: false, closedAtUtc: DateTime.UtcNow.AddDays(-1));

        var result = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId,
            new ReopenTicketRequestDto("Customer called back", ticket.CurrentDepartmentId, []));

        Assert.Equal(TicketMutationOutcome.NotAllowedForRequestType, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task RequestTypeAllowingReopen_StillGoesThroughTheExistingReopenPolicy()
    {
        var f = CreateService();

        // Outside ISSUE-011's window: the capability allows reopen, but the
        // existing ReopenPolicy remains the final enforcement point.
        var expired = await SeedClosedClassifiedTicketAsync(f, allowReopen: true, closedAtUtc: DateTime.UtcNow.AddDays(-30));
        var expiredResult = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], expired.TicketId,
            new ReopenTicketRequestDto("Late request", expired.CurrentDepartmentId, []));
        Assert.Equal(TicketMutationOutcome.ReopenWindowExpired, expiredResult.Outcome);

        // Inside the window it succeeds exactly as before this phase.
        var fresh = await SeedClosedClassifiedTicketAsync(f, allowReopen: true, closedAtUtc: DateTime.UtcNow.AddDays(-1));
        var freshResult = await f.Service.ReopenAsync(
            Guid.NewGuid(), [Roles.CsAgent], fresh.TicketId,
            new ReopenTicketRequestDto("Customer called back", fresh.CurrentDepartmentId, []));
        Assert.Equal(TicketMutationOutcome.Success, freshResult.Outcome);
        Assert.Equal(TicketStatus.InProgress, fresh.TicketStatus);
    }

    // ---- Authorization unchanged ----

    [Fact]
    public async Task StrangerWithAReason_IsStillForbidden_AuthorizationRunsBeforeWorkflowChecks()
    {
        var f = CreateService();
        var (ticket, _) = await SeedClassifiedInProgressTicketAsync(f, true, true);

        var result = await f.Service.ChangeStatusAsync(
            Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", [], "Missing documents"));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Empty(f.PendingRecords.All);
    }
}
