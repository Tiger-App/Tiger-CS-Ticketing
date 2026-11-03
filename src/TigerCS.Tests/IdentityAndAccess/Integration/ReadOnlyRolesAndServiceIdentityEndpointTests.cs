using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// The agreed role model enforced end to end through the real HTTP pipeline:
/// <list type="bullet">
///   <item><description>Chairman/CEO (and Reporting User) are READ-ONLY - they
///   read tickets, the dashboard and reports, and every mutation is refused.</description></item>
///   <item><description>A configured service identity (the Genesys integration
///   account, a CS Agent by permission set) reaches only the integration
///   surface and none of the human endpoints.</description></item>
///   <item><description>Department scope: a Department Employee/Head is refused on another
///   department's ticket (403 read and write; 404 only when the ticket does not exist).</description></item>
/// </list>
/// </summary>
public class ReadOnlyRolesAndServiceIdentityEndpointTests : IClassFixture<ReadOnlyRolesAndServiceIdentityEndpointTests.Host>
{
    public sealed class TestServiceIdentityRegistry : IServiceIdentityRegistry
    {
        public HashSet<Guid> Ids { get; } = [];

        public IReadOnlyCollection<Guid> ServiceIdentityIds => Ids;

        public bool IsServiceIdentity(Guid employeeId) => Ids.Contains(employeeId);
    }

    /// <summary>One shared host for the class (a host per test exhausts the machine's inotify instances); every test seeds its own employees.</summary>
    public sealed class Host : IDisposable
    {
        public TestServiceIdentityRegistry Registry { get; } = new();

        public TigerCsApiFactory Factory { get; }

        public Host() => Factory = new TigerCsApiFactory { ExtraServices = s => s.AddSingleton<IServiceIdentityRegistry>(Registry) };

        public void Dispose() => Factory.Dispose();
    }

    private readonly TestServiceIdentityRegistry _registry;
    private readonly TigerCsApiFactory _factory;

