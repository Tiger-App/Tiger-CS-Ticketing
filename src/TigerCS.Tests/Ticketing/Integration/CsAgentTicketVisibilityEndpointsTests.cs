using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// CS Agent ticket visibility through the REAL HTTP pipeline: the Genesys endpoints create the tickets and report who connected, the
/// agents then try the API and direct ticket URLs. An agent must see a ticket only when it is assigned to them or they handled one of
/// its Genesys interactions; a stranger must be refused on <i>every</i> ticket endpoint — reads and writes — before anything runs; and
/// Supervisor/Manager/Administrator/executive access must be exactly what it was.
/// </summary>
public sealed class CsAgentTicketVisibilityEndpointsTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientAsync(string role = Roles.CsAgent, int? departmentId = null, string? genesysUserId = null)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        if (departmentId is { } department)
        {
            await _factory.AssignPrimaryDepartmentAsync(employeeId, department);
        }

        if (genesysUserId is not null)
        {
            await _factory.MapGenesysAgentAsync(employeeId, genesysUserId);
        }

        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private static string NewGenesysId() => "ga-" + Guid.NewGuid().ToString("N");

    private int _departmentId;

    private async Task<int> DepartmentAsync()
    {
        if (_departmentId == 0)
        {
            await _factory.SeedPrioritiesAsync();
            _departmentId = await _factory.CreateDepartmentAsync("Visibility " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        }

        return _departmentId;
    }

    /// <summary>A phone ticket created through the Genesys inquiry endpoint, optionally naming the agent who answered.</summary>
    private async Task<(long TicketId, string ConversationId)> GenesysTicketAsync(HttpClient genesys, string? answeredBy = null)
    {
        var conversationId = "conv-" + Guid.NewGuid().ToString("N");
        var response = await genesys.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(
                conversationId, "Phone", CustomerPhone: "+971500000001", DepartmentId: await DepartmentAsync(), AgentId: answeredBy));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();
        return (created!.TicketId, conversationId);
    }

    /// <summary>Genesys reports an agent connecting (a transfer to them, or them taking the call).</summary>
    private static async Task ConnectAsync(HttpClient genesys, long ticketId, string conversationId, string genesysAgentId)
    {
        var response = await genesys.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Routing: new GenesysRoutingPart(QueueId: "q-1", AgentId: genesysAgentId, AgentName: "Agent")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpStatusCode> DetailStatusAsync(HttpClient client, long ticketId) =>
        (await client.GetAsync($"/api/tickets/{ticketId}")).StatusCode;

    /// <summary>A CS Manager assigns the ticket (CS Manager is the cross-department assigner); the assignee must belong to its department.</summary>
    private async Task AssignAsync(HttpClient manager, long ticketId, Guid assignee)
    {
        await _factory.AssignPrimaryDepartmentAsync(assignee, await DepartmentAsync());
        var current = await (await manager.GetAsync($"/api/tickets/{ticketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        var response = await manager.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment",
            new AssignTicketRequestDto(
                assignee, Convert.FromBase64String(current!.RowVersion),
                // Reassigning an owned ticket must say why (ticket transfer tracking); the first assignment out of the queue needs none.
                current.CurrentOwnerEmployeeId is null ? null : TigerCS.Domain.Modules.Ticketing.TicketTransferReason.Other));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<long[]> ListedIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tickets?pageSize=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<TicketListResultDto>();
        return [.. page!.Items.Select(i => i.TicketId).Order()];
    }

    // =====================================================================
    // The list
    // =====================================================================

    [Fact]
    public async Task TheTicketList_HoldsOnlyTheTicketsAssignedToTheAgentOrHandledByThem()
    {
        var (genesys, _) = await ClientAsync();
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var gA = NewGenesysId();
        var (agentA, agentAId) = await ClientAsync(genesysUserId: gA);

        var (answered, _) = await GenesysTicketAsync(genesys, answeredBy: gA);   // A answered the call
        var (assigned, _) = await GenesysTicketAsync(genesys);                   // assigned to A by a manager
        await AssignAsync(manager, assigned, agentAId);
        var (neither, _) = await GenesysTicketAsync(genesys);                    // same department, nobody

        var seen = await ListedIdsAsync(agentA);

        Assert.Equal([answered, assigned], seen);
        Assert.DoesNotContain(neither, seen);
        Assert.Contains(neither, await ListedIdsAsync(manager));
    }

    // =====================================================================
    // Direct URLs and API calls by a stranger
    // =====================================================================

    public static IEnumerable<object[]> TicketReads() =>
    [
        [""], ["/history"], ["/interactions"], ["/notes"], ["/approvals"], ["/reviews"], ["/customer-history"],
        ["/customer-profile"], ["/sla"], ["/escalations"], ["/sla/priority-downgrade-requests"]
    ];

    [Theory]
    [MemberData(nameof(TicketReads))]
    public async Task ByDirectUrl_AStrangerIsRefusedOnEveryTicketRead_WhileTheAgentWhoHandledItIsNot(string suffix)
    {
        var (genesys, _) = await ClientAsync();
        var gA = NewGenesysId();
        var (handler, _) = await ClientAsync(genesysUserId: gA);
        var (stranger, _) = await ClientAsync();
        var (ticketId, _) = await GenesysTicketAsync(genesys, answeredBy: gA);

        var refused = await stranger.GetAsync($"/api/tickets/{ticketId}{suffix}");
        var allowed = await handler.GetAsync($"/api/tickets/{ticketId}{suffix}");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
    }

    public static IEnumerable<object[]> TicketWrites() =>
    [
        ["classification"], ["assignment"], ["transfer"], ["status"], ["resolution"], ["close"], ["reopen"], ["notes"],
        ["approvals"], ["workflow-events"], ["reconciliation"], ["sla/first-response"], ["escalations"],
        ["sla/priority-downgrade-requests"]
    ];

    [Theory]
    [MemberData(nameof(TicketWrites))]
    public async Task ByDirectApiCall_AStrangerCannotChangeATicket_EvenWithAnEmptyBody(string suffix)
    {
        var (genesys, _) = await ClientAsync();
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var (stranger, _) = await ClientAsync();
        var (ticketId, _) = await GenesysTicketAsync(genesys);
        var before = await (await manager.GetAsync($"/api/tickets/{ticketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();

        // An empty body would be a 400 once bound; a 403 proves the stranger is turned away BEFORE the request is even read.
        var response = await stranger.PostAsJsonAsync($"/api/tickets/{ticketId}/{suffix}", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await (await manager.GetAsync($"/api/tickets/{ticketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(before!.RowVersion, after!.RowVersion);
        Assert.Equal(before.TicketStatus, after.TicketStatus);
    }

    [Fact]
    public async Task ANonexistentTicket_IsNotFound_ForAnAgent_NotForbidden()
    {
        var (agent, _) = await ClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, await DetailStatusAsync(agent, 987654321));
    }

    // =====================================================================
    // Assignment and reassignment
    // =====================================================================

    [Fact]
    public async Task AssigningGivesTheAgentTheTicket_AndReassigningTakesItBack()
    {
        var (genesys, _) = await ClientAsync();
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var (previous, previousId) = await ClientAsync();
        var (next, nextId) = await ClientAsync();
        var (ticketId, _) = await GenesysTicketAsync(genesys);

        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(previous, ticketId));  // unassigned: nobody's

        await AssignAsync(manager, ticketId, previousId);
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(previous, ticketId));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(next, ticketId));

        await AssignAsync(manager, ticketId, nextId);                                         // reassigned
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(previous, ticketId));  // lost: it was only an assignment
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(next, ticketId));
    }

    [Fact]
    public async Task ReassigningToAnotherCsAgent_TakesTheTicketFromTheAgentWhoAnsweredTheCall()
    {
        var (genesys, _) = await ClientAsync();
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var gA = NewGenesysId();
        var (answerer, answererId) = await ClientAsync(genesysUserId: gA);
        var (other, otherId) = await ClientAsync();
        var (ticketId, _) = await GenesysTicketAsync(genesys, answeredBy: gA);

        await AssignAsync(manager, ticketId, answererId);
        await AssignAsync(manager, ticketId, otherId);

        // Now another CS Agent's ticket: gone from the first agent's list and closed to them by URL, even though they answered the call.
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(answerer, ticketId));
        Assert.DoesNotContain(ticketId, await ListedIdsAsync(answerer));
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(other, ticketId));
        Assert.Contains(ticketId, await ListedIdsAsync(other));
        Assert.Equal(otherId, (await _factory.GetTicketAsync(ticketId))!.CurrentOwnerEmployeeId);   // ownership untouched
    }

    [Fact]
    public async Task FourCsAgents_EachSeeOnlyTheirOwnTickets_ByListCountSearchAndDirectUrl_WhileSupervisorManagerAndAdminSeeThemAll()
    {
        var (genesys, _) = await ClientAsync();
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor);
        var (admin, _) = await ClientAsync(Roles.SystemAdministrator);

        // Shahd, Esraa, Moaz and Bader: four CS Agents, two tickets each, every ticket answered on the phone by Shahd.
        var gShahd = NewGenesysId();
        var agents = new List<(string Name, HttpClient Client, Guid Id, List<long> Tickets)>();
        foreach (var name in new[] { "Shahd", "Esraa", "Moaz", "Bader" })
        {
            var (client, id) = await ClientAsync(genesysUserId: name == "Shahd" ? gShahd : NewGenesysId());
            agents.Add((name, client, id, []));
        }

        foreach (var agent in agents)
        {
            for (var i = 0; i < 2; i++)
            {
                var (ticketId, _) = await GenesysTicketAsync(genesys, answeredBy: gShahd);
                await AssignAsync(manager, ticketId, agent.Id);
                agent.Tickets.Add(ticketId);
            }
        }

        var everyTicket = agents.SelectMany(a => a.Tickets).Order().ToArray();
        foreach (var agent in agents)
        {
            var others = everyTicket.Except(agent.Tickets).ToArray();

            // The list (Queue / My Tickets / "All tickets I can see") and its count hold only this agent's tickets.
            var page = await (await agent.Client.GetAsync("/api/tickets?pageSize=200")).Content.ReadFromJsonAsync<TicketListResultDto>();
            Assert.Equal(agent.Tickets.Order(), page!.Items.Select(i => i.TicketId).Order().Where(everyTicket.Contains));
            Assert.Empty(page.Items.Select(i => i.TicketId).Intersect(others));

            // Searching for another agent's ticket by its number finds nothing; a hostile owner filter finds nothing either.
            foreach (var foreign in others.Take(2))
            {
                var number = (await _factory.GetTicketAsync(foreign))!.TicketNumber;
                var found = await (await agent.Client.GetAsync($"/api/tickets?search={number}&pageSize=50")).Content.ReadFromJsonAsync<TicketListResultDto>();
                Assert.DoesNotContain(foreign, found!.Items.Select(i => i.TicketId));
            }

            var foreignOwner = agents.First(a => a.Id != agent.Id).Id;
            var byOwner = await (await agent.Client.GetAsync($"/api/tickets?ownerEmployeeId={foreignOwner}&pageSize=200")).Content.ReadFromJsonAsync<TicketListResultDto>();
            Assert.Empty(byOwner!.Items.Select(i => i.TicketId).Intersect(others));

            // Direct URLs and the API: the agent's own open, everyone else's are refused.
            foreach (var own in agent.Tickets)
            {
                Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(agent.Client, own));
            }

            foreach (var foreign in others)
            {
                Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(agent.Client, foreign));   // including the call Shahd answered
                Assert.Equal(HttpStatusCode.Forbidden, (await agent.Client.GetAsync($"/api/tickets/{foreign}/history")).StatusCode);
            }
        }

        // Supervisor, Manager and Administrator see every one of them, with unchanged access.
        foreach (var privileged in new[] { supervisor, manager, admin })
        {
            Assert.Subset(new HashSet<long>(await ListedIdsAsync(privileged)), new HashSet<long>(everyTicket));
            foreach (var ticketId in everyTicket)
            {
                Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(privileged, ticketId));
            }
        }
    }

    // =====================================================================
    // Genesys handling and transfers, end to end
    // =====================================================================

    [Fact]
    public async Task ATransferredCall_IsVisibleToBothAgents_NotToAThird_AndAnAnsweredCallBeatsAnUnrelatedDepartment()
    {
        var (genesys, _) = await ClientAsync();
        var gA = NewGenesysId();
        var gB = NewGenesysId();
        var (first, _) = await ClientAsync(genesysUserId: gA);
        var (second, _) = await ClientAsync(genesysUserId: gB);
        var (third, _) = await ClientAsync(genesysUserId: NewGenesysId());
        var (ticketId, conversationId) = await GenesysTicketAsync(genesys, answeredBy: gA);

        // Before the transfer only the agent who answered has it.
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(first, ticketId));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(second, ticketId));

        await ConnectAsync(genesys, ticketId, conversationId, gB);                        // A transfers to B; B connects

        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(first, ticketId));        // the one who transferred away keeps it
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(second, ticketId));       // the one who received it has it
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(third, ticketId)); // a mapped agent who touched nothing
        Assert.Contains(ticketId, await ListedIdsAsync(first));
        Assert.Contains(ticketId, await ListedIdsAsync(second));
        Assert.DoesNotContain(ticketId, await ListedIdsAsync(third));
    }

    [Fact]
    public async Task AnAgentIdThatIsNotMappedToAUser_ABotOrAnUnknownAgent_GrantsNobodyTheTicket()
    {
        var (genesys, _) = await ClientAsync();
        var (stranger, _) = await ClientAsync(genesysUserId: NewGenesysId());
        var (ticketId, conversationId) = await GenesysTicketAsync(genesys, answeredBy: "the-virtual-agent");

        await ConnectAsync(genesys, ticketId, conversationId, "another-id-nobody-owns");

        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(stranger, ticketId));
    }

    [Fact]
    public async Task AnAgentMerelyOfferedTheWork_AHandoffAssignmentWithNoConnect_DoesNotGainTheTicket()
    {
        var (genesys, _) = await ClientAsync();
        var gOffered = NewGenesysId();
        var (offered, _) = await ClientAsync(genesysUserId: gOffered);
        var (ticketId, conversationId) = await GenesysTicketAsync(genesys);
        var raised = await genesys.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Handoff: new GenesysHandoffPart(Required: true, Trigger: "CustomerRequestedHuman")));
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);

        // Genesys says the work is assigned to this agent; they have not answered anything yet.
        var assigned = await genesys.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Handoff: new GenesysHandoffPart(AssignedAgentId: gOffered)));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(offered, ticketId));
    }

    // =====================================================================
    // Pending human work and the dashboard
    // =====================================================================

    [Fact]
    public async Task ThePendingWorkList_ShowsAnAgentOnlyWorkOnTicketsTheyCanSee()
    {
        var (genesys, _) = await ClientAsync();
        var gA = NewGenesysId();
        var (agent, _) = await ClientAsync(genesysUserId: gA);
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor);
        var (ticketId, conversationId) = await GenesysTicketAsync(genesys);
        var raised = await genesys.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Handoff: new GenesysHandoffPart(Required: true, Trigger: "CustomerRequestedHuman")));
        var handoffId = (await raised.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!.TicketAgentHandoffId!.Value;

        async Task<long[]> WorkSeenBy(HttpClient client) =>
            [.. (await (await client.GetAsync("/api/pending-customer-interactions")).Content.ReadFromJsonAsync<AgentHandoffListResultDto>())!
                .Items.Select(i => i.TicketAgentHandoffId)];

        Assert.DoesNotContain(handoffId, await WorkSeenBy(agent));          // waiting work nobody connected to is not theirs
        Assert.Contains(handoffId, await WorkSeenBy(supervisor));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { })).StatusCode);

        await ConnectAsync(genesys, ticketId, conversationId, gA);          // Genesys connects the agent

        Assert.Contains(handoffId, await WorkSeenBy(agent));
    }

    [Fact]
    public async Task TheDashboard_CountsOnlyTheAgentsOwnTickets_WhileASupervisorsIsUnchanged()
    {
        var (genesys, _) = await ClientAsync();
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var gA = NewGenesysId();
        var (agent, agentId) = await ClientAsync(genesysUserId: gA);
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor);
        await GenesysTicketAsync(genesys, answeredBy: gA);
        var (assigned, _) = await GenesysTicketAsync(genesys);
        await AssignAsync(manager, assigned, agentId);
        await GenesysTicketAsync(genesys);   // unrelated, same department

        var agentOverview = await (await agent.GetAsync("/api/dashboard/overview")).Content.ReadFromJsonAsync<DashboardOverviewDto>();
        var supervisorOverview = await (await supervisor.GetAsync("/api/dashboard/overview")).Content.ReadFromJsonAsync<DashboardOverviewDto>();
        var summary = await (await agent.GetAsync("/api/dashboard")).Content.ReadFromJsonAsync<DashboardSummaryDto>();

        Assert.Equal(2, agentOverview!.Kpis.OpenTickets);
        Assert.Equal(2, agentOverview.VolumeTotal);
        Assert.Equal(1, agentOverview.Kpis.MyTickets);
        Assert.Equal(2, summary!.OpenTickets);
        Assert.True(supervisorOverview!.Kpis.OpenTickets >= 3);
    }

    // =====================================================================
    // Access that must not change
    // =====================================================================

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task SupervisorManagerExecutiveAndAdministrator_StillOpenAnyTicket_ByDirectUrl(string role)
    {
        var (genesys, _) = await ClientAsync();
        var (client, _) = await ClientAsync(role);
        var (ticketId, _) = await GenesysTicketAsync(genesys);

        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(client, ticketId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{ticketId}/history")).StatusCode);
        Assert.Contains(ticketId, await ListedIdsAsync(client));
    }

    [Fact]
    public async Task APersonWhoIsBothAgentAndSupervisor_KeepsTheSupervisorsView()
    {
        var (genesys, _) = await ClientAsync();
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        await _factory.AddRoleAsync(employeeId, Roles.CsSupervisor);
        var both = _factory.CreateClient();
        var login = await (await both.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        both.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        var (ticketId, _) = await GenesysTicketAsync(genesys);

        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(both, ticketId));
    }

    // =====================================================================
    // Manually created tickets: the creating agent owns them
    // =====================================================================

    /// <summary>The wizard's last step: an intake record promoted to a ticket in <paramref name="departmentId"/>, as <paramref name="creator"/>.</summary>
    private async Task<TicketResponseDto> CreateManualTicketAsync(HttpClient creator, int departmentId, bool? departmentQueue = null)
    {
        var categoryId = await _factory.CreateCategoryAsync("Walk-in " + Guid.NewGuid(), departmentId);
        var intake = await (await creator.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000099", null, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var response = await creator.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "Walk-in question",
                AssignToDepartmentQueue: departmentQueue));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketResponseDto>())!;
    }

    [Fact]
    public async Task ACsAgentWhoCreatesATicketByHand_OwnsIt_AndCanOpenListAndWorkIt()
    {
        var department = await DepartmentAsync();
        var (creator, creatorId) = await ClientAsync(departmentId: department);
        var (colleague, _) = await ClientAsync(departmentId: department);

        var ticket = await CreateManualTicketAsync(creator, department);

        Assert.Equal(creatorId, await _factory.GetTicketCurrentOwnerAsync(ticket.TicketId));
        var detail = await (await creator.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(creatorId, detail!.CurrentOwnerEmployeeId);
        Assert.Contains(ticket.TicketId, await ListedIdsAsync(creator));
        Assert.Equal(HttpStatusCode.OK, (await creator.GetAsync($"/api/tickets/{ticket.TicketId}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(colleague, ticket.TicketId)); // a colleague still has no access
    }

    [Fact]
    public async Task WhenTheAgentSelectsTheDepartmentQueue_TheTicketStaysUnassigned_AndTheCreatorCanOnlyReadIt()
    {
        var department = await DepartmentAsync();
        var (creator, _) = await ClientAsync(departmentId: department);

        var ticket = await CreateManualTicketAsync(creator, department, departmentQueue: true);

        Assert.Null(await _factory.GetTicketCurrentOwnerAsync(ticket.TicketId));
        var detail = await (await creator.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.True(detail!.IsReadOnlyAccess); // theirs to follow, not to work: it is in the queue, not assigned to them
    }

    [Fact]
    public async Task AnAgentOutsideTheTicketsDepartment_IsNotSelfAssigned()
    {
        var department = await DepartmentAsync();
        var otherDepartment = await _factory.CreateDepartmentAsync("Other " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (creator, _) = await ClientAsync(departmentId: otherDepartment);

        var ticket = await CreateManualTicketAsync(creator, department);

        Assert.Null(await _factory.GetTicketCurrentOwnerAsync(ticket.TicketId));
    }

    [Fact]
    public async Task ASupervisorWhoCreatesATicket_IsNotSelfAssigned_AsBefore()
    {
        var department = await DepartmentAsync();
        var (creator, _) = await ClientAsync(Roles.CsSupervisor, departmentId: department);

        var ticket = await CreateManualTicketAsync(creator, department);

        Assert.Null(await _factory.GetTicketCurrentOwnerAsync(ticket.TicketId));
    }

    // =====================================================================
    // Awaiting Human Agent: view and claim waiting work in the agent's department
    // =====================================================================

    private async Task<(long TicketId, long HandoffId)> WaitingWorkAsync(HttpClient genesys)
    {
        var (ticketId, conversationId) = await GenesysTicketAsync(genesys);
        var raised = await genesys.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticketId}",
            new GenesysTicketUpdateRequest(conversationId, Handoff: new GenesysHandoffPart(Required: true, Trigger: "CustomerRequestedHuman")));
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        return (ticketId, (await raised.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!.TicketAgentHandoffId!.Value);
    }

    private static async Task<long[]> WorkListedAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/pending-customer-interactions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<AgentHandoffListResultDto>())!.Items.Select(i => i.TicketAgentHandoffId)];
    }

    [Fact]
    public async Task AnAgent_SeesAndClaimsWaitingWorkInTheirDepartment_WithoutGainingGeneralTicketAccess()
    {
        var (genesys, _) = await ClientAsync();
        var department = await DepartmentAsync();
        var (agent, agentId) = await ClientAsync(departmentId: department);
        var (ticketId, handoffId) = await WaitingWorkAsync(genesys);

        Assert.Contains(handoffId, await WorkListedAsync(agent));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(agent, ticketId));          // listing the work opened nothing
        Assert.DoesNotContain(ticketId, await ListedIdsAsync(agent));

        var claim = await agent.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal(agentId, (await claim.Content.ReadFromJsonAsync<AgentHandoffDto>())!.AssignedEmployeeId);
        Assert.Equal(agentId, await _factory.GetTicketCurrentOwnerAsync(ticketId));
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(agent, ticketId));                 // now theirs
        Assert.Contains(ticketId, await ListedIdsAsync(agent));
    }

    [Fact]
    public async Task AnAgentOfAnotherDepartment_NeitherSeesNorClaimsThatWork()
    {
        var (genesys, _) = await ClientAsync();
        await DepartmentAsync();
        var otherDepartment = await _factory.CreateDepartmentAsync("Other " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (outsider, _) = await ClientAsync(departmentId: otherDepartment);
        var (ticketId, handoffId) = await WaitingWorkAsync(genesys);

        Assert.DoesNotContain(handoffId, await WorkListedAsync(outsider));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { })).StatusCode);
        Assert.Null(await _factory.GetTicketCurrentOwnerAsync(ticketId));
    }

    [Fact]
    public async Task ASecondAgentInTheDepartment_Gets409NamingTheHolder_WhenTheWorkIsAlreadyClaimed()
    {
        var (genesys, _) = await ClientAsync();
        var department = await DepartmentAsync();
        var (first, firstId) = await ClientAsync(departmentId: department);
        var (second, _) = await ClientAsync(departmentId: department);
        var (ticketId, handoffId) = await WaitingWorkAsync(genesys);
        Assert.Contains(handoffId, await WorkListedAsync(second));

        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { })).StatusCode);
        var loser = await second.PostAsJsonAsync($"/api/pending-customer-interactions/{handoffId}/start", new { });

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Contains(firstId.ToString(), await loser.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstId, await _factory.GetTicketCurrentOwnerAsync(ticketId));
        Assert.DoesNotContain(handoffId, await WorkListedAsync(second));                            // no longer offered to them
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(second, ticketId));
    }

    [Fact]
    public async Task TheClaimException_DoesNotLetAnAgentCancelOrCompleteWorkTheyHaveNotClaimed()
    {
        var (genesys, _) = await ClientAsync();
        var department = await DepartmentAsync();
        var (agent, _) = await ClientAsync(departmentId: department);
        var (_, handoffId) = await WaitingWorkAsync(genesys);

        var cancel = await agent.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoffId}/cancel", new CancelAgentHandoffRequestDto("not needed"));
        var complete = await agent.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoffId}/complete", new CompleteAgentHandoffRequestDto("done"));

        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, complete.StatusCode);
    }

    // =====================================================================
    // Ticket creator: read-only access that survives a department transfer
    // =====================================================================

    /// <summary>A CS Manager moves the ticket to <paramref name="targetDepartmentId"/> — the existing Department Transfer, which also clears the owner.</summary>
    /// <summary>
    /// A transfer keeps the ticket's CS Agent, so handing the work to the receiving team is an EXPLICIT reassignment: the manager names
    /// <paramref name="assignTo"/> (default: a fresh employee of the receiving department).
    /// </summary>
    private async Task TransferAsync(HttpClient manager, long ticketId, int targetDepartmentId, Guid? assignTo = null)
    {
        if (assignTo is null)
        {
            var (_, _, receiverId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
            await _factory.AssignPrimaryDepartmentAsync(receiverId, targetDepartmentId);
            assignTo = receiverId;
        }

        var current = await (await manager.GetAsync($"/api/tickets/{ticketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        var response = await manager.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/transfer",
            new TransferTicketRequestDto(targetDepartmentId, "Handled by the receiving team.", Convert.FromBase64String(current!.RowVersion), assignTo));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<int> NewDepartmentAsync() =>
        await _factory.CreateDepartmentAsync("Receiving " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A ticket <paramref name="creator"/> raised in their own department (so it is theirs) and a CS Manager then transferred to another.</summary>
    private async Task<(long TicketId, int ReceivingDepartmentId, HttpClient Manager)> CreatedThenTransferredAsync(HttpClient creator, int ownDepartment)
    {
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var ticket = await CreateManualTicketAsync(creator, ownDepartment);
        var receiving = await NewDepartmentAsync();
        await TransferAsync(manager, ticket.TicketId, receiving);
        return (ticket.TicketId, receiving, manager);
    }

    [Fact]
    public async Task TheCreator_CanOpenFollowAndReadTheTicket_BeforeAndAfterItIsTransferred()
    {
        var own = await DepartmentAsync();
        var (creator, _) = await ClientAsync(departmentId: own);
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var ticket = await CreateManualTicketAsync(creator, own);

        // Before: theirs by self-assignment — full access, not read-only.
        var before = await (await creator.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.False(before!.IsReadOnlyAccess);
        Assert.Equal(HttpStatusCode.Created, (await creator.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/notes", new CreateNoteRequestDto("Customer called again."))).StatusCode);

        var receiving = await NewDepartmentAsync();
        await TransferAsync(manager, ticket.TicketId, receiving);

        // After: still readable — status, department, owner, history, updates — and flagged read-only.
        var after = await (await creator.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.True(after!.IsReadOnlyAccess);
        Assert.Equal(receiving, after.CurrentDepartmentId);
        Assert.NotNull(after.CurrentOwnerEmployeeId);   // reassigned explicitly on transfer: never ownerless
        foreach (var suffix in new[] { "/history", "/notes", "/interactions", "/sla", "/escalations", "/approvals", "/customer-history", "/reviews" })
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, (await creator.GetAsync($"/api/tickets/{ticket.TicketId}{suffix}")).StatusCode);
        }

        var notes = await (await creator.GetAsync($"/api/tickets/{ticket.TicketId}/notes")).Content.ReadFromJsonAsync<TicketNoteListResultDto>();
        Assert.Contains(notes!.Items, n => n.NoteText == "Customer called again.");   // the updates are readable too

        // The approvals view the page is built from offers a read-only creator nothing to do.
        var approvals = await (await creator.GetAsync($"/api/tickets/{ticket.TicketId}/approvals")).Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
        Assert.False(approvals!.CallerCanRecordEvents);
        Assert.All(approvals.Approvals, a => Assert.False(a.CallerCanDecide));
        Assert.All(approvals.RequestableApprovals, r => Assert.False(r.CallerCanRequest));
        Assert.Contains(ticket.TicketId, await ListedIdsAsync(creator));
    }

    [Fact]
    public async Task AnotherAgent_CannotViewATicketTheyDidNotCreate_BeforeOrAfterATransfer()
    {
        var own = await DepartmentAsync();
        var (creator, _) = await ClientAsync(departmentId: own);
        var (colleague, _) = await ClientAsync(departmentId: own);
        var ticket = await CreateManualTicketAsync(creator, own);
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var receiving = await NewDepartmentAsync();
        var (receivingA, receivingAId) = await ClientAsync(departmentId: receiving);
        var (receivingB, receivingBId) = await ClientAsync(departmentId: receiving);

        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(colleague, ticket.TicketId));
        await TransferAsync(manager, ticket.TicketId, receiving, assignTo: receivingAId);
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(colleague, ticket.TicketId));

        // The manager's transfer explicitly reassigns the ticket to ONE agent of the receiving department; only that assignee sees it.
        // A CS Agent's department membership alone still grants nothing.
        var after = await (await manager.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        var (assignee, other) = after!.CurrentOwnerEmployeeId == receivingAId ? (receivingA, receivingB) : (receivingB, receivingA);
        Assert.Contains(after.CurrentOwnerEmployeeId, new Guid?[] { receivingAId, receivingBId });
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(assignee, ticket.TicketId));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(other, ticket.TicketId));
        Assert.DoesNotContain(ticket.TicketId, await ListedIdsAsync(colleague));
        Assert.DoesNotContain(ticket.TicketId, await ListedIdsAsync(other));
    }

    [Theory]
    [MemberData(nameof(TicketWrites))]
    public async Task ACreatorOutsideTheDepartment_GetsReadOnlyAccess_EveryModificationIs403(string suffix)
    {
        var own = await DepartmentAsync();
        var (creator, _) = await ClientAsync(departmentId: own);
        var (ticketId, _, manager) = await CreatedThenTransferredAsync(creator, own);
        var before = await (await manager.GetAsync($"/api/tickets/{ticketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();

        var response = await creator.PostAsJsonAsync($"/api/tickets/{ticketId}/{suffix}", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await (await manager.GetAsync($"/api/tickets/{ticketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(before!.RowVersion, after!.RowVersion);   // nothing changed
    }

    [Fact]
    public async Task ACreatorOutsideTheDepartment_CannotAddANote_EvenWithAValidBody()
    {
        var own = await DepartmentAsync();
        var (creator, creatorId) = await ClientAsync(departmentId: own);
        var (ticketId, _, manager) = await CreatedThenTransferredAsync(creator, own);

        var note = await creator.PostAsJsonAsync($"/api/tickets/{ticketId}/notes", new CreateNoteRequestDto("Still me."));

        Assert.Equal(HttpStatusCode.Forbidden, note.StatusCode);
        var notes = await (await manager.GetAsync($"/api/tickets/{ticketId}/notes")).Content.ReadFromJsonAsync<TicketNoteListResultDto>();
        Assert.DoesNotContain(notes!.Items, n => n.NoteText == "Still me.");
        Assert.NotNull(await _factory.GetTicketCurrentOwnerAsync(ticketId));   // reassigned explicitly on transfer: never ownerless
        Assert.NotEqual(Guid.Empty, creatorId);
    }

    [Fact]
    public async Task ACreatorCannotOpenAnotherAgentsTransferredTicket_AndCanStillWorkTheirOwn()
    {
        var own = await DepartmentAsync();
        var (creator, _) = await ClientAsync(departmentId: own);
        var (other, _) = await ClientAsync(departmentId: own);
        var (theirTicket, _, _) = await CreatedThenTransferredAsync(other, own);
        var mine = await CreateManualTicketAsync(creator, own);

        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(creator, theirTicket));
        Assert.Equal(HttpStatusCode.Created, (await creator.PostAsJsonAsync($"/api/tickets/{mine.TicketId}/notes", new CreateNoteRequestDto("Mine."))).StatusCode);
    }

    [Fact]
    public async Task CreatedByMe_ListsTheCreatorsTickets_IncludingTheOnesThatMovedDepartment_AndNothingTheyDidNotCreate()
    {
        var own = await DepartmentAsync();
        var (creator, creatorId) = await ClientAsync(departmentId: own);
        var (colleague, colleagueId) = await ClientAsync(departmentId: own);
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var moved = await CreateManualTicketAsync(creator, own);
        await TransferAsync(manager, moved.TicketId, await NewDepartmentAsync());
        var stillMine = await CreateManualTicketAsync(creator, own);
        var colleaguesTicket = await CreateManualTicketAsync(colleague, own);
        await AssignAsync(manager, colleaguesTicket.TicketId, creatorId);             // now assigned to the creator, created by the colleague

        async Task<long[]> CreatedByMe(HttpClient client)
        {
            var page = await (await client.GetAsync("/api/tickets?createdByMe=true&pageSize=200")).Content.ReadFromJsonAsync<TicketListResultDto>();
            return [.. page!.Items.Select(i => i.TicketId).Order()];
        }

        Assert.Equal([moved.TicketId, stillMine.TicketId], await CreatedByMe(creator));
        Assert.Contains(colleaguesTicket.TicketId, await ListedIdsAsync(creator));       // assigned to me: in the normal list, not in "created by me"
        Assert.Empty(await CreatedByMe(colleague));          // created by the colleague, but now assigned to another CS Agent: no longer theirs to see
        Assert.NotEqual(creatorId, colleagueId);
    }

    [Fact]
    public async Task ASupervisorsAccess_AndTheirCreatedByMeFilter_AreUnchanged()
    {
        var own = await DepartmentAsync();
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor, departmentId: own);
        var (agent, _) = await ClientAsync(departmentId: own);
        var theirs = await CreateManualTicketAsync(agent, own);
        var mine = await CreateManualTicketAsync(supervisor, own);

        var created = await (await supervisor.GetAsync("/api/tickets?createdByMe=true&pageSize=200")).Content.ReadFromJsonAsync<TicketListResultDto>();

        Assert.Equal(mine.TicketId, Assert.Single(created!.Items).TicketId);
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(supervisor, theirs.TicketId));
        Assert.False((await (await supervisor.GetAsync($"/api/tickets/{theirs.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>())!.IsReadOnlyAccess);
    }

    [Fact]
    public async Task TheCreatorOfAGenesysTicket_IsTheIntegrationAccount_SoNoAgentGainsAccessThroughCreation()
    {
        var (genesys, genesysId) = await ClientAsync();
        var (agent, _) = await ClientAsync();
        var (ticketId, _) = await GenesysTicketAsync(genesys);

        Assert.Equal(genesysId, (await _factory.GetTicketAsync(ticketId))!.CreatedByEmployeeId);
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(agent, ticketId));
    }
}
