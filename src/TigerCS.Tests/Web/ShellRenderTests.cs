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
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The remaining application screens rendered end to end through the real
/// Razor pipeline — Login (anonymous), Dashboard, the New Ticket wizard's
/// first step and the Administration landing page — proving each renders
/// on the shared shell with the same primitives (page header, panel,
/// filter bar, stepper, empty/error states) and that the Dashboard's
/// shortcuts land on the right Tickets views.
/// </summary>
public sealed class ShellRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public ShellRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApi()));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client(string role = Roles.CsAgent)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    private static async Task<string> Ok(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
        return html;
    }

    [Fact]
    public async Task Login_RendersTheBackgroundShell_WithBrandingAndTheSignInForm()
    {
        var html = await Ok(await Client().GetAsync("/Login"));

        Assert.Contains("class=\"login-shell\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"login-aside\"", html, StringComparison.Ordinal);
        Assert.Contains("Tiger <span>Ticketing</span>", html, StringComparison.Ordinal);
        // The card: brand, welcome, subtitle, the two fields, one action.
        Assert.Contains("class=\"login-card__mark\"", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"login-brand__product\">Welcome back</div>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"login-brand__subtitle\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Identifier\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Password\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"btn btn-gold btn-block\">Sign In</button>", html, StringComparison.Ordinal);
        // No dead links, no admin-template chrome.
        Assert.DoesNotContain("href=\"#\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("login-corner", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// bg.png is the Login/Access Denied background, painted full-viewport
    /// from the self-hosted stylesheet — never an inline style attribute,
    /// which "style-src 'self'" would drop, and never hidden behind a solid
    /// panel over it.
    /// </summary>
    [Fact]
    public async Task Login_PaintsBgPngAsTheFullViewportBackground_FromTheStylesheet()
    {
        var html = await Ok(await Client().GetAsync("/Login"));
        Assert.DoesNotContain("style=", html, StringComparison.Ordinal);

        var css = await Ok(await Client().GetAsync("/css/site.css"));
        var shell = css[css.IndexOf(".login-shell {", StringComparison.Ordinal)..];
        shell = shell[..shell.IndexOf('}')];
        Assert.Contains("background-image: url(\"../img/bg.png\")", shell, StringComparison.Ordinal);
        Assert.Contains("background-size: cover", shell, StringComparison.Ordinal);
        Assert.Contains("background-position: center center", shell, StringComparison.Ordinal);
        Assert.Contains("min-height: 100vh", shell, StringComparison.Ordinal);

        // The image is served, and the brand panel over it is not a slab.
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/img/bg.png")).StatusCode);
        var aside = css[css.IndexOf(".login-aside {", StringComparison.Ordinal)..];
        aside = aside[..aside.IndexOf('}')];
        Assert.DoesNotContain("background", aside, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_SaysSo_WhenThePasswordWasJustChanged()
    {
        var html = await Ok(await Client().GetAsync("/Login?passwordChanged=true"));

        Assert.Contains("Your password was changed. Please sign in again.", html, StringComparison.Ordinal);
        Assert.Contains("class=\"alert alert-success\"", html, StringComparison.Ordinal);

        var plain = await Ok(await Client().GetAsync("/Login"));
        Assert.DoesNotContain("Your password was changed.", plain, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Change my password" is reachable from the account menu on every
    /// signed-in screen and renders on the shared shell with the three
    /// password fields, the policy beside the new one, and no inline style.
    /// </summary>
    [Fact]
    public async Task ChangePassword_IsLinkedFromTheAccountMenu_AndRendersTheForm()
    {
        var dashboard = await Ok(await Client().GetAsync("/Dashboard"));
        Assert.Contains("<a class=\"menu-item\" href=\"/Account/ChangePassword\">Change my password</a>", dashboard, StringComparison.Ordinal);

        var html = await Ok(await Client().GetAsync("/Account/ChangePassword"));
        Assert.Contains("<h1 class=\"page-title\">Change my password</h1>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"panel admin-form-panel\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.CurrentPassword\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.NewPassword\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.ConfirmPassword\"", html, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"current-password\"", html, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"new-password\"", html, StringComparison.Ordinal);
        Assert.Contains(TigerCsWeb::TigerCS.Web.Pages.Account.ChangePasswordModel.PasswordPolicyText, html, StringComparison.Ordinal);
        Assert.Contains(">Change Password</button>", html, StringComparison.Ordinal);
        Assert.Contains("Signed in as Test Agent", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangePassword_RequiresASignedInUser()
    {
        var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        anonymous.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        var response = await anonymous.GetAsync("/Account/ChangePassword");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AccessDenied_UsesTheSameLayout()
    {
        var html = await Ok(await Client().GetAsync("/AccessDenied"));

        Assert.Contains("class=\"login-shell\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"login-aside\"", html, StringComparison.Ordinal);
        Assert.Contains("Access denied", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets\">Back to Tickets</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_RendersSearchAndWorkspaceShortcuts_ThatLandOnEachTicketsView()
    {
        var html = await Ok(await Client().GetAsync("/Dashboard"));

        Assert.Contains("<h1 class=\"page-title\">Dashboard</h1>", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Dashboard</a>", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/Customers\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/Lookup\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets\">Open Tickets workspace</a>", html, StringComparison.Ordinal);

        // Each shortcut keeps its semantic tone — the same vocabulary the KPI
        // row and the breakdown cards use.
        foreach (var (href, label, tone) in new[]
        {
            ("/Tickets", "Queue", "info"),
            ("/Tickets?view=pending", "Pending Interactions", "secondary"),
            ("/Tickets?view=my", "My Tickets", "progress"),
            ("/Tickets?view=closed", "Closed", "success"),
        })
        {
            Assert.Contains($"<a class=\"dash-shortcut tone-{tone}\" href=\"{href}\">", html, StringComparison.Ordinal);
            Assert.Contains($"<span class=\"dash-shortcut__title\">{label}</span>", html, StringComparison.Ordinal);
        }

        // The Api is unreachable in this fake, so the KPI area shows the shared error state — never a broken layout.
        Assert.Contains("could not be loaded right now", html, StringComparison.Ordinal);
        Assert.Contains("class=\"error-state\"", html, StringComparison.Ordinal);
    }

    private async Task<string> DashboardWithKpisAsync(DashboardKpisDto kpis)
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-Populated", "true");
        FakeApi.Populated = true;
        FakeApi.KpisOverride = kpis;
        try { return await Ok(await client.GetAsync("/Dashboard")); }
        finally { FakeApi.Populated = false; FakeApi.KpisOverride = null; }
    }

    [Fact]
    public async Task Dashboard_ClosureSection_ShowsTheProminentTotal_TheOriginalIcons_AndOneGoldStarForAOnePointZeroScore()
    {
        var html = await DashboardWithKpisAsync(new DashboardKpisDto(42, 6, 11, 3, 4, 2,
            ReviewedTickets: 1, NotReviewedTickets: 118, ClosedByAiTickets: 0, ClosedByAgentTickets: 119, AverageReviewRating: 1.0));

        // The total leads the heading, as its own large element, and the sentence still reads whole.
        Assert.Contains("<span class=\"dash-closure__count\">119</span>", html, StringComparison.Ordinal);
        Assert.Contains("Tickets Closed in Selected Period</span>", html, StringComparison.Ordinal);

        // Each of the four counts: its own coloured chip (the stylesheet's per-card colour key), the glyph it always had, the number, the label — in that order, in one link.
        foreach (var (key, glyph) in new[]
        {
            ("reviewed", "M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.9 6.8 19.6l1-5.8-4.3-4.1 5.9-.9L12 3.5Z"),
            ("notreviewed", "stroke-dasharray=\"2.4 2.4\""),
            ("agent", "M15.5 17.5l1.6 1.6 3-3"),
            ("ai", "M12 8V4.5M9.5 12.5h.01M14.5 12.5h.01M9.5 15.5h5"),
        })
        {
            Assert.Contains($"data-closure=\"{key}\" data-kpi=\"{key}\"", html, StringComparison.Ordinal);
            var row = html[html.IndexOf($"data-closure=\"{key}\"", StringComparison.Ordinal)..];
            row = row[..row.IndexOf("</li>", StringComparison.Ordinal)];
            Assert.Contains("dash-closure__icon", row, StringComparison.Ordinal);
            Assert.Contains(glyph, row, StringComparison.Ordinal);
            Assert.True(row.IndexOf("dash-closure__icon", StringComparison.Ordinal) < row.IndexOf("dash-closure__value", StringComparison.Ordinal)
                && row.IndexOf("dash-closure__value", StringComparison.Ordinal) < row.IndexOf("dash-closure__label", StringComparison.Ordinal));
        }

        Assert.Contains("<strong class=\"dash-closure__value\">118</strong>", html, StringComparison.Ordinal);

        // The score: five stars, exactly the first filled, the exact score beside them, and an accessible name.
        Assert.Contains("role=\"img\" aria-label=\"1.0 out of 5 stars\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong class=\"dash-closure__score\">1.0 / 5</strong>", html, StringComparison.Ordinal);
        var stars = html[html.IndexOf("class=\"dash-stars\"", StringComparison.Ordinal)..];
        stars = stars[..stars.IndexOf("</span>", StringComparison.Ordinal)];
        Assert.Equal(5, Count(stars, "class=\"dash-stars__star\""));
        Assert.Equal(5, Count(stars, "class=\"dash-stars__outline\""));      // five outlined stars...
        Assert.Equal(1, Count(stars, "class=\"dash-stars__fill\""));         // ...exactly one of them filled
        Assert.Contains("data-fill=\"1\"", stars, StringComparison.Ordinal);
        Assert.Equal(4, Count(stars, "data-fill=\"0\""));
        Assert.DoesNotContain("No reviews yet", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", html[html.IndexOf("dash-closure\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_ClosureSection_FillsPartOfTheLastStarForAFractionalScore_AndKeepsTheExactNumber()
    {
        var html = await DashboardWithKpisAsync(new DashboardKpisDto(42, 6, 11, 3, 4, 2,
            ReviewedTickets: 10, NotReviewedTickets: 5, ClosedByAiTickets: 1, ClosedByAgentTickets: 14, AverageReviewRating: 3.7));

        Assert.Contains("aria-label=\"3.7 out of 5 stars\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong class=\"dash-closure__score\">3.7 / 5</strong>", html, StringComparison.Ordinal);
        var stars = html[html.IndexOf("class=\"dash-stars\"", StringComparison.Ordinal)..];
        stars = stars[..stars.IndexOf("</span>", StringComparison.Ordinal)];
        Assert.Equal(4, Count(stars, "class=\"dash-stars__fill\""));          // three whole stars and the part of the fourth
        Assert.Equal(3, Count(stars, "data-fill=\"1\""));
        Assert.Contains("data-fill=\"0.7\"", stars, StringComparison.Ordinal);
        Assert.Contains("<rect x=\"0\" y=\"0\" width=\"15.7\" height=\"24\" />", stars, StringComparison.Ordinal);   // 70% of the star glyph's own width (2.75 + 0.7 x 18.5), not of the 24-unit box
        Assert.Equal(1, Count(stars, "data-fill=\"0\""));
    }

    [Fact]
    public async Task Dashboard_ClosureSection_SaysNoReviewsYet_InsteadOfStars_WhenNothingHasAScore()
    {
        var html = await DashboardWithKpisAsync(new DashboardKpisDto(42, 6, 11, 3, 4, 2,
            ReviewedTickets: 0, NotReviewedTickets: 7, ClosedByAiTickets: 0, ClosedByAgentTickets: 7, AverageReviewRating: null));

        Assert.Contains("<span class=\"dash-closure__empty\">No reviews yet</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"dash-stars\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("out of 5 stars", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_WithData_RendersKpiCards_FilterDisclosure_AttentionTable_AndBreakdowns()
    {
        // Tests in one class run sequentially, so a static switch on the fake is safe here.
        var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-Populated", "true");
        FakeApi.Populated = true;
        string html;
        try { html = await Ok(await client.GetAsync("/Dashboard")); }
        finally { FakeApi.Populated = false; }

        // Six operational KPI cards; the closure figures are a separate section beneath them.
        Assert.Equal(6, Count(html, "class=\"kpi-card kpi-card--icon"));
        // Gold is spent on one card only.
        foreach (var (tone, expected) in new[]
        {
            ("tone-info", 1), ("tone-secondary", 1), ("tone-progress", 1), ("tone-critical", 1),
            ("tone-warning", 1), ("tone-brand", 1)
        })
        {
            Assert.Equal(expected, Count(html, $"kpi-card--icon {tone}"));
        }
        Assert.Contains("Tickets Closed in Selected Period", html, StringComparison.Ordinal);
        foreach (var label in new[] { "Ticket Reviews", "Closed By", "Reviewed", "Not Reviewed", "Average Review Score", "Human Agent", "AI Agent" })
        {
            Assert.Contains(label, html, StringComparison.Ordinal);
        }
        Assert.Contains("closureCategory=ClosedByAi", html, StringComparison.Ordinal);
        Assert.Contains("class=\"filter-disclosure\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"dashboardFilters\"", html, StringComparison.Ordinal);
        Assert.Contains("Tickets Requiring Attention", html, StringComparison.Ordinal);
        Assert.Contains("data-href=\"/Tickets/5\"", html, StringComparison.Ordinal);
        Assert.Contains("Volume by Department", html, StringComparison.Ordinal);
        Assert.Equal(6, Count(html, "class=\"panel dash-card tone-"));
        // KPI drill-downs land on the Tickets workspace; My Tickets on its own tab.
        Assert.Contains("href=\"/Tickets?", html, StringComparison.Ordinal);
        Assert.Contains("view=my", html, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public async Task Dashboard_DrawsBarWidthsWithStylesheetClasses_UnderTheStrictContentSecurityPolicy()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-Populated", "true");
        FakeApi.Populated = true;
        HttpResponseMessage response;
        string html;
        try
        {
            response = await client.GetAsync("/Dashboard");
            html = await Ok(response);
        }
        finally { FakeApi.Populated = false; }

        // The policy the browser will enforce: self-hosted styles only, so an
        // inline width attribute would be dropped and the bars would be empty.
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("style-src 'self'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);

        // Nothing on the page carries an inline style, the bars least of all.
        Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"bar-fill\"></span>", html, StringComparison.Ordinal);

        // The percentage still drives the width: each fed figure renders as
        // its own class (50% → w50, 37.5% → w38, 58.3% → w58, 4.2% → w4).
        foreach (var expected in new[] { "bar-fill--w50", "bar-fill--w38", "bar-fill--w13", "bar-fill--w58", "bar-fill--w42", "bar-fill--w67", "bar-fill--w4" })
        {
            Assert.Contains($"class=\"bar-fill {expected}\"", html, StringComparison.Ordinal);
        }

        // Every class the page asked for is defined in the served stylesheet.
        var css = await Ok(await Client().GetAsync("/css/site.css"));
        foreach (var used in new[] { "bar-fill--w50", "bar-fill--w38", "bar-fill--w13", "bar-fill--w58", "bar-fill--w42", "bar-fill--w67", "bar-fill--w4" })
        {
            Assert.Contains($".{used} {{ width: ", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NewTicket_StepOne_RendersTheStepperAndTheSearchForm()
    {
        var html = await Ok(await Client().GetAsync("/NewTicket"));

        Assert.Contains("<h1 class=\"page-title\">New Ticket</h1>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"wizard-stepper\"", html, StringComparison.Ordinal);
        Assert.Contains("wizard-stepper__item is-current", html, StringComparison.Ordinal);
        foreach (var step in new[] { "Customer", "Property", "Issue", "Review" })
        {
            Assert.Contains($"<span class=\"wizard-stepper__label\">{step}<small>", html, StringComparison.Ordinal);
        }

        Assert.Contains("Step 1 of 4", html, StringComparison.Ordinal);
        Assert.Contains("<h2 class=\"section-card__heading\">Customer</h2>", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Intake.PhoneNumber\"", html, StringComparison.Ordinal);
        Assert.Contains("WhatsApp", html, StringComparison.Ordinal);
        Assert.Contains("Ticket summary", html, StringComparison.Ordinal);
        Assert.Contains("Not selected yet", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Tickets</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administration_Landing_RendersOnTheSharedShell_WithTheSubNavAndModuleCards()
    {
        var html = await Ok(await Client(Roles.SystemAdministrator).GetAsync("/Admin"));

        Assert.Contains("aria-current=\"page\">Administration</a>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-subnav\"", html, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"page-title\">Administration</h1>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-modules\"", html, StringComparison.Ordinal);

        // One card per module, each carrying its own accent — Users blue,
        // Departments teal, Request Types indigo, Workflows purple, Channels
        // green — and the card itself is the link, so no "Manage" button and
        // no identical dark icon box is repeated five times.
        foreach (var tone in new[] { "tone-info", "tone-progress", "tone-secondary", "tone-purple", "tone-success" })
        {
            Assert.Contains($"class=\"admin-card {tone}\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("admin-card__open", html, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-card__cta", html, StringComparison.Ordinal);

        var users = await Ok(await Client(Roles.SystemAdministrator).GetAsync("/Admin/Users"));
        Assert.Contains("<nav class=\"admin-crumbs tone-info\" aria-label=\"Breadcrumb\">", users, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-crumbs__current\">Users</span>", users, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-head__eyebrow\"", users, StringComparison.Ordinal);
        Assert.Contains("class=\"filter-bar\"", users, StringComparison.Ordinal);
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.ContainsKey("X-Test-Anonymous"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var role = Request.Headers.TryGetValue("X-Test-Role", out var header) ? header.ToString() : Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Agent"),
                new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>The channel catalogue always answers; the dashboard overview answers only for a page request that carried X-Test-Populated (the test flips the fake's switch); everything else is unreachable, so each page's error/empty states are exercised.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);
        public static volatile bool Populated;

        /// <summary>When set, the populated overview carries these KPIs instead of the default ones (a switch like <see cref="Populated"/>).</summary>
        public static DashboardKpisDto? KpisOverride;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? body = path switch
            {
                "/api/channels" => new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2) },
                "/api/dashboard/overview" when Populated => Overview(),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private static DashboardOverviewDto Overview()
        {
            var filters = new DashboardAppliedFiltersDto(new DateOnly(2026, 8, 13), new DateOnly(2026, 9, 12), null, null, null, null, null, null);
            DashboardBreakdownItemDto Item(string key, string label, int count, double pct) => new(key, label, count, pct);
            return new DashboardOverviewDto(
                filters,
                KpisOverride ?? new DashboardKpisDto(42, 6, 11, 3, 4, 2),
                120,
                [Item("3", "WhatsApp", 60, 50), Item("1", "Phone", 45, 37.5), Item("4", "Social Media", 15, 12.5)],
                [Item("9", "Payment Reminder", 70, 58.3), Item("10", "Complaint", 50, 41.7)],
                [Item("2", "Customer Service", 80, 66.7), Item("3", "Collections", 40, 33.3)],
                [Item("Open", "Open", 30, 25), Item("InProgress", "In Progress", 50, 41.7), Item("Resolved", "Resolved", 40, 33.3)],
                [Item("1", "Critical", 5, 4.2), Item("2", "High", 25, 20.8), Item("3", "Medium", 60, 50), Item("4", "Low", 30, 25)],
                [Item("UnderOneDay", "< 1 day", 10, 23.8), Item("OneToThreeDays", "1–3 days", 20, 47.6), Item("OverSevenDays", "> 7 days", 12, 28.6)],
                [
                    new DashboardRecentTicketDto(5, "TG-CS-00005", "Mariam Al Falasi", "T-1204", "Tiger Tower", 2, "Customer Service", 9, "Payment Reminder", 1, "Open", "Breached", Now.AddHours(-5), null, null, Now.AddDays(-2)),
                    new DashboardRecentTicketDto(6, "TG-CS-00006", "Omar Haddad", "P-08", "Palm Residence", 3, "Collections", 10, "Complaint", 2, "InProgress", "Running", Now.AddHours(3), ViewerId, "Test Agent", Now.AddHours(-20)),
                ],
                new DashboardFilterOptionsDto(
                    [new DashboardFilterOptionDto("2", "Customer Service"), new DashboardFilterOptionDto("3", "Collections")],
                    [new DashboardFilterOptionDto(ViewerId.ToString(), "Test Agent")],
                    [new DashboardFilterOptionDto("3", "WhatsApp"), new DashboardFilterOptionDto("1", "Phone")],
                    [new DashboardFilterOptionDto("9", "Payment Reminder", 2), new DashboardFilterOptionDto("10", "Complaint", 3)],
                    [new DashboardFilterOptionDto("Open", "Open"), new DashboardFilterOptionDto("InProgress", "In Progress")],
                    [new DashboardFilterOptionDto("1", "Critical"), new DashboardFilterOptionDto("2", "High")]));
        }
    }
}
