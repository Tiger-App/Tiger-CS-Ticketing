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
/// CS Agent ticket visibility: an agent sees a ticket only when it is <b>assigned to them right now</b> or when they <b>actually handled
/// a Genesys interaction</b> on it — never because they share its department or queue. Admin/Supervisor/Manager/executive access is
/// unchanged. These run the real application services over fakes whose "visible" rule mirrors the SQL predicate; the predicate itself is
/// proven against a real database in <c>CsAgentTicketVisibilitySqliteTests</c>, and the HTTP surface in
/// <c>CsAgentTicketVisibilityEndpointsTests</c>.
/// </summary>
public class CsAgentTicketVisibilityTests
{
    private const int CustomerService = 1;
    private const int Facilities = 2;

    private sealed class Registry(params Guid[] ids) : IServiceIdentityRegistry
    {
        public IReadOnlyCollection<Guid> ServiceIdentityIds => ids;

        public bool IsServiceIdentity(Guid employeeId) => ids.Contains(employeeId);
    }

    private sealed record Fixture(
        TicketQueryAppService Query,
        DashboardAppService Dashboard,
        FakeTicketRepository Tickets,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments);

    private static Fixture CreateService(params Guid[] serviceIdentities)
    {
        var tickets = new FakeTicketRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var resolutions = new FakeTicketResolutionRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        tickets.Resolutions = resolutions;
        tickets.Directory = departmentAssignments;
        var query = new TicketQueryAppService(
            tickets, departmentAssignments, resolutions, statusHistory, ReopenPolicy.Default, TimeProvider.System,
            handlerRepository: tickets.Handlers, serviceIdentities: new Registry(serviceIdentities));
        var dashboard = new DashboardAppService(
            tickets, query, TimeProvider.System, new FakeDashboardQueryRepository(), departmentAssignments);
        return new Fixture(query, dashboard, tickets, departmentAssignments);
    }

