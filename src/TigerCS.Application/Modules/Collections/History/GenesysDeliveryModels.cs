using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.History;

/// <summary>The message statuses TigerCS keeps. They come ONLY from what Genesys reported for a message; nothing is inferred from an upload or from another status.</summary>
public static class GenesysMessageStates
{
    public const string Queued = "Queued", Sent = "Sent", Delivered = "Delivered", Failed = "Failed", Unknown = "Unknown";

    /// <summary>Maps a raw Genesys <c>messageStatus</c> to a kept status. Null = not an outbound delivery state at all (an inbound reply, "received"): it is not stored or counted.</summary>
    public static string? FromGenesys(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" => Unknown,
        "received" => null,
        "queued" => Queued,
        "sent" or "published" => Sent,
        "delivered" or "read" => Delivered,
        "failed" or "undelivered" => Failed,
        _ => Unknown
    };
}

/// <summary>The contact-level outcomes shown on the Campaign List and in the details.</summary>
public static class GenesysContactOutcomes
{
    public const string NotSent = "NotSent", Pending = "Pending", Sent = "Sent", Delivered = "Delivered", Failed = "Failed";

    public static string Label(string outcome) => outcome switch
    {
        NotSent => "Not sent",
        Pending => "Pending / Unknown",
        Failed => "Failed",
        _ => outcome
    };
}

/// <summary>
/// The delivery rules. One CONTACT (dispatch item) is counted exactly once, however many message attempts or delivery events Genesys holds for it:
/// <list type="bullet">
/// <item><b>NotSent</b>: the contact never reached Genesys (excluded, released, not yet submitted, or the upload was rejected). Kept apart from delivery failures.</item>
/// <item><b>Delivered</b>: at least one attempt is confirmed delivered. <b>Sent</b>: no confirmed delivery, at least one attempt confirmed sent (so Sent includes Delivered in the list).</item>
/// <item><b>Failed</b>: Genesys confirmed the failure of every attempt that has a final status.</item>
/// <item><b>Pending</b>: uploaded, but no attempt has a final outcome (nothing reported, queued, or an unrecognised status).</item>
/// </list>
/// Delivered is never calculated as Sent minus Failed: it is only ever a count of delivery confirmations.
/// </summary>
public static class GenesysDeliveryRules
{
    public static string ContactOutcome(IEnumerable<string> messageStatuses)
    {
        var any = false; var sent = false; var failed = false;
        foreach (var s in messageStatuses)
        {
            any = true;
            if (s == GenesysMessageStates.Delivered) return GenesysContactOutcomes.Delivered;
            if (s == GenesysMessageStates.Sent) sent = true;
            else if (s == GenesysMessageStates.Failed) failed = true;
        }
        if (!any) return GenesysContactOutcomes.Pending;
        return sent ? GenesysContactOutcomes.Sent : failed ? GenesysContactOutcomes.Failed : GenesysContactOutcomes.Pending;
    }

    /// <summary>True when a record reached Genesys's contact list: assigned to a batch, not excluded/released, and not rejected by the upload.</summary>
    public static bool ReachedGenesys(DispatchItemStatus status, bool inBatch) =>
        inBatch && status is DispatchItemStatus.UploadedToGenesys or DispatchItemStatus.UnknownOutcome or DispatchItemStatus.Approved;

    public static string Outcome(DispatchItemStatus status, bool inBatch, IEnumerable<string> messageStatuses) =>
        ReachedGenesys(status, inBatch) ? ContactOutcome(messageStatuses) : GenesysContactOutcomes.NotSent;
}

/// <summary>The confirmed figures of one campaign. <c>Sent + Failed + Pending + NotSent = Contacts</c> and <c>Delivered &lt;= Sent</c>; <c>MessageAttempts</c> counts messages, not contacts.</summary>
public sealed record GenesysDeliveryTally(int Contacts, int Sent, int Delivered, int Failed, int Pending, int NotSent, int MessageAttempts)
{
    public static GenesysDeliveryTally Tally(IEnumerable<(string Outcome, int Attempts)> contacts)
    {
        int c = 0, s = 0, d = 0, f = 0, p = 0, n = 0, a = 0;
        foreach (var (outcome, attempts) in contacts)
        {
            c++; a += attempts;
            switch (outcome)
            {
                case GenesysContactOutcomes.Delivered: d++; s++; break;
                case GenesysContactOutcomes.Sent: s++; break;
                case GenesysContactOutcomes.Failed: f++; break;
                case GenesysContactOutcomes.NotSent: n++; break;
                default: p++; break;
            }
        }
        return new(c, s, d, f, p, n, a);
    }
}

// ---------------------------------------------------------------- store contract (read from Genesys-synced data; the sync service is the only writer)

public sealed record SyncItemRow(long ItemId, string? ContactId, DispatchItemStatus Status, bool InBatch);

public sealed record SyncInput(long DispatchId, DateTime InitiatedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, IReadOnlyList<string> CampaignIds, IReadOnlyList<string> ContactListIds, IReadOnlyList<SyncItemRow> Items);

public sealed record StoredMessage(long ItemId, string ConversationId, string MessageId, string Status, DateTime? SentAtUtc, DateTime? DeliveredAtUtc, string? FailureReason);

public sealed record SyncStateRow(long DispatchId, DateTime LastSyncedAtUtc, string Outcome, string? Error, GenesysDeliveryTally Tally, int ConversationsFound, DateTime? LastCompletedAtUtc);

public interface IGenesysDeliveryStore
{
    Task<SyncInput?> LoadForSyncAsync(Guid publicId, CancellationToken ct);
    Task<IReadOnlyList<StoredMessage>> ListMessagesAsync(long dispatchId, CancellationToken ct);
    Task<IReadOnlyList<StoredMessage>> ListMessagesForItemsAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct);
    /// <summary>Inserts new messages and updates existing ones by (conversation id, message id): never a second row for the same message.</summary>
    Task UpsertMessagesAsync(long dispatchId, IReadOnlyList<StoredMessage> messages, DateTime syncedAtUtc, CancellationToken ct);
    Task SaveStateAsync(SyncStateRow state, CancellationToken ct);
    Task<IReadOnlyDictionary<long, SyncStateRow>> GetStatesAsync(IReadOnlyCollection<long> dispatchIds, CancellationToken ct);
    Task<DateTime?> GetLastSyncedAsync(CancellationToken ct);
}

// ---------------------------------------------------------------- DTOs

/// <summary>What the Campaign List shows per campaign. When <c>Synced</c> is false no delivery figure is known yet: the figures are then null, never zero.</summary>
public sealed record GenesysDeliveryDto(
    bool Synced, DateTime? LastSyncedAtUtc, string? SyncOutcome, string? SyncError,
    int? Sent, int? Delivered, int? Failed, int? Pending, int NotSent, int MessageAttempts);

public sealed record GenesysSyncResultDto(int Requested, int Synced, int Skipped, int Failed, DateTime? LastSyncedAtUtc, IReadOnlyList<string> Problems);
