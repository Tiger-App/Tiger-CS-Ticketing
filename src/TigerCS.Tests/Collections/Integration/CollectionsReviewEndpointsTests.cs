using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Integration;

/// <summary>The review and dispatch routes through the real host: authentication, the explicit Collections grants and the System Administrator override.</summary>
public sealed class CollectionsReviewEndpointsTests
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
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static readonly SelectionRequest Selection = new(new ReviewFilter(), ReviewQueryService.ModeAllMatching);
    private static readonly Guid AnyDispatch = Guid.NewGuid();

    private static async Task<Dictionary<string, HttpStatusCode>> CallEveryRouteAsync(HttpClient client) => new()
    {
        ["runs/current"] = (await client.GetAsync("/api/collections/review/runs/current")).StatusCode,
        ["runs/{id}"] = (await client.GetAsync("/api/collections/review/runs/999")).StatusCode,
        ["records"] = (await client.GetAsync("/api/collections/review/records")).StatusCode,
        ["reasons"] = (await client.GetAsync("/api/collections/review/reasons")).StatusCode,
        ["dispatches"] = (await client.GetAsync("/api/collections/review/dispatches")).StatusCode,
        ["readiness"] = (await client.GetAsync("/api/collections/review/dispatches/readiness")).StatusCode,
        ["dispatches/{id}"] = (await client.GetAsync($"/api/collections/review/dispatches/{AnyDispatch}")).StatusCode,
        ["refresh"] = (await client.PostAsJsonAsync("/api/collections/review/refresh", new { companyId = (int?)null })).StatusCode,
        ["summary"] = (await client.PostAsJsonAsync("/api/collections/review/selection/summary", Selection)).StatusCode,
        ["confirm"] = (await client.PostAsJsonAsync("/api/collections/review/dispatches",
            new ConfirmDispatchRequest(Selection, 1, "x", "k", true))).StatusCode,
        ["overlaps"] = (await client.GetAsync("/api/collections/review/overlaps")).StatusCode,
        ["contacts"] = (await client.GetAsync($"/api/collections/review/dispatches/{AnyDispatch}/contacts")).StatusCode,
        ["sweep"] = (await client.PostAsync("/api/collections/review/suppression/sweep", null)).StatusCode,
        ["cancel"] = (await client.PostAsync($"/api/collections/review/dispatches/{AnyDispatch}/cancel", null)).StatusCode,
        ["retry-email"] = (await client.PostAsync($"/api/collections/review/dispatches/{AnyDispatch}/admin-email/retry", null)).StatusCode,
        ["reconcile"] = (await client.PostAsJsonAsync($"/api/collections/review/dispatches/{AnyDispatch}/batches/1/reconcile",
            new ReconcileBatchRequest("ConfirmedNotUploaded", "checked"))).StatusCode,
    };

    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnEveryReviewRoute_ThroughTheOverride()
    {
        using var factory = Factory(new Source());
        using var client = await Client(factory, Roles.SystemAdministrator);
        var results = await CallEveryRouteAsync(client);
        // Authorized means the request got past authentication and the Collections grant: whatever the answer is, it is not 401 or 403.
        Assert.All(results, r => Assert.True(r.Value is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{r.Key}: {r.Value}"));
        Assert.Equal(HttpStatusCode.OK, results["records"]);
        Assert.Equal(HttpStatusCode.OK, results["reasons"]);
        Assert.Equal(HttpStatusCode.NotFound, results["dispatches/{id}"]);
    }

    [Fact]
    public async Task Anonymous_IsRejectedEverywhere_AndReportingUserHasNoCollectionsAccess()
    {
        var source = new Source();
        using var factory = Factory(source);
        using var anonymous = factory.CreateClient();
        Assert.All(await CallEveryRouteAsync(anonymous), r => Assert.Equal(HttpStatusCode.Unauthorized, r.Value));
        using var reporter = await Client(factory, Roles.ReportingUser);
        var results = await CallEveryRouteAsync(reporter);
        foreach (var route in new[] { "runs/current", "runs/{id}", "records", "dispatches", "readiness", "dispatches/{id}", "refresh", "summary", "confirm", "reconcile", "cancel", "retry-email", "overlaps", "sweep", "contacts" })
            Assert.Equal(HttpStatusCode.Forbidden, results[route]);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task AFinancialReader_CanBrowse_ButCannotSelectConfirmOrReconcile()
    {
        var source = new Source();
        using var factory = Factory(source);
        using var agent = await Client(factory, Roles.CsAgent);
        var results = await CallEveryRouteAsync(agent);
        Assert.Equal(HttpStatusCode.OK, results["records"]);
        Assert.Equal(HttpStatusCode.OK, results["dispatches"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["summary"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["confirm"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["reconcile"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["cancel"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["retry-email"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["sweep"]);
        Assert.Equal(HttpStatusCode.Forbidden, results["contacts"]);
        Assert.Equal(HttpStatusCode.OK, results["overlaps"]);
    }

    [Fact]
    public async Task ABrowseRequestNeverCallsTheFinancialSource()
    {
        var source = new Source();
        using var factory = Factory(source);
        using var manager = await Client(factory, Roles.CsManager);
        var page = await manager.GetFromJsonAsync<ReviewPageDto>("/api/collections/review/records?paymentStatus=Unknown&minRemaining=0&page=1&pageSize=25");
        Assert.Equal(0, page!.TotalCount);
        Assert.Equal(0, source.Reads);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/collections/review/records?paymentStatus=Nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/collections/review/records?pageSize=1000")).StatusCode);
    }

    [Fact]
    public async Task ConfirmationNeedsTheIdempotencyKey_AndAnAcknowledgement()
    {
        using var factory = Factory(new Source());
        using var manager = await Client(factory, Roles.CsManager);
        var noKey = await manager.PostAsJsonAsync("/api/collections/review/dispatches", new ConfirmDispatchRequest(Selection, 1, "x", "", true));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        var noAck = await manager.PostAsJsonAsync("/api/collections/review/dispatches", new ConfirmDispatchRequest(Selection, 1, "x", "k", false));
        Assert.Equal(HttpStatusCode.BadRequest, noAck.StatusCode);
    }
}
