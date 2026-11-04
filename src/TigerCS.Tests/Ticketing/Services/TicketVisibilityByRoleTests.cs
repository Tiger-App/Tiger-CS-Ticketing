using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The three ticket-visibility scenarios that are decided by who the caller is rather than where they work:
/// <b>System</b> and <b>CS Manager</b> see every ticket; the <b>Genesys integration account</b> (<c>genesys.integration</c>, a configured
/// service identity) sees every Genesys-created ticket in every department, however it has since been transferred or reassigned; everyone
/// else keeps their existing role/department restrictions. Visibility only — modification rights are covered by the lifecycle, assignment
/// and note tests, which this change does not touch. Same fakes as <c>CsAgentTicketVisibilityTests</c>; the SQL form of the predicate is
/// <c>TicketQueryFilters.VisibleTo</c>, proven in <c>CsAgentTicketVisibilitySqliteTests</c>.
/// </summary>
public class TicketVisibilityByRoleTests
{
    private const int CustomerService = 1;
    private const int Facilities = 2;
    private const int Finance = 3;

    private sealed class Registry(params Guid[] ids) : IServiceIdentityRegistry
    {
        public IReadOnlyCollection<Guid> ServiceIdentityIds => ids;

        public bool IsServiceIdentity(Guid employeeId) => ids.Contains(employeeId);
    }

    private sealed record Fixture(
        TicketQueryAppService Query,
        DashboardAppService Dashboard,
        FakeTicketRepository Tickets,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        Guid Genesys);

    private static Fixture CreateService()
    {
        var genesys = Guid.NewGuid();
        var tickets = new FakeTicketRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var resolutions = new FakeTicketResolutionRepository();
        tickets.Resolutions = resolutions;
        var query = new TicketQueryAppService(
            tickets, departmentAssignments, resolutions, new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System,
            handlerRepository: tickets.Handlers, serviceIdentities: new Registry(genesys));
        var dashboard = new DashboardAppService(
            tickets, query, TimeProvider.System, new FakeDashboardQueryRepository(), departmentAssignments);
        return new Fixture(query, dashboard, tickets, departmentAssignments, genesys);
    }

    private static async Task<Ticket> SeedAsync(Fixture f, int departmentId, Guid? createdBy = null, Guid? assignedTo = null)
    {
        var ticket = Ticket.CreateVerified(
            $"TG-CS-20261030-{f.Tickets.All.Count + 1:D4}", departmentId, 10, 20, 5, (byte)PriorityLevel.High, "Issue", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        if (createdBy is { } creator)
        {
            ticket.RecordCreator(creator);
        }

        if (assignedTo is { } owner)
        {
            ticket.AssignTo(owner);
        }

        return ticket;
    }

    /// <summary>Four tickets that differ in department, creator and assignee — the spread "all tickets" must not depend on.</summary>
    private static async Task<(Ticket Genesys, Ticket GenesysTransferred, Ticket GenesysReassigned, Ticket Manual)> SeedMixAsync(Fixture f)
    {
        var genesys = await SeedAsync(f, CustomerService, f.Genesys);
        var transferred = await SeedAsync(f, CustomerService, f.Genesys);
        transferred.TransferToDepartment(Finance);
        var reassigned = await SeedAsync(f, Facilities, f.Genesys, assignedTo: Guid.NewGuid());
        reassigned.AssignTo(Guid.NewGuid()); // moved from one agent to another
        var manual = await SeedAsync(f, Facilities, createdBy: Guid.NewGuid(), assignedTo: Guid.NewGuid());
        return (genesys, transferred, reassigned, manual);
    }

    /// <summary>A manually created ticket the integration account nevertheless owns and handled an interaction on.</summary>
    private static async Task<Ticket> SeedManualTouchedByGenesysAsync(Fixture f)
    {
        var ticket = await SeedAsync(f, Facilities, createdBy: Guid.NewGuid(), assignedTo: f.Genesys);
        await f.Tickets.Handlers.AddIfAbsentAsync(TicketInteractionHandler.Create(
            ticket.TicketId, ticket.TicketId, f.Genesys, "genesys-x", InteractionHandlerSource.Routing, connectedAtUtc: null, DateTime.UtcNow));
        return ticket;
    }

    private static TicketListRequestDto AllTickets(string? search = null) =>
        new(null, null, null, null, null, null, null, search, null, 1, 50);

    private static async Task<long[]> QueueIdsAsync(Fixture f, Guid caller, params string[] roles) =>
        [.. (await f.Query.GetQueueAsync(caller, roles, AllTickets())).Items.Select(i => i.TicketId).Order()];

    private static readonly string[] Agent = [Roles.CsAgent];

    // =====================================================================
    // System and CS Manager: every ticket
    // =====================================================================

    [Theory]
    [InlineData(Roles.SystemAdministrator)]
    [InlineData(Roles.CsManager)]
    public async Task SystemAndCsManager_SeeEveryTicket_InTheQueue_TheDetail_AndTheDashboard(string role)
    {
        var f = CreateService();
        var (genesys, transferred, reassigned, manual) = await SeedMixAsync(f);
        var unassigned = await SeedAsync(f, Finance);
        var everything = new[] { genesys, transferred, reassigned, manual, unassigned }.Select(t => t.TicketId).Order().ToArray();
        var caller = Guid.NewGuid(); // a member of no department at all

        Assert.Equal(everything, await QueueIdsAsync(f, caller, role));
        Assert.Null(f.Query.ResolveTicketViewAgentScope(caller, [role]));

        foreach (var id in everything)
        {
            Assert.Equal(TicketQueryOutcome.Success, (await f.Query.GetDetailAsync(caller, [role], id)).Outcome);
            Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(caller, [role], id));
        }

        Assert.Equal(everything.Length, (await f.Dashboard.GetSummaryAsync(caller, [role])).OpenTickets);
    }

