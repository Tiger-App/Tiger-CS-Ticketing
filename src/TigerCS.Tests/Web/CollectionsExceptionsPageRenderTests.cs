extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using TigerCS.Application.Modules.Collections.Exceptions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Web;

/// <summary>
/// "Manage Exceptions" (/Collections/Exceptions) and its entry point on Collections Campaigns, rendered by the real Web host over a fake API: GULF_CUSTOMERS units with search by unit code, tower and
/// customer; manual inclusion of the ticked units only; a clear "Manually Included" indicator; removal before sending; a permanent special case that cannot be ticked.
/// </summary>
public sealed class CollectionsExceptionsPageRenderTests
{
    private static ExceptionUnitDto Unit(int n, bool included = false, bool special = false, bool eligible = true) => new(
        $"4|T{n:D4}|TP140-{n}", 4, $"T{n:D4}", $"TP140-{n}", $"Gulf Customer {n}", "140", "Tower 140", 100m * n, "AED", included, included ? new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc) : null, included, special, eligible,
        eligible ? [] : [new ReviewReasonDto(ReviewReasons.NoValidContact, "NeedsReview", ReviewReasons.Explain(ReviewReasons.NoValidContact))]);

    private sealed class Api(FakeCollectionsApi inner) : DelegatingHandler(inner)
    {
        public List<ExceptionUnitDto> Units { get; set; } = [Unit(2), Unit(4, included: true), Unit(1, special: true), Unit(6, eligible: false)];
        public bool InclusionDay { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Calls { get; } = [];
        public string? LastChangeBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/review/exceptions")) return await base.SendAsync(request, cancellationToken);
            Calls.Add(request.Method + " " + request.RequestUri.PathAndQuery);
            if (Status != HttpStatusCode.OK) return new(Status) { Content = JsonContent.Create(new { title = "Forbidden", detail = "no", status = (int)Status }) };
            if (request.Method == HttpMethod.Get)
            {
                var search = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["search"];
                var shown = string.IsNullOrWhiteSpace(search) ? Units : Units.Where(u => u.UnitCode.Contains(search, StringComparison.OrdinalIgnoreCase) || u.CustomerName.Contains(search, StringComparison.OrdinalIgnoreCase) || (u.TowerName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ExceptionListDto("Manual:2026-10-15:CurrentMonth", "CurrentMonth", new DateOnly(2026, 10, 15), InclusionDay, shown.Count, shown.Count(u => u.ManuallyIncluded), 1, 25, shown)) };
            }
            LastChangeBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ExceptionChangeResultDto("Manual:2026-10-15:CurrentMonth", 1, 0, [])) };
        }
    }

    private static async Task<(HttpStatusCode Status, string Html)> GetAsync(Api api, string url)
    {
        using var factory = CollectionsWebHost.Factory(api);
        using var client = factory.CreateClient();
        var response = await client.GetAsync(url);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));

    [Fact]
    public async Task TheCampaignsPage_HasACompactManageExceptionsAction_ForTheChosenCampaignType()
    {
        var (status, html) = await GetAsync(new Api(new FakeCollectionsApi()), "/Collections/Campaigns?stage=FollowUpReminder&sendingMode=Manual");
        Assert.Equal(HttpStatusCode.OK, status);
        var link = Regex.Match(html, "<a[^>]*data-manage-exceptions[^>]*>Manage Exceptions</a>").Value;
        Assert.NotEmpty(link);
        Assert.Contains("href=\"/Collections/Exceptions?stage=FollowUpReminder\"", link); Assert.Contains("btn-compact", link);
    }

    [Fact]
    public async Task TheList_ShowsGulfUnits_WithTheManuallyIncludedIndicator_AndASpecialCaseThatCannotBeTicked()
    {
        var api = new Api(new FakeCollectionsApi());
        var (status, html) = await GetAsync(api, "/Collections/Exceptions?stage=CurrentMonthReminder");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("<h1 class=\"page-title\">Manage Exceptions</h1>", html);
        Assert.Contains("reminderType=CurrentMonth", Assert.Single(api.Calls));
        var rows = Regex.Matches(html, "<tr data-ex-row=\"([^\"]+)\">(.*?)</tr>", RegexOptions.Singleline).Select(m => (Key: m.Groups[1].Value, Html: m.Groups[2].Value)).ToList();
        Assert.Equal(["4|T0002|TP140-2", "4|T0004|TP140-4", "4|T0001|TP140-1", "4|T0006|TP140-6"], rows.Select(r => r.Key));
        Assert.DoesNotContain("data-ex-included", rows[0].Html);                                         // not included
        Assert.Contains("data-ex-included>Manually Included<", rows[1].Html);                           // the indicator
        Assert.Contains("data-ex-special>Permanently excluded<", rows[2].Html);
        Assert.Contains("disabled", Regex.Match(rows[2].Html, "<input[^>]*type=\"checkbox\"[^>]*>").Value);   // a special case is never selectable
        Assert.DoesNotContain("disabled", Regex.Match(rows[0].Html, "<input[^>]*type=\"checkbox\"[^>]*>").Value);
        Assert.Contains("data-ex-blocked", rows[3].Html); Assert.Contains(ReviewReasons.NoValidContact, rows[3].Html);   // the other checks still hold the unit
        Assert.Contains("Add selected to Manual campaign", html); Assert.Contains("Remove inclusion", html);
        Assert.Contains("Manual:2026-10-15:CurrentMonth", html);
        Assert.Contains("1</strong> manually included", Regex.Replace(Text(html), "\\s+", " ").Replace("1 manually included", "1</strong> manually included"));
        Assert.DoesNotContain("TP140-3", html);                                                          // only the units the API listed
    }

    [Fact]
    public async Task Search_IsOneBoxOverUnitCodeTowerAndCustomer_AndIsSentToTheApi()
    {
        var api = new Api(new FakeCollectionsApi());
        var (_, html) = await GetAsync(api, "/Collections/Exceptions?stage=CurrentMonthReminder&search=Gulf+Customer+4");
        Assert.Contains("placeholder=\"Unit code, tower or customer\"", html);
        Assert.Contains("search=Gulf+Customer+4", api.Calls.Single());
        Assert.Equal(["4|T0004|TP140-4"], Regex.Matches(html, "data-ex-row=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
        var (_, none) = await GetAsync(new Api(new FakeCollectionsApi()), "/Collections/Exceptions?stage=CurrentMonthReminder&search=nobody");
        Assert.Contains("data-ex-empty", none);
    }

    [Fact]
    public async Task OnTheFourteenth_ThePageSaysSo()
    {
        var (_, html) = await GetAsync(new Api(new FakeCollectionsApi()) { InclusionDay = true }, "/Collections/Exceptions");
        Assert.Contains("data-ex-day", html);
        var (_, other) = await GetAsync(new Api(new FakeCollectionsApi()), "/Collections/Exceptions");
        Assert.DoesNotContain("data-ex-day", other);
    }

    [Fact]
    public async Task AUserWithoutTheSendRight_SeesWhyAndNoUnits()
    {
        var (status, html) = await GetAsync(new Api(new FakeCollectionsApi()) { Status = HttpStatusCode.Forbidden }, "/Collections/Exceptions");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("data-ex-load-error", html); Assert.Contains("needs the right to send", html);
        Assert.DoesNotContain("data-ex-row", html);
    }

    [Fact]
    public async Task IncludeAndRemove_PostOnlyTheTickedUnits_ToTheApi()
    {
        var api = new Api(new FakeCollectionsApi());
        using var factory = CollectionsWebHost.Factory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await client.GetStringAsync("/Collections/Exceptions?stage=CurrentMonthReminder");
        var token = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token), new("stage", "CurrentMonthReminder"), new("page", "1"), new("unitKey", "4|T0002|TP140-2") };
        var included = await client.PostAsync("/Collections/Exceptions?handler=Include", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, included.StatusCode);
        Assert.StartsWith("/Collections/Exceptions?stage=CurrentMonthReminder", included.Headers.Location!.OriginalString);
        Assert.Contains("\"reminderType\":\"CurrentMonth\"", api.LastChangeBody); Assert.Contains("4|T0002|TP140-2", api.LastChangeBody); Assert.DoesNotContain("TP140-4", api.LastChangeBody);
        Assert.Contains(api.Calls, c => c.StartsWith("POST") && c.Contains("/exceptions/include"));

        var removed = await client.PostAsync("/Collections/Exceptions?handler=Remove", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Contains(api.Calls, c => c.StartsWith("POST") && c.Contains("/exceptions/remove"));

        var noneTicked = await client.PostAsync("/Collections/Exceptions?handler=Include", new FormUrlEncodedContent(form.Where(p => p.Key != "unitKey").ToList()));
        Assert.Equal(HttpStatusCode.Redirect, noneTicked.StatusCode);
        Assert.Equal(2, api.Calls.Count(c => c.StartsWith("POST")));                                      // nothing ticked: nothing is sent, and nothing is included by default
    }
}
