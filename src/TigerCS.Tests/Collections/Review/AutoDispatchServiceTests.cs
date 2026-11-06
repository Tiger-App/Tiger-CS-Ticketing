using Hangfire;
using Microsoft.EntityFrameworkCore;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.BackgroundJobs;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Auto sending mode: on the scheduled date of each approved campaign the dispatcher prepares and dispatches the eligible customers by itself, with no person confirming, but only while the separate
/// <c>AutoSendingEnabled</c> switch is on (off by default). Everything a person's send is subject to still applies. The harness clock starts on 14 Oct 2026, the Current Month day.
/// </summary>
public sealed class AutoDispatchServiceTests
{
    private const string CurrentMonthList = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1";

    private static async Task<ReviewHarness> SeededAsync(bool enabled = true, Action<ReviewHarness>? configure = null, bool refresh = true, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Campaign.AutoSendingEnabled = enabled;
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : [ReviewHarness.Row(1), ReviewHarness.Row(2)]);
        if (refresh) await h.RefreshAsync();
        return h;
    }

    private static AutoDispatchStageResult CurrentMonth(AutoDispatchResult result) => Assert.Single(result.Stages, s => s.Stage == "CurrentMonth");

    private static async Task<AutoDispatchResult> RunAsync(ReviewHarness h)
    {
        using var scope = h.NewScope();
        return await scope.Auto.RunAsync(CancellationToken.None);
    }

    private static async Task<List<CollectionsDispatch>> DispatchesAsync(ReviewHarness h)
    {
        using var context = h.CreateContext();
        return [.. context.CollectionsDispatches.Include(d => d.Items).AsEnumerable().OrderBy(d => d.CollectionsDispatchId)];
    }

    // ------------------------------------------------------------ the switch

    [Fact]
    public void AutoSendingEnabled_IsFalseByDefault_InCodeAndInEveryShippedSettingsFile()
    {
        Assert.False(new CollectionsCampaignOptions().AutoSendingEnabled);
        var root = AppContext.BaseDirectory; while (root is not null && !Directory.Exists(Path.Combine(root, "src", "TigerCS.Api"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        foreach (var file in Directory.GetFiles(Path.Combine(root!, "src", "TigerCS.Api"), "appsettings*.json"))
            Assert.DoesNotMatch("\"AutoSendingEnabled\"\\s*:\\s*true", File.ReadAllText(file));        // nothing ships with automatic dispatch switched on
    }

    [Fact]
    public async Task WhileTheSwitchIsOff_NothingIsPreparedOrSent_EvenOnAScheduledDayWithReadyCustomers()
    {
        using var h = await SeededAsync(enabled: false);                                              // 14 Oct: Current Month is scheduled and both customers are Ready
        var result = await RunAsync(h);
        Assert.False(result.Ran); Assert.Contains("AutoSendingEnabled is false", result.Detail);
        var queued = h.Scheduler.Refreshes.Count;                                                       // the seeding refresh the harness queued
        Assert.Empty(h.Scheduler.Dispatches); Assert.Equal(queued, h.Scheduler.Refreshes.Count); Assert.Empty(h.Client.Calls); Assert.Empty(await DispatchesAsync(h));
    }

    [Fact]
    public void TheRecurringJob_IsRegisteredOnlyWhileAutoSendingAndGenesysAreOn_AndRemovedOtherwise()
    {
        var manager = new RecordingManager();
        var services = new ServiceCollection().AddSingleton<IRecurringJobManager>(manager).BuildServiceProvider();
        var jobs = new BackgroundJobOptions { Enabled = true };
        var genesysOn = new GenesysOutboundOptions { Enabled = true };

        services.UseTigerCsRecurringAutoDispatch(jobs, new CollectionsCampaignOptions(), genesysOn, "Asia/Dubai");          // default: off
        Assert.Empty(manager.Added); Assert.Contains(CollectionsAutoDispatchJob.RecurringJobId, manager.Removed);
        services.UseTigerCsRecurringAutoDispatch(jobs, new CollectionsCampaignOptions { AutoSendingEnabled = true }, new GenesysOutboundOptions(), "Asia/Dubai");   // Genesys upload off
        Assert.Empty(manager.Added);
        services.UseTigerCsRecurringAutoDispatch(new BackgroundJobOptions { Enabled = false }, new CollectionsCampaignOptions { AutoSendingEnabled = true }, genesysOn, "Asia/Dubai");   // no Hangfire
        Assert.Empty(manager.Added);

        services.UseTigerCsRecurringAutoDispatch(jobs, new CollectionsCampaignOptions { AutoSendingEnabled = true }, genesysOn, "Asia/Dubai");
        var (_, cron, zone) = Assert.Single(manager.Added).Value;
        Assert.Equal("0 8-16 * * *", cron); Assert.Equal("Asia/Dubai", zone.Id);
    }

    private sealed class RecordingManager : IRecurringJobManager
    {
        public Dictionary<string, (Job Job, string Cron, TimeZoneInfo Zone)> Added { get; } = [];
        public List<string> Removed { get; } = [];
        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) => Added[recurringJobId] = (job, cronExpression, options.TimeZone);
        public void Trigger(string recurringJobId) { }
        public void RemoveIfExists(string recurringJobId) => Removed.Add(recurringJobId);
    }

    // ------------------------------------------------------------ the scheduled date

    [Fact]
    public async Task OnTheScheduledDate_ItDispatchesTheEligibleCampaign_WithTheStageAmounts_AndNoOneConfirming()
    {
        using var h = await SeededAsync();
        var result = await RunAsync(h);
        Assert.True(result.Ran);
        var stage = CurrentMonth(result); Assert.Equal(("CurrentMonth", "Dispatched", 2), (stage.Stage, stage.Outcome, stage.Count));
        Assert.Equal(["CurrentMonth", "LegalNotice"], result.Stages.Select(x => x.Stage).Order());     // both are scheduled on 14 Oct; Legal Notice has no eligible customer
        var dispatch = Assert.Single(await DispatchesAsync(h));
        Assert.Equal(DispatchService.AutoActorId, dispatch.InitiatedByEmployeeId);                    // audit: the system, not a person
        Assert.Equal("auto:2026-10-14:CurrentMonth", dispatch.IdempotencyKey); Assert.Contains("\"SendingMode\":\"Auto\"", dispatch.FilterJson);
        Assert.Equal(2, dispatch.ApprovedCount); Assert.True(dispatch.AcknowledgedActiveCampaignRisk);
        Assert.Empty(h.Client.Calls);                                                                  // the dispatch job (revalidation + upload) runs next

        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var call = Assert.Single(h.Client.Calls);
        Assert.Equal(CurrentMonthList, call.ContactListId); Assert.Equal(2, call.Contacts.Count);
        Assert.All(call.Contacts, c => { Assert.Equal("500.00", c.AmountDue); Assert.Equal("Current Month", c.ReminderType); Assert.True(c.Callable); });
        var shown = (await scope.Dispatch.ListAsync(h.Manager, CancellationToken.None)).Value!.Single();
        Assert.Equal("Automatic (scheduled)", shown.ApprovedByName); Assert.Equal("Completed", shown.Status);
    }

    [Fact]
    public async Task ItIsIdempotent_ARepeatedRunNeverDispatchesTheSameCampaignTwice()
    {
        using var h = await SeededAsync();
        await RunAsync(h);
        for (var i = 0; i < 3; i++)                                                                    // the job runs hourly
        {
            var again = await RunAsync(h);
            Assert.True(again.Ran); Assert.DoesNotContain(again.Stages, s => s.Outcome == "Dispatched");
        }
        Assert.Single(await DispatchesAsync(h)); Assert.Single(h.Scheduler.Dispatches);
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal("NothingReady", CurrentMonth(await RunAsync(h)).Outcome);               // the customers are now "already sent"
        Assert.Single(h.Client.Calls);
    }

    [Fact]
    public async Task OnADayWithoutAScheduledCampaign_NothingHappens()
    {
        using var h = await SeededAsync(configure: x => { x.Time.Advance(TimeSpan.FromDays(1)); x.Source.ReadAt = ReviewHarness.Now.AddDays(1); });   // 15 Oct
        var result = await RunAsync(h);
        Assert.False(result.Ran); Assert.Contains("No campaign is scheduled for today", result.Detail);
        Assert.Empty(h.Scheduler.Dispatches); Assert.Empty(await DispatchesAsync(h));
    }

    [Fact]
    public async Task ItPreparesTheReviewDataItself_WhenNoneWasBuiltToday()
    {
        using var h = await SeededAsync(refresh: false);                                               // no review run exists yet
        var result = await RunAsync(h);
        Assert.True(result.Ran); Assert.Equal("Dispatched", CurrentMonth(result).Outcome);
        Assert.Empty(h.Scheduler.Refreshes);                                                           // it ran the refresh itself; nothing was queued twice
        using var context = h.CreateContext();
        Assert.Equal(DispatchService.AutoActorId, context.CollectionsReviewRuns.Single().RequestedByEmployeeId);
    }

    [Fact]
    public async Task ARefreshThatFails_DispatchesNothing()
    {
        using var h = await SeededAsync(refresh: false);
        h.Source.Failure = new PactReceivablesSourceException("The PACT report connection is not configured.");
        var result = await RunAsync(h);
        Assert.False(result.Ran); Assert.Contains("could not be refreshed", result.Detail);
        Assert.Empty(h.Scheduler.Dispatches); Assert.Empty(await DispatchesAsync(h));
    }

    // ------------------------------------------------------------ the safety rules apply exactly as for a person

    [Fact]
    public async Task Reconciliation_Contacts_SharedPhones_AndLegalRestrictions_StillKeepCustomersOut()
    {
        using var unreconciled = await SeededAsync(configure: h => h.Campaign.FinancialSourceValidated = false);
        Assert.Equal("NothingReady", CurrentMonth(await RunAsync(unreconciled)).Outcome); Assert.Empty(unreconciled.Scheduler.Dispatches);

        using var contacts = await SeededAsync(rows: [ReviewHarness.Row(1, mobile: "123"), ReviewHarness.Row(2, mobile: "971500000009"), ReviewHarness.Row(3, mobile: "971500000009"), ReviewHarness.Row(4)]);
        var result = CurrentMonth(await RunAsync(contacts));
        Assert.Equal(("Dispatched", 1), (result.Outcome, result.Count));                              // only the customer with a valid, unshared number
        Assert.Equal("Customer 4", (await DispatchesAsync(contacts)).Single().Items.Single().CustomerName);

        // Legal notices are sent only once released, and an internal legal referral is never dispatched to a customer.
        var notice = ReviewHarness.Row(1, 2000m) with { DueDate = new DateTime(2026, 9, 10) };
        using var legal = await SeededAsync(configure: h => { h.Time.Advance(TimeSpan.FromDays(-2)); h.Campaign.LegalNoticeExportEnabled = false; }, rows: notice);   // 12 Oct: Legal Notice day
        var held = await RunAsync(legal);
        Assert.Equal("NotReleased", Assert.Single(held.Stages, s => s.Stage == "LegalNotice").Outcome);
        Assert.DoesNotContain(held.Stages, s => s.Stage == "LegalCase");
        Assert.Empty(legal.Scheduler.Dispatches);
    }

    [Fact]
    public async Task TheGenesysGates_StayInForce_WhileTheSwitchIsOn()
    {
        using var h = await SeededAsync(configure: x => x.Genesys.LiveCustomerDispatchEnabled = false);
        var refused = CurrentMonth(await RunAsync(h));
        Assert.Equal("Refused", refused.Outcome); Assert.Contains("switched off", refused.Detail);
        Assert.Empty(h.Scheduler.Dispatches);
        using var noSuppression = await SeededAsync(configure: x => x.Genesys.SuppressionEnabled = false);
        Assert.Equal("Refused", CurrentMonth(await RunAsync(noSuppression)).Outcome);
    }

    [Fact]
    public async Task AnotherActiveCampaignOnTheList_StopsTheDispatch_ButTheStagesOwnCampaignIsTheIntendedConsumer()
    {
        using var other = await SeededAsync(configure: h => h.Client.Campaigns.Add(new GenesysCampaignRef("c-other", "Some other campaign", CurrentMonthList, "on")));
        var refused = CurrentMonth(await RunAsync(other));
        Assert.Equal("Refused", refused.Outcome); Assert.Contains("Some other campaign", refused.Detail); Assert.Empty(other.Scheduler.Dispatches);

        using var own = await SeededAsync(configure: h => h.Client.Campaigns.Add(new GenesysCampaignRef("c-stage", "Current month reminder - day 14", CurrentMonthList, "on")));
        Assert.Equal("Dispatched", CurrentMonth(await RunAsync(own)).Outcome);
    }

    [Fact]
    public async Task ThePaidBalancesAreRevalidated_RightBeforeTheUpload()
    {
        using var h = await SeededAsync();
        await RunAsync(h);
        h.Source.Items.RemoveAll(r => r.TenantId == "T0002");                                          // customer 2 pays after the dispatch was prepared
        using var scope = h.NewScope();
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var contact = Assert.Single(Assert.Single(h.Client.Calls).Contacts);
        Assert.Equal("Customer 1", contact.CustomerName);
    }

    [Fact]
    public async Task AManualSendAndTheAutoDispatcher_NeverSendTheSameCustomerTwice()
    {
        using var h = await SeededAsync();
        // The Current Month campaign is scheduled today: a person sends it manually first ...
        var manual = new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m), ReviewQueryService.ModeAllMatching);
        var (_, confirm) = await h.PrepareAsync(manual, "manual-first");
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        // ... and Auto then finds nothing left to send for it (the same record is already in a live dispatch).
        Assert.Equal("NothingReady", CurrentMonth(await RunAsync(h)).Outcome);
        Assert.Single(h.Scheduler.Dispatches); Assert.Single(await DispatchesAsync(h));
    }

    [Fact]
    public async Task ACustomerContactedTodayUnderAnotherCampaign_IsHeldBackFromAuto()
    {
        // Customer 1 is in Current Month (scheduled today) AND has an August instalment (the Overdue campaign). A manual Overdue send would call twice on the scheduled day and is blocked, so the
        // frequency limit is shown through a record of another type being live: seed that dispatch directly, as a previous send of the day.
        var rows = new[] { ReviewHarness.Row(1, 500m, day: 20), ReviewHarness.Row(1, 300m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1B" }, ReviewHarness.Row(2) };
        using var h = await SeededAsync(rows: rows);
        using (var context = h.CreateContext())
        {
            var overdue = context.CollectionsReviewRecords.Single(r => r.ReminderType == CampaignReminderType.Overdue && r.TenantId == "T0001");
            context.CollectionsDispatches.Add(new CollectionsDispatch
            {
                IdempotencyKey = "earlier", Fingerprint = "x", Status = DispatchStatus.Completed, InitiatedByEmployeeId = Guid.NewGuid(), InitiatedAtUtc = h.Time.GetUtcNow().UtcDateTime, ReviewRunId = overdue.CollectionsReviewRunId,
                SelectionMode = "SelectedKeys", FilterJson = "{}", ApprovedCount = 1,
                Items = [new CollectionsDispatchItem { RecordKey = overdue.RecordKey, ReminderType = CampaignReminderType.Overdue, CompanyId = 4, TenantId = "T0001", UnitId = overdue.UnitId, UnitCode = overdue.UnitCode,
                    CustomerName = "Customer 1", Phone = overdue.Phone, Email = overdue.Email, Amount = 300m, Currency = "AED", DueDate = new DateOnly(2026, 8, 10), Status = DispatchItemStatus.UploadedToGenesys }]
            });
            await context.SaveChangesAsync();
        }
        var result = CurrentMonth(await RunAsync(h));
        Assert.Equal(("Dispatched", 1), (result.Outcome, result.Count));                              // only customer 2: customer 1 was already contacted today
    }
}
