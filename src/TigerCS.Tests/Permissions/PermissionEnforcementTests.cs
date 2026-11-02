using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using P = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Permissions;

/// <summary>
/// Backend enforcement of per-employee permission overrides, through the real
/// pipeline (JWT sign-in, policies, handlers): a denial refuses even where the
/// role policy would admit, a grant admits through a role-gated policy, an
/// administrator-only grant never works, permission management itself is
/// administrator-only, and no controller action escapes the permission map.
/// </summary>
public class PermissionEnforcementTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public PermissionEnforcementTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> SignInAsync(string role)
    {
        var (username, password, id) = await _factory.SeedEmployeeAsync(role);
        return (await LoginAsync(username, password), id);
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private async Task SetOverrideDirectlyAsync(Guid employeeId, string key, PermissionEffect effect)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.UserPermissionOverrides.Add(new UserPermissionOverride(employeeId, key, effect, null, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Without_overrides_existing_role_access_is_unchanged()
    {
        var (manager, _) = await SignInAsync(Roles.CsManager);
        var (agent, _) = await SignInAsync(Roles.CsAgent);

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/reports/team-performance")).StatusCode);
    }

    [Fact]
    public async Task A_denial_refuses_an_endpoint_the_role_policy_admits_for_that_employee_only()
    {
        var (denied, deniedId) = await SignInAsync(Roles.CsManager);
        var (other, _) = await SignInAsync(Roles.CsManager);
        await SetOverrideDirectlyAsync(deniedId, P.DashboardView, PermissionEffect.Deny);

        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task A_denial_beats_the_system_administrator_override()
    {
        var (admin, adminId) = await SignInAsync(Roles.SystemAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/users")).StatusCode);

        await SetOverrideDirectlyAsync(adminId, P.AdminUsersView, PermissionEffect.Deny);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/users")).StatusCode);
        // Only that permission is revoked.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/departments")).StatusCode);
    }

    [Fact]
    public async Task A_grant_admits_the_employee_through_a_role_gated_policy()
    {
        var (agent, agentId) = await SignInAsync(Roles.CsAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/reports/team-performance")).StatusCode);

        await SetOverrideDirectlyAsync(agentId, P.ReportsView, PermissionEffect.Grant);

        var status = (await agent.GetAsync("/api/reports/team-performance")).StatusCode;
        Assert.NotEqual(HttpStatusCode.Forbidden, status);
        Assert.NotEqual(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_grant_never_lifts_the_read_only_write_guard()
    {
        var (ceo, ceoId) = await SignInAsync(Roles.ChairmanCeo);
        await SetOverrideDirectlyAsync(ceoId, P.TicketsEdit, PermissionEffect.Grant);

        var response = await ceo.PostAsJsonAsync("/api/tickets/1/notes", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_administrator_only_grant_inserted_behind_the_apis_back_is_ignored()
    {
        var (agent, agentId) = await SignInAsync(Roles.CsAgent);
        foreach (var key in new[] { P.AdminUsersEdit, P.AdminUsersCreate, P.AdminPermissionsManage, P.AdminRolesAssign })
        {
            await SetOverrideDirectlyAsync(agentId, key, PermissionEffect.Grant);
        }

        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync($"/api/admin/users/{agentId}/roles", new SetUserRolesRequestDto([Roles.SystemAdministrator]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync($"/api/admin/users/{agentId}/password", new ResetUserPasswordRequestDto("Whatever-1!", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"/api/admin/users/{agentId}/permissions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync($"/api/admin/users/{agentId}/permissions", new SetUserPermissionsRequestDto(new Dictionary<string, string>()))).StatusCode);
    }

    [Fact]
    public async Task Permission_management_is_refused_to_every_non_administrator_and_to_anonymous_callers()
    {
        var target = Guid.NewGuid();
        foreach (var role in Roles.All.Where(r => r != Roles.SystemAdministrator))
        {
            var (client, _) = await SignInAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/users/{target}/permissions")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PutAsJsonAsync($"/api/admin/users/{target}/permissions", new SetUserPermissionsRequestDto(new Dictionary<string, string>()))).StatusCode);
        }

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/admin/users/{target}/permissions")).StatusCode);
    }

    [Fact]
    public async Task An_administrator_denied_manage_permissions_is_refused_on_the_permission_endpoints()
    {
        var (admin, adminId) = await SignInAsync(Roles.SystemAdministrator);
        var (_, targetId) = await SignInAsync(Roles.CsAgent);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/admin/users/{targetId}/permissions")).StatusCode);

        await SetOverrideDirectlyAsync(adminId, P.AdminPermissionsManage, PermissionEffect.Deny);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/admin/users/{targetId}/permissions")).StatusCode);
    }

    [Fact]
    public async Task Saving_permissions_through_the_api_takes_effect_on_the_next_request_and_is_audited()
    {
        var (admin, adminId) = await SignInAsync(Roles.SystemAdministrator);
        var (manager, managerId) = await SignInAsync(Roles.CsManager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/dashboard")).StatusCode);

        var save = await admin.PutAsJsonAsync($"/api/admin/users/{managerId}/permissions",
            new SetUserPermissionsRequestDto(new Dictionary<string, string> { [P.DashboardView] = "Deny" }, "audit drill"));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var dto = await save.Content.ReadFromJsonAsync<UserPermissionsDto>();
        Assert.Contains(dto!.Modules.SelectMany(m => m.Permissions), p => p.Key == P.DashboardView && p.Override == "Deny" && !p.IsEffective);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/dashboard")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var entry = await db.AuditEntries.SingleAsync(a => a.Action == "AdminSetUserPermission" && a.EntityId == managerId.ToString());
        Assert.Equal(adminId, entry.ActorEmployeeId);
        Assert.Contains("Override=Inherit", entry.BeforeValue);
        Assert.Contains("Override=Deny", entry.AfterValue);
        Assert.Contains("Reason=audit drill", entry.AfterValue);
        Assert.True(entry.OccurredAtUtc > DateTime.UtcNow.AddMinutes(-5));

        // Reverting restores access.
        var revert = await admin.PutAsJsonAsync($"/api/admin/users/{managerId}/permissions",
            new SetUserPermissionsRequestDto(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task The_api_refuses_self_edit_and_administrator_only_grants_with_403()
    {
        var (admin, adminId) = await SignInAsync(Roles.SystemAdministrator);
        var (_, managerId) = await SignInAsync(Roles.CsManager);

        var self = await admin.PutAsJsonAsync($"/api/admin/users/{adminId}/permissions",
            new SetUserPermissionsRequestDto(new Dictionary<string, string> { [P.TicketsView] = "Deny" }));
        var escalate = await admin.PutAsJsonAsync($"/api/admin/users/{managerId}/permissions",
            new SetUserPermissionsRequestDto(new Dictionary<string, string> { [P.AdminPermissionsManage] = "Grant" }));
        var unknown = await admin.PutAsJsonAsync($"/api/admin/users/{managerId}/permissions",
            new SetUserPermissionsRequestDto(new Dictionary<string, string> { ["Nope.Nope"] = "Grant" }));

        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    private sealed class MutableRegistry : TigerCS.Application.Authorization.IServiceIdentityRegistry
    {
        public HashSet<Guid> Ids { get; } = [];
        public IReadOnlyCollection<Guid> ServiceIdentityIds => Ids;
        public bool IsServiceIdentity(Guid employeeId) => Ids.Contains(employeeId);
    }

    [Fact]
    public async Task The_configured_integration_account_cannot_be_given_overrides_and_keeps_its_confinement()
    {
        var registry = new MutableRegistry();
        using var factory = new TigerCsApiFactory
        {
            ExtraServices = services =>
            {
                services.RemoveAll<TigerCS.Application.Authorization.IServiceIdentityRegistry>();
                services.AddSingleton<TigerCS.Application.Authorization.IServiceIdentityRegistry>(registry);
            }
        };
        var (adminUser, adminPassword, _) = await factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        var (genesysUser, genesysPassword, genesysId) = await factory.SeedEmployeeAsync(Roles.CsAgent);
        registry.Ids.Add(genesysId);

        async Task<HttpClient> Login(string u, string pw)
        {
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(u, pw));
            response.EnsureSuccessStatusCode();
            var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
            return client;
        }

        var admin = await Login(adminUser, adminPassword);
        var genesys = await Login(genesysUser, genesysPassword);

        var attempt = await admin.PutAsJsonAsync($"/api/admin/users/{genesysId}/permissions",
            new SetUserPermissionsRequestDto(new Dictionary<string, string> { [P.TicketsCreate] = "Deny" }));
        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);

        // Still confined to the integration surface: own profile yes, dashboard no.
        Assert.Equal(HttpStatusCode.OK, (await genesys.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await genesys.GetAsync("/api/dashboard")).StatusCode);

        // The page DTO flags it, so the UI locks the form.
        var view = await admin.GetFromJsonAsync<UserPermissionsDto>($"/api/admin/users/{genesysId}/permissions");
        Assert.True(view!.IsServiceIdentity);
    }

    // ---- coverage: no controller action escapes the permission map ----

    public static IEnumerable<object[]> AllActions() =>
        typeof(AdminUsersController).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributes(true).Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute)))
            .Select(m => new object[] { $"{m.ReflectedType!.Name}.{m.Name}", m });

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Every_controller_action_declares_a_permission_or_an_explicit_exemption(string name, MethodInfo action)
    {
        var attributes = action.GetCustomAttributes(inherit: true)
            .Concat(action.ReflectedType!.GetCustomAttributes(inherit: true))
            .ToList();

        var covered = attributes.OfType<RequirePermissionAttribute>().Any() || attributes.OfType<NoPermissionRequiredAttribute>().Any();
        Assert.True(covered, $"{name} has neither [RequirePermission] nor [NoPermissionRequired].");

        Assert.All(attributes.OfType<RequirePermissionAttribute>(),
            a => Assert.True(PermissionCatalog.IsKnown(a.Permission), $"{name}: unknown permission '{a.Permission}'."));
    }
}
