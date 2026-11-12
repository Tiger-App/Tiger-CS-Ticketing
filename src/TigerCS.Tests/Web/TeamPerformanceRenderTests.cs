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
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Services.Auth;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Team Performance report page rendered end to end through the real
/// Razor pipeline: a CS Manager sees the filter form, one row per employee
/// (every department) with clickable counts, the totals row and the help
/// block; opening a count renders the records panel; the SLA Breaches count
/// opens the SLA details page (summary, rules, highlighted, sortable,
/// paged evaluations); the nav item is offered to the CS
/// Manager tier only; a CS Agent is refused by the folder policy; and an
/// Api 403 renders as a "no permission" state, never an empty table.
/// </summary>
public sealed class TeamPerformanceRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly Guid AminaId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BilalId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CoraId = new("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime Now = new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public TeamPerformanceRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeReportsApi()));
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

    private HttpClient Client(string role)
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
    public void PrimaryDepartmentText_IsThePrimaryName_OrNotAssigned_NeverAnotherDepartment()
    {
        Assert.Equal("Not Assigned", TigerCsWeb::TigerCS.Web.Pages.Reports.TeamPerformanceModel.PrimaryDepartmentText(null));
        Assert.Equal("Legal", TigerCsWeb::TigerCS.Web.Pages.Reports.TeamPerformanceModel.PrimaryDepartmentText(new TeamPerformanceDepartmentDto(1, "Legal", true)));
    }

    [Fact]
    public void NavItem_IsOfferedToTheCsManagerTierOnly()
    {
        static CurrentUser? User(params string[] roles) => CurrentUser.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "T"), .. roles.Select(r => new Claim(ClaimTypes.Role, r))],
            authenticationType: "Test")));

        Assert.True(ReportsPolicy.AppliesTo(User(Roles.CsManager)));
        Assert.True(ReportsPolicy.AppliesTo(User(Roles.GeneralManager)));
        Assert.True(ReportsPolicy.AppliesTo(User(Roles.ChairmanCeo)));
        Assert.True(ReportsPolicy.AppliesTo(User(Roles.SystemAdministrator)));
        Assert.False(ReportsPolicy.AppliesTo(User(Roles.CsAgent)));
        Assert.False(ReportsPolicy.AppliesTo(User(Roles.CsSupervisor, Roles.DepartmentHead)));
        Assert.False(ReportsPolicy.AppliesTo(null));
    }

    [Fact]
    public async Task NavLink_RendersForACsManager_AndIsHiddenFromACsAgent()
    {
        var manager = await Ok(await Client(Roles.CsManager).GetAsync("/Dashboard"));
        Assert.Contains("href=\"/Reports/TeamPerformance\"", manager, StringComparison.Ordinal);
        Assert.Contains(">Team Performance</a>", manager, StringComparison.Ordinal);

        var agent = await Ok(await Client(Roles.CsAgent).GetAsync("/Dashboard"));
        Assert.DoesNotContain("/Reports/TeamPerformance", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("Team Performance", agent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CsAgent_IsRefusedByTheFolderPolicy_BeforeThePageRenders()
    {
        var response = await Client(Roles.CsAgent).GetAsync("/Reports/TeamPerformance");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CsManager_SeesTheReport_WithClickableCounts_TotalsAndTheHelpBlock()
    {
        var html = await Ok(await Client(Roles.CsManager).GetAsync("/Reports/TeamPerformance"));

        Assert.Contains("<h1 class=\"page-title\">Team Performance</h1>", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Team Performance</a>", html, StringComparison.Ordinal);

        // The GET filter form, built from the Api's own options.
        Assert.Contains("class=\"filter-bar report-filters\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"employeeId\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"role\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"departmentId\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Collections Officer\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"3\"", html, StringComparison.Ordinal);            // Collections, a configured department
        Assert.Contains("Amina Agent — CS Agent</option>", html, StringComparison.Ordinal);

        // One row per employee in every department, roles and departments comma-joined (primary first), each count a link.
        Assert.Contains("class=\"table-scroll\"", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"cell-primary\">Amina Agent</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"cell-primary\">Cora Collector</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Collections Officer, CS Supervisor</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Collections</td>", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\">Primary Department</th>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<th scope=\"col\">Departments</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Call Center</td>", html, StringComparison.Ordinal);                       // Bilal: primary only, not "Call Center, Customer Service"
        Assert.DoesNotContain("<td>Call Center, Customer Service</td>", html, StringComparison.Ordinal);
        // SLA Breaches opens the SLA details page, carrying the period and filters; the other counts open the records panel.
        Assert.Contains($"href=\"/Reports/TeamPerformanceSla?employeeId={AminaId}&amp;status=Breached\"", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/Reports/TeamPerformance?employeeId={BilalId}&amp;metric=TicketsWorked\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"report-count report-count--critical\"", html, StringComparison.Ordinal);

        // The SLA columns: the distinct-ticket metric is labelled as such, the events are separate, and the rest of the SLA figures follow.
        Assert.Contains("<th scope=\"col\" class=\"cell-num\">Tickets with SLA Breach</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\" class=\"cell-num\">Breach Events</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\" class=\"cell-num\">SLA Evaluated</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\" class=\"cell-num\">SLA Met</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\" class=\"cell-num\">SLA Compliance %</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"cell-num\">60%</td>", html, StringComparison.Ordinal);       // Amina: 3 / 5
        Assert.Contains("<td class=\"cell-num\">100%</td>", html, StringComparison.Ordinal);      // Bilal: 4 / 4
        Assert.Contains("<td class=\"cell-num\">&#x2014;</td>", html, StringComparison.Ordinal);         // Cora: nothing evaluated

        // Totals at the bottom.
        Assert.Contains("<tr class=\"report-totals\">", html, StringComparison.Ordinal);
        Assert.Contains("3 employees</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"cell-num\">9</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"cell-num\">77.8%</td>", html, StringComparison.Ordinal);     // combined 7 / 9, not an average of the rows

        // The definitions travel with the page.
        Assert.Contains("How these numbers are calculated", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Tickets with SLA Breach</dt>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Breach Events</dt>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>SLA Evaluated</dt>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>SLA Compliance %</dt>", html, StringComparison.Ordinal);

        // No records panel until a count is clicked; no inline styles ever.
        Assert.DoesNotContain("id=\"records\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningACount_RendersTheRecordsPanel_WithRowsLinkingToTicketDetails()
    {
        var html = await Ok(await Client(Roles.CsManager).GetAsync(
            $"/Reports/TeamPerformance?employeeId={AminaId}&metric=SlaBreaches&dateFrom=2026-08-13&dateTo=2026-09-12"));

        Assert.Contains("id=\"records\"", html, StringComparison.Ordinal);
        Assert.Contains("Tickets with SLA Breach · Amina Agent", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\">Breached</th>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<th scope=\"col\">Completed</th>", html, StringComparison.Ordinal);
        Assert.Contains("data-href=\"/Tickets/5\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets/5\">TG-CS-00005</a>", html, StringComparison.Ordinal);
        Assert.Contains("badge-sla-breached\"", html.Contains("badge-sla-breached") ? html : "badge-sla-breached\"", StringComparison.Ordinal);

        // The report above it is narrowed to the opened employee and offers the way back.
        Assert.Contains("<td class=\"cell-primary\">Amina Agent</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<td class=\"cell-primary\">Bilal Caller</td>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Reports/TeamPerformance?dateFrom=2026-08-13&amp;dateTo=2026-09-12\">Show all employees</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlaDetailsPage_RendersSummaryRules_HighlightedRecords_SortHeadersAndPager()
    {
        var html = await Ok(await Client(Roles.CsManager).GetAsync(
            $"/Reports/TeamPerformanceSla?employeeId={AminaId}&dateFrom=2026-08-13&dateTo=2026-09-12&status=Breached&role=CS%20Agent&departmentId=2"));

        Assert.Contains("SLA Details · Amina Agent", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Team Performance</a>", html, StringComparison.Ordinal);

        // Summary tiles, the formula, and the worked numbers.
        Assert.Contains("<span>SLA evaluated</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span>SLA met</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span>Breach events</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span>Tickets with SLA breach</span>", html, StringComparison.Ordinal);
        Assert.Contains("First Response: 1 · Resolution: 1", html, StringComparison.Ordinal);
        Assert.Contains("<span>Pending (not evaluated)</span>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>60%</strong>", html, StringComparison.Ordinal);
        Assert.Contains("Pending clocks are not evaluated and are excluded.", html, StringComparison.Ordinal);
        Assert.Contains("Here: 3 / (3 + 2) × 100 = 60%.", html, StringComparison.Ordinal);

        // The rules, read from the Api, and the configured policy table.
        Assert.Contains("How SLA is calculated", html, StringComparison.Ordinal);
        Assert.Contains("<dt>When the timer starts</dt>", html, StringComparison.Ordinal);
        Assert.Contains("SLA targets by priority, as configured", html, StringComparison.Ordinal);

        // Table: ticket links, red / green highlighting, UTC stamps, breach duration, policy and the reason.
        Assert.Contains("href=\"/Tickets/5\">TG-CS-00005</a>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"sla-row sla-row--breached\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"sla-row sla-row--met\"", html, StringComparison.Ordinal);
        Assert.Contains("badge badge-sla-breached\">Breached</span>", html, StringComparison.Ordinal);
        Assert.Contains("badge badge-sla-met\">Met</span>", html, StringComparison.Ordinal);
        Assert.Contains("2026-09-01 08:00", html, StringComparison.Ordinal);
        Assert.Contains("2 d 1 h (ongoing)", html, StringComparison.Ordinal);
        Assert.Contains("Medium priority", html, StringComparison.Ordinal);
        Assert.Contains("priority SLA policy", html, StringComparison.Ordinal);
        Assert.Contains("Breach recorded because the deadline passed.", html, StringComparison.Ordinal);

        // Server-side sorting + paging links keep the employee, period, filters and status.
        Assert.Contains("sortBy=due&amp;sortDir=desc", html, StringComparison.Ordinal);
        Assert.Contains("aria-sort=\"descending\"", html, StringComparison.Ordinal);
        Assert.Contains("Showing 1–2 of 42 · page 1 of 2", html, StringComparison.Ordinal);
        Assert.Contains("page=2", html, StringComparison.Ordinal);
        Assert.Contains($"employeeId={AminaId}", html, StringComparison.Ordinal);
        Assert.Contains("role=CS&#x2B;Agent", html, StringComparison.Ordinal);
        Assert.Contains("departmentId=2", html, StringComparison.Ordinal);

        // The status tabs carry the counts; the way back keeps the filters; no inline styles.
        Assert.Contains("Breach events (2)", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Reports/TeamPerformance?dateFrom=2026-08-13&amp;dateTo=2026-09-12&amp;role=CS&#x2B;Agent&amp;departmentId=2\">Back to Team Performance</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlaDetailsPage_IsRefusedForACsAgent_AndRendersStatesForApiFailures()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(Roles.CsAgent).GetAsync($"/Reports/TeamPerformanceSla?employeeId={AminaId}")).StatusCode);

        var notFound = await Ok(await Client(Roles.CsManager).GetAsync($"/Reports/TeamPerformanceSla?employeeId={Guid.NewGuid()}"));
        Assert.Contains("This employee is not on the report under the selected filters.", notFound, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"sla-table", notFound, StringComparison.Ordinal);

        FakeReportsApi.Forbidden = true;
        string forbidden;
        try { forbidden = await Ok(await Client(Roles.CsManager).GetAsync($"/Reports/TeamPerformanceSla?employeeId={AminaId}")); }
        finally { FakeReportsApi.Forbidden = false; }
        Assert.Contains("No permission", forbidden, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"sla-table", forbidden, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiForbidden_RendersANoPermissionState_NotAnEmptyTable()
    {
        // Tests in one class run sequentially, so a static switch on the fake is safe here.
        FakeReportsApi.Forbidden = true;
        string html;
        try { html = await Ok(await Client(Roles.CsManager).GetAsync("/Reports/TeamPerformance")); }
        finally { FakeReportsApi.Forbidden = false; }

        Assert.Contains("No permission", html, StringComparison.Ordinal);
        Assert.Contains("You do not have permission to view this report.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"table-scroll\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("report-filters", html, StringComparison.Ordinal);
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.TryGetValue("X-Test-Role", out var header) ? header.ToString() : Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Manager"),
                new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>Answers the two reports endpoints with a fixed team (or 403 while <see cref="Forbidden"/> is set); everything else is unavailable.</summary>
    private sealed class FakeReportsApi : HttpMessageHandler
    {
        public static volatile bool Forbidden;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (Forbidden && path.StartsWith("/api/reports/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            object? body = path switch
            {
                "/api/reports/team-performance" => Report(query["employeeId"] is { } id ? Guid.Parse(id) : null),
                "/api/reports/team-performance/records" => Records(Guid.Parse(query["employeeId"]!), query["metric"]!),
                "/api/reports/team-performance/sla-details" when Guid.Parse(query["employeeId"]!) == AminaId => SlaDetails(query),
                "/api/reports/team-performance/sla-details" => null,
                _ => null,
            };
            if (path == "/api/reports/team-performance/sla-details" && body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private static TeamPerformanceReportDto Report(Guid? onlyEmployee)
        {
            TeamPerformanceRowDto[] all =
            [
                new(AminaId, "Amina Agent", ["CS Agent"],
                    [new TeamPerformanceDepartmentDto(2, "Customer Service", true)], new TeamPerformanceDepartmentDto(2, "Customer Service", true), 3, 9, 2, 1, 2, 5, 3, 60.0m),
                new(BilalId, "Bilal Caller", ["CS Agent"],
                    [new TeamPerformanceDepartmentDto(4, "Call Center", true), new TeamPerformanceDepartmentDto(2, "Customer Service", false)], new TeamPerformanceDepartmentDto(4, "Call Center", true), 4, 6, 5, 0, 0, 4, 4, 100.0m),
                new(CoraId, "Cora Collector", ["Collections Officer", "CS Supervisor"],
                    [new TeamPerformanceDepartmentDto(3, "Collections", true)], new TeamPerformanceDepartmentDto(3, "Collections", true), 2, 3, 1, 0, 0, 0, 0, null),
            ];
            var rows = all.Where(r => onlyEmployee is null || r.EmployeeId == onlyEmployee).ToList();
            return new TeamPerformanceReportDto(
                new TeamPerformanceAppliedFiltersDto(new DateOnly(2026, 8, 13), new DateOnly(2026, 9, 12), onlyEmployee, null, null),
                rows,
                new TeamPerformanceTotalsDto(rows.Count, rows.Sum(r => r.CurrentlyAssigned), rows.Sum(r => r.TicketsWorked), rows.Sum(r => r.CompletedFollowUps),
                    rows.Sum(r => r.TicketsWithSlaBreach), rows.Sum(r => r.SlaBreachEvents), rows.Sum(r => r.SlaEvaluated), rows.Sum(r => r.SlaMet),
                    rows.Sum(r => r.SlaEvaluated) == 0 ? null : Math.Round(rows.Sum(r => r.SlaMet) * 100m / rows.Sum(r => r.SlaEvaluated), 1)),
                new TeamPerformanceFilterOptionsDto(
                    all.Select(r => new TeamPerformanceOptionDto(r.EmployeeId.ToString(), r.DisplayName, string.Join(", ", r.Roles))).ToList(),
                    [new TeamPerformanceOptionDto("4", "Call Center"), new TeamPerformanceOptionDto("3", "Collections"), new TeamPerformanceOptionDto("2", "Customer Service")],
                    ["Collections Officer", "CS Agent", "CS Supervisor"]));
        }

        private static TeamPerformanceSlaDetailsDto SlaDetails(System.Collections.Specialized.NameValueCollection query)
        {
            var due = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
            TeamPerformanceSlaRecordDto Record(long id, string number, string status, string? breachText, DateTime? actual) => new(
                id, number, "Customer Service", "Resolution", due.AddDays(-5), due.AddDays(-5), 1800, "30 h · Business hours", due, actual,
                status, status == "Pending" ? null : due, breachText is null ? null : 3000, breachText, breachText?.Contains("ongoing") == true,
                AminaId, "Amina Agent", "Medium priority · priority SLA policy · 30 h · Business hours",
                status == "Breached" ? "Breach recorded because the deadline passed." : "Recorded in time.");

            return new TeamPerformanceSlaDetailsDto(
                AminaId, "Amina Agent", ["CS Agent"], [new TeamPerformanceDepartmentDto(2, "Customer Service", true)],
                new DateOnly(2026, 8, 13), new DateOnly(2026, 9, 12), query["status"], query["sortBy"] ?? "event", query["sortDir"] ?? "desc",
                Page: 1, PageSize: 25, TotalRecords: 42, TotalPages: 2,
                new TeamPerformanceSlaSummaryDto(Evaluated: 5, Met: 3, BreachEvents: 2, TicketsWithBreach: 1, FirstResponseBreaches: 1, ResolutionBreaches: 1, Pending: 2, CompliancePercent: 60.0m),
                new TeamPerformanceSlaRulesDto(
                    [new TeamPerformanceSlaRuleDto("When the timer starts", "At ticket creation.")],
                    [new TeamPerformanceSlaPolicyDto("Medium", 240, 1800, "Business hours", true)],
                    null),
                [
                    Record(5, "TG-CS-00005", "Breached", "2 d 1 h (ongoing)", null),
                    Record(6, "TG-CS-00006", "Met", null, due.AddHours(-3))
                ]);
        }

        private static TeamPerformanceRecordsDto Records(Guid employeeId, string metric) => new(
            employeeId, employeeId == AminaId ? "Amina Agent" : "Bilal Caller",
            ["CS Agent"],
            metric, new DateOnly(2026, 8, 13), new DateOnly(2026, 9, 12),
            [
                new TeamPerformanceRecordDto(5, "TG-CS-00005", "Water leak in kitchen", "InProgress", 1, "Customer Service", Now.AddDays(-6),
                    BreachedAtUtc: metric == "SlaBreaches" ? Now.AddDays(-2) : null,
                    CompletedAtUtc: metric == "CompletedFollowUps" ? Now.AddDays(-1) : null)
            ]);
    }
}
