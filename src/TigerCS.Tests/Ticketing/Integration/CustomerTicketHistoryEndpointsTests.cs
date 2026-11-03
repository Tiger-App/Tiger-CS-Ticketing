using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Customer Ticket History through the REAL HTTP pipeline. A CS Agent still sees only their assigned tickets in the Queue (PR #161), but while
/// they own an ACTIVE ticket of a verified customer they may OPEN that customer's other tickets - whoever handled them - read-only. The
/// read-only part is enforced by the API (403 on every modification), not by hidden buttons; an unrelated customer's tickets stay closed;
/// and the access ends the moment the agent no longer handles the customer.
/// </summary>
public sealed class CustomerTicketHistoryEndpointsTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;
    private static int _nextCustomerId = 900_000;

    private static int NewCustomerId() => Interlocked.Increment(ref _nextCustomerId);

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientAsync(string role = Roles.CsAgent)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private int _departmentId;
    private int _categoryId;

    private async Task<(int Department, int Category)> ScopeAsync()
    {
        if (_departmentId == 0)
        {
            await _factory.SeedPrioritiesAsync();
            _departmentId = await _factory.CreateDepartmentAsync("History " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
            _categoryId = await _factory.CreateCategoryAsync("History " + Guid.NewGuid(), _departmentId);
        }

        return (_departmentId, _categoryId);
    }

    private async Task<long> TicketAsync(int customerId, Guid? owner, TicketStatus status = TicketStatus.Open)
    {
        var (department, category) = await ScopeAsync();
        return await _factory.SeedVerifiedCustomerTicketAsync(customerId, department, category, owner, status);
    }

    private static async Task<TicketDetailDto> DetailAsync(HttpClient client, long ticketId)
    {
        var response = await client.GetAsync($"/api/tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static async Task<long[]> ListedIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tickets?pageSize=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<TicketListResultDto>())!.Items.Select(i => i.TicketId).Order()];
    }

    private static async Task<CustomerHistoryDto> HistoryAsync(HttpClient client, long ticketId)
    {
        var response = await client.GetAsync($"/api/tickets/{ticketId}/customer-history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomerHistoryDto>())!;
    }

    [Fact]
    public async Task AgentA_OwnsTheirTicketInFull_AndOpensAgentBsPreviousTicketOfTheSameCustomer_ReadOnly()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var customer = NewCustomerId();
        var current = await TicketAsync(customer, agentAId);
        var previous = await TicketAsync(customer, agentBId, TicketStatus.Closed);

        Assert.False((await DetailAsync(agentA, current)).IsReadOnlyAccess);
        var opened = await DetailAsync(agentA, previous);

        Assert.True(opened.IsReadOnlyAccess);
        Assert.Equal(previous, opened.TicketId);
        foreach (var read in new[] { "/history", "/notes", "/interactions", "/sla", "/approvals", "/customer-history" })
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, (await agentA.GetAsync($"/api/tickets/{previous}{read}")).StatusCode);
        }
    }

    [Fact]
    public async Task TheQueue_StillHoldsOnlyTheAgentsAssignedTickets_NotTheCustomersHistory()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var customer = NewCustomerId();
        var current = await TicketAsync(customer, agentAId);
        var previous = await TicketAsync(customer, agentBId, TicketStatus.Resolved);

        var listed = await ListedIdsAsync(agentA);

        Assert.Equal([current], listed);
        Assert.DoesNotContain(previous, listed);
    }

    [Fact]
    public async Task TheCustomerHistory_ShowsEveryPreviousTicketWithItsAgentDatesAndCorrectCounts()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var (_, agentCId) = await ClientAsync();
        var customer = NewCustomerId();
        var current = await TicketAsync(customer, agentAId);
        var byB = await TicketAsync(customer, agentBId, TicketStatus.Closed);
        var byC = await TicketAsync(customer, agentCId, TicketStatus.Resolved);
        var stillOpen = await TicketAsync(customer, agentBId, TicketStatus.InProgress);
        await TicketAsync(NewCustomerId(), agentBId);   // another customer's ticket: never counted

        var history = await HistoryAsync(agentA, current);

        Assert.Equal("Verified", history.VerificationType);
        Assert.Equal(3, history.TotalTickets);   // the current ticket is not its own history
        Assert.Equal(1, history.OpenTickets);
        Assert.Equal(2, history.ClosedTickets);
        Assert.Equal([byB, byC, stillOpen], history.Tickets.Select(t => t.TicketId).Order().ToArray());
        Assert.All(history.Tickets, t => Assert.NotNull(t.AssignedAgentName));
        Assert.Equal(agentBId, history.Tickets.Single(t => t.TicketId == byB).AssignedAgentEmployeeId);
        Assert.NotNull(history.Tickets.Single(t => t.TicketId == byB).ClosedAtUtc);
    }

    public static IEnumerable<object[]> Modifications() =>
    [
        ["classification"], ["assignment"], ["ownership"], ["transfer"], ["status"], ["resolution"], ["close"], ["reopen"], ["notes"],
        ["approvals"], ["workflow-events"], ["reconciliation"], ["sla/first-response"], ["escalations"], ["sla/priority-downgrade-requests"]
    ];

    [Theory]
    [MemberData(nameof(Modifications))]
    public async Task AgentA_CannotModifyAgentBsPreviousTicket_Transfer_Assign_Resolve_Close_Reopen_Or_Anything_Else(string suffix)
    {
        var (agentA, agentAId) = await ClientAsync();
        var (manager, _) = await ClientAsync(Roles.CsManager);
        var (_, agentBId) = await ClientAsync();
        var customer = NewCustomerId();
        await TicketAsync(customer, agentAId);
        var previous = await TicketAsync(customer, agentBId, TicketStatus.Closed);
        var before = await DetailAsync(manager, previous);

        var response = await agentA.PostAsJsonAsync($"/api/tickets/{previous}/{suffix}", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await DetailAsync(manager, previous);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(before.TicketStatus, after.TicketStatus);
        Assert.Equal(before.CurrentOwnerEmployeeId, after.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AgentA_CannotAddANote_ToAPreviousTicket_EvenWithAValidBody()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var customer = NewCustomerId();
        await TicketAsync(customer, agentAId);
        var previous = await TicketAsync(customer, agentBId, TicketStatus.InProgress);

        var response = await agentA.PostAsJsonAsync($"/api/tickets/{previous}/notes", new CreateNoteRequestDto("Trying to write on B's ticket."));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AgentA_CannotReachAnUnrelatedCustomersTickets_ByDirectUrlHistoryOrCustomerEndpoint()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        await TicketAsync(NewCustomerId(), agentAId);
        var unrelatedCustomer = NewCustomerId();
        var unrelated = await TicketAsync(unrelatedCustomer, agentBId, TicketStatus.Closed);

        Assert.Equal(HttpStatusCode.Forbidden, (await agentA.GetAsync($"/api/tickets/{unrelated}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentA.GetAsync($"/api/tickets/{unrelated}/customer-history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentA.GetAsync($"/api/tickets/{unrelated}/notes")).StatusCode);

        var byCustomer = await agentA.GetAsync($"/api/customers/crm/{unrelatedCustomer}/ticket-history");
        Assert.Equal(HttpStatusCode.OK, byCustomer.StatusCode);
        var dto = await byCustomer.Content.ReadFromJsonAsync<CustomerHistoryDto>();
        Assert.Empty(dto!.Tickets);
        Assert.Equal(0, dto.TotalTickets);
        Assert.DoesNotContain(unrelated, await ListedIdsAsync(agentA));
    }

    [Fact]
    public async Task TheCustomerEndpoint_ReturnsTheWholeHistory_OnlyForACustomerTheAgentCurrentlyHandles()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var handled = NewCustomerId();
        await TicketAsync(handled, agentAId);
        await TicketAsync(handled, agentBId, TicketStatus.Closed);
        await TicketAsync(handled, agentBId, TicketStatus.Resolved);

        var dto = await (await agentA.GetAsync($"/api/customers/crm/{handled}/ticket-history")).Content.ReadFromJsonAsync<CustomerHistoryDto>();

        Assert.Equal(3, dto!.TotalTickets);
    }

    [Fact]
    public async Task TheAccessEnds_WhenTheAgentNoLongerHandlesTheCustomer()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var customer = NewCustomerId();
        var current = await TicketAsync(customer, agentAId);
        var previous = await TicketAsync(customer, agentBId, TicketStatus.Closed);
        Assert.Equal(HttpStatusCode.OK, (await agentA.GetAsync($"/api/tickets/{previous}")).StatusCode);

        // A finishes the customer's only active ticket through the real endpoints.
        var detail = await DetailAsync(agentA, current);
        var started = await agentA.PostAsJsonAsync($"/api/tickets/{current}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(detail.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var working = await started.Content.ReadFromJsonAsync<TicketDetailDto>();
        var resolved = await agentA.PostAsJsonAsync(
            $"/api/tickets/{current}/resolution",
            new ResolveTicketRequestDto("Resolved", "Done.", null, null, Convert.FromBase64String(working!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await agentA.GetAsync($"/api/tickets/{previous}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentA.GetAsync($"/api/tickets/{previous}/notes")).StatusCode);
        var customerHistory = await (await agentA.GetAsync($"/api/customers/crm/{customer}/ticket-history")).Content.ReadFromJsonAsync<CustomerHistoryDto>();
        Assert.Equal(1, customerHistory!.TotalTickets);   // back to the agent's own scope: just the ticket they still own (now resolved)
        Assert.Equal(current, Assert.Single(customerHistory.Tickets).TicketId);
    }

    [Fact]
    public async Task AnAgentWhoOnlyOwnsAResolvedTicketOfTheCustomer_HasNoHistoryAccess()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var customer = NewCustomerId();
        await TicketAsync(customer, agentAId, TicketStatus.Resolved);
        var previous = await TicketAsync(customer, agentBId, TicketStatus.Closed);

        Assert.Equal(HttpStatusCode.Forbidden, (await agentA.GetAsync($"/api/tickets/{previous}")).StatusCode);
    }

    [Fact]
    public async Task ACsAgentWithNoTicketOfTheCustomer_CannotOpenTheirHistory_EvenWhenAnotherAgentCan()
    {
        var (agentA, agentAId) = await ClientAsync();
        var (stranger, _) = await ClientAsync();
        var (_, agentBId) = await ClientAsync();
        var customer = NewCustomerId();
        await TicketAsync(customer, agentAId);
        var previous = await TicketAsync(customer, agentBId, TicketStatus.Closed);

        Assert.Equal(HttpStatusCode.OK, (await agentA.GetAsync($"/api/tickets/{previous}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/tickets/{previous}")).StatusCode);
    }

    [Fact]
    public async Task PermissionsOfSupervisorsAndManagers_AreUnchanged()
    {
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor);
        var (_, agentBId) = await ClientAsync();
        var ticket = await TicketAsync(NewCustomerId(), agentBId);

        var detail = await DetailAsync(supervisor, ticket);

        Assert.False(detail.IsReadOnlyAccess);
        Assert.Contains(ticket, await ListedIdsAsync(supervisor));
    }
}
