using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The phase-3 approval/dependency service against the SLA document's two
/// supported cases: Collections / Send Receipts (Accounting approval →
/// ApprovalReceived) and Handover (Customer Service approval →
/// CustomerServiceApproved), plus Registration's PrerequisitesCompleted and
/// the Handover maintenance dependency. No SLA deadline is computed anywhere
/// here — only trustworthy typed trigger events.
/// </summary>
public class TicketApprovalAppServiceTests
{
    private const int CollectionsDepartmentId = 3;
    private const int AccountingDepartmentId = 6;
    private static readonly DateTime Now = new(2026, 9, 4, 10, 15, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        TicketApprovalAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketApprovalRepository Approvals,
        FakeTicketWorkflowEventRepository Events,
        FakeRequestTypeApprovalRequirementRepository Requirements,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeDepartmentRepository Departments,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        FakeRequestTypeRepository RequestTypes,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeWorkflowTemplateRepository WorkflowTemplates);

    /// <summary>
    /// <paramref name="nowUtc"/> drives the reopen-window arithmetic that
    /// ReopenApproval depends on; every other approval type is unaffected by
    /// it, which is why the existing tests pass no clock at all.
    /// </summary>
    private static Fixture CreateService(DateTime? nowUtc = null)
    {
        var tickets = new FakeTicketRepository();
        var approvals = new FakeTicketApprovalRepository();
        var events = new FakeTicketWorkflowEventRepository();
        var requirements = new FakeRequestTypeApprovalRequirementRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository { EveryUserIsResponsible = true };
        var departments = new FakeDepartmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var requestTypes = new FakeRequestTypeRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var workflowTemplates = new FakeWorkflowTemplateRepository();

        var service = new TicketApprovalAppService(
            tickets, approvals, events, requirements, departmentAssignments, departments,
            unitOfWork, audit,
            nowUtc is { } moment ? new FakeTimeProvider(moment) : TimeProvider.System,
            new ReopenEligibilityService(statusHistory, requestTypes, workflowTemplates, ReopenPolicy.Default), handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        return new Fixture(
            service, tickets, approvals, events, requirements, departmentAssignments, departments,
            audit, unitOfWork, requestTypes, statusHistory, workflowTemplates);
    }

    /// <summary>A Collections / Send Receipts ticket owned by a Collections employee, whose request type requires Accounting approval targeting the Accounting department.</summary>
    private static async Task<(Ticket Ticket, Guid Owner, int RequestTypeId)> SeedSendReceiptsTicketAsync(Fixture f)
    {
        var requestTypeId = 71;
        f.Requirements.Add(RequestTypeApprovalRequirement.ForDepartment(
            requestTypeId, ApprovalType.AccountingApproval, AccountingDepartmentId));

        var owner = Guid.NewGuid();
        var ticket = Ticket.CreateUnverified(
            "TG-COL-20260904-0001", CollectionsDepartmentId, categoryId: 5,
            (byte)PriorityLevel.Medium, "Send the receipt", Now);
        await f.Tickets.AddAsync(ticket);
        ticket.ClassifyRequestType(requestTypeId);
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);
        return (ticket, owner, requestTypeId);
    }

