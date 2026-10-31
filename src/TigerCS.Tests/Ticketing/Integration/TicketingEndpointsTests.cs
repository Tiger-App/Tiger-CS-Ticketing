using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// End-to-end against the real Api host (real routing, real authorization,
/// real EF Core-mapped schema — InMemory provider — the real MockCrmGateway/
/// MockPactGateway/MockTasleehGateway fixture data) — exercising the actual
/// sequence an agent follows: intake -> customer lookup (CRM/PACT/Tasleeh)
/// -> ticket creation. Business-rule change: customer lookup is enrichment,
/// never a Ticket creation gate — every test below that creates a ticket
/// without a customer match proves that directly.
/// </summary>
public class TicketingEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    /// <summary>Matches MockCrmGateway's CRM-UNIT-1107/CRM-CONTACT-3010 fixture (Sami Nasser, an Owner — a valid Buyer ownership record) — the standard "customer found, with an eligible unit" phone number for these tests.</summary>
    private const string PhoneWithCrmMatch = "+971509990001";
    private const string PhoneWithNoMatch = "+971500009999";

    private readonly TigerCsApiFactory _factory;

    public TicketingEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string role = "CS Agent")
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    [Fact]
    public async Task CreateIntakeRecord_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Channel Management: the intake validates the channel against
    // configuration, and the ticket keeps the channel it entered on. ----

    [Fact]
    public async Task CreateIntakeRecord_ChannelIsValidatedAgainstConfiguration_ByIdOrCode()
    {
        var client = await CreateAuthenticatedClientAsync();

        // Unknown id / unknown code → 400 validation problem on channelId.
        var unknownId = await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("99", PhoneWithNoMatch, null, false, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, unknownId.StatusCode);
        Assert.Contains("ChannelId", await unknownId.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("NoSuchChannel", PhoneWithNoMatch, null, false, null, null))).StatusCode);

        // Numeric id and (case-insensitive) code both resolve to the same seeded channel.
        var byId = await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("6", PhoneWithNoMatch, null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, byId.StatusCode);
        var byIdIntake = await byId.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.Equal("WHATSAPP", byIdIntake!.ChannelId);
        Assert.Equal("WhatsApp", byIdIntake.ChannelName);

        var byCode = await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("whatsapp", PhoneWithNoMatch, null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, byCode.StatusCode);
        Assert.Equal("WHATSAPP", (await byCode.Content.ReadFromJsonAsync<IntakeRecordResponseDto>())!.ChannelId);
    }

    [Fact]
    public async Task CreateIntakeRecord_PhoneRequirement_FollowsTheChannelConfiguration()
    {
        var client = await CreateAuthenticatedClientAsync();

        // Phone (RequiresPhone = true): a blank phone is a 400 on phoneNumber.
        var blankOnPhone = await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "", null, false, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, blankOnPhone.StatusCode);
        Assert.Contains("PhoneNumber", await blankOnPhone.Content.ReadAsStringAsync());

        // Walk in / Kiosk (RequiresPhone = false): a blank phone is accepted and stored as empty.
        var blankOnKiosk = await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("WALK_IN_KIOSK", "", null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, blankOnKiosk.StatusCode);
        var intake = await blankOnKiosk.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.Equal("WALK_IN_KIOSK", intake!.ChannelId);
        Assert.Equal(string.Empty, intake.PhoneNumber);
    }

    [Fact]
    public async Task CreatedTicket_PreservesItsOriginatingChannel_EvenAfterTheChannelIsDeactivated()
    {
        var agent = await CreateAuthenticatedClientAsync();
        var admin = await CreateAuthenticatedClientAsync(Roles.SystemAdministrator);
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Walk-in Query", departmentId);

        // A channel of this test's own (so deactivating it disturbs no other test), phone optional.
        var code = "KIOSK" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var channelResponse = await admin.PostAsJsonAsync("/api/admin/channels", new SaveChannelRequestDto("Lobby Kiosk " + code, code, false, false, 60));
        Assert.Equal(HttpStatusCode.Created, channelResponse.StatusCode);
        var channel = await channelResponse.Content.ReadFromJsonAsync<AdminChannelDto>();

        var intakeResponse = await agent.PostAsJsonAsync("/api/intake-records",
            new CreateIntakeRecordRequestDto(channel!.ChannelId.ToString(), "", null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.Equal(code, intake!.ChannelId);

        var ticketResponse = await agent.PostAsJsonAsync("/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Low, "Walk-in question"));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        await _factory.RecordHandlerAsync(ticket!.TicketId, agent);

        var detail = await (await agent.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(channel.ChannelId, detail!.OriginatingChannelId);
        Assert.Equal("Lobby Kiosk " + code, detail.OriginatingChannelName);

        // Exactly one originating interaction, on the intake's channel; the intake keeps its channel.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var interactions = await db.TicketInteractions.Where(i => i.TicketId == ticket.TicketId).ToListAsync();
            var originating = Assert.Single(interactions, i => i.IsOriginatingInteraction);
            Assert.Equal(channel.ChannelId, originating.ChannelId);
            Assert.Equal(channel.ChannelId, (await db.IntakeRecords.SingleAsync(i => i.IntakeRecordId == intake.IntakeRecordId)).ChannelId);
        }

        // Admin deactivates the channel: gone from new-ticket selection, still named on the historical ticket.
        var deactivated = await admin.PatchAsJsonAsync($"/api/admin/channels/{channel.ChannelId}/activation", new SetActiveRequestDto(false, "closed"));
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.DoesNotContain(await (await agent.GetAsync("/api/channels")).Content.ReadFromJsonAsync<List<ChannelDto>>() ?? [], c => c.ChannelId == channel.ChannelId);

        var afterDeactivation = await (await agent.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Lobby Kiosk " + code, afterDeactivation!.OriginatingChannelName);

        // And a NEW intake on the retired channel is refused.
        var stale = await agent.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto(channel.ChannelId.ToString(), "", null, false, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Contains("inactive", await stale.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FullLifecycle_IntakeLookupCreate_ProducesVerifiedTicket()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmResult = lookup!.Sources.Single(s => s.Source == "Crm");
        Assert.Equal("Found", crmResult.Status);
        var crmCustomer = Assert.Single(crmResult.Customers);
        var crmUnit = Assert.Single(crmCustomer.Units);
        Assert.NotNull(crmUnit.UnitReferenceId);
        Assert.NotNull(crmUnit.ContactReferenceId);

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmUnit.UnitReferenceId, crmUnit.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling"));

        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Verified", ticket!.VerificationStatus);
        Assert.Equal("Open", ticket.TicketStatus);
        Assert.Equal(crmUnit.UnitReferenceId, ticket.UnitReferenceId);
        Assert.StartsWith("TG-", ticket.TicketNumber);

        // Reusing an already-linked IntakeRecord is rejected, not silently re-executed.
        var replay = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmUnit.UnitReferenceId, crmUnit.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "Second attempt"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_NoCustomerMatch_StillReturns201WithUnverifiedTicket()
    {
        // Business-rule change: CRM/PACT/Tasleeh finding nothing never blocks
        // ticket creation — even for a unit-related intake.
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, true, "9999", (byte)PriorityLevel.Critical));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        Assert.All(lookup!.Sources, s => Assert.Equal("NotFound", s.Status));

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Critical, "Flooding in lobby"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Unverified", ticket!.VerificationStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
    }

    [Theory]
    [InlineData("+971501234567")]
    [InlineData("971501234567")]
    public async Task CreateIntakeRecord_PlusPrefixedOrPlainDigitsPhone_Accepted_StoredVerbatim(string phoneNumber)
    {
        // A leading '+' is a valid phone number: the API accepts it and the
        // IntakeRecord stores the EXACT value entered — no layer reformats
        // it (only PactCustomerHttpGateway transforms it, PACT-request-side).
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", phoneNumber, null, false, null, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var intake = await response.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.Equal(phoneNumber, intake!.PhoneNumber);
    }

    [Fact]
    public async Task CreateIntakeRecord_BlankPhoneNumber_Returns400_RecordNotCreated()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "", null, false, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CustomerLookup_DepartmentScopedToCrmOnly_SearchesOnlyCrm_NeverFallsBackToPactEvenThoughItHasAMatch()
    {
        // MockPactGateway has a real fixture match for +971500000002 — this
        // proves that a Department configured for CRM only never falls back
        // to PACT even when PACT would have found something.
        const string phoneWithPactMatchOnly = "+971500000002";

        var client = await CreateAuthenticatedClientAsync();
        var departmentId = await _factory.CreateDepartmentAsync("CRM-Only Dept " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.SeedDepartmentCustomerLookupSourceAsync(departmentId, CustomerLookupSource.Crm);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", phoneWithPactMatchOnly, departmentId, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();

        var source = Assert.Single(lookup!.Sources);
        Assert.Equal("Crm", source.Source);
        Assert.Equal("NotFound", source.Status);
    }

    [Fact]
    public async Task CustomerLookup_NoDepartmentSelected_SearchesAllThreeSources()
    {
        var client = await CreateAuthenticatedClientAsync();

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();

        Assert.Equal(3, lookup!.Sources.Count);
        Assert.Contains(lookup.Sources, s => s.Source == "Crm");
        Assert.Contains(lookup.Sources, s => s.Source == "Pact");
        Assert.Contains(lookup.Sources, s => s.Source == "Tasleeh");
    }

    [Fact]
    public async Task CreateIntakeRecord_UnknownDepartmentId_Returns404()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, 999_999, false, null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_CrmOutageDuringLookup_StillReturns201()
    {
        // Business-rule change: a source that cannot be reached at all
        // (Failed, not NotFound) still never blocks ticket creation.
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // MockCrmGateway/MockPactGateway/MockTasleehGateway all trigger a
        // simulated outage when the searched value contains "OUTAGE".
        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500OUTAGE01", null, false, null, null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        Assert.All(lookup!.Sources, s => Assert.Equal("Failed", s.Status));

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "General question"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_MissingCategory_Returns404_TicketRejected()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, false, null, null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, CategoryId: 999_999, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTicket_NonUnitIntake_Returns201()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithNoMatch, null, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.False(intake!.IsUnitRelated);

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "General billing question"));

        // Routes successfully from category alone — no Unit/CRM data of any kind was ever supplied.
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Unverified", ticket!.VerificationStatus);
        Assert.Equal("Open", ticket.TicketStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);
        Assert.Equal(departmentId, ticket.OriginatingDepartmentId);
        Assert.StartsWith("TG-", ticket.TicketNumber);

        // The agent who took the call is a handler of the ticket's interaction, so it stays visible to them.
        await _factory.RecordHandlerAsync(ticket.TicketId, client);
        var detailResponse = await client.GetAsync($"/api/tickets/{ticket.TicketId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
    }

    // CS Agent/CS Supervisor by the CustomerVerification policy's own role
    // list; System Administrator by the ADR-0024 central override.
    public static IEnumerable<object[]> AllRolesWithExpectedAccess() =>
        Roles.All.Select(role => new object[]
        {
            role,
            role is Roles.CsAgent or Roles.CsSupervisor or Roles.SystemAdministrator
        });

    [Theory]
    [MemberData(nameof(AllRolesWithExpectedAccess))]
    public async Task CreateIntakeRecord_EveryRole_MatchesCustomerVerificationPolicy(string role, bool expectAuthorized)
    {
        var client = await CreateAuthenticatedClientAsync(role);

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));

        Assert.Equal(expectAuthorized ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(long TicketId, int DepartmentId, byte[] RowVersion)> CreateVerifiedTicketAsync(HttpClient creatorClient, string departmentPrefix = "Facilities")
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(departmentPrefix + " " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intakeResponse = await creatorClient.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", PhoneWithCrmMatch, null, true, "1204", null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await creatorClient.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmCustomer = Assert.Single(lookup!.Sources.Single(s => s.Source == "Crm").Customers);
        var crmUnit = Assert.Single(crmCustomer.Units);

        var ticketResponse = await creatorClient.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmUnit.UnitReferenceId, crmUnit.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling"));
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();

        // The creating agent took the call: they handled the ticket's interaction, which is what keeps it visible to them.
        await _factory.RecordHandlerAsync(ticket!.TicketId, creatorClient);

        return (ticket.TicketId, departmentId, Convert.FromBase64String(ticket.RowVersion));
    }

    [Fact]
    public async Task FullOperationsLifecycle_AssignWorkResolveClose_Succeeds()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, initialRowVersion) = await CreateVerifiedTicketAsync(agentClient);

        var (workerUsername, workerPassword, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);
        var workerClient = _factory.CreateClient();
        var loginResponse = await workerClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(workerUsername, workerPassword));
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        workerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        // PR correction: Department Employee holds no assignment capability
        // at all (not even self-claim) — a Department Head in the same
        // department assigns them instead.
        var (deptHeadUsername, deptHeadPassword, deptHeadId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(deptHeadId, departmentId);
        var deptHeadClient = _factory.CreateClient();
        var deptHeadLoginResponse = await deptHeadClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(deptHeadUsername, deptHeadPassword));
        var deptHeadLogin = await deptHeadLoginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        deptHeadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deptHeadLogin!.AccessToken);

        // Department Employee cannot self-claim.
        var selfClaimAttempt = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(workerId, initialRowVersion));
        Assert.Equal(HttpStatusCode.Forbidden, selfClaimAttempt.StatusCode);

        var assignResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(workerId, initialRowVersion));
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);
        var afterAssign = await assignResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        // Start work.
        var statusResponse = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var afterStatus = await statusResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        // Resolve as the Department Employee who owns it.
        var resolveResponse = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, Convert.FromBase64String(afterStatus!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var afterResolve = await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        // A department user who is neither the assignee nor CS may not close (the assignee may — covered in the lifecycle tests).
        var forbiddenClose = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve!.RowVersion)));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenClose.StatusCode);

        // CS Agent closes it.
        var closeResponse = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var closed = await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", closed!.TicketStatus);

        // Notes are visible cross-department to CS.
        var noteResponse = await agentClient.PostAsJsonAsync($"/api/tickets/{ticketId}/notes", new CreateNoteRequestDto("Customer confirmed resolution."));
        Assert.Equal(HttpStatusCode.Created, noteResponse.StatusCode);

        var detailResponse = await agentClient.GetAsync($"/api/tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
    }

    [Fact]
    public async Task Reopen_Lifecycle_NonCsRolesAreForbidden_CsAgentSucceeds_AndEligibilityRidesTheDetail()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, initialRowVersion) = await CreateVerifiedTicketAsync(agentClient);

        // Drive to Resolved through the normal lifecycle actors.
        var (workerUsername, workerPassword, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);
        var workerClient = _factory.CreateClient();
        var login = await (await workerClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto(workerUsername, workerPassword))).Content.ReadFromJsonAsync<LoginResponseDto>();
        workerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var (deptHeadUsername, deptHeadPassword, deptHeadId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(deptHeadId, departmentId);
        var deptHeadClient = _factory.CreateClient();
        var deptHeadLogin = await (await deptHeadClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto(deptHeadUsername, deptHeadPassword))).Content.ReadFromJsonAsync<LoginResponseDto>();
        deptHeadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deptHeadLogin!.AccessToken);

        var afterAssign = await (await deptHeadClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(workerId, initialRowVersion)))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterStatus = await (await workerClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterResolve = await (await workerClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, Convert.FromBase64String(afterStatus!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        // Resolved is NOT reopenable under the approved rule, and the detail
        // read says so — which is what keeps the Reopen control off the page.
        var whileResolved = await (await agentClient.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.False(whileResolved!.IsReopenEligible);
        Assert.Null(whileResolved.ClosedAtUtc);

        var reopenWhileResolved = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Too early.", departmentId, Convert.FromBase64String(afterResolve!.RowVersion)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reopenWhileResolved.StatusCode);

        // Close it — only now is it reopenable, and the window starts here.
        var afterClose = await (await agentClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var detail = await (await agentClient.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.True(detail!.IsReopenEligible);
        Assert.NotNull(detail.ClosedAtUtc);

        // Approved rule: the Agent reopens. The Department Employee who worked
        // the ticket cannot.
        var forbiddenReopen = await workerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Trying to reopen my own work.", departmentId, Convert.FromBase64String(afterClose!.RowVersion)));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenReopen.StatusCode);

        // Nor may any other non-CS role — the direct endpoint is the rule's
        // enforcement point, so hitting it straight cannot bypass the missing
        // UI control. Department Head and General Manager are here on purpose:
        // both reach the ticket, neither holds direct Reopen.
        foreach (var role in new[] { Roles.DepartmentHead, Roles.GeneralManager, Roles.ChairmanCeo, Roles.ReportingUser })
        {
            var refusedClient = await CreateAuthenticatedClientAsync(role);
            var refused = await refusedClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/reopen",
                new ReopenTicketRequestDto("Not mine to reopen.", departmentId, Convert.FromBase64String(afterClose.RowVersion)));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // A blank reason, and a missing department, are rejected before any
        // state changes.
        var blankReason = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("   ", departmentId, Convert.FromBase64String(afterClose.RowVersion)));
        Assert.Equal(HttpStatusCode.BadRequest, blankReason.StatusCode);

        var noDepartment = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Customer called back.", 0, Convert.FromBase64String(afterClose.RowVersion)));
        Assert.Equal(HttpStatusCode.BadRequest, noDepartment.StatusCode);

        var unknownDepartment = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Customer called back.", 99999, Convert.FromBase64String(afterClose.RowVersion)));
        Assert.Equal(HttpStatusCode.NotFound, unknownDepartment.StatusCode);

        // The CS Agent reopens: back to InProgress, ReopenCount incremented,
        // live resolution outcome cleared, owner released to the queue.
        var reopenResponse = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Customer called back — still not cooling.", departmentId, Convert.FromBase64String(afterClose.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, reopenResponse.StatusCode);
        var reopened = await reopenResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("InProgress", reopened!.TicketStatus);
        Assert.Equal(1, reopened.ReopenCount);
        Assert.Null(reopened.ResolutionOutcome);
        Assert.Null(reopened.CurrentOwnerEmployeeId);
        Assert.Equal(departmentId, reopened.CurrentDepartmentId);

        // Identity is untouched — same ticket, not a copy.
        Assert.Equal(ticketId, reopened.TicketId);
        Assert.Equal(detail.TicketNumber, reopened.TicketNumber);

        // The reason is now readable: the lifecycle history endpoint serves the
        // row the reopen wrote, which is what Ticket Details renders.
        var history = await (await agentClient.GetAsync($"/api/tickets/{ticketId}/history"))
            .Content.ReadFromJsonAsync<TicketLifecycleHistoryDto>();
        var reopenEntry = Assert.Single(
            history!.Entries,
            e => e.Dimension == "TicketStatus" && e.OldValue == "Closed" && e.NewValue == "InProgress");
        Assert.Equal("Customer called back — still not cooling.", reopenEntry.Note);
        Assert.False(reopenEntry.ActorIsSystem);

        // ...and the typed event carries the routing/SLA facts beside it.
        var approvals = await (await agentClient.GetAsync($"/api/tickets/{ticketId}/approvals"))
            .Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
        var reopenEvent = Assert.Single(approvals!.Events, e => e.EventType == "Reopened");
        Assert.True(ReopenActivityFacts.TryParse(reopenEvent.Note, out var facts));
        Assert.Equal(departmentId, facts.ToDepartmentId);
        Assert.Equal(1, facts.ReopenCount);

        // An actively-worked ticket has nothing to reopen.
        var secondReopen = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Already reopened.", departmentId, Convert.FromBase64String(reopened.RowVersion)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, secondReopen.StatusCode);
    }

    [Fact]
    public async Task Reopen_MovesTheTicketOutOfClosedAndBackIntoTheActiveQueue_WithoutDuplicatingIt()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, initialRowVersion) = await CreateVerifiedTicketAsync(agentClient);

        var (workerUsername, workerPassword, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);
        var workerClient = _factory.CreateClient();
        var login = await (await workerClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto(workerUsername, workerPassword))).Content.ReadFromJsonAsync<LoginResponseDto>();
        workerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var (headUsername, headPassword, headId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(headId, departmentId);
        var headClient = _factory.CreateClient();
        var headLogin = await (await headClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto(headUsername, headPassword))).Content.ReadFromJsonAsync<LoginResponseDto>();
        headClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", headLogin!.AccessToken);

        var afterAssign = await (await headClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(workerId, initialRowVersion)))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterStatus = await (await workerClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterResolve = await (await workerClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, Convert.FromBase64String(afterStatus!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterClose = await (await agentClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        Assert.Contains(await QueueTicketIdsAsync(agentClient, "Closed"), id => id == ticketId);

        var reopened = await (await agentClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/reopen",
                new ReopenTicketRequestDto("Customer called back.", departmentId, Convert.FromBase64String(afterClose!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("InProgress", reopened!.TicketStatus);

        // Gone from Closed, present among the active work — both are live
        // queries over the same row, so nothing had to be "refreshed".
        Assert.DoesNotContain(await QueueTicketIdsAsync(agentClient, "Closed"), id => id == ticketId);
        Assert.Contains(await QueueTicketIdsAsync(agentClient, "InProgress"), id => id == ticketId);

        // No owner, so it waits in the department queue rather than in anyone's
        // My Tickets — including the employee who worked it before.
        Assert.Null(reopened.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(await QueueTicketIdsAsync(agentClient, ticketStatus: null, ownerEmployeeId: workerId), id => id == ticketId);

        // A reopen is not a new ticket: the queue holds exactly one row for it,
        // at its current state, and the customer's history gained no sibling
        // copy of it (the anchor ticket is excluded from its own history).
        var active = await QueueTicketIdsAsync(agentClient, "InProgress");
        Assert.Single(active, id => id == ticketId);

        var history = await (await agentClient.GetAsync($"/api/tickets/{ticketId}/customer-history?limit=10"))
            .Content.ReadFromJsonAsync<CustomerHistoryDto>();
        // (Other tests in this shared fixture contribute their own tickets to
        // this customer's history — what matters is that the reopened one is
        // not among them a second time.)
        Assert.DoesNotContain(history!.Tickets, t => t.TicketId == ticketId);

        var detail = await (await agentClient.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("InProgress", detail!.TicketStatus);
        Assert.False(detail.IsReopenEligible);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task Reopen_ByEveryAuthorizedRole_Returns200(string role)
    {
        // The final approved rule end to end, through the real endpoint: the
        // CS layer holds direct Reopen, and System Administrator arrives at
        // the same place through ADR-0024's central override rather than
        // through TicketRoleSets.Reopen, which does not name it.
        var setupClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, closedRowVersion) = await DriveTicketToClosedAsync(setupClient);

        var client = await CreateAuthenticatedClientAsync(role);

        // A CS Agent reopens only a ticket they can see: this one took the call too.
        if (role == Roles.CsAgent)
        {
            await _factory.RecordHandlerAsync(ticketId, client);
        }

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Customer called back.", departmentId, closedRowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reopened = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("InProgress", reopened!.TicketStatus);
        Assert.Equal(ticketId, reopened.TicketId);
        Assert.Equal(1, reopened.ReopenCount);
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task Reopen_ByEveryUnauthorizedRole_Returns403_AndLeavesTheTicketClosed(string role)
    {
        // Direct endpoint access is the bypass worth closing: these roles see
        // no Reopen control, and calling the endpoint anyway changes nothing.
        // The department-scoped ones are given membership of the ticket's own
        // department first, so the refusal is the role rule, not visibility.
        var setupClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, closedRowVersion) = await DriveTicketToClosedAsync(setupClient);

        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        await _factory.AssignPrimaryDepartmentAsync(employeeId, departmentId);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Let me reopen this.", departmentId, closedRowVersion));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // ...and the ticket is untouched, so a refused call cannot be a
        // partial one: still Closed, still reopen-eligible for CS.
        var detail = await (await setupClient.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", detail!.TicketStatus);
        Assert.Equal(0, detail.ReopenCount);
        Assert.True(detail.IsReopenEligible);
    }

    /// <summary>
    /// Creates a ticket and drives it through the normal lifecycle actors to
    /// Closed/Resolved — the one state Reopen applies to — returning the
    /// RowVersion a reopen must present.
    /// </summary>
    private async Task<(long TicketId, int DepartmentId, byte[] ClosedRowVersion)> DriveTicketToClosedAsync(HttpClient csClient)
    {
        var (ticketId, departmentId, initialRowVersion) = await CreateVerifiedTicketAsync(csClient);

        var (workerUsername, workerPassword, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);
        var workerClient = _factory.CreateClient();
        var workerLogin = await (await workerClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto(workerUsername, workerPassword))).Content.ReadFromJsonAsync<LoginResponseDto>();
        workerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", workerLogin!.AccessToken);

        var (headUsername, headPassword, headId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(headId, departmentId);
        var headClient = _factory.CreateClient();
        var headLogin = await (await headClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto(headUsername, headPassword))).Content.ReadFromJsonAsync<LoginResponseDto>();
        headClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", headLogin!.AccessToken);

        var afterAssign = await (await headClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(workerId, initialRowVersion)))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterStatus = await (await workerClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterResolve = await (await workerClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, Convert.FromBase64String(afterStatus!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterClose = await (await csClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        return (ticketId, departmentId, Convert.FromBase64String(afterClose!.RowVersion));
    }

    private static async Task<IReadOnlyList<long>> QueueTicketIdsAsync(
        HttpClient client, string? ticketStatus, Guid? ownerEmployeeId = null)
    {
        var query = $"?page=1&pageSize=100{(ticketStatus is null ? "" : $"&ticketStatus={ticketStatus}")}"
            + (ownerEmployeeId is { } owner ? $"&ownerEmployeeId={owner}" : string.Empty);
        var result = await (await client.GetAsync($"/api/tickets{query}")).Content.ReadFromJsonAsync<TicketListResultDto>();
        return [.. result!.Items.Select(t => t.TicketId)];
    }

    [Fact]
    public async Task Assign_ByDepartmentHeadOfADifferentDepartment_Returns403_PreventsCrossDepartmentAssignment()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, _, _) = await CreateVerifiedTicketAsync(agentClient);

        // Department Head genuinely holds Assign authority (PR correction) —
        // just not for a department they don't belong to, which is what
        // this test isolates.
        var (outsiderUsername, outsiderPassword, outsiderId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        var otherDepartmentId = await _factory.CreateDepartmentAsync("Unrelated " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.AssignPrimaryDepartmentAsync(outsiderId, otherDepartmentId);

        var outsiderClient = _factory.CreateClient();
        var loginResponse = await outsiderClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(outsiderUsername, outsiderPassword));
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        outsiderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var response = await outsiderClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(outsiderId, []));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ByDepartmentHead_Returns403_TransferIsCsManagerOnly()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, _) = await CreateVerifiedTicketAsync(agentClient);

        var (deptHeadUsername, deptHeadPassword, deptHeadId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(deptHeadId, departmentId);
        var deptHeadClient = _factory.CreateClient();
        var loginResponse = await deptHeadClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(deptHeadUsername, deptHeadPassword));
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        deptHeadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var otherDepartmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/transfer", new TransferTicketRequestDto(otherDepartmentId, "Misrouted", []));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClosedTicket_AssignTransferStatusResolveClose_AllReturn422_NoFurtherStateChange()
    {
        var agentClient = await CreateAuthenticatedClientAsync(Roles.CsAgent);
        var (ticketId, departmentId, initialRowVersion) = await CreateVerifiedTicketAsync(agentClient);

        var (deptHeadUsername, deptHeadPassword, deptHeadId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        await _factory.AssignPrimaryDepartmentAsync(deptHeadId, departmentId);
        var deptHeadClient = _factory.CreateClient();
        var deptHeadLoginResponse = await deptHeadClient.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(deptHeadUsername, deptHeadPassword));
        var deptHeadLogin = await deptHeadLoginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        deptHeadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deptHeadLogin!.AccessToken);

        var assignResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(deptHeadId, initialRowVersion));
        var afterAssign = await assignResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var statusResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion)));
        var afterStatus = await statusResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var resolveResponse = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, Convert.FromBase64String(afterStatus!.RowVersion)));
        var afterResolve = await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var closeResponse = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve!.RowVersion)));
        var closed = await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", closed!.TicketStatus);
        var closedRowVersion = Convert.FromBase64String(closed.RowVersion);

        // Every mutating operation now rejects with 422 — no exceptions.
        var reassign = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment", new AssignTicketRequestDto(deptHeadId, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reassign.StatusCode);

        var otherDepartmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var managerClient = await CreateAuthenticatedClientAsync(Roles.CsManager);
        var transfer = await managerClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/transfer", new TransferTicketRequestDto(otherDepartmentId, "n/a", closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, transfer.StatusCode);

        var statusChange = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new ChangeStatusRequestDto("InProgress", closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, statusChange.StatusCode);

        var reResolve = await deptHeadClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "n/a", null, null, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reResolve.StatusCode);

        var reClose = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reClose.StatusCode);

        // Reading remains allowed throughout.
        var detail = await agentClient.GetAsync($"/api/tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var finalDetail = await detail.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", finalDetail!.TicketStatus);

        var notes = await agentClient.GetAsync($"/api/tickets/{ticketId}/notes");
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
    }

    [Fact]
    public async Task GetQueue_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/tickets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
