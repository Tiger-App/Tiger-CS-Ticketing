using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Persists what a manual "Sync from Genesys" confirmed (messages and a per-campaign summary) and reads it back. Reads never call Genesys; the only writes are to the two sync tables
/// (CollectionsGenesysMessageResults, CollectionsGenesysSyncStates): dispatches, batches and items are never modified.
/// </summary>
public sealed class GenesysDeliveryStore(TigerCsDbContext db) : IGenesysDeliveryStore
{
    public async Task<SyncInput?> LoadForSyncAsync(Guid publicId, CancellationToken ct)
    {
        var d = await db.CollectionsDispatches.AsNoTracking().Where(x => x.PublicId == publicId)
            .Select(x => new { x.CollectionsDispatchId, x.InitiatedAtUtc, x.StartedAtUtc, x.CompletedAtUtc }).FirstOrDefaultAsync(ct);
        if (d is null) return null;
        var batches = await db.CollectionsGenesysBatches.AsNoTracking().Where(b => b.CollectionsDispatchId == d.CollectionsDispatchId)
            .Select(b => new { b.GenesysCampaignId, b.ContactListId }).ToListAsync(ct);
        var items = await db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsDispatchId == d.CollectionsDispatchId)
            .Select(i => new SyncItemRow(i.CollectionsDispatchItemId, i.GenesysContactId, i.Status, i.CollectionsGenesysBatchId != null)).ToListAsync(ct);
        return new(d.CollectionsDispatchId, d.InitiatedAtUtc, d.StartedAtUtc, d.CompletedAtUtc,
            batches.Select(b => b.GenesysCampaignId).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            batches.Select(b => b.ContactListId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), items);
    }

    public async Task<IReadOnlyList<StoredMessage>> ListMessagesAsync(long dispatchId, CancellationToken ct) =>
        (await db.CollectionsGenesysMessageResults.AsNoTracking().Where(m => m.CollectionsDispatchId == dispatchId).ToListAsync(ct)).Select(Map).ToList();

    public async Task<IReadOnlyList<StoredMessage>> ListMessagesForItemsAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct) =>
        itemIds.Count == 0 ? [] : (await db.CollectionsGenesysMessageResults.AsNoTracking().Where(m => itemIds.Contains(m.CollectionsDispatchItemId)).ToListAsync(ct)).Select(Map).ToList();

    private static StoredMessage Map(CollectionsGenesysMessageResult m) =>
        new(m.CollectionsDispatchItemId, m.GenesysConversationId, m.GenesysMessageId, m.Status, m.SentAtUtc, m.DeliveredAtUtc, m.FailureReason);

    public async Task UpsertMessagesAsync(long dispatchId, IReadOnlyList<StoredMessage> messages, DateTime syncedAtUtc, CancellationToken ct)
    {
        if (messages.Count == 0) return;
        var existing = (await db.CollectionsGenesysMessageResults.Where(m => m.CollectionsDispatchId == dispatchId).ToListAsync(ct))
            .ToDictionary(m => (m.GenesysConversationId.ToLowerInvariant(), m.GenesysMessageId.ToLowerInvariant()));
        foreach (var m in messages)
        {
            var key = (m.ConversationId.ToLowerInvariant(), m.MessageId.ToLowerInvariant());
            if (!existing.TryGetValue(key, out var row))
            {
                row = new CollectionsGenesysMessageResult { CollectionsDispatchId = dispatchId, GenesysConversationId = m.ConversationId, GenesysMessageId = m.MessageId };
                db.CollectionsGenesysMessageResults.Add(row);
                existing[key] = row;
            }
            row.CollectionsDispatchItemId = m.ItemId;
            row.GenesysContactId = m.ConversationId;
            row.Status = m.Status;
            row.SentAtUtc = m.SentAtUtc;
            row.DeliveredAtUtc = m.DeliveredAtUtc;
            row.FailureReason = m.FailureReason;
            row.SyncedAtUtc = syncedAtUtc;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveStateAsync(SyncStateRow s, CancellationToken ct)
    {
        var row = await db.CollectionsGenesysSyncStates.FirstOrDefaultAsync(x => x.CollectionsDispatchId == s.DispatchId, ct);
        if (row is null) { row = new CollectionsGenesysSyncState { CollectionsDispatchId = s.DispatchId }; db.CollectionsGenesysSyncStates.Add(row); }
        row.LastSyncedAtUtc = s.LastSyncedAtUtc; row.Outcome = s.Outcome; row.Error = s.Error is { Length: > 500 } e ? e[..500] : s.Error;
        row.ConversationsFound = s.ConversationsFound;
        // A failed sync keeps the last confirmed figures: only a completed (or partial) one replaces them.
        if (s.Outcome != "Failed")
        {
            row.Contacts = s.Tally.Contacts; row.Sent = s.Tally.Sent; row.Delivered = s.Tally.Delivered; row.Failed = s.Tally.Failed;
            row.Pending = s.Tally.Pending; row.NotSent = s.Tally.NotSent; row.MessageAttempts = s.Tally.MessageAttempts;
            row.LastCompletedAtUtc = s.LastSyncedAtUtc;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<long, SyncStateRow>> GetStatesAsync(IReadOnlyCollection<long> dispatchIds, CancellationToken ct) =>
        (await db.CollectionsGenesysSyncStates.AsNoTracking().Where(s => dispatchIds.Contains(s.CollectionsDispatchId)).ToListAsync(ct))
        .ToDictionary(s => s.CollectionsDispatchId, s => new SyncStateRow(s.CollectionsDispatchId, s.LastSyncedAtUtc, s.Outcome, s.Error,
            new GenesysDeliveryTally(s.Contacts, s.Sent, s.Delivered, s.Failed, s.Pending, s.NotSent, s.MessageAttempts), s.ConversationsFound, s.LastCompletedAtUtc));

    public async Task<DateTime?> GetLastSyncedAsync(CancellationToken ct) =>
        await db.CollectionsGenesysSyncStates.AsNoTracking().MaxAsync(s => (DateTime?)s.LastSyncedAtUtc, ct);
}
