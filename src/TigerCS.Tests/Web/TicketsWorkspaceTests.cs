// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;
using TigerCsWeb::TigerCS.Web.Services.Auth;

namespace TigerCS.Tests.Web;

/// <summary>
/// The unified Tickets workspace: the primary navigation is Dashboard |
/// Customers | Tickets | Administration; Queue, Pending Interactions, My
/// Tickets and Closed are views (tabs) of the one Tickets page, each still
/// running the query its former page ran; the selected view survives a
/// refresh (it is in the URL) and a trip into Ticket Details (it is
/// remembered for the breadcrumb and back links); and the retired
/// Pending Customer Interactions address redirects into the workspace.
/// </summary>
public sealed class TicketsWorkspaceTests
{
    private static readonly Guid ViewerId = Guid.NewGuid();

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string View(params string[] pathUnderPages) =>
        File.ReadAllText(SourceFile(Path.Combine(["TigerCS.Web", "Pages", .. pathUnderPages])));

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

    private static DefaultHttpContext GivePageContext(PageModel model, ClaimsPrincipal principal, string? queryString = null, string? cookieHeader = null)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        if (queryString is not null) httpContext.Request.QueryString = new QueryString(queryString);
        if (cookieHeader is not null) httpContext.Request.Headers.Cookie = cookieHeader;
        model.PageContext = new PageContext(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor()));
        return httpContext;
    }

    private static Dictionary<string, string> Query(string url)
    {
        var parsed = HttpUtility.ParseQueryString(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri
            ? new Uri(url).Query
            : url.Contains('?') ? url[url.IndexOf('?')..] : string.Empty);
        return parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k]!);
    }

    private static AgentHandoffDto Handoff(long id, string status = "Requested") => new(
        id, 100 + id, $"TG-{id}", 1, null, 2, 3, status, "Callback", null, DateTime.UtcNow.AddMinutes(-30),
        null, null, null, null, null, null, null, "Mariam", "+971501234567", "Needs a call back", "Open", true, false, null);

    /// <summary>A workspace model wired to one fake Api: tickets, handoffs, channels and users all answered by the same responder.</summary>
    private static (TicketsModel Model, FakeApiHandler Handler) CreateModel(
        Func<HttpRequestMessage, string?, HttpResponseMessage>? responder = null)
    {
        var handler = new FakeApiHandler(responder ?? DefaultResponder);
        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };
        var model = new TicketsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketNameResolver(
                new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance),
                new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)),
            new ChannelsApiClient(Client(), NullLogger<ChannelsApiClient>.Instance),
            new RequestTypesApiClient(Client(), NullLogger<RequestTypesApiClient>.Instance),
            new PendingCustomerInteractionsApiClient(Client(), NullLogger<PendingCustomerInteractionsApiClient>.Instance),
            new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance));
        return (model, handler);
    }

    private static HttpResponseMessage DefaultResponder(HttpRequestMessage request, string? body)
    {
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        if (pathAndQuery.StartsWith("/api/tickets?", StringComparison.Ordinal))
        {
            // Distinct totals per predicate, so each tab's badge can be traced to its own query.
            var q = Query(pathAndQuery);
            var total = q.ContainsKey("ownerEmployeeId") ? 3 : q.TryGetValue("ticketStatus", out var status) ? status == "Closed" ? 7 : 1 : q.ContainsKey("excludeClosed") ? 40 : 42;
            return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new TicketListResultDto([], total, 1, 1));
        }

        if (pathAndQuery.StartsWith("/api/pending-customer-interactions?", StringComparison.Ordinal))
        {
            var q = Query(pathAndQuery);
            return q.ContainsKey("unassignedOnly") && q["pageSize"] == "1"
                ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, new AgentHandoffListResultDto([], 5, 1, 1))
                : FakeApiHandler.JsonResponse(HttpStatusCode.OK, new AgentHandoffListResultDto([Handoff(1), Handoff(2, "InProgress")], 2, 1, 20));
        }

        if (pathAndQuery.StartsWith("/api/channels", StringComparison.Ordinal))
        {
            return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2) });
        }

        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    }

    private static Task GetAsync(TicketsModel model, string? view = null, Guid? ownerEmployeeId = null, string? ticketStatus = null,
        string? sla = null, string? assignee = null, int? departmentId = null, bool mineOnly = false, byte? channelId = null, int page = 1, string? search = null, byte? priorityId = null) =>
        model.OnGetAsync(
            departmentId, priorityId, ticketStatus, null, ownerEmployeeId, search, null, null, page, 20, CancellationToken.None,
            channelId: channelId, view: view, sla: sla, assignee: assignee, mineOnly: mineOnly);

    // ---------------------------------------------------------------
    // 1. Primary navigation: Dashboard | Customers | Tickets | Administration
    // ---------------------------------------------------------------

    [Fact]
    public void PrimaryNav_IsDashboardCustomersTicketsAdministration_AndNothingElse()
    {
        var nav = View("Shared", "_Nav.cshtml");

        foreach (var item in new[] { "Add(\"dashboard\", \"Dashboard\", \"/Dashboard\"", "Add(\"customers\", \"Customers\", \"/Customers\"", "Add(\"tickets\", \"Tickets\", \"/Tickets\"", "(\"admin\", \"Administration\", \"/Admin\")" })
        {
            Assert.Contains(item, nav, StringComparison.Ordinal);
        }

        // The four consolidated items are gone from the top level — by label, by key and by link.
        foreach (var gone in new[] { "\"Queue\"", "Pending Interactions", "My Tickets", "\"Closed\"", "\"queue\"", "\"pending\"", "\"my\"", "\"closed\"", "/PendingCustomerInteractions", "ownerEmployeeId=", "ticketStatus=Closed" })
        {
            Assert.DoesNotContain(gone, nav, StringComparison.Ordinal);
        }

        // Administration keeps its visibility rule.
        Assert.Contains("AdministrationPolicy.AppliesTo(currentUser)", nav, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tickets.cshtml")]
    [InlineData("TicketDetails.cshtml")]
    [InlineData("NewTicket.cshtml")]
    public void EveryTicketPage_LightsTheTicketsNavItem(string page)
    {
        var html = View(page);

        Assert.Contains("ViewData[\"ActiveNav\"] = \"tickets\";", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewData[\"ActiveNav\"] = \"queue\";", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWebPage_StillLinksToTheRetiredTopLevelItems()
    {
        var pagesDir = Path.Combine(SourceFile("TigerCS.Web"), "Pages");
        foreach (var file in Directory.EnumerateFiles(pagesDir, "*.cshtml", SearchOption.AllDirectories))
        {
            var html = File.ReadAllText(file);
            Assert.DoesNotContain("/PendingCustomerInteractions", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Back to Ticket Queue", html, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ActiveNav\"] = \"pending\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ActiveNav\"] = \"my\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ActiveNav\"] = \"closed\"", html, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(pagesDir, "PendingCustomerInteractions.cshtml")));
        Assert.False(File.Exists(Path.Combine(pagesDir, "PendingCustomerInteractions.cshtml.cs")));
    }

    // ---------------------------------------------------------------
    // 2. The Tickets page: title, action, tabs with counts, views
    // ---------------------------------------------------------------

    [Fact]
    public void TicketsPage_HasTheWorkspaceTitle_TheGatedNewTicketAction_AndFourTabsWithCounts()
    {
        var page = View("Tickets.cshtml");

        Assert.Contains("<h1 class=\"page-title\">Tickets</h1>", page, StringComparison.Ordinal);
        Assert.Contains("@if (Model.CanCreateTicket)", page, StringComparison.Ordinal);
        Assert.Contains("<a class=\"btn btn-gold\" href=\"/NewTicket\">+ New Ticket</a>", page, StringComparison.Ordinal);
        Assert.Contains("class=\"view-tabs\"", page, StringComparison.Ordinal);
        Assert.Contains("@foreach (var view in TicketsViews.All)", page, StringComparison.Ordinal);
        Assert.Contains("Model.TabCount(view)", page, StringComparison.Ordinal);
        Assert.Contains("view-tabs__count", page, StringComparison.Ordinal);

        // The two view bodies are partials of the one page — not copies of the old pages.
        Assert.Contains("_PendingInteractionsView", page, StringComparison.Ordinal);
        Assert.Contains("_TicketListView", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketListView_OffersTheWorkspaceFiltersAndSearch_AndPagination()
    {
        var list = View("Shared", "_TicketListView.cshtml");

        Assert.Contains("name=\"search\"", list, StringComparison.Ordinal);
        foreach (var filter in new[] { "name=\"departmentId\"", "name=\"ticketStatus\"", "name=\"priorityId\"", "name=\"sla\"", "name=\"assignee\"", "name=\"createdFrom\"", "name=\"createdTo\"", "name=\"verificationStatus\"" })
        {
            Assert.Contains(filter, list, StringComparison.Ordinal);
        }

        Assert.Contains("<input type=\"hidden\" name=\"view\" value=\"@viewKey\" />", list, StringComparison.Ordinal);
        Assert.Contains("class=\"pagination\"", list, StringComparison.Ordinal);
        Assert.Contains("_TicketRow", list, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketList_HasNoPriorityVerificationOrSlaColumns_ButTheirFiltersRemain_AndDetailsStillShowThem()
    {
        var list = View("Shared", "_TicketListView.cshtml");
        var row = View("Shared", "_TicketRow.cshtml");

        // The columns are gone from the header and every row (Queue, My Tickets and Closed share this table)...
        foreach (var column in new[] { "<th>Priority</th>", "<th>Verification</th>", "<th>SLA</th>" })
        {
            Assert.DoesNotContain(column, list, StringComparison.Ordinal);
        }

        foreach (var cell in new[] { "badge-priority-", "badge-verification-", "badge-sla-" })
        {
            Assert.DoesNotContain(cell, row, StringComparison.Ordinal);
        }

        // ...the columns that stay are untouched, and so is every filter control.
        foreach (var column in new[] { "<th>Status</th>", "<th>Ticket</th>", "<th>Assigned Department</th>", "<th>Assigned To</th>", "<th>Created</th>" })
        {
            Assert.Contains(column, list, StringComparison.Ordinal);
        }

        foreach (var filter in new[] { "name=\"priorityId\"", "name=\"sla\"", "name=\"verificationStatus\"" })
        {
            Assert.Contains(filter, list, StringComparison.Ordinal);
        }

        // Ticket Details keeps all three.
        var details = View("TicketDetails.cshtml");
        Assert.Contains("badge-priority-", details, StringComparison.Ordinal);
        Assert.Contains("badge-verification-", details, StringComparison.Ordinal);
        Assert.Contains("SLA Information", details, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingInteractionsView_KeepsItsFiltersAndActions_PostingBackIntoTheWorkspace()
    {
        var pending = View("Shared", "_PendingInteractionsView.cshtml");

        foreach (var filter in new[] { "name=\"channelId\"", "name=\"departmentId\"", "name=\"mineOnly\"", "name=\"unassignedOnly\"", "name=\"includeResolved\"" })
        {
            Assert.Contains(filter, pending, StringComparison.Ordinal);
        }

        foreach (var handler in new[] { "asp-page-handler=\"Start\"", "asp-page-handler=\"Complete\"", "asp-page-handler=\"Cancel\"" })
        {
            Assert.Contains(handler, pending, StringComparison.Ordinal);
        }

        Assert.Contains("asp-page=\"/Tickets\"", pending, StringComparison.Ordinal);
        Assert.Contains("asp-all-route-data=\"@Model.PendingRouteValues\"", pending, StringComparison.Ordinal);
        // Still no channel-delivery action — Genesys owns that.
        Assert.DoesNotContain("href=\"tel:", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("wa.me", pending, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, TicketsView.Queue)]
    [InlineData("queue", TicketsView.Queue)]
    [InlineData("all", TicketsView.All)]
    [InlineData("ALL", TicketsView.All)]
    [InlineData("pending", TicketsView.Pending)]
    [InlineData("my", TicketsView.My)]
    [InlineData("closed", TicketsView.Closed)]
    [InlineData("CLOSED", TicketsView.Closed)]
    [InlineData("nonsense", TicketsView.Queue)]
    public void ViewKey_SelectsTheView_AndIsInTheUrl_SoARefreshKeepsIt(string? key, TicketsView expected)
    {
        Assert.Equal(expected, TicketsViews.Resolve(key, null, ViewerId, null));
        Assert.Equal(expected == TicketsView.Queue ? "/Tickets" : $"/Tickets?view={expected.Key()}", expected.Href());
    }

    [Fact]
    public void OldMyTicketsAndClosedLinks_StillLandOnTheirTabs()
    {
        // The former nav links: /Tickets?ownerEmployeeId={me} and /Tickets?ticketStatus=Closed.
        Assert.Equal(TicketsView.My, TicketsViews.Resolve(null, ViewerId, ViewerId, null));
        Assert.Equal(TicketsView.Closed, TicketsViews.Resolve(null, null, ViewerId, "Closed"));
        // Someone else's tickets are a filtered Queue, not "My Tickets"; an explicit view always wins.
        Assert.Equal(TicketsView.Queue, TicketsViews.Resolve(null, Guid.NewGuid(), ViewerId, null));
        Assert.Equal(TicketsView.Queue, TicketsViews.Resolve("queue", ViewerId, ViewerId, "Closed"));
    }

    [Fact]
    public async Task QueueView_IsTheDefault_AndCallsTheTicketQueueEndpoint_WithTabCountsFromEachTabsOwnQuery()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));

        await GetAsync(model);

        Assert.Equal(TicketsView.Queue, model.View);
        Assert.Equal(40, model.TotalCount);

        // Tab badges: Queue = everything but Closed, All = unfiltered total, My = ownerEmployeeId=viewer, Closed = ticketStatus=Closed, Pending = unassigned handoffs.
        Assert.Equal(40, model.TabCount(TicketsView.Queue));
        Assert.Equal(42, model.TabCount(TicketsView.All));
        Assert.Equal(3, model.TabCount(TicketsView.My));
        Assert.Equal(7, model.TabCount(TicketsView.Closed));
        Assert.Equal(5, model.TabCount(TicketsView.Pending));

        var pendingCount = Assert.Single(handler.Requests, r => r.RequestUri.Contains("/api/pending-customer-interactions?", StringComparison.Ordinal));
        Assert.Equal("true", Query(pendingCount.RequestUri)["unassignedOnly"]);
        Assert.Equal("1", Query(pendingCount.RequestUri)["pageSize"]);
    }

    [Fact]
    public async Task QueueView_ExcludesClosed_ButKeepsSearchFiltersAndPaging()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));

        await GetAsync(model, view: "queue", search: "lease", priorityId: 2);

        var list = handler.Requests.First(r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        var q = Query(list.RequestUri);
        Assert.Equal("true", q["excludeClosed"]);
        Assert.Equal("lease", q["search"]);
        Assert.Equal("2", q["priorityId"]);
        Assert.Equal("1", q["page"]);
        Assert.False(q.ContainsKey("ticketStatus"));
    }

    [Fact]
    public async Task AllView_ListsEveryVisibleTicket_IncludingClosed_WithNoStatusRestriction()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));

        await GetAsync(model, view: "all");

        Assert.Equal(TicketsView.All, model.View);
        Assert.Equal(42, model.TotalCount);
        var list = handler.Requests.First(r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        var q = Query(list.RequestUri);
        Assert.False(q.ContainsKey("excludeClosed"));
        Assert.False(q.ContainsKey("ticketStatus"));
        Assert.False(q.ContainsKey("ownerEmployeeId"));
    }

    [Fact]
    public async Task MyTicketsAndPendingViews_DoNotExcludeClosed()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));

        await GetAsync(model, view: "my");

        var list = handler.Requests.First(r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        Assert.False(Query(list.RequestUri).ContainsKey("excludeClosed"));
    }

    [Fact]
    public async Task MyTicketsView_FixesTheOwnerToTheViewer_ExactlyAsTheOldLinkDid()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));

        await GetAsync(model, view: "my", ownerEmployeeId: Guid.NewGuid());

        Assert.Equal(TicketsView.My, model.View);
        Assert.Equal(ViewerId, model.OwnerEmployeeId);
        var list = handler.Requests.First(r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        Assert.Equal(ViewerId.ToString(), Query(list.RequestUri)["ownerEmployeeId"]);
        Assert.False(Query(list.RequestUri).ContainsKey("ticketStatus"));
    }

    [Fact]
    public async Task ClosedView_FixesTheStatusToClosed_ExactlyAsTheOldLinkDid()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));

        await GetAsync(model, view: "closed", ticketStatus: "Open");

        Assert.Equal(TicketsView.Closed, model.View);
        var list = handler.Requests.First(r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        Assert.Equal("Closed", Query(list.RequestUri)["ticketStatus"]);
    }

    [Fact]
    public async Task SlaAndAssigneeFilters_MapOntoTheQueueEndpointsExistingPredicates()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));
        var assignee = Guid.NewGuid();

        await GetAsync(model, sla: "breached", assignee: assignee.ToString());
        var list = handler.Requests.First(r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        Assert.Equal("true", Query(list.RequestUri)["slaBreached"]);
        Assert.Equal(assignee.ToString(), Query(list.RequestUri)["ownerEmployeeId"]);
        Assert.Equal("breached", model.SlaFilterValue);
        Assert.Equal(assignee.ToString(), model.AssigneeFilterValue);

        var (queueModel, queueHandler) = CreateModel();
        GivePageContext(queueModel, Principal(Roles.CsAgent));
        await GetAsync(queueModel, sla: "dueToday", assignee: "queue");
        var queueList = queueHandler.Requests.First(r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        Assert.Equal("true", Query(queueList.RequestUri)["dueToday"]);
        Assert.Equal("true", Query(queueList.RequestUri)["inDepartmentQueue"]);
        Assert.Equal("queue", queueModel.AssigneeFilterValue);
    }

    [Fact]
    public async Task PendingView_CallsThePendingInteractionsEndpoint_WithItsOwnFilters_AndNotTheTicketList()
    {
        var (model, handler) = CreateModel();
        GivePageContext(model, Principal(Roles.CsAgent));

        await GetAsync(model, view: "pending", mineOnly: true, channelId: 3, departmentId: 2, page: 2);

        Assert.Equal(TicketsView.Pending, model.View);
        Assert.Equal(ApiOutcome.Success, model.PendingOutcome);
        Assert.Equal(2, model.PendingRows.Count);
        Assert.Equal("WhatsApp", model.PendingRows[0].ChannelName);
        Assert.Equal(2, model.PendingTotalCount);

        var list = Assert.Single(handler.Requests, r => r.RequestUri.Contains("/api/pending-customer-interactions?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        var query = Query(list.RequestUri);
        Assert.Equal(ViewerId.ToString(), query["assignedEmployeeId"]);
        Assert.Equal("3", query["channelId"]);
        Assert.Equal("2", query["departmentId"]);
        Assert.Equal("2", query["page"]);

        // No ticket list is fetched for this view — only the small counters.
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri.Contains("/api/tickets?", StringComparison.Ordinal) && r.RequestUri.Contains("pageSize=20", StringComparison.Ordinal));
        Assert.Empty(model.Rows);

        // The action forms carry the view and its filters back.
        Assert.Equal("pending", model.PendingRouteValues["view"]);
        Assert.Equal("true", model.PendingRouteValues["mineOnly"]);
        Assert.Equal("3", model.PendingRouteValues["channelId"]);
        // "page" is the reserved Razor Pages route key and would be dropped; the page returns via the "returnPage" form field instead.
        Assert.DoesNotContain("page", model.PendingRouteValues.Keys);
    }

    [Fact]
    public async Task PendingActions_CallTheSameEndpoints_ThenRedirectBackToTheSameFilteredView()
    {
        var (model, handler) = CreateModel((request, body) =>
            request.Method == HttpMethod.Post
                ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, Handoff(9, "InProgress"))
                : DefaultResponder(request, body));
        GivePageContext(model, Principal(Roles.CsAgent), queryString: "?view=pending&channelId=3&mineOnly=true&handler=Start");

        var result = await model.OnPostStartAsync(9, CancellationToken.None);

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Tickets?view=pending&channelId=3&mineOnly=true", redirect.Url);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.RequestUri.EndsWith("/api/pending-customer-interactions/9/start", StringComparison.Ordinal));
        Assert.Null(model.PendingActionError);

        var cancelled = await model.OnPostCancelAsync(9, reason: "  ", CancellationToken.None);
        Assert.IsType<LocalRedirectResult>(cancelled);
        Assert.Equal("A reason is required to cancel pending customer work.", model.PendingActionError);
    }

    [Theory]
    [InlineData(true, Roles.CsAgent)]
    [InlineData(true, Roles.CsSupervisor)]
    [InlineData(true, Roles.SystemAdministrator)]
    [InlineData(false, Roles.DepartmentEmployee)]
    [InlineData(false, Roles.CsManager)]
    [InlineData(false, Roles.ReportingUser)]
    public async Task NewTicketAction_IsShownOnlyToRolesTheApiLetsCreateTickets(bool expected, string role)
    {
        var (model, _) = CreateModel();
        GivePageContext(model, Principal(role));

        await GetAsync(model);

        Assert.Equal(expected, model.CanCreateTicket);
        Assert.Equal(expected, TicketCreationPolicy.AppliesTo(CurrentUser.FromPrincipal(Principal(role))));
    }

    // ---------------------------------------------------------------
    // 3. Context: the selected view/filters survive a trip into Ticket Details
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketsPage_RemembersItsViewAndFilters_InTheContextCookie()
    {
        var (model, _) = CreateModel();
        var httpContext = GivePageContext(model, Principal(Roles.CsAgent), queryString: "?view=my&priorityId=2&page=3&evil=1");

        await GetAsync(model, view: "my", page: 3);

        var setCookie = httpContext.Response.Headers.SetCookie.ToArray()
            .Single(h => h is not null && h.StartsWith(TicketsContext.CookieName + "=", StringComparison.Ordinal))!;
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);

        var value = Uri.UnescapeDataString(setCookie[(TicketsContext.CookieName.Length + 1)..setCookie.IndexOf(';')]);
        var context = TicketsContext.FromCookieValue(value);
        Assert.Equal(TicketsView.My, context.View);
        Assert.Equal("/Tickets?view=my&priorityId=2&page=3", context.Href);
    }

    [Fact]
    public void ContextCookie_RoundTrips_AndDropsAnythingItDoesNotUnderstand()
    {
        var query = new Dictionary<string, StringValues>
        {
            ["view"] = "queue",
            ["ticketStatus"] = "Open",
            ["page"] = "2",
            ["handler"] = "Start",
            ["redirect"] = "https://evil.example",
        };

        var value = TicketsContext.ToCookieValue(TicketsView.Closed, query);
        var context = TicketsContext.FromCookieValue(value);

        // The resolved view wins over a stale view= in the query; unknown keys never come back.
        Assert.Equal(TicketsView.Closed, context.View);
        Assert.Equal("/Tickets?view=closed&ticketStatus=Open&page=2", context.Href);

        Assert.Equal(TicketsContext.Default, TicketsContext.FromCookieValue(null));
        Assert.Equal(TicketsContext.Default, TicketsContext.FromCookieValue(""));
        Assert.Equal("/Tickets?view=pending", TicketsContext.FromCookieValue("view=pending&junk=1").Href);
        Assert.Equal("/Tickets", TicketsContext.FromCookieValue("nothing=here").Href);
    }

    [Fact]
    public async Task TicketDetails_LeadsBackToTheRememberedView_OrToTheWorkspaceWhenNothingIsRemembered()
    {
        var handler = new FakeApiHandler((request, _) =>
            request.RequestUri!.PathAndQuery.StartsWith("/api/tickets/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };
        TicketDetailsModel Details() => new(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance),
            new TicketNameResolver(
                new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance),
                new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)));

        var remembered = Details();
        GivePageContext(remembered, Principal(Roles.CsAgent), cookieHeader: $"{TicketsContext.CookieName}=view%3Dpending%26channelId%3D3");
        await remembered.OnGetAsync(5, null, CancellationToken.None);
        Assert.Equal(TicketsView.Pending, remembered.ReturnContext.View);
        Assert.Equal("/Tickets?view=pending&channelId=3", remembered.ReturnContext.Href);

        var fresh = Details();
        GivePageContext(fresh, Principal(Roles.CsAgent));
        await fresh.OnGetAsync(5, null, CancellationToken.None);
        Assert.Equal(TicketsContext.Default, fresh.ReturnContext);

        // The view renders that context as the breadcrumb's middle crumb and the back link.
        var details = View("TicketDetails.cshtml");
        Assert.Contains("<a href=\"@Model.ReturnContext.Href\">@Model.ReturnContext.View.Label()</a>", details, StringComparison.Ordinal);
        Assert.Contains("Back to @Model.ReturnContext.View.Label()", details, StringComparison.Ordinal);
        // The Customer Profile now lives under Customers (its own workspace);
        // Ticket Details links to it carrying the ticket, and the profile's
        // "Back to ticket" returns here — see CustomerWorkspaceRenderTests.
        var profile = View("CustomerProfile.cshtml");
        Assert.Contains("ViewData[\"ActiveNav\"] = \"customers\";", profile, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // 4. Old links: the retired address redirects into the workspace; the dashboard opens the My Tickets tab
    // ---------------------------------------------------------------

    [Fact]
    public async Task RetiredPendingCustomerInteractionsAddress_RedirectsPermanentlyIntoTheWorkspace_KeepingItsFilters()
    {
        using var factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var bare = await client.GetAsync("/PendingCustomerInteractions");
        Assert.Equal(HttpStatusCode.MovedPermanently, bare.StatusCode);
        Assert.Equal("/Tickets?view=pending", bare.Headers.Location!.ToString());

        var filtered = await client.GetAsync("/PendingCustomerInteractions?channelId=3&mineOnly=true&page=2");
        Assert.Equal(HttpStatusCode.MovedPermanently, filtered.StatusCode);
        Assert.Equal("/Tickets?view=pending&channelId=3&mineOnly=true&page=2", filtered.Headers.Location!.ToString());
    }

    [Fact]
    public void DashboardMyTicketsKpi_OpensTheMyTicketsTab()
    {
        var filters = new DashboardAppliedFiltersDto(new DateOnly(2026, 8, 12), new DateOnly(2026, 9, 10), null, null, null, null, null, null);

        var query = Query(DashboardLinks.MyTickets(filters, ViewerId));

        Assert.Equal("my", query["view"]);
        Assert.Equal(ViewerId.ToString(), query["ownerEmployeeId"]);
        // Every ticket currently assigned, in any status: no active-only narrowing, so the list matches the card.
        Assert.False(query.ContainsKey("activeOnly"));
    }

    [Fact]
    public void SiteCss_StylesTheViewTabs_AndLetsThemScrollOnNarrowScreens()
    {
        var css = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "css", "site.css")));

        Assert.Contains(".view-tabs__link.is-active", css, StringComparison.Ordinal);
        Assert.Contains(".view-tabs__count", css, StringComparison.Ordinal);
        Assert.Contains(".view-tabs__list { display: flex; align-items: stretch; gap: 2px; overflow-x: auto;", css, StringComparison.Ordinal);
    }
}
