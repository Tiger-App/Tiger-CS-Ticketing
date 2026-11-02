// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using P = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Web;

/// <summary>
/// Navigation and page gates follow the viewer's effective access (role rule, explicit grant, explicit denial)
/// read from GET /api/users/me. Presentation only - the Api enforces the same overrides - so a failed /me read
/// falls back to the role rules and never to a broader menu than the roles allow.
/// </summary>
public sealed class NavigationPermissionsTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();

    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;
    private static string[] _granted = [];
    private static string[] _denied = [];
    private static bool _meFails;

    public NavigationPermissionsTests()
    {
        _granted = [];
        _denied = [];
        _meFails = false;
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new Api()));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpStatusCode Status, string Html)> GetAsync(string path, string role)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Nav(string html)
    {
        var start = html.IndexOf("<nav class=\"app-nav\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "primary navigation not rendered");
        return html[start..html.IndexOf("</nav>", start, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task A_denied_permission_removes_its_navigation_item_and_others_stay()
    {
        _denied = [P.CollectionsView, P.DashboardView];

        var (status, html) = await GetAsync("/Tickets", Roles.CsManager);
        var nav = Nav(html);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("href=\"/Collections/Receivables\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Dashboard\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"/Reports/TeamPerformance\"", nav, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_overrides_the_navigation_is_exactly_what_the_roles_allowed_before()
    {
        var (_, agentHtml) = await GetAsync("/Tickets", Roles.CsAgent);
        var agent = Nav(agentHtml);
        Assert.Contains("href=\"/Collections/Receivables\"", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Reports/TeamPerformance\"", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Admin\"", agent, StringComparison.Ordinal);

        var (_, adminHtml) = await GetAsync("/Tickets", Roles.SystemAdministrator);
        Assert.Contains("href=\"/Admin\"", Nav(adminHtml), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reports_grant_opens_the_reports_item_and_page_for_a_role_that_lacked_it()
    {
        var (_, before) = await GetAsync("/Tickets", Roles.CsAgent);
        Assert.DoesNotContain("href=\"/Reports/TeamPerformance\"", Nav(before), StringComparison.Ordinal);

        _granted = [P.ReportsView];
        var (_, after) = await GetAsync("/Tickets", Roles.CsAgent);
        Assert.Contains("href=\"/Reports/TeamPerformance\"", Nav(after), StringComparison.Ordinal);

        // The page gate agrees (not bounced to AccessDenied); the Api then decides what it returns.
        var (status, _) = await GetAsync("/Reports/TeamPerformance", Roles.CsAgent);
        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Without_the_grant_the_reports_page_gate_still_refuses_that_role()
    {
        var (status, _) = await GetAsync("/Reports/TeamPerformance", Roles.CsAgent);
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Denying_both_administration_views_hides_the_item_and_closes_the_admin_gate_for_an_administrator()
    {
        _denied = [P.AdminUsersView, P.AdminConfigurationView];

        var (_, html) = await GetAsync("/Tickets", Roles.SystemAdministrator);
        Assert.DoesNotContain("href=\"/Admin\"", Nav(html), StringComparison.Ordinal);

        var (status, _) = await GetAsync("/Admin", Roles.SystemAdministrator);
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task When_me_cannot_be_read_the_menu_falls_back_to_the_role_rules_only()
    {
        _meFails = true;
        _granted = [P.ReportsView];

        var (_, html) = await GetAsync("/Tickets", Roles.CsAgent);
        var nav = Nav(html);

        Assert.DoesNotContain("href=\"/Reports/TeamPerformance\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets\"", nav, StringComparison.Ordinal);
    }

    private sealed class AuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.TryGetValue("X-Test-Role", out var header) ? header.ToString() : Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Viewer"),
                new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class Api : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path == "api/users/me" && !_meFails)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new CurrentUserResponseDto(ViewerId, "Test Viewer", [Roles.CsAgent], [], true, null, _granted, _denied),
                        options: new(System.Text.Json.JsonSerializerDefaults.Web))
                });
            }

            // Everything else: empty lists / not found - the pages render their empty or error states, which is enough for the shell.
            var body = path.StartsWith("api/tickets", StringComparison.Ordinal)
                ? JsonContent.Create(new { items = Array.Empty<object>(), totalCount = 0, page = 1, pageSize = 20 }, options: new(System.Text.Json.JsonSerializerDefaults.Web))
                : null;
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = body });
        }
    }
}
