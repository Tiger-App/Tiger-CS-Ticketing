using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Services;

/// <summary>
/// The schedule preview behind Auto (the second of the two sending modes). The clock is 14 Oct 2026 (Dubai), the scheduled day of Current Month but NOT of Overdue (1st). A Manual list does not come
/// from this preview (see ManualSendingModeTests); the financial gate, contact validation and permissions are unchanged.
/// </summary>
public sealed class CampaignSendingModeTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static CollectionsCampaignAppServiceTests.Harness Overdue(bool validated = true, bool phone = true)
    {
        var h = new CollectionsCampaignAppServiceTests.Harness(); h.Campaign.FinancialSourceValidated = validated;
        // An instalment due in August is Overdue on 14 Oct.
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row() with { DueDate = new(2026, 8, 10), Mobile = phone ? "971500003001" : "", Email = phone ? "example@example.test" : "" });
        return h;
    }

    private static async Task<TigerCS.Application.Modules.Collections.Dto.CollectionsCampaignPreviewDto> PreviewAsync(
        CollectionsCampaignAppServiceTests.Harness h, string? mode, DateOnly? date = null, bool agent = false) =>
        (await h.Service.PreviewAsync(agent ? h.Agent : h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), date ?? Today, null, null, sendingMode: mode)).Value!;

    [Fact]
    public async Task Auto_IsTheDefault_AndKeepsTheScheduleRestriction()
    {
        var h = Overdue();
        foreach (var mode in new string?[] { null, "", "Auto" })
        {
            var report = await PreviewAsync(h, mode);
            var item = Assert.Single(report.Items);
            Assert.Equal(("PreviewOnly", "OutsideSchedule"), (item.Status, item.Reason));
            Assert.Equal("Auto", report.SendingMode); Assert.False(report.IsScheduledDate);
            Assert.Equal((0, 1), (report.ReadyCount, report.PreviewOnlyCount));
            Assert.False(report.AutoSendingEnabled);                                  // automatic execution is its own switch and is off by default
        }
    }

    [Fact]
    public async Task Manual_DoesNotUseTheSchedulePreview_ItsListComesFromTheStoredReviewData()
    {
        var h = Overdue();
        // The schedule preview is the Auto view. A Manual list (any campaign type at any time, amount from the filters) is built from the stored review records.
        var manual = await h.Service.PreviewAsync(h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), Today, null, null, sendingMode: "Manual");
        Assert.Equal(CollectionsOutcome.InvalidRequest, manual.Outcome); Assert.Contains("stored review data", manual.Detail);
        var export = await h.Service.ExportAsync(h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), "genesys", Today, sendingMode: "Manual");
        Assert.Equal(CollectionsOutcome.InvalidRequest, export.Outcome);                  // never a raw Genesys file outside the approval
    }

    [Fact]
    public async Task Auto_DoesNotBypassFinancialReconciliation_OrContactValidation_AndKeepsPermissions()
    {
        var unreconciled = await PreviewAsync(Overdue(validated: false), "Auto");
        Assert.Equal("NeedsReview", unreconciled.Items[0].Status); Assert.Contains("SourceReconciliationRequired", unreconciled.Items[0].Reason);
        Assert.False(unreconciled.FinancialSourceValidated); Assert.Equal(0, unreconciled.ReadyCount);

        var noContact = await PreviewAsync(Overdue(phone: false), "Auto");
        Assert.Equal("NeedsReview", noContact.Items[0].Status); Assert.Contains("NoValidContact", noContact.Items[0].Reason);
        Assert.Equal(0, noContact.ReadyCount);

        var h = Overdue();
        var agent = await PreviewAsync(h, "Auto", agent: true);                           // a reader sees the list but the send right is still false
        Assert.False(agent.CanExportReview);
        var bad = await h.Service.PreviewAsync(h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), Today, null, null, sendingMode: "Sometimes");
        Assert.Equal(CollectionsOutcome.InvalidRequest, bad.Outcome);
    }

    [Fact]
    public async Task Auto_OnItsScheduledDay_MakesTheEligibleCustomerReady()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row() with { DueDate = new(2026, 8, 10), Mobile = "971500003001", Email = "example@example.test" });
        var report = (await h.Service.PreviewAsync(h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), new DateOnly(2026, 10, 1), null, null, sendingMode: "Auto")).Value!;
        Assert.Equal("Auto", report.SendingMode);
        // 1 Oct is the Overdue day, but the clock says 14 Oct: a list is only ever dispatched for TODAY, so this is a preview.
        Assert.Equal("PreviewOnly", report.Items[0].Status);
    }

    [Theory]
    [InlineData(null, true, "Auto")]
    [InlineData("manual", true, "Manual")]
    [InlineData(" AUTO ", true, "Auto")]
    [InlineData("auto-ish", false, "Auto")]
    [InlineData("DirectSend", false, "Auto")]
    public void TheModeParser_DefaultsToAuto_AndRefusesAnythingElse(string? input, bool ok, string expected)
    {
        Assert.Equal(ok, CollectionsSendingModes.TryParse(input, out var mode)); Assert.Equal(expected, mode);
    }
}
