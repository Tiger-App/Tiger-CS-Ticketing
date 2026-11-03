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
/// Next / Prev actually work when clicked: the rendered pager links are followed exactly as a browser would, against a fake API that really
/// paginates 45 tickets and records every request. Proves page 2 holds different tickets from page 1, Prev returns to the same page, the end
/// of the list disables Next, every filter survives the click, My Tickets keeps its owner filter on every page, and a stale page number past
/// the end shows the last page instead of an empty list.
/// </summary>
public sealed partial class TicketsPagerClickTests : IDisposable
{
    private const int Total = 45;
    private static readonly Guid ViewerId = Guid.NewGuid();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;
    private readonly PagingApi _api = new();

    public TicketsPagerClickTests()
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

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}\n{html[..Math.Min(html.Length, 1500)]}");
        return html;
    }

    [GeneratedRegex("TG-CS-(\\d{5})")]
    private static partial Regex TicketNumber();

    [GeneratedRegex("<a href=\"([^\"]+)\">Next</a>")]
    private static partial Regex NextLink();

    [GeneratedRegex("<a href=\"([^\"]+)\">Prev</a>")]
    private static partial Regex PrevLink();

    private static int[] Rows(string html)
    {
        var body = html[html.IndexOf("<tbody>", StringComparison.Ordinal)..html.IndexOf("</tbody>", StringComparison.Ordinal)];
        return TicketNumber().Matches(body).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToArray();
    }

    private static string? Next(string html) => NextLink().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    private static string? Prev(string html) => PrevLink().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    [Fact]
    public async Task ClickingNext_ThenPrev_WalksEveryTicketExactlyOnce_AndReturnsToTheSamePage()
    {
        using var client = Client();

        var page1 = await GetAsync(client, "/Tickets");
        Assert.Contains("Page 1 of 3", page1, StringComparison.Ordinal);
        Assert.Null(Prev(page1));                                       // Prev is disabled on the first page
        Assert.Contains("<span class=\"is-disabled\">Prev</span>", page1, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, 20), Rows(page1));

        var page2 = await GetAsync(client, Next(page1)!);
        Assert.Contains("Page 2 of 3", page2, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(21, 20), Rows(page2));             // different tickets, no repeats, none skipped

        var page3 = await GetAsync(client, Next(page2)!);
        Assert.Contains("Page 3 of 3", page3, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(41, 5), Rows(page3));
        Assert.Null(Next(page3));                                       // Next is disabled on the last page
        Assert.Contains("<span class=\"is-disabled\">Next</span>", page3, StringComparison.Ordinal);

        var back = await GetAsync(client, Prev(page3)!);
        Assert.Equal(Rows(page2), Rows(back));
        Assert.Equal(Enumerable.Range(1, 20), Rows(await GetAsync(client, Prev(back)!)));
    }

    [Fact]
    public async Task MyTickets_KeepsItsOwnerFilterAndEveryOtherFilter_WhenNextIsClicked()
    {
        using var client = Client();

        var page1 = await GetAsync(client, "/Tickets?view=my&priorityId=2&search=lobby&pageSize=10&sortBy=priority&sortDir=asc");
        var nextUrl = Next(page1)!;
        Assert.Contains("view=my", nextUrl, StringComparison.Ordinal);
        Assert.Contains("priorityId=2", nextUrl, StringComparison.Ordinal);
        Assert.Contains("search=lobby", nextUrl, StringComparison.Ordinal);
        Assert.Contains("pageSize=10", nextUrl, StringComparison.Ordinal);
        Assert.Contains("sortBy=priority", nextUrl, StringComparison.Ordinal);
        Assert.Contains("page=2", nextUrl, StringComparison.Ordinal);

        _api.Requests.Clear();
        var page2 = await GetAsync(client, nextUrl);

        Assert.Equal(Enumerable.Range(11, 10), Rows(page2));
        // The page also asks for small counters (pageSize=1); the list itself is the request for page 2.
        var request = Assert.Single(_api.Requests, r => r.GetValueOrDefault("page") == "2");
        Assert.Equal("2", request["page"]);
        Assert.Equal("10", request["pageSize"]);
        Assert.Equal("2", request["priorityId"]);
        Assert.Equal("lobby", request["search"]);
        Assert.Equal(ViewerId.ToString(), request["ownerEmployeeId"]);   // My Tickets: still the viewer's own tickets on page 2
    }

    [Fact]
    public async Task APageNumberPastTheEnd_ShowsTheLastPage_NotAnEmptyList()
    {
        using var client = Client();

        var html = await GetAsync(client, "/Tickets?page=9");

        Assert.Contains("Page 3 of 3", html, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(41, 5), Rows(html));
        Assert.DoesNotContain("No tickets match the current filters", html, StringComparison.Ordinal);
        Assert.Null(Next(html));
        Assert.Contains("page=2", Prev(html)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingInteractions_NextLinkCarriesThePage_AndTheApiReceivesIt()
    {
        using var client = Client();

        var page1 = await GetAsync(client, "/Tickets?view=pending&channelId=3");
        var nextUrl = Next(page1)!;
        Assert.Contains("view=pending", nextUrl, StringComparison.Ordinal);
        Assert.Contains("channelId=3", nextUrl, StringComparison.Ordinal);
        Assert.Contains("page=2", nextUrl, StringComparison.Ordinal);

        _api.PendingRequests.Clear();
        await GetAsync(client, nextUrl);

        Assert.Single(_api.PendingRequests, r => r.GetValueOrDefault("page") == "2");
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

    /// <summary>A fake TigerCS.Api whose ticket list really pages (45 tickets, newest number last) and remembers each list request's query.</summary>
    private sealed class PagingApi : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

        public List<Dictionary<string, string?>> Requests { get; } = [];
        public List<Dictionary<string, string?>> PendingRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Agent", [Roles.CsAgent], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/channels" => Array.Empty<ChannelDto>(),
                "/api/tickets" => List(query),
                "/api/pending-customer-interactions" => Pending(query),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private AgentHandoffListResultDto Pending(System.Collections.Specialized.NameValueCollection query)
        {
            PendingRequests.Add(query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => (string?)query[k]));
            var page = int.TryParse(query["page"], out var p) && p > 0 ? p : 1;
            var size = int.TryParse(query["pageSize"], out var s) && s > 0 ? s : 20;
            var items = Enumerable.Range(1, Total).Skip((page - 1) * size).Take(size)
                .Select(i => new AgentHandoffDto(
                    i, 5, "TG-CS-00005", 1, null, 2, 3, "Requested", "Callback", null, Now.AddMinutes(-30),
                    null, null, null, null, null, null, null, "Mariam", "+971501234567", "Needs a call back", "Open", true, false, null))
                .ToList();
            return new AgentHandoffListResultDto(items, Total, page, size);
        }

        private TicketListResultDto List(System.Collections.Specialized.NameValueCollection query)
        {
            Requests.Add(query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => (string?)query[k]));

            var page = int.TryParse(query["page"], out var p) && p > 0 ? p : 1;
            var size = int.TryParse(query["pageSize"], out var s) && s > 0 ? s : 50;
            var items = Enumerable.Range(1, Total).Skip((page - 1) * size).Take(size)
                .Select(i => new TicketSummaryDto(i, $"TG-CS-{i:D5}", 2, ViewerId, 1, 2, "Open", "Unverified", $"Ticket {i}", Now.AddMinutes(-i)))
                .ToList();
            return new TicketListResultDto(items, Total, page, size);
        }
    }
}
