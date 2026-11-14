extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Web;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Web;

/// <summary>
/// The summary cards of a Manual Campaigns list are filters of that list: Customers (everything, and the reset), Eligible, Held back (with each customer's reason) and Already sent. Total amount is a
/// figure, not a filter. A card only narrows the rows shown: every other filter, the page (back to 1), the counts and what can be sent stay as they are, and following a card never sends anything.
/// The fake API answers like the real one: the counts and the eligible amount are computed from every customer, and only the rows are narrowed by <c>validationStatus</c>.
/// </summary>
public sealed class CollectionsCampaignCardFilterTests
{
    private static readonly DateTime Read = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly DubaiToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));
    private const string Base = "/Collections/Campaigns?render=full&stage=CurrentMonthReminder&sendingMode=Manual";
    private const int PageSize = 25;

    private static ReviewRunDto Run(bool reconciled = true, bool stale = false, string status = "Completed") => new(7, status, status, 100, DubaiToday, null,
        new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), "PACT receivables (companies 4 and 32)", "", reconciled, Read, Read, Read, Read, 1234, 56, null, true, stale);

    private static ReviewRecordDto Row(int n, string status, params string[] reasons) => new($"RK{n}", "CurrentMonth", 4, $"Card Customer {n}", $"+9715000000{n:D2}", $"c{n}@example.test", "TP140", $"TP140-{n}",
        "Unpaid", 100m * n, "AED", new DateOnly(2026, 9, 20), status, reasons.Select(r => new ReviewReasonDto(r, "Exclusion", ReviewReasons.Explain(r))).ToList(),
        "PACT", Read, null, null, "140", "Tower 140", $"300{n}");

    /// <summary>2 eligible (1, 2), 3 held back (3 and 4 excluded, 5 needs review), 2 already sent (6, 7): 7 customers.</summary>
    private static List<ReviewRecordDto> Mixed() =>
    [
        Row(1, "Ready"), Row(2, "Ready"),
        Row(3, "Excluded", ReviewReasons.ManualSharedPhone), Row(4, "Excluded", ReviewReasons.LegalNoticeReleaseRequired), Row(5, "NeedsReview", ReviewReasons.NoValidContact),
        Row(6, "AlreadySent", ReviewReasons.ContactedToday), Row(7, "AlreadySent", ReviewReasons.ContactedToday)
    ];

    private sealed class Api(FakeCollectionsApi inner) : DelegatingHandler(inner)
    {
        public ReviewRunDto? Run { get; set; } = CollectionsCampaignCardFilterTests.Run();
        public List<ReviewRecordDto> Records { get; set; } = Mixed();
        public List<string> RecordQueries { get; } = [];
        public List<string> All { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            All.Add(request.Method + " " + request.RequestUri.PathAndQuery);
            HttpResponseMessage Json(object? value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
            if (path.EndsWith("/runs/current")) return Json(Run);
            if (path.EndsWith("/review/records"))
            {
                RecordQueries.Add(request.RequestUri.Query);
                var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
                var counts = new ReviewCountsDto(Records.Count, Records.Count(r => r.ValidationStatus == "Ready"), Records.Count(r => r.ValidationStatus == "NeedsReview"),
                    Records.Count(r => r.ValidationStatus == "Excluded"), Records.Count(r => r.ValidationStatus == "AlreadySent"));
                // What the API does: the counts and the eligible amount come from every customer; the validation status only narrows the rows.
                var wanted = query["validationStatus"];
                var shown = Records.Where(r => wanted switch
                {
                    null or "" => true,
                    "HeldBack" => r.ValidationStatus is "NeedsReview" or "Excluded",
                    _ => r.ValidationStatus == wanted
                }).ToList();
                var page = int.TryParse(query["page"], out var p) ? p : 1;
                return Json(new ReviewPageDto(Run, counts, page, PageSize, shown.Count, shown.Skip((page - 1) * PageSize).Take(PageSize).ToList(), 0m, false,
                    Records.Where(r => r.ValidationStatus == "Ready").Sum(r => r.RemainingAmount ?? 0m)));
            }
            if (path.EndsWith("/dispatches/readiness")) return Json(new DispatchReadinessDto(true, [], true));
            if (path.EndsWith("/dispatches") && request.Method == HttpMethod.Get) return Json(new List<DispatchDto>());
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<(string Html, Api Api)> LoadAsync(string url = Base, Action<Api>? configure = null)
    {
        var api = new Api(new FakeCollectionsApi { IncludeNotEligible = true });
        configure?.Invoke(api);
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        return (await (await client.GetAsync(url)).Content.ReadAsStringAsync(), api);
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value);

    private sealed record Card(string Name, string Css, string Html, string Value)
    {
        public bool Active => Css.Contains("campaign-card--active");
        public string? Href => Regex.Match(Html, "<a [^>]*href=\"([^\"]*)\"") is { Success: true } m ? Decode(m.Groups[1].Value) : null;
    }

    /// <summary>Every summary card of the page, by its data-card-filter name ("total" for the Total amount card, which has none).</summary>
    private static Dictionary<string, Card> Cards(string html)
    {
        var cards = new Dictionary<string, Card>();
        foreach (Match m in Regex.Matches(html, "<li class=\"(campaign-card[^\"]*)\"(?: data-card-filter=\"([^\"]*)\")?>(.*?)</li>", RegexOptions.Singleline))
        {
            var inner = m.Groups[3].Value;
            var label = Regex.Match(inner, "campaign-card__label\">(?:<a [^>]*>)?([^<]+)").Groups[1].Value;
            var name = m.Groups[2].Success ? m.Groups[2].Value : label == "Total amount" ? "total" : label;
            cards[name] = new Card(name, m.Groups[1].Value, inner, Regex.Match(inner, "campaign-card__value\"[^>]*>([^<]+)<").Groups[1].Value);
        }
        return cards;
    }

    private static List<string> RowStatuses(string html) => Regex.Matches(html, "<tr data-campaign-status=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToList();
    private static List<string> RowCustomers(string html) => Regex.Matches(html, "<tr data-campaign-status=\"\\w+\">.*?<strong>(Card Customer \\d+)</strong>", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
    private static int Listed(string html) => int.Parse(Regex.Match(html, "Page \\d+ of \\d+ · (\\d+) customer").Groups[1].Value);
    private static string Query(Api api) => Assert.Single(api.RecordQueries);
    private static string Rooted(string href) => href.StartsWith('/') ? href : "/" + href;

    // ------------------------------------------------------------------ the four filter cards and the figure card

    [Fact]
    public async Task TheFourCards_AreFilterLinks_AndTotalAmountIsAFigureOnly()
    {
        var (html, _) = await LoadAsync();
        var cards = Cards(html);
        Assert.Equal(["all", "eligible", "held-back", "total", "already-sent"], cards.Keys);
        foreach (var name in new[] { "all", "eligible", "held-back", "already-sent" })
        {
            var card = cards[name];
            Assert.Contains("campaign-card--filter", card.Css);
            Assert.Matches($"<a class=\"campaign-card__link\" href=\"[^\"]*\" data-results-link data-card=\"{name}\"", card.Html);      // a plain link the page script follows in place
            Assert.DoesNotContain("<form", card.Html); Assert.DoesNotContain("<button type=\"submit\"", card.Html);                    // following a card is a GET, never an action
        }
        var total = cards["total"];
        Assert.DoesNotContain("<a ", total.Html); Assert.DoesNotContain("data-results-link", total.Html); Assert.DoesNotContain("campaign-card--filter", total.Css); Assert.False(total.Active);
        Assert.Equal("AED 300.00", total.Value);                                                                                      // 100 + 200: the eligible customers only, whatever the card
    }

    [Fact]
    public async Task ByDefault_TheCustomersCardIsActive_AndTheListIsNotNarrowed()
    {
        var (html, api) = await LoadAsync();
        var cards = Cards(html);
        Assert.True(cards["all"].Active); Assert.Equal(1, cards.Values.Count(c => c.Active));
        Assert.Matches("<a [^>]*data-card=\"all\" aria-current=\"true\"", cards["all"].Html);
        Assert.DoesNotContain("aria-current", cards["eligible"].Html);
        Assert.DoesNotContain("validationStatus", Query(api));
        Assert.Equal(7, Listed(html)); Assert.Equal(7, RowStatuses(html).Count);
    }

    [Fact]
    public async Task Eligible_ShowsOnlyEligibleCustomers_AndTheListMatchesTheCount()
    {
        var (html, api) = await LoadAsync(Base + "&card=eligible");
        var cards = Cards(html);
        Assert.Contains("validationStatus=Ready", Query(api));
        Assert.True(cards["eligible"].Active); Assert.Equal(1, cards.Values.Count(c => c.Active));
        Assert.Equal(["Ready", "Ready"], RowStatuses(html));
        Assert.Equal(["Card Customer 1", "Card Customer 2"], RowCustomers(html));
        Assert.Equal(int.Parse(cards["eligible"].Value), Listed(html)); Assert.Equal(2, Listed(html));
    }

    [Fact]
    public async Task HeldBack_ShowsOnlyHeldBackCustomers_WithTheirReasons_AndTheListMatchesTheCount()
    {
        var (html, api) = await LoadAsync(Base + "&card=held-back");
        var cards = Cards(html);
        Assert.Contains("validationStatus=HeldBack", Query(api));
        Assert.True(cards["held-back"].Active); Assert.Equal(1, cards.Values.Count(c => c.Active));
        Assert.Equal(["Excluded", "Excluded", "NeedsReview"], RowStatuses(html));
        Assert.Equal(["Card Customer 3", "Card Customer 4", "Card Customer 5"], RowCustomers(html));
        Assert.Equal(int.Parse(cards["held-back"].Value), Listed(html)); Assert.Equal(3, Listed(html));        // the tile counts Excluded + Needs review, and so does the list
        // Every held-back row says why, with the exact code and the explanation.
        foreach (var row in Regex.Matches(html, "<tr data-campaign-status=\"\\w+\">.*?</tr>", RegexOptions.Singleline).Select(m => m.Value))
            Assert.Matches("data-unit-note data-reason-code=\"\\w+\">[^<]+</span>", row);
        Assert.Contains("data-reason-code=\"ManualSharedPhone\">" + System.Net.WebUtility.HtmlEncode(ReviewReasons.Explain(ReviewReasons.ManualSharedPhone)), html);
        Assert.Contains("data-reason-code=\"LegalNoticeReleaseRequired\"", html);
        Assert.Contains("data-reason-code=\"NoValidContact\"", html);
        Assert.Contains("Held back</span>", html);                                                              // the row state of an excluded customer
    }

    [Fact]
    public async Task AlreadySent_ShowsOnlyCustomersThatAlreadyReceivedTheCampaign_AndTheListMatchesTheCount()
    {
        var (html, api) = await LoadAsync(Base + "&card=already-sent");
        var cards = Cards(html);
        Assert.Contains("validationStatus=AlreadySent", Query(api));
        Assert.True(cards["already-sent"].Active); Assert.Equal(1, cards.Values.Count(c => c.Active));
        Assert.Equal(["AlreadySent", "AlreadySent"], RowStatuses(html));
        Assert.Equal(["Card Customer 6", "Card Customer 7"], RowCustomers(html));
        Assert.Equal(int.Parse(cards["already-sent"].Value), Listed(html)); Assert.Equal(2, Listed(html));
    }

    [Theory]
    [InlineData("", "all")]
    [InlineData("&card=eligible", "eligible")]
    [InlineData("&card=held-back", "held-back")]
    [InlineData("&card=already-sent", "already-sent")]
    [InlineData("&card=ELIGIBLE", "eligible")]
    [InlineData("&card=nonsense", "all")]                    // an unknown value is the Customers card
    [InlineData("&card=", "all")]
    public async Task EveryCard_KeepsTheCountsAndTheAmount_WhateverTheCard(string card, string active)
    {
        var (html, _) = await LoadAsync(Base + card);
        var cards = Cards(html);
        Assert.Equal(["7", "2", "3", "AED 300.00", "2"], cards.Values.Select(c => c.Value));                    // the summary never changes with the card
        Assert.True(cards[active].Active); Assert.Equal(1, cards.Values.Count(c => c.Active));
        var expected = cards[active].Value;
        Assert.Equal(int.Parse(expected), Listed(html));                                                         // and the list is exactly what the active card counts
    }

    [Fact]
    public async Task TheCountsOfTheCards_AddUp_SoNoCustomerBelongsToTwoCards()
    {
        var (html, _) = await LoadAsync();
        var c = Cards(html);
        Assert.Equal(int.Parse(c["all"].Value), int.Parse(c["eligible"].Value) + int.Parse(c["held-back"].Value) + int.Parse(c["already-sent"].Value));
    }

    // ------------------------------------------------------------------ Customers resets the card

    [Fact]
    public async Task Customers_ResetsTheCardFilter_AndShowsEveryone()
    {
        var (html, _) = await LoadAsync(Base + "&card=held-back");
        var reset = Cards(html)["all"].Href!;
        Assert.DoesNotContain("card=", reset);
        Assert.Contains("stage=CurrentMonthReminder", reset); Assert.Contains("sendingMode=Manual", reset);
        var (after, api) = await LoadAsync(Rooted(reset));
        Assert.DoesNotContain("validationStatus", Query(api));
        Assert.True(Cards(after)["all"].Active); Assert.Equal(7, Listed(after));
    }

    // ------------------------------------------------------------------ every filter is preserved, the page goes back to 1

    private const string Filters = "&towerId=7&floor=1&paymentStatus=Overdue&dateFrom=2026-08-01&dateTo=2026-09-30&minTotal=250&search=Card";

    [Theory]
    [InlineData("all")]
    [InlineData("eligible")]
    [InlineData("held-back")]
    [InlineData("already-sent")]
    public async Task EachCard_KeepsEveryFilter_AndGoesBackToPageOne(string card)
    {
        var (html, _) = await LoadAsync(Base + Filters + "&card=eligible&page=3");
        var href = Cards(html)[card].Href!;
        var link = HttpUtility.ParseQueryString(new Uri("http://x" + Rooted(href)).Query);
        Assert.Equal("CurrentMonthReminder", link["stage"]); Assert.Equal("Manual", link["sendingMode"]);
        Assert.Equal("7", link["towerId"]); Assert.Equal("1", link["floor"]); Assert.Equal("Overdue", link["paymentStatus"]);
        Assert.Equal("2026-08-01", link["dateFrom"]); Assert.Equal("2026-09-30", link["dateTo"]); Assert.Equal("250", link["minTotal"]); Assert.Equal("Card", link["search"]);
        Assert.Equal("1", link["page"]);                                                                         // whatever page the person was on
        Assert.Equal(card == "all" ? null : card, link["card"]);

        // Following it asks the API for the same list: every filter, page 1, and only the status of the card.
        var (_, api) = await LoadAsync(Rooted(href));
        var query = HttpUtility.ParseQueryString(Query(api));
        Assert.Equal("7", query["towerId"]); Assert.Equal("1", query["floors"]); Assert.Equal("Overdue", query["dueStatus"]);
        Assert.Equal("2026-08-01", query["dueFrom"]); Assert.Equal("2026-09-30", query["dueTo"]); Assert.Equal("250", query["minRemaining"]); Assert.Equal("Card", query["customer"]);
        Assert.Equal("CurrentMonth", query["reminderType"]); Assert.Equal("Manual", query["sendingMode"]);
        Assert.Equal("1", query["page"]);
        Assert.Equal(card switch { "eligible" => "Ready", "held-back" => "HeldBack", "already-sent" => "AlreadySent", _ => null }, query["validationStatus"]);
    }

    [Fact]
    public async Task ThePager_KeepsTheActiveCard()
    {
        var (html, api) = await LoadAsync(Base + "&card=eligible", a => a.Records = [.. Enumerable.Range(1, 30).Select(n => Row(n, "Ready")), Row(31, "Excluded", ReviewReasons.ManualSharedPhone)]);
        Assert.Equal(25, RowStatuses(html).Count); Assert.Equal(30, Listed(html));                              // 30 eligible, two pages
        var next = Decode(Regex.Match(html, "<a class=\"btn btn-sm\" data-results-link href=\"([^\"]*)\">Next</a>").Groups[1].Value);
        Assert.Contains("card=eligible", next); Assert.Contains("page=2", next);
        var (second, secondApi) = await LoadAsync(Rooted(next), a => a.Records = [.. Enumerable.Range(1, 30).Select(n => Row(n, "Ready")), Row(31, "Excluded", ReviewReasons.ManualSharedPhone)]);
        Assert.Equal(5, RowStatuses(second).Count); Assert.All(RowStatuses(second), s => Assert.Equal("Ready", s));
        Assert.Contains("page=2", Query(secondApi)); Assert.Contains("validationStatus=Ready", Query(secondApi));
        Assert.True(Cards(second)["eligible"].Active);
        Assert.Contains("Previous", second);
    }

    // ------------------------------------------------------------------ a card never sends, and is never part of what is sent

    [Theory]
    [InlineData("")]
    [InlineData("&card=eligible")]
    [InlineData("&card=held-back")]
    [InlineData("&card=already-sent")]
    public async Task FollowingACard_SendsAndExportsNothing_AndTheApprovalFormDoesNotCarryIt(string card)
    {
        var (html, api) = await LoadAsync(Base + card);
        Assert.All(api.All, call => Assert.StartsWith("GET ", call));                                           // not a single POST: no approval, no send, no export
        Assert.DoesNotContain(api.All, call => call.Contains("/dispatches?") || call.Contains("/export/") || call.Contains("/selection/") || call.Contains("/refresh"));
        // The posted approval / export filter is the page's filter fields only: the card is a view of the list, never part of what is approved or sent.
        var form = Regex.Match(html, "<form method=\"post\" class=\"campaign-approve\".*?</form>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(form);
        Assert.DoesNotContain("name=\"card\"", form); Assert.DoesNotContain("name=\"validationStatus\"", form);
        Assert.DoesNotContain("card=", form);
        Assert.Contains("name=\"stage\"", form); Assert.Contains("name=\"sendingMode\"", form);
        Assert.Equal(4, Regex.Matches(html, "data-card-filter=").Count);
    }

    // ------------------------------------------------------------------ cards belong to the Manual list

    [Fact]
    public async Task Auto_HasNoFilterCards_AndIgnoresTheCardValue()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?render=full&card=eligible")).Content.ReadAsStringAsync();
        Assert.Contains("data-campaign-kpis", html);
        Assert.DoesNotContain("data-card-filter", html); Assert.DoesNotContain("campaign-card--filter", html); Assert.DoesNotContain("campaign-card--active", html);
        Assert.DoesNotContain(api.Requests, r => r.Contains("card=", StringComparison.Ordinal) || r.Contains("validationStatus", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the shipped script and styles

    private static string Asset(string relative, [CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(testFilePath)!, "..", "..")), "TigerCS.Web", "wwwroot", relative));

    [Fact]
    public void TheCards_AreLiftedOutOfTheResultsBox_SoThePageScriptFollowsTheirLinksThere_AndTheLinksAreGetOnly()
    {
        var script = Asset("js/receivables.js");
        Assert.Contains("[data-kpi-box]", script);                                        // collections-campaign-filters.js lifts the cards into this box, outside [data-results]
        Assert.Contains("kpiBox.addEventListener('click', followResultsLink)", script);
        Assert.Contains("a[data-results-link]", script);
        var filters = Asset("js/collections-campaign-filters.js");
        Assert.DoesNotContain("method: 'POST'", filters); Assert.DoesNotContain("data-card", filters);   // the card is plain server-rendered markup: no script state to go stale
    }

    [Fact]
    public void TheActiveCard_IsMarkedByASubtleBorder_AndTheTotalAmountCardHasNoClickStyle()
    {
        var css = Asset("css/collections-campaigns.css");
        Assert.Matches(@"\.campaign-card--active, \.campaign-card--active:hover \{ --card-edge: var\(--color-info", css);
        Assert.Matches(@"\.campaign-card--filter \{[^}]*cursor: pointer;[^}]*border-color: var\(--card-edge\)", css);
        Assert.DoesNotMatch(@"\.campaign-card \{[^}]*cursor: pointer", css);              // only the filter cards look clickable
        Assert.DoesNotContain("background: var(--color-info", css.Substring(css.IndexOf(".campaign-card--filter", StringComparison.Ordinal), 600));   // no fill, only the border
    }

    // ------------------------------------------------------------------ "Snapshot not loaded yet" next to loaded customers

    private const string Idle = "Snapshot not loaded yet";

    [Fact]
    public async Task BeforeAPreview_ManualStillSaysTheSnapshotIsNotLoaded()
    {
        var (html, _) = await LoadAsync("/Collections/Campaigns?stage=CurrentMonthReminder&sendingMode=Manual");
        Assert.Contains(Idle, html); Assert.Contains("data-snapshot-idle", html); Assert.DoesNotContain("data-snapshot-manual", html);
    }

    [Fact]
    public async Task AfterAManualPreview_TheFragmentCarriesTheStatus_SoTheScriptCanReplaceTheNotLoadedChip()
    {
        // The page script removes [data-snapshot-idle] only when a [data-snapshot-status] arrives in the fragment (lift()). Manual used to send none, because the status was built from the Auto
        // preview report, which Manual does not have: the customers and counts were shown next to "Snapshot not loaded yet".
        var (fragment, _) = await LoadAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&sendingMode=Manual");
        Assert.Contains("data-preview-current", fragment);                                                        // customers are loaded
        Assert.Contains("data-snapshot-status", fragment); Assert.Contains("data-snapshot-manual", fragment);
        Assert.True(fragment.IndexOf("data-snapshot-status", StringComparison.Ordinal) < fragment.IndexOf("data-customer-results", StringComparison.Ordinal));
        Assert.DoesNotContain(Idle, fragment);
        Assert.Contains("data-status-pill=\"reconciliation\" data-state=\"Ready\"", fragment);
        Assert.Contains("data-status-pill=\"approval\" data-state=\"Ready\"", fragment);
    }

    [Fact]
    public async Task OnAFullManualRender_TheStatusBoxShowsTheStatus_NotTheNotLoadedChip()
    {
        var (html, _) = await LoadAsync();
        var box = Regex.Match(html, "<section class=\"campaign-snapshot\" data-snapshot-box.*?</section>", RegexOptions.Singleline).Value;
        Assert.Contains("data-snapshot-status", box); Assert.DoesNotContain(Idle, html); Assert.DoesNotContain("data-snapshot-idle", html);
        Assert.Contains("Approval data", box); Assert.Contains("data-updated", box);
    }

    [Fact]
    public async Task TheManualStatus_ReportsAnUnreconciledSource_AndStaleOrOldApprovalData_NeverAsReady()
    {
        var (unreconciled, _) = await LoadAsync(configure: a => a.Run = Run(reconciled: false));
        Assert.Contains("data-status-pill=\"reconciliation\" data-state=\"Failed\"", unreconciled);
        Assert.Contains("data-source-status=\"NotReconciled\"", unreconciled); Assert.Contains("data-reconciliation-link", unreconciled);   // the blocker is never collapsed
        var (stale, _) = await LoadAsync(configure: a => a.Run = Run(stale: true));
        Assert.Contains("data-status-pill=\"approval\" data-state=\"Warning\"", stale);
        var (running, _) = await LoadAsync(configure: a => a.Run = Run(status: "Running"));
        Assert.Contains("data-status-pill=\"approval\" data-state=\"Loading\"", running);
        var (failed, _) = await LoadAsync(configure: a => a.Run = Run(status: "Failed"));
        Assert.Contains("data-status-pill=\"approval\" data-state=\"Failed\"", failed);
    }
}
