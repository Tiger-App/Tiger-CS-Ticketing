using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Services;

/// <summary>
/// Preview side of the two sending modes. The clock is 14 Oct 2026 (Dubai), the scheduled day of Current Month but NOT of Overdue (1st). Manual replaces only the schedule by
/// "the send date is today"; the financial gate, contact validation and permissions are tested to be unchanged.
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
    public async Task Manual_OnTheSendDateToday_MakesTheEligibleCustomerReady_WithoutTheSchedule()
    {
        var h = Overdue();
        var report = await PreviewAsync(h, "Manual");
        var item = Assert.Single(report.Items);
        Assert.Equal(("Ready", "Qualifies"), (item.Status, item.Reason));
        Assert.Equal("Manual", report.SendingMode); Assert.True(report.IsScheduledDate);
        Assert.Equal((1, 0), (report.ReadyCount, report.PreviewOnlyCount));
        Assert.Equal([new DateOnly(2026, 10, 1)], report.ScheduledDates);              // the policy schedule is still reported, only not enforced
    }

    [Fact]
    public async Task Manual_FutureSendDate_StaysPending_AndAPastDateIsOver()
    {
        var h = Overdue();
        var future = await PreviewAsync(h, "Manual", Today.AddDays(6));
        Assert.Equal(("PreviewOnly", CollectionsSendingModes.PendingSendDate), (future.Items[0].Status, future.Items[0].Reason));
        Assert.Equal((0, 1), (future.ReadyCount, future.PreviewOnlyCount)); Assert.False(future.IsScheduledDate);
        var past = await PreviewAsync(h, "Manual", Today.AddDays(-2));
        Assert.Equal(("PreviewOnly", CollectionsSendingModes.SendDateInPast), (past.Items[0].Status, past.Items[0].Reason));
        Assert.Equal(0, past.ReadyCount);
    }

    [Fact]
    public async Task Manual_DoesNotBypassFinancialReconciliation_OrContactValidation()
    {
        var unreconciled = await PreviewAsync(Overdue(validated: false), "Manual");
        Assert.Equal("NeedsReview", unreconciled.Items[0].Status); Assert.Contains("SourceReconciliationRequired", unreconciled.Items[0].Reason);
        Assert.False(unreconciled.FinancialSourceValidated); Assert.Equal(0, unreconciled.ReadyCount);

        var noContact = await PreviewAsync(Overdue(phone: false), "Manual");
        Assert.Equal("NeedsReview", noContact.Items[0].Status); Assert.Contains("NoValidContact", noContact.Items[0].Reason);
        Assert.Equal(0, noContact.ReadyCount);
    }

    [Fact]
    public async Task Manual_KeepsPermissions_AndRefusesAnUnknownMode_AndTheRawGenesysFile()
    {
        var h = Overdue();
        var agent = await PreviewAsync(h, "Manual", agent: true);                          // a reader sees the list but the send right is still false
        Assert.False(agent.CanExportReview);
        var bad = await h.Service.PreviewAsync(h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), Today, null, null, sendingMode: "Sometimes");
        Assert.Equal(CollectionsOutcome.InvalidRequest, bad.Outcome);
        // A manually dated list is sent only through Approve & Send (revalidation, suppression, duplicate protection), never as a raw Genesys file.
        var genesys = await h.Service.ExportAsync(h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), "genesys", Today, sendingMode: "Manual");
        Assert.Equal(CollectionsOutcome.InvalidRequest, genesys.Outcome); Assert.Contains("Approve & Send", genesys.Detail);
        var review = await h.Service.ExportAsync(h.Manager, nameof(CollectionsCampaignStage.OverdueReminder), "review", Today, sendingMode: "Manual");
        Assert.True(review.IsSuccess, review.Detail);                                     // the review file stays available
    }

    [Theory]
    [InlineData(null, true, "Auto")]
    [InlineData("manual", true, "Manual")]
    [InlineData(" AUTO ", true, "Auto")]
    [InlineData("auto-ish", false, "Auto")]
    public void TheModeParser_DefaultsToAuto_AndRefusesAnythingElse(string? input, bool ok, string expected)
    {
        Assert.Equal(ok, CollectionsSendingModes.TryParse(input, out var mode)); Assert.Equal(expected, mode);
        Assert.Null(CollectionsSendingModes.ManualTimingReason(Today, Today));
        Assert.Equal(CollectionsSendingModes.PendingSendDate, CollectionsSendingModes.ManualTimingReason(Today.AddDays(1), Today));
        Assert.Equal(CollectionsSendingModes.SendDateInPast, CollectionsSendingModes.ManualTimingReason(Today.AddDays(-1), Today));
    }
}
