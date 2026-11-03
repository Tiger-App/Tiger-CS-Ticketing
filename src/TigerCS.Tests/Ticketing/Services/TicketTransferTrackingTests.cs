using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>Ticket transfer / escalation tracking: every manual hand-over writes one typed event; first assignments and Genesys call routing do not.</summary>
public class TicketTransferTrackingTests
{
    private sealed record Fixture(
        TicketAssignmentAppService Service,
        FakeTicketRepository Tickets,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeDepartmentRepository Departments,
        FakeTicketWorkflowEventRepository Events);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var assignments = new FakeTicketAssignmentRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var departments = new FakeDepartmentRepository();
        var audit = new FakeAuditEntryWriter();
        var workflowSettings = new FakeDepartmentWorkflowSettingsRepository();
        var rules = new FakeRequestTypeAssignmentRuleRepository();
        var events = new FakeTicketWorkflowEventRepository();

        var autoAssignment = new TicketAutoAssignmentService(rules, workflowSettings, departmentAssignments, assignments, audit);
        var service = new TicketAssignmentAppService(
            tickets, assignments, departmentAssignments, departments, new FakeTicketingUnitOfWork(), audit, TimeProvider.System,
            workflowSettings, autoAssignment, events);

        return new Fixture(service, tickets, departmentAssignments, departments, events);
    }

    private static async Task<Ticket> SeedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0001", departmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        return ticket;
    }

    [Fact]
    public async Task Transfer_RecordsWhoToWhereWhenAndWhy()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var firstAgent = Guid.NewGuid();
        ticket.AssignTo(firstAgent);
        var target = f.Departments.AddDepartment("Maintenance", "MNT");
        var manager = Guid.NewGuid();

        var result = await f.Service.TransferAsync(
            manager, [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(target.DepartmentId, "Customer needs a technician", [], null, TicketTransferReason.UnableToResolve));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var e = Assert.Single(f.Events.All);
        Assert.Equal(WorkflowEventType.TicketTransferred, e.EventType);
        Assert.Equal(manager, e.ActorEmployeeId);
        Assert.Equal((byte)TicketTransferReason.UnableToResolve, e.TransferReasonCode);
        Assert.Equal(2, e.FromDepartmentId);
        Assert.Equal(target.DepartmentId, e.ToDepartmentId);
        Assert.Null(e.ToEmployeeId);
        Assert.Equal("Customer needs a technician", e.Note);
        Assert.True(e.IsEscalation);
        // The department and the CS Agent are independent: the transfer keeps the agent.
        Assert.Equal(firstAgent, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task TransferAndAssign_RecordsTheReceivingEmployee()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var target = f.Departments.AddDepartment("Maintenance", "MNT");
        var receiver = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(receiver, target.DepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(target.DepartmentId, "Escalating", [], receiver, TicketTransferReason.NeedsEscalation));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var e = Assert.Single(f.Events.All);
        Assert.Equal(receiver, e.ToEmployeeId);
        Assert.True(e.IsEscalation);
    }

    [Fact]
    public async Task TransferWithoutReasonCode_IsRecordedAsUnspecified_NotAsEscalation()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var target = f.Departments.AddDepartment("Maintenance", "MNT");

        await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new TransferTicketRequestDto(target.DepartmentId, "Moving", []));

        var e = Assert.Single(f.Events.All);
        Assert.Equal((byte)TicketTransferReason.Unspecified, e.TransferReasonCode);
        Assert.False(e.IsEscalation);
    }

    [Fact]
    public async Task Reassign_ToADifferentEmployee_IsATransfer_ButFirstAssignmentFromTheQueueIsNot()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        foreach (var id in new[] { first, second, supervisor })
        {
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        }

        await f.Service.AssignAsync(supervisor, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(first, []));
        Assert.Empty(f.Events.All);

        await f.Service.AssignAsync(
            supervisor, [Roles.CsSupervisor], ticket.TicketId,
            new AssignTicketRequestDto(second, [], TicketTransferReason.UnableToResolve, "Needs a senior agent"));

        var e = Assert.Single(f.Events.All);
        Assert.Equal(supervisor, e.ActorEmployeeId);
        Assert.Equal(second, e.ToEmployeeId);
        Assert.Equal(e.FromDepartmentId, e.ToDepartmentId);
        Assert.True(e.IsEscalation);
        Assert.Equal(second, ticket.CurrentOwnerEmployeeId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TicketTransferReason.Unspecified)]
    [InlineData(TicketTransferReason.SystemRouting)]
    public async Task Reassign_OfAnOwnedTicket_WithoutAnExplicitReason_IsRefused_AndChangesNothing(TicketTransferReason? reason)
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var owner = Guid.NewGuid();
        var next = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        foreach (var id in new[] { owner, next, supervisor })
        {
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        }

        ticket.AssignTo(owner);

        var result = await f.Service.AssignAsync(
            supervisor, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(next, [], reason));

        Assert.Equal(TicketMutationOutcome.TransferReasonRequired, result.Outcome);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.Events.All);
    }

    [Theory]
    [InlineData(TicketTransferReason.UnableToResolve)]
    [InlineData(TicketTransferReason.NeedsEscalation)]
    [InlineData(TicketTransferReason.WrongDepartment)]
    [InlineData(TicketTransferReason.CustomerRequest)]
    [InlineData(TicketTransferReason.Other)]
    public async Task Reassign_OfAnOwnedTicket_WithAnExplicitReason_Succeeds_AndRecordsIt(TicketTransferReason reason)
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var owner = Guid.NewGuid();
        var next = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        foreach (var id in new[] { owner, next, supervisor })
        {
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        }

        ticket.AssignTo(owner);

        var result = await f.Service.AssignAsync(
            supervisor, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(next, [], reason));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal((byte)reason, Assert.Single(f.Events.All).TransferReasonCode);
    }

    [Fact]
    public async Task FirstAssignmentOutOfTheQueue_NeedsNoReason_AndRecordsNoTransfer()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var agent = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(agent, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(supervisor, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(supervisor, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(agent, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.Events.All);
    }

    [Fact]
    public async Task ReassignRefusal_DoesNotLeakToAnUnauthorizedCaller_TheRoleCheckComesFirst()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        ticket.AssignTo(Guid.NewGuid());

        var result = await f.Service.AssignAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId, new AssignTicketRequestDto(Guid.NewGuid(), []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task Reassign_ToTheSameOwner_RecordsNothing()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var owner = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(owner, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(supervisor, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        ticket.AssignTo(owner);

        await f.Service.AssignAsync(supervisor, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(owner, []));

        Assert.Empty(f.Events.All);
    }

    [Fact]
    public async Task RepeatedTransfers_KeepEveryEventInOrder()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var maintenance = f.Departments.AddDepartment("Maintenance", "MNT");
        var finance = f.Departments.AddDepartment("Finance", "FIN");

        await f.Service.TransferAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(maintenance.DepartmentId, "First", [], null, TicketTransferReason.UnableToResolve));
        await f.Service.TransferAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(finance.DepartmentId, "Second", [], null, TicketTransferReason.WrongDepartment));

        var events = await f.Events.ListByTicketIdAsync(ticket.TicketId);
        Assert.Equal(2, events.Count);
        Assert.Equal([maintenance.DepartmentId, finance.DepartmentId], events.Select(x => x.ToDepartmentId!.Value));
        Assert.Equal(1, events.Count(x => x.IsEscalation));
    }

    [Fact]
    public void ForTransfer_RejectsAnUndefinedReason()
    {
        Assert.Throws<ArgumentException>(() => TicketWorkflowEvent.ForTransfer(
            1, DateTime.UtcNow, null, (TicketTransferReason)200, 1, 2, null, null, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(Roles.CsSupervisor, true)]
    [InlineData(Roles.CsManager, true)]
    [InlineData(Roles.DepartmentHead, true)]
    [InlineData(Roles.GeneralManager, true)]
    [InlineData(Roles.SystemAdministrator, true)]
    [InlineData(Roles.CsAgent, false)]
    [InlineData(Roles.DepartmentEmployee, false)]
    [InlineData(Roles.ReportingUser, false)]
    public void EscalationReview_IsLimitedToSupervisoryRoles(string role, bool allowed) =>
        Assert.Equal(allowed, TicketQueryAppService.CanReviewEscalations([role]));
}
