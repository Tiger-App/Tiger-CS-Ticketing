using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <param name="Stage">The campaign type.</param>
/// <param name="Outcome"><c>Dispatched</c>, <c>AlreadyDispatched</c>, <c>NothingReady</c>, <c>NotReleased</c>, <c>Refused</c>.</param>
/// <param name="Detail">Why, when the campaign was not dispatched.</param>
/// <param name="Count">How many customers the dispatch carries.</param>
public sealed record AutoDispatchStageResult(string Stage, string Outcome, string? Detail, int Count);

public sealed record AutoDispatchResult(bool Ran, string? Detail, IReadOnlyList<AutoDispatchStageResult> Stages);

/// <summary>
/// Auto sending mode: on the scheduled date of each approved campaign policy it prepares and dispatches the eligible customers by itself, with no person confirming. It exists only to
/// follow the approved schedule and is inert unless <c>Collections:Campaigns:AutoSendingEnabled</c> is true (false by default).
/// <list type="bullet">
/// <item>It never builds its own list: it asks the review data for the Ready records of a campaign scheduled TODAY (the stage amount, exactly as the approved policy defines it) and creates the dispatch
/// through the same pipeline as a person's confirmation: reconciliation, contact validation, shared-phone, legal-release, duplicate-send and contact-frequency rules, the active-campaign and contact-list
/// checks, a frozen audit record, revalidation of the current balances right before the upload, and the paid-after-upload suppression.</item>
/// <item>One dispatch per campaign per business day: the idempotency key is the campaign and the date, so repeating the job (it runs hourly) can never send twice.</item>
/// <item>It never sends an internal Legal referral, and a legal notice only when legal notices have been released.</item>
/// <item>The initiator on the audit record is the system actor <see cref="DispatchService.AutoActorId"/>, shown as "Automatic (scheduled)".</item>
/// </list>
/// </summary>
public sealed class AutoDispatchService(
    CollectionsOptions options, CollectionsCampaignOptions campaignOptions, CollectionsClock clock, IReviewStore store,
    ReviewQueryService query, ReviewRefreshService refresh, DispatchService dispatch, ILogger<AutoDispatchService> logger)
{
    private static readonly SemaphoreSlim OneRun = new(1, 1);

    /// <summary>The campaigns Auto may dispatch. An internal Legal referral is never dispatched to a customer.</summary>
    private static readonly CollectionsCampaignStage[] Stages =
        [CollectionsCampaignStage.OverdueReminder, CollectionsCampaignStage.CurrentMonthReminder, CollectionsCampaignStage.FollowUpReminder, CollectionsCampaignStage.LegalNotice];

    public async Task<AutoDispatchResult> RunAsync(CancellationToken ct)
    {
        if (!campaignOptions.AutoSendingEnabled)
            return Skipped("Automatic dispatch is switched off (Collections:Campaigns:AutoSendingEnabled is false); nothing was prepared or sent.");
        if (!options.Enabled) return Skipped("Collections is switched off.");
        if (!await OneRun.WaitAsync(0, ct)) return Skipped("Another automatic run is in progress in this process.");
        try
        {
            var today = clock.BusinessDate;
            var scheduled = Stages.Where(stage => CollectionsCampaignPolicy.ScheduledDates(stage, today).Contains(today)).ToList();
            if (scheduled.Count == 0) return Skipped($"No campaign is scheduled for today ({today:dd MMM yyyy}).");

            // The review data must have been built TODAY and be fresh: the schedule, legal-release and repeated-call checks are evaluated for the day it was built.
            var run = await store.GetCurrentRunAsync(ct);
            if (run is null || run.AsOfDate != today || query.IsStale(run))
            {
                var started = await refresh.StartScheduledAsync(ct);
                if (!started.IsSuccess) return Skipped($"The review data could not be refreshed ({started.Outcome}); nothing was dispatched.");
                if (started.Outcome == CollectionsOutcome.Replayed) return Skipped("A review refresh is already running; the next automatic run continues.");
                await refresh.RunAsync(started.Value!.RunId, ct);
                run = await store.GetCurrentRunAsync(ct);
                if (run is null || run.AsOfDate != today || query.IsStale(run))
                    return Skipped("The review data could not be refreshed; nothing was dispatched. The next automatic run tries again.");
            }

            var results = new List<AutoDispatchStageResult>();
            foreach (var stage in scheduled) results.Add(await DispatchAsync(stage, today, ct));
            foreach (var r in results) logger.LogInformation("Automatic dispatch {Stage}: {Outcome} ({Count}) {Detail}", r.Stage, r.Outcome, r.Count, r.Detail);
            return new(true, null, results);
        }
        finally { OneRun.Release(); }
    }

    private async Task<AutoDispatchStageResult> DispatchAsync(CollectionsCampaignStage stage, DateOnly today, CancellationToken ct)
    {
        var type = CampaignReminderTypes.FromStage(stage);
        if (stage == CollectionsCampaignStage.LegalNotice && !campaignOptions.LegalNoticeExportEnabled)
            return new(type.ToString(), "NotReleased", "Legal notices have not been released for customers.", 0);
        // Auto resolves the Ready records of the campaign (the schedule applies: a record outside it is not Ready), with the contact-frequency limit.
        var request = new SelectionRequest(new ReviewFilter(ReminderType: type.ToString(), SendingMode: CollectionsSendingModes.Auto), ReviewQueryService.ModeAllMatching);
        var resolved = await query.ResolveAsync(request, ct);
        if (resolved.Error is not null) return new(type.ToString(), "Refused", resolved.Error, 0);
        if (resolved.Records.Count == 0) return new(type.ToString(), "NothingReady", "No customer is Ready for this campaign.", 0);

        // The operator who switched AutoSendingEnabled on accepted that the campaign's own Genesys campaign dials what is uploaded to its list; there is no person to tick the box.
        var confirm = new ConfirmDispatchRequest(request, resolved.Records.Count, ReviewQueryService.Fingerprint(resolved.Records),
            $"auto:{today:yyyy-MM-dd}:{type}", AcknowledgeActiveCampaignRisk: true);
        var result = await dispatch.ConfirmAutoAsync(confirm, ct);
        if (result.IsSuccess) return new(type.ToString(), result.Outcome == CollectionsOutcome.Replayed ? "AlreadyDispatched" : "Dispatched", null, resolved.Records.Count);
        // The same key with a different list: this campaign was already dispatched today (customers who became Ready later wait for the next scheduled date).
        if (result.Outcome == CollectionsOutcome.IdempotencyConflict) return new(type.ToString(), "AlreadyDispatched", "This campaign was already dispatched today.", 0);
        return new(type.ToString(), "Refused", result.Detail ?? result.Outcome.ToString(), 0);
    }

    private static AutoDispatchResult Skipped(string detail) => new(false, detail, []);
}
