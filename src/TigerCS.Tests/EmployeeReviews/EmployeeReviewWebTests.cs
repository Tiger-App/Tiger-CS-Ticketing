// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TabletText = TigerCsWeb::TigerCS.Web.Services.EmployeeReviews.TabletText;

namespace TigerCS.Tests.EmployeeReviews;

/// <summary>
/// The Web pages end to end (real Razor pipeline, fake Api): the kiosk tablet in English and Arabic, activation, the submit handler's
/// status mapping, the dashboard and the administration page. The Api's own behaviour is covered by the endpoint tests.
/// </summary>
public sealed class EmployeeReviewWebTests : IDisposable
{
    private static readonly string GoodToken = "tgr-" + new string('G', 43);
    private static readonly string InactiveToken = "tgr-" + new string('I', 43);
    private static readonly string RevokedToken = "tgr-" + new string('R', 43);
    private static readonly string DownToken = "tgr-" + new string('D', 43);

    // Activation codes the fake Api understands (each is a well-formed 8-character code).
    private const string GoodCode = "K7K7K7K7";
    private const string UsedCode = "CCCCCCCC";
    private const string InactiveCode = "HHHHHHHH";
    private const string LockedCode = "MMMMMMMM";
    private const string DownCode = "NNNNNNNN";
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly Guid AmalId = Guid.NewGuid();

    private readonly List<WebApplicationFactory<TigerCsWeb::Program>> _factories = [];

    public void Dispose() => _factories.ForEach(f => f.Dispose());

