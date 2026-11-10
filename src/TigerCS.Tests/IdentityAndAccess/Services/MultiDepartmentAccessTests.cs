using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Services;

/// <summary>
/// An employee can hold several department memberships (one primary) and the ticket surfaces follow them: the Department filter offers every
/// accessible department, and the queue, the detail and the access check cover exactly those departments - server side - and nothing else.
/// </summary>
public class MultiDepartmentAccessTests
{
    // The fake department repository numbers departments from 1 in creation order (see the Fixture constructor).
    private const int Collections = 1;
    private const int Registration = 2;
    private const int Maintenance = 3;

    private sealed class Fixture
    {
        public readonly FakeUserDepartmentAssignmentRepository Memberships = new();
        public readonly FakeUserRoleReader RoleReader = new();
        public readonly FakeEmployeeRepository Employees = new();
        public readonly FakeDepartmentRepository Departments = new();
        public readonly FakeTicketRepository Tickets = new();
        public readonly TicketQueryAppService Query;
        public readonly UserProfileAppService Profile;

        public Fixture()
        {
            var resolutions = new FakeTicketResolutionRepository();
            Tickets.Resolutions = resolutions;
            Query = new TicketQueryAppService(
                Tickets, Memberships, resolutions, new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System,
                handlerRepository: Tickets.Handlers);
            Profile = new UserProfileAppService(Employees, RoleReader, Memberships, departmentRepository: Departments);
            foreach (var (name, code) in new[] { ("Collections", "COL"), ("Registration", "REG"), ("Maintenance", "MNT") })
            {
                Departments.AddDepartment(name, code);
            }
        }

        public Guid Person(string role, params (int DepartmentId, bool Primary)[] memberships)
        {
            var id = Guid.NewGuid();
            Employees.Add(new Employee(id, "Person " + id.ToString()[..4], false, DateTime.UtcNow), role);
            RoleReader.Add(id, role);
            foreach (var (departmentId, primary) in memberships)
            {
                var assignment = new UserDepartmentAssignment(id, departmentId, primary, DateTime.UtcNow, null);
                typeof(UserDepartmentAssignment).GetProperty(nameof(UserDepartmentAssignment.Department))!
                    .SetValue(assignment, Departments.GetByIdAsync(departmentId).Result);
                Memberships.Assignments.Add(assignment);
            }

            return id;
        }

        public async Task<Ticket> TicketInAsync(int departmentId)
        {
            var ticket = Ticket.CreateVerified(
                $"TG-X-20261030-{Tickets.All.Count + 1:D4}", departmentId, 10, 20, 5, (byte)PriorityLevel.High, "Issue", DateTime.UtcNow);
            await Tickets.AddAsync(ticket);
            return ticket;
        }
    }

    private static TicketListRequestDto All() => new(null, null, null, null, null, null, null, null, null, 1, 50);

    [Fact]
    public async Task AUserWithSeveralDepartments_IsOfferedAllOfThem_AndSeesTheirTickets_ButNotAThirdDepartments()
    {
        var f = new Fixture();
        string[] roles = [Roles.DepartmentHead];
        var head = f.Person(Roles.DepartmentHead, (Collections, true), (Registration, false));
        var inCollections = await f.TicketInAsync(Collections);
        var inRegistration = await f.TicketInAsync(Registration);
        var inMaintenance = await f.TicketInAsync(Maintenance);

        var me = (await f.Profile.GetCurrentUserAsync(head))!;
        var queue = (await f.Query.GetQueueAsync(head, roles, All())).Items.Select(i => i.TicketId).Order().ToArray();

        Assert.Equal(["Collections", "Registration"], me.AccessibleDepartments!.Select(d => d.Name).Order());   // the filter's options, not just Collections
        Assert.Equal(Collections, me.Departments.Single(d => d.IsPrimary).DepartmentId);                        // primary is independent of access
        Assert.Equal([inCollections.TicketId, inRegistration.TicketId], queue);
        Assert.Equal(TicketQueryOutcome.Success, (await f.Query.GetDetailAsync(head, roles, inRegistration.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(head, roles, inMaintenance.TicketId)).Outcome);
        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(head, roles, inMaintenance.TicketId));
    }

    [Fact]
    public async Task FilteringByADepartmentTheUserCannotAccess_ReturnsNothing_NotThatDepartmentsTickets()
    {
        var f = new Fixture();
        var head = f.Person(Roles.DepartmentHead, (Collections, true), (Registration, false));
        await f.TicketInAsync(Collections);
        await f.TicketInAsync(Maintenance);

        var forged = await f.Query.GetQueueAsync(head, [Roles.DepartmentHead], All() with { DepartmentId = Maintenance });

        Assert.Empty(forged.Items);
        Assert.Equal(0, forged.TotalCount);
    }

    [Fact]
    public async Task ACrossDepartmentRole_IsOfferedEveryActiveDepartment_EvenWithOneMembership()
    {
        var f = new Fixture();
        f.Departments.AddDepartment("Legacy", "LEG", isActive: false);
        var manager = f.Person(Roles.CsManager, (Collections, true));

        var me = (await f.Profile.GetCurrentUserAsync(manager))!;

        Assert.Equal(["Collections", "Maintenance", "Registration"], me.AccessibleDepartments!.Select(d => d.Name).Order());   // inactive "Legacy" is not offered
        Assert.Single(me.Departments);                                                                                       // memberships are unchanged
    }

    [Fact]
    public async Task ADepartmentEmployeeOrCsAgent_IsOfferedOnlyTheirOwnMemberships()
    {
        var f = new Fixture();
        var employee = f.Person(Roles.DepartmentEmployee, (Collections, true), (Registration, false));
        var agent = f.Person(Roles.CsAgent, (Collections, true));

        Assert.Equal(2, (await f.Profile.GetCurrentUserAsync(employee))!.AccessibleDepartments!.Count);
        Assert.Equal([Collections], (await f.Profile.GetCurrentUserAsync(agent))!.AccessibleDepartments!.Select(d => d.DepartmentId));
    }

    [Fact]
    public async Task AUserWithNoDepartmentMembership_HasNoAccessibleDepartments_AndSeesNoDepartmentTickets()
    {
        var f = new Fixture();
        var nobody = f.Person(Roles.DepartmentHead);
        await f.TicketInAsync(Collections);

        Assert.Empty((await f.Profile.GetCurrentUserAsync(nobody))!.AccessibleDepartments!);
        Assert.Empty((await f.Query.GetQueueAsync(nobody, [Roles.DepartmentHead], All())).Items);
    }
}
