using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.TakeOwnership;

/// <summary>
/// FUTURE tickets: automatic Call Center routing gives every human-handling ticket to exactly one CS Agent by Round Robin, with no manual claiming - the same pool
/// (CS Agents only, the only role that can Transfer as owner) the one-time backlog distribution uses, so the backlog and everything after it follow one rule.
/// (Genesys calls already answered by a mapped, eligible agent go straight to that agent: GenesysAnsweringAgentRoutingTests.)
/// </summary>
public sealed class CallCenterIntakeCsAgentOnlyTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CallCenterIntake_DealsToCsAgentsOnly_InRotation_NeverToADepartmentEmployeeOrHeadWhoHappensToBeInTheCallCenter()
    {
        var departments = new FakeDepartmentRepository();
        var callCenter = departments.AddDepartment("Call Center", "CC");
        var members = new FakeUserDepartmentAssignmentRepository();
        var accounts = new FakeUserAccountManager();
        var (agentA, agentB, employee, head) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var (id, role) in new[] { (agentA, Roles.CsAgent), (agentB, Roles.CsAgent), (employee, Roles.DepartmentEmployee), (head, Roles.DepartmentHead) })
        {
            members.Assignments.Add(new UserDepartmentAssignment(id, callCenter.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
            accounts.Add(id, id.ToString("N"), role);
        }

        var service = new TicketAutoAssignmentService(
            new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), members, new FakeTicketAssignmentRepository(),
            new FakeAuditEntryWriter(), new FakeAssignmentRotationRepository(), departments, accounts, accounts);

        var owners = new List<Guid>();
        for (var n = 1; n <= 8; n++)
        {
            var ticket = Ticket.CreateUnverified($"TG-CC-20261102-{n:D4}", callCenter.DepartmentId, categoryId: 5, (byte)PriorityLevel.Medium, "AC not cooling", Now);
            var result = await service.AssignCallCenterIntakeAsync(ticket, Now, Guid.NewGuid());
            Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, result.Outcome);
            owners.Add(ticket.CurrentOwnerEmployeeId!.Value);
        }

        Assert.DoesNotContain(employee, owners);
        Assert.DoesNotContain(head, owners);
        Assert.Equal(4, owners.Count(o => o == agentA));          // fair: 8 tickets over 2 CS Agents
        Assert.Equal(4, owners.Count(o => o == agentB));
    }

    [Fact]
    public async Task CallCenterWithNoCsAgent_LeavesTheTicketVisiblyInTheQueue_NeverGuessesAnEmployee()
    {
        var departments = new FakeDepartmentRepository();
        var callCenter = departments.AddDepartment("Call Center", "CC");
        var members = new FakeUserDepartmentAssignmentRepository();
        var accounts = new FakeUserAccountManager();
        var employee = Guid.NewGuid();
        members.Assignments.Add(new UserDepartmentAssignment(employee, callCenter.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
        accounts.Add(employee, "e", Roles.DepartmentEmployee);
        var service = new TicketAutoAssignmentService(
            new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), members, new FakeTicketAssignmentRepository(),
            new FakeAuditEntryWriter(), new FakeAssignmentRotationRepository(), departments, accounts, accounts);
        var ticket = Ticket.CreateUnverified("TG-CC-20261102-0001", callCenter.DepartmentId, categoryId: 5, (byte)PriorityLevel.Medium, "AC not cooling", Now);

        var result = await service.AssignCallCenterIntakeAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.NoEligibleEmployee, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task OtherDepartments_KeepTheirOwnRoundRobinPool_DepartmentEmployeesAndHeadsStillReceiveTickets()
    {
        var departments = new FakeDepartmentRepository();
        var accounting = departments.AddDepartment("Accounting", "ACCT");
        departments.AddDepartment("Call Center", "CC");
        var members = new FakeUserDepartmentAssignmentRepository();
        var accounts = new FakeUserAccountManager();
        var employee = Guid.NewGuid();
        members.Assignments.Add(new UserDepartmentAssignment(employee, accounting.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
        accounts.Add(employee, "e", Roles.DepartmentEmployee);
        var service = new TicketAutoAssignmentService(
            new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), members, new FakeTicketAssignmentRepository(),
            new FakeAuditEntryWriter(), new FakeAssignmentRotationRepository(), departments, accounts, accounts);
        var ticket = Ticket.CreateUnverified("TG-ACC-20261102-0001", accounting.DepartmentId, categoryId: 5, (byte)PriorityLevel.Medium, "Receipt", Now);

        var result = await service.ApplyCategoryRoutingAsync(ticket, new AutoAssignmentResult(AutoAssignmentOutcome.NoRequestType), Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, result.Outcome);
        Assert.Equal(employee, ticket.CurrentOwnerEmployeeId);
    }
}
