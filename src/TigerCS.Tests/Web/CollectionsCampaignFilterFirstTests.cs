extern alias TigerCsWeb;

using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>The Campaigns page is filter first: filters only on load, customers only after Preview Customers, Approve &amp; Send gated on a current, reconciled preview.</summary>
public sealed class CollectionsCampaignFilterFirstTests
{
    private const string Applied = "stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&dateFrom=2026-10-01&dateTo=2026-10-31&minTotal=250&paymentStatus=Overdue&search=3001";

    private static async Task<string> Get(HttpClient client, string url) => await (await client.GetAsync(url)).Content.ReadAsStringAsync();
    private static string Between(string html, string start, string end) { var a = html.IndexOf(start, StringComparison.Ordinal); return a < 0 ? "" : html[a..html.IndexOf(end, a, StringComparison.Ordinal)]; }

    [Theory]
    [InlineData("/Collections/Campaigns")]
    [InlineData($"/Collections/Campaigns?{Applied}")]                      // a bookmark with filters still waits for the button
    public async Task InitialLoad_ShowsFiltersOnly_AndNeverQueriesOrSends(string url)
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await Get(client, url);
        Assert.Empty(api.Calls("/campaigns/preview")); Assert.Empty(api.Calls("/campaigns/export"));
        Assert.DoesNotContain(api.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));          // nothing is approved, confirmed or uploaded to Genesys
        Assert.DoesNotContain(api.Requests, r => r.Contains("dispatch", StringComparison.OrdinalIgnoreCase) && !r.Contains("review/dispatches", StringComparison.Ordinal));
        Assert.Contains("data-results-idle", html); Assert.Contains("No customers loaded yet.", html);
        Assert.DoesNotContain("data-results-loading", html);                  // receivables.js only auto-loads a results area that says it is loading
        Assert.DoesNotContain("campaigns-table", html); Assert.DoesNotContain("data-approve-form", html); Assert.DoesNotContain("campaign-select", html);
        Assert.Contains("data-preview-button>Preview Customers</button>", html);
    }

    [Fact]
    public async Task MainFilters_AreStageTowerPaymentStatusAndPeriod_AndTheRestIsInTheCollapsedAdvancedSection()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await Get(client, "/Collections/Campaigns");
        var main = Between(html, "campaign-filters__main", "campaign-filters__footer");
        var labels = Regex.Matches(main, "<label class=\"field-label\"[^>]*>(.*?)</label>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["Campaign stage", "Project / Tower", "Payment status", "Period"], labels);
        foreach (var option in new[] { ">Up to today<", ">This month<", ">Last 6 months<", ">Custom range<" }) Assert.Contains(option, main);
        Assert.DoesNotContain("name=\"businessDate\"", main); Assert.DoesNotContain("name=\"dateFrom\"", main); Assert.DoesNotContain("name=\"minTotal\"", main);

        var details = Regex.Match(html, "<details[^>]*data-advanced-filters[^>]*>.*?</details>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain(" open", Regex.Match(details, "<details[^>]*>").Value);                       // collapsed while everything is at its default
        foreach (var name in new[] { "businessDate", "dateFrom", "dateTo", "minTotal" }) Assert.Contains($"name=\"{name}\"", details);
        Assert.Contains("Preview date", details); Assert.Contains("Minimum Total (AED)", details);
        Assert.Contains("form=\"campaignFilters\"", html);                                                  // search stays next to the results
        Assert.True(html.IndexOf("id=\"campaignSearch\"", StringComparison.Ordinal) > html.IndexOf("</form>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("minTotal=250")]
    [InlineData("businessDate=2026-10-14")]
    [InlineData("dateFrom=2026-03-05&dateTo=2026-04-09")]
    public async Task AdvancedFilters_OpenWhenOneHoldsANonDefaultValue_SoAnAppliedFilterIsNeverHidden(string query)
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await Get(client, $"/Collections/Campaigns?{query}");
        Assert.Matches("<details[^>]*data-advanced-filters", html);
        Assert.Contains(" open", Regex.Match(html, "<details[^>]*data-advanced-filters[^>]*>").Value);
    }

    [Fact]
    public async Task Period_StartsOnUpToToday_WithTheDatesItSends_AndRecognisesTheOtherShortcuts()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var today = Regex.Match(await Get(client, "/Collections/Campaigns"), "data-today=\"([\\d-]+)\"").Groups[1].Value;
        var initial = await Get(client, "/Collections/Campaigns");
        Assert.Contains("<option value=\"UpToToday\" selected=\"selected\">", initial);
        Assert.Matches($"id=\"campaignTo\"[^>]*value=\"{today}\"", initial);                                // the visible To date is what is sent; From stays empty (the whole history)
        Assert.DoesNotMatch("id=\"campaignFrom\"[^>]*value=\"2", initial);

        var thisMonth = await Get(client, "/Collections/Campaigns?businessDate=2026-10-14&dateFrom=2026-10-01&dateTo=2026-10-31");
        Assert.Contains("<option value=\"ThisMonth\" selected=\"selected\">", thisMonth);
        var last6 = await Get(client, "/Collections/Campaigns?businessDate=2026-10-14&dateFrom=2026-04-14&dateTo=2026-10-14");
        Assert.Contains("<option value=\"LastSixMonths\" selected=\"selected\">", last6);
        var custom = await Get(client, "/Collections/Campaigns?businessDate=2026-10-14&dateFrom=2026-02-03&dateTo=2026-05-09");
        Assert.Contains("<option value=\"Custom\" selected=\"selected\">", custom);
        Assert.Empty(api.Calls("/campaigns/preview"));
    }

    [Fact]
    public async Task PreviewCustomers_LoadsOnlyTheMatchingCustomers_WithCountsAmountAndCheckboxesForReadyOnes()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        // The button submits the form with render=full (the no-JavaScript path); the script fetches handler=Results with the same filters.
        var html = await Get(client, $"/Collections/Campaigns?{Applied}&render=full");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        foreach (var expected in new[] { "stage=CurrentMonthReminder", "towerId=7", "paymentStatus=Overdue", "dateFrom=2026-10-01", "dateTo=2026-10-31", "minTotal=250", "search=3001", "businessDate=2026-10-14" }) Assert.Contains(expected, call);
        Assert.Contains("data-eligible-count>1<", html); Assert.Contains("data-eligible-total>AED 650.00<", html);
        Assert.Single(Regex.Matches(html, "class=\"campaign-select\""));                                    // only the Ready customer can be ticked, not the Not eligible one
        Assert.Contains("data-campaign-status=\"NotEligible\"", html);
        Assert.Contains("data-preview-current", html);
        Assert.DoesNotContain(api.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));              // previewing never sends or approves anything
    }

    [Fact]
    public async Task ApproveAndSend_IsEnabledOnlyForACurrentReconciledPreviewWithReadyCustomers()
    {
        using var ready = CollectionsWebHost.Factory(new FakeCollectionsApi { IncludeNotEligible = true }); using var client = ready.CreateClient();
        var html = await Get(client, $"/Collections/Campaigns?{Applied}&render=full");
        var button = Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;
        Assert.DoesNotContain("disabled", button); Assert.Contains("data-blocked=\"false\"", button);        // the script adds "nothing ticked" on top of this
        Assert.Contains("data-selected-count", html); Assert.Contains("data-all-count", html);
        Assert.Contains("data-clear-selection", html);

        using var unreconciled = CollectionsWebHost.Factory(new FakeCollectionsApi { IncludeNotEligible = true, SourceUnreconciled = true }); using var c2 = unreconciled.CreateClient();
        html = await Get(c2, $"/Collections/Campaigns?{Applied}&render=full");
        button = Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;
        Assert.Contains("disabled", button); Assert.Contains("data-blocked=\"true\"", button);
        Assert.Contains("not reconciled", Regex.Match(html, "data-approve-reason[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);

        using var none = CollectionsWebHost.Factory(new FakeCollectionsApi()); using var c3 = none.CreateClient();   // the only customer needs review: nobody is Ready
        html = await Get(c3, $"/Collections/Campaigns?{Applied}&render=full");
        button = Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;
        Assert.Contains("disabled", button); Assert.DoesNotContain("campaign-select", html);
    }

    [Fact]
    public async Task SnapshotStatus_IsItsOwnRegion_NotPartOfTheCustomerResults_AndShowsRefreshAndReconciliationProblems()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true, SharjahFailed = true, SourceUnreconciled = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await Get(client, $"/Collections/Campaigns?{Applied}&render=full");
        var snapshot = Between(html, "data-snapshot-box", "campaign-search");
        Assert.Contains("data-load-problem", snapshot); Assert.Contains("Tiger Group Sharjah", snapshot);
        Assert.Contains("data-source-status=\"NotReconciled\"", snapshot); Assert.Contains("Not reconciled", snapshot);
        var results = html[html.IndexOf("data-results ", StringComparison.Ordinal)..];
        Assert.DoesNotContain("data-load-problem", results); Assert.DoesNotContain("data-source-status", results);
        Assert.Contains("data-snapshot-idle", await Get(client, "/Collections/Campaigns"));                    // before a preview the box says so instead of staying empty
    }

    [Fact]
    public async Task SnapshotStatus_ShowsOneStateChipPerArea_KeepsTheReconciliationBlockerOutsideTheDetails_AndOpensDetailsForAFailure()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true, SharjahFailed = true, SourceUnreconciled = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var snapshot = Between(await Get(client, $"/Collections/Campaigns?{Applied}&render=full"), "data-snapshot-box", "campaign-search");
        Assert.Matches("data-status-pill=\"reconciliation\" data-state=\"Failed\"", snapshot);
        Assert.Matches("data-status-pill=\"snapshot\" data-state=\"Failed\"", snapshot);
        var details = Regex.Match(snapshot, "<details[^>]*data-status-details[^>]*>.*?</details>", RegexOptions.Singleline).Value;
        Assert.Contains(" open", Regex.Match(details, "<details[^>]*>").Value);                  // a failed load shows its Retry without a click
        Assert.Contains("data-load-problem", details); Assert.Contains("data-load-button", details);
        Assert.DoesNotContain("data-source-status", details);                                       // the reconciliation blocker is never collapsed
        Assert.Contains("data-source-status=\"NotReconciled\"", snapshot);

        using var healthy = CollectionsWebHost.Factory(new FakeCollectionsApi()); using var c2 = healthy.CreateClient();
        var ok = Between(await Get(c2, $"/Collections/Campaigns?{Applied}&render=full"), "data-snapshot-box", "campaign-search");
        Assert.Matches("data-status-pill=\"reconciliation\" data-state=\"Ready\"", ok);
        Assert.DoesNotContain("data-status-details", ok);                                           // nothing to explain, so no details section
    }

    [Fact]
    public async Task AsynchronousResults_CarryTheSnapshotStatus_SoItCanBeLiftedIntoItsBox()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true, SharjahFailed = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var fragment = await Get(client, $"/Collections/Campaigns?handler=Results&{Applied}");
        Assert.Contains("data-snapshot-status", fragment); Assert.Contains("data-load-problem", fragment); Assert.Contains("data-preview-current", fragment);
        Assert.True(fragment.IndexOf("data-snapshot-status", StringComparison.Ordinal) < fragment.IndexOf("data-customer-results", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pagination_AndSelectAllMatching_KeepTheAppliedFilters()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await Get(client, $"/Collections/Campaigns?handler=Results&{Applied}");
        var form = Regex.Match(html, "<form[^>]*data-approve-form.*?</form>", RegexOptions.Singleline).Value;
        foreach (var expected in new[] { "name=\"stage\" value=\"CurrentMonthReminder\"", "name=\"towerId\" value=\"7\"", "name=\"paymentStatus\" value=\"Overdue\"", "name=\"minTotal\" value=\"250\"", "name=\"search\" value=\"3001\"" }) Assert.Contains(expected, form);
        Assert.Contains("value=\"AllMatching\" data-all-matching", form); Assert.Contains("data-all-count>1<", form);
        Assert.Contains("data-select-page", html);
    }

    [Fact]
    public void TheScripts_ClearTheResultsAndTicksWhenFiltersChange_AndNeverAutoLoad()
    {
        var root = AppContext.BaseDirectory; while (root is not null && !Directory.Exists(Path.Combine(root, "src", "TigerCS.Web"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var filters = File.ReadAllText(Path.Combine(root!, "src", "TigerCS.Web", "wwwroot", "js", "collections-campaign-filters.js"));
        var approve = File.ReadAllText(Path.Combine(root!, "src", "TigerCS.Web", "wwwroot", "js", "collections-campaign-approve.js"));
        Assert.Contains("campaign-preview-invalidated", filters); Assert.Contains("campaign-preview-invalidated", approve);   // the ticks go with the customers
        Assert.Contains("data-results-stale", filters); Assert.Contains("Click Preview Customers", filters);
        Assert.Contains("sessionStorage.removeItem", filters);
        Assert.Contains("data-blocked", approve); Assert.Contains("selected.size === 0", approve);
        Assert.DoesNotContain("fetch(", filters);                                                              // only the Preview Customers button (receivables.js) loads customers
    }
}