    [Theory]
    [InlineData(Roles.SystemAdministrator)]
    [InlineData(Roles.CsManager)]
    public async Task SystemAndCsManager_SearchAndFilters_NeverNarrowBelowTheirVisibleSet(string role)
    {
        var f = CreateService();
        var (_, _, _, manual) = await SeedMixAsync(f);
        var caller = Guid.NewGuid();

        var byDepartment = await f.Query.GetQueueAsync(
            caller, [role], AllTickets() with { DepartmentId = manual.CurrentDepartmentId });
        var byOwner = await f.Query.GetQueueAsync(
            caller, [role], AllTickets() with { OwnerEmployeeId = manual.CurrentOwnerEmployeeId });

        Assert.Contains(manual.TicketId, byDepartment.Items.Select(i => i.TicketId));
        Assert.Equal([manual.TicketId], byOwner.Items.Select(i => i.TicketId));
    }

    [Fact]
    public async Task ACsManagerWhoIsAlsoAnAgent_StaysUnrestricted_AndAServiceIdentityHoldingCsManagerIsToo()
    {
        var f = CreateService();
        await SeedMixAsync(f);

        Assert.Equal(4, (await QueueIdsAsync(f, Guid.NewGuid(), Roles.CsAgent, Roles.CsManager)).Length);
        // Holding a cross-department role beyond CS Agent is a deliberate grant, not the integration-account rule.
        Assert.Equal(4, (await QueueIdsAsync(f, f.Genesys, Roles.CsAgent, Roles.CsManager)).Length);
        Assert.Equal(4, (await QueueIdsAsync(f, f.Genesys, Roles.SystemAdministrator)).Length);
    }

    // =====================================================================
    // genesys.integration: every Genesys-created ticket, across departments
    // =====================================================================

    [Fact]
    public async Task GenesysIntegration_SeesEveryGenesysCreatedTicket_AcrossDepartments_IncludingTransferredAndReassigned()
    {
        var f = CreateService();
        var (genesys, transferred, reassigned, manual) = await SeedMixAsync(f);

        var visible = await QueueIdsAsync(f, f.Genesys, Roles.CsAgent);

        Assert.Equal([genesys.TicketId, transferred.TicketId, reassigned.TicketId], visible);
        Assert.DoesNotContain(manual.TicketId, visible);
        Assert.NotEqual(genesys.CurrentDepartmentId, transferred.CurrentDepartmentId); // really did leave its department
        Assert.Equal(3, new[] { genesys, transferred, reassigned }.Select(t => t.CurrentDepartmentId).Distinct().Count());
    }

