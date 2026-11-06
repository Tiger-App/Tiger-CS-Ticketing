extern alias TigerCsWeb;

using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>Review export is independent of Approve &amp; Send, and "eligible" / "ready" / "needs review" are three explicit, consistent counts.</summary>
public sealed class CollectionsCampaignReadinessTests
{
    private const string Applied = "stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&dateFrom=2026-10-01&dateTo=2026-10-31&minTotal=250&paymentStatus=Overdue&search=3001";

    private static async Task<string> Results(FakeCollectionsApi api, string query = Applied)
    {
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        return await (await client.GetAsync($"/Collections/Campaigns?handler=Results&{query}")).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task ReviewExport_IsOffered_EvenWhenNoCustomerIsReadyAndApproveIsDisabled()
    {
        var html = await Results(new FakeCollectionsApi());                                  // the only customer needs review: ReadyCount = 0
        Assert.Contains("data-export-review", html); Assert.Contains("Export review CSV", html);
        Assert.Matches("<a [^>]*href=\"[^\"]*handler=Export[^\"]*mode=review", html);
        Assert.DoesNotContain("data-approve-button", html);                                   // Auto sends nothing from this page
        Assert.DoesNotContain("Export Genesys CSV", html);                                   // the Genesys file keeps its stricter rule
    }

    [Fact]
    public async Task ReviewExport_ExportsExactlyTheAppliedPreviewFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&{Applied}")).Content.ReadAsStringAsync();
        var link = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]*handler=Export[^\"]*mode=review[^\"]*)\"").Groups[1].Value);
        var file = await client.GetAsync(link);
        Assert.Equal("text/csv", file.Content.Headers.ContentType?.MediaType);
        string[] Filter(string call) => call.Split('?')[1].Split('&').Where(p => !p.StartsWith("page=", StringComparison.Ordinal) && !p.StartsWith("mode=", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(Filter(Assert.Single(api.Calls("/campaigns/preview"))), Filter(Assert.Single(api.Calls("/campaigns/export"))));
        Assert.Contains("mode=review", Assert.Single(api.Calls("/campaigns/export")));      // never the Genesys file
    }

    [Theory]
    [InlineData(true, false, "not fully loaded|older than|Load the missing data|minutes old")]
    [InlineData(false, true, "does not have permission")]
    public async Task ReviewExport_StaysVisibleButDisabled_WithTheReason_WhenItsSafeguardsAreNotMet(bool stale, bool forbidden, string reasonPattern)
    {
        var html = await Results(new FakeCollectionsApi { Stale = stale, ExportForbidden = forbidden });
        Assert.DoesNotContain("handler=Export", html);                                       // no working link: the API would refuse the request anyway
        var button = Regex.Match(html, "<button[^>]*data-export-review-unavailable[^>]*>").Value;
        Assert.NotEmpty(button); Assert.Contains("disabled", button);
        Assert.Matches(reasonPattern, Regex.Match(html, "data-export-review-reason[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);
    }

    [Fact]
    public async Task ReviewExport_Reason_NamesTheCoverageGapOnce_AndTheStaleAgeWithAReloadHint()
    {
        var gap = Regex.Match(await Results(new FakeCollectionsApi { Covered = false }), "data-export-review-reason[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("not fully loaded", gap); Assert.Single(Regex.Matches(gap, "Load the missing data")); Assert.DoesNotContain("Reload the data", gap);
        var stale = Regex.Match(await Results(new FakeCollectionsApi { Stale = true }), "data-export-review-reason[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("300 minutes old (limit 90)", stale); Assert.EndsWith("Reload the data, then export.", stale);
    }

    [Fact]
    public async Task Counts_AreLabelledEligibleReadyAndNeedsReview_AndAddUp()
    {
        var needsReview = await Results(new FakeCollectionsApi());                          // 1 listed, eligible, not ready
        Assert.Contains("Financially eligible customers", needsReview); Assert.Contains("Ready to send", needsReview); Assert.Contains("Needs review / blocked", needsReview); Assert.Contains("Why sending is blocked", needsReview);
        Assert.Contains("data-eligible-count>1<", needsReview); Assert.Contains("data-ready-count>0<", needsReview); Assert.Contains("data-blocked-count>1<", needsReview);

        var mixed = await Results(new FakeCollectionsApi { IncludeNotEligible = true });    // 2 listed: 1 ready + 1 not eligible
        Assert.Contains("data-eligible-count>1<", mixed); Assert.Contains("data-ready-count>1<", mixed); Assert.Contains("data-blocked-count>0<", mixed);
        Assert.DoesNotContain("data-not-ready-reasons", mixed);                             // nothing to explain when every eligible customer is ready
    }

    [Fact]
    public async Task WhenEligibleCustomersAreNotReady_TheReasonsAreListed_AndApproveSaysSo()
    {
        var html = await Results(new FakeCollectionsApi { SourceUnreconciled = true, Stale = true });
        var reasons = Regex.Matches(html, "data-not-ready-reason>(.*?)</li>", RegexOptions.Singleline).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Contains(reasons, r => r.Contains("reconciled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reasons, r => r.StartsWith("Financial data:", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("Financial source not reconciled (1 on this page)", StringComparison.Ordinal));   // the per-customer reason, in words
        Assert.DoesNotContain(reasons, r => r.Contains("SourceReconciliationRequired", StringComparison.Ordinal));                // never a raw code

        var ready = await Results(new FakeCollectionsApi());                                // reconciled, fresh, but the customer needs review
        Assert.Contains("data-not-ready-reasons", ready);                                   // the per-customer reasons stay visible
        Assert.Contains("data-auto-note", ready); Assert.DoesNotContain("data-approve-reason", ready);
    }
}
