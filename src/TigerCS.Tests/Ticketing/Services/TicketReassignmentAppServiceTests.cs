using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Assign / Reassign / Transfer for every user who can modify the ticket (<see cref="TicketAssignmentAppService.ReassignAsync"/>): a regular employee,
/// not an Admin / Manager / Supervisor, can hand over their own ticket; visibility and the other restrictions are unchanged.
/// </summary>
public class TicketReassignmentAppServiceTests
{
    private sealed record Fixture(
        TicketAssignmentAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketAssignmentRepository Assignments,
        FakeUserDepartmentAssignmentRepository Members,
        FakeDepartmentRepository Departments,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        FakeTicketWorkflowEventRepository Events);

    private static Fixture Create()
    {
        var tickets = new FakeTicketRepository();
        var assignments = new FakeTicketAssignmentRepository();
        var members = new FakeUserDepartmentAssignmentRepository();
        var departments = new FakeDepartmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var settings = new FakeDepartmentWorkflowSettingsRepository();
        var events = new FakeTicketWorkflowEventRepository();
        var auto = new TicketAutoAssignmentService(new FakeRequestTypeAssignmentRuleRepository(), settings, members, assignments, audit);
        var service = new TicketAssignmentAppService(
            tickets, assignments, members, departments, unitOfWork, audit, TimeProvider.System, settings, auto, events);
        return new Fixture(service, tickets, assignments, members, departments, audit, unitOfWork, events);
    }

    private static async Task<Ticket> SeedAsync(Fixture f, Guid? owner, int departmentId = 2)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0001", departmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        if (owner is { } o)
        {
            ticket.AssignTo(o);
        }

