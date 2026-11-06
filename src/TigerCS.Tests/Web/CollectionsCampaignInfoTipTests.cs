extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>
/// Collections Campaigns keeps its explanations behind compact Info icons instead of printing them under labels, figures and buttons. These tests guard that
/// every explanation is still in the page (and reachable by hover, keyboard, tap and assistive technology), while critical errors, blockers and the
/// remediation controls stay visible in the page itself.
/// </summary>
public sealed partial class CollectionsCampaignInfoTipTests
{
    private const string Applied = "stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&dateFrom=2026-10-01&dateTo=2026-10-31&minTotal=250&paymentStatus=Overdue&search=3001";

    private static async Task<string> Get(FakeCollectionsApi api, string url)
    {
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        return await (await client.GetAsync(url)).Content.ReadAsStringAsync();
    }

    private static Task<string> Results(FakeCollectionsApi api) => Get(api, $"/Collections/Campaigns?handler=Results&{Applied}");
    private static Task<string> FullPage(FakeCollectionsApi api) => Get(api, $"/Collections/Campaigns?{Applied}&render=full");
    /// <summary>Sending is a Manual action: the Send button and its reason live on the Manual list.</summary>
    private static Task<string> ManualPage(FakeCollectionsApi api) => Get(api, $"/Collections/Campaigns?{Applied}&render=full&sendingMode=Manual");

    [GeneratedRegex("<span class=\"info-tip\" data-info-tip>\\s*<button(?<button>[^>]*)>.*?</button>\\s*<span class=\"info-tip__content\"(?<content>[^>]*)>(?<text>.*?)</span>(?=\\s*</span>)", RegexOptions.Singleline)]
    private static partial Regex TipPattern { get; }

    private sealed record Tip(string Id, string Label, string DescribedBy, string ContentAttributes, string Text, string ButtonAttributes);

    private static List<Tip> Tips(string html) => [.. TipPattern.Matches(html).Select(m => new Tip(
        Regex.Match(m.Groups["content"].Value, "id=\"([^\"]+)\"").Groups[1].Value,
        WebUtility.HtmlDecode(Regex.Match(m.Groups["button"].Value, "aria-label=\"([^\"]*)\"").Groups[1].Value),
        Regex.Match(m.Groups["button"].Value, "aria-describedby=\"([^\"]*)\"").Groups[1].Value,
        m.Groups["content"].Value,
        WebUtility.HtmlDecode(Regex.Replace(m.Groups["text"].Value, "<[^>]+>", "")).Trim(),
        m.Groups["button"].Value))];

    private static Tip TipById(string html, string id) => Tips(html).Single(t => t.Id == id);

    /// <summary>The page with every Info tooltip removed: what a person reads without opening any of them.</summary>
    private static string WithoutTips(string html) => Regex.Replace(html, "<span class=\"info-tip__content\".*?</span>(?=\\s*</span>)", "", RegexOptions.Singleline);

    private static string Between(string html, string start, string end) { var a = html.IndexOf(start, StringComparison.Ordinal); return a < 0 ? "" : html[a..html.IndexOf(end, a, StringComparison.Ordinal)]; }

    // ------------------------------------------------------------------ the Info icon itself

    [Fact]
    public async Task EveryInfoIcon_IsAKeyboardReachableButton_WithAnAccessibleName_AndPointsAtItsTooltip()
    {
        var html = await FullPage(new FakeCollectionsApi { IncludeNotEligible = true, SharjahFailed = true, SourceUnreconciled = true, Stale = true, OutstandingAmount = 900m });
        var tips = Tips(html);
        Assert.True(tips.Count >= 10, $"expected the cards, status chips, details and actions to carry Info icons, found {tips.Count}");
        Assert.Equal(tips.Count, Regex.Matches(html, "data-info-tip-button").Count);                         // the pattern missed none
        Assert.Equal(tips.Count, tips.Select(t => t.Id).Distinct().Count());                                  // aria-describedby needs unique ids
        foreach (var tip in tips)
        {
            Assert.Contains("type=\"button\"", tip.ButtonAttributes);                                         // never submits the form it sits in
            Assert.Contains("aria-expanded=\"false\"", tip.ButtonAttributes);
            Assert.StartsWith("More information: ", tip.Label);
            Assert.True(tip.Label.Length > "More information: ".Length, $"{tip.Id} has no name");
            Assert.Equal(tip.Id, tip.DescribedBy);                                                            // the button is described by exactly its own tooltip
            Assert.Contains("role=\"tooltip\"", tip.ContentAttributes);
            Assert.False(string.IsNullOrWhiteSpace(tip.Text), $"{tip.Id} is empty");
            Assert.DoesNotContain("tabindex=\"-1\"", tip.ButtonAttributes);                                   // reachable with the keyboard
        }
    }