    private static async Task<Ticket> SeedAsync(Fixture f, int departmentId = CustomerService, Guid? assignedTo = null)
    {
        var ticket = Ticket.CreateVerified(
            $"TG-CS-20261030-{f.Tickets.All.Count + 1:D4}", departmentId, 10, 20, 5, (byte)PriorityLevel.High, "Issue", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        if (assignedTo is { } owner)
        {
            ticket.AssignTo(owner);
        }

        return ticket;
    }

    /// <summary>Genesys reported <paramref name="agent"/> connected to an interaction on the ticket — the fact the integration records.</summary>
    private static Task HandledOnGenesysAsync(Fixture f, Ticket ticket, Guid agent, long interactionId = 0) =>
        f.Tickets.Handlers.AddIfAbsentAsync(TicketInteractionHandler.Create(
            interactionId == 0 ? ticket.TicketId : interactionId, ticket.TicketId, agent, $"genesys-{agent:N}",
            InteractionHandlerSource.Routing, connectedAtUtc: null, DateTime.UtcNow));

    private static void MemberOf(Fixture f, Guid employee, int departmentId) =>
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employee, departmentId, true, DateTime.UtcNow, null));

    private static readonly string[] Agent = [Roles.CsAgent];

    private static TicketListRequestDto AllTickets() =>
        new(null, null, null, null, null, null, null, null, null, 1, 50);

    private static async Task<long[]> QueueIdsAsync(Fixture f, Guid caller, params string[] roles) =>
        [.. (await f.Query.GetQueueAsync(caller, roles, AllTickets())).Items.Select(i => i.TicketId).Order()];

    // =====================================================================
    // Assignment
    // =====================================================================

    [Fact]
    public async Task Queue_ShowsTheTicketsAssignedToTheAgent_AndNothingElse()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        var mine = await SeedAsync(f, assignedTo: agent);
        await SeedAsync(f, assignedTo: Guid.NewGuid());
        await SeedAsync(f);

        Assert.Equal([mine.TicketId], await QueueIdsAsync(f, agent, Roles.CsAgent));
    }

    [Fact]
    public async Task Detail_OfATicketAssignedToTheAgent_Succeeds()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        var mine = await SeedAsync(f, assignedTo: agent);

        var result = await f.Query.GetDetailAsync(agent, Agent, mine.TicketId);

        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Detail_OfATicketAssignedToSomeoneElse_IsForbidden()
    {
        var f = CreateService();
        var theirs = await SeedAsync(f, assignedTo: Guid.NewGuid());

        var result = await f.Query.GetDetailAsync(Guid.NewGuid(), Agent, theirs.TicketId);

        Assert.Equal(TicketQueryOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task Detail_OfAnUnassignedTicketNobodyHandled_IsForbidden()
    {
        var f = CreateService();
        var unassigned = await SeedAsync(f);

        var result = await f.Query.GetDetailAsync(Guid.NewGuid(), Agent, unassigned.TicketId);

        Assert.Equal(TicketQueryOutcome.Forbidden, result.Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetLifecycleHistoryAsync(Guid.NewGuid(), Agent, unassigned.TicketId)).Outcome);
    }

    [Fact]
    public async Task ADepartmentMembership_DoesNotGrantAnyAccess_ToACsAgent()
    {
        // The agent belongs to the ticket's department (and so, in the old model, to its queue). That is not a reason to see it.
        var f = CreateService();
        var agent = Guid.NewGuid();
        MemberOf(f, agent, CustomerService);
        var inMyDepartment = await SeedAsync(f, CustomerService);
        await SeedAsync(f, CustomerService, assignedTo: Guid.NewGuid());

        Assert.Empty(await QueueIdsAsync(f, agent, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(agent, Agent, inMyDepartment.TicketId)).Outcome);
    }

    [Fact]
    public async Task ANonexistentTicket_IsStillNotFound_NotForbidden()
    {
        var f = CreateService();

        Assert.Equal(TicketQueryOutcome.NotFound, (await f.Query.GetDetailAsync(Guid.NewGuid(), Agent, 9999)).Outcome);
        Assert.Equal(TicketAccess.NotFound, await f.Query.CheckAccessAsync(Guid.NewGuid(), Agent, 9999));
    }

    // =====================================================================
    // Genesys handling
    // =====================================================================

    [Fact]
    public async Task HandlingATicketOnGenesys_GrantsNothingByItself_WhetherUnassignedOrAssignedToSomeoneElse()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        var unassigned = await SeedAsync(f);
        var elsewhere = await SeedAsync(f, assignedTo: Guid.NewGuid());
        await HandledOnGenesysAsync(f, unassigned, agent);
        await HandledOnGenesysAsync(f, elsewhere, agent);

        Assert.Empty(await QueueIdsAsync(f, agent, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(agent, Agent, unassigned.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(agent, Agent, elsewhere.TicketId)).Outcome);
    }

    [Fact]
    public async Task ATicketHandledByAnotherAgent_StaysHiddenFromThisOne()
    {
        var f = CreateService();
        var me = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var ticket = await SeedAsync(f);
        await HandledOnGenesysAsync(f, ticket, colleague);

        Assert.Empty(await QueueIdsAsync(f, me, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(me, Agent, ticket.TicketId)).Outcome);
    }

    [Fact]
    public async Task ATicketBothAssignedAndHandled_AppearsOnce()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        var ticket = await SeedAsync(f, assignedTo: agent);
        await HandledOnGenesysAsync(f, ticket, agent);
        await HandledOnGenesysAsync(f, ticket, agent, interactionId: 777); // a second interaction of the same ticket

        var queue = await f.Query.GetQueueAsync(agent, Agent, AllTickets());

        Assert.Equal(ticket.TicketId, Assert.Single(queue.Items).TicketId);
        Assert.Equal(1, queue.TotalCount);
    }

    // =====================================================================
    // Transfers: several agents handled the same interaction
    // =====================================================================

    [Fact]
    public async Task ATransferredCall_IsVisibleOnlyToTheAgentItIsNowAssignedTo()
    {
        var f = CreateService();
        var firstAgent = Guid.NewGuid();
        var receivingAgent = Guid.NewGuid();
        var thirdAgent = Guid.NewGuid();
        var ticket = await SeedAsync(f, assignedTo: receivingAgent);
        await HandledOnGenesysAsync(f, ticket, firstAgent);
        await HandledOnGenesysAsync(f, ticket, receivingAgent);

        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, receivingAgent, Roles.CsAgent));
        Assert.Empty(await QueueIdsAsync(f, firstAgent, Roles.CsAgent));   // handled it first, but it is the receiving agent's now
        Assert.Empty(await QueueIdsAsync(f, thirdAgent, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(firstAgent, Agent, ticket.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(thirdAgent, Agent, ticket.TicketId)).Outcome);
    }

    // =====================================================================
    // Reassignment
    // =====================================================================

    [Fact]
    public async Task Reassigning_TakesAssignmentAccessAwayFromThePreviousAssignee_AndGivesItToTheNew()
    {
        var f = CreateService();
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        var ticket = await SeedAsync(f, assignedTo: previous);
        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, previous, Roles.CsAgent));

        ticket.AssignTo(next);

        Assert.Empty(await QueueIdsAsync(f, previous, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(previous, Agent, ticket.TicketId)).Outcome);
        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, next, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Success, (await f.Query.GetDetailAsync(next, Agent, ticket.TicketId)).Outcome);
    }

    [Fact]
    public async Task Reassigning_ToAnotherCsAgent_TakesTheTicketFromThePreviousAgent_EvenIfTheyHandledTheCall()
    {
        var f = CreateService();
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        f.DepartmentAssignments.RolesByEmployee[next] = [Roles.CsAgent];
        var ticket = await SeedAsync(f, assignedTo: previous);
        ticket.RecordCreator(previous);
        await HandledOnGenesysAsync(f, ticket, previous);

        ticket.AssignTo(next);

        Assert.Empty(await QueueIdsAsync(f, previous, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(previous, Agent, ticket.TicketId)).Outcome);
        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, next, Roles.CsAgent));
    }

    [Fact]
    public async Task TransferringToAnotherDepartment_ClearsTheOwner_SoAnAssigneeWhoNeverHandledItLosesTheTicket()
    {
        var f = CreateService();
        var assignee = Guid.NewGuid();
        var ticket = await SeedAsync(f, assignedTo: assignee);

        ticket.TransferToDepartment(Facilities);

        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(await QueueIdsAsync(f, assignee, Roles.CsAgent));
    }

    // =====================================================================
    // Access that must not change
    // =====================================================================

    public static IEnumerable<object[]> UnrestrictedRoles() =>
    [
        [Roles.CsSupervisor], [Roles.CsManager], [Roles.GeneralManager], [Roles.ChairmanCeo], [Roles.SystemAdministrator]
    ];

    [Theory]
    [MemberData(nameof(UnrestrictedRoles))]
    public async Task SupervisorManagerExecutiveAndAdmin_StillSeeEveryTicket(string role)
    {
        var f = CreateService();
        var unassigned = await SeedAsync(f, CustomerService);
        var elsewhere = await SeedAsync(f, Facilities, assignedTo: Guid.NewGuid());
        var caller = Guid.NewGuid();

        Assert.Equal([unassigned.TicketId, elsewhere.TicketId], await QueueIdsAsync(f, caller, role));
        Assert.Equal(TicketQueryOutcome.Success, (await f.Query.GetDetailAsync(caller, [role], unassigned.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Success, (await f.Query.GetDetailAsync(caller, [role], elsewhere.TicketId)).Outcome);
        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(caller, [role], elsewhere.TicketId));
    }

    [Fact]
    public async Task APersonWhoIsBothAgentAndSupervisor_KeepsTheSupervisorsFullView()
    {
        var f = CreateService();
        var both = Guid.NewGuid();
        var first = await SeedAsync(f);
        var second = await SeedAsync(f, Facilities, assignedTo: Guid.NewGuid());

        Assert.Equal([first.TicketId, second.TicketId], await QueueIdsAsync(f, both, Roles.CsAgent, Roles.CsSupervisor));
        Assert.False(f.Query.IsRestrictedAgent(both, [Roles.CsAgent, Roles.CsSupervisor]));
        Assert.True(f.Query.IsRestrictedAgent(both, [Roles.CsAgent]));
    }

    [Fact]
    public async Task ADepartmentEmployee_SeesOnlyTicketsAssignedToThem_NotTheirDepartmentsTickets()
    {
        var f = CreateService();
        var employee = Guid.NewGuid();
        MemberOf(f, employee, Facilities);
        await SeedAsync(f, CustomerService);                                    // another department, unassigned
        await SeedAsync(f, Facilities);                                         // their department, unassigned
        await SeedAsync(f, Facilities, assignedTo: Guid.NewGuid());             // their department, a colleague's
        var mine = await SeedAsync(f, Facilities, assignedTo: employee);        // theirs
        var mineElsewhere = await SeedAsync(f, CustomerService, assignedTo: employee);   // theirs, in a department they do not belong to

        Assert.Equal(new[] { mine.TicketId, mineElsewhere.TicketId }.Order(), await QueueIdsAsync(f, employee, Roles.DepartmentEmployee));
    }

    [Fact]
    public async Task ADepartmentHead_KeepsTheirDepartmentView_PlusTheirOwnTickets()
    {
        var f = CreateService();
        var head = Guid.NewGuid();
        MemberOf(f, head, Facilities);
        var inMyDepartment = await SeedAsync(f, Facilities);
        var assignedToMe = await SeedAsync(f, CustomerService, assignedTo: head);
        await SeedAsync(f, CustomerService);   // neither

        Assert.Equal(new[] { inMyDepartment.TicketId, assignedToMe.TicketId }.Order(), await QueueIdsAsync(f, head, Roles.DepartmentHead));
    }

    [Fact]
    public async Task AnAgentWhoIsAlsoADepartmentEmployee_GetsNoDepartmentView_OnlyTheirOwnTickets()
    {
        var f = CreateService();
        var person = Guid.NewGuid();
        MemberOf(f, person, Facilities);
        await SeedAsync(f, Facilities);   // department membership as an Employee grants nothing
        var assignedToMe = await SeedAsync(f, CustomerService, assignedTo: person);
        await SeedAsync(f, CustomerService); // neither

        Assert.Equal([assignedToMe.TicketId], await QueueIdsAsync(f, person, Roles.CsAgent, Roles.DepartmentEmployee));
    }

    [Fact]
    public async Task AnAgentWhoIsAlsoADepartmentHead_KeepsThatRolesDepartmentView_PlusTheirOwnTickets()
    {
        var f = CreateService();
        var person = Guid.NewGuid();
        MemberOf(f, person, Facilities);
        var inMyDepartment = await SeedAsync(f, Facilities);
        var assignedToMe = await SeedAsync(f, CustomerService, assignedTo: person);
        await SeedAsync(f, CustomerService); // neither

        Assert.Equal(
            new[] { inMyDepartment.TicketId, assignedToMe.TicketId }.Order(),
            await QueueIdsAsync(f, person, Roles.CsAgent, Roles.DepartmentHead));
    }

    [Fact]
    public async Task TheGenesysIntegrationAccount_ACsAgentByPermissionSetButNotAPerson_IsNotRestricted()
    {
        var integration = Guid.NewGuid();
        var f = CreateService(integration);
        var ticket = await SeedAsync(f);
        ticket.RecordCreator(integration); // its visibility is the Genesys-created tickets (TicketVisibilityByRoleTests covers the rest)

        Assert.False(f.Query.IsRestrictedAgent(integration, Agent));
        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, integration, Roles.CsAgent));
        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(integration, Agent, ticket.TicketId));
    }

    // =====================================================================
    // The rule itself
    // =====================================================================

    [Theory]
    [InlineData(true, Roles.CsAgent)]
    [InlineData(true, Roles.CsAgent, Roles.DepartmentEmployee)]
    [InlineData(true, Roles.CsAgent, Roles.ReportingUser)]
    [InlineData(false, Roles.CsAgent, Roles.CsSupervisor)]
    [InlineData(false, Roles.CsAgent, Roles.CsManager)]
    [InlineData(false, Roles.CsAgent, Roles.GeneralManager)]
    [InlineData(false, Roles.CsAgent, Roles.ChairmanCeo)]
    [InlineData(false, Roles.CsAgent, Roles.SystemAdministrator)]
    [InlineData(false, Roles.CsSupervisor)]
    [InlineData(false, Roles.DepartmentEmployee)]
    [InlineData(false, Roles.DepartmentHead)]
    [InlineData(false, Roles.SystemAdministrator)]
    public void OnlyACsAgentWithNoOtherCrossDepartmentRole_IsRestricted(bool expected, params string[] roles) =>
        Assert.Equal(expected, TicketVisibilityRule.IsRestrictedCsAgent(roles));

    [Fact]
    public void TheAgentScopeIsAlwaysTheCallerThemselves_AndOnlyForARestrictedAgent()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();

        Assert.Equal(agent, f.Query.ResolveAgentScope(agent, Agent));
        Assert.Null(f.Query.ResolveAgentScope(agent, [Roles.CsSupervisor]));
        Assert.Null(f.Query.ResolveAgentScope(agent, [Roles.DepartmentEmployee]));
    }

    // =====================================================================
    // Dashboard counts
    // =====================================================================

    [Fact]
    public async Task DashboardCounts_CoverOnlyTheTicketsTheAgentCanSee()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        await SeedAsync(f, assignedTo: agent);                       // assigned to me
        var handled = await SeedAsync(f);                            // handled by me, unassigned: not mine
        await HandledOnGenesysAsync(f, handled, agent);
        await SeedAsync(f, assignedTo: Guid.NewGuid());              // someone else's
        await SeedAsync(f);                                          // unassigned, nobody handled it

        var agentView = await f.Dashboard.GetSummaryAsync(agent, Agent);
        var supervisorView = await f.Dashboard.GetSummaryAsync(Guid.NewGuid(), [Roles.CsSupervisor]);

        Assert.Equal(1, agentView.OpenTickets);
        Assert.Equal(1, agentView.MyTickets);
        Assert.Equal(0, agentView.Unassigned);   // an unassigned ticket is never an agent's, handled or not
        Assert.Single(agentView.AttentionTickets);
        Assert.Equal(4, supervisorView.OpenTickets);
        Assert.Equal(2, supervisorView.Unassigned);
    }

    // =====================================================================
    // Ticket creator: read-only access that survives a department transfer
    // =====================================================================

    /// <summary>A ticket <paramref name="creator"/> created in <paramref name="departmentId"/> and then moved to <paramref name="movedTo"/>.</summary>
    private static async Task<Ticket> CreatedThenTransferredAsync(Fixture f, Guid creator, int departmentId = CustomerService, int movedTo = Facilities)
    {
        var ticket = await SeedAsync(f, departmentId, assignedTo: creator);
        ticket.RecordCreator(creator);
        ticket.TransferToDepartment(movedTo); // clears the owner, as a real transfer does
        return ticket;
    }

    [Fact]
    public async Task TheCreator_NoLongerOpensAnUnassignedTicketThatMovedToAnotherDepartment()
    {
        var f = CreateService();
        var creator = Guid.NewGuid();
        MemberOf(f, creator, CustomerService);
        var ticket = await CreatedThenTransferredAsync(f, creator);

        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(creator, Agent, ticket.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetLifecycleHistoryAsync(creator, Agent, ticket.TicketId)).Outcome);
        Assert.Empty(await QueueIdsAsync(f, creator, Roles.CsAgent));
        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(creator, Agent, ticket.TicketId));
    }

    [Fact]
    public async Task ABusinessTransfer_KeepsTheCsAgentAsOwner_SoTheyKeepFullAccess()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        var ticket = await SeedAsync(f, CustomerService, assignedTo: agent);
        ticket.RecordCreator(agent);

        ticket.TransferToDepartment(Facilities, preserveOwner: true);   // department classification / business transfer keep the owner

        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(agent, Agent, ticket.TicketId));
        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, agent, Roles.CsAgent));
    }

    [Fact]
    public async Task AReleasedTransfer_LeavesTheTicketUnassigned_SoTheCreatorNoLongerSeesIt()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        var ticket = await SeedAsync(f, CustomerService, assignedTo: agent);
        ticket.RecordCreator(agent);

        ticket.TransferToDepartment(Facilities);   // system routing that clears the owner

        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(agent, Agent, ticket.TicketId));
        Assert.Empty(await QueueIdsAsync(f, agent, Roles.CsAgent));
    }

    [Fact]
    public async Task ACreatorAssignedBackHasFullAccess_ButOneWhoOnlyHandledTheCallHasNone()
    {
        var f = CreateService();
        var creator = Guid.NewGuid();
        var assignedBack = await CreatedThenTransferredAsync(f, creator);
        assignedBack.AssignTo(creator);
        var handled = await CreatedThenTransferredAsync(f, creator);
        await HandledOnGenesysAsync(f, handled, creator);

        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(creator, Agent, assignedBack.TicketId));
        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(creator, Agent, handled.TicketId));
    }

    [Fact]
    public async Task AnotherAgent_CannotOpenATicketSomeoneElseCreated_EvenInTheSameDepartmentOrAfterTheTransfer()
    {
        var f = CreateService();
        var creator = Guid.NewGuid();
        var other = Guid.NewGuid();
        MemberOf(f, other, CustomerService);
        MemberOf(f, other, Facilities);
        var ticket = await CreatedThenTransferredAsync(f, creator);

        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Query.GetDetailAsync(other, Agent, ticket.TicketId)).Outcome);
        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(other, Agent, ticket.TicketId));
        Assert.Empty(await QueueIdsAsync(f, other, Roles.CsAgent));
    }

    [Fact]
    public async Task CreatedByMe_NarrowsToTheCallersVisibleCreations_AndNeverWidensAnything()
    {
        var f = CreateService();
        var me = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var movedAway = await CreatedThenTransferredAsync(f, me);      // unassigned now: no longer mine to see
        var stillMine = await SeedAsync(f, assignedTo: me);
        stillMine.RecordCreator(me);
        var assignedButCreatedByColleague = await SeedAsync(f, assignedTo: me);
        assignedButCreatedByColleague.RecordCreator(colleague);
        var colleaguesOwn = await SeedAsync(f, assignedTo: colleague);
        colleaguesOwn.RecordCreator(colleague);

        var created = (await f.Query.GetQueueAsync(
            me, Agent, AllTickets() with { CreatedByMe = true })).Items.Select(i => i.TicketId).Order().ToArray();
        var notFiltered = await QueueIdsAsync(f, me, Roles.CsAgent);

        Assert.Equal([stillMine.TicketId], created);
        Assert.Equal([stillMine.TicketId, assignedButCreatedByColleague.TicketId], notFiltered);
        Assert.DoesNotContain(colleaguesOwn.TicketId, notFiltered);
        Assert.DoesNotContain(movedAway.TicketId, notFiltered);
        // The filter cannot be pointed at someone else: it always means the caller.
        Assert.DoesNotContain(
            (await f.Query.GetQueueAsync(colleague, Agent, AllTickets() with { CreatedByMe = true, OwnerEmployeeId = me })).Items,
            i => i.TicketId == movedAway.TicketId);
    }

    [Fact]
    public async Task ASupervisorsCreatedByMe_IsStillJustTheirOwnCreations()
    {
        var f = CreateService();
        var supervisor = Guid.NewGuid();
        var mine = await SeedAsync(f);
        mine.RecordCreator(supervisor);
        var someoneElses = await SeedAsync(f);
        someoneElses.RecordCreator(Guid.NewGuid());

        var all = (await f.Query.GetQueueAsync(supervisor, [Roles.CsSupervisor], AllTickets())).Items.Count;
        var created = (await f.Query.GetQueueAsync(supervisor, [Roles.CsSupervisor], AllTickets() with { CreatedByMe = true })).Items;

        Assert.Equal(2, all);
        Assert.Equal(mine.TicketId, Assert.Single(created).TicketId);
    }

    [Fact]
    public async Task TheDashboard_DoesNotCountACreatorsUnassignedMovedTicket_LikeTheList()
    {
        var f = CreateService();
        var creator = Guid.NewGuid();
        await CreatedThenTransferredAsync(f, creator);
        await SeedAsync(f); // unrelated

        var summary = await f.Dashboard.GetSummaryAsync(creator, Agent);

        Assert.Equal(0, summary.OpenTickets);
    }

    // =====================================================================
    // Department work: the responsible CS Agent follows up and closes
    // =====================================================================

    [Fact]
    public async Task WhenADepartmentEmployeeWorksTheTicket_TheResponsibleCsAgentKeepsFullAccess_AndOtherAgentsDoNot()
    {
        var f = CreateService();
        var responsible = Guid.NewGuid();
        var otherAgent = Guid.NewGuid();
        var departmentEmployee = Guid.NewGuid();   // holds no CS Agent role
        var ticket = await SeedAsync(f, Facilities, assignedTo: departmentEmployee);
        f.DepartmentAssignments.ResponsibleCsAgentByTicket[ticket.TicketId] = responsible;

        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, responsible, Roles.CsAgent));
        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(responsible, Agent, ticket.TicketId));   // Full: follow up AND close
        Assert.Empty(await QueueIdsAsync(f, otherAgent, Roles.CsAgent));
        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(otherAgent, Agent, ticket.TicketId));
    }

    [Fact]
    public async Task OnceTheTicketIsResolvedAndClosed_TheResponsibleCsAgentStillSeesIt_WhileItRemainsAssignedToTheDepartmentEmployee()
    {
        var f = CreateService();
        var responsible = Guid.NewGuid();
        var ticket = await SeedAsync(f, Facilities, assignedTo: Guid.NewGuid());
        f.DepartmentAssignments.ResponsibleCsAgentByTicket[ticket.TicketId] = responsible;
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(responsible, Agent, ticket.TicketId));   // Resolved: ready for the CS Agent to close

        ticket.Close();

        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(responsible, Agent, ticket.TicketId));
        Assert.Equal([ticket.TicketId], await QueueIdsAsync(f, responsible, Roles.CsAgent));
    }

    [Fact]
    public async Task WhenTheDepartmentHandsTheTicketToAnotherCsAgent_TheEarlierResponsibleAgentLosesIt()
    {
        var f = CreateService();
        var earlier = Guid.NewGuid();
        var newAgent = Guid.NewGuid();
        f.DepartmentAssignments.RolesByEmployee[newAgent] = [Roles.CsAgent];
        var ticket = await SeedAsync(f, Facilities, assignedTo: Guid.NewGuid());
        f.DepartmentAssignments.ResponsibleCsAgentByTicket[ticket.TicketId] = earlier;
        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(earlier, Agent, ticket.TicketId));

        ticket.AssignTo(newAgent);   // another CS Agent now owns it

        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(earlier, Agent, ticket.TicketId));
        Assert.Empty(await QueueIdsAsync(f, earlier, Roles.CsAgent));
        Assert.Equal(TicketAccess.Allowed, await f.Query.CheckAccessAsync(newAgent, Agent, ticket.TicketId));
    }

    [Fact]
    public async Task AnAgentWhoIsNotTheResponsibleAgent_NeverSeesDepartmentWork_EvenWhenTheyCreatedOrHandledTheTicket()
    {
        var f = CreateService();
        var responsible = Guid.NewGuid();
        var creatorAndHandler = Guid.NewGuid();
        var ticket = await SeedAsync(f, Facilities, assignedTo: Guid.NewGuid());
        ticket.RecordCreator(creatorAndHandler);
        await HandledOnGenesysAsync(f, ticket, creatorAndHandler);
        f.DepartmentAssignments.ResponsibleCsAgentByTicket[ticket.TicketId] = responsible;

        Assert.Equal(TicketAccess.Denied, await f.Query.CheckAccessAsync(creatorAndHandler, Agent, ticket.TicketId));
        Assert.Empty(await QueueIdsAsync(f, creatorAndHandler, Roles.CsAgent));
    }

    [Fact]
    public async Task SupervisorManagerAndAdministrator_StillSeeEveryTicket()
    {
        var f = CreateService();
        await SeedAsync(f, assignedTo: Guid.NewGuid());
        await SeedAsync(f);
        await SeedAsync(f, Facilities, assignedTo: Guid.NewGuid());

        foreach (var role in new[] { Roles.CsSupervisor, Roles.CsManager, Roles.SystemAdministrator })
        {
            Assert.Equal(3, (await QueueIdsAsync(f, Guid.NewGuid(), role)).Length);
        }
    }

    [Fact]
    public void TheTicketRecordsItsCreatorOnce()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20261030-9999", CustomerService, 10, 20, 5, (byte)PriorityLevel.High, "Issue", DateTime.UtcNow);
        var first = Guid.NewGuid();

        Assert.True(ticket.RecordCreator(first));
        Assert.False(ticket.RecordCreator(Guid.NewGuid()));
        Assert.Equal(first, ticket.CreatedByEmployeeId);
        Assert.Throws<ArgumentException>(() => ticket.RecordCreator(Guid.Empty));
    }
}
