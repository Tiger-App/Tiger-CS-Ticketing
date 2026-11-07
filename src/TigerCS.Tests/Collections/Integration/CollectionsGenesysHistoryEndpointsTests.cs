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
        ["exports"] = (await client.GetAsync($"{Base}/exports")).StatusCode
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
        using var agent = await Client(factory, Roles.CsAgent);
        var asAgent = await CallEveryRouteAsync(agent);
        Assert.Equal(HttpStatusCode.OK, asAgent["submissions"]);
        Assert.Equal(HttpStatusCode.NotFound, asAgent["submission"]);
        Assert.Equal(HttpStatusCode.Forbidden, asAgent["exports"]);
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
}
