using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// The feature flag, end-to-end against the real host with
/// <c>Genesys:Enabled=false</c> — the shipped default.
///
/// <para>
/// Two things must both hold, and only a host actually running with the flag
/// off can show them: the Genesys endpoints refuse to do anything, and
/// <b>every ordinary ticketing flow is completely unaffected</b>. The second
/// is the one that matters operationally — switching Genesys off must never
/// be able to take manual/Face-to-Face ticket creation down with it.
/// </para>
/// </summary>
public sealed class GenesysDisabledEndpointsTests : IDisposable
{
    /// <summary>
    /// The standard test host with the Genesys feature flag switched back off.
    /// <see cref="TigerCsApiFactory.ExtraConfiguration"/> is applied after the
    /// factory's own defaults and therefore wins — the same extension point
    /// the PACT end-to-end test uses to re-point a real integration.
    /// </summary>
    private readonly TigerCsApiFactory _factory = new()
    {
        ExtraConfiguration = { ["Genesys:Enabled"] = "false" }
    };

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> CreateAgentClientAsync()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync("CS Agent");
        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    [Fact]
    public async Task GenesysIngestion_IsRefused_AndWritesNothing()
    {
        var client = await CreateAgentClientAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Disabled CS " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await client.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest("conv-flag-off", "Phone", CustomerPhone: "+971500000001", DepartmentId: departmentId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Genesys:Enabled is false", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GenesysTicketUpdate_IsRefused()
    {
        var client = await CreateAgentClientAsync();

        var response = await client.PatchAsJsonAsync(
            "/api/genesys/tickets/1",
            new GenesysTicketUpdateRequest(
                "conv-flag-off",
                Ended: new GenesysConversationEndPart(DateTime.UtcNow, "AgentDisconnect")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GenesysCustomerLookup_IsRefused()
    {
        var client = await CreateAgentClientAsync();

        var response = await client.GetAsync("/api/genesys/customers/lookup?phoneNumber=%2B971500000001");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Genesys:Enabled is false", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NormalManualTicketCreation_StillWorksCompletely()
    {
        // Requirement 16, proved against a real host with the integration
        // switched off: intake -> ticket -> detail, all unaffected.
        var client = await CreateAgentClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Disabled FM " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // A Face-to-Face walk-in — the flow that must never depend on Genesys.
        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("WALK_IN_KIOSK", "", departmentId, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.High, "Walk-in: lift out of service"));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Open", ticket!.TicketStatus);
        Assert.Equal("Running", ticket.SlaState);

        // Creating (or handling) a ticket grants a CS Agent nothing; the agent who took the walk-in sees it because it is assigned to them.
        await _factory.AssignOwnerAsync(ticket.TicketId, TigerCsApiFactory.UserIdOf(client));
        var detail = await client.GetAsync($"/api/tickets/{ticket.TicketId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        // Its conversation history reads fine too — it simply has a local,
        // non-Genesys originating interaction.
        var interactions = await client.GetAsync($"/api/tickets/{ticket.TicketId}/interactions");
        Assert.Equal(HttpStatusCode.OK, interactions.StatusCode);
        var history = await interactions.Content.ReadFromJsonAsync<TicketInteractionHistoryDto>();
        Assert.Equal("Ticketing", Assert.Single(history!.Interactions).Source);
    }
}