        return ticket;
    }

    private static void Member(Fixture f, Guid employee, int departmentId, bool active = true)
    {
        var membership = new UserDepartmentAssignment(employee, departmentId, true, DateTime.UtcNow, null);
        if (!active)
        {
            var e = new Employee(employee, "Former", false, DateTime.UtcNow);
            e.Deactivate(DateTime.UtcNow);
            typeof(UserDepartmentAssignment).GetProperty(nameof(UserDepartmentAssignment.Employee))!.SetValue(membership, e);
        }

        f.Members.Assignments.Add(membership);
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.CsAgent)]
    public async Task RegularEmployee_ReassignsTheirOwnTicketToAnotherEmployee_WithoutAnAdminRole(string role)
    {
        var f = Create();
        var me = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);
        Member(f, me, 2);
        Member(f, colleague, 2);
        var statusBefore = ticket.TicketStatus;

        var result = await f.Service.ReassignAsync(me, [role], ticket.TicketId, new ReassignTicketRequestDto(2, [], colleague, "Covering for lunch"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(colleague, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(2, ticket.CurrentDepartmentId);
        Assert.Equal(statusBefore, ticket.TicketStatus);
        var history = Assert.Single(f.Assignments.Added);
        Assert.Equal(colleague, history.AssignedEmployeeId);
        Assert.Equal(me, history.AssigningActorEmployeeId);
        var audit = Assert.Single(f.Audit.Entries, e => e.Action == "Assign");
        Assert.Contains("Comment=Covering for lunch", audit.AfterValue);
        Assert.Single(f.Events.All);
    }

    [Fact]
    public async Task RegularEmployee_CanAssignTheirTicketToThemselvesBackAndForth()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);
        Member(f, me, 2);
        Member(f, other, 2);

        Assert.Equal(TicketMutationOutcome.AlreadyAssignedToEmployee,
            (await f.Service.ReassignAsync(me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(2, [], me))).Outcome);
    }

    [Fact]
    public async Task RegularEmployee_TransfersToAnotherDepartmentAndAssignsAnEmployeeThere()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);
        Member(f, me, 2);
        var target = f.Departments.AddDepartment("Registration", "REG");
        var sara = Guid.NewGuid();
        Member(f, sara, target.DepartmentId);

        var result = await f.Service.ReassignAsync(
            me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(target.DepartmentId, [], sara, "Wrong department"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(target.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(2, ticket.OriginatingDepartmentId);
        Assert.Equal(sara, ticket.CurrentOwnerEmployeeId);
        Assert.Contains(f.Audit.Entries, e => e.Action == "Transfer" && e.AfterValue!.Contains("Wrong department"));
        Assert.Contains(f.Audit.Entries, e => e.Action == "Assign");
        var history = Assert.Single(f.Assignments.Added);
        Assert.Equal(target.DepartmentId, history.AssignedDepartmentId);
        Assert.Equal(sara, history.AssignedEmployeeId);
    }

    [Fact]
    public async Task DepartmentOnly_MovesTheTicketWithoutNamingAnEmployee_AndKeepsStatus()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);
        Member(f, me, 2);
        var target = f.Departments.AddDepartment("Registration", "REG");
        var statusBefore = ticket.TicketStatus;

        var result = await f.Service.ReassignAsync(me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(target.DepartmentId, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(target.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(statusBefore, ticket.TicketStatus);
        Assert.Contains(f.Audit.Entries, e => e.Action == "Transfer");
    }

    [Fact]
    public async Task SameDepartmentWithoutAnEmployee_HasNothingToChange()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);

        var result = await f.Service.ReassignAsync(me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(2, []));

        Assert.Equal(TicketMutationOutcome.AssignmentTargetRequired, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task InactiveEmployee_IsNeverAssigned_InTheSameOrAnotherDepartment()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);
        Member(f, me, 2);
        var target = f.Departments.AddDepartment("Registration", "REG");
        var former = Guid.NewGuid();
        Member(f, former, 2, active: false);
        Member(f, former, target.DepartmentId, active: false);

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment,
            (await f.Service.ReassignAsync(me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(2, [], former))).Outcome);
        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment,
            (await f.Service.ReassignAsync(me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(target.DepartmentId, [], former))).Outcome);
        Assert.Equal(me, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(2, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task EmployeeFromAnotherDepartment_IsRefused_WhenTheDepartmentIsNotTheirs()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);
        var elsewhere = Guid.NewGuid();
        Member(f, elsewhere, 99);

        var result = await f.Service.ReassignAsync(me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(2, [], elsewhere));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
    }

    [Fact]
    public async Task InactiveTargetDepartment_IsRefused()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);

        var result = await f.Service.ReassignAsync(me, [Roles.DepartmentEmployee], ticket.TicketId, new ReassignTicketRequestDto(12345, []));

        Assert.Equal(TicketMutationOutcome.TargetDepartmentInactive, result.Outcome);
        Assert.Equal(2, ticket.CurrentDepartmentId);
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.CsAgent)]
    public async Task SomeoneWhoCannotAccessTheTicket_IsForbidden_AndNothingChanges(string role)
    {
        var f = Create();
        var stranger = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: Guid.NewGuid());
        Member(f, stranger, 2);
        Member(f, colleague, 2);

        var result = await f.Service.ReassignAsync(stranger, [role], ticket.TicketId, new ReassignTicketRequestDto(2, [], colleague));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task ReadOnlyRoles_StayReadOnly(string role)
    {
        var f = Create();
        var viewer = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: null);
        Member(f, viewer, 2);
        Member(f, colleague, 2);

        var result = await f.Service.ReassignAsync(viewer, [role], ticket.TicketId, new ReassignTicketRequestDto(2, [], colleague));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task DepartmentMember_AssignsAnUnassignedTicketOfTheirDepartmentToThemselves()
    {
        var f = Create();
        var head = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: null);
        Member(f, head, 2);

        var result = await f.Service.ReassignAsync(head, [Roles.DepartmentHead], ticket.TicketId, new ReassignTicketRequestDto(2, [], head));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(head, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ClosedTicket_IsRefused()
    {
        var f = Create();
        var me = Guid.NewGuid();
        var ticket = await SeedAsync(f, owner: me);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();

        var result = await f.Service.ReassignAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new ReassignTicketRequestDto(2, [], Guid.NewGuid()));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
    }
}
