extern alias TigerCsWeb;

using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>The redesigned Campaigns filters reach the API unchanged for the preview, every page and both exports.</summary>
public sealed class CollectionsCampaignFilterTests
{
    private const string Filters = "stage=LegalNotice&towerId=7&businessDate=2026-10-14&dateFrom=2026-09-01&dateTo=2026-10-31&minTotal=250&paymentStatus=Overdue&search=3001";

    [Fact]
    public async Task EveryFilter_ReachesThePreviewCall_TogetherWithThePaymentStatus()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync($"/Collections/Campaigns?handler=Results&{Filters}");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        foreach (var expected in new[] { "stage=LegalNotice", "towerId=7", "businessDate=2026-10-14", "dateFrom=2026-09-01", "dateTo=2026-10-31", "minTotal=250", "paymentStatus=Overdue", "search=3001" })
            Assert.Contains(expected, call);
    }

    [Fact]
    public async Task PaymentStatus_DefaultsToBoth_AndIsNeverDropped()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("Both", Assert.Single(api.Calls("/campaigns/preview")).Split('?')[1].Split('&').Single(p => p.StartsWith("paymentStatus=", StringComparison.Ordinal)));
        // Paging and exports keep it.
        Assert.Matches("paymentStatus=Both", html);
    }

    [Fact]
    public async Task ExportLinksAndExports_UseExactlyThePreviewFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&{Filters}")).Content.ReadAsStringAsync();
        var link = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]*handler=Export[^\"]*mode=review[^\"]*)\"").Groups[1].Value);
        Assert.Contains("paymentStatus=Overdue", link); Assert.Contains("minTotal=250", link); Assert.Contains("towerId=7", link);
        Assert.Contains("dateFrom=2026-09-01", link); Assert.Contains("dateTo=2026-10-31", link);

        await client.GetAsync(link);
        var export = Assert.Single(api.Calls("/campaigns/export"));
        var preview = Assert.Single(api.Calls("/campaigns/preview"));
        string[] Filter(string call) => call.Split('?')[1].Split('&').Where(p => !p.StartsWith("page=", StringComparison.Ordinal) && !p.StartsWith("mode=", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(Filter(preview), Filter(export));          // the same filters, parameter for parameter
    }

    [Fact]
    public async Task LastSixMonths_IsAPeriodChoice_ThatKeepsTheExplicitDatesAndTheOtherFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=LegalNotice&towerId=7&paymentStatus=Overdue&businessDate=2026-10-14&dateFrom=2026-04-14&dateTo=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("<option value=\"LastSixMonths\" selected=\"selected\">Last 6 months</option>", html);
        Assert.Contains("name=\"dateFrom\" type=\"date\"", html); Assert.Contains("value=\"2026-04-14\"", html); Assert.Contains("value=\"2026-10-14\"", html);
        Assert.Contains("<option value=\"Overdue\" selected=\"selected\">Overdue</option>", html);
        Assert.Contains("<input type=\"checkbox\" name=\"towerId\" value=\"7\" checked=\"checked\" />", html);          // the chosen tower stays ticked in the multi-select
    }

    [Fact]
    public async Task TheFilterForm_HasNoMonthOrYear_AndOneOfEachOtherControl()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder")).Content.ReadAsStringAsync();
        var form = Regex.Match(html, "<form id=\"campaignFilters\".*?</form>", RegexOptions.Singleline).Value;
        foreach (var name in new[] { "stage", "paymentStatus", "dateFrom", "dateTo", "minTotal", "businessDate" })
            Assert.Single(Regex.Matches(form, $"name=\"{name}\""));                  // no duplicate filters
        Assert.Equal(2, Regex.Matches(form, "name=\"towerId\"").Count);                  // Project / Tower is a multi-select: one checkbox per tower of the list (two in the fake), none ticked = All projects
        Assert.Single(Regex.Matches(html, "id=\"campaignSearch\""));                         // the search box sits by the results but still belongs to the same form
        Assert.Contains("form=\"campaignFilters\"", html);
        Assert.DoesNotContain("name=\"month\"", form); Assert.DoesNotContain("name=\"year\"", form);
        foreach (var option in new[] { "Due and Overdue (both)", ">Due<", ">Overdue<" }) Assert.Contains(option, form);
    }

    [Fact]
    public async Task OldMonthYearBookmarks_StillBecomeTheWholeMonthRange()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&month=9&year=2026");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        Assert.Contains("dateFrom=2026-09-01", call); Assert.Contains("dateTo=2026-09-30", call);
    }

    [Fact]
    public async Task ApproveAndSend_CarriesTheSameFiltersAsThePreview_AndThereIsNoReviewLink()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&{Filters}&sendingMode=Manual")).Content.ReadAsStringAsync();
        var form = Regex.Match(html, "<form[^>]*data-approve-form.*?</form>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(form);
        foreach (var expected in new[] { "name=\"stage\" value=\"LegalNotice\"", "name=\"towerId\" value=\"7\"", "name=\"dateFrom\" value=\"2026-09-01\"", "name=\"dateTo\" value=\"2026-10-31\"",
            "name=\"minTotal\" value=\"250\"", "name=\"paymentStatus\" value=\"Overdue\"", "name=\"search\" value=\"3001\"" })
            Assert.Contains(expected, form);
        Assert.DoesNotContain("data-review-link", html);
    }

    [Fact]
    public async Task TheResults_NameTheGenesysCampaignExactlyAsTheStage()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=LegalNotice&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("data-campaign-name>Legal notice - days 12 and 14<", html);
        var page = await (await client.GetAsync("/Collections/Campaigns?stage=LegalNotice")).Content.ReadAsStringAsync();
        Assert.Contains(">Legal notice - days 12 and 14</option>", page);                // the dropdown shows the same text
    }
}