    private static Guid SeedAccountingApprover(Fixture f)
    {
        var approver = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(approver, AccountingDepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
        return approver;
    }

    // =====================================================================
    // Reopen Approval — the request path for roles WITHOUT direct Reopen.
    // Approval authorizes the ask; it never reopens the ticket.
    // =====================================================================

    /// <summary>
    /// A Closed/Resolved ticket in the Collections department whose request
    /// type configures ReopenApproval targeting the CS Manager role, closed
    /// <paramref name="closedDaysAgo"/> days before <see cref="Now"/> — the
    /// lifecycle-history row is what the reopen window is measured from, so
    /// the test writes the same row Close itself writes.
    /// </summary>
    private static async Task<Ticket> SeedClosedReopenableTicketAsync(
        Fixture f, double closedDaysAgo = 1, bool allowReopen = true, ResolutionOutcome outcome = ResolutionOutcome.Resolved, Guid? owner = null)
    {
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var requestType = f.RequestTypes.Add(new RequestType(
            departmentId: CollectionsDepartmentId, "Reopenable", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: true, allowPendingCustomer: true, allowPendingInternal: true, allowReopen));

        f.Requirements.Add(RequestTypeApprovalRequirement.ForRole(
            requestType.RequestTypeId, ApprovalType.ReopenApproval, Roles.CsManager));

        var ticket = Ticket.CreateUnverified(
            "TG-COL-20260904-0009", CollectionsDepartmentId, categoryId: 5,
            (byte)PriorityLevel.Medium, "Reopen me", Now.AddDays(-30));
        await f.Tickets.AddAsync(ticket);
        ticket.ClassifyRequestType(requestType.RequestTypeId);
        var assignee = owner ?? Guid.NewGuid();
        ticket.AssignTo(assignee);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(outcome, duplicateOfTicketId: null);
        ticket.Close();
        f.DepartmentAssignments.LastAssigneeByClosedTicket[ticket.TicketId] = assignee;   // closure cleared the owner; the assignment history remembers who held it

        await f.StatusHistory.AddAsync(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.TicketStatus,
            (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
            Guid.NewGuid(), actorIsSystem: false, note: null,
            Guid.NewGuid(), Now.AddDays(-closedDaysAgo)));

        return ticket;
    }

    /// <summary>
    /// The same fixture's data behind a service whose clock has moved on —
    /// how a cycle requested inside the window is decided outside it. A second
    /// CreateService() would build fresh repositories and lose the cycle.
    /// </summary>
    private static TicketApprovalAppService ServiceAt(Fixture f, DateTime nowUtc) =>
        new(f.Tickets, f.Approvals, f.Events, f.Requirements, f.DepartmentAssignments, f.Departments,
            f.UnitOfWork, f.Audit, new FakeTimeProvider(nowUtc),
            new ReopenEligibilityService(f.StatusHistory, f.RequestTypes, f.WorkflowTemplates, ReopenPolicy.Default));

    private static Guid SeedDepartmentMember(Fixture f, int departmentId)
    {
        var member = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(member, departmentId, isPrimary: true, Now, assignedByEmployeeId: null));
        return member;
    }

    private static RequestApprovalRequestDto ReopenRequest(string? reason = "Customer called back — still not cooling.") =>
        new(nameof(ApprovalType.ReopenApproval), reason);

    /// <summary>
    /// Test-data setup only: a Reopen Approval cycle that was raised BEFORE Closed became terminal and is still Pending in production data. The service can no
    /// longer create one (the request path refuses), so the decision-time tests seed it straight into the repository, exactly as the old request path wrote it.
    /// </summary>
    private static async Task<TicketApproval> SeedLegacyPendingReopenApprovalAsync(Fixture f, Ticket ticket, Guid requester, string comment = "Customer called back.")
    {
        var requirement = await f.Requirements.GetActiveAsync(ticket.RequestTypeId!.Value, ApprovalType.ReopenApproval);
        var approval = TicketApproval.Request(ticket.TicketId, requirement!, requester, Now.AddDays(-1), comment, Guid.NewGuid());
        await f.Approvals.AddAsync(approval);
        return approval;
    }

