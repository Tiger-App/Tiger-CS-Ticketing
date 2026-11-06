extern alias TigerCsWeb;

using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>The Sending Mode selector: exactly Manual and Auto. Auto is the approved policy schedule (a read-only preview here: the automatic dispatcher sends on the date, while AutoSendingEnabled is on); Manual is covered by CollectionsCampaignManualRenderTests. Ready, Blocked and Preview only are always shown apart.</summary>
public sealed class CollectionsCampaignSendingModeRenderTests
{
    private const string Url = "/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode=";

    private static async Task<(string Html, FakeCollectionsApi Api)> LoadAsync(string mode, Action<FakeCollectionsApi>? configure = null)
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true };
        configure?.Invoke(api);
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        return (await (await client.GetAsync(Url + mode)).Content.ReadAsStringAsync(), api);
    }

    private static string Between(string html, string start, string end) { var a = html.IndexOf(start, StringComparison.Ordinal); return a < 0 ? "" : html[a..html.IndexOf(end, a, StringComparison.Ordinal)]; }
    private static string Reason(string html) => System.Net.WebUtility.HtmlDecode(Regex.Match(html, "data-approve-reason[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);
    private static string TipById(string html, string id) => System.Net.WebUtility.HtmlDecode(Regex.Match(html, $"id=\"{id}\"[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);
    private static string Button(string html) => Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;

    [Fact]
    public async Task TheSelector_IsACompactManualAutoToggle_AndAutoIsTheDefault()
    {
        var (html, _) = await LoadAsync("");
        var toggle = Regex.Match(html, "<div class=\"segmented\"[^>]*data-sending-mode>.*?</div>", RegexOptions.Singleline).Value;
        Assert.Contains("role=\"radiogroup\"", toggle);
        Assert.Matches("name=\"sendingMode\" value=\"Manual\"(?![^>]*checked)", toggle);              // Manual is not the default
        Assert.Matches("name=\"sendingMode\" value=\"Auto\"[^>]*checked", toggle);
        Assert.Equal(2, Regex.Matches(toggle, "name=\"sendingMode\"").Count);                           // exactly two modes
        Assert.DoesNotContain("<select", Regex.Match(html, "id=\"campaignModeLabel\".*?</div>\\s*</div>", RegexOptions.Singleline).Value);   // no dropdown
        Assert.Contains(">Sending mode", html);
    }

    [Fact]
    public async Task Auto_KeepsThePreviewDateInAdvancedFilters_AndShowsThePolicySchedule()
    {
        var (html, api) = await LoadAsync("Auto");
        var main = Between(html, "campaign-filters__main", "campaign-filters__footer");
        var advanced = Regex.Match(html, "<details[^>]*data-advanced-filters[^>]*>.*?</details>", RegexOptions.Singleline).Value;
        Assert.Contains("Preview date", advanced); Assert.DoesNotContain("name=\"businessDate\"", Regex.Match(main, "campaign-date-slot.*?</div>", RegexOptions.Singleline).Value);
        Assert.Single(Regex.Matches(html, "name=\"businessDate\" type=\"date\""));                    // exactly one date input
        var label = Regex.Match(html, "<span[^>]*data-policy-schedule[^>]*>(.*?)</span>", RegexOptions.Singleline);
        Assert.Equal("Schedule: 14 Oct 2026", label.Groups[1].Value); Assert.DoesNotContain("hidden", label.Value);          // a small label, not a paragraph
        var tip = TipById(html, "tip-sending-mode");
        Assert.Contains("on a scheduled date the automatic dispatcher prepares and sends the eligible campaign by itself", tip);
        Assert.Contains("a separate setting, off by default", tip);
        Assert.Contains("data-policy-schedule-result>14 Oct 2026<", html);
        Assert.Contains("data-auto-sending>Off<", html);                                               // automatic execution is its own switch, off by default
        Assert.Contains("sendingMode=Auto", Assert.Single(api.Calls("/campaigns/preview")));
        Assert.Contains("data-preview-only-count>0<", html);
    }

    [Fact]
    public async Task Auto_SendsNothingFromThePage_ItOnlyExplainsWhatTheDispatcherDoes()
    {
        var (html, _) = await LoadAsync("Auto");
        Assert.DoesNotContain("data-approve-form", html); Assert.DoesNotContain("data-approve-button", html); Assert.DoesNotContain("Approve &amp; Send", html);
        Assert.DoesNotContain("class=\"campaign-select\"", html); Assert.DoesNotContain("data-select-page", html);
        Assert.Contains("Auto does not send from this page", html); Assert.Contains("choose <strong>Manual</strong>", html);
        Assert.Contains("data-ready-count>1<", html);                                                  // the Ready figure is still the approved policy's
    }

    [Fact]
    public async Task Auto_OutsideTheSchedule_IsPreviewOnly_AndExplainsTheScheduledDays()
    {
        var (html, _) = await LoadAsync("Auto", a => a.PreviewOnlyCustomer = true);
        var reasons = Regex.Matches(html, "data-not-ready-reason>(.*?)</li>", RegexOptions.Singleline).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Contains(reasons, r => r.StartsWith("Preview only:") && r.Contains("is scheduled for 01 Nov 2026") && r.Contains("it cannot be sent outside the schedule"));
        Assert.Contains("data-preview-only-count>1<", html); Assert.Contains("data-ready-count>0<", html);
        Assert.Contains("Preview only: Not a scheduled send day for this stage", html);
    }

    [Fact]
    public async Task ReadyBlockedAndPreviewOnly_AreThreeSeparateFigures()
    {
        var (html, _) = await LoadAsync("Auto");
        foreach (var hook in new[] { "data-ready-count", "data-blocked-count", "data-preview-only-count" }) Assert.Contains(hook, html);
        foreach (var label in new[] { ">Customers<", ">Ready<", ">Blocked<", ">Total amount<" }) Assert.Contains(label, html);      // four compact KPI cards
        Assert.Contains("Preview only <strong data-preview-only-count>", html);                                                 // preview-only sits in the one-line notes
        Assert.Contains("data-row-state>Ready<", html);
    }
}