    [Fact]
    public async Task TheIcon_IsDecorativeSvg_AndTheTooltipTextIsInTheMarkup_NotInjectedByScriptOrATitleAttribute()
    {
        var html = await Results(new FakeCollectionsApi());
        Assert.Contains("<svg class=\"info-tip__icon\"", html); Assert.Contains("aria-hidden=\"true\"", Regex.Match(html, "<svg class=\"info-tip__icon\"[^>]*>").Value);
        Assert.DoesNotContain("data-tooltip=", html); Assert.DoesNotContain("data-bs-toggle", html);
        foreach (var tip in Tips(html)) Assert.DoesNotContain("title=", tip.ButtonAttributes);                // no second, native tooltip competing with ours
    }

    // ------------------------------------------------------------------ summary cards

    [Fact]
    public async Task EachMetricCard_ShowsLabel_InfoIcon_AndValue_AndNothingElse()
    {
        var html = await Results(new FakeCollectionsApi { OutstandingAmount = 900m });
        var cards = Regex.Matches(html, "<li class=\"campaign-card[^\"]*\">(.*?)</li>", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
        Assert.True(cards.Count >= 4);
        foreach (var card in cards)
        {
            Assert.Matches("^\\s*<span class=\"campaign-card__label\">[^<]+<span class=\"info-tip\"", card);   // label, then its Info icon on the same line
            Assert.Single(Regex.Matches(card, "data-info-tip-button"));
            Assert.Matches("</span>\\s*<strong class=\"campaign-card__value\"[^>]*>[^<]+</strong>\\s*(<span class=\"campaign-card__sub\">Outstanding <span data-eligible-outstanding>[^<]+</span></span>\\s*)?$", card); // then the value (the amount card adds its one-line outstanding figure), nothing else
            Assert.DoesNotMatch("<p[\\s>]", card); Assert.DoesNotContain("campaign-card__hint", card);
        }
        Assert.DoesNotContain("campaign-card__hint", html); Assert.DoesNotContain("campaign-helper", html);
    }

    [Fact]
    public async Task TheCardHints_AreUnchanged_ButNowLiveInTheirInfoTooltips()
    {
        var html = await Results(new FakeCollectionsApi());                                                    // 1 listed, eligible, not ready
        Assert.Equal("of 1 listed. The stage rule found an amount to collect.", TipById(html, "tip-eligible").Text);
        Assert.Equal("Eligible and passing every send check.", TipById(html, "tip-ready").Text);
        Assert.Equal("Eligible, but not yet safe to send.", TipById(html, "tip-blocked").Text);
        Assert.Contains("More information: Financially eligible customers", TipById(html, "tip-eligible").Label);
        // The counts and their hooks are untouched.
        Assert.Contains("data-eligible-count>1<", html); Assert.Contains("data-ready-count>0<", html); Assert.Contains("data-blocked-count>1<", html);
    }

    [Fact]
    public async Task TheQualifyingVsOutstandingExplanation_MovedIntoBothAmountTooltips_AndIsNoLongerAParagraph()
    {
        var html = await Results(new FakeCollectionsApi { OutstandingAmount = 900m });                         // 650.00 qualifies of 900.00 owed
        const string due = "The outstanding balance is everything these customers owe that is Due or Overdue. The qualifying amount is only the part this stage's rule collects";
        Assert.DoesNotContain("everything these customers owe", WithoutTips(html));                          // not printed under the cards any more
        Assert.DoesNotContain("Neither is the customers' full contract balance", WithoutTips(html));
        var qualifying = TipById(html, "tip-qualifying");                                                      // one Total amount card; the outstanding balance is its one-line sub figure
        Assert.Contains(due, qualifying.Text); Assert.Contains("Neither is the customers' full contract balance.", qualifying.Text);
        Assert.StartsWith("Stage amount of the eligible customers, as sent to Genesys.", qualifying.Text);
        Assert.Contains("(AED 250.00 of the balance is not part of this stage)", qualifying.Text);
        Assert.Contains("data-amount-explanation", qualifying.ContentAttributes);                              // the existing hook now marks the explanation itself
        Assert.Contains("data-eligible-total>AED", html); Assert.Contains("data-eligible-outstanding>AED", html);    // the figures are unchanged
    }

    // ------------------------------------------------------------------ campaign details

    [Fact]
    public async Task CampaignNotes_ArePartOfOneCompactLine_WithTheirHooks()
    {
        var html = await Results(new FakeCollectionsApi());
        var meta = Between(html, "<p class=\"campaign-meta\">", "</p>");
        Assert.Contains("data-campaign-name>Current month reminder - day 14<", meta);
        Assert.Contains("Payment <strong>", meta);
        Assert.Contains("data-mode-name", meta); Assert.Contains("data-preview-only-count", meta); Assert.Contains("data-auto-sending", meta);
        Assert.DoesNotContain("<dl class=\"campaign-details\">", html);                                           // no separate details block any more
    }

    [Fact]
    public async Task AnAvailableExport_HasNoInfoIcon()
    {
        var html = await Results(new FakeCollectionsApi { IncludeNotEligible = true });
        Assert.Contains("data-export-review", html); Assert.DoesNotContain("tip-export-review-reason", html);
    }

    [Fact]
    public async Task ABlockedApprove_ShowsTheExactReasonInAPopover_AndThePageNoLongerRepeatsItInline()
    {
        var html = await ManualPage(new FakeCollectionsApi { IncludeNotEligible = true, SourceUnreconciled = true });
        var button = Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;
        Assert.Contains("disabled", button); Assert.Contains("data-blocked=\"true\"", button); Assert.Contains("aria-describedby=\"tip-approve-reason\"", button);
        var tip = TipById(html, "tip-approve-reason");
        Assert.StartsWith("The financial source is not reconciled, so nothing can be approved or sent.", tip.Text);
        Assert.Contains("data-approve-reason", tip.ContentAttributes);                                         // collections-campaign-approve.js still finds and updates it
        Assert.DoesNotContain("data-approve-blocked-hint", html);
        Assert.DoesNotContain("<span class=\"receivables-meta\" data-approve-reason", html);
    }

    [Fact]
    public async Task ABlockedApprove_NamesMissingSendPermission_AndDoesNotBypassIt()
    {
        var html = await ManualPage(new FakeCollectionsApi { IncludeNotEligible = true, ExportForbidden = true });
        var button = Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;
        Assert.Contains("disabled", button); Assert.Contains("data-blocked=\"true\"", button);
        Assert.StartsWith("Your account does not have permission to approve and send campaigns", TipById(html, "tip-approve-reason").Text);
    }

    [Fact]
    public void TheScheduleReason_IsNamed_ForAPreviewThatIsNotASendDay()
    {
        var today = new DateOnly(2026, 10, 14);
        Assert.Null(TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel.ScheduleRestriction("Current month", [today], today, today, true));
        Assert.Contains("not today", TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel.ScheduleRestriction("Current month", [today], today.AddDays(1), today, true));
        Assert.Contains("not one of those days", TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel.ScheduleRestriction("Current month", [today.AddDays(3)], today, today, false));
    }

    [Fact]
    public async Task ABlockedApprove_NamesTheGenesysSettingsThatAreMissing_BeforeAnythingIsApproved()
    {
        var api = new FakeCollectionsApi { IncludeNotEligible = true };
        api.DispatchBlockers.Add("Customer dispatch to Genesys is switched off (Collections:GenesysOutbound:LiveCustomerDispatchEnabled).");
        var html = await ManualPage(api);
        var button = Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;
        Assert.Contains("disabled", button); Assert.Contains("data-blocked=\"true\"", button);
        var text = TipById(html, "tip-approve-reason").Text;
        Assert.StartsWith("Sending to Genesys is not configured in this environment.", text);
        Assert.Contains("LiveCustomerDispatchEnabled", text);
    }

    [Fact]
    public async Task AnEnabledApprove_StillExplainsItself_InThePopover()
    {
        var html = await ManualPage(new FakeCollectionsApi { IncludeNotEligible = true });
        Assert.Equal("Only Ready customers can be sent. Nothing is sent until you confirm the exact count, amount and Genesys contact list.", TipById(html, "tip-approve-reason").Text);
        Assert.DoesNotContain("Only Ready customers can be sent", WithoutTips(html));
        Assert.Contains("data-selected-units", html); Assert.Contains("data-clear-selection", html);          // the selection hooks stay
    }

    // ------------------------------------------------------------------ snapshot status

    [Fact]
    public async Task SnapshotStatus_KeepsLabelsAndPillsVisible_AndMovesSupplementaryTextIntoTooltips()
    {
        var html = await FullPage(new FakeCollectionsApi { IncludeNotEligible = true });
        var snapshot = Between(html, "data-snapshot-box", "campaign-search");
        foreach (var area in new[] { "reconciliation", "snapshot", "crm" }) Assert.Matches($"data-status-pill=\"{area}\" data-state=\"\\w+\">\\w+</span>", snapshot);
        foreach (var label in new[] { "Financial reconciliation", "Snapshot data", "CRM contacts" }) Assert.Matches($"campaign-status__label\">{label}<span class=\"info-tip\"", snapshot);
        Assert.Contains("Financial source: Reconciled", TipById(html, "tip-status-snapshot").Text);
        Assert.Matches(@"Updated \d{2} \w{3} \d{4} \d{2}:\d{2} \(Dubai\)", TipById(html, "tip-status-snapshot").Text);
        Assert.Matches("tip-status-snapshot\" role=\"tooltip\" data-info-tip-content>[^<]*<span data-source-state>", html);   // the existing hooks survive, inside the tooltip
        Assert.Contains("<span data-updated>Updated ", html);
        Assert.DoesNotContain("Financial source: Reconciled", WithoutTips(html));                              // no loose "Financial source / Updated" line under the chips
        Assert.Contains("never read as \"CRM has no mobile for these customers\"", TipById(html, "tip-status-crm").Text);
    }

    [Fact]
    public async Task CriticalSnapshotErrors_AndTheirRemediation_StayVisibleOutsideAnyTooltip()
    {
        var html = await FullPage(new FakeCollectionsApi { IncludeNotEligible = true, SharjahFailed = true, SourceUnreconciled = true });
        var visible = WithoutTips(Between(html, "data-snapshot-box", "campaign-search"));
        Assert.Matches("role=\"alert\" data-source-status=\"NotReconciled\"", visible);                        // the reconciliation blocker and ...
        Assert.Contains("nothing can be approved or sent", visible);
        Assert.Contains("data-reconciliation-link", visible);                                                  // ... its action
        Assert.Contains("data-load-problem", visible); Assert.Contains("Could not load data for <strong>Tiger Group Sharjah</strong>.", visible);
        Assert.Contains("data-load-button", visible); Assert.Contains(">Retry<", visible);                    // the load failure and its Retry
        Assert.Contains("data-status-details", visible);
    }

    [Fact]
    public async Task BeforeAPreview_TheSnapshotBoxShowsACompactChip_WithTheOldSentenceInItsTooltip()
    {
        var html = await Get(new FakeCollectionsApi(), "/Collections/Campaigns");
        var idle = Regex.Match(html, "<p class=\"campaign-snapshot__idle\" data-snapshot-idle>.*?</p>", RegexOptions.Singleline).Value;
        Assert.Contains("campaign-pill--idle\">Snapshot not loaded yet</span>", idle);
        Assert.Equal("Its status appears here once customers are previewed, and stays visible while you change filters.", TipById(html, "tip-snapshot-idle").Text);
        Assert.Contains("tip-snapshot-idle", idle);                                                            // the script removes the whole idle node when the real status arrives
        Assert.DoesNotContain("Its status appears here", WithoutTips(html));
    }

    // ------------------------------------------------------------------ why sending is blocked

    [Fact]
    public async Task WhySendingIsBlocked_ListsEveryFinancialAndScheduleBlockerInThePage_NeverOnlyInATooltip()
    {
        var html = await Results(new FakeCollectionsApi { SourceUnreconciled = true, Stale = true });
        var visible = WithoutTips(html);
        var blockers = Regex.Match(visible, "data-not-ready-reasons.*?</details>", RegexOptions.Singleline).Value;      // a small panel that expands on demand
        Assert.Contains("Why sending is blocked", blockers); Assert.Matches("<details class=\"campaign-blockers\"", visible);
        var reasons = Regex.Matches(blockers, "data-not-ready-reason>(.*?)</li>", RegexOptions.Singleline).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Contains(reasons, r => r.StartsWith("Financial data:", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("has not been reconciled against PACT", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("Financial source not reconciled (1 on this page)", StringComparison.Ordinal));
        Assert.Contains("No customer is", blockers);                                                           // the concise summary line
        Assert.DoesNotContain("data-not-ready-more", html);                                                    // a short list has nothing to fold
    }

    [Fact]
    public void TheBlockerGates_AreAlwaysFullyListed_AndOnlyTheLongTailOfCustomerReasonsMayFold()
    {
        // NotReadyReasons() (used by the page and by earlier tests) is the gates followed by the per-customer reasons, so folding the tail never drops a gate.
        var model = typeof(TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel);
        Assert.NotNull(model.GetMethod("NotReadyGateReasons")); Assert.NotNull(model.GetMethod("NotReadyCustomerReasons")); Assert.NotNull(model.GetMethod("NotReadyReasons"));
    }

    // ------------------------------------------------------------------ layout and behaviour contract (the shipped CSS and script)

    private static string Asset(string relative, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return File.ReadAllText(Path.Combine(srcDir, "TigerCS.Web", "wwwroot", relative));
    }

    [Fact]
    public async Task ThePage_LoadsTheInfoTipStylesAndScript_BeforeTheCampaignScripts()
    {
        var html = await Get(new FakeCollectionsApi(), "/Collections/Campaigns");
        Assert.Contains("/css/info-tip.css", html); Assert.Contains("/js/info-tip.js", html);
        Assert.True(html.IndexOf("/js/info-tip.js", StringComparison.Ordinal) < html.IndexOf("/js/collections-campaign-filters.js", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStylesheet_ShowsTheTooltipOnHover_KeyboardFocus_AndTap_WithoutClippingLongText()
    {
        var css = Asset("css/info-tip.css");
        Assert.Contains(".info-tip:hover > .info-tip__content", css); Assert.Contains("@media (hover: hover)", css);        // desktop hover (not sticky on touch)
        Assert.Contains(".info-tip__button:focus-visible ~ .info-tip__content", css);                                         // keyboard focus
        Assert.Contains(".info-tip.is-open > .info-tip__content", css);                                                       // tap / click / Enter
        Assert.Contains(".info-tip.is-dismissed", css);                                                                       // Escape
        Assert.Contains("position: fixed", css);                                                                              // cannot be clipped by a scrolling table or panel
        Assert.Matches(@"max-width:\s*min\(22rem,\s*calc\(100vw - 1rem\)\)", css);                                            // long text wraps and stays on a phone screen
        Assert.Contains("overflow-wrap: anywhere", css); Assert.Contains("max-height: calc(100vh - 1rem)", css); Assert.Contains("overflow-y: auto", css);
        Assert.Contains("width: 1.5rem", css); Assert.Contains("height: 1.5rem", css);                                         // one icon size, 24px touch target
        Assert.Contains("prefers-reduced-motion", css);
        Assert.DoesNotMatch(@"\.info-tip__content \{[^}]*display:\s*none", css);                                              // hidden by visibility, so aria-describedby still resolves
    }

    [Fact]
    public void TheScript_HandlesTapEscapeOutsideTapAndFocusLeaving_ThroughDelegation_WithoutInlineStyles()
    {
        var js = Asset("js/info-tip.js");
        foreach (var needed in new[] { "'click'", "'keydown'", "'Escape'", "aria-expanded", "'focusin'", "'focusout'", "'mouseover'", "'resize'", "'scroll'", "is-open", "is-dismissed" }) Assert.Contains(needed, js);
        Assert.Contains("document.addEventListener('click'", js);                                                             // delegated: works for the asynchronously reloaded results
        Assert.Contains("setProperty('left'", js); Assert.Contains("setProperty('top'", js);                                  // CSSOM: allowed by the strict CSP
        Assert.DoesNotContain("setAttribute('style'", js); Assert.DoesNotContain("innerHTML", js); Assert.DoesNotContain("cssText", js);
    }

    [Fact]
    public void TheCampaignStylesheet_KeepsCardsEqualHeight_AndStacksTheLayoutOnPhones()
    {
        var css = Asset("css/collections-campaigns.css");
        Assert.Contains("align-items: stretch", css);                                                                         // equal-height cards in a row
        Assert.Matches(@"\.campaign-card__value \{ margin-top: auto;", css);                                                   // figures line up at the bottom of the card
        Assert.Contains(".campaign-action {", css); Assert.Contains(".campaign-approve__actions", css);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", css);                                             // two cards per row on a phone
        Assert.DoesNotContain(".campaign-card__hint", css); Assert.DoesNotContain(".campaign-helper", css);
        Assert.DoesNotContain(".campaign-actions { display: flex; gap: .5rem", Asset("css/collections-snapshot.css"));         // the stale duplicate that overrode the layout is gone
        Assert.DoesNotContain(".campaign-approve {", Asset("css/collections-review.css"));                                    // the later sheet no longer overrides the approve layout
    }
}
