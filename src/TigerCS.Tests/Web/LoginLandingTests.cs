extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using PermissionKeys = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Web;

/// <summary>
/// Where a person lands after signing in: the Dashboard for every role (unless their Dashboard access was explicitly denied), and always the page they asked for
/// (a local returnUrl) first. Landing is only a choice of page: it changes no authentication, no route and no authorization.
/// </summary>
public sealed class LoginLandingTests
{
    private const string Collections = "/Collections/CampaignList";
    private const string Dashboard = "/Dashboard";

    private sealed class Api : HttpMessageHandler
    {
        public string[] Roles { get; set; } = [TigerCS.Domain.Modules.IdentityAndAccess.Roles.CsAgent];
        public string[] Denied { get; set; } = [];
        public string[] Granted { get; set; } = [];
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method + " " + path);
            HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
            if (path == "/api/auth/login")
                return Task.FromResult(Json(new LoginResponseDto("token", DateTime.UtcNow.AddHours(1), Guid.NewGuid(), "Test User", Roles, null)));
            if (path == "/api/users/me")
                return Task.FromResult(Json(new CurrentUserResponseDto(Guid.NewGuid(), "Test User", Roles, [], false, null, Granted, Denied)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "6f1d2a40-8f0e-4c7b-9b57-0a1f3c2d4e5f"), new Claim(ClaimTypes.Name, "Test User"), new Claim(ClaimTypes.Role, role.ToString())], "Test")), "Test")));
        }
    }


    private static WebApplicationFactory<TigerCsWeb::Program> Factory(HttpMessageHandler api, bool fakeSession) =>
        new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = api));
                if (!fakeSession) return;
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; });
            });
        });

    /// <summary>Signs in through the real Login page (antiforgery token and cookie included) and returns where it redirects.</summary>
    private static async Task<(HttpStatusCode Status, string? Location)> SignInAsync(Api api, string? returnUrl = null)
    {
        using var factory = Factory(api, fakeSession: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var url = returnUrl is null ? "/Login" : "/Login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var page = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Input.Identifier"] = "user@tiger.test", ["Input.Password"] = "Passw0rd!" };
        var action = Regex.Match(page, "<form[^>]*method=\"post\"[^>]*action=\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : url;
        var response = await client.PostAsync(action, new FormUrlEncodedContent(form));
        return (response.StatusCode, response.Headers.Location?.OriginalString);
    }

    // ------------------------------------------------------------ after signing in

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task EveryRole_LandsOnTheDashboard_AfterSigningIn(string role)
    {
        var (status, location) = await SignInAsync(new Api { Roles = [role] });
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal(Dashboard, location);
    }

    [Fact]
    public async Task ARequestedPage_IsKept_ForAPersonWithCollectionsAccess_AndForOneWithout()
    {
        Assert.Equal("/Tickets/42?view=mine", (await SignInAsync(new Api { Roles = [Roles.CsAgent] }, "/Tickets/42?view=mine")).Location);
        Assert.Equal("/Customers", (await SignInAsync(new Api { Roles = [Roles.CsManager] }, "/Customers")).Location);
        Assert.Equal(Dashboard, (await SignInAsync(new Api { Roles = [Roles.CsAgent] }, "/Dashboard")).Location);   // an explicit Dashboard request is honoured
        Assert.Equal("/Reports/TeamPerformance", (await SignInAsync(new Api { Roles = [Roles.ReportingUser] }, "/Reports/TeamPerformance")).Location);
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    public async Task AnExternalReturnUrl_IsStillIgnored_NeverFollowed(string returnUrl)
    {
        var (_, location) = await SignInAsync(new Api { Roles = [Roles.CsAgent] }, returnUrl);
        Assert.Equal(Dashboard, location);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Login")]
    [InlineData("/Login?returnUrl=%2FTickets")]
    public async Task AReturnUrlThatIsTheRootOrTheLoginPage_IsNotFollowed_SoThereIsNoLoopOrExtraHop(string returnUrl)
    {
        var (_, location) = await SignInAsync(new Api { Roles = [Roles.CsAgent] }, returnUrl);
        Assert.Equal(Dashboard, location);
    }

    [Fact]
    public void FollowableReturnUrls_AreLocalAndNotTheRootOrLogin()
    {
        static bool Local(string u) => u.StartsWith('/') && !u.StartsWith("//") && !u.StartsWith("/\\");
        Assert.True(TigerCsWeb::TigerCS.Web.Services.Auth.LandingPage.IsFollowable("/Tickets/42?view=mine", Local));
        Assert.False(TigerCsWeb::TigerCS.Web.Services.Auth.LandingPage.IsFollowable(null, Local));
        Assert.False(TigerCsWeb::TigerCS.Web.Services.Auth.LandingPage.IsFollowable("  ", Local));
        Assert.False(TigerCsWeb::TigerCS.Web.Services.Auth.LandingPage.IsFollowable("/", Local));
        Assert.False(TigerCsWeb::TigerCS.Web.Services.Auth.LandingPage.IsFollowable("/login/", Local));
        Assert.False(TigerCsWeb::TigerCS.Web.Services.Auth.LandingPage.IsFollowable("//evil.example", Local));
    }

    [Fact]
    public async Task ARefusedLogin_StaysOnTheLoginPage_WithNoRedirect()
    {
        using var factory = Factory(new RefusingApi(), fakeSession: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await (await client.GetAsync("/Login")).Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var response = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token, ["Input.Identifier"] = "user@tiger.test", ["Input.Password"] = "wrong" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("was not accepted", await response.Content.ReadAsStringAsync());
    }

    private sealed class RefusingApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    // ------------------------------------------------------------ the site root

    private static async Task<(HttpStatusCode Status, string? Location)> RootAsync(Api api, string? role)
    {
        using var factory = Factory(api, fakeSession: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        var response = await client.GetAsync("/");
        return (response.StatusCode, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task TheRoot_SendsEveryAuthenticatedRoleToTheDashboard(string role)
    {
        var (status, location) = await RootAsync(new Api { Roles = [role] }, role);
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal(Dashboard, location);
    }

    [Fact]
    public async Task AnAnonymousVisitor_StillGoesToLogin()
    {
        var (status, location) = await RootAsync(new Api(), null);
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal("/Login", location);
    }

    [Fact]
    public async Task AnAnonymousDeepLink_IsSentToLogin_WithTheRequestedPageKept()
    {
        using var factory = Factory(new Api(), fakeSession: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Tickets/42?view=mine");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var target = response.Headers.Location!;
        var location = target.IsAbsoluteUri ? target.PathAndQuery : target.OriginalString;
        Assert.StartsWith("/Login", location);
        Assert.Equal("/Tickets/42?view=mine", Uri.UnescapeDataString(location[(location.IndexOf("ReturnUrl=", StringComparison.OrdinalIgnoreCase) + "ReturnUrl=".Length)..]));
    }

    [Fact]
    public async Task TheDashboard_IsNotARedirectLoop_ForAnAuthenticatedPerson()
    {
        using var factory = Factory(new Api { Roles = [Roles.CsAgent] }, fakeSession: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", Roles.CsAgent);
        var root = await client.GetAsync("/");
        Assert.Equal(Dashboard, root.Headers.Location?.OriginalString);
        var dashboard = await client.GetAsync(Dashboard);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);                  // lands and stays: the Dashboard never redirects back to "/"
        Assert.Null(dashboard.Headers.Location);
    }

    [Fact]
    public async Task APersonDeniedTheDashboard_IsNotSentThere_ButToTheirPreviousLanding()
    {
        // Denied Dashboard: Collections when they can reach it, otherwise the Tickets landing - never a page they cannot open, never a loop back to "/".
        Assert.Equal(Collections, (await RootAsync(new Api { Roles = [Roles.CsAgent], Denied = [PermissionKeys.DashboardView] }, Roles.CsAgent)).Location);
        Assert.Equal("/Tickets", (await RootAsync(new Api { Roles = [Roles.DepartmentEmployee], Denied = [PermissionKeys.DashboardView] }, Roles.DepartmentEmployee)).Location);
        // A Collections denial does not change the new default.
        Assert.Equal(Dashboard, (await RootAsync(new Api { Roles = [Roles.CsAgent], Denied = [PermissionKeys.CollectionsView] }, Roles.CsAgent)).Location);
    }

    // ------------------------------------------------------------ the Dashboard stays reachable

    [Fact]
    public async Task TheDashboard_RemainsInTheNavigation_NextToCollections()
    {
        using var factory = CollectionsWebHost.Factory(new FakeCollectionsApi());
        using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables")).Content.ReadAsStringAsync();
        Assert.Matches(new Regex(@"app-nav__link[^>]*href=""/Dashboard""[^>]*>Dashboard<"), html);
        Assert.Matches(new Regex(@"app-nav__link is-active""[^>]*href=""/Collections/CampaignList""[^>]*>Collections<"), html);
        var dashboard = await client.GetAsync("/Dashboard");
        Assert.NotEqual(HttpStatusCode.NotFound, dashboard.StatusCode);                              // the route is unchanged
    }
}
