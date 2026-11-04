extern alias TigerCsWeb;

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

public sealed class CollectionsCampaignRenderTests
{
    [Fact]
    public async Task InitialPage_HasAllFilters_AndDoesNotWaitForTheCampaignData()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&search=3001")).Content.ReadAsStringAsync();
        Assert.Empty(api.Calls("/campaigns/preview"));
        Assert.Contains("data-results-idle", html); Assert.Contains("Preview Customers", html);
        Assert.Contains("<input type=\"checkbox\" name=\"towerId\" value=\"7\" checked=\"checked\" /> <span>124 - Tower 124</span>", html);      // multi-select: ticked, shown with its name
        // Month and Year were redundant with the date range and are gone; Payment status and Project / Tower are the new controls.
        Assert.DoesNotContain("name=\"month\"", html); Assert.DoesNotContain("name=\"year\"", html);
        Assert.DoesNotContain("data-month-select", html); Assert.DoesNotContain("All months", html);
        Assert.Contains("name=\"paymentStatus\"", html); Assert.Contains("<option value=\"Both\" selected=\"selected\">Due and Overdue (both)</option>", html);
        Assert.Contains("Project / Tower", html); Assert.Contains("All projects", html);
        Assert.Contains("name=\"dateFrom\"", html); Assert.Contains("name=\"dateTo\"", html);
        Assert.Contains("<option value=\"LastSixMonths\"", html);
        Assert.Contains("Minimum Total (AED)", html); Assert.Contains("name=\"minTotal\"", html); Assert.Contains("value=\"100\"", html);
        Assert.DoesNotContain("name=\"companyId\"", html);
        Assert.Contains("/js/collections-campaign-filters.js", html);
        Assert.DoesNotContain("page-subtitle", html);                                // no explanatory text on the page
        Assert.DoesNotContain("fully paid instalments are never reminder candidates", html);
    }

    [Fact]
    public async Task Results_SendEveryFilter_UseTheUnpaidDateLabel_AndOfferExportsFromTheSameFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&dateFrom=2026-10-01&dateTo=2026-10-31&minTotal=250.5&search=3001")).Content.ReadAsStringAsync();
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        foreach (var expected in new[] { "stage=CurrentMonthReminder", "towerId=7", "businessDate=2026-10-14", "dateFrom=2026-10-01", "dateTo=2026-10-31", "minTotal=250.5", "search=3001" }) Assert.Contains(expected, call);
        Assert.DoesNotContain("companyId", call);
        // Exactly the requested columns, in order (after the leading selection checkbox column).
        var headers = Regex.Matches(Regex.Match(html, "<thead>.*?</thead>", RegexOptions.Singleline).Value, "<th(?:\\s[^>]*)?>(.*?)</th>").Select(m => m.Groups[1].Value).Skip(1).ToList();
        Assert.Equal(["Customer", "Mobile", "Email", "Project / Tower", "Unit", "Due", "Overdue", "Total"], headers);
        var cells = Regex.Matches(Regex.Match(html, "<tbody>.*?</tbody>", RegexOptions.Singleline).Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
            .Select(m => System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")).Trim()).Skip(1).ToList();
        Assert.Equal(["Campaign Customer", "+971500003001", "— No email: email not possible", "Al Ghaf", "TP140-101", "—", "650.00", "650.00"], cells);   // no email -> a dash and the short reason; zero Due -> a dash; Total = Due + Overdue
        Assert.Contains("Export review CSV", html);
        Assert.DoesNotContain("Export Genesys CSV", html);
        // Eligible customers and the total qualifying amount, and the exact Genesys campaign name the stage will use.
        Assert.Contains("data-eligible-count", html); Assert.Contains("data-eligible-total", html);
        Assert.Contains("data-campaign-name>Current month reminder - day 14<", html);
        foreach (var gone in new[] { "Earliest unpaid due date", "Qualifying balance", "Financial source reconciliation", "Data details", "Scheduled:", "Data refreshed", "campaign-summary", "Source:", "Preview only" })
            Assert.DoesNotContain(gone, html);
        // Export links carry exactly the previewed filters (incl. the minimum), so the file matches the list.
        Assert.Contains("handler=Export", html); Assert.Contains("minTotal=250.5", html); Assert.Contains("towerId=7", html); Assert.Contains("dateFrom=2026-10-01", html);
    }

    [Fact]
    public async Task ANotEligibleUnit_StaysListedWithItsBalances_AndOnlyItsReadinessSaysNo_WithNoViewDetails()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("Not Eligible Customer", html);
        Assert.Contains("TP140-102", html);
        Assert.Matches("data-campaign-status=\"NotEligible\"", html);
        Assert.Contains("Not eligible: no balance qualifies for this stage", html);
        Assert.Matches(@"data-unit-due>\s*300\.00\s*<", html);                       // the unit's real Due is shown, not hidden or zeroed
        Assert.Contains("Export Genesys CSV", html);                                    // every ELIGIBLE row is ready: the Genesys file (ready rows only) is offered
        foreach (var gone in new[] { "View Details", "data-unit-toggle", "data-unit-detail", "Original amount", "Days late", "receivables-table--instalments" })
            Assert.DoesNotContain(gone, html);                                          // Campaigns omit the expandable instalment details of Receivables
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "does not have permission")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "campaign source is unavailable")]
    public async Task Errors_AreExplicit(HttpStatusCode status, string text)
    {
        var api = new FakeCollectionsApi { Status = status };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder")).Content.ReadAsStringAsync();
        Assert.Contains(text, html);
        Assert.DoesNotContain("Export review CSV", html);
    }

    [Theory]
    [InlineData(2026, 2, "2026-02-01", "2026-02-28")]
    [InlineData(2026, 9, "2026-09-01", "2026-09-30")]
    public async Task MonthAndYear_SetTheCampaignRangeToTheWholeMonth(int year, int month, string from, string to)
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync($"/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&month={month}&year={year}");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        Assert.Contains($"dateFrom={from}", call); Assert.Contains($"dateTo={to}", call);
    }

    [Fact]
    public async Task MinimumTotal_DefaultsTo100_CanBeCleared_AndAFutureMonthMakesNoDataCall()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder");
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&minTotal=");
        var calls = api.Calls("/campaigns/preview").ToList();
        Assert.Contains("minTotal=100", calls[0]); Assert.DoesNotContain("minTotal", calls[1]);
        var shell = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&minTotal=")).Content.ReadAsStringAsync();
        Assert.Matches("<input[^>]*name=\"minTotal\"[^>]*value=\"\"", shell);
        api.Requests.Clear();
        var next = new DateOnly(DateTime.UtcNow.AddHours(4).Year + 1, 3, 1);
        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&month=3&year={next.Year}")).Content.ReadAsStringAsync();
        Assert.Empty(api.Calls("/campaigns/preview")); Assert.Contains("Nothing is due in this period yet.", html);
    }

    [Fact]
    public async Task DownloadIsUtf8Csv_WithNoStore_PassesTheSameFilters_AndInvalidDateNeverCallsApi()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var download = await client.GetAsync("/Collections/Campaigns?handler=Export&stage=CurrentMonthReminder&mode=review&businessDate=2026-10-14&towerId=7&minTotal=500&dateFrom=2026-10-01&dateTo=2026-10-31&search=3001");
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(download.Headers.CacheControl!.NoStore);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        var call = Assert.Single(api.Calls("/campaigns/export"));
        foreach (var expected in new[] { "towerId=7", "minTotal=500", "dateFrom=2026-10-01", "dateTo=2026-10-31", "search=3001", "mode=review" }) Assert.Contains(expected, call);
        var previews = api.Calls("/campaigns/preview").Count();
        var invalid = await client.GetAsync("/Collections/Campaigns?businessDate=invalid&render=full");
        Assert.Contains("Choose valid filters", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(previews, api.Calls("/campaigns/preview").Count());   // an invalid date never reaches the API
    }

    [Fact]
    public async Task StaleSnapshot_ShowsOneShortLine_AndWithholdsBothExportLinks()
    {
        var api = new FakeCollectionsApi { Stale = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        // Stale data is not a load failure: the page says the data is out of date (with its time) and offers Retry for that company only.
        Assert.Contains("Data for <strong>Tiger Group Dubai</strong> is out of date.", html);
        Assert.DoesNotContain("Could not load data for", html);
        Assert.Contains("data-state=\"Stale\"", html);
        Assert.Contains(">Retry<", html);
        Assert.DoesNotContain("handler=Export", html); Assert.DoesNotContain("Export Genesys CSV", html);   // an export of old data stays blocked
        Assert.Contains("data-export-review-unavailable", html);                                          // ... but the button is still there, disabled, with its reason
        foreach (var gone in new[] { "Export is disabled", "This data is stale", "data-snapshot-warning", "data-export-blocked", "Data details" }) Assert.DoesNotContain(gone, html);
    }

    [Fact]
    public async Task CoverageGap_ShowsOneShortLine_AndBlocksExport()
    {
        var api = new FakeCollectionsApi { Covered = false };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-03-15&dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("Could not load data for <strong>Tiger Group Dubai</strong>.", html); Assert.Contains("data-load-button", html);
        Assert.DoesNotContain("handler=Export", html); Assert.Contains("data-export-review-unavailable", html);
        Assert.DoesNotContain("data-coverage-gap", html); Assert.DoesNotContain("Load missing data", html);
    }

    [Fact]
    public async Task BeforeTheFirstSnapshot_ThePageStillRenders_WithTheShortLineAndRetry()
    {
        var api = new FakeCollectionsApi { NothingLoaded = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&dateFrom=2026-01-01&dateTo=2026-10-31")).Content.ReadAsStringAsync();
        Assert.Contains("Could not load data for <strong>Tiger Group Dubai</strong>.", html); Assert.Contains(">Retry<", html);
        Assert.DoesNotContain("Export review CSV", html);
        Assert.DoesNotContain("No receivables data has been loaded yet.", html);
    }

    [Fact]
    public async Task WhenSharjahFails_TheLineNamesOnlySharjah_RetryReloadsIt_AndTheDubaiRowsStillShow()
    {
        var api = new FakeCollectionsApi { SharjahFailed = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        var line = Regex.Match(html, "load-problem.*?</form>", RegexOptions.Singleline).Value;
        Assert.Contains("Could not load data for <strong>Tiger Group Sharjah</strong>.", line);
        Assert.DoesNotContain("Dubai", line);
        Assert.Contains("name=\"companyId\" value=\"32\"", line); Assert.Contains(">Retry<", line);
        Assert.Single(Regex.Matches(html, "data-load-problem"));
        Assert.Contains("Campaign Customer", html);
        Assert.DoesNotContain("handler=Export", html);                             // incomplete data is never exported (the disabled button says why)
        foreach (var gone in new[] { "Export is disabled", "Data details", "error 7399", "consecutive" }) Assert.DoesNotContain(gone, html);
    }
}
