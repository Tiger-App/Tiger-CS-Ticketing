using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Requirement 7 of the authorization correction (ADR-0024): the override is
/// scoped to one role, and every other role's denials are unchanged by it.
///
/// <para>
/// This is the negative half of
/// <c>SystemAdministratorEndpointAuthorizationTests</c> — deliberately the
/// same endpoint list, walked with a Reporting User token (the role
/// Solution-Analysis.md §4.1 restricts to reports/dashboards, with no ticket
/// actions at all) and, for the operational actions, a Department Employee
/// token as well. Every case below returned 403 before the correction and
/// must still return 403 after it.
/// </para>
///
/// <para>
/// Endpoints carrying no role requirement (the fallback
/// authenticated-staff policy — <c>GET /api/users/me</c>,
/// <c>GET /api/departments/{id}/users</c>, <c>GET /api/tickets</c>) are
/// covered separately at the bottom: "still denied where applicable" means
/// where a denial was ever the correct answer, and asserting 403 on an
/// endpoint that never denied Reporting User would encode the wrong rule.
/// </para>
/// </summary>
public class UnauthorizedRolesStillDeniedTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public UnauthorizedRolesStillDeniedTests(TigerCsApiFactory factory) => _factory = factory;

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

    /// <summary>Creates a real verified ticket as a CS Agent, so the denials below are about the caller's role and not a missing fixture.</summary>
    private async Task<TicketDetailDto> CreateVerifiedTicketAsync()
    {
        var (client, _) = await CreateClientAsync(Roles.CsAgent);

        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        // "+971509990001" matches MockCrmGateway's CRM-UNIT-1107/CRM-CONTACT-3010 fixture (Sami Nasser, an Owner — a valid Buyer ownership record).
        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990001", null, true, "5001", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var lookup = await (await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup"))
            .Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crmCustomer = Assert.Single(lookup!.Sources.Single(s => s.Source == "Crm").Customers);
        var crmUnit = Assert.Single(crmCustomer.Units);
        var created = await (await client.PostAsJsonAsync(
                "/api/tickets",
                new CreateTicketRequestDto(
                    intake.IntakeRecordId, crmUnit.UnitReferenceId, crmUnit.ContactReferenceId, categoryId, (byte)PriorityLevel.High, "AC unit not cooling")))
            .Content.ReadFromJsonAsync<TicketResponseDto>();
        // The creating CS Agent takes the ticket (assigned to them): a CS Agent sees only tickets currently assigned to them.
        await _factory.AssignOwnerAsync(created!.TicketId, TigerCsApiFactory.UserIdOf(client));

        var detail = await (await client.GetAsync($"/api/tickets/{created.TicketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        return detail!;
    }

    // ---------------------------------------------------------------
    // Administration endpoints — System Administrator policy.
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(Roles.ReportingUser)]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task GetRoleCatalog_NonAdministratorRoles_Return403(string role)
    {
        var (client, _) = await CreateClientAsync(role);

        var response = await client.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(Roles.ReportingUser)]
    [InlineData(Roles.CsManager)]
    public async Task SetUserActivation_NonAdministratorRoles_Return403(string role)
    {
        var (client, _) = await CreateClientAsync(role);
        var (_, _, targetEmployeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);

        var response = await client.PatchAsJsonAsync(
            $"/api/users/{targetEmployeeId}/activation", new ActivationRequestDto(false, "n/a"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // CRM verification surface — CustomerVerification policy.
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(Roles.ReportingUser)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task CustomerVerificationSurface_UnauthorizedRoles_AllReturn403(string role)
    {
        var (client, _) = await CreateClientAsync(role);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/units/CRM-UNIT-1001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/units/search?unitNumber=1204")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(1, 1, true, "ManualAgentConfirmation"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/intake-records/1/customer-lookup")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(1, 1, 1, 1, (byte)PriorityLevel.High, "n/a"))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            "/api/tickets/1/reconciliation",
            new ReconcileTicketRequestDto(Guid.NewGuid(), []))).StatusCode);
    }

    // ---------------------------------------------------------------
    // Ticket operations — application-layer authorization.
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketOperations_ReportingUser_AllReturn403()
    {
        var ticket = await CreateVerifiedTicketAsync();
        var (client, employeeId) = await CreateClientAsync(Roles.ReportingUser);

        // Reporting User is not in CrossDepartmentView and belongs to no
        // department, so detail/notes are invisible to it as well.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/tickets/{ticket.TicketId}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/notes", new CreateNoteRequestDto("n/a"))).StatusCode);

        var rowVersion = Convert.FromBase64String(ticket.RowVersion);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(employeeId, rowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/transfer", new TransferTicketRequestDto(ticket.CurrentDepartmentId + 1, "n/a", rowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", rowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "n/a", null, null, rowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(rowVersion))).StatusCode);
    }

    [Fact]
    public async Task TicketOperations_DepartmentEmployeeInTheTicketsOwnDepartment_StillDeniedTheActionsTheMatrixWithholds()
    {
        // A Department Employee who genuinely belongs to the ticket's
        // department: proves the remaining denials are the permission
        // matrix's per-action splits (ISSUE-022), not a department-scoping
        // side effect — and that the override left them alone.
        var ticket = await CreateVerifiedTicketAsync();
        var (client, employeeId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(employeeId, ticket.CurrentDepartmentId);

        // Department membership alone shows an employee nothing: the ticket is not theirs until it is assigned to them.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);
        await _factory.AssignOwnerAsync(ticket.TicketId, employeeId);
        var detail = await client.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{ticket.TicketId}");
        var rowVersion = Convert.FromBase64String(detail!.RowVersion);

        // Visible (assigned to them) — but no assignment capability at all,
        // not even self-claim, and never Transfer or Close.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment", new AssignTicketRequestDto(employeeId, rowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/transfer", new TransferTicketRequestDto(ticket.CurrentDepartmentId + 1, "n/a", rowVersion))).StatusCode);
        // Close: only the ticket's current owner may close it (the existing ownership rule), and only once it is resolved - an unresolved
        // ticket is a 409, never a role-based close for the employee.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(rowVersion))).StatusCode);
    }

    [Fact]
    public async Task TicketDetail_CsSupervisorFromAnotherDepartment_IsStillDepartmentScopedForDepartmentRoles()
    {
        // The department-scoping boundary itself is unchanged: a Department
        // Head outside the ticket's department is still refused, while the
        // CS layer's documented cross-department reach still works.
        var ticket = await CreateVerifiedTicketAsync();

        var (outsiderClient, outsiderId) = await CreateClientAsync(Roles.DepartmentHead);
        var otherDepartmentId = await _factory.CreateDepartmentAsync("Unrelated " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.AssignPrimaryDepartmentAsync(outsiderId, otherDepartmentId);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);

        var (supervisorClient, _) = await CreateClientAsync(Roles.CsSupervisor);
        Assert.Equal(HttpStatusCode.OK, (await supervisorClient.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);
    }

    // ---------------------------------------------------------------
    // Endpoints that never denied these roles — asserted as-is, so a
    // future change to them is visible rather than silent.
    // ---------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedStaffEndpoints_ReportingUser_RemainAllowed()
    {
        var (client, _) = await CreateClientAsync(Roles.ReportingUser);
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/departments/{departmentId}/users")).StatusCode);

        // The queue endpoint itself is open to any authenticated staff
        // member; what Reporting User actually sees through it is scoped to
        // its (empty) department membership, which is the real control.
        var queueResponse = await client.GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
        var queue = await queueResponse.Content.ReadFromJsonAsync<TicketListResultDto>();
        Assert.Empty(queue!.Items);
    }

    [Fact]
    public async Task EveryProtectedEndpoint_WithoutAToken_Returns401()
    {
        // The override changed nothing about authentication: an anonymous
        // caller is still rejected before any policy runs.
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/crm/units/CRM-UNIT-1001")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null))).StatusCode);
    }
}
