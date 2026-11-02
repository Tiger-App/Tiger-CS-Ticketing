// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Customer Profile's Payment tab through the real Razor pipeline against
/// a fake TigerCS.Api: every state the specification names (loading; no
/// linked account; account selection required; current account with zero
/// balance; unavailable with Retry; stale with timestamp; forbidden), the
/// lazily fetched panel, and Send Reminder from a candidate.
/// </summary>
public sealed class CustomerPaymentTabRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly FakeApi _api = new();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public CustomerPaymentTabRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = _api));
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

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> Ok(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
        return html;
    }

    [Fact]
    public async Task PaymentsBeforeTicket_RenderThroughRazorWithoutReadingAnyCustomerProfile_AndKeepSelectorContext()
    {
        var html = await Ok(await Client().GetAsync("/Customers/Payments?phoneNumber=971500000002&customerKey=crm%3A9001&account=25"));
        Assert.Contains("Payments &amp; Fines", html, StringComparison.Ordinal);
        Assert.Contains("Not computed", html, StringComparison.Ordinal);
        Assert.Contains("Cheque", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/Customers/Payments\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"phoneNumber\" value=\"971500000002\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"customerKey\" value=\"crm:9001\"", html, StringComparison.Ordinal);
        Assert.Contains("Show</button>", html, StringComparison.Ordinal);
        // The unit-based lookup of the CRM customer (no profile read), then the verified EDSM lookup, and nothing else.
        // (The navigation's own GET /api/users/me - the viewer's permission overrides - is not a customer read.)
        var customerRequests = _api.Requests.Where(r => !r.StartsWith("/api/users/me", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, customerRequests.Count);
        Assert.StartsWith("/api/collections/customers/crm/9001/units?", customerRequests[0], StringComparison.Ordinal);
        Assert.Contains("/api/collections/customer-lookup/payment-summary?", customerRequests[1], StringComparison.Ordinal);
        Assert.Contains("customerKey=crm%3A9001", customerRequests[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("forbidden", "You don't have permission")]
    [InlineData("unavailable", "Balance unavailable.")]
    public async Task PreTicketPaymentErrors_ShowNoAmounts(string mode, string expected)
    {
        _api.Mode = mode;
        var html = await Ok(await Client().GetAsync("/Customers/Payments?phoneNumber=971500000002&customerKey=ext%3APact%3A3001"));
        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.DoesNotContain("AED 0.00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-field=", html, StringComparison.Ordinal);
    }

    private bool CollectionsCalled => _api.Requests.Any(r => r.Contains("/api/collections", StringComparison.Ordinal));

    [Fact]
    public async Task ThePaymentTab_IsDeferredWithALoadingState_AndTheProfileCallsNoFinancialRoute()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001"));

        Assert.Contains("<label class=\"tab-label\" for=\"tab-payment\">Payment</label>", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-src=\"/Customers/crm%3A9001?handler=PaymentPanel\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-state=\"Deferred\"", html, StringComparison.Ordinal);
        Assert.Contains("Loading payment details…", html, StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
        Assert.DoesNotContain("/api/genesys", string.Join("\n", _api.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralAccounts_RequireASelection_BeforeAnyDetail()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("data-payment-state=\"SelectAccount\"", html, StringComparison.Ordinal);
        Assert.Contains("This customer has 2 finance accounts.", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-account-link=\"ACC-45001\"", html, StringComparison.Ordinal);
        Assert.Contains("AED 30,500.00 <small>due now</small>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Instalments", html, StringComparison.Ordinal);
        Assert.DoesNotContain("view=instalments", string.Join("\n", _api.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelectedAccount_ShowsTheSourceFigures_InstalmentsHistoryAndReminders()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        Assert.Matches(new Regex("id=\"tab-payment\"[^>]*checked"), html);
        Assert.Contains("data-payment-state=\"Loaded\"", html, StringComparison.Ordinal);
        Assert.Contains("AED 30,500.00", html, StringComparison.Ordinal);       // amount due now
        Assert.Contains("AED 45,000.00", html, StringComparison.Ordinal);       // remaining principal
        Assert.Contains("Payable penalties", html, StringComparison.Ordinal);
        Assert.Contains("overlaps the overdue, due-today and future amounts", html, StringComparison.Ordinal);
        Assert.Contains("Last updated", html, StringComparison.Ordinal);
        Assert.Contains("Currency <strong>AED</strong>", html, StringComparison.Ordinal);

        Assert.Contains(">Partially paid<", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"badge badge-pay-critical\">Overdue</span>", html, StringComparison.Ordinal); // partially paid AND overdue
        Assert.Contains("RCT-70001", html, StringComparison.Ordinal);
        Assert.Contains("INST-MAY-2026", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets/77\">TG-COL-00077</a>", html, StringComparison.Ordinal);
        Assert.Contains("data-documents-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Download", html, StringComparison.Ordinal);

        var requests = string.Join("\n", _api.Requests);
        Assert.Contains("/api/collections/customers/9001/payments?accountId=ACC-45001&view=instalments", requests, StringComparison.Ordinal);
        Assert.Contains("/api/collections/customers/9001/payments?accountId=ACC-45001&view=history", requests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_ShowsTheCandidatesAmountAccountAndChannels_ForAPermittedViewer()
    {
        _api.CanSend = true;
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        Assert.Contains(">Send Reminder</button>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>AED 30,000.00</strong>", html, StringComparison.Ordinal);
        Assert.Contains("unpaid principal older than one calendar month", html, StringComparison.Ordinal);
        Assert.Contains("name=\"channels\" value=\"Email\" checked", html, StringComparison.Ordinal);
        Assert.Contains("name=\"channels\" value=\"Sms\" checked", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"VoiceBot\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"idempotencyKey\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_IsHidden_WithoutTheGrant_OnStaleData_AndWhenNothingIsDue()
    {
        _api.CanSend = false;
        Assert.DoesNotContain("Send Reminder", await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001")), StringComparison.Ordinal);

        _api.CanSend = true;
        _api.Stale = true;
        var stale = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));
        Assert.DoesNotContain(">Send Reminder</button>", stale, StringComparison.Ordinal);
        Assert.Contains("data-balance-stale", stale, StringComparison.Ordinal);
        Assert.Contains("Reminders cannot be sent on stale figures.", stale, StringComparison.Ordinal);

        _api.Stale = false;
        var settled = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45002"));
        Assert.Contains("data-balance-settled", settled, StringComparison.Ordinal);
        Assert.Contains("data-no-candidate", settled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_ShowsNoFigures()
    {
        _api.Mode = "forbidden";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("You don't have permission to view this customer's payment information.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AED", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_SaysSoWithRetry_NeverZero_AndKeepsReminderHistory()
    {
        _api.Mode = "unavailable";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("Balance unavailable.", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-retry>Retry</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AED 0.00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Amount due now", html, StringComparison.Ordinal);
        Assert.Contains("TG-COL-00077", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_NoLinkedAccount_AndAPhoneOnlyCaller_EachSaySo()
    {
        _api.Mode = "disabled";
        Assert.Contains("Collections is not enabled in this environment", await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment")), StringComparison.Ordinal);

        _api.Mode = "notfound";
        Assert.Contains("No finance account is linked to this customer.", await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment")), StringComparison.Ordinal);

        _api.Mode = "loaded";
        _api.Requests.Clear();
        Assert.Contains("Payment information is available only for customers identified in Tiger CRM or PACT.",
            await Ok(await Client().GetAsync("/Customers/phone:%2B971501112222?tab=payment")), StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
    }

    [Fact]
    public async Task ThePanelHandler_ReturnsTheTabAlone_ForTheRequestedAccount()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?handler=PaymentPanel&account=ACC-45001"));

        Assert.StartsWith("<div class=\"payment-tab\" data-payment-state=\"Loaded\" data-payment-account=\"ACC-45001\"", html.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("<html", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_QueuesTheCandidate_WithTheFormsIdempotencyKey_AndReturnsToTheTab()
    {
        _api.CanSend = true;
        var client = Client();
        var page = await Ok(await client.GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));
        string Field(string name) => Regex.Match(page, $"name=\"{name}\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var post = await client.PostAsync("/Customers/crm:9001?handler=SendReminder", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value),
            new("candidateId", Field("candidateId")),
            new("accountId", "ACC-45001"),
            new("idempotencyKey", Field("idempotencyKey")),
            new("channels", "Email"),
            new("channels", "Sms"),
            new("language", "en"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Customers/crm%3A9001?tab=payment&account=ACC-45001#payment", post.Headers.Location!.OriginalString);

        var sent = JsonSerializer.Deserialize<QueueCollectionsReminderRequestDto>(_api.LastPostBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("CAND-1", sent.CandidateId);
        Assert.Equal(["Email", "Sms"], sent.Channels);
        Assert.Equal(Field("idempotencyKey"), _api.LastIdempotencyKey);

        var back = await Ok(await client.GetAsync(post.Headers.Location));
        Assert.Contains("Reminder REM-90001 queued for AED 30,000.00 on Email, SMS. Queued does not mean delivered.", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedCandidate_IsReportedToTheAgent()
    {
        _api.CanSend = true;
        _api.QueueAnswer = HttpStatusCode.Conflict;
        var client = Client();
        var page = await Ok(await client.GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        var post = await client.PostAsync("/Customers/crm:9001?handler=SendReminder", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value),
            new("candidateId", "CAND-1"), new("accountId", "ACC-45001"), new("channels", "Email"),
        ]));
        var back = await Ok(await client.GetAsync(post.Headers.Location));

        Assert.Contains("The amount or eligibility changed since this reminder was offered.", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChargesAndCreditsTheSourceDidNotReport_SaySo_WhilePrincipalIsShown()
    {
        _api.ChargesUnreported = true;
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        Assert.Contains("data-payment-state=\"Loaded\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-balance-unavailable", html, StringComparison.Ordinal);
        Assert.Contains("AED 45,000.00", html, StringComparison.Ordinal);                  // principal still shown
        Assert.Contains("<dt>Payable penalties</dt><dd>Not provided by source</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Applied credit</dt><dd>Not provided by source</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Amount due now</dt><dd>Not provided by source</dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Payable fees</dt><dd>AED 0.00</dd>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiteCssAndJs_AreVersioned_SoADeploymentNeverLeavesAStaleStylesheetHidingThePanel()
    {
        // Root cause of the "empty Payment panel" report: an unversioned, heuristically cached
        // site.css from before the tab existed has no #panel-payment display rule.
        var profile = await Ok(await Client().GetAsync("/Customers/crm:9001"));
        var login = await Ok(await _factory.CreateClient().GetAsync("/Login"));

        foreach (var html in new[] { profile, login })
        {
            Assert.Matches(new Regex("href=\"/css/site\\.css\\?v=[A-Za-z0-9_-]{20,}\""), html);
            Assert.Matches(new Regex("src=\"/js/site\\.js\\?v=[A-Za-z0-9_-]{20,}\""), html);
        }
    }

    [Fact]
    public async Task ACrmCustomerWithNoFinancialSource_GetsTheExplicitNotMappedMessage_NotAnEmptyOrTemporaryState()
    {
        _api.Mode = "nosource";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?handler=PaymentPanel"));

        Assert.Contains("data-payment-state=\"NotMapped\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-not-mapped", html, StringComparison.Ordinal);
        Assert.Contains("<strong>No payment figures for this customer.</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("no verified mapping from a CRM customer to a PACT tenant exists", html, StringComparison.Ordinal);
        Assert.DoesNotContain("temporarily unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-balance-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("due now", html, StringComparison.Ordinal);
        Assert.Contains("Reminder history", html, StringComparison.Ordinal);   // TigerCS's own record is still shown
        Assert.Contains("/payment-summary", string.Join("\n", _api.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APactCustomer_IsDeferred_NotRefusedAsNonCrm()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001"));

        Assert.Contains("data-payment-state=\"Deferred\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-payment-state=\"NotCrmCustomer\"", html, StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
    }

    [Fact]
    public async Task APactCustomer_ShowsTheSelectedCompanysCardsAndTables_NeverZeroForMissingValues()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?tab=payment"));
        var requests = string.Join("\n", _api.Requests);

        Assert.Contains("data-payment-state=\"EdsmSummary\"", html, StringComparison.Ordinal);
        Assert.Contains("/api/collections/customers/by-key/ext%3APact%3A3001/payment-summary", requests, StringComparison.Ordinal);

        // Compact selector: one option per verified company + tenant pair; the first is shown.
        Assert.Contains("id=\"paymentAccount\"", html, StringComparison.Ordinal);
        Assert.Contains(">Tiger Group Dubai &#xB7; owned &#xB7; tenant 3001</option>", html, StringComparison.Ordinal);
        Assert.Contains(">Hirmas Dubai &#xB7; rented &#xB7; tenant 3001</option>", html, StringComparison.Ordinal);
        Assert.Contains("data-edsm-company=\"4\" data-edsm-model=\"Owned\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-company=\"25\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-account=\"4\"", html, StringComparison.Ordinal);
        Assert.Contains("data-edsm-contracts", html, StringComparison.Ordinal);
        Assert.Contains("0304 &#xB7; Tiger Marina Residences (88001)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("One figure set for this company and tenant", html, StringComparison.Ordinal);

        // Amount cards: EDSM's own strings with the configured currency; unavailable values get a short label.
        Assert.Contains("<strong>AED 1,250,000.00</strong>", html, StringComparison.Ordinal);
        Assert.Matches(new Regex("data-edsm-field=\"paidAmount\" data-edsm-amount-status=\"Missing\">\\s*<strong>Not provided</strong>"), html);
        Assert.Contains("data-edsm-field=\"lateFines\" data-edsm-amount-status=\"Empty\"><dt>Late fines</dt><dd>None</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<span>Not yet due</span>", html, StringComparison.Ordinal);
        // Four cards (Due, Paid, Not yet due, Total) and two side-by-side panels, as in the approved design.
        Assert.Equal(4, Regex.Matches(html, "class=\"kpi-card[^\"]*\" data-edsm-field=").Count);
        Assert.Contains("<div class=\"facts-section__title\">Balance</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"facts-section__title\">Late fines and reminders</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">AED 0.00<", html, StringComparison.Ordinal);

        // Data only: no delay notice, definitions, cache or mapping-verification notes, and no field explanations.
        Assert.Contains("data-currency-configured>Currency <strong>AED</strong>", html, StringComparison.Ordinal);
        Assert.Contains("data-retrieved-at>Last updated <strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-source-delay", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Definitions and source details", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-details", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Definition of Total.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("EDSM sent", html, StringComparison.Ordinal);
        Assert.DoesNotContain("EDSM returns no", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-mapping-verified", html, StringComparison.Ordinal);
        Assert.DoesNotContain("caches each read", html, StringComparison.Ordinal);

        // Transactions and EDSM due-installments as tables; Send Reminder present but unavailable.
        // Unified Payment details: Status (source list, badge) and Payment Type (EDSM paymentTypeId) side by side.
        Assert.Contains("data-edsm-payment-details>Payment details</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<th class=\"col-num\">#</th><th>Date</th><th>Status</th><th>Payment Type</th><th class=\"col-num\">Amount</th><th>Cheque Number</th>", html, StringComparison.Ordinal);
        var table = html[html.IndexOf("edsm-payment-details", StringComparison.Ordinal)..html.IndexOf("</table>", html.IndexOf("edsm-payment-details", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Matches(new Regex("<td class=\"col-num\">1</td>\\s*<td>15 Jan 2026</td>\\s*<td><span class=\"badge badge-pay-ok\">Paid</span></td>\\s*<td>Not provided</td>\\s*<td class=\"col-num\">AED 500,000.00</td>"), table);
        Assert.Matches(new Regex("<td class=\"col-num\">2</td>\\s*<td>15 Sep 2026</td>\\s*<td><span class=\"badge badge-pay-critical\">Due</span></td>\\s*<td>Not provided</td>\\s*<td class=\"col-num\">AED 62,500.00</td>\\s*<td class=\"text-mono\">000412</td>"), table);
        Assert.Matches(new Regex("<td class=\"col-num\">3</td>\\s*<td>15 Dec 2026</td>\\s*<td><span class=\"badge badge-pay-pending\">Outstanding</span></td>"), table);
        Assert.DoesNotContain("<td>Cheque</td>", table, StringComparison.Ordinal);   // never inferred from a cheque number
        Assert.Contains("data-edsm-due-installments=\"Available\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("whether a row is still unpaid is not confirmed", html, StringComparison.Ordinal);
        Assert.Contains("data-reminder-unavailable>Not available</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<select class=\"field-select\" id=\"edsmReminderChannel\" disabled aria-disabled=\"true\">", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"btn btn-gold\" disabled aria-disabled=\"true\">Send Reminder</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=SendReminder", html, StringComparison.Ordinal);

        Assert.DoesNotContain("/outstanding", requests, StringComparison.Ordinal);
        Assert.DoesNotContain("/reminders", requests, StringComparison.Ordinal);
        Assert.DoesNotContain("/candidates", requests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APactCustomer_SelectingTheRentedCompany_ShowsItsOwnLabels_WithoutCaveats()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?handler=PaymentPanel&account=25"));

        Assert.Contains("data-edsm-company=\"25\" data-edsm-model=\"Rented\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"25\" selected=\"selected\">", html, StringComparison.Ordinal);
        Assert.Contains("<strong>AED -500.00</strong>", html, StringComparison.Ordinal);        // rented due may be negative
        Assert.Contains("data-edsm-field=\"lateFines\" data-edsm-amount-status=\"Empty\"><dt>Late fines</dt><dd>Not computed</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<span>Post-dated cheques</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Refunds appear in this list as positive payments.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-caveat", html, StringComparison.Ordinal);
        Assert.Matches(new Regex("data-edsm-row-status=\"Paid\" data-edsm-payment-type-id=\"2\">[\\s\\S]*?badge-pay-ok\">Paid</span></td>\\s*<td>Cheque</td>"), html);
        Assert.Matches(new Regex("data-edsm-row-status=\"Due\" data-edsm-payment-type-id=\"3\">[\\s\\S]*?badge-pay-critical\">Due</span></td>\\s*<td>Fees</td>\\s*<td class=\"col-num\">AED 1,000.00</td>"), html);
        Assert.Contains("data-edsm-list-unavailable=\"Outstanding\">Outstanding: not available.</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APactCompanyRefusedByEdsm_ShowsItsErrorAndNoFigures()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?handler=PaymentPanel&account=7"));

        Assert.Contains("data-edsm-company=\"7\" data-edsm-model=\"Rented\" data-edsm-status=\"Unauthorized\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong>EDSM rejected TigerCS&#x27;s credentials.</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("EDSM rejected the configured API key.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-field=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APartialSummary_IsFlaggedAsIncomplete_WithAShortLabel()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?tab=payment"));

        Assert.Contains("data-edsm-incomplete=\"Partial\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Incomplete balance.</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Company 7 (Alsabeel Sharjah): no figures (Unauthorized).", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Company 25 (Hirmas Dubai): Outstanding transactions not read (Unavailable).", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompleteSummary_ShowsNoIncompleteWarning()
    {
        _api.SummaryCompleteness = "Complete";

        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?tab=payment"));

        Assert.Contains("data-payment-state=\"EdsmSummary\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-incomplete", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompanyCutOffByTheDeadline_SaysEdsmDidNotAnswerInTime_AndShowsNoFigures()
    {
        _api.DeadlineCompany = 4;

        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?tab=payment&account=4"));

        Assert.Contains("data-edsm-status=\"DeadlineExceeded\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong>EDSM did not answer in time.</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No figures are shown for this account.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("60 s deadline", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-field=", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">AED 0.00<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APactCustomer_WhenTheSummaryIsUnavailable_ShowsNoFigures()
    {
        _api.Mode = "unavailable";
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?tab=payment"));

        Assert.Contains("data-payment-state=\"Unavailable\"", html, StringComparison.Ordinal);
        Assert.Contains("data-balance-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-summary", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Reminder history", html, StringComparison.Ordinal);
    }

    // ---- unit-based Payment tab (CRM -> PACT by company + tower + apartment) ----

    private static LinkedUnitCandidateDto UnitCandidate(
        string id = "crm:9090", string status = "Available", string? reason = null, string? detail = null, decimal? due = 300m, decimal? overdue = 700m, string unit = "909",
        int? company = 4, string source = "Crm", string name = "Sreesaran Maru Sudhakar", string tenant = "T-909", string? contract = null,
        IReadOnlyList<string>? review = null, IReadOnlyList<CollectionsUnitInstalmentDto>? instalments = null, CollectionsUnitPaymentFiguresDto? payments = null) =>
        new(id, source, company, "140", "Al Ghaf Tower", "Al Ghaf Tower", unit, "TP140-" + unit, status == "MatchFailed" ? "MatchFailed" : "Linked", review ?? [],
            new CollectionsUnitPartyDto(name, "+971501234567", "sree@example.test", "Crm", "Crm", "Crm"), 498397, 9090, 79, tenant, 1400909, contract, null,
            status, reason, detail, due, overdue, due is null || overdue is null ? null : due + overdue,
            instalments ?? (status == "Available" ? [new("Voucher V1", DateOnly.FromDateTime(Now).AddDays(-40), 700m, 0m, 700m, "Overdue", 40)] : []), DateOnly.FromDateTime(Now), "Fresh",
            payments ?? (status == "Available" ? Figures(overdue ?? 0m, due ?? 0m, 500m) : status == "NoDues" ? Figures(0m, 0m, 0m) : null));

    /// <summary>The tab's figures (all sums derive from the lines): an overdue line (before this month), a line due this month and, when there is one, a not-yet-due line (next month). Total and paid are never provided by the snapshot.</summary>
    private static CollectionsUnitPaymentFiguresDto Figures(decimal overdue, decimal due, decimal future)
    {
        var monthStart = new DateOnly(Now.Year, Now.Month, 1);
        var lines = new List<CollectionsUnitScheduleLineDto>();
        if (overdue > 0) lines.Add(new("Voucher V1", monthStart.AddDays(-5), null, null, overdue, "Overdue", 5));
        if (due > 0) lines.Add(new("Voucher V2", monthStart.AddDays(2), null, null, due, "Due", null));
        if (future > 0) lines.Add(new("Voucher V3", monthStart.AddMonths(1).AddDays(9), null, null, future, "NotYetDue", null));
        return new(monthStart, monthStart.AddMonths(1).AddDays(-1), lines, Now);
    }

    /// <summary>The text of one summary card's value (the rendered HTML, decoded).</summary>
    private static string Kpi(string html, string key) =>
        WebUtility.HtmlDecode(Regex.Match(html, $"data-kpi=\"{key}\">\\s*<strong>(.*?)</strong>", RegexOptions.Singleline).Groups[1].Value.Trim());

    private static CustomerUnitLinkResultDto Link(params LinkedUnitCandidateDto[] units) =>
        new("+971501234567", "Found", "NotSearched", units, units.Length > 1, units.Length == 1 ? units[0].SelectionId : null, []);

    private static StatementAmountDto Amt(decimal v) => new("Provided", v, v.ToString("N2"));

    [Fact]
    public async Task TheCompleteStatement_ShowsDueOutstandingPaidFinesTotal_AndThePaymentDetails_PaidGreenDueRed()
    {
        _api.Mode = "nosource";
        var statement = new UnitPaymentStatementDto("Available", null, "TP140-909", 4, "Tiger Group Dubai", "Owned", "T-909", [],
            Amt(0m), Amt(889393.44m), Amt(594467.56m), new("ZeroOrLess", 0m, null), Amt(1483861m), "Outstanding",
            [new(new DateOnly(2026, 1, 5), null, "Paid", 100000m, "100,000.00", "CHQ-1", "Cheque"), new(new DateOnly(2026, 1, 5), null, "Paid", 20000m, "20,000.00", "CHQ-2", "Cheque"),
             new(new DateOnly(2027, 3, 1), null, "Outstanding", 300000m, "300,000.00", "CHQ-9", "Cheque")],
            true, [], "AED", Now, "Edsm");
        _api.UnitLink = Link(UnitCandidate()) with { Statement = statement };

        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("data-unit-statement", html, StringComparison.Ordinal);
        Assert.Matches(@"data-statement=""due"">\s*<strong[^>]*>AED 0\.00<", html);
        Assert.Matches(@"data-statement=""outstanding"">\s*<strong[^>]*statement-red-text[^>]*>AED 889,393\.44<", html);
        Assert.Matches(@"data-statement=""paid"">\s*<strong[^>]*statement-green-text[^>]*>AED 594,467\.56<", html);
        Assert.Matches(@"data-statement=""fines"">\s*<strong[^>]*>AED 0\.00<", html);
        Assert.Matches(@"data-statement=""total"">\s*<strong[^>]*>AED 1,483,861\.00<", html);
        Assert.True(html.IndexOf("data-statement=\"due\"", StringComparison.Ordinal) < html.IndexOf("data-statement=\"outstanding\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("data-statement=\"outstanding\"", StringComparison.Ordinal) < html.IndexOf("data-statement=\"paid\"", StringComparison.Ordinal));
        Assert.Contains("<th>Payment Status</th>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-kpi=\"remaining\"", html, StringComparison.Ordinal);       // the Due / Overdue snapshot cards do not stand in for (or sit beside) the complete statement
        Assert.DoesNotContain("data-payment-schedule", html, StringComparison.Ordinal);
        Assert.Contains("<th>Cheque Number</th>", html, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(html, "data-statement-row=\"Paid\"").Count);          // a split of one date stays two rows
        Assert.Single(Regex.Matches(html, "data-statement-row=\"Outstanding\""));         // a future row is listed
        Assert.Contains("CHQ-9", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWithheldStatement_NamesTheReason_ShowsNoAmount_AndNeverZero()
    {
        _api.Mode = "nosource";
        var withheld = UnitPaymentStatementDto.Withheld("NotExclusive", "PACT's payment statement is kept per customer account, and this account also covers TP140-407.", "TP140-909", 4, "T-909", ["TP140-407"]);
        _api.UnitLink = Link(UnitCandidate()) with { Statement = withheld };

        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("data-statement-status=\"NotExclusive\"", html, StringComparison.Ordinal);
        Assert.Contains("Payment summary not shown", html, StringComparison.Ordinal);
        Assert.Contains("TP140-407", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-statement=\"total\"", html, StringComparison.Ordinal);
        Assert.Equal("Unavailable", Kpi(html, "total"));                                  // the snapshot cards still never show zero
    }

    [Fact]
    public async Task ReportedCase_CrmCustomerAbsentFromPact_ShowsTheUnitsFigures_NotTheGenericNoFiguresMessage()
    {
        // The EDSM route has no PACT tenant for a CRM-only customer (NotMapped), exactly as for the reported customer ...
        _api.Mode = "nosource";
        // ... but the unit-based lookup (CRM unit -> PACT by company + tower + apartment) has the figures.
        _api.UnitLink = Link(UnitCandidate());

        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("data-payment-state=\"UnitSummary\"", html, StringComparison.Ordinal);
        Assert.Contains("data-unit-state=\"Available\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-summary", html, StringComparison.Ordinal);
        Assert.Equal("AED 1,500.00", Kpi(html, "remaining"));                       // every unpaid payment, the not-yet-due 500 included (not Due + Overdue = 1,000)
        Assert.Equal("AED 300.00", Kpi(html, "due"));
        Assert.Equal("AED 700.00", Kpi(html, "overdue"));
        Assert.Equal("Unavailable", Kpi(html, "total"));                            // the snapshot cannot provide them: never zero, never inferred
        Assert.Equal("Unavailable", Kpi(html, "paid"));
        Assert.Contains("data-total-paid-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-no-due-overdue", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-schedule", html, StringComparison.Ordinal);
        Assert.Contains("data-schedule-status=\"NotYetDue\"", html, StringComparison.Ordinal);
        Assert.Contains(">Not yet due<", html, StringComparison.Ordinal);
        Assert.Contains("Last updated", html, StringComparison.Ordinal);
        Assert.Contains("Al Ghaf Tower", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "sree@example\\.test"));          // the contact is shown once, its (single) source said once
        Assert.Contains("Sreesaran Maru Sudhakar", html, StringComparison.Ordinal);
        Assert.Contains("971501234567", html, StringComparison.Ordinal);
        Assert.Contains("sree@example.test", html, StringComparison.Ordinal);
        Assert.Contains("Voucher V1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No payment figures for this customer", html, StringComparison.Ordinal);
        // The unit route is what the tab asked first; the figures never came from a phone search.
        Assert.StartsWith("/api/collections/customers/crm/9001/units", _api.Requests.First(r => r.Contains("/api/collections")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportedCase_WithoutAVerifiedProjectMapping_SaysTheUnitCouldNotBeMatched_NotThatThereAreNoFigures()
    {
        _api.Mode = "nosource";
        _api.UnitLink = Link(UnitCandidate(status: "MatchFailed", reason: "ProjectMappingMissing", due: null, overdue: null,
            detail: "CRM project 79 (Al Ghaf Tower) has no verified mapping to a PACT tower."));

        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("data-payment-state=\"UnitSummary\"", html, StringComparison.Ordinal);
        Assert.Contains("data-unit-state=\"MatchFailed\"", html, StringComparison.Ordinal);
        Assert.Contains("The unit could not be matched to one PACT record.", html, StringComparison.Ordinal);
        Assert.Contains("The CRM project has no verified PACT tower mapping.", html, StringComparison.Ordinal);
        Assert.Contains("Sreesaran Maru Sudhakar", html, StringComparison.Ordinal);   // the identity and unit are still shown
        Assert.DoesNotContain("No payment figures for this customer", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AED 0.00", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFourFinancialStates_AreShownDistinctly_AndNeverAsZero()
    {
        _api.Mode = "nosource";
        _api.UnitLink = Link(UnitCandidate(status: "NoDues", due: 0m, overdue: 0m, detail: "Nothing is due on or before today."));
        var zero = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));
        Assert.Contains("data-unit-state=\"NoDues\"", zero, StringComparison.Ordinal);
        // Nothing due or overdue: the summary and the schedule stay, with the short message - the values are real zeros here (PACT confirmed), the other two are unavailable.
        Assert.Contains("data-payment-summary", zero, StringComparison.Ordinal);
        Assert.Contains("No due or overdue payments this month", zero, StringComparison.Ordinal);
        Assert.Equal(("AED 0.00", "AED 0.00", "AED 0.00"), (Kpi(zero, "due"), Kpi(zero, "overdue"), Kpi(zero, "remaining")));
        Assert.Equal("Unavailable", Kpi(zero, "total"));
        Assert.Contains("No unpaid payments on this unit", zero, StringComparison.Ordinal);

        _api.UnitLink = Link(UnitCandidate(status: "NoFinancialData", reason: "PactHoldsNoRecord", due: null, overdue: null, detail: "PACT holds no receivable record for this unit."));
        var none = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));
        Assert.Contains("data-unit-state=\"NoFinancialData\"", none, StringComparison.Ordinal);
        Assert.Contains("No financial data available.", none, StringComparison.Ordinal);
        Assert.Contains("This is not a zero balance.", none, StringComparison.Ordinal);
        Assert.Equal(["Unavailable", "Unavailable", "Unavailable", "Unavailable", "Unavailable"], new[] { "total", "paid", "remaining", "due", "overdue" }.Select(k => Kpi(none, k)));
        Assert.DoesNotContain("data-no-due-overdue", none, StringComparison.Ordinal);                 // unknown is not "nothing due"
        Assert.DoesNotContain("Confirmed: nothing is due", none, StringComparison.Ordinal);

        _api.UnitLink = Link(UnitCandidate(status: "SourceError", reason: "PactUnavailable", due: null, overdue: null, detail: "The unit could not be read from the PACT receivables snapshot. Please retry."));
        var error = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));
        Assert.Contains("data-unit-state=\"SourceError\"", error, StringComparison.Ordinal);
        Assert.Contains("data-payment-retry", error, StringComparison.Ordinal);
        Assert.Contains("Do not quote a balance.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirmed: nothing is due", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralUnits_AreListedAndOneMustBeChosen_EachWithItsOwnAmounts()
    {
        _api.Mode = "nosource";
        var first = UnitCandidate("crm:9090", unit: "909", due: 300m, overdue: 700m);
        var second = UnitCandidate("crm:9091", unit: "101", due: 0m, overdue: 55m, tenant: "T-101", instalments: [new("Voucher V2", DateOnly.FromDateTime(Now).AddDays(-3), 55m, 0m, 55m, "Overdue", 3)]);
        _api.UnitLink = Link(first, second);

        var list = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));
        Assert.Contains("data-unit-selection-required", list, StringComparison.Ordinal);
        Assert.Contains("data-unit-link=\"crm:9090\"", list, StringComparison.Ordinal);
        Assert.Contains("data-unit-link=\"crm:9091\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain("data-unit-state=", list, StringComparison.Ordinal);       // no unit's detail before a choice

        _api.UnitLink = Link(first, second) with { SelectedId = "crm:9091", SelectionRequired = false };
        var chosen = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=crm%3A9091"));
        Assert.Contains("Voucher V1", chosen, StringComparison.Ordinal);                  // this unit's own schedule ...
        Assert.Equal("AED 555.00", Kpi(chosen, "remaining"));                             // ... and only its own amounts (55 overdue + 500 not yet due)
        Assert.Equal("AED 55.00", Kpi(chosen, "overdue"));
        Assert.DoesNotContain("AED 1,500.00", chosen, StringComparison.Ordinal);          // the other unit's amounts are not mixed in
        Assert.Contains(_api.Requests, r => r.Contains("selection=crm%3A9091", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task APactCustomerFoundByPhoneOnLeasingCompany7_ShowsTheExactMissingSource_NotAGenericMessage()
    {
        _api.Mode = "nosource";                                                          // EDSM has no mapping for the Leasing tenant (company 7 is not an EDSM company)
        var leasing = UnitCandidate("pact:7:3001:7001:LC-1", status: "NoFinancialData", reason: "LeasingReceivablesSourceMissing", due: null, overdue: null, company: 7,
            source: "Leasing", name: "Fatima Noor", tenant: "3001", contract: "LC-1", detail: "No company-7 (Leasing) receivables source exists: the PACT receivables snapshot ... This is not a zero balance.");
        _api.LookupLink = Link(leasing);

        var html = await Ok(await Client().GetAsync("/Customers/Payments?phoneNumber=971500000002&customerKey=ext%3APact%3A3001"));

        Assert.Contains("data-payment-state=\"UnitSummary\"", html, StringComparison.Ordinal);
        Assert.Contains("No company-7 (Leasing) receivables source exists.", html, StringComparison.Ordinal);
        Assert.Contains("PACT Leasing", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Contract</dt><dd>LC-1</dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No payment figures for this customer", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePaymentsBeforeTicketPage_UsesTheUnitLookup_ForACrmCustomer()
    {
        _api.UnitLink = Link(UnitCandidate());
        var html = await Ok(await Client().GetAsync("/Customers/Payments?phoneNumber=971501234567&customerKey=crm%3A9001"));
        Assert.Contains("data-unit-state=\"Available\"", html, StringComparison.Ordinal);
        Assert.Equal("AED 1,500.00", Kpi(html, "remaining"));
        Assert.Contains("phone=971501234567", _api.Requests.First(r => r.Contains("/units")), StringComparison.Ordinal);
        Assert.DoesNotContain(_api.Requests, r => r.Contains("customer-lookup/payment-summary", StringComparison.Ordinal));   // figures found: no phone-based EDSM search needed
    }

    [Fact]
    public async Task TheCardsAndTheScheduleTable_ShowTheSameNumbers_ForTheSameUnit_ThroughTheRealComposition()
    {
        _api.Mode = "nosource";
        // The real composition (not hand-typed figures): October 2,500 + 1,500, September 1,200 + 800, three later months 3,500 each.
        var today = new DateOnly(2026, 10, 14);
        PactUnitInstalment Row(int y, int m, int d, decimal amount, string v) => new(4, "T-909", 1400909, v, new(y, m, d), amount, null, null, "Installment");
        var identity = new PactUnitIdentity(4, "T-909", 1400909, "TP140-909", "Pact Name", "", "", "140", "Al Ghaf Tower", 7, 7);
        var snapshot = new SnapshotStatusDto([new SnapshotCompanyStatusDto(4, "Co4", true, Now, Now, "Succeeded", null, 0, 10, null, null, 0, 0, 0, 0, 0, 0, "Fresh", 1)], [], 120);
        var data = new PactUnitReceivables(snapshot, [identity], [
            Row(2026, 9, 11, 1200m, "A"), Row(2026, 9, 28, 800m, "B"), Row(2026, 10, 2, 2500m, "C"), Row(2026, 10, 25, 1500m, "D"),
            Row(2026, 11, 5, 3500m, "E"), Row(2026, 12, 5, 3500m, "F"), Row(2027, 1, 5, 3500m, "G")],
            new Dictionary<int, CrmUnitLink>(), true, Now);
        var summary = TigerCS.Application.Modules.Collections.Services.CollectionsUnitPaymentSummaryAppService.Compose("TP140-909", today, data, 120);
        var candidate = UnitCandidate("crm:9090", unit: "909") with { Payments = summary.Payments, Due = summary.Due, Overdue = summary.Overdue, Total = summary.Total };
        _api.UnitLink = Link(candidate);

        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        var rows = Regex.Matches(html, "<tr data-schedule-status=\"(\\w+)\">(.*?)</tr>", RegexOptions.Singleline)
            .Select(m => (Status: m.Groups[1].Value, Remaining: decimal.Parse(Regex.Match(m.Groups[2].Value, "cell-primary\">AED ([\\d,\\.]+)<").Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture))).ToList();
        decimal Money(string key) => decimal.Parse(Kpi(html, key).Replace("AED ", "").Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(7, rows.Count);
        Assert.Equal(4000m, Money("due"));
        Assert.Equal(2000m, Money("overdue"));
        Assert.Equal(16500m, Money("remaining"));
        Assert.Equal(Money("remaining"), rows.Sum(r => r.Remaining));                                       // the table adds up to the Remaining card
        Assert.Equal(Money("due"), rows.Where(r => r.Status == "Due").Sum(r => r.Remaining));               // ... Due card = the Due rows
        Assert.Equal(Money("overdue"), rows.Where(r => r.Status == "Overdue").Sum(r => r.Remaining));       // ... Overdue card = the Overdue rows
        Assert.Equal(Money("remaining"), Money("due") + Money("overdue") + rows.Where(r => r.Status == "NotYetDue").Sum(r => r.Remaining));
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Supervisor"),
                new Claim(ClaimTypes.Role, Roles.CsSupervisor),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public string Mode { get; set; } = "loaded";

        /// <summary>The answer of GET /api/collections/customers/crm/9001/units; null = the route does not exist (404), so the per-account view is used as before.</summary>
        public CustomerUnitLinkResultDto? UnitLink { get; set; }

        /// <summary>The answer of GET /api/collections/customers/lookup (phone lookup incl. Leasing); null = 404.</summary>
        public CustomerUnitLinkResultDto? LookupLink { get; set; }
        public bool CanSend { get; set; }
        public bool Stale { get; set; }
        public bool ChargesUnreported { get; set; }
        public HttpStatusCode QueueAnswer { get; set; } = HttpStatusCode.Accepted;
        public List<string> Requests { get; } = [];
        public string? LastPostBody { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

        private static readonly CustomerDirectoryProfileDto Profile = new(
            "crm:9001", "Crm", "Test Buyer", ["+971500000900"], [], "Crm", 9001, null, null, 0, 1, Now.AddDays(-30), Now.AddDays(-1), 6, [], [], []);

        private static readonly CustomerDirectoryProfileDto PactCustomer = new(
            "ext:Pact:3001", "External", "Fatima Noor", ["+971500000002"], [], "Pact", null, "Pact", "3001", 0, 1, Now.AddDays(-30), Now.AddDays(-1), 8, [], [], []);

        private static readonly CollectionsPactContractRefDto Contract = new("88001", "41230", "0304", "Tiger Marina Residences", "Residential");

        private static CollectionsEdsmFieldDto F(string key, string label, string status, decimal? value, string? raw, string? meaning = null) =>
            new(key, label, $"Definition of {label}.", status, value, raw, meaning);

        private static CollectionsPaymentSummaryResponseDto CrmNotMapped() => new(
            "crm:9001", "NotMapped",
            "This customer is identified by Tiger CRM (customerId 9001). EDSM is keyed by PACT companyID and tenantID, and no verified mapping from a CRM customer to a PACT tenant exists.",
            null, "Pact", Now, null, "AED", "Configured", "en-US", 10, 20, null, null, [], []);

        private CollectionsPaymentSummaryResponseDto Summary() => new CollectionsPaymentSummaryResponseDto(
            "ext:Pact:3001", "Mapped", null, "3001", "Pact", Now, null, "AED", "Configured", "en-US", 10, 20, Now.AddMinutes(-5), "Cached",
            [
                new CollectionsCompanyPaymentSummaryDto(4, "Tiger Group Dubai", "Owned", "Available", null, [Contract],
                [
                    F("paidAmount", "Paid", "Missing", null, null),
                    F("dueAmount", "Due", "Unreadable", null, "1.500,00"),
                    F("outstandingAmount", "Not yet due", "Provided", 437_500m, "437,500.00"),
                    F("paidAmountUnconfigured", "Paid (format check)", "FormatNotConfigured", null, "812.500,00"),
                    F("lateFines", "Late fines", "Empty", null, "", "ZeroOrLess"),
                    F("totalAmount", "Total", "Provided", 1_250_000m, "1,250,000.00"),
                ], "NotChecked", false, [],
                [
                    new CollectionsEdsmTransactionListDto("Paid", "Available", null, null,
                        [new CollectionsEdsmTransactionDto(500_000m, "Provided", "500,000.00", new DateOnly(2026, 1, 15), "15-Jan-2026", null, null)]),
                    new CollectionsEdsmTransactionListDto("Due", "Available", null, null,
                        [new CollectionsEdsmTransactionDto(62_500m, "Provided", "62,500.00", new DateOnly(2026, 9, 15), "15-Sep-2026", "000412", null)]),
                    new CollectionsEdsmTransactionListDto("Outstanding", "Available", null, null,
                        [new CollectionsEdsmTransactionDto(187_500m, "Provided", "187,500.00", new DateOnly(2026, 12, 15), "15-Dec-2026", null, null)]),
                ],
                new CollectionsEdsmDueInstallmentsDto("Available", null, new DateOnly(2026, 9, 4), new DateOnly(2026, 11, 5),
                    [new CollectionsEdsmDueInstallmentDto(41230, "PDC-0412", "000412", new DateOnly(2026, 9, 15), 62_500m, "Due ")])),
                new CollectionsCompanyPaymentSummaryDto(25, "Hirmas Dubai", "Rented", "Available", null, [],
                [
                    F("dueAmount", "Due", "Provided", -500m, "-500.00"),
                    F("lateFines", "Late fines", "Empty", null, "", "NotComputedForRented"),
                ], "NotChecked", false, [],
                [
                    new CollectionsEdsmTransactionListDto("Paid", "Available", null, "Refunds appear in this list as positive payments.",
                        [new CollectionsEdsmTransactionDto(60_000m, "Provided", "60,000.00", new DateOnly(2026, 2, 1), "01-Feb-2026", "100201", "Cheque", 2)]),
                    new CollectionsEdsmTransactionListDto("Due", "Available", null, null,
                        [new CollectionsEdsmTransactionDto(1_000m, "Provided", "1,000.00 AED", new DateOnly(2026, 3, 1), "01-Mar-2026", null, "Fees", 3)]),
                    new CollectionsEdsmTransactionListDto("Outstanding", "Unavailable", "timed out", null, []),
                ], null),
                new CollectionsCompanyPaymentSummaryDto(7, "Alsabeel Sharjah", "Rented", "Unauthorized", "EDSM rejected the configured API key.", [], [],
                    "NotChecked", false, [], [], null),
            ],
            [])
            with
            {
                // What the service reports for this data: one company refused, one list timed out.
                Completeness = SummaryCompleteness,
                IncompleteReasons = SummaryCompleteness == "Complete"
                    ? []
                    : ["Company 7 (Alsabeel Sharjah): no figures (Unauthorized).", "Company 25 (Hirmas Dubai): Outstanding transactions not read (Unavailable)."]
            };

        public string SummaryCompleteness { get; set; } = "Partial";

        /// <summary>When set, this company comes back as cut off by the read deadline: no fields, no lists.</summary>
        public int? DeadlineCompany { get; set; }

        private CollectionsPaymentSummaryResponseDto SummaryForMode() => DeadlineCompany is not { } cut
            ? Summary()
            : Summary() with
            {
                Companies = Summary().Companies
                    .Select(c => c.CompanyId != cut ? c : c with
                    {
                        Status = "DeadlineExceeded",
                        StatusDetail = "Not read: the request's 60 s deadline passed before EDSM answered for this company.",
                        Fields = [], TotalCheck = "NotChecked", AllZero = false, Notes = [], Transactions = [], DueInstallments = null
                    })
                    .ToList()
            };

        private static readonly CustomerDirectoryProfileDto Caller = new(
            "phone:%2B971501112222", "Phone", null, ["+971501112222"], [], "Unverified", null, null, null, 0, 0, Now.AddDays(-3), Now.AddDays(-3), 7, [], [], []);

        private CollectionsAccountDto Arrears => new(
            "ACC-45001", 45001, "Example Tower", "1205", "AED", Stale ? Now.AddHours(-5) : Now.AddMinutes(-2), Stale ? "Stale" : "Current",
            45_000m, 30_000m, 0m, 15_000m,
            ChargesUnreported ? null : 500m, ChargesUnreported ? null : 0m, ChargesUnreported ? null : 0m, ChargesUnreported ? null : 30_500m,
            10_000m, Today.AddMonths(-4),
            new CollectionsNextPaymentDto("INST-OCT-2026", Today.AddDays(13), 10_000m), []);

        private static CollectionsAccountDto Settled => new(
            "ACC-45002", 45002, "Example Tower", "0805", "AED", Now.AddMinutes(-2), "Current",
            0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, null, null, []);

        private static CollectionsReminderHistoryResponseDto Reminders() => new(9001, null,
        [
            new CollectionsReminderHistoryItemDto("REM-12", "ACC-45001", "OverdueMonthly", "2026-10:OverdueMonthly", "AED", 30_000m,
                "UnpaidPrincipalOlderThanOneCalendarMonth", Now.AddDays(-1), "Integration",
                [new CollectionsChannelStatusDto("VoiceBot", "Answered", Now.AddDays(-1), 1, null)],
                "AlreadyPaid", 77, "TG-COL-00077",
                [new CollectionsReminderResponseDto("EVT-1", "VoiceBot", "AlreadyPaid", Now.AddDays(-1), true, true, "Created", 77, "TG-COL-00077")])
        ], null);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (path.StartsWith("/api/collections", StringComparison.Ordinal))
            {
                if (path == "/api/collections/customers/crm/9001/units")
                {
                    return UnitLink is null ? Problem(HttpStatusCode.NotFound, "AccountNotFound") : Json(HttpStatusCode.OK, UnitLink);
                }

                if (path == "/api/collections/customers/lookup")
                {
                    return LookupLink is null ? Problem(HttpStatusCode.NotFound, "AccountNotFound") : Json(HttpStatusCode.OK, LookupLink);
                }

                if (request.Method == HttpMethod.Post)
                {
                    LastPostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    LastIdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
                    return QueueAnswer == HttpStatusCode.Conflict
                        ? Problem(HttpStatusCode.Conflict, "CandidateChanged")
                        : Json(HttpStatusCode.Accepted, new CollectionsReminderJobDto("REM-90001", 9001, "ACC-45001", "OverdueMonthly", "2026-10:OverdueMonthly",
                            "AED", 30_000m, "UnpaidPrincipalOlderThanOneCalendarMonth", ["INST-JUN-2026"], Now, "Queued",
                            [new("Email", "Queued", Now, 1, null), new("Sms", "Queued", Now, 1, null)]));
                }

                if (path.EndsWith("/reminders", StringComparison.Ordinal))
                {
                    return Mode == "forbidden" ? Problem(HttpStatusCode.Forbidden, "Forbidden") : Json(HttpStatusCode.OK, Reminders());
                }

                if (path.EndsWith("/candidates", StringComparison.Ordinal))
                {
                    if (!CanSend)
                    {
                        return Problem(HttpStatusCode.Forbidden, "Forbidden");
                    }

                    var items = query["reminderType"] == "OverdueMonthly" && query["accountId"] == "ACC-45001" && !Stale
                        ? new List<CollectionsReminderCandidateDto>
                        {
                            new("CAND-1", 9001, "ACC-45001", 45001, "Example Tower", "1205", "AED", 30_000m, "UnpaidPrincipalOlderThanOneCalendarMonth",
                                ["INST-JUN-2026", "INST-JUL-2026"], Today.AddMonths(-4), ["VoiceBot", "Sms", "Email"], Now, Now.AddMinutes(15))
                        }
                        : [];
                    return Json(HttpStatusCode.OK, new CollectionsReminderCandidatesResponseDto(query["reminderType"]!, "2026-10:" + query["reminderType"], Today, "Asia/Dubai", true, items, null));
                }

                return Mode switch
                {
                    "nosource" when path.EndsWith("/payment-summary", StringComparison.Ordinal) => Json(HttpStatusCode.OK, CrmNotMapped()),
                    "nosource" => Problem(HttpStatusCode.ServiceUnavailable, "FinanceUnavailable"),
                    "forbidden" => Problem(HttpStatusCode.Forbidden, "Forbidden"),
                    "disabled" => Problem(HttpStatusCode.ServiceUnavailable, "CollectionsDisabled"),
                    "unavailable" => Problem(HttpStatusCode.ServiceUnavailable, "FinanceUnavailable"),
                    "notfound" => Problem(HttpStatusCode.NotFound, "AccountNotFound"),
                    _ when path.EndsWith("/payment-summary", StringComparison.Ordinal) => Json(HttpStatusCode.OK, SummaryForMode()),
                    _ when path.EndsWith("/outstanding", StringComparison.Ordinal) =>
                        Json(HttpStatusCode.OK, new CollectionsOutstandingResponseDto(9001, Today, Now, Stale ? "Stale" : "Current", "Test source", [Arrears, Settled], null)),
                    _ when query["view"] == "history" => Json(HttpStatusCode.OK, new CollectionsPaymentHistoryResponseDto(9001, query["accountId"]!, "AED", Now, "Current", "history",
                        [new CollectionsPaymentDto("PAY-70001", Today.AddMonths(-5), 5_000m, "BankTransfer", "Posted", "RCT-70001", true, [new("INST-MAY-2026", 5_000m, null)])], null)),
                    _ => Json(HttpStatusCode.OK, new CollectionsInstalmentsResponseDto(9001, query["accountId"]!, "AED", Now, "Current", "instalments",
                    [
                        new CollectionsInstalmentDto("INST-JUN-2026", Today.AddMonths(-4), 5_000m, 2_000m, 3_000m, "PartiallyPaid", true),
                        new CollectionsInstalmentDto("INST-OCT-2026", Today.AddDays(13), 10_000m, 0m, 10_000m, "Upcoming", false),
                    ], null)),
                };
            }

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Supervisor", [Roles.CsSupervisor], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/customers/profile/crm:9001" => Profile,
                "/api/customers/profile/ext:Pact:3001" => PactCustomer,
                "/api/customers/profile/phone:+971501112222" => Caller,
                _ => null,
            };

            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, body);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body, body.GetType()) };

        private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
            new(status) { Content = JsonContent.Create(new { type = $"https://tigercs.internal/problems/collections/{code}", title = code, detail = code, code, message = code }) };
    }
}