    /// <summary>The ticket and the approvals/events/audit are all untouched: a refusal writes nothing.</summary>
    private static void AssertNothingWritten(Fixture f, Ticket ticket, int approvalsBefore = 0)
    {
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(approvalsBefore, f.Approvals.All.Count);
        Assert.DoesNotContain(f.Events.All, e => e.EventType is WorkflowEventType.ApprovalRequested or WorkflowEventType.Reopened);
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action is "RequestApproval" or "Reopen");
    }

    // ---- Who may REQUEST ----

    [Fact]
    public async Task ReopenApproval_ByDepartmentEmployeeOfTheTicketsDepartment_IsRefused_ClosedIsTerminal()
    {
        // This used to be the explicit "department membership decides" rule. The requester is authorised exactly as before, but the ticket is Closed, which is
        // terminal, so no Reopen Approval cycle can be raised.
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        Assert.NotEqual(requester, ticket.CurrentOwnerEmployeeId);

        var result = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        Assert.Null(result.Response);
        AssertNothingWritten(f, ticket);
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    public async Task ReopenApproval_ByADepartmentRoleOutsideTheTicketsDepartment_IsForbidden(string role)
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var outsider = SeedDepartmentMember(f, AccountingDepartmentId);

        var result = await f.Service.RequestApprovalAsync(outsider, [role], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.Forbidden, result.Outcome);
        Assert.Empty(f.Approvals.All);
    }

    [Fact]
    public async Task ReopenApproval_ByDepartmentHeadOfTheTicketsDepartment_IsRefused_ClosedIsTerminal()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var head = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(head, [Roles.DepartmentHead], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        AssertNothingWritten(f, ticket);
    }

    [Theory]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task ReopenApproval_ByAnExecutiveRole_WithNoDepartmentMembership_IsRefused_ClosedIsTerminal(string role)
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);

        var result = await f.Service.RequestApprovalAsync(Guid.NewGuid(), [role], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        AssertNothingWritten(f, ticket);
    }

    [Fact]
    public async Task ReopenApproval_ByReportingUser_IsForbidden_EvenInsideTheDepartment()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var reporter = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(
            reporter, [Roles.ReportingUser], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.Forbidden, result.Outcome);
        Assert.Empty(f.Approvals.All);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    public async Task ReopenApproval_IsNotOfferedToRolesThatAlreadyHoldDirectReopen(string role)
    {
        // They can simply reopen the ticket — asking permission is meaningless,
        // so the view never offers the control and the endpoint refuses it.
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);

        var view = await f.Service.GetApprovalsViewAsync(Guid.NewGuid(), [role], ticket.TicketId);
        var offered = Assert.Single(view.Response!.RequestableApprovals);
        Assert.Equal(nameof(ApprovalType.ReopenApproval), offered.ApprovalType);
        Assert.False(offered.CallerCanRequest);

        var result = await f.Service.RequestApprovalAsync(Guid.NewGuid(), [role], ticket.TicketId, ReopenRequest());
        Assert.Equal(ApprovalMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ReopenApproval_IsNotOfferedToASystemAdministrator_AndTheRequestIsRefused_ClosedIsTerminal()
    {
        // ADR-0024 is an authorization override, so the endpoint stays reachable and the caller is authorised; the control is suppressed (the override
        // already grants direct Reopen), and the request itself is now refused because the ticket is Closed, which is terminal.
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);

        var view = await f.Service.GetApprovalsViewAsync(Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId);
        Assert.False(Assert.Single(view.Response!.RequestableApprovals).CallerCanRequest);

        var result = await f.Service.RequestApprovalAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId, ReopenRequest());
        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        AssertNothingWritten(f, ticket);
    }

    [Fact]
    public async Task ReopenApproval_IsNotOfferedToAnyRequester_ClosedIsTerminal()
    {
        var f = CreateService(Now);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var colleague = SeedDepartmentMember(f, CollectionsDepartmentId);
        // An employee sees (and so can act on) only the tickets assigned to them: the requester is the ticket's assignee.
        var ticket = await SeedClosedReopenableTicketAsync(f, owner: requester);

        var view = await f.Service.GetApprovalsViewAsync(requester, [Roles.DepartmentEmployee], ticket.TicketId);

        // The requestable entry is still listed (the request type configures it) but it is never offered: CallerCanRequest is false.
        var offered = Assert.Single(view.Response!.RequestableApprovals);
        Assert.Equal(nameof(ApprovalType.ReopenApproval), offered.ApprovalType);
        Assert.False(offered.CallerCanRequest);

        // A department colleague the ticket is not assigned to still cannot open its approvals view.
        f.DepartmentAssignments.NotResponsibleForClosedTicket.Add((ticket.TicketId, colleague));
        var colleagueView = await f.Service.GetApprovalsViewAsync(colleague, [Roles.DepartmentEmployee], ticket.TicketId);
        Assert.Equal(TigerCS.Application.Modules.Ticketing.Dto.TicketQueryOutcome.Forbidden, colleagueView.Outcome);
    }

    // ---- The Closed carve-out is narrow ----

    [Theory]
    [InlineData(nameof(ApprovalType.AccountingApproval))]
    [InlineData(nameof(ApprovalType.CustomerServiceApproval))]
    public async Task OtherApprovalTypes_AreStillRefusedOnAClosedTicket(string approvalType)
    {
        var f = CreateService(Now);
        var (ticket, owner, requestTypeId) = await SeedSendReceiptsTicketAsync(f);
        f.Requirements.Add(RequestTypeApprovalRequirement.ForRole(
            requestTypeId, ApprovalType.CustomerServiceApproval, Roles.CsSupervisor));
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        f.DepartmentAssignments.LastAssigneeByClosedTicket[ticket.TicketId] = owner;

        var result = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto(approvalType, "Please."));

        Assert.Equal(ApprovalMutationOutcome.TicketClosed, result.Outcome);
        Assert.Empty(f.Approvals.All);
    }

    // ---- Lifecycle eligibility gates the request ----

    [Theory]
    [InlineData(ResolutionOutcome.Cancelled)]
    [InlineData(ResolutionOutcome.Rejected)]
    [InlineData(ResolutionOutcome.Duplicate)]
    public async Task ReopenApproval_OnATerminalOutcome_IsNotRequestable(ResolutionOutcome outcome)
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f, outcome: outcome);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
    }

    [Fact]
    public async Task ReopenApproval_OutsideTheReopenWindow_IsNotRequestable()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f, closedDaysAgo: ReopenPolicy.DefaultWindowDays + 1);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
    }

    [Fact]
    public async Task ReopenApproval_WhenTheRequestTypeForbidsReopen_IsNotRequestable()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f, allowReopen: false);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
    }

    [Fact]
    public async Task ReopenApproval_OnARequestTypeThatDoesNotConfigureIt_IsNotConfigured()
    {
        // Configuration-driven like every other type: a request type that does
        // not carry the requirement offers no Reopen Approval, however eligible
        // the caller is. (Authorization is checked first, so the caller here is
        // a genuine department member — an unauthorized one gets Forbidden and
        // is never told what is or is not configured.)
        var f = CreateService(Now);
        var (ticket, _, _) = await SeedSendReceiptsTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest());

        Assert.Equal(ApprovalMutationOutcome.ApprovalNotConfigured, result.Outcome);
    }

    // ---- The reason ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReopenApproval_WithoutAReason_IsRejected_AsNotEligible_BecauseTheTerminalRuleIsCheckedFirst(string? reason)
    {
        // The reason check used to be reachable (ReasonRequired). The lifecycle-eligibility check runs before it and Closed is terminal, so the request is
        // refused as ReopenNotEligible whatever the reason says; either way nothing is written.
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest(reason));

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        Assert.Empty(f.Approvals.All);
    }

    [Fact]
    public async Task ReopenApproval_RefusedRequest_StoresNoReason_NoEvent_NoAudit()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);

        var result = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest("  Customer called back.  "));

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        Assert.Null(result.Response);
        Assert.Empty(f.Approvals.All);
        Assert.DoesNotContain(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalRequested);
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action == "RequestApproval");
        AssertNothingWritten(f, ticket);
    }

    // ---- Deciding (a cycle raised before Closed became terminal is still Pending in production data) ----

    [Fact]
    public async Task ReopenApproval_ApprovedByTheTargetedCsManager_IsRefused_AndLeavesTheTicketClosedAndUntouched()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var legacy = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester);

        var departmentBefore = ticket.CurrentDepartmentId;
        var ownerBefore = ticket.CurrentOwnerEmployeeId;

        var result = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            legacy.TicketApprovalId, new DecideApprovalRequestDto("Approve", "Go ahead."));

        // Granting would create an Approved cycle nobody can ever execute: refused, and the cycle stays Pending.
        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, Assert.Single(f.Approvals.All).Status);

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(departmentBefore, ticket.CurrentDepartmentId);
        Assert.Equal(ownerBefore, ticket.CurrentOwnerEmployeeId);

        Assert.DoesNotContain(f.Events.All, e => e.EventType is WorkflowEventType.Reopened or WorkflowEventType.ApprovalReceived);
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action is "Reopen" or "ApproveApproval");
    }

    [Fact]
    public async Task ReopenApproval_CannotBeDecidedByANonTargetedRole()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var legacy = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester);

        // Authorization still runs first: a non-targeted role is Forbidden, not told about the terminal rule.
        foreach (var role in new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.DepartmentHead, Roles.GeneralManager, Roles.ReportingUser })
        {
            var refused = await f.Service.DecideAsync(
                Guid.NewGuid(), [role], ticket.TicketId,
                legacy.TicketApprovalId, new DecideApprovalRequestDto("Approve"));
            Assert.Equal(ApprovalMutationOutcome.Forbidden, refused.Outcome);
        }

        Assert.Equal(ApprovalStatus.Pending, Assert.Single(f.Approvals.All).Status);
    }

    [Fact]
    public async Task ReopenApproval_DecidedByASystemAdministrator_IsAuthorisedByTheAdr0024Override_ButStillRefused_ClosedIsTerminal()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var legacy = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester);

        var result = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId,
            legacy.TicketApprovalId, new DecideApprovalRequestDto("Approve"));

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, result.Outcome);
        Assert.Equal(ApprovalStatus.Pending, Assert.Single(f.Approvals.All).Status);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task ReopenApproval_RejectedStillRequiresAReason_AndLeavesTheTicketClosed()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var legacy = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester);

        var noReason = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            legacy.TicketApprovalId, new DecideApprovalRequestDto("Reject"));
        Assert.Equal(ApprovalMutationOutcome.ReasonRequired, noReason.Outcome);

        var rejected = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            legacy.TicketApprovalId, new DecideApprovalRequestDto("Reject", "Outside policy."));

        Assert.Equal(ApprovalMutationOutcome.Success, rejected.Outcome);
        Assert.Equal(nameof(ApprovalStatus.Rejected), rejected.Response!.Status);
        Assert.Equal("Outside policy.", rejected.Response.DecisionComment);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Contains(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalRejected);
    }

    // ---- Eligibility is re-checked at decision time ----

    [Fact]
    public async Task ReopenApproval_ApprovedInsideOrAfterTheOldWindow_IsRefused_AndNeverBecomesApproved()
    {
        // Used to be: requested inside the window, decided outside it, refused. The window no longer matters - granting is refused inside it as well.
        var closedAt = Now.AddDays(-1);
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f, closedDaysAgo: 1);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var legacy = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester);

        var inside = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, legacy.TicketApprovalId, new DecideApprovalRequestDto("Approve"));
        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, inside.Outcome);

        // The same cycle, decided by a service whose clock is past the window.
        var later = ServiceAt(f, closedAt.AddDays(ReopenPolicy.DefaultWindowDays + 1));
        var expired = await later.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, legacy.TicketApprovalId, new DecideApprovalRequestDto("Approve"));

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, expired.Outcome);
        Assert.Equal(ApprovalStatus.Pending, Assert.Single(f.Approvals.All).Status);
    }

    [Fact]
    public async Task ReopenApproval_RejectIsStillAllowedAfterExpiry_SoAStaleCycleCanBeClosedOut()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f, closedDaysAgo: 1);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var legacy = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester);

        var later = ServiceAt(f, Now.AddDays(ReopenPolicy.DefaultWindowDays + 5));
        var rejected = await later.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            legacy.TicketApprovalId, new DecideApprovalRequestDto("Reject", "Window has passed."));

        Assert.Equal(ApprovalMutationOutcome.Success, rejected.Outcome);
        Assert.Equal(ApprovalStatus.Rejected, Assert.Single(f.Approvals.All).Status);
    }

    // ---- Duplicates and re-request ----

    [Fact]
    public async Task ReopenApproval_AnyNewRequest_IsRefused_EvenOverALegacyPendingCycle_AndNoSecondCycleOpens()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var legacy = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester);

        // The terminal-state refusal is checked before the duplicate-cycle rule: it used to be DuplicateActiveApproval.
        var again = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest("Again."));
        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, again.Outcome);

        var approve = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, legacy.TicketApprovalId, new DecideApprovalRequestDto("Approve"));
        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, approve.Outcome);

        var afterApproval = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest("Once more."));
        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, afterApproval.Outcome);

        var only = Assert.Single(f.Approvals.All);
        Assert.Equal(ApprovalStatus.Pending, only.Status);
        Assert.Equal("Customer called back.", only.RequestComment);
    }

    [Fact]
    public async Task ReopenApproval_CannotBeRequestedAgainAfterRejection_ClosedIsTerminal_AndTheRejectedCycleStaysInHistory()
    {
        var f = CreateService(Now);
        var ticket = await SeedClosedReopenableTicketAsync(f);
        var requester = SeedDepartmentMember(f, CollectionsDepartmentId);
        var first = await SeedLegacyPendingReopenApprovalAsync(f, ticket, requester, "First ask.");
        await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            first.TicketApprovalId, new DecideApprovalRequestDto("Reject", "Not yet."));

        var second = await f.Service.RequestApprovalAsync(
            requester, [Roles.DepartmentEmployee], ticket.TicketId, ReopenRequest("Second ask."));

        Assert.Equal(ApprovalMutationOutcome.ReopenNotEligible, second.Outcome);

        // Only the rejected legacy cycle exists, untouched and still the current one: nothing superseded it.
        var rejectedCycle = Assert.Single(f.Approvals.All);
        Assert.Equal(ApprovalStatus.Rejected, rejectedCycle.Status);
        Assert.True(rejectedCycle.IsCurrent);
        Assert.Equal("First ask.", rejectedCycle.RequestComment);
        Assert.Equal("Not yet.", rejectedCycle.DecisionComment);
    }

    // ---- Send Receipts / Accounting approval ----

    [Fact]
    public async Task SendReceipts_RequestAccountingApproval_OpensPendingCycle_WithEventAndAudit()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        var result = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RequestApprovalRequestDto("AccountingApproval", "Receipt for unit 1204"));

        Assert.Equal(ApprovalMutationOutcome.Success, result.Outcome);
        var approval = Assert.Single(f.Approvals.All);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal(owner, approval.RequestedByEmployeeId);
        Assert.Equal(AccountingDepartmentId, approval.TargetDepartmentId);

        // The ApprovalRequested event, the approval, and the audit entry
        // share one correlation id — one auditable action.
        var requestedEvent = Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalRequested);
        Assert.Equal(approval.TicketApprovalId, requestedEvent.TicketApprovalId);
        Assert.Equal(approval.CorrelationId, requestedEvent.CorrelationId);
        var audit = Assert.Single(f.Audit.Entries, e => e.Action == "RequestApproval");
        Assert.Equal(approval.CorrelationId, audit.CorrelationId);

        // The SLA trigger event does NOT exist yet — the 1-day clock has
        // nothing to start from until Accounting actually approves.
        Assert.DoesNotContain(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalReceived);
    }

    [Fact]
    public async Task RequestApproval_OnATypeTheRequestTypeDoesNotConfigure_IsRejected()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        var result = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RequestApprovalRequestDto("CustomerServiceApproval"));

        Assert.Equal(ApprovalMutationOutcome.ApprovalNotConfigured, result.Outcome);
        Assert.Empty(f.Approvals.All);
    }

    [Fact]
    public async Task DuplicateActiveApproval_IsPrevented()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        var first = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        Assert.Equal(ApprovalMutationOutcome.Success, first.Outcome);

        var second = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        Assert.Equal(ApprovalMutationOutcome.DuplicateActiveApproval, second.Outcome);
        Assert.Single(f.Approvals.All);
    }

    [Fact]
    public async Task AccountingApprove_RecordsDecision_AndEmitsApprovalReceived_OnlyAfterApproval()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);
        var approver = SeedAccountingApprover(f);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        var approval = Assert.Single(f.Approvals.All);

        var result = await f.Service.DecideAsync(
            approver, [Roles.DepartmentEmployee], ticket.TicketId, approval.TicketApprovalId,
            new DecideApprovalRequestDto("Approve", "Payment verified"));

        Assert.Equal(ApprovalMutationOutcome.Success, result.Outcome);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(approver, approval.DecidedByEmployeeId);
        Assert.NotNull(approval.DecisionAtUtc);

        // The typed ApprovalReceived event — phase 4's Send Receipts trigger
        // source — exists exactly once, timestamped at the decision.
        var received = Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalReceived);
        Assert.Equal(approval.DecisionAtUtc, received.OccurredAtUtc);
        Assert.Equal(approver, received.ActorEmployeeId);

        Assert.Single(f.Audit.Entries, e => e.Action == "ApproveApproval");

        // No SLA deadline of any kind was computed — that is phase 4.
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public async Task AccountingReject_RequiresAReason_AndNeverResolvesOrClosesTheTicket()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);
        var approver = SeedAccountingApprover(f);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        var approval = Assert.Single(f.Approvals.All);

        var withoutReason = await f.Service.DecideAsync(
            approver, [Roles.DepartmentEmployee], ticket.TicketId, approval.TicketApprovalId,
            new DecideApprovalRequestDto("Reject"));
        Assert.Equal(ApprovalMutationOutcome.ReasonRequired, withoutReason.Outcome);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);

        var rejected = await f.Service.DecideAsync(
            approver, [Roles.DepartmentEmployee], ticket.TicketId, approval.TicketApprovalId,
            new DecideApprovalRequestDto("Reject", "No matching payment"));
        Assert.Equal(ApprovalMutationOutcome.Success, rejected.Outcome);
        Assert.Equal(ApprovalStatus.Rejected, approval.Status);

        Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalRejected);
        Assert.DoesNotContain(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalReceived);
        Assert.Single(f.Audit.Entries, e => e.Action == "RejectApproval");

        // Rejection decides the approval, never the ticket — the next
        // operational action stays explicit.
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public async Task ReRequestAfterRejection_OpensANewCycle_AndPreservesTheRejectedOne()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);
        var approver = SeedAccountingApprover(f);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        var firstCycle = Assert.Single(f.Approvals.All);
        await f.Service.DecideAsync(
            approver, [Roles.DepartmentEmployee], ticket.TicketId, firstCycle.TicketApprovalId,
            new DecideApprovalRequestDto("Reject", "Wrong amount"));

        var reRequest = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RequestApprovalRequestDto("AccountingApproval", "Amount corrected"));

        Assert.Equal(ApprovalMutationOutcome.Success, reRequest.Outcome);
        Assert.Equal(2, f.Approvals.All.Count);
        Assert.Equal(ApprovalStatus.Rejected, firstCycle.Status);
        Assert.False(firstCycle.IsCurrent);
        Assert.Equal("Wrong amount", firstCycle.DecisionComment);
        var secondCycle = f.Approvals.All.Single(a => a.IsCurrent);
        Assert.Equal(ApprovalStatus.Pending, secondCycle.Status);
    }

    [Fact]
    public async Task ReRequestOverAnApprovedCycle_IsRefused_TheGrantIsNeverSilentlySuperseded()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);
        var approver = SeedAccountingApprover(f);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        var cycle = Assert.Single(f.Approvals.All);
        await f.Service.DecideAsync(
            approver, [Roles.DepartmentEmployee], ticket.TicketId, cycle.TicketApprovalId,
            new DecideApprovalRequestDto("Approve"));

        var reRequest = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));

        Assert.Equal(ApprovalMutationOutcome.DuplicateActiveApproval, reRequest.Outcome);
        Assert.True(cycle.IsCurrent);
    }

    // ---- Authorization ----

    [Fact]
    public async Task NonAccountingActor_CannotDecideTheAccountingApproval()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        var approval = Assert.Single(f.Approvals.All);

        // The Collections owner (right department roles, wrong department)
        // and a CS agent (no department membership at all) are both refused.
        var byOwner = await f.Service.DecideAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, approval.TicketApprovalId,
            new DecideApprovalRequestDto("Approve"));
        Assert.Equal(ApprovalMutationOutcome.Forbidden, byOwner.Outcome);

        var byCsAgent = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, approval.TicketApprovalId,
            new DecideApprovalRequestDto("Approve"));
        Assert.Equal(ApprovalMutationOutcome.Forbidden, byCsAgent.Outcome);

        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.DoesNotContain(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalReceived);
    }

    [Fact]
    public async Task StrangerCannotRequestAnApproval()
    {
        var f = CreateService();
        var (ticket, _, _) = await SeedSendReceiptsTicketAsync(f);

        var result = await f.Service.RequestApprovalAsync(
            Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.TicketId,
            new RequestApprovalRequestDto("AccountingApproval"));

        Assert.Equal(ApprovalMutationOutcome.Forbidden, result.Outcome);
        Assert.Empty(f.Approvals.All);
    }

    // ---- Handover / Customer Service approval ----

    [Fact]
    public async Task HandoverCsApproval_ByTheConfiguredRole_EmitsCustomerServiceApproved()
    {
        var f = CreateService();
        const int handoverRequestTypeId = 81;
        f.Requirements.Add(RequestTypeApprovalRequirement.ForRole(
            handoverRequestTypeId, ApprovalType.CustomerServiceApproval, Roles.CsSupervisor));

        var owner = Guid.NewGuid();
        var ticket = Ticket.CreateUnverified(
            "TG-HO-20260904-0001", 4, 5, (byte)PriorityLevel.Medium, "Key handover", Now);
        await f.Tickets.AddAsync(ticket);
        ticket.ClassifyRequestType(handoverRequestTypeId);
        ticket.AssignTo(owner);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("CustomerServiceApproval"));
        var approval = Assert.Single(f.Approvals.All);

        // A CS Agent is not the configured role; a CS Supervisor is.
        var byAgent = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, approval.TicketApprovalId,
            new DecideApprovalRequestDto("Approve"));
        Assert.Equal(ApprovalMutationOutcome.Forbidden, byAgent.Outcome);

        var bySupervisor = await f.Service.DecideAsync(
            Guid.NewGuid(), [Roles.CsSupervisor], ticket.TicketId, approval.TicketApprovalId,
            new DecideApprovalRequestDto("Approve"));
        Assert.Equal(ApprovalMutationOutcome.Success, bySupervisor.Outcome);

        // The typed CustomerServiceApproved event — Handover's phase-4
        // trigger source — exists exactly once, at the decision timestamp.
        var approved = Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.CustomerServiceApproved);
        Assert.Equal(approval.DecisionAtUtc, approved.OccurredAtUtc);
        Assert.DoesNotContain(f.Events.All, e => e.EventType == WorkflowEventType.ApprovalReceived);
    }

    // ---- Handover maintenance dependency ----

    [Fact]
    public async Task Maintenance_RequiredThenCompleted_RecordsBothTimestamps_NoDurationAnywhere()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        var required = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("MaintenanceRequired", "AC repair pending"));
        Assert.Equal(ApprovalMutationOutcome.Success, required.Outcome);

        var completed = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("MaintenanceCompleted"));
        Assert.Equal(ApprovalMutationOutcome.Success, completed.Outcome);

        Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.MaintenanceRequired);
        Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.MaintenanceCompleted);

        // Once completed, the maintenance state is settled.
        var again = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("MaintenanceRequired"));
        Assert.Equal(ApprovalMutationOutcome.EventNotApplicable, again.Outcome);
    }

    [Fact]
    public async Task MaintenanceCompleted_WithoutMaintenanceRequired_IsNotApplicable()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        var completed = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("MaintenanceCompleted"));
        Assert.Equal(ApprovalMutationOutcome.EventNotApplicable, completed.Outcome);

        // The no-maintenance path is its own explicit record.
        var notRequired = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("MaintenanceNotRequired"));
        Assert.Equal(ApprovalMutationOutcome.Success, notRequired.Outcome);
        Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.MaintenanceNotRequired);
    }

    // ---- Registration prerequisites ----

    [Fact]
    public async Task PrerequisitesCompleted_IsExplicit_OnceOnly_AndNeverInferred()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        // Nothing emits the trigger until an authorized actor records it.
        Assert.DoesNotContain(f.Events.All, e => e.EventType == WorkflowEventType.PrerequisitesCompleted);

        var recorded = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("PrerequisitesCompleted", "Documents complete"));
        Assert.Equal(ApprovalMutationOutcome.Success, recorded.Outcome);
        var prereqEvent = Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.PrerequisitesCompleted);
        Assert.Equal(owner, prereqEvent.ActorEmployeeId);

        // The FIRST timestamp is the trigger — a repeat is refused, not
        // silently shifted.
        var repeat = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("PrerequisitesCompleted"));
        Assert.Equal(ApprovalMutationOutcome.EventAlreadyRecorded, repeat.Outcome);
        Assert.Single(f.Events.All, e => e.EventType == WorkflowEventType.PrerequisitesCompleted);

        // Approval events are never recordable through this path.
        var approvalEvent = await f.Service.RecordWorkflowEventAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new RecordWorkflowEventRequestDto("ApprovalReceived"));
        Assert.Equal(ApprovalMutationOutcome.InvalidInput, approvalEvent.Outcome);
    }

    // ---- Cancel + view ----

    [Fact]
    public async Task Cancel_KeepsTheCycleAsHistory_AndAllowsANewCycle()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        var cycle = Assert.Single(f.Approvals.All);

        var cancelled = await f.Service.CancelAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, cycle.TicketApprovalId,
            new CancelApprovalRequestDto("Raised in error"));
        Assert.Equal(ApprovalMutationOutcome.Success, cancelled.Outcome);
        Assert.Equal(ApprovalStatus.Cancelled, cycle.Status);
        Assert.Single(f.Audit.Entries, e => e.Action == "CancelApproval");

        var reRequest = await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));
        Assert.Equal(ApprovalMutationOutcome.Success, reRequest.Outcome);
        Assert.Equal(2, f.Approvals.All.Count);
    }

    [Fact]
    public async Task ApprovalsView_ShowsPerCallerCapabilities_AndDerivedStates()
    {
        var f = CreateService();
        var (ticket, owner, _) = await SeedSendReceiptsTicketAsync(f);
        var approver = SeedAccountingApprover(f);
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(owner, CollectionsDepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));

        // Before any request: the requirement is offered as requestable to
        // the owner, and nothing is decidable.
        var ownerView = await f.Service.GetApprovalsViewAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId);
        Assert.Equal(TicketQueryOutcome.Success, ownerView.Outcome);
        var requestable = Assert.Single(ownerView.Response!.RequestableApprovals);
        Assert.Equal("AccountingApproval", requestable.ApprovalType);
        Assert.True(requestable.CallerCanRequest);
        Assert.Null(ownerView.Response.MaintenanceState);
        Assert.Null(ownerView.Response.PrerequisitesCompletedAtUtc);

        await f.Service.RequestApprovalAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId, new RequestApprovalRequestDto("AccountingApproval"));

        // The Accounting approver sees the decision capability; the owner
        // does not.
        var approverView = await f.Service.GetApprovalsViewAsync(approver, [Roles.DepartmentEmployee], ticket.TicketId);
        Assert.True(Assert.Single(approverView.Response!.Approvals).CallerCanDecide);

        var ownerViewAfter = await f.Service.GetApprovalsViewAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId);
        Assert.False(Assert.Single(ownerViewAfter.Response!.Approvals).CallerCanDecide);
        Assert.Empty(ownerViewAfter.Response.RequestableApprovals);

        // A member of an unrelated department cannot see the view at all.
        var strangerView = await f.Service.GetApprovalsViewAsync(Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.TicketId);
        Assert.Equal(TicketQueryOutcome.Forbidden, strangerView.Outcome);
    }
}
