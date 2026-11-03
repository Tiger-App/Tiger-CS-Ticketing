// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// Regression: on /Tickets?view=queue&amp;pageSize=20 the footer said "Page 1 of 10" but Next went nowhere.
/// <c>page</c> is a reserved Razor Pages route value (the page name), so <c>Url.Page("/Tickets", new { page = 2 })</c>
/// silently dropped the page number and <c>int page</c> in OnGet could not bind from the query. These tests drive the real
/// Razor pipeline against a fake Api that pages like the real one (200 tickets) and records what the Web asked it for.
/// </summary>
public sealed class TicketsPaginationRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;
    private readonly PagingFakeApi _api = new();

    public TicketsPaginationRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = _api));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> Ok(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode}");
        return html;
    }

    /// <summary>The Prev/Next anchors of the pager, decoded; null for a disabled (non-link) control.</summary>
    private static (string? Prev, string? Next) Pager(string html)
    {
        var start = html.IndexOf("<nav class=\"pagination\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "pager not rendered");
        var nav = html[start..html.IndexOf("</nav>", start, StringComparison.Ordinal)];
        string? Link(string label)
        {
            var m = Regex.Match(nav, "<a href=\"([^\"]*)\">" + label + "</a>");
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        }
        return (Link("Prev"), Link("Next"));
    }

    private static string FooterText(string html) => Regex.Match(html, @"Page \d+ of \d+").Value;

    private static IDictionary<string, string> Q(string url)
    {
        var parsed = HttpUtility.ParseQueryString(new Uri("http://x" + url).Query);
        return parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k]!);
    }

    [Fact]
    public async Task Queue_FirstPage_NextLinksToPage2_PrevIsDisabled()
    {
        var html = await Ok(await Client().GetAsync("/Tickets?view=queue&pageSize=20"));

        Assert.Equal("Page 1 of 10", FooterText(html));
        var (prev, next) = Pager(html);
        Assert.Null(prev);
        Assert.NotNull(next);
        var q = Q(next!);
        Assert.Equal("2", q["page"]);
        Assert.Equal("queue", q["view"]);
        Assert.Equal("20", q["pageSize"]);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("my")]
    [InlineData("closed")]
    public async Task Next_IsFollowable_AndLoadsThatPage_ForEveryTicketView(string view)
    {
        using var client = Client();
        var first = await Ok(await client.GetAsync($"/Tickets?view={view}&pageSize=20"));
        var next = Pager(first).Next;
        Assert.NotNull(next);

        _api.Reset();
        var second = await Ok(await client.GetAsync(next!));

        Assert.Equal("Page 2 of 10", FooterText(second));
        Assert.Equal("2", _api.LastListQuery!["page"]);
        Assert.Equal("20", _api.LastListQuery["pageSize"]);
        var (prev, nextOfSecond) = Pager(second);
        Assert.Equal("1", Q(prev!)["page"]);
        Assert.Equal("3", Q(nextOfSecond!)["page"]);
        Assert.Equal(view, Q(prev!)["view"]);
    }

    [Fact]
    public async Task MiddlePage_HasBothLinks_AndLastPage_DisablesNext()
    {
        var middle = await Ok(await Client().GetAsync("/Tickets?view=queue&page=5&pageSize=20"));
        Assert.Equal("Page 5 of 10", FooterText(middle));
        var (prev, next) = Pager(middle);
        Assert.Equal("4", Q(prev!)["page"]);
        Assert.Equal("6", Q(next!)["page"]);

        var last = await Ok(await Client().GetAsync("/Tickets?view=queue&page=10&pageSize=20"));
        Assert.Equal("Page 10 of 10", FooterText(last));
        var (lastPrev, lastNext) = Pager(last);
        Assert.Equal("9", Q(lastPrev!)["page"]);
        Assert.Null(lastNext);
        Assert.Contains("<span class=\"is-disabled\">Next</span>", last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyResults_ShowPage1Of1_WithBothControlsDisabled()
    {
        _api.TotalOverride = 0;
        var html = await Ok(await Client().GetAsync("/Tickets?view=queue&pageSize=20"));

        Assert.Equal("Page 1 of 1", FooterText(html));
        var (prev, next) = Pager(html);
        Assert.Null(prev);
        Assert.Null(next);
        Assert.Contains("No tickets match the current filters.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageBeyondTheLast_IsClampedToTheLastPage_SoThePagerStaysUsable()
    {
        var html = await Ok(await Client().GetAsync("/Tickets?view=queue&page=99&pageSize=20"));

        Assert.Equal("Page 10 of 10", FooterText(html));
        Assert.Equal("10", _api.LastListQuery!["page"]);
        Assert.Equal("9", Q(Pager(html).Prev!)["page"]);
    }

    [Fact]
    public async Task PagingLinks_PreserveEveryFilterSortAndPageSize()
    {
        var url = "/Tickets?view=queue&activeOnly=true&departmentId=2&ticketStatus=Open&priorityId=2&search=lease&sortBy=createdAt&sortDir=desc&pageSize=50&page=2&inDepartmentQueue=true&slaBreached=true&createdByMe=true";
        var html = await Ok(await Client().GetAsync(url));
        // 200 tickets / 50 = 4 pages.
        Assert.Equal("Page 2 of 4", FooterText(html));
        var (prev, next) = Pager(html);

        foreach (var (link, page) in new[] { (prev!, "1"), (next!, "3") })
        {
            var q = Q(link);
            Assert.Equal(page, q["page"]);
            Assert.Equal("queue", q["view"]);
            Assert.Equal("true", q["activeOnly"]);
            Assert.Equal("2", q["departmentId"]);
            Assert.Equal("Open", q["ticketStatus"]);
            Assert.Equal("2", q["priorityId"]);
            Assert.Equal("lease", q["search"]);
            Assert.Equal("createdAt", q["sortBy"]);
            Assert.Equal("desc", q["sortDir"]);
            Assert.Equal("50", q["pageSize"]);
            Assert.Equal("true", q["inDepartmentQueue"]);
            Assert.Equal("true", q["slaBreached"]);
            Assert.Equal("true", q["createdByMe"]);
        }

        // ...and the Api received the same filters for the requested page.
        Assert.Equal("2", _api.LastListQuery!["page"]);
        Assert.Equal("50", _api.LastListQuery["pageSize"]);
        Assert.Equal("true", _api.LastListQuery["activeOnly"]);
        Assert.Equal("lease", _api.LastListQuery["search"]);
    }

    [Fact]
    public async Task MyView_PagingLinks_KeepTheViewerOwnerImplicit()
    {
        var html = await Ok(await Client().GetAsync("/Tickets?view=my&pageSize=20"));
        var q = Q(Pager(html).Next!);
        Assert.Equal("my", q["view"]);
        Assert.Equal("2", q["page"]);
        Assert.False(q.ContainsKey("ownerEmployeeId"));
        Assert.Equal(ViewerId.ToString(), _api.LastListQuery!["ownerEmployeeId"]);
    }

    [Fact]
    public async Task PendingInteractions_Next_AlsoCarriesThePageNumber()
    {
        var html = await Ok(await Client().GetAsync("/Tickets?view=pending&pageSize=20"));
        var (_, next) = Pager(html);
        Assert.NotNull(next);
        Assert.Equal("2", Q(next!)["page"]);
        Assert.Equal("pending", Q(next!)["view"]);
    }

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

    /// <summary>A fake Api that pages like the real one: 200 tickets, honouring page/pageSize, recording the list query.</summary>
    private sealed class PagingFakeApi : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        public int? TotalOverride { get; set; }
        public IDictionary<string, string>? LastListQuery { get; private set; }
        public void Reset() => LastListQuery = null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var total = TotalOverride ?? 200;

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Agent", [Roles.CsAgent], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/departments/2/users" => new PagedResultDto<DepartmentUserDto>([new DepartmentUserDto(ViewerId, "Test Agent", true, [Roles.CsAgent])], 1, 100, 1),
                "/api/channels" => new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2) },
                "/api/tickets" => ListTickets(query, total),
                "/api/pending-customer-interactions" => new AgentHandoffListResultDto([], 200, int.TryParse(query["page"], out var hp) ? hp : 1, int.TryParse(query["pageSize"], out var hs) ? hs : 20),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private TicketListResultDto ListTickets(System.Collections.Specialized.NameValueCollection query, int total)
        {
            var page = int.TryParse(query["page"], out var p) ? p : 1;
            var size = int.TryParse(query["pageSize"], out var s) ? s : 20;
            if (size != 1)
            {
                LastListQuery = query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k]!);
            }

            var items = Enumerable.Range((page - 1) * size + 1, Math.Max(0, Math.Min(size, total - (page - 1) * size)))
                .Select(i => new TicketSummaryDto(i, $"TG-CS-{i:D5}", 2, ViewerId, 1, 2, "Open", "Unverified", $"Seed ticket {i}", Now.AddHours(-2)))
                .ToList();
            return new TicketListResultDto(items, total, page, size);
        }
    }
}
