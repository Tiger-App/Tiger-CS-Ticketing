extern alias TigerCsWeb;

using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>The Sending Mode selector: Manual (Send Date picker, pending future dates) and Auto (the calculated policy schedule). Ready, Blocked and Preview only are always shown apart.</summary>
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
        var toggle = Regex.Match(html, "<div class=\"segmented\"[^>]*data-sending-mode[^>]*>.*?</div>", RegexOptions.Singleline).Value;
        Assert.Contains("role=\"radiogroup\"", toggle);
        Assert.Matches("name=\"sendingMode\" value=\"Manual\"(?![^>]*checked)", toggle);              // Manual is not the default
        Assert.Matches("name=\"sendingMode\" value=\"Auto\"[^>]*checked", toggle);
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
        Assert.Contains("automatic execution is a separate setting, off by default", TipById(html, "tip-sending-mode"));
        Assert.Contains("data-policy-schedule-result>14 Oct 2026<", html); Assert.DoesNotContain("data-send-date>", html);
        Assert.Contains("data-auto-sending>Off<", html);                                               // automatic execution is its own switch, off by default
        Assert.Contains("sendingMode=Auto", Assert.Single(api.Calls("/campaigns/preview")));
        Assert.Contains("data-preview-only-count>0<", html);
    }

    [Fact]
    public async Task Manual_ShowsTheSendDatePicker_AndKeepsApproveAndSend()
    {
        var (html, api) = await LoadAsync("Manual");
        var main = Between(html, "campaign-filters__main", "campaign-filters__footer");
        Assert.Contains("name=\"businessDate\"", main); Assert.Contains(">Send date<", main); Assert.DoesNotContain("Preview date", html);
        var advanced = Regex.Match(html, "<details[^>]*data-advanced-filters[^>]*>.*?</details>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("businessDate", advanced);
        Assert.Single(Regex.Matches(html, "name=\"businessDate\" type=\"date\""));
        Assert.Contains("value=\"2026-10-14\"", Regex.Match(main, "<input[^>]*name=\"businessDate\"[^>]*>").Value);
        Assert.Contains("Manual: you choose the send date", TipById(html, "tip-sending-mode"));                              // explained in a tooltip, not a paragraph
        Assert.DoesNotContain("data-mode-hint=\"Manual\"", html);
        Assert.Contains("data-mode-name>Manual<", html); Assert.Contains("data-send-date>14 Oct 2026<", html); Assert.Contains("today: can be sent after approval", html);
        Assert.Contains("sendingMode=Manual", Assert.Single(api.Calls("/campaigns/preview")));
        Assert.Contains("Approve &amp; Send to Genesys", html); Assert.DoesNotContain("disabled", Button(html));
        Assert.Matches("<input type=\"hidden\" name=\"sendingMode\" value=\"Manual\"", html);        // the approval re-resolves the same mode
        Assert.Contains("data-ready-count>1<", html);
    }

    [Fact]
    public async Task Manual_FutureSendDate_IsPending_NothingCanBeApproved_AndTheReasonSaysSo()
    {
        var (html, _) = await LoadAsync("Manual", a => a.PreviewOnlyCustomer = true);
        Assert.Contains("disabled", Button(html));
        Assert.StartsWith("Pending: the send date (19 Oct 2026) is in the future.", Reason(html));
        Assert.Contains("nothing is sent automatically", Reason(html));
        Assert.Contains("data-ready-count>0<", html); Assert.Contains("data-preview-only-count>1<", html); Assert.Contains("data-blocked-count>0<", html);
        Assert.Contains("pending: future date", html);
        Assert.Contains("Preview only: Send date is in the future", html);                              // the row says it too
        Assert.DoesNotContain("campaign-select", html);                                                // no checkbox: it cannot be ticked
        Assert.Contains("Why sending is blocked", html);
    }

    [Fact]
    public async Task Auto_OutsideTheSchedule_IsPreviewOnly_AndExplainsTheScheduledDays()
    {
        var (html, _) = await LoadAsync("Auto", a => a.PreviewOnlyCustomer = true);
        Assert.Contains("disabled", Button(html));
        Assert.StartsWith("Preview only:", Reason(html)); Assert.Contains("is scheduled for 01 Nov 2026", Reason(html));
        Assert.Contains("it cannot be sent outside the schedule", Reason(html));
        Assert.Contains("data-preview-only-count>1<", html); Assert.Contains("data-ready-count>0<", html);
        Assert.Contains("Preview only: Not a scheduled send day for this stage", html);
    }

    [Fact]
    public async Task ReadyBlockedAndPreviewOnly_AreThreeSeparateFigures()
    {
        var (html, _) = await LoadAsync("Manual");
        foreach (var hook in new[] { "data-ready-count", "data-blocked-count", "data-preview-only-count" }) Assert.Contains(hook, html);
        foreach (var label in new[] { ">Customers<", ">Ready<", ">Blocked<", ">Total amount<" }) Assert.Contains(label, html);      // four compact KPI cards
        Assert.Contains("Preview only <strong data-preview-only-count>", html);                                                 // preview-only sits in the one-line notes
        Assert.Contains("data-row-state>Ready<", html);
    }

    // ------------------------------------------------------------ selection checkboxes

    private static string Header(string html) => Regex.Match(html, "<input[^>]*data-select-page[^>]*>").Value;

    [Fact]
    public async Task OnlyReadyCustomers_GetAnEnabledCheckbox_AndSelectAllIsEnabled()
    {
        var (html, _) = await LoadAsync("Manual");
        Assert.Single(Regex.Matches(html, "class=\"campaign-select\""));
        Assert.DoesNotContain("disabled", Header(html)); Assert.Contains("Select all Ready customers on this page", Header(html));
        Assert.Contains("data-selected-summary", html); Assert.Contains(">0 selected<", html);                      // the count sits next to Approve & Send
        Assert.Contains("data-select-page", html);
    }

    [Fact]
    public async Task PreviewOnlyAndBlockedCustomers_GetADisabledCheckbox_WithTheReason()
    {
        var (html, _) = await LoadAsync("Manual", a => a.PreviewOnlyCustomer = true);               // the only listed customer is Preview only
        Assert.DoesNotContain("class=\"campaign-select\"", html);
        var rowBox = Regex.Match(html, "<input[^>]*row-select-disabled[^>]*>").Value;
        Assert.Contains("disabled", rowBox); Assert.Contains("cannot be selected", rowBox); Assert.Contains("Preview only", rowBox);
        Assert.Contains("disabled", Header(html));                                                  // nothing Ready: Select all is disabled and says why
        Assert.Contains("Nothing to select: no customer is Ready to send", System.Net.WebUtility.HtmlDecode(Header(html)));
        Assert.Contains(">No Ready customers<", html);

        var api = new FakeCollectionsApi();                                                         // one customer that needs review (blocked)
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var blocked = await (await client.GetAsync("/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("class=\"campaign-select\"", blocked);
        Assert.Contains("Blocked: cannot be selected", System.Net.WebUtility.HtmlDecode(Regex.Match(blocked, "<input[^>]*row-select-disabled[^>]*>").Value));
        Assert.Contains("disabled", Header(blocked));
    }
}

