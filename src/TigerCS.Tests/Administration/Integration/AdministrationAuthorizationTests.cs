using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Administration.Integration;

/// <summary>
/// Backend authorization is mandatory: every administration endpoint
/// refuses every role except System Administrator with 403 — users,
/// departments, request types, workflows and workflow publication alike.
/// </summary>
public class AdministrationAuthorizationTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public AdministrationAuthorizationTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateClientAsync(string role)
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    public static IEnumerable<object[]> NonAdministratorRoles() =>
        Roles.All.Where(r => r != Roles.SystemAdministrator).Select(r => new object[] { r });

    private static readonly (string Method, string Path)[] AdminEndpoints =
    [
        ("GET", "/api/admin/users"),
        ("GET", "/api/admin/users/password-policy"),
        ("POST", $"/api/admin/users/{Guid.NewGuid()}/unlock"),
        ("GET", $"/api/admin/users/{Guid.NewGuid()}"),
        ("POST", "/api/admin/users"),
        ("PUT", $"/api/admin/users/{Guid.NewGuid()}/profile"),
        ("PATCH", $"/api/admin/users/{Guid.NewGuid()}/activation"),
        ("PUT", $"/api/admin/users/{Guid.NewGuid()}/roles"),
        ("POST", $"/api/admin/users/{Guid.NewGuid()}/departments"),
        ("DELETE", $"/api/admin/users/{Guid.NewGuid()}/departments/1"),
        ("GET", "/api/admin/departments"),
        ("GET", "/api/admin/departments/1"),
        ("GET", "/api/admin/departments/coverage"),
        ("POST", "/api/admin/departments"),
        ("PUT", "/api/admin/departments/1"),
        ("PATCH", "/api/admin/departments/1/activation"),
        ("POST", "/api/admin/departments/1/members"),
        ("DELETE", $"/api/admin/departments/1/members/{Guid.NewGuid()}"),
        ("GET", "/api/admin/request-types"),
        ("GET", "/api/admin/request-types/1"),
        ("POST", "/api/admin/request-types"),
        ("PUT", "/api/admin/request-types/1"),
        ("PATCH", "/api/admin/request-types/1/activation"),
        ("PUT", "/api/admin/request-types/1/assignment-rule"),
        ("PUT", "/api/admin/request-types/1/approval-requirements/AccountingApproval"),
        ("PUT", "/api/admin/request-types/1/sla-policies/3"),
        ("GET", "/api/admin/workflows"),
        ("GET", "/api/admin/workflows/catalog"),
        ("GET", "/api/admin/workflows/1"),
        ("POST", "/api/admin/workflows"),
        ("PUT", "/api/admin/workflows/1"),
        ("PATCH", "/api/admin/workflows/1/activation"),
        ("POST", "/api/admin/workflows/1/versions"),
        ("GET", "/api/admin/workflows/versions/1"),
        ("PUT", "/api/admin/workflows/versions/1/settings"),
        ("POST", "/api/admin/workflows/versions/1/steps"),
        ("PUT", "/api/admin/workflows/versions/1/steps/1"),
        ("DELETE", "/api/admin/workflows/versions/1/steps/1"),
        ("POST", "/api/admin/workflows/versions/1/steps/1/move"),
        ("PUT", "/api/admin/workflows/versions/1/steps/1/transitions"),
        ("POST", "/api/admin/workflows/versions/1/publish"),
        ("DELETE", "/api/admin/workflows/versions/1"),
        ("GET", "/api/admin/channels"),
        ("GET", "/api/admin/channels/1"),
        ("POST", "/api/admin/channels"),
        ("PUT", "/api/admin/channels/1"),
        ("PATCH", "/api/admin/channels/1/activation")
    ];

    [Theory]
    [MemberData(nameof(NonAdministratorRoles))]
    public async Task EveryAdministrationEndpoint_RefusesEveryOtherRole(string role)
    {
        var client = await CreateClientAsync(role);

        foreach (var (method, path) in AdminEndpoints)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT" or "PATCH")
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await client.SendAsync(request);
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{role} got {(int)response.StatusCode} for {method} {path}; expected 403.");
        }
    }

    [Fact]
    public async Task AdministrationEndpoints_RefuseAnonymousCallers()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/admin/workflows/versions/1/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/request-types")).StatusCode);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.ReportingUser)]
    public async Task RequestTypeDirectory_IsReadableByAnyActiveStaff_ButNeverWritable(string role)
    {
        var client = await CreateClientAsync(role);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/request-types?departmentId=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/request-types", new { })).StatusCode);
    }

    [Fact]
    public async Task ExistingTicketPermissions_AreNotWidened_ByTheAdministrationPhase()
    {
        // A CS Manager could not manage users before and still cannot; a
        // Department Employee still cannot create tickets. Unchanged matrix.
        var manager = await CreateClientAsync(Roles.CsManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/admin/users")).StatusCode);

        var employee = await CreateClientAsync(Roles.DepartmentEmployee);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync("/api/intake-records", new { })).StatusCode);
    }
}