    private HttpClient Client(string role = Roles.CsManager, FakeApi? api = null)
    {
        var fake = api ?? new FakeApi(role);
        var factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = fake));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; });
            });
        });
        _factories.Add(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Test-Roles", role);
        return client;
    }

    /// <summary>A tablet browser: no sign-in at all, only (optionally) its own cookie.</summary>
    private HttpClient Kiosk(string? tokenCookie = null, string? languageCookie = null, FakeApi? api = null)
    {
        var client = Client(api: api ?? new FakeApi(Roles.CsAgent));
        client.DefaultRequestHeaders.Remove("X-Test-Roles");
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "1");
        var cookies = new List<string>();
        if (tokenCookie is not null) cookies.Add($"tigercs_review_tablet={tokenCookie}");
        if (languageCookie is not null) cookies.Add($"tigercs_tablet_lang={languageCookie}");
        if (cookies.Count > 0) client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        return client;
    }

    private static string Dec(string html) => WebUtility.HtmlDecode(html);

    // ---------------------------------------------------------------- the tablet page

    [Fact]
    public async Task Tablet_IsReachableWithoutLogin_ShowsTheEmployeeName_TheQuestion_FiveStars_AndNoSensitiveIds()
    {
        var response = await Kiosk(GoodToken).GetAsync("/EmployeeReviews/Tablet");
        var html = Dec(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-employee-name>Amal Agent<", html);
        Assert.Contains("How was your experience?", html);
        Assert.Equal(5, Regex.Matches(html, "data-star=\"[1-5]\"").Count);
        Assert.Contains("maxlength=\"1000\"", html);
        Assert.Contains("tiger-logo.png", html);
        Assert.Contains("name=\"viewport\"", html);

        // Nothing identifying travels in the page: no employee id, device id or token.
        Assert.DoesNotContain(AmalId.ToString(), html);
        Assert.DoesNotContain(GoodToken, html);
        Assert.DoesNotContain("employeeId", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Tablet_DefaultsToEnglishLeftToRight()
    {
        var html = Dec(await (await Kiosk(GoodToken).GetAsync("/EmployeeReviews/Tablet")).Content.ReadAsStringAsync());

        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", html);
        Assert.Contains(">Submit Feedback<", html);
        Assert.Contains("Comments (optional)", html);
    }

    [Fact]
    public async Task Tablet_InArabic_IsRightToLeft_WithArabicText_FromTheQueryOrTheRememberedChoice()
    {
        foreach (var (path, language) in new[] { ("/EmployeeReviews/Tablet?lang=ar", (string?)null), ("/EmployeeReviews/Tablet", "ar") })
        {
            var html = Dec(await (await Kiosk(GoodToken, language).GetAsync(path)).Content.ReadAsStringAsync());

            Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", html);
            Assert.Contains("كيف كانت تجربتك؟", html);
            Assert.Contains(">إرسال التقييم<", html);
            Assert.Contains("ملاحظات (اختياري)", html);
            Assert.DoesNotContain("How was your experience", html.Replace("data-en=\"How was your experience?\"", ""));
        }
    }

    [Fact]
    public async Task Tablet_CarriesBothLanguagesOnEveryElement_SoTheToggleSwitchesWithoutANetworkCall()
    {
        var html = Dec(await (await Kiosk(GoodToken).GetAsync("/EmployeeReviews/Tablet")).Content.ReadAsStringAsync());

        Assert.Contains("data-lang-toggle", html);
        Assert.Contains("data-i18n=\"question\" data-en=\"How was your experience?\" data-ar=\"كيف كانت تجربتك؟\"", html);
        Assert.Contains("data-placeholder-ar=", html);
        // The employee's name is data, not a translatable string.
        Assert.DoesNotContain("data-i18n=\"employee", html);
    }

    [Fact]
    public async Task Tablet_ThanksScreenIsPartOfThePage_AndTheScriptResetsItAfterFiveSeconds()
    {
        var html = await (await Kiosk(GoodToken).GetAsync("/EmployeeReviews/Tablet")).Content.ReadAsStringAsync();
        Assert.Contains("data-thanks-ms=\"5000\"", html);
        Assert.Contains("data-view=\"thanks\" hidden", html);

        var script = await ReadWebFileAsync("wwwroot/js/tablet.js");
        Assert.Contains("data-thanks-ms", script);
        Assert.Contains("setTimeout(resetForm, thanksMs)", script);
    }

    [Fact]
    public async Task TabletScript_StoresAReviewBeforeSending_AndKeepsItUntilTheServerConfirms()
    {
        var script = await ReadWebFileAsync("wwwroot/js/tablet.js");

        var stored = script.IndexOf("saveQueue(loadQueue().concat([item]))", StringComparison.Ordinal);
        var sent = script.IndexOf("flush().then", StringComparison.Ordinal);
        Assert.True(stored > 0 && stored < sent, "the review must be queued before the first send");
        Assert.Contains("removeFromQueue(queue[0].id)", script);          // removed only inside the 'saved'/'rejected' branch
        Assert.Contains("window.setInterval(flush, RETRY_MS)", script);   // retried on a timer
        Assert.Contains("addEventListener('online', flush)", script);     // and when the connection returns
        Assert.Contains("submissionId: item.id", script);                 // same id on every retry
    }

    [Fact]
    public async Task Tablet_WithoutACookie_SendsTheDeviceToActivation()
    {
        var response = await Kiosk().GetAsync("/EmployeeReviews/Tablet");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/EmployeeReviews/Activate", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Tablet_WithARevokedCredential_ForgetsItAndReturnsToActivation()
    {
        var response = await Kiosk(RevokedToken).GetAsync("/EmployeeReviews/Tablet");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/EmployeeReviews/Activate", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("tigercs_review_tablet=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tablet_WhenTheTabletIsInactive_ShowsANoticeInsteadOfTheReviewForm_AndLooksAgainLater()
    {
        var html = Dec(await (await Kiosk(InactiveToken).GetAsync("/EmployeeReviews/Tablet")).Content.ReadAsStringAsync());

        Assert.Contains("data-notice=\"inactive\"", html);
        Assert.DoesNotContain("data-star=", html);
        Assert.Contains("data-auto-reload=\"30000\"", html);
    }

    [Fact]
    public async Task Tablet_WhenTheApiIsDown_ShowsAnUnavailableNoticeThatRetries()
    {
        var html = Dec(await (await Kiosk(DownToken).GetAsync("/EmployeeReviews/Tablet")).Content.ReadAsStringAsync());

        Assert.Contains("data-notice=\"unavailable\"", html);
        Assert.Contains("data-auto-reload=\"10000\"", html);
    }

    // ---------------------------------------------------------------- the submit handler

    private async Task<(HttpStatusCode Status, string Body, FakeApi Api)> SubmitAsync(string? tokenCookie, object payload)
    {
        var api = new FakeApi(Roles.CsAgent);
        var kiosk = Kiosk(tokenCookie, api: api);
        var page = await kiosk.GetAsync("/EmployeeReviews/Tablet");
        var html = await page.Content.ReadAsStringAsync();
        var antiforgery = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var cookieHeader = string.Join("; ", page.Headers.TryGetValues("Set-Cookie", out var set) ? set.Select(c => c.Split(';')[0]) : []);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/EmployeeReviews/Tablet?handler=Submit") { Content = JsonContent.Create(payload) };
        request.Headers.Add("RequestVerificationToken", antiforgery);
        request.Headers.Add("Cookie", string.Join("; ", new[] { cookieHeader, $"tigercs_review_tablet={tokenCookie}" }.Where(c => c.Length > 0 && !c.EndsWith('='))));
        request.Headers.Add("X-Test-Anonymous", "1");
        var response = await kiosk.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), api);
    }

    [Fact]
    public async Task Submit_ForwardsTheCookiesTokenAsAHeader_AndOnlyTheReviewFields()
    {
        var id = Guid.NewGuid();

        var (status, body, api) = await SubmitAsync(GoodToken, new { submissionId = id, rating = 5, comment = "Great" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"saved\"", body);
        var sent = Assert.Single(api.Submissions);
        Assert.Equal(GoodToken, sent.Token);
        Assert.Contains(id.ToString(), sent.Body);
        Assert.DoesNotContain("employee", sent.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_IgnoresAnEmployeeOrDeviceIdTheBrowserAddsToThePayload()
    {
        var (_, _, api) = await SubmitAsync(GoodToken, new { submissionId = Guid.NewGuid(), rating = 4, employeeId = Guid.NewGuid(), deviceId = Guid.NewGuid() });

        var sent = Assert.Single(api.Submissions);
        Assert.DoesNotContain("employeeId", sent.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", sent.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rejected-token", HttpStatusCode.UnprocessableEntity, "rejected")]
    [InlineData("inactive-token", HttpStatusCode.Forbidden, "inactive")]
    [InlineData("revoked-token", HttpStatusCode.Unauthorized, "invalid-device")]
    [InlineData("down-token", HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task Submit_MapsTheApisAnswer_ToAStatusTheScriptActsOn(string which, HttpStatusCode expectedStatus, string expectedBody)
    {
        var token = which switch
        {
            "rejected-token" => "tgr-" + new string('J', 43),
            "inactive-token" => "tgr-" + new string('I', 43),
            "revoked-token" => "tgr-" + new string('R', 43),
            _ => "tgr-" + new string('N', 43)
        };

        var api = new FakeApi(Roles.CsAgent);
        var kiosk = Kiosk(GoodToken, api: api);
        var page = await kiosk.GetAsync("/EmployeeReviews/Tablet");
        var html = await page.Content.ReadAsStringAsync();
        var antiforgery = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var cookies = string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Append("tigercs_review_tablet=" + token));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/EmployeeReviews/Tablet?handler=Submit") { Content = JsonContent.Create(new { submissionId = Guid.NewGuid(), rating = 3 }) };
        request.Headers.Add("RequestVerificationToken", antiforgery);
        request.Headers.Add("Cookie", cookies);
        request.Headers.Add("X-Test-Anonymous", "1");

        var response = await kiosk.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Contains(expectedBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submit_WithoutTheAntiForgeryToken_IsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/EmployeeReviews/Tablet?handler=Submit") { Content = JsonContent.Create(new { submissionId = Guid.NewGuid(), rating = 5 }) };
        request.Headers.Add("Cookie", $"tigercs_review_tablet={GoodToken}");

        var response = await Kiosk(GoodToken).SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- activation

    private async Task<(HttpResponseMessage Response, FakeApi Api)> PostActivationAsync(string code)
    {
        var api = new FakeApi(Roles.SystemAdministrator);
        var kiosk = Kiosk(api: api);
        var page = await kiosk.GetAsync("/EmployeeReviews/Activate");
        var antiforgery = Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/EmployeeReviews/Activate")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = code, ["__RequestVerificationToken"] = antiforgery })
        };
        request.Headers.Add("Cookie", string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        request.Headers.Add("X-Test-Anonymous", "1");
        return (await kiosk.SendAsync(request), api);
    }

    [Fact]
    public async Task Activate_Page_AsksForAnEightCharacterCode_NotAPassword()
    {
        var html = Dec(await Kiosk().GetStringAsync("/EmployeeReviews/Activate"));

        var input = Regex.Match(html, "<input[^>]*id=\"activation-code\"[^>]*>").Value;
        Assert.Contains("maxlength=\"8\"", input);
        Assert.Contains("minlength=\"8\"", input);
        Assert.Contains("type=\"text\"", input);
        Assert.DoesNotContain("maxlength=\"128\"", input);
    }

    [Theory]
    [InlineData(GoodCode)]
    [InlineData("k7k7k7k7")]   // a tablet keyboard that starts in lower case
    public async Task Activate_IsAnonymous_AndAcceptsAValidCode_StoringTheDeviceTokenNotTheCode_InAnHttpOnlyStrictCookie(string typed)
    {
        var (response, api) = await PostActivationAsync(typed);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/EmployeeReviews/Tablet", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("tigercs_review_tablet=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(GoodToken, cookie);               // the long token the Api minted...
        Assert.DoesNotContain(GoodCode, cookie);          // ...never the short code
        Assert.Equal(GoodCode, Assert.Single(api.ActivationCodes));   // the Api received it normalized
    }

    [Theory]
    [InlineData("not-a-code")]
    [InlineData("tgr-GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")] // an old long token
    [InlineData("K7K7-K7K")]    // hyphen
    [InlineData("K7K7K7K")]     // 7 characters
    [InlineData("K7K7K7K7K")]   // 9 characters
    [InlineData("K7K7K7K0")]    // ambiguous character
    public async Task Activate_RejectsAMalformedCode_WithoutCallingTheApi_OrSettingAnyCookie(string code)
    {
        var (response, api) = await PostActivationAsync(code);
        var html = Dec(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TabletText.Get("activate-invalid", "en"), html);
        Assert.Empty(api.ActivationCodes);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], c => c.StartsWith("tigercs_review_tablet=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UsedCode, "activate-invalid")]          // wrong, expired or already used: one message for all
    [InlineData(InactiveCode, "activate-inactive")]
    [InlineData(LockedCode, "activate-too-many")]
    [InlineData(DownCode, "activate-unavailable")]
    public async Task Activate_ShowsTheApisRefusal_WithoutSettingAnyCookie(string code, string expectedText)
    {
        var (response, api) = await PostActivationAsync(code);
        var html = Dec(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TabletText.Get(expectedText, "en"), html);
        Assert.Equal(code, Assert.Single(api.ActivationCodes));
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], c => c.StartsWith("tigercs_review_tablet=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminPage_OffersAnActivatedTabletOnlyAConfirmedReactivation()
    {
        var html = Dec(await Client(Roles.SystemAdministrator).GetStringAsync("/Admin/EmployeeReviewDevices"));

        Assert.Contains("data-reactivate", html);
        Assert.Contains("Re-activate", html);
        Assert.Contains("switches it off immediately", html);
        Assert.Matches("<input[^>]*type=\"checkbox\"[^>]*name=\"confirmReplace\"[^>]*required|<input[^>]*name=\"confirmReplace\"[^>]*type=\"checkbox\"[^>]*required", html);
        Assert.DoesNotContain(">New activation code<", html);   // no one-click replacement for a tablet that is working
    }

    // ---------------------------------------------------------------- the dashboard and the admin page

    [Fact]
    public async Task Dashboard_Renders_TotalAverageDistributionEmployeesAndComments_WithUaeTimes()
    {
        var html = Dec(await Client().GetStringAsync("/EmployeeReviews"));

        Assert.Contains("data-kpi=\"total\"><span class=\"er-kpi__value\">12<", html);
        Assert.Contains("4.25", html);
        Assert.Equal(5, Regex.Matches(html, "data-star-row=\"").Count);
        Assert.Contains("data-table=\"employees\"", html);
        Assert.Contains($"/EmployeeReviews/Employee/{AmalId}", html);
        Assert.Contains("Customer Service", html);
        Assert.Contains("Quick and kind", html);
        Assert.Contains("10 Sep 2026, 00:30", html);   // 9 Sep 20:30 UTC shown as UAE time
        foreach (var label in new[] { "Date from", "Date to", "Employee", "Department", "Stars" })
        {
            Assert.Contains(label, html);
        }

        Assert.Contains("href=\"/EmployeeReviews\"", html);   // in the primary navigation
    }

    [Fact]
    public async Task Dashboard_ShowsANoPermissionState_WhenTheApiRefuses()
    {
        var html = Dec(await Client(Roles.ReportingUser).GetStringAsync("/EmployeeReviews"));

        Assert.Contains("You do not have permission to view employee reviews.", html);
        Assert.DoesNotContain("data-kpi=", html);
    }

    [Fact]
    public async Task EmployeeDrillDown_Renders_TheBreakdownAndHistory()
    {
        var html = Dec(await Client().GetStringAsync($"/EmployeeReviews/Employee/{AmalId}"));

        Assert.Contains("Amal Agent", html);
        Assert.Contains("data-table=\"history\"", html);
        Assert.Contains("Rating breakdown", html);
    }

    [Fact]
    public async Task AdminPage_ListsTablets_AndRegistersWithEligibleEmployees_ForTheSystemAdministratorOnly()
    {
        var html = Dec(await Client(Roles.SystemAdministrator).GetStringAsync("/Admin/EmployeeReviewDevices"));

        Assert.Contains("Employee Review Devices", html);
        Assert.Contains("data-device=\"Reception desk\"", html);
        Assert.Contains("Amal Agent", html);
        Assert.Contains("Register tablet", html);
        Assert.DoesNotContain(GoodToken, html);

        var denied = await Client(Roles.CsManager).GetAsync("/Admin/EmployeeReviewDevices");
        Assert.True(denied.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden, denied.StatusCode.ToString());
    }

    // ---------------------------------------------------------------- translation table

    [Fact]
    public void EveryTabletText_HasADistinctEnglishAndArabicVersion()
    {
        foreach (var (key, (en, ar)) in TabletText.Texts)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(ar), key);
            if (key is not "language-toggle")
            {
                Assert.Matches("[؀-ۿ]", ar);               // genuinely Arabic
            }

            Assert.NotEqual(en, ar);
        }
    }

    [Theory]
    [InlineData("ar", "ar", "rtl")]
    [InlineData("AR", "ar", "rtl")]
    [InlineData("en", "en", "ltr")]
    [InlineData("fr", "en", "ltr")]
    [InlineData(null, "en", "ltr")]
    [InlineData("", "en", "ltr")]
    public void Language_IsNormalised_AndEveryUnknownValueFallsBackToEnglish(string? requested, string language, string direction)
    {
        Assert.Equal(language, TabletText.Normalize(requested));
        Assert.Equal(direction, TabletText.Direction(requested ?? ""));
    }

    // ---------------------------------------------------------------- plumbing

    private static async Task<string> ReadWebFileAsync(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "TigerCS.Web", relative)))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return await File.ReadAllTextAsync(Path.Combine(dir!, "TigerCS.Web", relative));
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

            var role = Request.Headers["X-Test-Roles"].FirstOrDefault() ?? Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()), new Claim(ClaimTypes.Name, "Test Viewer"), new Claim(ClaimTypes.Role, role)
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeApi(string role) : HttpMessageHandler
    {
        public List<(string Token, string Body)> Submissions { get; } = [];

        public List<string> ActivationCodes { get; } = [];

        private static readonly EmployeeReviewFiltersDto Filters = new(
            new DateOnly(2026, 8, 12), new DateOnly(2026, 9, 10), null, null, null,
            [new ReviewEmployeeOptionDto(AmalId, "Amal Agent")], [new ReviewOptionDto(1, "Customer Service")]);

        private static readonly EmployeeReviewSummaryDto Summary = new(12, 4.25,
            [new StarCountDto(5, 6), new StarCountDto(4, 3), new StarCountDto(3, 2), new StarCountDto(2, 1), new StarCountDto(1, 0)]);

        private static readonly EmployeeReviewEntryDto Entry = new(
            Guid.NewGuid(), AmalId, "Amal Agent", "Customer Service", 5, "Quick and kind",
            new DateTime(2026, 9, 9, 20, 30, 0, DateTimeKind.Utc), new DateTime(2026, 9, 10, 0, 30, 0));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.TryGetValues("X-Device-Token", out var values) ? values.First() : "";

            if (path == "/api/public/employee-reviews/session")
            {
                if (token == new string('D', 43).Insert(0, "tgr-"))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                }

                var session = token == GoodToken ? new TabletSessionDto(TabletSessionStatus.Ready, new TabletInfoDto("Reception desk", "Amal Agent"))
                    : token == InactiveToken ? new TabletSessionDto(TabletSessionStatus.Inactive)
                    : new TabletSessionDto(TabletSessionStatus.Invalid);
                return Json(session);
            }

            if (path == "/api/public/employee-reviews/activate")
            {
                var code = (await request.Content!.ReadFromJsonAsync<ActivateTabletRequestDto>(cancellationToken))!.Code!;
                ActivationCodes.Add(code);
                return code switch
                {
                    GoodCode => Json(new ActivateTabletResultDto(ActivateTabletOutcome.Activated, GoodToken)),
                    InactiveCode => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new ActivateTabletResultDto(ActivateTabletOutcome.Inactive)) },
                    LockedCode => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                    DownCode => new HttpResponseMessage(HttpStatusCode.BadGateway),
                    _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new ActivateTabletResultDto(ActivateTabletOutcome.InvalidCode)) }
                };
            }

            if (path == "/api/public/employee-reviews/submit")
            {
                Submissions.Add((token, await request.Content!.ReadAsStringAsync(cancellationToken)));
                return token[4] switch
                {
                    'J' => new HttpResponseMessage(HttpStatusCode.BadRequest),
                    'I' => new HttpResponseMessage(HttpStatusCode.Forbidden),
                    'R' => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                    'G' => Json(new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.Submitted)),
                    _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                };
            }

            if (path == "/api/users/me")
            {
                return Json(new CurrentUserResponseDto(ViewerId, "Test Viewer", [role], [new DepartmentMembershipDto(1, "Customer Service", true)], false));
            }

            if (path == "/api/employee-reviews")
            {
                return role == Roles.ReportingUser
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : Json(new EmployeeReviewsOverviewDto(
                        Filters, Summary,
                        [new EmployeeReviewEmployeeRowDto(AmalId, "Amal Agent", "Customer Service", 12, 4.25, Summary.Distribution)],
                        [Entry]));
            }

            if (path == $"/api/employee-reviews/employees/{AmalId}")
            {
                return Json(new EmployeeReviewDetailDto(Filters, AmalId, "Amal Agent", "Customer Service", Summary, [Entry]));
            }

            if (path == "/api/admin/employee-review-devices")
            {
                return Json(new[]
                {
                    new EmployeeReviewDeviceDto(Guid.NewGuid(), "Reception desk", AmalId, "Amal Agent", "Customer Service", true, true, true,
                        new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), null)
                });
            }

            if (path == "/api/admin/employee-review-devices/eligible-employees")
            {
                return Json(new[] { new EligibleReviewEmployeeDto(AmalId, "Amal Agent", "Customer Service") });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, body!.GetType()) };
    }
}
