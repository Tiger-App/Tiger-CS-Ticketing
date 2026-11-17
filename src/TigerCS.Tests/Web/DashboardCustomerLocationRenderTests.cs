// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Web;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Customers by Location card and the list its counts open, end to end through the real Razor pipeline against a fake TigerCS.Api:
/// the card renders the Api's figures with green/blue segments, Unknown only when positive, both counts as links carrying every dashboard
/// filter — and the Customers page hands those same filters back to the Api and keeps them across paging and Apply.
/// </summary>
public sealed class DashboardCustomerLocationRenderTests
{
    private static readonly Guid ViewerId = Guid.NewGuid();

    internal sealed class FakeApi(DashboardCustomerLocationDto? location, bool canOpenCustomers = true) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            object? body = path switch
            {
                // A viewer denied the Customers module has an explicit permission override: the nav and the card then hide the way in.
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Supervisor", [Roles.CsSupervisor], [new DepartmentMembershipDto(2, "Customer Service", true)], true,
                    DeniedPermissions: canOpenCustomers ? null : [global::TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.CustomersView]),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/dashboard/overview" => new DashboardOverviewDto(
                    new DashboardAppliedFiltersDto(new DateOnly(2026, 8, 12), new DateOnly(2026, 9, 10), 2, null, null, null, null, null),
                    new DashboardKpisDto(0, 0, 0, 0, 0, 0), 0, [], [], [], [], [], [], [],
                    new DashboardFilterOptionsDto([new DashboardFilterOptionDto("2", "Customer Service")], [], [], [], [], []), location),
                "/api/customers" => new CustomerDirectoryListResultDto([], 0, 1, 25),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }

    private static async Task<string> GetAsync(FakeApi api, string url)
    {
        using var factory = CollectionsWebHost.Factory(api);
        using var client = factory.CreateClient();
        return await client.GetStringAsync(url);
    }

    [Fact]
    public async Task TheCard_RendersFiguresSegmentsAndLinks_FromTheApisNumbers()
    {
        var html = await GetAsync(new FakeApi(new DashboardCustomerLocationDto(30, 60, 15, 30, 5, 10, 50)), "/Dashboard");

        Assert.Contains("data-dash-card=\"customers-by-location\"", html);
        Assert.Contains("Customers by Location", html);
        Assert.Contains("data-loc=\"total\"", html);
        Assert.Contains("50 <span class=\"loc-total__label\">unique customers</span>", html);

        // Both counts are links into the Customers list, carrying the dashboard's range and department.
        var within = HttpUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "<a class=\"loc-stat loc-stat--uae\" href=\"([^\"]+)\"").Groups[1].Value);
        var outside = HttpUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "<a class=\"loc-stat loc-stat--intl\" href=\"([^\"]+)\"").Groups[1].Value);
        Assert.Equal("/Customers?location=within-uae&dateFrom=2026-08-12&dateTo=2026-09-10&departmentId=2", within);
        Assert.Equal("/Customers?location=outside-uae&dateFrom=2026-08-12&dateTo=2026-09-10&departmentId=2", outside);

        // The distribution bar: green, blue and (because there are unknowns) grey — widths are stylesheet classes, never inline styles.
        Assert.Contains("loc-seg loc-seg--uae bar-fill--w60", html);
        Assert.Contains("loc-seg loc-seg--intl bar-fill--w30", html);
        Assert.Contains("loc-seg loc-seg--unknown bar-fill--w10", html);
        Assert.Contains("data-loc=\"unknown\"", html);
        Assert.DoesNotContain("style=\"width", html);
    }

    [Fact]
    public async Task Unknown_IsAbsent_WhenItsCountIsZero()
    {
        var html = await GetAsync(new FakeApi(new DashboardCustomerLocationDto(30, 66.7, 15, 33.3, 0, 0, 45)), "/Dashboard");

        Assert.Contains("data-dash-card=\"customers-by-location\"", html);
        Assert.DoesNotContain("data-loc=\"unknown\"", html);
        Assert.DoesNotContain("loc-seg--unknown", html);
    }

    [Fact]
    public async Task AViewerWithoutTheCustomersModule_SeesTheFiguresButNoLinkToIt()
    {
        var html = await GetAsync(new FakeApi(new DashboardCustomerLocationDto(30, 66.7, 15, 33.3, 0, 0, 45), canOpenCustomers: false), "/Dashboard");

        Assert.Contains("data-dash-card=\"customers-by-location\"", html);
        Assert.DoesNotContain("<a class=\"loc-stat", html);
        Assert.Contains("loc-stat--static", html);
    }

    [Fact]
    public async Task NoCard_WhenTheApiReturnsNone_AndTheRestOfTheDashboardIsUntouched()
    {
        var html = await GetAsync(new FakeApi(null), "/Dashboard");

        Assert.DoesNotContain("customers-by-location", html);
        Assert.Contains("Operational counters", html);
    }

    [Fact]
    public async Task TheCustomersList_SendsTheLocationAndEveryDashboardFilterToTheApi_AndKeepsThemThroughApply()
    {
        var api = new FakeApi(null);
        var html = await GetAsync(api, "/Customers?location=outside-uae&dateFrom=2026-08-12&dateTo=2026-09-10&departmentId=2&channelId=3&ticketStatus=Open");

        var call = Assert.Single(api.Requests, r => r.StartsWith("/api/customers?", StringComparison.Ordinal));
        var q = HttpUtility.ParseQueryString(call[(call.IndexOf('?') + 1)..]);
        Assert.Equal("outside-uae", q["location"]);
        Assert.Equal("2026-08-12", q["dateFrom"]);
        Assert.Equal("2026-09-10", q["dateTo"]);
        Assert.Equal("2", q["departmentId"]);
        Assert.Equal("3", q["channelId"]);
        Assert.Equal("Open", q["ticketStatus"]);

        Assert.Contains("data-customers-scope=\"outside-uae\"", html);
        Assert.Contains("Outside UAE", html);
        // Pressing Apply posts the filters again, so they are carried as hidden inputs rather than silently dropped.
        Assert.Contains("<input type=\"hidden\" name=\"location\" value=\"outside-uae\" />", html);
        Assert.Contains("<input type=\"hidden\" name=\"dateFrom\" value=\"2026-08-12\" />", html);
    }

    [Fact]
    public async Task APlainCustomersList_IsUnchanged_NoLocationNoDashboardScope()
    {
        var api = new FakeApi(null);
        var html = await GetAsync(api, "/Customers");

        var call = Assert.Single(api.Requests, r => r.StartsWith("/api/customers?", StringComparison.Ordinal));
        Assert.DoesNotContain("location=", call);
        Assert.DoesNotContain("dateFrom=", call);
        Assert.DoesNotContain("data-customers-scope", html);
    }
}
