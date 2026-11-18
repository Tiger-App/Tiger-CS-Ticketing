using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Integration;

/// <summary>The Genesys Campaign History routes through the real host: authentication, the Collections rights, the System Administrator override, and read-only by construction.</summary>
public sealed class CollectionsGenesysHistoryEndpointsTests
{
    private sealed class Source : IPactReceivablesSource
    {
        public int Reads { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new PactReceivablesSnapshot([], DateTime.UtcNow, false));
        }
    }

    private static TigerCsApiFactory Factory(Source source) => new()
    {
        ExtraConfiguration = new()
        {
            ["Collections:Enabled"] = "true",
            ["CollectionsSource:PactReceivables:Enabled"] = "true",
            ["Collections:GenesysOutbound:Enabled"] = "true",
            ["Collections:GenesysOutbound:LiveCustomerDispatchEnabled"] = "true",
            ["Collections:GenesysOutbound:SuppressionEnabled"] = "true"
        },
        ExtraServices = services => services.AddScoped<IPactReceivablesSource>(_ => source)
    };

    /// <summary>A signed-in employee of <paramref name="role"/> who has been explicitly granted Collections module access (the role alone no longer opens it).</summary>
    private static async Task<HttpClient> ClientWithModuleAccess(TigerCsApiFactory factory, string role)
    {
        var (username, password, employeeId) = await factory.SeedEmployeeAsync(role);
        await factory.SetPermissionOverrideAsync(employeeId, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.CollectionsAccess, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.PermissionEffect.Grant);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static async Task<HttpClient> Client(TigerCsApiFactory factory, string role)
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static readonly Guid AnySubmission = Guid.NewGuid();
    private const string Base = "/api/collections/genesys-history";

    private static async Task<Dictionary<string, HttpStatusCode>> CallEveryRouteAsync(HttpClient client) => new()
    {
        ["submissions"] = (await client.GetAsync($"{Base}/submissions")).StatusCode,
        ["submission"] = (await client.GetAsync($"{Base}/submissions/{AnySubmission}")).StatusCode,
        ["records"] = (await client.GetAsync($"{Base}/submissions/{AnySubmission}/records")).StatusCode,
        ["exports"] = (await client.GetAsync($"{Base}/exports")).StatusCode,
        ["sync"] = (await client.PostAsJsonAsync($"{Base}/sync", new { submissionIds = new[] { AnySubmission } })).StatusCode
    };

    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnEveryHistoryRoute_ThroughTheOverride()
    {
        using var factory = Factory(new Source());
        using var client = await Client(factory, Roles.SystemAdministrator);
        var results = await CallEveryRouteAsync(client);
        Assert.All(results, r => Assert.True(r.Value is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{r.Key}: {r.Value}"));
        Assert.Equal(HttpStatusCode.OK, results["submissions"]);
        Assert.Equal(HttpStatusCode.OK, results["exports"]);
        Assert.Equal(HttpStatusCode.OK, results["sync"]);                                         // an unknown campaign is reported in the result, not as an error
        Assert.Equal(HttpStatusCode.NotFound, results["submission"]);
        Assert.Equal(HttpStatusCode.NotFound, results["records"]);
        var page = await client.GetFromJsonAsync<GenesysHistoryPageDto>($"{Base}/submissions");
        Assert.Equal((0, 0, 0), (page!.TotalCount, page.Items.Count, page.Summary.Submissions));         // the empty state
        Assert.Contains("call outcome is unavailable", page.CallOutcomeNote);
    }

    [Fact]
    public async Task Anonymous_IsRejected_AndAUserWithoutCollectionsAccessSeesNothing()
    {
        var source = new Source();
        using var factory = Factory(source);
        using var anonymous = factory.CreateClient();
        Assert.All(await CallEveryRouteAsync(anonymous), r => Assert.Equal(HttpStatusCode.Unauthorized, r.Value));
        using var reporter = await Client(factory, Roles.ReportingUser);
        Assert.All(await CallEveryRouteAsync(reporter), r => Assert.Equal(HttpStatusCode.Forbidden, r.Value));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task AFinancialReaderSeesSubmissions_ButOnlyASenderSeesDownloadedFiles()
    {
        using var factory = Factory(new Source());
        using var agent = await ClientWithModuleAccess(factory, Roles.CsAgent);   // module access is explicit; the granular permissions below still decide what the agent may do
        var asAgent = await CallEveryRouteAsync(agent);
        Assert.Equal(HttpStatusCode.OK, asAgent["submissions"]);
        Assert.Equal(HttpStatusCode.NotFound, asAgent["submission"]);
        Assert.Equal(HttpStatusCode.Forbidden, asAgent["exports"]);
        Assert.Equal(HttpStatusCode.Forbidden, asAgent["sync"]);                                  // syncing spends Genesys API quota: the send right
        using var manager = await Client(factory, Roles.CsManager);
        Assert.Equal(HttpStatusCode.OK, (await CallEveryRouteAsync(manager))["exports"]);
        var shown = await agent.GetFromJsonAsync<GenesysHistoryPageDto>($"{Base}/submissions");
        Assert.False(shown!.CanSeeExportHistory);
        Assert.True((await manager.GetFromJsonAsync<GenesysHistoryPageDto>($"{Base}/submissions"))!.CanSeeExportHistory);
    }

    [Fact]
    public async Task EveryHistoryRoute_IsReadOnly_AndABrowseNeverCallsTheFinancialSource()
    {
        var source = new Source();
        using var factory = Factory(source);
        using var manager = await Client(factory, Roles.CsManager);
        foreach (var url in new[] { $"{Base}/submissions", $"{Base}/submissions/{AnySubmission}", $"{Base}/submissions/{AnySubmission}/records", $"{Base}/exports" })
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await manager.PostAsync(url, null)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await manager.PutAsync(url, null)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await manager.DeleteAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await manager.PatchAsync(url, null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync($"{Base}/submissions/{AnySubmission}/resend", null)).StatusCode);   // there is no resend action
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task InvalidFilters_AreRefused_WithAProblem()
    {
        using var factory = Factory(new Source());
        using var manager = await Client(factory, Roles.CsManager);
        foreach (var query in new[] { "status=Called", "mode=Robot", "stage=Nope", "list=zzz", "from=2026-10-05&to=2026-10-01" })
            Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"{Base}/submissions?{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"{Base}/exports?from=2026-10-05&to=2026-10-01")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{Base}/submissions?pageSize=100000&page=-4")).StatusCode);          // paging is clamped, not an error
    }

    // ------------------------------------------------------------ Collections module access (the gate)

    [Fact]
    public async Task ModuleAccess_IsDeniedByDefault_ForEveryRoleThatIsNotASystemAdministratorOrCsManager()
    {
        using var factory = Factory(new Source());
        foreach (var role in Roles.All.Where(r => r != Roles.SystemAdministrator && r != Roles.CsManager))
        {
            using var client = await Client(factory, role);
            var results = await CallEveryRouteAsync(client);
            Assert.All(results, r => Assert.True(r.Value == HttpStatusCode.Forbidden, $"{role} {r.Key}: {r.Value}"));
        }
    }

    [Fact]
    public async Task ModuleAccess_IsOpenToSystemAdministratorAndCsManager_WithNoGrant_AndEvenWhenTheOverrideSaysDeny()
    {
        using var factory = Factory(new Source());
        using var admin = await Client(factory, Roles.SystemAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Base}/submissions")).StatusCode);

        var (username, password, managerId) = await factory.SeedEmployeeAsync(Roles.CsManager);
        using var manager = await SignInAsync(factory, username, password);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{Base}/submissions")).StatusCode);

        // The role is the entitlement: an override cannot take it away.
        await factory.SetPermissionOverrideAsync(managerId, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.CollectionsAccess,
            TigerCS.Domain.Modules.IdentityAndAccess.Permissions.PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{Base}/submissions")).StatusCode);
    }

    [Fact]
    public async Task ModuleAccess_IsGrantedExplicitly_TakesEffectOnTheNextRequest_AndStopsWhenRevoked()
    {
        using var factory = Factory(new Source());
        var (username, password, agentId) = await factory.SeedEmployeeAsync(Roles.CsAgent);
        using var agent = await SignInAsync(factory, username, password);
        var key = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.CollectionsAccess;

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"{Base}/submissions")).StatusCode);

        await factory.SetPermissionOverrideAsync(agentId, key, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.PermissionEffect.Grant);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"{Base}/submissions")).StatusCode);   // same token: overrides are read on every request

        await factory.ClearPermissionOverrideAsync(agentId, key);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"{Base}/submissions")).StatusCode);

        await factory.SetPermissionOverrideAsync(agentId, key, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"{Base}/submissions")).StatusCode);
    }

    [Fact]
    public async Task ModuleAccess_DoesNotWidenTheGranularRights_AGrantedAgentStillCannotSendOrExport()
    {
        using var factory = Factory(new Source());
        using var agent = await ClientWithModuleAccess(factory, Roles.CsAgent);
        var results = await CallEveryRouteAsync(agent);
        Assert.Equal(HttpStatusCode.OK, results["submissions"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["exports"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["sync"]);
    }

    private static async Task<HttpClient> SignInAsync(TigerCsApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }
}
