using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Requirement 6 of the authorization correction (ADR-0024): proves a
/// System Administrator JWT is authorized for <b>every</b> currently
/// protected API endpoint/action, end-to-end against the real host — real
/// routing, real JWT validation, the real policy catalog and the real
/// application services.
///
/// <para>
/// <b>Authorized, not merely "not 403".</b> Each test asserts the endpoint's
/// own success status. A 403 would fail, and so would a 500 or a
/// business-rule rejection dressed up as success, which is what makes these
/// tests evidence for requirement 2 as well: the administrator reaches the
/// business logic and the business logic behaves normally.
/// </para>
///
/// <para>
/// The account created here holds <b>only</b> <c>Roles.SystemAdministrator</c>
/// — requirement 3. Every authorization it passes below therefore comes from
/// the central override, not from a second role quietly granted to make a
/// test pass. <c>SeedEmployeeAsync</c> assigns exactly one role, and
/// <see cref="AdministratorHoldsOnlyTheSystemAdministratorRole"/> asserts it
/// against the live token.
/// </para>
/// </summary>
public class SystemAdministratorEndpointAuthorizationTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public SystemAdministratorEndpointAuthorizationTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return (client, employeeId);
    }

    private Task<(HttpClient Client, Guid EmployeeId)> CreateAdministratorAsync() =>
        CreateClientAsync(Roles.SystemAdministrator);

    /// <summary>Runs the real intake -> customer lookup -> ticket sequence as the given client, returning the created ticket.</summary>
    private async Task<TicketDetailDto> CreateVerifiedTicketAsync(HttpClient client, string departmentPrefix)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(
            departmentPrefix + " " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // "+971509990001" matches MockCrmGateway's CRM-UNIT-1107/CRM-CONTACT-3010 fixture (Sami Nasser, an Owner — a valid Buyer ownership record).
        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990001", null, true, "5001", null));
        intakeResponse.EnsureSuccessStatusCode();
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        lookupResponse.EnsureSuccessStatusCode();
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmCustomer = Assert.Single(lookup!.Sources.Single(s => s.Source == "Crm").Customers);
        var crmUnit = Assert.Single(crmCustomer.Units);

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, crmUnit.UnitReferenceId, crmUnit.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling"));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var created = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        await _factory.RecordHandlerAsync(created!.TicketId, client);

        var detailResponse = await client.GetAsync($"/api/tickets/{created.TicketId}");
        detailResponse.EnsureSuccessStatusCode();
        return (await detailResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static byte[] RowVersionOf(TicketDetailDto ticket) => Convert.FromBase64String(ticket.RowVersion);

    /// <summary>
    /// Runs intake -> CRM Buyer Lookup -> ticket creation, producing a ticket
    /// with a real CrmBuyerCustomerId (the identity Customer History keys
    /// verified lookups on) rather than <see cref="CreateVerifiedTicketAsync"/>'s
    /// legacy UnitReferenceId/ContactReferenceId pair. Uses
    /// FakeCrmBuyerLookupGateway's fixed fixture (CustomerId 9001).
    /// </summary>
    private async Task<TicketDetailDto> CreateCrmBuyerVerifiedTicketAsync(HttpClient client, string departmentPrefix)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(
            departmentPrefix + " " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000900", departmentId, false, null, null));
        intakeResponse.EnsureSuccessStatusCode();
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var buyersResponse = await client.GetAsync("/api/crm/buyers?phoneNumber=%2B971500000900");
        buyersResponse.EnsureSuccessStatusCode();
        var buyers = await buyersResponse.Content.ReadFromJsonAsync<List<CrmBuyerMatchDto>>();
        var buyer = Assert.Single(buyers!);
        var unit = Assert.Single(buyer.Units);

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.High, "AC unit not cooling",
                CrmBuyerCustomerId: buyer.Customer.CustomerId,
                CrmBuyerLeadId: unit.LeadId,
                CrmBuyerUnitId: unit.UnitId,
                CrmBuyerProjectId: unit.ProjectId,
                CrmBuyerCustomerName: buyer.Customer.FullNameEnglish,
                CrmBuyerProjectName: unit.ProjectName,
                CrmBuyerUnitNumber: unit.UnitNumber));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var created = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();

        var detailResponse = await client.GetAsync($"/api/tickets/{created!.TicketId}");
        detailResponse.EnsureSuccessStatusCode();
        return (await detailResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    // ---------------------------------------------------------------
    // Requirement 3 — the account is only ever "System Administrator".
    // ---------------------------------------------------------------

    [Fact]
    public async Task AdministratorHoldsOnlyTheSystemAdministratorRole()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<CurrentUserResponseDto>();
        Assert.Equal([Roles.SystemAdministrator], profile!.Roles);
    }

    // ---------------------------------------------------------------
    // Identity and Access endpoints
    // ---------------------------------------------------------------

    [Fact]
    public async Task Logout_Returns204()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GetOwnProfile_Returns200()
    {
        var (client, employeeId) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<CurrentUserResponseDto>();
        Assert.Equal(employeeId, profile!.EmployeeId);
    }

    [Fact]
    public async Task ListAssignableUsers_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var (_, _, agentId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var first = await _factory.CreateDepartmentAsync("Dir A " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var second = await _factory.CreateDepartmentAsync("Dir B " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.AssignPrimaryDepartmentAsync(agentId, first);
        await _factory.AssignDepartmentAsync(agentId, second);

        var response = await client.GetAsync("/api/users/assignable");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<AssignableUserDto>>();
        // One row per employee, carrying every department (primary first) and the roles.
        var agent = Assert.Single(users!, u => u.EmployeeId == agentId);
        Assert.Equal([Roles.CsAgent], agent.Roles);
        Assert.Equal([first, second], agent.Departments.Select(d => d.DepartmentId));
        Assert.True(agent.Departments.First().IsPrimary);
    }

    [Fact]
    public async Task TransferTicket_WithAssignToEmployeeId_MovesAndAssignsInOneStep_AndRefusesAnOutsider()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");
        var targetDepartmentId = await _factory.CreateDepartmentAsync("Leasing " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (_, _, insiderId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        await _factory.AssignPrimaryDepartmentAsync(insiderId, targetDepartmentId);
        var (_, _, outsiderId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        await _factory.AssignPrimaryDepartmentAsync(outsiderId, ticket.CurrentDepartmentId);

        // The assignee must belong to the RESULTING (target) department —
        // a member of the source department only is refused, and nothing moves.
        var refused = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/transfer",
            new TransferTicketRequestDto(targetDepartmentId, "Needs Leasing", RowVersionOf(ticket), outsiderId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var unchanged = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(ticket.CurrentDepartmentId, unchanged!.CurrentDepartmentId);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/transfer",
            new TransferTicketRequestDto(targetDepartmentId, "Needs Leasing", RowVersionOf(ticket), insiderId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(targetDepartmentId, updated!.CurrentDepartmentId);
        Assert.Equal(insiderId, updated.CurrentOwnerEmployeeId);
        Assert.Equal(ticket.OriginatingDepartmentId, updated.OriginatingDepartmentId);
    }

    [Fact]
    public async Task GetRoleCatalog_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SetUserActivation_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        // A different employee, not the administrator themselves: the
        // "cannot deactivate the last active System Administrator" rule
        // (UserActivationAppService) is a business rule the override does not
        // touch, and this test is about authorization.
        var (_, _, targetEmployeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);

        var response = await client.PatchAsJsonAsync(
            $"/api/users/{targetEmployeeId}/activation", new ActivationRequestDto(false, "Left the company"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListDepartments_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/departments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListChannels_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/channels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var channels = await response.Content.ReadFromJsonAsync<List<TigerCS.Application.Modules.Ticketing.Dto.ChannelDto>>();
        Assert.Contains(channels!, c => c.Code == "PHONE" && c.IsActive);
    }

    [Fact]
    public async Task ListDepartmentUsers_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await client.GetAsync($"/api/departments/{departmentId}/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCategories_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // CRM unit/contact lookup (CustomerVerification policy)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCrmUnit_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SearchCrmUnits_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/search?unitNumber=1204");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCrmUnitContacts_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// CRM Buyer Lookup. <see cref="TigerCsApiFactory"/> swaps
    /// <c>ICrmBuyerLookupGateway</c> for a fixture-backed fake in this test
    /// host — there is no real CRM to call here, same reasoning as
    /// <c>MockCrmGateway</c> for the unit/contact lookups above.
    /// </summary>
    [Fact]
    public async Task GetBuyerByPhone_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/buyers?phoneNumber=%2B971500000900");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Verification sessions
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateVerificationSession_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();

        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        var response = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task GetVerificationSession_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var created = await (await client.PostAsJsonAsync(
                "/api/verification-sessions",
                new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation")))
            .Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var response = await client.GetAsync($"/api/verification-sessions/{created!.VerificationSessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Intake records — requirement 8, called out explicitly.
    // ---------------------------------------------------------------

    /// <summary>
    /// Requirement 8, verbatim: <c>POST /api/intake-records</c> with a System
    /// Administrator JWT succeeds with 201. Before this correction the
    /// CustomerVerification policy denied the role outright (403); the
    /// central override is the only thing that changed.
    /// </summary>
    [Fact]
    public async Task CreateIntakeRecord_WithSystemAdministratorJwt_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Authorized *and* functional: the record is really created, not a
        // 201 over an empty write.
        var intake = await response.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        Assert.True(intake!.IntakeRecordId > 0);
        Assert.Equal("PHONE", intake.ChannelId);
        Assert.True(intake.IsUnitRelated);
    }

    // ---------------------------------------------------------------
    // Customer lookup
    // ---------------------------------------------------------------

    [Fact]
    public async Task SearchCustomerLookup_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = await response.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        Assert.Equal(3, lookup!.Sources.Count);
    }

    // ---------------------------------------------------------------
    // Ticket creation
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateTicket_WithCustomerMatch_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();

        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        Assert.Equal("Verified", ticket.VerificationStatus);
        Assert.Equal("Open", ticket.TicketStatus);
    }

    [Fact]
    public async Task CreateTicket_NonUnitIntake_Returns201()
    {
        var (client, _) = await CreateAdministratorAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var response = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "General billing question"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Ticket queries
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetTicketQueue_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/tickets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetTicketDetail_ForATicketInADepartmentTheAdministratorDoesNotBelongTo_Returns200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var response = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetTicketCustomerHistory_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateCrmBuyerVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync($"/api/tickets/{ticket.TicketId}/customer-history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<CustomerHistoryDto>();
        Assert.Equal("Verified", history!.VerificationType);
    }

    [Fact]
    public async Task GetTicketCustomerProfile_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateCrmBuyerVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync($"/api/tickets/{ticket.TicketId}/customer-profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<CustomerProfileDto>();
        Assert.Equal("Found", profile!.Status);
        Assert.Equal(ticket.CrmBuyerCustomerId, profile.CrmBuyerCustomerId);
    }

    /// <summary>
    /// Genesys integration phase 1, end-to-end through the real host: a
    /// normalized inquiry becomes exactly one ticket, a retry returns that
    /// same ticket, a ringing call creates nothing, and ending the
    /// conversation stores the transcript without closing the ticket.
    /// </summary>
    [Fact]
    public async Task GenesysEndpoints_AuthorizedThroughTheOverride()
    {
        var (client, _) = await CreateAdministratorAsync();
        await _factory.SeedPrioritiesAsync();

        // A department is all the configuration an inquiry needs: there is no
        // category to configure, because an unread inquiry has none.
        var departmentId = await _factory.CreateDepartmentAsync("Genesys CS " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("NOC for Resale", departmentId);

        var conversationId = "conv-" + Guid.NewGuid().ToString("N")[..12];

        // The agent picks up — a ringing call never reaches TigerCS, and
        // there is no event field to say so. Exactly one ticket.
        var answered = await client.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(conversationId, "Phone", CustomerPhone: "+971500000001", DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.Created, answered.StatusCode);
        var created = await answered.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();
        Assert.Equal("TicketCreated", created!.Outcome);

        // A retry returns the SAME ticket and creates no second one.
        var retry = await client.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(conversationId, "Phone", CustomerPhone: "+971500000001", DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retried = await retry.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();
        Assert.Equal("AlreadyIngested", retried!.Outcome);
        Assert.Equal(created.TicketId, retried.TicketId);

        // The ticket exists, and is honestly Unclassified: no category was
        // invented. It starts on the default Normal priority, so its SLA runs
        // from creation.
        var unclassified = await (await client.GetAsync($"/api/tickets/{created.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Null(unclassified!.CategoryId);
        Assert.False(unclassified.IsClassified);
        Assert.Equal(nameof(TigerCS.Domain.Modules.Ticketing.SlaState.Running), unclassified.SlaState);

        // The agent reads the conversation and classifies the SAME ticket.
        var classified = await client.PostAsJsonAsync(
            $"/api/tickets/{created.TicketId}/classification",
            new ClassifyTicketRequestDto(categoryId, (byte)PriorityLevel.High, null, Convert.FromBase64String(unclassified.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, classified.StatusCode);
        var afterClassification = await classified.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(created.TicketId, afterClassification!.TicketId);
        Assert.Equal(categoryId, afterClassification.CategoryId);
        Assert.True(afterClassification.IsClassified);
        Assert.Equal(nameof(TigerCS.Domain.Modules.Ticketing.SlaState.Running), afterClassification.SlaState);

        // Ending the conversation stores the transcript — and leaves the
        // ticket exactly where the workflow had it.
        var ended = await client.PatchAsJsonAsync(
            $"/api/genesys/tickets/{created.TicketId}",
            new GenesysTicketUpdateRequest(
                conversationId,
                Ended: new GenesysConversationEndPart(
                    DateTime.UtcNow, "AgentDisconnect",
                    [
                        new GenesysTranscriptMessageRequest("Customer", DateTime.UtcNow.AddMinutes(-2), "Any update on my unit?"),
                        new GenesysTranscriptMessageRequest("HumanAgent", DateTime.UtcNow.AddMinutes(-1), "Checking now.")
                    ])));
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        var endResult = await ended.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>();
        Assert.Equal("Applied", endResult!.Outcome);
        Assert.True(endResult.ConversationEnded);
        Assert.Equal(2, endResult.TranscriptMessageCount);
        // The echoed status is the proof: ending a conversation did not close
        // the ticket.
        Assert.Equal(nameof(TigerCS.Domain.Modules.Ticketing.TicketStatus.Open), endResult.TicketStatus);

        var ticketAfterEnd = await _factory.GetTicketAsync(created.TicketId);
        Assert.Equal(TigerCS.Domain.Modules.Ticketing.TicketStatus.Open, ticketAfterEnd!.TicketStatus);

        // A duplicate end changes nothing and stores no second transcript.
        var endedAgain = await client.PatchAsJsonAsync(
            $"/api/genesys/tickets/{created.TicketId}",
            new GenesysTicketUpdateRequest(conversationId, Ended: new GenesysConversationEndPart(DateTime.UtcNow, "AgentDisconnect")));
        Assert.Equal(HttpStatusCode.OK, endedAgain.StatusCode);
        Assert.Equal(2, (await endedAgain.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!.TranscriptMessageCount);

        // And the conversation cannot be updated through another ticket's route.
        var mismatch = await client.PatchAsJsonAsync(
            $"/api/genesys/tickets/{created.TicketId + 9999}",
            new GenesysTicketUpdateRequest(conversationId));
        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
    }

    /// <summary>
    /// The call-pickup lookup, contract #2: an agent answers and Genesys asks
    /// who is on the line.
    ///
    /// <para>
    /// Two things matter here. A lookup that matches nobody is still a
    /// <b>200</b>, never a 404 — because the Create Ticket call that follows
    /// must still work. And where the caller already has tickets, they come
    /// back with the open ones first, so the agent knows before they speak.
    /// (This host&apos;s CRM double matches every number, so
    /// <c>found: false</c> itself is proved in
    /// <c>GenesysCustomerLookupTests</c>, where the gateway is controlled.)
    /// </para>
    /// </summary>
    [Fact]
    public async Task GenesysCustomerLookup_AuthorizedThroughTheOverride()
    {
        var (client, _) = await CreateAdministratorAsync();
        await _factory.SeedPrioritiesAsync();

        var unknownNumber = "+9715" + Random.Shared.Next(10_000_000, 99_999_999);

        // Nobody by that number, and nothing about that is an error.
        var miss = await client.GetAsync($"/api/genesys/customers/lookup?phoneNumber={Uri.EscapeDataString(unknownNumber)}");
        Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
        var nothingFound = await miss.Content.ReadFromJsonAsync<GenesysCustomerLookupResultDto>();
        Assert.Equal(unknownNumber, nothingFound!.PhoneNumber);
        Assert.NotNull(nothingFound.CrmStatus);
        // No ticket has ever arrived from this number.
        Assert.Empty(nothingFound.Tickets);
        Assert.Equal(0, nothingFound.OpenTicketCount);

        // A blank number is the one refusal.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/genesys/customers/lookup?phoneNumber=%20")).StatusCode);

        // Ticket creation is unaffected by the miss — the whole point.
        var departmentId = await _factory.CreateDepartmentAsync("Genesys Lookup " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var conversationId = "conv-" + Guid.NewGuid().ToString("N")[..12];
        var created = await client.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(
                conversationId, "Phone", CustomerPhone: unknownNumber, DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ticket = await created.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();

        // The same number now carries that ticket as context for the next call.
        var hit = await client.GetAsync($"/api/genesys/customers/lookup?phoneNumber={Uri.EscapeDataString(unknownNumber)}");
        var withContext = await hit.Content.ReadFromJsonAsync<GenesysCustomerLookupResultDto>();
        var row = Assert.Single(withContext!.Tickets);
        Assert.Equal(ticket!.TicketId, row.TicketId);
        Assert.Equal(ticket.TicketNumber, row.TicketNumber);
        Assert.True(row.IsOpen);
        Assert.Equal(1, withContext.OpenTicketCount);
    }

    /// <summary>The unit-details route honours the System Administrator override like the rest of the Genesys boundary; the ownership rules themselves are in <c>GenesysCustomerUnitDetailsEndpointTests</c>.</summary>
    [Fact]
    public async Task GenesysCustomerUnitDetails_AuthorizedThroughTheOverride()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.PostAsJsonAsync(
            "/api/genesys/customers/unit-details",
            new GenesysCustomerUnitDetailsRequest("crm:9001", "tel:+971500000900"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The chatbot buyer-verification endpoints sit behind the same
    /// CustomerVerification policy as the other Genesys routes, so the System
    /// Administrator override reaches them: each is authorized and answered by
    /// the service (here, with the shipped switch still off) — never 401/403.
    /// Functional behaviour: <c>GenesysDocumentsEndpointTests</c>.
    /// </summary>
    [Fact]
    public async Task GenesysBuyerVerification_AuthorizedThroughTheOverride()
    {
        var (client, _) = await CreateAdministratorAsync();

        var bodies = new Dictionary<string, object>
        {
            ["buyer-lookup"] = new { phoneNumber = "+971501234567" },
            ["otp/send"] = new { phoneNumber = "+971501234567", crmUnitId = "1" },
            ["otp/resend"] = new { challengeId = Guid.NewGuid() },
            ["otp/verify"] = new { challengeId = Guid.NewGuid(), code = "123456" }
        };

        foreach (var (route, body) in bodies)
        {
            var response = await client.PostAsJsonAsync("/api/genesys/verification/" + route, body);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("DOCUMENT_COPY_DISABLED", await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// The chatbot document-copy endpoint sits behind the same
    /// CustomerVerification policy as the other Genesys routes, so the System
    /// Administrator override reaches it: the request is authorized and
    /// answered by the service (here, with the shipped switch still off) —
    /// never 401/403. The functional behaviour is covered by
    /// <c>GenesysDocumentsEndpointTests</c>.
    /// </summary>
    [Fact]
    public async Task GenesysDocumentSendCopy_AuthorizedThroughTheOverride()
    {
        var (client, _) = await CreateAdministratorAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/genesys/documents/send-copy")
        {
            Content = JsonContent.Create(new { verificationSessionId = Guid.NewGuid(), documentType = "Contract" })
        };
        request.Headers.Add("Idempotency-Key", "sysadmin-" + Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("DOCUMENT_COPY_DISABLED", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The AI-first journey, end to end through the real host: a website chat
    /// a bot could not finish, no agent available, and an agent picking the
    /// work up later from the list.
    ///
    /// <para>
    /// The point of doing this on <b>website chat</b> rather than phone is
    /// that none of it is a callback — the follow-up mode is ContinueChat,
    /// and nothing in the flow assumes dialling anybody.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PendingCustomerInteractions_AuthorizedThroughTheOverride()
    {
        var (client, administrator) = await CreateAdministratorAsync();
        await _factory.SeedPrioritiesAsync();

        var departmentId = await _factory.CreateDepartmentAsync("Genesys Chat " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var conversationId = "conv-" + Guid.NewGuid().ToString("N")[..12];

        // The chat starts and the ticket is created — Unclassified, as always.
        var started = await client.PostAsJsonAsync(
            "/api/genesys/tickets",
            new GenesysInquiryRequest(
                conversationId, "WebsiteChat",
                CustomerPhone: "+971500000055", CustomerName: "Ahmed Ali", DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var ticket = await started.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();

        // The bot cannot finish, and no agent is free.
        var handoffRequested = await client.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticket!.TicketId}",
            new GenesysTicketUpdateRequest(
                conversationId,
                Handoff: new GenesysHandoffPart(
                    Required: true, AgentAvailable: false, Mode: "ContinueChat",
                    Reason: "Customer asked about an NOC for resale")));
        Assert.Equal(HttpStatusCode.OK, handoffRequested.StatusCode);
        var handoff = await handoffRequested.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>();
        Assert.Equal("WaitingForAgent", handoff!.HandoffStatus);

        // Same ticket — a handoff never creates a second one.
        Assert.Equal(ticket.TicketId, handoff.TicketId);

        // A redelivered handoff returns the SAME work item.
        var handoffRetry = await client.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticket.TicketId}",
            new GenesysTicketUpdateRequest(
                conversationId, Handoff: new GenesysHandoffPart(Required: true, Mode: "ContinueChat")));
        Assert.Equal(HttpStatusCode.OK, handoffRetry.StatusCode);
        var retried = await handoffRetry.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>();
        Assert.Equal(handoff.TicketAgentHandoffId, retried!.TicketAgentHandoffId);

        // The customer closes the browser while waiting. The transcript is
        // stored, the ticket stays open — and the work stays actionable.
        var ended = await client.PatchAsJsonAsync(
            $"/api/genesys/tickets/{ticket.TicketId}",
            new GenesysTicketUpdateRequest(
                conversationId,
                Ended: new GenesysConversationEndPart(
                    DateTime.UtcNow, "CustomerDisconnect",
                    [
                        new GenesysTranscriptMessageRequest("Customer", DateTime.UtcNow.AddMinutes(-3), "I want to sell my apartment."),
                        new GenesysTranscriptMessageRequest("VirtualAgent", DateTime.UtcNow.AddMinutes(-2), "Are you asking about an NOC for resale?"),
                        new GenesysTranscriptMessageRequest("Customer", DateTime.UtcNow.AddMinutes(-1), "Yes.")
                    ])));
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        // The work stayed outstanding through the disconnect.
        Assert.Equal("WaitingForAgent", (await ended.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!.HandoffStatus);

        // The work list still shows it, and shows it as waiting.
        var list = await (await client.GetAsync("/api/pending-customer-interactions"))
            .Content.ReadFromJsonAsync<AgentHandoffListResultDto>();
        var row = Assert.Single(list!.Items, i => i.TicketAgentHandoffId == handoff.TicketAgentHandoffId!.Value);
        Assert.Equal("WaitingForAgent", row.Status);
        Assert.Equal("ContinueChat", row.Mode);
        Assert.Equal("Ahmed Ali", row.CustomerName);
        Assert.True(row.InteractionEnded);
        // Still unclassified, and still on the list: pending human work never
        // waits for classification.
        Assert.False(row.TicketIsClassified);

        // Opening the ticket shows the agent the bot conversation that led
        // here, and why a human was asked for.
        var history = await (await client.GetAsync($"/api/tickets/{ticket.TicketId}/interactions"))
            .Content.ReadFromJsonAsync<TicketInteractionHistoryDto>();
        var interaction = Assert.Single(history!.Interactions);
        Assert.Equal(3, interaction.Messages.Count);
        Assert.Contains(interaction.Messages, m => m.Sender == "VirtualAgent");
        Assert.Equal("Customer asked about an NOC for resale", interaction.Handoff!.RequestReason);
        Assert.Equal("WaitingForAgent", interaction.Handoff.Status);

        // Accepting the work makes the accepting employee the TICKET's owner,
        // and an owner must be an active member of the ticket's current
        // department. That is a domain invariant about the ticket's data, not a
        // permission, so ADR-0024's override deliberately does NOT reach it —
        // the administrator is refused exactly as anyone else would be.
        var refusedBeforeMembership = await client.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoff.TicketAgentHandoffId!.Value}/start", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedBeforeMembership.StatusCode);

        // ...and nothing was claimed by that refusal.
        Assert.Equal(
            "WaitingForAgent",
            (await (await client.GetAsync("/api/pending-customer-interactions"))
                .Content.ReadFromJsonAsync<AgentHandoffListResultDto>())!
                .Items.Single(i => i.TicketAgentHandoffId == handoff.TicketAgentHandoffId!.Value).Status);

        // With membership of the ticket's department the invariant is satisfied
        // and the whole flow completes — which is what proves the override
        // reaches these endpoints' AUTHORIZATION, the thing this test is about.
        await _factory.AssignPrimaryDepartmentAsync(administrator, departmentId);

        // The agent takes it, then finishes it.
        var startedWork = await client.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoff.TicketAgentHandoffId!.Value}/start", new { });
        Assert.Equal(HttpStatusCode.OK, startedWork.StatusCode);
        var inProgress = await startedWork.Content.ReadFromJsonAsync<AgentHandoffDto>();
        Assert.Equal("InProgress", inProgress!.Status);
        Assert.Equal(administrator, inProgress.AssignedEmployeeId);

        var completed = await client.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoff.TicketAgentHandoffId!.Value}/complete",
            new CompleteAgentHandoffRequestDto("Replied in the chat thread; NOC request logged."));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("Completed", (await completed.Content.ReadFromJsonAsync<AgentHandoffDto>())!.Status);

        // The load-bearing separation: the human work is done, the TICKET is
        // not. The NOC workflow continues. It is InProgress rather than Open
        // because accepting the interaction took ownership of it and started
        // it — and it is emphatically neither Resolved nor Closed, which is
        // what this assertion is really about.
        var ticketAfter = await _factory.GetTicketAsync(ticket.TicketId);
        Assert.Equal(TigerCS.Domain.Modules.Ticketing.TicketStatus.InProgress, ticketAfter!.TicketStatus);
        Assert.Null(ticketAfter.ResolutionOutcome);

        // Completed work leaves the default list.
        var afterList = await (await client.GetAsync("/api/pending-customer-interactions"))
            .Content.ReadFromJsonAsync<AgentHandoffListResultDto>();
        Assert.DoesNotContain(afterList!.Items, i => i.TicketAgentHandoffId == handoff.TicketAgentHandoffId!.Value);

        // A second completion is refused rather than double-counted.
        var again = await client.PostAsJsonAsync(
            $"/api/pending-customer-interactions/{handoff.TicketAgentHandoffId!.Value}/complete",
            new CompleteAgentHandoffRequestDto(null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // And cancelling requires a reason.
        var noReason = await client.PostAsJsonAsync(
            "/api/pending-customer-interactions/999999/cancel", new CancelAgentHandoffRequestDto("   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
    }

    [Fact]
    public async Task GetTicketReviews_Returns200_WithNoTokens()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync($"/api/tickets/{ticket.TicketId}/reviews");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"cycles\":[]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/tickets/987654/reviews")).StatusCode);
    }

    [Fact]
    public async Task GetTicketInteractions_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync($"/api/tickets/{ticket.TicketId}/interactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<TicketInteractionHistoryDto>();
        Assert.Equal(ticket.TicketId, history!.TicketId);

        // A manually-created ticket still has its originating interaction —
        // a locally-sourced one, with no Genesys conversation and no
        // transcript.
        var interaction = Assert.Single(history.Interactions);
        Assert.True(interaction.IsOriginatingInteraction);
        Assert.Equal("Ticketing", interaction.Source);
        Assert.Null(interaction.GenesysConversationId);
        Assert.Empty(interaction.Messages);
        Assert.Equal("Active", interaction.Status);
    }

    [Fact]
    public async Task ListCustomers_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateCrmBuyerVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync("/api/customers?page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var directory = await response.Content.ReadFromJsonAsync<CustomerDirectoryListResultDto>();
        // The directory is built from persisted tickets, deduplicated by
        // identity: the CRM Buyer this ticket was verified against is one row
        // however many tests created tickets for it (see the shared-database
        // note on GetCrmCustomerTicketHistory_Returns200).
        var row = Assert.Single(directory!.Items, r => r.CustomerKey == $"crm:{ticket.CrmBuyerCustomerId}");
        Assert.Equal("Crm", row.VerificationSource);
        Assert.True(row.TotalTickets >= 1);
        Assert.Equal(ticket.CrmBuyerCustomerName, row.DisplayName);
    }

    [Fact]
    public async Task GetCustomerProfileByKey_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateCrmBuyerVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync($"/api/customers/profile/crm:{ticket.CrmBuyerCustomerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<CustomerDirectoryProfileDto>();
        Assert.Equal($"crm:{ticket.CrmBuyerCustomerId}", profile!.CustomerKey);
        Assert.Contains(profile.Tickets, t => t.TicketId == ticket.TicketId);
        Assert.Contains("+971500000900", profile.PhoneNumbers);

        var unknown = await client.GetAsync("/api/customers/profile/crm:987654321");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var invalid = await client.GetAsync("/api/customers/profile/nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task GetCrmCustomerTicketHistory_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateCrmBuyerVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync($"/api/customers/crm/{ticket.CrmBuyerCustomerId}/ticket-history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<CustomerHistoryDto>();
        // FakeCrmBuyerLookupGateway's fixture always returns the same
        // CrmBuyerCustomerId (9001) regardless of phone searched, and this
        // class shares one database across every test method
        // (IClassFixture<TigerCsApiFactory>) — so another test's own
        // CRM-buyer-verified ticket may already exist here too. This asserts
        // the endpoint works and finds this ticket, not that this test's
        // database is empty beforehand.
        Assert.True(history!.TotalTickets >= 1);
        Assert.Contains(history.Tickets, t => t.TicketId == ticket.TicketId);
    }

    [Fact]
    public async Task CustomerHistory_NeverIssuesALiveCrmCall()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateCrmBuyerVerifiedTicketAsync(client, "Facilities");
        var callsBeforeHistory = _factory.CrmBuyerLookupGateway.CallCount;
        Assert.True(callsBeforeHistory > 0); // sanity: ticket creation really did call CRM once.

        var ticketHistoryResponse = await client.GetAsync($"/api/tickets/{ticket.TicketId}/customer-history");
        var crmCustomerHistoryResponse = await client.GetAsync($"/api/customers/crm/{ticket.CrmBuyerCustomerId}/ticket-history");

        Assert.Equal(HttpStatusCode.OK, ticketHistoryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, crmCustomerHistoryResponse.StatusCode);
        Assert.Equal(callsBeforeHistory, _factory.CrmBuyerLookupGateway.CallCount);
    }

    // ---------------------------------------------------------------
    // Assignment, transfer, status, resolution, closure
    // ---------------------------------------------------------------

    [Fact]
    public async Task AssignTicket_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var (_, _, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, ticket.CurrentDepartmentId);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(workerId, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(workerId, updated!.CurrentOwnerEmployeeId);
    }

    /// <summary>
    /// Take Ownership is the one ticket action the ADR-0024 override does NOT open: it moves a ticket into the Call Center and assigns it to
    /// the CALLER, so the caller must themselves be an active Call Center agent. A System Administrator is not one, and gets 403 with the ticket
    /// untouched - they keep Assign / Transfer, with which they can place the ticket with any Call Center agent.
    /// </summary>
    [Fact]
    public async Task TakeOwnership_IsRefused_BecauseTheCallerMustThemselvesBeACallCenterAgent_AndTheTicketIsUntouched()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var response = await client.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/ownership", new TakeOwnershipRequestDto(RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await _factory.GetTicketAsync(ticket.TicketId);
        Assert.Equal(ticket.CurrentDepartmentId, after!.CurrentDepartmentId);
        Assert.Equal(ticket.CurrentOwnerEmployeeId, after.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task TransferTicket_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");
        var targetDepartmentId = await _factory.CreateDepartmentAsync("Legal " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/transfer",
            new TransferTicketRequestDto(targetDepartmentId, "Misrouted", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal(targetDepartmentId, updated!.CurrentDepartmentId);
    }

    [Fact]
    public async Task ChangeTicketStatus_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var (_, _, workerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(workerId, ticket.CurrentDepartmentId);

        // Open -> InProgress requires an owner (Ticket.ChangeStatus) — a
        // business rule the override does not suspend, so the administrator
        // assigns first, exactly as any other authorized role would have to.
        var assigned = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(workerId, RowVersionOf(ticket))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("InProgress", updated!.TicketStatus);
    }

    [Fact]
    public async Task ResolveAndCloseTicket_BothReturn200()
    {
        // Resolve is Department Employee/Head only and Close is CS-layer only
        // (ISSUE-022) — the administrator holds neither role and performs
        // both here purely through the override.
        var (client, adminEmployeeId) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        await _factory.AssignPrimaryDepartmentAsync(adminEmployeeId, ticket.CurrentDepartmentId);

        var assigned = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(adminEmployeeId, RowVersionOf(ticket))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var inProgress = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var resolveResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, RowVersionOf(inProgress!)));
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Resolved", resolved!.TicketStatus);

        var closeResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(RowVersionOf(resolved)));
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var closed = await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", closed!.TicketStatus);
    }

    [Fact]
    public async Task ReopenTicket_Returns200()
    {
        // Reopen is CS-layer only (ISSUE-022, TicketRoleSets.Reopen) — the
        // administrator performs it purely through the ADR-0024 override.
        var (client, adminEmployeeId) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        await _factory.AssignPrimaryDepartmentAsync(adminEmployeeId, ticket.CurrentDepartmentId);

        var assigned = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(adminEmployeeId, RowVersionOf(ticket))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var inProgress = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var resolved = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, RowVersionOf(inProgress!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        // Approved rule: Reopen is Closed-only, so the administrator closes
        // first — also through the override, since Close is CS-layer too.
        var closed = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(RowVersionOf(resolved!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var reopenResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/reopen",
            new ReopenTicketRequestDto(
                "Customer called back — still not cooling.", ticket.CurrentDepartmentId, RowVersionOf(closed!)));
        Assert.Equal(HttpStatusCode.OK, reopenResponse.StatusCode);
        var reopened = await reopenResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("InProgress", reopened!.TicketStatus);
        Assert.Equal(1, reopened.ReopenCount);
    }

    [Fact]
    public async Task GetTicketLifecycleHistory_Returns200()
    {
        // The lifecycle-history read is department-scoped like the detail read
        // it sits beside, so the administrator reaches it purely through the
        // ADR-0024 override — and it returns the rows the ticket's own
        // lifecycle wrote, which is what Ticket Details renders Activity from.
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        var response = await client.GetAsync($"/api/tickets/{ticket.TicketId}/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<TicketLifecycleHistoryDto>();
        Assert.NotNull(history);
    }

    [Fact]
    public async Task SearchCustomers_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/customers/search?phoneNumber=%2B971509990001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetExternalCustomerHistory_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/customers/external/Pact/PACT-CUST-1/ticket-history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetDashboard_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetDashboardOverview_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/dashboard/overview?dateFrom=2026-08-01&dateTo=2026-09-01");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Notes
    // ---------------------------------------------------------------

    [Fact]
    public async Task AddAndListTicketNotes_Return201And200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var addResponse = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/notes", new CreateNoteRequestDto("Checked the audit trail for this ticket."));
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);

        var listResponse = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}/notes");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    }

    // ---------------------------------------------------------------
    // SLA and Escalation (MVP-API-Contracts.md §5.1/§5.2/§5.7/§5.9)
    //
    // The structural claim ADR-0024 makes is that a policy added later is
    // covered with no change to the override. This whole module is that
    // claim's first real test: not one line of SLA or escalation code names
    // the System Administrator role, and none of the role sets in
    // SlaRoleSets include it — every endpoint below is reached purely
    // through the central mechanism.
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetTicketSla_Returns200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var response = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}/sla");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sla = await response.Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();

        // Not merely "not 403": the summary is real, with the due dates
        // ticket creation computed for it.
        Assert.NotNull(sla!.FirstResponseDueAtUtc);
        Assert.NotNull(sla.ResolutionDueAtUtc);
        Assert.Equal("Running", sla.SlaState);
    }

    [Fact]
    public async Task RecordFirstResponse_Returns200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        var response = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/first-response",
            new RecordFirstResponseRequestDto("Manual", null, RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sla = await response.Content.ReadFromJsonAsync<TicketSlaSummaryResponseDto>();
        Assert.NotNull(sla!.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task EscalateTicketAndListEscalations_Return201And200()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var (adminClient, _) = await CreateAdministratorAsync();

        // Level 4 specifically: MVP-ERD.md §2.17 restricts ManualLevel4 to a
        // CS Manager or GM actor, and the administrator holds neither role.
        // Passing here is the override working on the narrowest gate in the
        // module, not on its most permissive one.
        var escalateResponse = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/escalations",
            new ManualEscalationRequestDto(4, "ManualLevel4", "Executive attention requested.", RowVersionOf(ticket)));

        Assert.Equal(HttpStatusCode.Created, escalateResponse.StatusCode);
        var escalation = await escalateResponse.Content.ReadFromJsonAsync<TicketEscalationResponseDto>();
        Assert.Equal(4, escalation!.Level);

        var listResponse = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}/escalations");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var escalations = await listResponse.Content.ReadFromJsonAsync<List<TicketEscalationResponseDto>>();
        Assert.Single(escalations!);
    }

    // ---------------------------------------------------------------
    // CRM reconciliation
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReconcileUnverifiedTicket_Returns200()
    {
        var (client, _) = await CreateAdministratorAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // Created with no customer match — business-rule change: customer
        // lookup no longer gates creation, so this ticket starts Unverified.
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, true, "1204", (byte)PriorityLevel.Critical)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var unverified = await (await client.PostAsJsonAsync(
                "/api/tickets",
                new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Critical, "Flooding in lobby")))
            .Content.ReadFromJsonAsync<TicketResponseDto>();

        // The CRM comes back: the administrator verifies the same unit
        // ("1204", matching the intake's RawUnitNumberEntered) and reconciles.
        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var session = await (await client.PostAsJsonAsync(
                "/api/verification-sessions",
                new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation")))
            .Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{unverified!.TicketId}/reconciliation",
            new ReconcileTicketRequestDto(session!.VerificationSessionId, Convert.FromBase64String(unverified.RowVersion)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reconciled = await response.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Verified", reconciled!.VerificationStatus);
    }

    // ---------------------------------------------------------------
    // Requirement 2 — the override is authorization only.
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosedTicketImmutability_StillAppliesToTheAdministrator()
    {
        var (client, adminEmployeeId) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");
        await _factory.AssignPrimaryDepartmentAsync(adminEmployeeId, ticket.CurrentDepartmentId);

        var assigned = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(adminEmployeeId, RowVersionOf(ticket))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var inProgress = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", RowVersionOf(assigned!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var resolved = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Done.", null, null, RowVersionOf(inProgress!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var closed = await (await client.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(RowVersionOf(resolved!))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var closedRowVersion = RowVersionOf(closed!);

        // 422, not 403 and not 200: the administrator is authorized and the
        // business rule refuses anyway.
        var reassign = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(adminEmployeeId, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reassign.StatusCode);

        var statusChange = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, statusChange.StatusCode);

        var reResolve = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "n/a", null, null, closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reResolve.StatusCode);

        var reClose = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(closedRowVersion));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reClose.StatusCode);
    }

    [Fact]
    public async Task InvalidStatusTransition_StillRejectedForTheAdministrator()
    {
        var (client, _) = await CreateAdministratorAsync();
        var ticket = await CreateVerifiedTicketAsync(client, "Facilities");

        // Open -> PendingCustomer is not in the transition table, and an
        // unassigned Open ticket cannot go InProgress either. A reason is
        // supplied so this exercises the transition table, not the separate
        // pending-reason-required guard.
        var response = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status",
            new ChangeStatusRequestDto("PendingCustomer", RowVersionOf(ticket), "Awaiting documents"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RequestValidation_StillAppliesToTheAdministrator()
    {
        var (client, _) = await CreateAdministratorAsync();

        var response = await client.GetAsync("/api/crm/units/search?unitNumber=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Optimistic-concurrency control under the override is proven at the
    // application-service level instead, in
    // TicketAssignmentAppServiceTests.AssignAsync_SystemAdministrator_ConcurrentModification_*:
    // this factory's EF Core InMemory provider has no real change tracker to
    // prime, so a stale rowVersion cannot lose a race here (the same
    // InMemory-vs-real-SQL-Server split already documented on
    // FakeTicketRepository.SetRowVersion and TigerCsApiFactory). Asserting a
    // 409 over this host would test the provider, not the override.

    [Fact]
    public async Task AuditEntriesAreStillWrittenForTheAdministratorsActions()
    {
        var (client, adminEmployeeId) = await CreateAdministratorAsync();

        var response = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var intake = await response.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var audit = await db.AuditEntries.FirstOrDefaultAsync(e =>
            e.ActorEmployeeId == adminEmployeeId
            && e.Action == "CreateIntakeRecord"
            && e.EntityId == intake!.IntakeRecordId.ToString());

        Assert.NotNull(audit);
    }

    // ---------------------------------------------------------------
    // Requirement 9 — the one place granting authorization would have
    // conflicted with a business rule. Reported in the PR description.
    // ---------------------------------------------------------------

    /// <summary>
    /// Verification-session single-agent ownership (MVP-ERD.md §2.24) is a
    /// per-record business invariant, not a permission-matrix cell, so the
    /// override deliberately does not reach it: the administrator passes the
    /// CustomerVerification policy and reaches the application service (which
    /// is the correction's requirement), and the service then refuses the
    /// session because it belongs to another agent.
    ///
    /// <para>
    /// This costs the administrator nothing operationally — it can create
    /// its own session and use that end-to-end, as
    /// <see cref="CreateTicket_WithCustomerMatch_Returns201"/> does.
    /// Overriding it instead would let an administrator consume a session an
    /// agent was mid-way through and attribute the resulting requester
    /// snapshot to a verification they never performed, which is an audit
    /// integrity problem, not an access one. Business-rule change: ticket
    /// creation itself no longer consumes a VerificationSession at all (see
    /// TicketCreationAppService.CreateAsync's remarks), so this ownership
    /// rule is now exercised through the two calls that still do: reading a
    /// session directly, and reconciling a ticket against one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnotherAgentsVerificationSession_IsStillRefused_OwnershipIsNotAnAuthorizationPolicy()
    {
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);

        var unit = await (await agentClient.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await agentClient.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        var agentsSession = await (await agentClient.PostAsJsonAsync(
                "/api/verification-sessions",
                new CreateVerificationSessionRequestDto(unit!.UnitReferenceId, contacts![0].ContactReferenceId, true, "ManualAgentConfirmation")))
            .Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var (adminClient, _) = await CreateAdministratorAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await adminClient.GetAsync($"/api/verification-sessions/{agentsSession!.VerificationSessionId}")).StatusCode);

        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);
        var intake = await (await adminClient.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500009999", null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var unverified = await (await adminClient.PostAsJsonAsync(
                "/api/tickets",
                new CreateTicketRequestDto(intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.High, "AC unit not cooling")))
            .Content.ReadFromJsonAsync<TicketResponseDto>();

        var reconcileResponse = await adminClient.PostAsJsonAsync(
            $"/api/tickets/{unverified!.TicketId}/reconciliation",
            new ReconcileTicketRequestDto(agentsSession.VerificationSessionId, Convert.FromBase64String(unverified.RowVersion)));

        Assert.Equal(HttpStatusCode.Forbidden, reconcileResponse.StatusCode);
    }

    [Fact]
    public async Task DeactivatedAdministrator_IsStillRefused()
    {
        // The one authorization-shaped rule the override deliberately does
        // not satisfy: ActiveEmployeeRequirement is an IIdentityGateRequirement
        // (Security-Architecture.md §14, FR-ADM-02). The token below is
        // valid and unexpired; deactivation alone revokes it.
        var (client, employeeId) = await CreateAdministratorAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/roles")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var employee = await db.Employees.SingleAsync(e => e.EmployeeId == employeeId);
            employee.Deactivate(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null))).StatusCode);
    }

    /// <summary>Creates a ticket classified with a request type that requires an Accounting-style approval (targeted at its own department, for test simplicity — the admin decides through the override anyway).</summary>
    private async Task<TicketDetailDto> CreateTicketWithApprovalRequirementAsync(HttpClient client)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(
            "Collections " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Send Receipts", departmentId);
        var requestTypeId = await _factory.CreateRequestTypeAsync(
            "Send Receipts", departmentId, TigerCS.Domain.Modules.WorkflowConfiguration.ApprovalType.AccountingApproval);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990001", null, false, null, null));
        intakeResponse.EnsureSuccessStatusCode();
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "Send the receipt",
                RequestTypeId: requestTypeId));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var created = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();

        var detailResponse = await client.GetAsync($"/api/tickets/{created!.TicketId}");
        detailResponse.EnsureSuccessStatusCode();
        return (await detailResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    [Fact]
    public async Task ApprovalWorkflowEndpoints_AuthorizedThroughTheOverride()
    {
        // Approval request/decision/cancel/event authorization is target- and
        // operational-actor-based; the administrator holds none of those and
        // acts purely through the ADR-0024 override.
        var (client, _) = await CreateAdministratorAsync();

        // Ticket 1: request -> approve -> view (with the recorded cycle/event).
        var ticket = await CreateTicketWithApprovalRequirementAsync(client);

        var requestResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/approvals", new RequestApprovalRequestDto("AccountingApproval", "Please approve"));
        Assert.Equal(HttpStatusCode.OK, requestResponse.StatusCode);
        var approval = await requestResponse.Content.ReadFromJsonAsync<TicketApprovalDto>();

        var decideResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/approvals/{approval!.TicketApprovalId}/decision",
            new DecideApprovalRequestDto("Approve", "Verified"));
        Assert.Equal(HttpStatusCode.OK, decideResponse.StatusCode);

        var eventResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/workflow-events", new RecordWorkflowEventRequestDto("PrerequisitesCompleted"));
        Assert.Equal(HttpStatusCode.NoContent, eventResponse.StatusCode);

        var viewResponse = await client.GetAsync($"/api/tickets/{ticket.TicketId}/approvals");
        Assert.Equal(HttpStatusCode.OK, viewResponse.StatusCode);
        var view = await viewResponse.Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
        Assert.Contains(view!.Approvals, a => a.Status == "Approved");
        Assert.Contains(view.Events, e => e.EventType == "ApprovalReceived");

        // Ticket 2: request -> cancellation.
        var second = await CreateTicketWithApprovalRequirementAsync(client);
        var secondApproval = await (await client.PostAsJsonAsync(
                $"/api/tickets/{second.TicketId}/approvals", new RequestApprovalRequestDto("AccountingApproval")))
            .Content.ReadFromJsonAsync<TicketApprovalDto>();
        var cancelResponse = await client.PostAsJsonAsync(
            $"/api/tickets/{second.TicketId}/approvals/{secondApproval!.TicketApprovalId}/cancellation",
            new CancelApprovalRequestDto("Raised in error"));
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
    }
    [Fact]
    public async Task PriorityDowngradeEndpoints_AuthorizedThroughTheOverride()
    {
        // A downgrade is requested by an agent and decided by someone else.
        // The administrator holds neither Department Head nor any department
        // membership, so approving and rejecting is purely the ADR-0024 override.
        var (agentClient, _) = await CreateClientAsync(Roles.CsAgent);
        var (adminClient, _) = await CreateAdministratorAsync();

        // Ticket 1: request -> list -> pending inbox -> approve.
        var ticket = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var requestResponse = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/sla/priority-downgrade-requests",
            new CreateDowngradeRequestRequestDto((byte)PriorityLevel.Medium, "Customer confirmed it is not urgent."));
        Assert.Equal(HttpStatusCode.Created, requestResponse.StatusCode);
        var pending = await requestResponse.Content.ReadFromJsonAsync<PriorityDowngradeRequestResponseDto>();
        Assert.Equal("Pending", pending!.Status);

        var listResponse = await adminClient.GetAsync($"/api/tickets/{ticket.TicketId}/sla/priority-downgrade-requests");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var inboxResponse = await adminClient.GetAsync("/api/priority-downgrade-requests/pending");
        Assert.Equal(HttpStatusCode.OK, inboxResponse.StatusCode);
        var inbox = await inboxResponse.Content.ReadFromJsonAsync<PendingDowngradePageDto>();
        Assert.Contains(inbox!.Items, i => i.PriorityDowngradeRequestId == pending.PriorityDowngradeRequestId);

        var approveResponse = await adminClient.PostAsJsonAsync(
            $"/api/priority-downgrade-requests/{pending.PriorityDowngradeRequestId}/approve", new ApproveDowngradeRequestRequestDto());
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var decision = await approveResponse.Content.ReadFromJsonAsync<DowngradeDecisionResponseDto>();
        Assert.Equal("Approved", decision!.Request.Status);
        Assert.Equal("Downgrade", decision.NewSlaPeriod!.ChangeReason);

        // Ticket 2: request -> reject.
        var second = await CreateVerifiedTicketAsync(agentClient, "Facilities");
        var secondRequest = await (await agentClient.PostAsJsonAsync(
                $"/api/tickets/{second.TicketId}/sla/priority-downgrade-requests",
                new CreateDowngradeRequestRequestDto((byte)PriorityLevel.Low, "Cosmetic issue only.")))
            .Content.ReadFromJsonAsync<PriorityDowngradeRequestResponseDto>();
        var rejectResponse = await adminClient.PostAsJsonAsync(
            $"/api/priority-downgrade-requests/{secondRequest!.PriorityDowngradeRequestId}/reject",
            new RejectDowngradeRequestRequestDto("Still impacts tenants."));
        Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
    }
}