    public ReadOnlyRolesAndServiceIdentityEndpointTests(Host host)
    {
        _registry = host.Registry;
        _factory = host.Factory;
    }

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientForAsync(string role, int? departmentId = null)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        if (departmentId is { } department)
        {
            await _factory.AssignPrimaryDepartmentAsync(employeeId, department);
        }

        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private async Task<TicketDetailDto> CreateTicketAsync(HttpClient? creator = null)
    {
        var client = creator ?? (await ClientForAsync(Roles.CsAgent)).Client;

        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

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

        return (await (await client.GetAsync($"/api/tickets/{created.TicketId}")).Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static readonly object EmptyBody = new { };

    /// <summary>Every ticket mutation the API exposes, as (method, path suffix). Bodies are irrelevant: the read-only guard refuses before binding.</summary>
    public static IEnumerable<object[]> TicketMutations() =>
    [
        ["POST", "classification"],
        ["POST", "assignment"],
        ["POST", "transfer"],
        ["POST", "status"],
        ["POST", "resolution"],
        ["POST", "close"],
        ["POST", "reopen"],
        ["POST", "notes"],
        ["POST", "sla/first-response"],
        ["POST", "escalations"],
        ["POST", "workflow-events"],
        ["POST", "approvals/1/decision"],
        ["POST", "approvals/1/cancellation"],
        ["POST", "reconciliation"],
    ];

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url) =>
        client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(EmptyBody) });

    // ------------------------------------------------------------------
    // Chairman/CEO and Reporting User - read-only
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task ReadOnlyRoles_RefuseEveryTicketMutation_With403(string role)
    {
        var ticket = await CreateTicketAsync();
        var (client, _) = await ClientForAsync(role);

        foreach (var row in TicketMutations())
        {
            var (method, suffix) = ((string)row[0], (string)row[1]);
            var response = await SendAsync(client, method, $"/api/tickets/{ticket.TicketId}/{suffix}");
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{role} {method} /{suffix} returned {(int)response.StatusCode}, expected 403.");
        }

        // Nothing changed.
        var after = await _factory.GetTicketAsync(ticket.TicketId);
        Assert.Equal(ticket.TicketStatus, after!.TicketStatus.ToString());
        // (The ticket's owner is whoever the helper assigned it to - the creating agent - and stays so: no refused call reassigned it.)
        Assert.Equal(ticket.CurrentOwnerEmployeeId, after.CurrentOwnerEmployeeId);
    }

    [Theory]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task ReadOnlyRoles_RefuseOtherWriteEndpoints(string role)
    {
        var (client, _) = await ClientForAsync(role);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "POST", "/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "POST", "/api/pending-customer-interactions/1/start")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "POST", "/api/pending-customer-interactions/1/complete")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "POST", "/api/pending-customer-interactions/1/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "POST", "/api/collections/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "PATCH", $"/api/users/{Guid.NewGuid()}/activation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "POST", "/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Chairman_ReadsTickets_Dashboard_Reports_AndNotes()
    {
        var ticket = await CreateTicketAsync();
        var (client, _) = await ClientForAsync(Roles.ChairmanCeo);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{ticket.TicketId}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{ticket.TicketId}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{ticket.TicketId}/approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dashboard/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/team-performance")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pending-customer-interactions")).StatusCode);
    }

    [Fact]
    public async Task Chairman_MayStillRequestAReopenApproval_TheOnlyDocumentedWrite_ButNoOtherApprovalType()
    {
        var ticket = await CreateTicketAsync();
        var (client, _) = await ClientForAsync(Roles.ChairmanCeo);

        // Solution-Analysis.md 4.1: Chairman/CEO may REQUEST a Reopen Approval. It passes the
        // read-only guard and the requester rule; this open ticket is simply not configured for it (not 403).
        var reopen = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/approvals", new RequestApprovalRequestDto("ReopenApproval", "Customer called again"));
        Assert.NotEqual(HttpStatusCode.Forbidden, reopen.StatusCode);

        // Any other approval type is an operational action the read-only role does not hold.
        var other = await client.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/approvals", new RequestApprovalRequestDto("AccountingApproval", null));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task AssigneeDirectory_IsRefusedToChairman_AndGeneralManager_IsStillAdmittedToIt()
    {
        var (chairman, _) = await ClientForAsync(Roles.ChairmanCeo);
        var (manager, _) = await ClientForAsync(Roles.CsManager);
        var (generalManager, _) = await ClientForAsync(Roles.GeneralManager);

        Assert.Equal(HttpStatusCode.Forbidden, (await chairman.GetAsync("/api/users/assignable")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/users/assignable")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await generalManager.GetAsync("/api/users/assignable")).StatusCode);
    }

    [Fact]
    public async Task ReportingUser_IsRefusedTheReports_ButChairmanIsNot()
    {
        var (reporting, _) = await ClientForAsync(Roles.ReportingUser);
        var (chairman, _) = await ClientForAsync(Roles.ChairmanCeo);

        Assert.Equal(HttpStatusCode.Forbidden, (await reporting.GetAsync("/api/reports/team-performance")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await chairman.GetAsync("/api/reports/team-performance")).StatusCode);
    }

    [Fact]
    public async Task ReadOnlyRole_HoldingAWorkingRoleToo_KeepsTheWorkingRolesAuthority()
    {
        // A user may hold several roles: Chairman + CS Manager is not "read-only only".
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.ChairmanCeo);
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<TigerCS.Infrastructure.Identity.ApplicationUser>>();
            var user = await userManager.FindByIdAsync(employeeId.ToString());
            await userManager.AddToRoleAsync(user!, Roles.CsManager);
        }

        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/assignable")).StatusCode);
    }

    // ------------------------------------------------------------------
    // System Administrator override still applies
    // ------------------------------------------------------------------

    [Fact]
    public async Task SystemAdministrator_StillPassesTheGuards_ButNotTheActiveAccountGate()
    {
        var ticket = await CreateTicketAsync();
        var (admin, adminId) = await ClientForAsync(Roles.SystemAdministrator);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports/team-performance")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users/assignable")).StatusCode);
        var note = await admin.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/notes", new CreateNoteRequestDto("Admin note"));
        Assert.Equal(HttpStatusCode.Created, note.StatusCode);

        // Deactivation is the one thing the override never bypasses.
        await _factory.DeactivateEmployeeAsync(adminId);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/reports/team-performance")).StatusCode);
    }

    // ------------------------------------------------------------------
    // Service identity (Genesys integration account)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ServiceIdentity_ReachesOnlyTheIntegrationSurface()
    {
        var ticket = await CreateTicketAsync();
        var (service, serviceId) = await ClientForAsync(Roles.CsAgent);
        var (humanAgent, _) = await ClientForAsync(Roles.CsAgent);
        _registry.Ids.Add(serviceId);

        // Human-only reads: refused for the service account, open to a human CS Agent.
        foreach (var url in new[]
                 {
                     $"/api/tickets/{ticket.TicketId}/notes",
                     "/api/dashboard/overview",
                     "/api/reports/team-performance",
                     "/api/users/assignable",
                     "/api/pending-customer-interactions",
                     "/api/roles",
                     "/api/departments",
                     "/api/customers",
                     "/api/admin/sla/configuration"
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await humanAgent.GetAsync("/api/tickets")).StatusCode);

        // Human-only mutations: refused (the service account holds CS Agent, which would otherwise allow close/reopen/notes).
        foreach (var row in TicketMutations())
        {
            var (method, suffix) = ((string)row[0], (string)row[1]);
            if (suffix == "reconciliation")
            {
                continue; // an integration route (CustomerVerification), covered below
            }

            var response = await SendAsync(service, method, $"/api/tickets/{ticket.TicketId}/{suffix}");
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"service identity {method} /{suffix} returned {(int)response.StatusCode}, expected 403.");
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(service, "POST", "/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task ServiceIdentity_SeesAllItsGenesysTickets_AcrossDepartments_AndNoOthers_WhileCsManagerAndAdminSeeEverything()
    {
        var (service, serviceId) = await ClientForAsync(Roles.CsAgent);
        _registry.Ids.Add(serviceId);
        var genesysA = await CreateTicketAsync(service);   // each call opens its own department, so these are cross-department
        var genesysB = await CreateTicketAsync(service);
        var manual = await CreateTicketAsync();            // created by a human agent in a third department
        Assert.NotEqual(genesysA.CurrentDepartmentId, genesysB.CurrentDepartmentId);

        async Task<long[]> ListAsync(HttpClient client) =>
            [.. (await client.GetFromJsonAsync<TicketListResultDto>("/api/tickets?pageSize=200"))!.Items.Select(i => i.TicketId).Order()];

        // genesys.integration: its own tickets in every department, never the human-created one - list, search and detail alike.
        Assert.Equal(new[] { genesysA.TicketId, genesysB.TicketId }.Order(), await ListAsync(service));
        Assert.Equal(HttpStatusCode.OK, (await service.GetAsync($"/api/tickets/{genesysB.TicketId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync($"/api/tickets/{manual.TicketId}")).StatusCode);
        var searched = await service.GetFromJsonAsync<TicketListResultDto>(
            $"/api/tickets?departmentId={manual.CurrentDepartmentId}");
        Assert.Empty(searched!.Items);

        // Visibility is not modification: it still cannot work a ticket it can see.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(service, "POST", $"/api/tickets/{genesysA.TicketId}/close")).StatusCode);

        // CS Manager and System Administrator: everything, from no department at all.
        var everything = new[] { genesysA.TicketId, genesysB.TicketId, manual.TicketId };
        foreach (var role in new[] { Roles.CsManager, Roles.SystemAdministrator })
        {
            var (client, _) = await ClientForAsync(role);
            var listed = await ListAsync(client);
            Assert.All(everything, id => Assert.Contains(id, listed));
            foreach (var id in everything)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{id}")).StatusCode);
            }
        }
    }

    [Fact]
    public async Task ServiceIdentity_StillReaches_TheCustomerVerificationRoutes_AndItsOwnProfile()
    {
        var (service, serviceId) = await ClientForAsync(Roles.CsAgent);
        _registry.Ids.Add(serviceId);

        Assert.Equal(HttpStatusCode.OK, (await service.GetAsync("/api/users/me")).StatusCode);

        var intake = await service.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990001", null, true, "5001", null));
        Assert.Equal(HttpStatusCode.Created, intake.StatusCode);

        var session = await service.PostAsJsonAsync(
            "/api/verification-sessions", new CreateVerificationSessionRequestDto(1, 1, true, "ManualAgentConfirmation"));
        Assert.NotEqual(HttpStatusCode.Forbidden, session.StatusCode);

        // api/genesys/* sits behind CustomerVerification - authorization passes (the body is then judged on its merits).
        var genesys = await service.PostAsJsonAsync("/api/genesys/tickets", new { });
        Assert.NotEqual(HttpStatusCode.Forbidden, genesys.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, genesys.StatusCode);

        // Genesys Collections is explicitly marked as an integration route.
        var collections = await service.GetAsync("/api/genesys/collections/customers/1/outstanding");
        Assert.NotEqual(HttpStatusCode.Unauthorized, collections.StatusCode);
        Assert.True(
            collections.StatusCode != HttpStatusCode.Forbidden || await IsDomainForbiddenAsync(collections),
            "The service identity must pass the endpoint restriction on api/genesys/collections.");
    }

    /// <summary>The Collections feature answers its own 403/503 from the application layer; the endpoint restriction answers an empty 403.</summary>
    private static async Task<bool> IsDomainForbiddenAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync()).Length > 0;

    [Fact]
    public async Task ServiceIdentity_ConfigurationDoesNotAffectAHumanWithTheSameRole()
    {
        var (human, _) = await ClientForAsync(Roles.CsAgent);
        var (service, serviceId) = await ClientForAsync(Roles.CsAgent);
        _registry.Ids.Add(serviceId);

        Assert.Equal(HttpStatusCode.OK, (await human.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await service.GetAsync("/api/tickets")).StatusCode); // list/detail are visibility-scoped reads
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync("/api/dashboard/overview")).StatusCode);
    }

    [Fact]
    public async Task ServiceIdentity_ExcludedFromTeamPerformance_AndTheAssigneeDirectory()
    {
        var (_, serviceId) = await ClientForAsync(Roles.CsAgent);
        var (_, humanId) = await ClientForAsync(Roles.CsAgent);
        var departmentId = await _factory.CreateDepartmentAsync("Call Center " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.AssignPrimaryDepartmentAsync(serviceId, departmentId);
        await _factory.AssignPrimaryDepartmentAsync(humanId, departmentId);
        _registry.Ids.Add(serviceId);
        var (manager, _) = await ClientForAsync(Roles.CsManager);

        var report = await manager.GetFromJsonAsync<TigerCS.Application.Modules.Reporting.Dto.TeamPerformanceReportDto>(
            "/api/reports/team-performance");
        Assert.Contains(report!.Rows, r => r.EmployeeId == humanId);
        Assert.DoesNotContain(report.Rows, r => r.EmployeeId == serviceId);

        var assignable = await manager.GetFromJsonAsync<List<AssignableUserDto>>("/api/users/assignable");
        Assert.Contains(assignable!, u => u.EmployeeId == humanId);
        Assert.DoesNotContain(assignable!, u => u.EmployeeId == serviceId);
    }

    // ------------------------------------------------------------------
    // Department scope - Department Employee / Head
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    public async Task DepartmentRoles_AreRefusedOnAnotherDepartmentsTicket_ForReadsAndWrites(string role)
    {
        var ticket = await CreateTicketAsync();
        var otherDepartmentId = await _factory.CreateDepartmentAsync("Other " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (outsider, _) = await ClientForAsync(role, otherDepartmentId);

        // Existing convention: cross-department is 403 (the ticket exists, the caller may not see it)...
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/tickets/{ticket.TicketId}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/notes", new CreateNoteRequestDto("sneaky"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(ticket.RowVersion)))).StatusCode);

        // ...and 404 is reserved for a ticket that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/tickets/987654321")).StatusCode);
    }

    [Fact]
    public async Task DepartmentEmployee_InTheTicketsDepartment_CanReadAndNote()
    {
        var ticket = await CreateTicketAsync();
        var (member, _) = await ClientForAsync(Roles.DepartmentEmployee, ticket.CurrentDepartmentId);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/tickets/{ticket.TicketId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await member.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/notes", new CreateNoteRequestDto("On it"))).StatusCode);
    }
}
