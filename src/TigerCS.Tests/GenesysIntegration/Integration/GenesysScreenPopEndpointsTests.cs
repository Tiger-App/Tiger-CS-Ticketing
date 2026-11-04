// TigerCS.Web's Program is referenced via an extern alias so it never collides with
// TigerCS.Api's, which the Api factory uses unqualified.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// One real Api host with Screen Pop configured, and one real TigerCS.Web
/// host whose Api calls are served by it — so a launch can be followed
/// exactly as Genesys would: issue on the Api, open the URL in a fresh
/// browser on the Web.
/// </summary>
public sealed class ScreenPopHostFixture : IDisposable
{
    public const string WebBaseUrl = "https://tigercs-web.test";

    public TigerCsApiFactory Api { get; } = new()
    {
        ExtraConfiguration = { ["Genesys:ScreenPopWebBaseUrl"] = WebBaseUrl }
    };

    public WebApplicationFactory<TigerCsWeb::Program> Web { get; }

    public ScreenPopHostFixture()
    {
        Web = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = Api.Server.CreateHandler())));
        });
    }

    /// <summary>A brand-new browser: its own empty cookie jar, redirects not followed so each hop can be asserted.</summary>
    public HttpClient NewBrowser() =>
        Web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public void Dispose()
    {
        Web.Dispose();
        Api.Dispose();
    }
}

/// <summary>
/// Secure Screen Pop end-to-end: <c>POST /api/genesys/screen-pop</c> issues a
/// one-hour, one-time launch URL; TigerCS Web's <c>/ScreenPop</c> redeems it
/// in a browser with no TigerCS cookie and establishes the mapped user's
/// ordinary session — which is then authorized like any other.
/// </summary>
public sealed class GenesysScreenPopEndpointsTests(ScreenPopHostFixture hosts) : IClassFixture<ScreenPopHostFixture>
{
    private TigerCsApiFactory Api => hosts.Api;

    private async Task<(HttpClient Client, Guid EmployeeId)> SignInAsync(string role = Roles.CsAgent)
    {
        var (username, password, employeeId) = await Api.SeedEmployeeAsync(role);
        var client = Api.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    /// <summary>A Genesys agent mapped to a Ticketing user who belongs to one department.</summary>
    private async Task<(string GenesysUserId, Guid UserId, int DepartmentId)> MappedAgentAsync(string role = Roles.DepartmentEmployee)
    {
        var (_, userId) = await SignInAsync(role);
        var departmentId = await Api.CreateDepartmentAsync("Genesys Dept " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await Api.AssignPrimaryDepartmentAsync(userId, departmentId);
        var genesysUserId = "ga-" + Guid.NewGuid().ToString("N");
        await Api.MapGenesysAgentAsync(userId, genesysUserId);
        return (genesysUserId, userId, departmentId);
    }

    private async Task<(long TicketId, string TicketNumber, string ConversationId)> IngestAsync(HttpClient service, int departmentId)
    {
        await Api.SeedPrioritiesAsync();
        var conversationId = "conv-" + Guid.NewGuid().ToString("N");
        var response = await service.PostAsJsonAsync(
            "/api/genesys/tickets", new GenesysInquiryRequest(conversationId, "Phone", DepartmentId: departmentId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>();
        return (body!.TicketId, body.TicketNumber, conversationId);
    }

    private static async Task<GenesysScreenPopResponse> IssueAsync(HttpClient service, GenesysScreenPopRequest request)
    {
        var response = await service.PostAsJsonAsync("/api/genesys/screen-pop", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GenesysScreenPopResponse>())!;
    }

    private static string TokenOf(string launchUrl) => new Uri(launchUrl).Query["?token=".Length..];

    private Task<HttpResponseMessage> RedeemAsync(string token) =>
        Api.CreateClient().PostAsJsonAsync("/api/auth/screen-pop/redeem", new ScreenPopRedeemRequestDto(token));

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task ExpireAsync(string token)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var hash = Application.Modules.GenesysIntegration.Services.GenesysScreenPopAppService.HashToken(token);
        var launch = await db.GenesysScreenPopLaunches.FirstAsync(l => l.TokenHash == hash);
        // Test setup reaching past the entity's write-once surface on purpose:
        // the launch exposes no way to back-date itself.
        db.Entry(launch).Property(nameof(GenesysScreenPopLaunch.IssuedAtUtc)).CurrentValue = DateTime.UtcNow.AddHours(-1).AddSeconds(-5);
        db.Entry(launch).Property(nameof(GenesysScreenPopLaunch.ExpiresAtUtc)).CurrentValue = DateTime.UtcNow.AddSeconds(-5);
        await db.SaveChangesAsync();
    }

    // ---- Issue ----

    [Fact]
    public async Task Issue_ReturnsAOneHourLaunchUrl_AndStoresOnlyTheHash()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, userId, _) = await MappedAgentAsync();

        var before = DateTime.UtcNow;
        var issued = await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId));

