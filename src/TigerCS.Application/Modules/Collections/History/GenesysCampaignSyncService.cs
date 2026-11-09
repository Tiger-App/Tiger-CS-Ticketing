using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.History;

/// <summary>
/// The manual "Sync from Genesys": reads, from Genesys Cloud, what really happened to the contacts TigerCS uploaded, and stores the confirmed result locally so the Campaign List never calls Genesys
/// just to open.
/// <list type="bullet">
/// <item><b>Read only towards Genesys</b> (analytics conversation-details query + conversation messages). Nothing is uploaded, resent, created or changed there. No background job: it runs only when a person presses the button.</item>
/// <item><b>Mapping.</b> A local campaign (dispatch) maps to its Genesys campaign id(s) and contact-list id(s) through its stored batches; each interaction is tied to a local contact by the Genesys contact id stored at upload. An
/// interaction whose contact is not in this dispatch (a Genesys campaign is reused by several dispatches) is ignored.</item>
/// <item><b>No double counting.</b> One stored row per Genesys message (conversation id + message id); the outcome of a contact is derived once from all its attempts (<see cref="GenesysDeliveryRules"/>). Contact counts and message-attempt counts are separate.</item>
/// <item><b>Honest gaps.</b> A failed or partial read never turns unknown into delivered or failed: contacts without a confirmed outcome stay Pending / Unknown, and a failed sync keeps the last confirmed figures.</item>
/// </list>
/// </summary>
public sealed class GenesysCampaignSyncService(
    CollectionsAuthorizationService authorization, CollectionsClock clock, IGenesysDeliveryStore store, IGenesysOutboundClient client,
    ILogger<GenesysCampaignSyncService> logger)
{
    /// <summary>The most interactions whose messages one campaign sync reads. A larger campaign is synced in several presses and is reported as Partial until complete.</summary>
    public const int MaxMessageReadsPerCampaign = 400;
    public const int MaxCampaignsPerPress = 25;
    private const int WindowDays = 30, MaxWindows = 12;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public const string Completed = "Completed", Partial = "Partial", Failed = "Failed";

    /// <summary>Syncs the given campaigns (public ids). <paramref name="force"/> false skips a campaign whose last sync already confirmed every submitted contact.</summary>
    public async Task<CollectionsResult<GenesysSyncResultDto>> SyncAsync(CollectionsCaller caller, IReadOnlyList<Guid> submissionIds, bool force, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders) return CollectionsResult<GenesysSyncResultDto>.Fail(CollectionsOutcome.Forbidden);
        var ids = submissionIds.Distinct().Take(MaxCampaignsPerPress).ToList();
        if (ids.Count == 0) return CollectionsResult<GenesysSyncResultDto>.Fail(CollectionsOutcome.InvalidRequest, "No campaign was selected to sync.");
        if (!await OneAtATime.WaitAsync(0, ct)) return CollectionsResult<GenesysSyncResultDto>.Fail(CollectionsOutcome.FinanceUnavailable, "A sync from Genesys is already running. Try again in a moment.");
        try
        {
            int synced = 0, skipped = 0, failed = 0; var problems = new List<string>();
            foreach (var id in ids)
            {
                var input = await store.LoadForSyncAsync(id, ct);
                if (input is null) { failed++; problems.Add("A selected campaign no longer exists."); continue; }
                var state = (await store.GetStatesAsync([input.DispatchId], ct)).GetValueOrDefault(input.DispatchId);
                if (!force && state is { Outcome: Completed } && state.Tally.Pending == 0 && state.LastCompletedAtUtc is not null) { skipped++; continue; }
                var (outcome, error) = await SyncOneAsync(input, state, ct);
                if (outcome == Failed) { failed++; problems.Add(error ?? "The sync failed."); } else synced++;
            }
            return CollectionsResult<GenesysSyncResultDto>.Ok(new(ids.Count, synced, skipped, failed, await store.GetLastSyncedAsync(ct), problems.Distinct().Take(3).ToList()));
        }
        finally { OneAtATime.Release(); }
    }

    private async Task<(string Outcome, string? Error)> SyncOneAsync(SyncInput input, SyncStateRow? previous, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var itemByContact = input.Items.Where(i => !string.IsNullOrEmpty(i.ContactId)).GroupBy(i => i.ContactId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string? error = null; var partial = false; var found = 0;

        if (input.CampaignIds.Count == 0)
            error = "This campaign has no Genesys campaign id stored (it was uploaded to a contact list only), so Genesys interactions cannot be matched to it.";
        else if (itemByContact.Count == 0)
            error = "No Genesys contact id is stored for this campaign's contacts, so Genesys interactions cannot be matched to them.";

        var stored = (await store.ListMessagesAsync(input.DispatchId, ct)).ToList();
        var storedByKey = stored.ToDictionary(m => (m.ConversationId.ToLowerInvariant(), m.MessageId.ToLowerInvariant()));
        var changed = new List<StoredMessage>();

        if (error is null)
        {
            var from = (input.StartedAtUtc ?? input.InitiatedAtUtc).AddHours(-1);
            var matched = new Dictionary<string, SyncItemRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var campaignId in input.CampaignIds)
            {
                var start = from;
                for (var w = 0; w < MaxWindows && start < now; w++)
                {
                    var end = start.AddDays(WindowDays) < now ? start.AddDays(WindowDays) : now;
                    var result = await client.GetCampaignConversationsAsync(campaignId, start, end, ct);
                    if (result.Outcome != GenesysUploadOutcome.Accepted) { error = result.Error ?? "Genesys could not be read."; break; }
                    if (!result.Complete) partial = true;
                    foreach (var c in result.Conversations)
                        if (c.ContactId is not null && itemByContact.TryGetValue(c.ContactId, out var item)) matched[c.ConversationId] = item;
                    start = end;
                }
                if (error is not null) break;
            }
            found = matched.Count;

            if (error is null)
            {
                // Only interactions without a final outcome are read again: a Delivered or Failed message is final and costs no further call.
                var final = stored.GroupBy(m => m.ConversationId.ToLowerInvariant()).Where(g => g.All(m => m.Status is GenesysMessageStates.Delivered or GenesysMessageStates.Failed)).Select(g => g.Key).ToHashSet();
                var toRead = matched.Where(kv => !final.Contains(kv.Key.ToLowerInvariant())).ToList();
                if (toRead.Count > MaxMessageReadsPerCampaign) { partial = true; toRead = toRead.Take(MaxMessageReadsPerCampaign).ToList(); }
                foreach (var (conversationId, item) in toRead)
                {
                    var messages = await client.GetConversationMessagesAsync(conversationId, ct);
                    if (messages.Outcome != GenesysUploadOutcome.Accepted) { error = messages.Error ?? "Genesys messages could not be read."; break; }
                    foreach (var m in messages.Messages)
                    {
                        var status = GenesysMessageStates.FromGenesys(m.Status);
                        if (status is null) continue;
                        storedByKey.TryGetValue((conversationId.ToLowerInvariant(), m.MessageId.ToLowerInvariant()), out var before);
                        var sentAt = status == GenesysMessageStates.Sent ? m.MessageTimeUtc : before?.SentAtUtc;
                        var deliveredAt = status == GenesysMessageStates.Delivered ? m.MessageTimeUtc : null;
                        var reason = status == GenesysMessageStates.Failed ? m.FailureReason ?? "Genesys reported the message as failed (no reason given)." : null;
                        var row = new StoredMessage(item.ItemId, conversationId, m.MessageId, status, sentAt, deliveredAt, reason);
                        changed.Add(row); storedByKey[(conversationId.ToLowerInvariant(), m.MessageId.ToLowerInvariant())] = row;
                    }
                }
            }
        }

        try
        {
            if (changed.Count > 0) await store.UpsertMessagesAsync(input.DispatchId, changed, now, ct);
            var all = storedByKey.Values.ToList();
            var tally = Tally(input, all);
            var outcome = error is not null ? Failed : partial ? Partial : Completed;
            // A failed sync records that it failed and keeps the figures of the last completed one (the store does not overwrite them).
            await store.SaveStateAsync(new(input.DispatchId, now, outcome, error ?? (partial ? "Some interactions were not read in this sync; press Sync from Genesys again to continue." : null),
                error is not null && previous is not null ? previous.Tally : tally, found, previous?.LastCompletedAtUtc), ct);
            if (error is not null) logger.LogWarning("Genesys sync of a campaign failed: {Error}", error);
            return (outcome, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Saving the Genesys sync result failed ({ExceptionType}).", ex.GetType().Name);
            return (Failed, "The synced result could not be saved.");
        }
    }

    public static GenesysDeliveryTally Tally(SyncInput input, IReadOnlyList<StoredMessage> messages)
    {
        var byItem = messages.GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => g.ToList());
        return GenesysDeliveryTally.Tally(input.Items.Select(i =>
        {
            var own = byItem.GetValueOrDefault(i.ItemId) ?? [];
            return (GenesysDeliveryRules.Outcome(i.Status, i.InBatch, own.Select(m => m.Status)), own.Count);
        }));
    }
}
