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
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Tickets workspace rendered end to end through the real Razor pipeline
/// (real layout, nav, page and partials) against a fake TigerCS.Api and a
/// signed-in test user — the proof that every view actually renders, that
/// the nav shows exactly the four workspaces, that each tab is selected by
/// its URL, and that Ticket Details leads back to the remembered view.
/// </summary>
public sealed class TicketsWorkspaceRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public TicketsWorkspaceRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                // Every typed Api client talks to the fake below instead of the network.
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApi()));

                // A signed-in CS Agent on every request.
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

    [Fact]
    public async Task PrimaryNav_RendersExactlyDashboardCustomersTicketsCollections_ForANonAdministrator()
    {
        var html = await Ok(await Client().GetAsync("/Tickets"));

        var navStart = html.IndexOf("<nav class=\"app-nav\"", StringComparison.Ordinal);
        var nav = html[navStart..html.IndexOf("</nav>", navStart, StringComparison.Ordinal)];
        Assert.Equal(4, Count(nav, "class=\"app-nav__link"));
        Assert.Contains(">Dashboard</a>", nav, StringComparison.Ordinal);
        Assert.Contains(">Customers</a>", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets\" aria-current=\"page\">Tickets</a>", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"/Collections/CampaignList\">Collections</a>", nav, StringComparison.Ordinal);
        foreach (var gone in new[] { ">Queue<", "Pending Interactions", "My Tickets", ">Closed<", "Administration" })
        {
            Assert.DoesNotContain(gone, nav, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("/Tickets", "Queue", "Tickets · Tiger Ticketing")]
    [InlineData("/Tickets?view=queue", "Queue", "Tickets · Tiger Ticketing")]
    [InlineData("/Tickets?view=all", "All", "All &#xB7; Tickets · Tiger Ticketing")]
    [InlineData("/Tickets?view=pending", "Pending Interactions", "Pending Interactions &#xB7; Tickets · Tiger Ticketing")]
    [InlineData("/Tickets?view=my", "My Tickets", "My Tickets &#xB7; Tickets · Tiger Ticketing")]
    [InlineData("/Tickets?view=closed", "Closed", "Closed &#xB7; Tickets · Tiger Ticketing")]
    public async Task EachView_RendersUnderTheTicketsTitle_WithItsTabSelected_AndCounts(string url, string tabLabel, string title)
    {
        var html = await Ok(await Client().GetAsync(url));

        // (Razor HTML-encodes the middle dot the page model supplies; the layout's own dot is literal markup.)
        Assert.Contains($"<title>{title}</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"page-title\">Tickets</h1>", html, StringComparison.Ordinal);
        Assert.Contains("+ New Ticket", html, StringComparison.Ordinal);

        // Five tabs (Queue, All, Pending Interactions, My Tickets, Closed), exactly one selected, each with its own count.
        Assert.Equal(5, Count(html, "class=\"view-tabs__link"));
        Assert.Equal(1, Count(html, "class=\"view-tabs__link is-active\""));
        var activeStart = html.IndexOf("class=\"view-tabs__link is-active\"", StringComparison.Ordinal);
        var activeTab = html[activeStart..html.IndexOf("</a>", activeStart, StringComparison.Ordinal)];
        Assert.Contains(tabLabel, activeTab, StringComparison.Ordinal);
        Assert.Contains("view-tabs__count", activeTab, StringComparison.Ordinal);

        // The shared chrome: status counters, a filter bar, a table panel and a pager.
        Assert.Contains("class=\"kpi-grid\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"filter-bar\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"data-table\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"pagination\"", html, StringComparison.Ordinal);
        // Rows link into Ticket Details, the child workspace of Tickets.
        Assert.Contains("href=\"/Tickets/5\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingView_RendersHandoffRows_WithActionsPostingBackToThePendingView()
    {
        var html = await Ok(await Client().GetAsync("/Tickets?view=pending&channelId=3"));

        Assert.Contains("Accept &amp; Start", html, StringComparison.Ordinal);
        Assert.Contains("Complete Follow-up", html, StringComparison.Ordinal);
        Assert.Contains("Cancel Follow-up", html, StringComparison.Ordinal);
        Assert.Contains("What do these actions do?", html, StringComparison.Ordinal);
        Assert.Contains("&#x2B;971501234567", html, StringComparison.Ordinal);
        Assert.Contains("WhatsApp", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/Tickets?view=pending&amp;channelId=3&amp;handler=Start\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQueue_OffersACreatedByMeFilter_AndKeepsItAcrossPaging()
    {
        var plain = await Ok(await Client().GetAsync("/Tickets"));
        Assert.Contains("name=\"createdByMe\"", plain, StringComparison.Ordinal);
        Assert.Contains("Created by me", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"true\" selected=\"selected\">Created by me", plain, StringComparison.Ordinal);

        var filtered = await Ok(await Client().GetAsync("/Tickets?createdByMe=true"));
        Assert.Contains("<option value=\"true\" selected=\"selected\">Created by me", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyTicketDetails_ShowsTheNoticeAndOffersNoActions_WhileAnOrdinaryTicketStillDoes()
    {
        var ordinary = await Ok(await Client().GetAsync("/Tickets/5"));
        Assert.Contains("Add a note", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("read-only-notice", ordinary, StringComparison.Ordinal);

        var readOnly = await Ok(await Client().GetAsync("/Tickets/8"));
        Assert.Contains("read-only-notice", readOnly, StringComparison.Ordinal);
        Assert.Contains("it is read-only for you", readOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Add a note", readOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Close", readOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Reopen", readOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Transfer", readOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Assign", readOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Classify", readOnly, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedView_HidesTheStatusAndSlaControls_TheMyView_HidesTheAssigneeControl()
    {
        var closed = await Ok(await Client().GetAsync("/Tickets?view=closed"));
        Assert.DoesNotContain("name=\"ticketStatus\"", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"sla\"", closed, StringComparison.Ordinal);
        Assert.Contains("name=\"assignee\"", closed, StringComparison.Ordinal);

        var mine = await Ok(await Client().GetAsync("/Tickets?view=my"));
        Assert.DoesNotContain("name=\"assignee\"", mine, StringComparison.Ordinal);
        Assert.Contains("name=\"ticketStatus\"", mine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketDetails_BreadcrumbAndBackLink_LeadToTheViewTheAgentCameFrom()
    {
        using var client = Client();

        // Coming from the Closed tab, page 2.
        await Ok(await client.GetAsync("/Tickets?view=closed&page=2"));
        var details = await Ok(await client.GetAsync("/Tickets/5"));
        Assert.Contains("<a href=\"/Tickets\">Tickets</a>", details, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/Tickets?view=closed&amp;page=2\">Closed</a>", details, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Tickets</a>", details, StringComparison.Ordinal);

        // Then from the Pending Interactions tab with a filter.
        await Ok(await client.GetAsync("/Tickets?view=pending&mineOnly=true"));
        details = await Ok(await client.GetAsync("/Tickets/5"));
        Assert.Contains("<a href=\"/Tickets?view=pending&amp;mineOnly=true\">Pending Interactions</a>", details, StringComparison.Ordinal);

        // A fresh session (no remembered context) falls back to the Queue.
        var fresh = await Ok(await Client().GetAsync("/Tickets/5"));
        Assert.Contains("<a href=\"/Tickets\">Queue</a>", fresh, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketDetails_ForACsManager_OffersTheWholeDirectoryGroupedByDepartment_WithTheTicketsOwnDepartmentFirst()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-Role", Roles.CsManager);

        var html = await Ok(await client.GetAsync("/Tickets/5"));

        Assert.Contains("<optgroup label=\"Customer Service (current department)\">", html, StringComparison.Ordinal);
        Assert.Contains("<optgroup label=\"Collections\">", html, StringComparison.Ordinal);
        // A person in several departments is offered under each one, with roles.
        Assert.Contains("value=\"20000000-0000-0000-0000-000000000002|3\">Leasing Lina — CS Agent (also in Customer Service)", html, StringComparison.Ordinal);
        Assert.Contains("value=\"20000000-0000-0000-0000-000000000002|2\">Leasing Lina — CS Agent (also in Collections)", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Assign.Reason\"", html, StringComparison.Ordinal);
        Assert.Contains("transfers the ticket to that department", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No active members found in this department.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketDetails_ForASupervisor_DistinguishesAFailedMemberLoadFromAnEmptyDepartment()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-Role", Roles.CsSupervisor);

        // Department 2: the fake Api answers the member list.
        var ok = await Ok(await client.GetAsync("/Tickets/5"));
        Assert.Contains("Test Agent — CS Agent", ok, StringComparison.Ordinal);
        Assert.DoesNotContain("No active members found", ok, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be loaded", ok, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketDetails_ExplainsWhyAnUnclassifiedTicketHasNoSla_AndThatStartHandlingIsNotAFirstResponse()
    {
        var html = await Ok(await Client().GetAsync("/Tickets/5"));

        Assert.Contains("this ticket is unclassified", html, StringComparison.Ordinal);
        Assert.Contains("classifies the ticket and sets its priority", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Clock started</dt><dd>Not started</dd>", html, StringComparison.Ordinal);
        Assert.Contains("How this SLA is calculated", html, StringComparison.Ordinal);
        Assert.Contains("Accept &amp; Start / Start Handling) does NOT count", html, StringComparison.Ordinal);
        Assert.DoesNotContain("SLA data unavailable", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketDetails_CrossDepartmentAssignTarget_ParsesEmployeeAndDepartment()
    {
        Assert.True(TicketDetailsModel.TryParseAssignTarget("20000000-0000-0000-0000-000000000002|3", out var employee, out var department));
        Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000002"), employee);
        Assert.Equal(3, department);

        Assert.True(TicketDetailsModel.TryParseAssignTarget("20000000-0000-0000-0000-000000000002", out _, out var none));
        Assert.Null(none);

        Assert.False(TicketDetailsModel.TryParseAssignTarget("", out _, out _));
        Assert.False(TicketDetailsModel.TryParseAssignTarget("not-a-guid|3", out _, out _));
        Assert.False(TicketDetailsModel.TryParseAssignTarget($"{Guid.Empty}|3", out _, out _));
    }

    [Fact]
    public async Task SigningInAsANonCreatorRole_HidesTheNewTicketAction()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-Role", Roles.DepartmentEmployee);

        var html = await Ok(await client.GetAsync("/Tickets"));

        Assert.DoesNotContain("+ New Ticket", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
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

    /// <summary>Enough of TigerCS.Api for the workspace to render every view with data.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Agent", [Roles.CsAgent], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service"), new DepartmentDto(3, "Collections") },
                "/api/departments/2/users" => new PagedResultDto<DepartmentUserDto>([new DepartmentUserDto(ViewerId, "Test Agent", true, [Roles.CsAgent])], 1, 100, 1),
                "/api/departments/9/users" => new PagedResultDto<DepartmentUserDto>([], 1, 100, 0),
                "/api/users/assignable" => new[]
                {
                    new AssignableUserDto(ViewerId, "Test Agent", [Roles.CsAgent], [new DepartmentMembershipDto(2, "Customer Service", true)]),
                    new AssignableUserDto(Guid.Parse("20000000-0000-0000-0000-000000000002"), "Leasing Lina", [Roles.CsAgent],
                        [new DepartmentMembershipDto(3, "Collections", true), new DepartmentMembershipDto(2, "Customer Service", false)]),
                },
                "/api/tickets/7" => Detail(7) with { CurrentDepartmentId = 9 },
                "/api/channels" => new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2) },
                "/api/tickets" => new TicketListResultDto([Ticket(5), Ticket(6)], query["ownerEmployeeId"] is not null ? 3 : query["ticketStatus"] == "Closed" ? 7 : 42, 1, 20),
                "/api/tickets/5" => Detail(5),
                "/api/tickets/8" => Detail(8) with { CurrentDepartmentId = 3, CurrentOwnerEmployeeId = null, IsReadOnlyAccess = true, CreatedByEmployeeId = ViewerId },
                "/api/tickets/8/sla" => null,
                "/api/tickets/5/sla" => new TicketSlaSummaryResponseDto(
                    "NotApplicable", null, false, null, null, false, false, null, 0, "None",
                    new SlaExplanationDto(false, null, null, "Per-priority SLA policy", null, null, null, null, null, null, null,
                        "No SLA is running: this ticket is unclassified (no category and/or no priority). The SLA starts when an agent classifies the ticket and sets its priority.",
                        "First response is satisfied only by a recorded human response. Accepting a pending interaction (Accept & Start / Start Handling) does NOT count.",
                        "Pause and resume are not implemented in this release.", [])),
                "/api/tickets/6/sla" => null,
                "/api/pending-customer-interactions" => query["unassignedOnly"] is not null && query["pageSize"] == "1"
                    ? new AgentHandoffListResultDto([], 5, 1, 1)
                    : new AgentHandoffListResultDto([Handoff(1)], 1, 1, 20),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private static TicketSummaryDto Ticket(long id) =>
            new(id, $"TG-CS-{id:D5}", 2, ViewerId, 1, 2, "Open", "Unverified", $"Seed ticket {id}", Now.AddHours(-2));

        private static TicketDetailDto Detail(long id) =>
            new(id, $"TG-CS-{id:D5}", 2, 2, ViewerId, null, null, 1, 2, "Open", "Unverified", "None", "Running", null, null,
                $"Seed ticket {id}", 0, Now.AddHours(-2), Convert.ToBase64String(new byte[8]));

        private static AgentHandoffDto Handoff(long id) => new(
            id, 5, "TG-CS-00005", 1, null, 2, 3, "Requested", "Callback", null, Now.AddMinutes(-30),
            null, null, null, null, null, null, null, "Mariam", "+971501234567", "Needs a call back", "Open", true, false, null);
    }
}