        Assert.StartsWith($"{ScreenPopHostFixture.WebBaseUrl}/ScreenPop?token=", issued.LaunchUrl);
        Assert.Equal(3600, issued.ExpiresInSeconds);
        Assert.InRange(issued.ExpiresAtUtc, before.AddHours(1).AddSeconds(-5), DateTime.UtcNow.AddHours(1).AddSeconds(5));
        Assert.Equal("/Tickets", issued.TargetPath);

        var token = TokenOf(issued.LaunchUrl);
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var stored = await db.GenesysScreenPopLaunches.AsNoTracking().Where(l => l.UserId == userId).ToListAsync();
        var launch = Assert.Single(stored);
        Assert.NotEqual(token, launch.TokenHash);
        Assert.False(await db.GenesysScreenPopLaunches.AnyAsync(l => l.TokenHash == token));
    }

    [Fact]
    public async Task Issue_UnmappedAgent_Returns403_WithTheStableCode()
    {
        var (service, _) = await SignInAsync();

        var response = await service.PostAsJsonAsync("/api/genesys/screen-pop", new GenesysScreenPopRequest("ga-never-mapped-" + Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(GenesysController.ErrorCodes.AgentNotMapped, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Issue_InactiveAgent_Returns403_Inactive()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, userId, _) = await MappedAgentAsync();
        await Api.DeactivateEmployeeAsync(userId);

        var response = await service.PostAsJsonAsync("/api/genesys/screen-pop", new GenesysScreenPopRequest(genesysUserId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(GenesysController.ErrorCodes.AgentInactive, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Issue_MissingGenesysUserId_Returns400()
    {
        var (service, _) = await SignInAsync();

        var response = await service.PostAsJsonAsync("/api/genesys/screen-pop", new GenesysScreenPopRequest(" ", TicketId: 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Issue_RequiresAuthentication()
    {
        var response = await Api.CreateClient().PostAsJsonAsync("/api/genesys/screen-pop", new GenesysScreenPopRequest("ga-7"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Redeem ----

    [Fact]
    public async Task Redeem_ReturnsTheMappedUsersOrdinarySession_AndOnlyOnce()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, userId, departmentId) = await MappedAgentAsync();
        var (ticketId, _, conversationId) = await IngestAsync(service, departmentId);
        var issued = await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId, conversationId));
        var token = TokenOf(issued.LaunchUrl);

        var first = await RedeemAsync(token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var session = (await first.Content.ReadFromJsonAsync<ScreenPopSessionResponseDto>())!;
        Assert.Equal(userId, session.EmployeeId);
        Assert.Equal($"/Tickets/{ticketId}", session.TargetPath);
        Assert.Equal([Roles.DepartmentEmployee], session.Roles);
        Assert.Equal(departmentId, session.PrimaryDepartmentId);

        // A working TigerCS access token for exactly that user.
        var asAgent = Api.CreateClient();
        asAgent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var me = JsonDocument.Parse(await asAgent.GetStringAsync("/api/users/me"));
        Assert.Contains(userId.ToString(), me.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var second = await RedeemAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(AuthController.ScreenPopErrorCodes.TokenUsed, await ProblemCodeAsync(second));
    }

    [Fact]
    public async Task Redeem_ExpiredToken_Returns401_Expired()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, _, _) = await MappedAgentAsync();
        var token = TokenOf((await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId))).LaunchUrl);
        await ExpireAsync(token);

        var response = await RedeemAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthController.ScreenPopErrorCodes.TokenExpired, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Redeem_UnknownToken_Returns401_Invalid()
    {
        var response = await RedeemAsync("this-token-was-never-issued");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AuthController.ScreenPopErrorCodes.TokenInvalid, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Redeem_AfterTheAgentWasDeactivated_Returns403_AndNoSession()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, userId, _) = await MappedAgentAsync();
        var token = TokenOf((await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId))).LaunchUrl);
        await Api.DeactivateEmployeeAsync(userId);

        var response = await RedeemAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(GenesysController.ErrorCodes.AgentInactive, await ProblemCodeAsync(response));
        Assert.DoesNotContain("accessToken", await response.Content.ReadAsStringAsync());
    }

    // ---- Authorization is unchanged by Screen Pop ----

    [Fact]
    public async Task ScreenPopSession_IsStillBoundByTicketVisibility()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, agentUserId, ownDepartment) = await MappedAgentAsync();
        var otherDepartment = await Api.CreateDepartmentAsync("Other Dept " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (ownTicket, _, _) = await IngestAsync(service, ownDepartment);
        var (otherTicket, _, _) = await IngestAsync(service, otherDepartment);
        var (unassignedSameDepartmentTicket, _, _) = await IngestAsync(service, ownDepartment);
        // A Department Employee sees only tickets assigned to them (the Genesys answer assigns the agent who takes the conversation).
        await Api.AssignOwnerAsync(ownTicket, agentUserId);

        // The launch lands on a ticket in a department the agent does not
        // belong to — the landing is where the browser goes, not a grant.
        var issued = await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId, TicketId: otherTicket));
        Assert.Equal($"/Tickets/{otherTicket}", issued.TargetPath);
        var session = (await (await RedeemAsync(TokenOf(issued.LaunchUrl))).Content.ReadFromJsonAsync<ScreenPopSessionResponseDto>())!;

        var asAgent = Api.CreateClient();
        asAgent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var other = await asAgent.GetAsync($"/api/tickets/{otherTicket}");
        Assert.Contains(other.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        var sameDepartmentUnassigned = await asAgent.GetAsync($"/api/tickets/{unassignedSameDepartmentTicket}");
        Assert.Contains(sameDepartmentUnassigned.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        var own = await asAgent.GetAsync($"/api/tickets/{ownTicket}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    // ---- The browser: fresh, cookie-less, via TigerCS Web ----

    [Fact]
    public async Task FreshBrowser_WithNoCookies_OpensTheLaunchUrl_AndLandsOnTheTicketSignedIn()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, agentUserId, departmentId) = await MappedAgentAsync();
        var (ticketId, ticketNumber, conversationId) = await IngestAsync(service, departmentId);
        await Api.AssignOwnerAsync(ticketId, agentUserId);   // the answering agent owns the ticket they are popped onto
        var issued = await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId, conversationId));

        using var browser = hosts.NewBrowser();

        // Before: this browser has no session — the ticket page sends it to Login.
        var anonymous = await browser.GetAsync($"/Tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/Login", anonymous.Headers.Location!.AbsolutePath);

        // Open the launch URL: a standard redirect to the target, with the
        // auth cookie set and the token-bearing response never cached.
        var launch = await browser.GetAsync(new Uri(issued.LaunchUrl).PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, launch.StatusCode);
        Assert.Equal($"/Tickets/{ticketId}", launch.Headers.Location!.OriginalString);
        Assert.Contains(launch.Headers.GetValues("Set-Cookie"), c => c.StartsWith("TigerCS.Web.Auth", StringComparison.Ordinal));
        Assert.True(launch.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", launch.Headers.GetValues("Referrer-Policy").Single());

        // After: the same browser is now signed in and the ticket renders.
        var ticketPage = await browser.GetAsync($"/Tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, ticketPage.StatusCode);
        Assert.Contains(ticketNumber, await ticketPage.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SecondBrowser_ReusingAnAlreadyUsedLaunchUrl_IsNotSignedIn()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, _, _) = await MappedAgentAsync();
        var issued = await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId));
        var path = new Uri(issued.LaunchUrl).PathAndQuery;

        using var first = hosts.NewBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await first.GetAsync(path)).StatusCode);

        using var second = hosts.NewBrowser();
        var reused = await second.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.False(reused.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("TigerCS.Web.Auth=", StringComparison.Ordinal) && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)));

        var tickets = await second.GetAsync("/Tickets");
        Assert.Equal(HttpStatusCode.Redirect, tickets.StatusCode);
        Assert.Equal("/Login", tickets.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Browser_ExpiredLaunchUrl_ShowsTheExpiredMessage_AndIsNotSignedIn()
    {
        var (service, _) = await SignInAsync();
        var (genesysUserId, _, _) = await MappedAgentAsync();
        var issued = await IssueAsync(service, new GenesysScreenPopRequest(genesysUserId));
        await ExpireAsync(TokenOf(issued.LaunchUrl));

        using var browser = hosts.NewBrowser();
        var response = await browser.GetAsync(new Uri(issued.LaunchUrl).PathAndQuery);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("more than one hour old", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Tickets")).StatusCode);
    }

    [Fact]
    public async Task Browser_AlreadySignedInAsSomeoneElse_IsSwitchedToTheMappedAgent()
    {
        var (service, _) = await SignInAsync();
        var (firstAgent, _, _) = await MappedAgentAsync();
        var (secondAgent, secondUserId, _) = await MappedAgentAsync();

        using var browser = hosts.NewBrowser();
        await browser.GetAsync(new Uri((await IssueAsync(service, new GenesysScreenPopRequest(firstAgent))).LaunchUrl).PathAndQuery);
        var switched = await browser.GetAsync(new Uri((await IssueAsync(service, new GenesysScreenPopRequest(secondAgent))).LaunchUrl).PathAndQuery);

        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.True(await db.GenesysScreenPopLaunches.AnyAsync(l => l.UserId == secondUserId && l.RedeemedAtUtc != null));
    }
}