    [Fact]
    public async Task GenesysIntegration_CanOpenAGenesysTicket_ButNotAManualOne()
    {
        var f = CreateService();
        var (_, transferred, reassigned, manual) = await SeedMixAsync(f);

        Assert.Equal(TicketQueryOutcome.Success, (await f.Query.GetDetailAsync(f.Genesys, Agent, transferred.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Success, (await f.Query.GetDetailAsync(f.Genesys, Agent, reassigned.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(f.Genesys, Agent, manual.TicketId)).Outcome);
        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(f.Genesys, Agent, manual.TicketId));
    }

    [Fact]
    public async Task GenesysIntegration_DoesNotSeeAManualTicket_EvenWhenItOwnsOrHandledAnInteractionOnIt()
    {
        var f = CreateService();
        var (genesys, transferred, reassigned, _) = await SeedMixAsync(f);
        var touched = await SeedManualTouchedByGenesysAsync(f);

        Assert.Equal([genesys.TicketId, transferred.TicketId, reassigned.TicketId], await QueueIdsAsync(f, f.Genesys, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(f.Genesys, Agent, touched.TicketId)).Outcome);
        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(f.Genesys, Agent, touched.TicketId));
        Assert.Equal(3, (await f.Dashboard.GetSummaryAsync(f.Genesys, Agent)).OpenTickets);
        // The same ticket is still fully visible to CS Manager.
        Assert.Contains(touched.TicketId, await QueueIdsAsync(f, Guid.NewGuid(), Roles.CsManager));
    }

    [Fact]
    public async Task GenesysIntegration_SearchOnlyEverReturnsGenesysTickets()
    {
        var f = CreateService();
        var (_, _, reassigned, manual) = await SeedMixAsync(f);

        var byOwner = await f.Query.GetQueueAsync(
            f.Genesys, Agent, AllTickets() with { OwnerEmployeeId = manual.CurrentOwnerEmployeeId });
        var byDepartment = await f.Query.GetQueueAsync(
            f.Genesys, Agent, AllTickets() with { DepartmentId = reassigned.CurrentDepartmentId });

        Assert.Empty(byOwner.Items);                                              // a filter never widens the scope
        Assert.Equal([reassigned.TicketId], byDepartment.Items.Select(i => i.TicketId));
    }

    [Fact]
    public async Task GenesysIntegration_DashboardCountsCoverOnlyGenesysTickets()
    {
        var f = CreateService();
        await SeedMixAsync(f);

        Assert.Equal(3, (await f.Dashboard.GetSummaryAsync(f.Genesys, Agent)).OpenTickets);
        Assert.Equal(4, (await f.Dashboard.GetSummaryAsync(Guid.NewGuid(), [Roles.CsManager])).OpenTickets);
    }

    [Fact]
    public async Task GenesysIntegration_GrantsNoDepartmentLevelAccess_AndIsNotAnAdmin()
    {
        var f = CreateService();
        await SeedMixAsync(f);

        Assert.True(f.Query.IsGenesysIntegration(f.Genesys, Agent));
        Assert.False(f.Query.IsGenesysIntegration(f.Genesys, [Roles.SystemAdministrator]));
        Assert.False(f.Query.IsGenesysIntegration(Guid.NewGuid(), Agent)); // a human CS Agent is not the integration account
        Assert.False(await TicketVisibilityRule.CanViewDepartmentAsync(
            f.DepartmentAssignments, f.Genesys, Agent, CustomerService, new Registry(f.Genesys)));
        Assert.True(await TicketVisibilityRule.CanViewDepartmentAsync(
            f.DepartmentAssignments, Guid.NewGuid(), [Roles.CsManager], CustomerService, new Registry(f.Genesys)));
        Assert.Equal(f.Genesys, f.Query.ResolveTicketViewAgentScope(f.Genesys, Agent));
        Assert.Null(f.Query.ResolveAgentScope(f.Genesys, Agent)); // customer-directory lookups keep the integration account unrestricted
    }

    // =====================================================================
    // Everyone else: unchanged
    // =====================================================================

    [Fact]
    public async Task OtherRoles_KeepTheirExistingRestrictions()
    {
        var f = CreateService();
        var (genesys, transferred, reassigned, manual) = await SeedMixAsync(f);
        var employee = Guid.NewGuid();
        var humanAgent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employee, Facilities, true, DateTime.UtcNow, null));

        // Department Employee: only tickets assigned to them — their department's tickets (a Genesys one included) are not theirs.
        Assert.Empty(await QueueIdsAsync(f, employee, Roles.DepartmentEmployee));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(employee, [Roles.DepartmentEmployee], manual.TicketId)).Outcome);

        // Department Head: unchanged — their own department's tickets (the approved department-management view).
        var head = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(head, Facilities, true, DateTime.UtcNow, null));
        Assert.Equal(
            new[] { reassigned.TicketId, manual.TicketId }.Order().ToArray(),
            await QueueIdsAsync(f, head, Roles.DepartmentHead));

        // Human CS Agent: assigned/handled/created only — Genesys-created tickets are not theirs.
        Assert.Empty(await QueueIdsAsync(f, humanAgent, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(humanAgent, Agent, genesys.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(employee, [Roles.DepartmentEmployee], transferred.TicketId)).Outcome);

        // Reporting User holds no department either.
        Assert.Empty(await QueueIdsAsync(f, Guid.NewGuid(), Roles.ReportingUser));
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task ExecutiveAndSupervisoryRoles_AreStillCrossDepartment(string role)
    {
        var f = CreateService();
        await SeedMixAsync(f);

        Assert.Equal(4, (await QueueIdsAsync(f, Guid.NewGuid(), role)).Length);
    }
}
