// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The customer pages rendered end to end through the real Razor pipeline
/// against a fake TigerCS.Api and a signed-in CS Agent: the Customers
/// directory lists customers immediately (no search needed) with every row
/// opening the Customer Profile; the profile renders its header and tabs
/// with every ticket linking to Ticket Details; Ticket Details shows the
/// customer as a linked summary card; and the old ticket-anchored profile
/// address redirects into the customer's profile — navigation in both
/// directions, with the Tickets/Customers return context preserved.
/// </summary>
public sealed class CustomerWorkspaceRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public CustomerWorkspaceRenderTests()
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

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> Ok(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
        return html;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ---------------------------------------------------------------
    // Customers directory
    // ---------------------------------------------------------------

    [Fact]
    public async Task Customers_ListsKnownCustomersImmediately_WithoutASearch()
    {
        var html = await Ok(await Client().GetAsync("/Customers"));

        Assert.Contains("<h1 class=\"page-title\">Customers</h1>", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Customers</a>", html, StringComparison.Ordinal);
        Assert.Contains("2 customers</span>", html, StringComparison.Ordinal);

        // One row per customer, each opening the profile, with the useful columns.
        foreach (var column in new[] { "<th>Customer</th>", "<th>Phone</th>", "<th>Project / Unit</th>", "<th>Verification Source</th>", "Open Tickets</th>", "Total Tickets</th>", "<th>Last Ticket</th>", "<th>Last Interaction</th>" })
        {
            Assert.Contains(column, html, StringComparison.Ordinal);
        }

        Assert.Contains("data-href=\"/Customers/crm%3A9001\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/crm%3A9001\"", html, StringComparison.Ordinal);
        Assert.Contains("Mariam Al Falasi", html, StringComparison.Ordinal);
        Assert.Contains("data-href=\"/Customers/phone%3A%252B971501112222\"", html, StringComparison.Ordinal);
        Assert.Contains("Unnamed caller", html, StringComparison.Ordinal);
        Assert.Contains("Tiger CRM</span>", html, StringComparison.Ordinal);
        Assert.Contains("Unverified</span>", html, StringComparison.Ordinal);

        // Search, filters and pagination are all there; the cross-source lookup is a secondary action.
        Assert.Contains("name=\"search\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"verificationSource\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"openOnly\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"pagination\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/Lookup\">Look up by phone</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customers_SearchFiltersTheList_AndAnEmptyMatchOffersTheLookup()
    {
        var html = await Ok(await Client().GetAsync("/Customers?search=nobody"));

        Assert.Contains("No customers match these filters.", html, StringComparison.Ordinal);
        Assert.Contains("value=\"nobody\"", html, StringComparison.Ordinal);
        Assert.Contains("Look up by phone", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customers_TheOldLookupStillExists_UnderCustomersLookup()
    {
        var html = await Ok(await Client().GetAsync("/Customers/Lookup"));

        Assert.Contains("<h1 class=\"page-title\">Customer Lookup</h1>", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/Customers/Lookup\"", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/Customers\">Customers</a>", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Customer Profile
    // ---------------------------------------------------------------

    [Fact]
    public async Task CustomerProfile_RendersHeaderAndTabs_WithEveryTicketOpeningTicketDetails()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm%3A9001"));

        Assert.Contains("<h1 class=\"customer-header__name\">Mariam Al Falasi</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Verified via Tiger CRM", html, StringComparison.Ordinal);
        Assert.Contains("&#x2B;971501234567", html, StringComparison.Ordinal);
        Assert.Contains("T-1310 &#xB7; Tiger Tower", html, StringComparison.Ordinal);
        Assert.Contains("CRM #9001", html, StringComparison.Ordinal);

        foreach (var tab in new[] { ">Overview<", ">Contact Info<", ">Units <span", ">Tickets <span", ">Interactions <span" })
        {
            Assert.Contains(tab, html, StringComparison.Ordinal);
        }

        // Both tickets link to Ticket Details; two distinct units are listed.
        Assert.Contains("href=\"/Tickets/5\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets/6\"", html, StringComparison.Ordinal);
        Assert.Contains("data-href=\"/Tickets/5\"", html, StringComparison.Ordinal);
        Assert.Contains("T-1204", html, StringComparison.Ordinal);
        // Live CRM enrichment (Contact Info / Units) came through the existing ticket-anchored endpoint.
        // (Razor HTML-encodes non-ASCII text: "مريم" is rendered as numeric entities.)
        Assert.Contains("&#x645;&#x631;&#x64A;&#x645;", html, StringComparison.Ordinal);
        Assert.Contains("mariam@example.test", html, StringComparison.Ordinal);
        Assert.Contains("<th>Lead Status</th>", html, StringComparison.Ordinal);
        // Interactions across the customer's tickets.
        Assert.Contains("WhatsApp", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Customers</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomerProfile_ReturnsToTheRememberedDirectoryList_AndToTheTicketItCameFrom()
    {
        using var client = Client();

        await Ok(await client.GetAsync("/Customers?search=Falasi&openOnly=true&page=2"));
        var html = await Ok(await client.GetAsync("/Customers/crm%3A9001?fromTicket=5"));

        Assert.Contains("<a href=\"/Customers?search=Falasi&amp;openOnly=true&amp;page=2\">Customers</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets/5\">← Back to TG-CS-00005</a>", html, StringComparison.Ordinal);

        var fresh = await Ok(await Client().GetAsync("/Customers/crm%3A9001"));
        Assert.Contains("<a href=\"/Customers\">Customers</a>", fresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Back to TG-CS", fresh, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomerProfile_PhoneOnlyCustomer_IsLabelledUnverified_AndUnknownKeysAre404()
    {
        var html = await Ok(await Client().GetAsync("/Customers/phone%3A%252B971501112222"));
        Assert.Contains("Unnamed caller", html, StringComparison.Ordinal);
        Assert.Contains("Unverified &#xB7; phone only", html, StringComparison.Ordinal);
        Assert.Contains("tickets grouped by the caller", html, StringComparison.Ordinal);

        var missing = await Client().GetAsync("/Customers/crm%3A424242");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// PACT returns the customer's own <c>customerName</c> and
    /// <c>customerEmail</c>; once snapshotted they reach the page, so the
    /// profile shows the real person instead of the "Pact customer"
    /// placeholder and a "—" email.
    /// </summary>
    [Fact]
    public async Task CustomerProfile_PactVerifiedCustomer_ShowsTheSourcesOwnNameAndEmail()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext%3APact%3A7001"));

        Assert.Contains("Fatima Noor", html, StringComparison.Ordinal);
        // The placeholder the page falls back to is "PACT customer" (the
        // source label); with a real name on file it must not appear at all.
        Assert.DoesNotContain("PACT customer", html, StringComparison.Ordinal);
        Assert.Contains("<dd>fatima@example.com</dd>", html, StringComparison.Ordinal);

        // PACT has no Arabic name field, so the Arabic row stays blank —
        // the English name is never copied across to fill it.
        Assert.Contains("<dt>Full Name Arabic</dt><dd>&#x2014;</dd>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four spellings of one mobile are one contact number. Only the
    /// genuinely different second line is listed as "Also called from".
    /// </summary>
    [Fact]
    public async Task CustomerProfile_ShowsOneRowPerRealNumber_NotOnePerSpelling()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext%3APact%3A7001"));

        // Razor HTML-encodes the leading '+' as &#x2B;.
        Assert.Equal(1, Count(html, "<dt>Mobile Number</dt>"));
        Assert.Contains("<dt>Mobile Number</dt><dd class=\"text-mono\">971509724162</dd>", html, StringComparison.Ordinal);

        // "971509724162", "+971509724162", "+971 50 972 4162" and
        // "971-50-972-4162" all normalize to the same number: exactly one
        // alias row, for the one number that is genuinely different.
        Assert.Equal(1, Count(html, "<dt>Also called from</dt>"));
        Assert.Contains("<dt>Also called from</dt><dd class=\"text-mono\">&#x2B;971559998877</dd>", html, StringComparison.Ordinal);

        // None of the other spellings is rendered anywhere as a contact value.
        foreach (var spelling in new[] { "&#x2B;971509724162", "&#x2B;971 50 972 4162", "971-50-972-4162" })
        {
            Assert.DoesNotContain(spelling, html, StringComparison.Ordinal);
        }
    }

    /// <summary>When the source holds neither, the page falls back to a source label and an em dash — it never fabricates a name or an address.</summary>
    [Fact]
    public async Task CustomerProfile_PactCustomerWithNoNameOrEmail_FallsBackSafely()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext%3APact%3A7002"));

        Assert.Contains("PACT customer", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Email</dt><dd>&#x2014;</dd>", html, StringComparison.Ordinal);
        // Arabic stays blank — PACT has no Arabic name and the English one is never copied into it.
        Assert.Contains("<dt>Full Name Arabic</dt><dd>&#x2014;</dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Other email</dt>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Also called from</dt>", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Ticket Details <-> Customer Profile
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketDetails_ShowsTheCustomerAsALinkedSummaryCard_WithTheWorkspaceTabs()
    {
        var html = await Ok(await Client().GetAsync("/Tickets/5"));

        // The card: name links to the profile, the action too, carrying the ticket back.
        Assert.Contains("class=\"customer-card\"", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"customer-card__name\" href=\"/Customers/crm%3A9001?fromTicket=5\">Mariam Al Falasi</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/crm%3A9001?fromTicket=5\">View Customer Profile</a>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Customer Name</dt><dd>Mariam Al Falasi</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Phone</dt><dd class=\"text-mono\">&#x2B;971501234567</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Verification Source</dt><dd>Verified via Tiger CRM</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Project / Unit</dt><dd>T-1204 &#xB7; Tiger Tower</dd>", html, StringComparison.Ordinal);

        // Overview | Activity | Customer | Approvals | Attachments, Overview selected. The conversation lives in the Overview now,
        // so there is no separate Interactions tab.
        foreach (var label in new[] { ">Overview</label>", ">Activity <span", ">Customer", ">Approvals", ">Attachments</label>" })
        {
            Assert.Contains(label, html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("id=\"tab-conversations\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"conversation-title\">Conversation History", html, StringComparison.Ordinal);

        Assert.Contains("id=\"tab-details\" class=\"tab-input\" checked", html, StringComparison.Ordinal);
        Assert.Contains("No attachments on this ticket.", html, StringComparison.Ordinal);
        Assert.Contains("No approvals or dependencies apply.", html, StringComparison.Ordinal);
        // The breadcrumb still leads back into the Tickets workspace.
        Assert.Contains("<a href=\"/Tickets\">Tickets</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldTicketCustomerAddress_RedirectsToTheTicketsCustomerProfile()
    {
        var response = await Client().GetAsync("/Tickets/5/Customer");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Customers/crm%3A9001?fromTicket=5", response.Headers.Location!.ToString());
    }

    // ---------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Agent"),
                new Claim(ClaimTypes.Role, Roles.CsAgent),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>Enough of TigerCS.Api for the customer pages: two known customers (a CRM Buyer with two tickets, a phone-only caller) and the ticket reads behind them.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);

        private static readonly CustomerDirectoryRowDto Mariam = new(
            "crm:9001", "Crm", "Mariam Al Falasi", "+971501234567", "Tiger Tower", "T-1310", "Crm", 1, 2, 6, "TG-CS-00006", "InProgress", Now.AddDays(-1), Now.AddHours(-3));

        private static readonly CustomerDirectoryRowDto Caller = new(
            "phone:%2B971501112222", "Phone", null, "+971501112222", "Marina Heights", "M-401", "Unverified", 1, 1, 7, "TG-CS-00007", "Open", Now.AddDays(-3), null);

        private static readonly CustomerDirectoryProfileDto MariamProfile = new(
            "crm:9001", "Crm", "Mariam Al Falasi", ["+971501234567"], [], "Crm", 9001, null, null, 1, 2, Now.AddDays(-10), Now.AddDays(-1), 6,
            [
                new CustomerDirectoryUnitDto("Tiger Tower", "T-1310", "Crm", 1, Now.AddDays(-1), 42, null),
                new CustomerDirectoryUnitDto("Tiger Tower", "T-1204", "Crm", 1, Now.AddDays(-10), 41, null),
            ],
            [
                new CustomerDirectoryTicketDto(6, "TG-CS-00006", "InProgress", 2, 2, Now.AddDays(-1), "Tiger Tower", "T-1310", "Leak under the sink", Now.AddHours(-2), null, "Verified"),
                new CustomerDirectoryTicketDto(5, "TG-CS-00005", "Closed", 3, 2, Now.AddDays(-10), "Tiger Tower", "T-1204", "AC not cooling", Now.AddDays(-4), Now.AddDays(-4), "Verified"),
            ],
            [
                new CustomerDirectoryInteractionDto(1, 6, "TG-CS-00006", 3, "WhatsApp", "Inbound", Now.AddHours(-3), Now.AddHours(-2), "Ended", "Amal Agent", true),
            ]);

        /// <summary>
        /// A PACT-verified customer: PACT supplied customerName and
        /// customerEmail, and the four captured numbers are the SAME mobile
        /// written four ways plus one genuinely different second line.
        /// </summary>
        private static readonly CustomerDirectoryProfileDto PactProfile = new(
            "ext:Pact:7001", "External", "Fatima Noor",
            ["971509724162", "+971509724162", "+971 50 972 4162", "971-50-972-4162", "+971559998877"],
            ["fatima@example.com"],
            "Pact", null, "Pact", "7001", 1, 1, Now.AddDays(-4), Now.AddDays(-4), 8,
            [new CustomerDirectoryUnitDto("Palm Residence", "P-08", "Pact", 1, Now.AddDays(-4), null, "41230")],
            [new CustomerDirectoryTicketDto(8, "TG-CS-00008", "Open", 3, 2, Now.AddDays(-4), "Palm Residence", "P-08", "Handover query", Now.AddDays(-4), null, "Unverified")],
            []);

        /// <summary>A PACT-verified customer PACT holds no name or email for — the page must fall back, never invent.</summary>
        private static readonly CustomerDirectoryProfileDto NamelessPactProfile = new(
            "ext:Pact:7002", "External", null, ["+971500000009"], [], "Pact", null, "Pact", "7002", 1, 1,
            Now.AddDays(-6), Now.AddDays(-6), 9,
            [],
            [new CustomerDirectoryTicketDto(9, "TG-CS-00009", "Open", 3, 2, Now.AddDays(-6), null, null, "Handover query", Now.AddDays(-6), null, "Unverified")],
            []);

        private static readonly CustomerDirectoryProfileDto CallerProfile = new(
            "phone:%2B971501112222", "Phone", null, ["+971501112222"], [], "Unverified", null, null, null, 1, 1, Now.AddDays(-3), Now.AddDays(-3), 7,
            [new CustomerDirectoryUnitDto("Marina Heights", "M-401", "Manual", 1, Now.AddDays(-3), null, null)],
            [new CustomerDirectoryTicketDto(7, "TG-CS-00007", "Open", 4, 2, Now.AddDays(-3), "Marina Heights", "M-401", "Noise complaint", Now.AddDays(-3), null, "Unverified")],
            []);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Agent", [Roles.CsAgent], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/departments/2/users" => new PagedResultDto<DepartmentUserDto>([new DepartmentUserDto(ViewerId, "Test Agent", true, [Roles.CsAgent])], 1, 100, 1),
                "/api/channels" => new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2) },
                "/api/customers" => query["search"] == "nobody"
                    ? new CustomerDirectoryListResultDto([], 0, 1, 25)
                    : new CustomerDirectoryListResultDto([Mariam, Caller], 2, 1, 25),
                "/api/customers/profile/crm:9001" => MariamProfile,
                "/api/customers/profile/phone:%2B971501112222" => CallerProfile,
                "/api/customers/profile/ext:Pact:7001" => PactProfile,
                "/api/customers/profile/ext:Pact:7002" => NamelessPactProfile,
                "/api/tickets/5" => Detail(5, "T-1204"),
                "/api/tickets/6" => Detail(6, "T-1310"),
                "/api/tickets/5/customer-history" or "/api/tickets/6/customer-history" => new CustomerHistoryDto(
                    "Verified", 9001, "+971501234567", "Mariam Al Falasi", 2, 1, 1,
                    [new CustomerHistoryTicketDto(6, "TG-CS-00006", Now.AddDays(-1), "InProgress", 2, 1, 2, "Tiger Tower", "T-1310", "Verified", "Leak under the sink")],
                    CustomerKey: "crm:9001"),
                "/api/tickets/6/customer-profile" => new CustomerProfileDto(
                    9001, "Found", "Mariam Al Falasi", "مريم الفلاسي", "+971501234567", "mariam@example.test",
                    [new CustomerProfileUnitDto(42, "Tiger Tower", "T-1310", 1, "Handover", 2, 13), new CustomerProfileUnitDto(41, "Tiger Tower", "T-1204", 1, "Handover", 2, 12)]),
                "/api/tickets/5/interactions" => new TicketInteractionHistoryDto(5, []),
                "/api/tickets/6/interactions" => new TicketInteractionHistoryDto(6, []),
                "/api/tickets/5/approvals" or "/api/tickets/6/approvals" => new TicketApprovalsViewDto([], [], [], null, null, false),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private static TicketDetailDto Detail(long id, string unit) =>
            new(id, $"TG-CS-{id:D5}", 2, 2, ViewerId, null, null, 1, 2, "Open", "Verified", "None", "Running", null, null,
                "AC not cooling", 0, Now.AddDays(-10), Convert.ToBase64String(new byte[8]),
                CrmBuyerCustomerId: 9001, CrmBuyerLeadId: 90010, CrmBuyerUnitId: 41, CrmBuyerProjectId: 7,
                CrmBuyerCustomerName: "Mariam Al Falasi", CrmBuyerProjectName: "Tiger Tower", CrmBuyerUnitNumber: unit);
    }
}
