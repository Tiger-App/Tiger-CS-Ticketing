// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Operational Dashboard page (Dashboard Phase 1): it renders whatever
/// the Api returns (including nothing), every KPI and bar row drills into
/// the EXISTING ticket queue with the matching filter, the filter bar is
/// built from the Api's own option lists, and the customer search quick
/// action survives unchanged.
/// </summary>
public sealed class DashboardUiTests
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly DashboardAppliedFiltersDto DefaultFilters =
        new(new DateOnly(2026, 8, 12), new DateOnly(2026, 9, 10), null, null, null, null, null, null);

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private static ClaimsPrincipal Principal(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ViewerId.ToString()),
            new(ClaimTypes.Name, "Test Agent")
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static void GivePageContext(PageModel model, ClaimsPrincipal principal) =>
        model.PageContext = new PageContext(new ActionContext(
            new DefaultHttpContext { User = principal }, new RouteData(), new PageActionDescriptor()));

    private static (DashboardModel Model, FakeApiHandler Handler) CreateModel(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder, params string[] roles)
    {
        var handler = new FakeApiHandler(responder);
        var client = new DashboardApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<DashboardApiClient>.Instance);
        var model = new DashboardModel(client);
        GivePageContext(model, Principal(roles.Length == 0 ? [Roles.CsAgent] : roles));
        return (model, handler);
    }

    private static DashboardOverviewDto Overview(
        DashboardAppliedFiltersDto? filters = null, DashboardKpisDto? kpis = null, int volumeTotal = 0,
        IReadOnlyList<DashboardBreakdownItemDto>? byChannel = null,
        IReadOnlyList<DashboardBreakdownItemDto>? byStatus = null,
        IReadOnlyList<DashboardBreakdownItemDto>? ageing = null,
        IReadOnlyList<DashboardRecentTicketDto>? recent = null,
        DashboardFilterOptionsDto? options = null) =>
        new(
            filters ?? DefaultFilters,
            kpis ?? new DashboardKpisDto(0, 0, 0, 0, 0, 0),
            volumeTotal,
            byChannel ?? [],
            [],
            [],
            byStatus ?? [],
            [],
            ageing ?? Enum.GetNames<Application.Modules.Ticketing.Abstractions.BacklogAgeBucket>()
                .Select(b => new DashboardBreakdownItemDto(b, b, 0, 0)).ToList(),
            recent ?? [],
            options ?? new DashboardFilterOptionsDto([], [], [], [], [], []));

    private static DashboardOverviewDto EmptyOverview() => Overview();

    private static IReadOnlyDictionary<string, string> Query(string href)
    {
        var parsed = HttpUtility.ParseQueryString(new Uri("http://localhost" + href).Query);
        return parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k]!);
    }

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string View(params string[] parts) =>
        File.ReadAllText(SourceFile(Path.Combine(["TigerCS.Web", "Pages", .. parts])));

    // ---------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------

    [Fact]
    public async Task Dashboard_LoadsSuccessfully_AndBuildsSixKpiCardsAndSixBreakdownCards()
    {
        var overview = Overview(
            kpis: new DashboardKpisDto(12, 3, 4, 1, 2, 5),
            volumeTotal: 10,
            byChannel: [new DashboardBreakdownItemDto("1", "Phone", 7, 70), new DashboardBreakdownItemDto(null, "No channel recorded", 3, 30)],
            byStatus: [new DashboardBreakdownItemDto("InProgress", "InProgress", 6, 60), new DashboardBreakdownItemDto("Resolved", "Resolved", 4, 40)]);
        var (model, handler) = CreateModel((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, overview));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Equal(ApiOutcome.Success, model.Outcome);
        Assert.NotNull(model.Overview);
        Assert.Contains(handler.Requests, r => r.RequestUri.Contains("api/dashboard/overview", StringComparison.Ordinal));

        // The approved KPI set, in order — the same six for every role; the
        // Api already scoped the numbers.
        Assert.Equal(["Open Tickets", "My Tickets", "In Department Queue", "SLA Breached", "Due Today", "Pending Approval"],
            model.Cards.Select(c => c.Label).ToArray());
        Assert.Equal([12, 3, 4, 1, 2, 5], model.Cards.Select(c => c.Value).ToArray());
        Assert.All(model.Cards, c => Assert.StartsWith("/Tickets?", c.Href));

        Assert.Equal(["Volume by Channel", "Volume by Request Type", "Status", "Volume by Department", "Open Backlog Ageing", "Priority"],
            model.BreakdownCards.Select(c => c.Title).ToArray());
        var channel = model.BreakdownCards.Single(c => c.Title == "Volume by Channel");
        Assert.Equal(10, channel.Total);
        Assert.Equal("70%", channel.Rows[0].PercentageText);
        Assert.Equal("bar-fill--w70", channel.Rows[0].BarWidthClass);
        // Status labels are humanized from the lifecycle names, never invented.
        var status = model.BreakdownCards.Single(c => c.Title == "Status");
        Assert.Equal(["In Progress", "Resolved"], status.Rows.Select(r => r.Label).ToArray());
    }

    [Fact]
    public async Task Dashboard_RendersCleanly_WithNoDataAtAll()
    {
        var (model, _) = CreateModel((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, EmptyOverview()));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(model.Overview);
        Assert.Equal(6, model.Cards.Count);
        Assert.All(model.Cards, c => Assert.Equal(0, c.Value));
        Assert.Equal(6, model.BreakdownCards.Count);
        Assert.All(model.BreakdownCards, c => Assert.Equal(0, c.Total));
        // No NaN, no invalid width: an empty widget's rows are all 0%.
        Assert.All(model.BreakdownCards.SelectMany(c => c.Rows), r =>
        {
            Assert.Equal("0%", r.PercentageText);
            Assert.Equal("bar-fill--w0", r.BarWidthClass);
        });
        Assert.Empty(model.Overview.RecentTickets);
        Assert.Equal("Aug 12 – Sep 10, 2026", model.PeriodLabel);
        Assert.False(model.HasFilters);
    }

    [Fact]
    public async Task Dashboard_WhenTheApiFails_ShowsTheErrorState_NotAnException()
    {
        var (model, _) = CreateModel((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Null(model.Overview);
        Assert.Empty(model.Cards);
        Assert.NotEqual(ApiOutcome.Success, model.Outcome);
    }

    // ---------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------

    [Fact]
    public async Task Dashboard_PassesEveryFilterToTheApi_AndBuildsPickersFromTheApisOptions()
    {
        var options = new DashboardFilterOptionsDto(
            [new DashboardFilterOptionDto("2", "Collections")],
            [new DashboardFilterOptionDto(ViewerId.ToString(), "Hadi Head")],
            [new DashboardFilterOptionDto("1", "Phone"), new DashboardFilterOptionDto("2", "App / Website (Legacy)", null, false)],
            [new DashboardFilterOptionDto("9", "Payment Reminder", 2)],
            [new DashboardFilterOptionDto("Open", "Open")],
            [new DashboardFilterOptionDto("1", "Critical")]);
        var (model, handler) = CreateModel((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, Overview(options: options)));

        await model.OnGetAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 10), 2, ViewerId, 1, 9, "InProgress", 2, CancellationToken.None);

        var query = Query(new Uri(handler.Requests.Single().RequestUri).PathAndQuery);
        Assert.Equal("2026-09-01", query["dateFrom"]);
        Assert.Equal("2026-09-10", query["dateTo"]);
        Assert.Equal("2", query["departmentId"]);
        Assert.Equal(ViewerId.ToString(), query["ownerEmployeeId"]);
        Assert.Equal("1", query["channelId"]);
        Assert.Equal("9", query["requestTypeId"]);
        Assert.Equal("InProgress", query["ticketStatus"]);
        Assert.Equal("2", query["priorityId"]);

        Assert.True(model.HasFilters);
        Assert.Equal("Collections", model.DepartmentLabel(2));
        Assert.Equal("Other departments", model.DepartmentLabel(99));
    }

    [Fact]
    public void DashboardView_FilterBar_IsBuiltFromTheApisOptions_NeverHardCoded()
    {
        var html = View("Dashboard.cshtml");

        foreach (var name in new[] { "dateFrom", "dateTo", "departmentId", "ownerEmployeeId", "channelId", "requestTypeId", "ticketStatus", "priorityId" })
        {
            Assert.Contains($"name=\"{name}\"", html);
        }

        // Every picker iterates the Api's option list for this caller's scope.
        Assert.Contains("options!.Departments", html);
        Assert.Contains("options.Agents", html);
        Assert.Contains("options.Channels", html);
        Assert.Contains("options.RequestTypes", html);
        Assert.Contains("options.Statuses", html);
        Assert.Contains("options.Priorities", html);
        // No literal status or priority list in the view.
        Assert.DoesNotContain("new[] { \"Open\"", html);
        Assert.DoesNotContain("p <= 4", html);
        // Department change refreshes the dependent agent/request-type pickers; clear resets everything.
        Assert.Contains("name=\"departmentId\" data-autosubmit", html);
        Assert.Contains("href=\"/Dashboard\">Clear</a>", html);
    }

    // ---------------------------------------------------------------
    // Drill-down links
    // ---------------------------------------------------------------

    [Fact]
    public async Task KpiDrilldowns_OpenTheExistingTicketQueue_WithTheMatchingFilter()
    {
        var filters = DefaultFilters with { DepartmentId = 2, ChannelId = 6 };
        var (model, _) = CreateModel((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, Overview(filters)));

        await model.OnGetAsync(null, null, 2, null, 6, null, null, null, CancellationToken.None);

        var byLabel = model.Cards.ToDictionary(c => c.Label, c => Query(c.Href!));

        Assert.Equal("true", byLabel["Open Tickets"]["activeOnly"]);
        Assert.False(byLabel["My Tickets"].ContainsKey("activeOnly"));
        Assert.Equal(ViewerId.ToString(), byLabel["My Tickets"]["ownerEmployeeId"]);
        Assert.Equal("true", byLabel["In Department Queue"]["inDepartmentQueue"]);
        Assert.Equal("true", byLabel["SLA Breached"]["slaBreached"]);
        Assert.Equal("true", byLabel["Due Today"]["dueToday"]);
        Assert.Equal("true", byLabel["Pending Approval"]["pendingApproval"]);

        // The dashboard's dimension filters travel with every KPI; the date
        // range does not — KPIs are current-state.
        Assert.All(byLabel.Values, q =>
        {
            Assert.Equal("2", q["departmentId"]);
            Assert.Equal("6", q["channelId"]);
            Assert.False(q.ContainsKey("createdFrom"));
        });
        Assert.All(model.Cards, c => Assert.StartsWith(DashboardLinks.TicketsPath + "?", c.Href));
    }

    [Fact]
    public void BarRowDrilldowns_ProduceTheMatchingQueueFilter_PerDimension()
    {
        var f = DefaultFilters with { OwnerEmployeeId = ViewerId };

        var channel = Query(DashboardLinks.Channel(f, "6"));
        Assert.Equal("6", channel["channelId"]);
        Assert.Equal("2026-08-12", channel["createdFrom"]);
        Assert.Equal("2026-09-10", channel["createdTo"]);
        Assert.Equal(ViewerId.ToString(), channel["ownerEmployeeId"]);

        Assert.Equal("9", Query(DashboardLinks.RequestType(f, "9"))["requestTypeId"]);
        Assert.Equal("3", Query(DashboardLinks.Department(f, "3"))["departmentId"]);
        Assert.Equal("InProgress", Query(DashboardLinks.Status(f, "InProgress"))["ticketStatus"]);
        Assert.Equal("1", Query(DashboardLinks.Priority(f, "1"))["priorityId"]);

        // Backlog age is current-state: bucket only, no date range.
        var age = Query(DashboardLinks.BacklogAge(f, "OneToThreeDays"));
        Assert.Equal("OneToThreeDays", age["backlogAge"]);
        Assert.False(age.ContainsKey("createdFrom"));

        // A selected status/priority is replaced, not duplicated, by the row's own value.
        var replaced = Query(DashboardLinks.Status(f with { TicketStatus = "Open" }, "Closed"));
        Assert.Equal("Closed", replaced["ticketStatus"]);
    }

    [Fact]
    public async Task BarRows_LinkOnlyWhenTheValueIsRecorded_OnTheTicket()
    {
        var overview = Overview(
            volumeTotal: 4,
            byChannel: [new DashboardBreakdownItemDto("1", "Phone", 3, 75), new DashboardBreakdownItemDto(null, "No channel recorded", 1, 25)]);
        var (model, _) = CreateModel((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, overview));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        var rows = model.BreakdownCards.Single(c => c.Title == "Volume by Channel").Rows;
        Assert.Equal("1", Query(rows[0].Href!)["channelId"]);
        Assert.Null(rows[1].Href);
        Assert.Equal("No channel recorded", rows[1].Label);
    }

    [Fact]
    public void BarCard_KeepsLongListsCompact_BehindViewAll()
    {
        var rows = Enumerable.Range(1, 11).Select(i => new DashboardBarRow($"Type {i}", 1, 9.1, "/Tickets")).ToList();
        var card = new DashboardBarCard("Volume by Request Type", "period", rows, 11, "/Tickets", "none");

        Assert.Equal(DashboardBarCard.TopRowCount, card.TopRows.Count);
        Assert.Equal(3, card.MoreRows.Count);

        var html = View("Dashboard.cshtml");
        Assert.Contains("View all (@Model.Rows.Count)", View("Shared", "_DashboardBarCard.cshtml"));
        Assert.Contains("_DashboardBarCard", html);
        Assert.Contains("_DashboardBarRow", View("Shared", "_DashboardBarCard.cshtml"));
    }

    [Fact]
    public void BarRow_FormatsPercentagesConsistently_AndClampsTheBar()
    {
        Assert.Equal("33.3%", new DashboardBarRow("a", 1, 33.3, null).PercentageText);
        Assert.Equal("50%", new DashboardBarRow("a", 1, 50.0, null).PercentageText);
        Assert.Equal("0%", new DashboardBarRow("a", 0, 0, null).PercentageText);
        Assert.Equal("bar-fill--w100", new DashboardBarRow("a", 1, 140, null).BarWidthClass);
    }

    // ---------------------------------------------------------------
    // Bar width without an inline style (Content-Security-Policy)
    // ---------------------------------------------------------------

    [Theory]
    // The percentage still drives the visual width: the class carries it,
    // rounded to the nearest whole point and clamped to the track.
    [InlineData(0, "bar-fill--w0")]
    [InlineData(0.4, "bar-fill--w0")]
    [InlineData(0.6, "bar-fill--w1")]
    [InlineData(33.3, "bar-fill--w33")]
    [InlineData(58.3, "bar-fill--w58")]
    [InlineData(41.7, "bar-fill--w42")]
    [InlineData(99.9, "bar-fill--w100")]
    [InlineData(100, "bar-fill--w100")]
    [InlineData(140, "bar-fill--w100")]
    [InlineData(-5, "bar-fill--w0")]
    [InlineData(double.NaN, "bar-fill--w0")]
    [InlineData(double.PositiveInfinity, "bar-fill--w0")]
    public void BarRow_TurnsThePercentageIntoAWidthClass_NeverAnInlineStyle(double percentage, string expectedClass)
    {
        var row = new DashboardBarRow("a", 1, percentage, null);

        Assert.Equal(expectedClass, row.BarWidthClass);
        Assert.InRange(row.BarWidthPercent, 0, 100);
    }

    [Fact]
    public void BarRowView_DrawsTheFillWithAClass_AndEmitsNoStyleAttribute()
    {
        var partial = View("Shared", "_DashboardBarRow.cshtml");

        Assert.Contains("class=\"bar-fill @Model.BarWidthClass\"", partial);
        Assert.Contains("class=\"bar-fill bar-fill--muted @Model.BarWidthClass\"", partial);
        Assert.DoesNotContain("style=", partial);
        Assert.DoesNotContain("BarWidth\"", partial);
    }

    [Fact]
    public void NoViewEmitsAnInlineStyleAttribute_BecauseTheCspForbidsInlineStyles()
    {
        // A style attribute would be dropped by the browser under
        // "style-src 'self'", so it can never carry anything the page needs.
        var pagesDirectory = Path.Combine(SourceFile("TigerCS.Web"), "Pages");
        var offenders = Directory
            .EnumerateFiles(pagesDirectory, "*.cshtml", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("style=", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(pagesDirectory, file))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void SiteCss_DefinesEveryBarWidthClass_ZeroToOneHundred()
    {
        var css = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "css", "site.css")));

        for (var percent = 0; percent <= 100; percent++)
        {
            // Each class the page can ask for resolves, and to its own width.
            Assert.Contains($".bar-fill--w{percent} {{ width: {percent}%; }}", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ContentSecurityPolicy_IsUnchanged_AndStillForbidsInlineStyles()
    {
        // The fix must not have relaxed the policy: styles and scripts stay
        // self-hosted only, with no 'unsafe-inline' / 'unsafe-eval' anywhere.
        var programCs = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Program.cs")));
        var policyStart = programCs.IndexOf("\"Content-Security-Policy\"", StringComparison.Ordinal);
        Assert.True(policyStart > 0, "The Content-Security-Policy header is missing.");
        var policy = programCs[policyStart..(programCs.IndexOf("await next();", policyStart, StringComparison.Ordinal))];

        foreach (var directive in new[] { "default-src 'self'", "script-src 'self'", "style-src 'self'", "object-src 'none'", "base-uri 'none'", "frame-ancestors 'none'" })
        {
            Assert.Contains(directive, policy, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-hashes", policy, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Recent / Critical + customer search preserved
    // ---------------------------------------------------------------

    [Fact]
    public void DashboardView_RecentCriticalTable_ShowsOperationalColumns_AndLinksToTicketDetails()
    {
        var html = View("Dashboard.cshtml");

        foreach (var column in new[] { "Ticket", "Department", "Created" })
        {
            Assert.Contains($"<th scope=\"col\">{column}</th>", html);
        }

        foreach (var removed in new[] { "Customer", "Unit / Project", "Request Type", "Priority", "Status", "Assigned To", "SLA" })
        {
            Assert.DoesNotContain($"<th scope=\"col\">{removed}</th>", html);
        }

        Assert.Contains("href=\"/Tickets/@ticket.TicketId\"", html);
        Assert.Contains("DashboardLinks.ViewAll(f)", html);
        // Names come from the Api's joined projection — no per-row name resolution on this page.
        Assert.Contains("ticket.DepartmentName", html);
        Assert.DoesNotContain("ResolveOwnerNameAsync", html);
        // Never a full description, never a misleading ownerless label.
        Assert.DoesNotContain("RequestSummary", html);
        Assert.DoesNotContain("Unassigned", html);
    }

    [Fact]
    public void DashboardView_PreservesTheCustomerSearchQuickAction_AboveTheAnalytics()
    {
        var html = View("Dashboard.cshtml");

        var search = html.IndexOf("class=\"workspace-search\"", StringComparison.Ordinal);
        var filters = html.IndexOf("id=\"dashboardFilters\"", StringComparison.Ordinal);
        var kpis = html.IndexOf("class=\"kpi-grid", StringComparison.Ordinal);

        Assert.True(search > 0, "The customer search section is missing.");
        // The redesigned dashboard leads with the customer search and the
        // KPI cards; the filter form folds behind a disclosure below them.
        Assert.True(search < kpis && kpis < filters, "Customer search must sit above the KPI cards, with the filters folded below.");
        // The same phone-search flow as before: GET /Customers?phoneNumber=…
        Assert.Contains("action=\"/Customers\" method=\"get\"", html);
        Assert.Contains("name=\"phoneNumber\"", html);
        Assert.Contains("+ New Ticket", html);
    }

    // ---------------------------------------------------------------
    // Ticket queue: drill-down parameters pass straight through
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketQueue_PassesDashboardDrilldownFilters_ToTheQueueEndpoint_AndKeepsThemAcrossPaging()
    {
        var handler = new FakeApiHandler((request, _) =>
            request.RequestUri!.PathAndQuery.StartsWith("/api/tickets", StringComparison.Ordinal)
                ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, new TicketListResultDto([], 0, 1, 20))
            : request.RequestUri.PathAndQuery.StartsWith("/api/channels", StringComparison.Ordinal)
                ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[] { new ChannelDto(6, "WhatsApp", "WHATSAPP", true, true, true, 2) })
            : request.RequestUri.PathAndQuery.StartsWith("/api/request-types", StringComparison.Ordinal)
                ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[] { new Application.Modules.Administration.Dto.RequestTypeOptionDto(9, "Payment Reminder", 2, 3, true) })
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };
        var model = new TicketsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketNameResolver(
                new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance),
                new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)),
            new ChannelsApiClient(Client(), NullLogger<ChannelsApiClient>.Instance),
            new RequestTypesApiClient(Client(), NullLogger<RequestTypesApiClient>.Instance));
        GivePageContext(model, Principal(Roles.CsAgent));

        await model.OnGetAsync(
            2, null, null, null, null, null, null, null, 1, 20, CancellationToken.None,
            channelId: 6, requestTypeId: 9, activeOnly: false, inDepartmentQueue: true, slaBreached: false, dueToday: false,
            backlogAge: "OverSevenDays", pendingApproval: true, createdFrom: new DateOnly(2026, 8, 12), createdTo: new DateOnly(2026, 9, 10));

        var queueRequest = handler.Requests.First(r =>
            r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("inDepartmentQueue", StringComparison.Ordinal));
        var query = Query(new Uri(queueRequest.RequestUri).PathAndQuery);
        Assert.Equal("2", query["departmentId"]);
        Assert.Equal("6", query["channelId"]);
        Assert.Equal("9", query["requestTypeId"]);
        Assert.Equal("true", query["inDepartmentQueue"]);
        Assert.Equal("OverSevenDays", query["backlogAge"]);
        Assert.Equal("true", query["pendingApproval"]);
        Assert.Equal("2026-08-12", query["createdFrom"]);
        Assert.Equal("2026-09-10", query["createdTo"]);
        Assert.False(query.ContainsKey("activeOnly"));

        Assert.True(model.HasDrilldown);
        Assert.Contains("In Department Queue", model.DrilldownLabels);
        Assert.Contains("Backlog age: > 7 days", model.DrilldownLabels);
        // Names, never raw ids, on the chips.
        Assert.Contains("Channel: WhatsApp", model.DrilldownLabels);
        Assert.Contains("Request type: Payment Reminder", model.DrilldownLabels);
        Assert.DoesNotContain(model.DrilldownLabels, l => l.Contains('#'));

        // The queue view (the Tickets workspace page plus its ticket-list
        // partial) carries the criteria through its own filter form and pagination.
        var view = View("Tickets.cshtml") + View("Shared", "_TicketListView.cshtml");
        foreach (var name in new[] { "channelId", "requestTypeId", "activeOnly", "inDepartmentQueue", "slaBreached", "dueToday", "backlogAge", "pendingApproval", "createdFrom", "createdTo" })
        {
            Assert.Contains($"name=\"{name}\"", view);
            Assert.Contains($"[\"{name}\"] = ", view);
        }

        Assert.Contains("Filtered from Dashboard", view);
    }

    // ---------------------------------------------------------------
    // Closure KPI cards (Reviewed, Not Reviewed, Closed by AI, Closed by Agent)
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosureCards_ShowTheApiCounts_InOrder_BeneathTheSixOriginalCards()
    {
        var overview = Overview(kpis: new DashboardKpisDto(12, 3, 4, 1, 2, 5, ReviewedTickets: 7, NotReviewedTickets: 9, ClosedByAiTickets: 6, ClosedByAgentTickets: 10));
        var (model, _) = CreateModel((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, overview));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Equal(["Reviewed Tickets", "Not Reviewed", "Closed by AI", "Closed by Agent"], model.ClosureCards.Select(c => c.Label).ToArray());
        Assert.Equal([7, 9, 6, 10], model.ClosureCards.Select(c => c.Value).ToArray());
        // The original six are untouched.
        Assert.Equal(6, model.Cards.Count);
    }

    [Fact]
    public void ClosureCardDrilldowns_OpenTheClosedTab_WithTheCategory_TheClosureDateRange_AndDimensionFilters()
    {
        var f = DefaultFilters with { DepartmentId = 2, ChannelId = 6 };

        var links = new Dictionary<string, string>
        {
            ["Reviewed"] = DashboardLinks.Reviewed(f),
            ["NotReviewed"] = DashboardLinks.NotReviewed(f),
            ["ClosedByAi"] = DashboardLinks.ClosedByAi(f),
            ["ClosedByAgent"] = DashboardLinks.ClosedByAgent(f),
        };

        foreach (var (category, href) in links)
        {
            Assert.StartsWith(DashboardLinks.TicketsPath + "?", href);
            var q = Query(href);
            Assert.Equal(category, q["closureCategory"]);
            Assert.Equal("Closed", q["ticketStatus"]);
            Assert.Equal("closed", q["view"]);
            // The range travels as the latest CLOSURE date, never the creation date.
            Assert.Equal("2026-08-12", q["closedFrom"]);
            Assert.Equal("2026-09-10", q["closedTo"]);
            Assert.False(q.ContainsKey("createdFrom"));
            Assert.False(q.ContainsKey("createdTo"));
            Assert.Equal("2", q["departmentId"]);
            Assert.Equal("6", q["channelId"]);
        }
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("InProgress")]
    [InlineData("Resolved")]
    [InlineData("Closed")]
    public void ClosureCardDrilldown_AlwaysOpensTheClosedTab_WhateverTheDashboardsStatusFilter(string statusFilter)
    {
        var q = Query(DashboardLinks.ClosedByAi(DefaultFilters with { TicketStatus = statusFilter, DepartmentId = 3 }));

        Assert.Equal("Closed", q["ticketStatus"]);
        Assert.Equal("closed", q["view"]);
        Assert.Equal("ClosedByAi", q["closureCategory"]);
        Assert.Equal("3", q["departmentId"]);
        Assert.Equal("2026-08-12", q["closedFrom"]);
    }

    [Fact]
    public async Task TheReviewCards_SayWhenTheyEvaluateTestModeReviews()
    {
        var overview = Overview(kpis: new DashboardKpisDto(0, 0, 0, 0, 0, 0, ReviewsAreTestMode: true));
        var (model, _) = CreateModel((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, overview));

        await model.OnGetAsync(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.Contains("test-mode reviews", model.ClosureCards.Single(c => c.Label == "Reviewed Tickets").Hint);
        Assert.Contains("test-mode reviews", model.ClosureCards.Single(c => c.Label == "Not Reviewed").Hint);
        Assert.DoesNotContain("test-mode", model.ClosureCards.Single(c => c.Label == "Closed by AI").Hint);
    }

    [Fact]
    public void TheDashboardView_RendersTheClosureCards_AsClickableCards_InTheSameCardMarkup()
    {
        var view = View("Dashboard.cshtml");

        Assert.Contains("Model.ClosureCards", view);
        Assert.Contains("kpi-grid--four", view);
        Assert.Contains("class=\"kpi-card kpi-card--icon tone-@KpiTone(card.Label)\" href=\"@card.Href\"", view);
    }

    [Fact]
    public void TheTicketsPage_CarriesTheClosureCategoryThroughToTheQueueEndpoint_AndEveryRefilter()
    {
        var page = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Tickets.cshtml.cs")));
        var list = View("Shared", "_TicketListView.cshtml");
        var client = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Services", "Api", "TicketsApiClient.cs")));
        var views = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Models", "TicketsView.cs")));

        Assert.Contains("string? closureCategory = null", page);
        Assert.Contains("Unclassified ? true : null, ClosureCategory, ClosedFrom, ClosedTo", page);
        Assert.Contains("[\"closureCategory\"] = Model.ClosureCategory", list);
        Assert.Contains("[\"closedFrom\"] = Model.ClosedFrom", list);
        Assert.Contains("name=\"closedTo\"", list);
        Assert.Contains("query[\"closedFrom\"]", client);
        Assert.Contains("name=\"closureCategory\"", list);
        Assert.Contains("query[\"closureCategory\"]", client);
        Assert.Contains("\"closureCategory\"", views);
    }
}
