namespace TigerCS.Domain.Modules.QueueingIntegration;

public enum QueueingEventStatus : byte
{
    /// <summary>Received and recorded; processing has not completed (in flight, or interrupted - a redelivery or the retry sweep finishes it).</summary>
    Received = 1,

    /// <summary>Applied to the ticket.</summary>
    Processed = 2,

    /// <summary>
    /// Could not be applied YET - the ticket does not exist yet (out of order), or a mapping / configuration it needs is missing. The event is
    /// kept, nothing was changed, and it is retried (<see cref="QueueingEvent.NextRetryAtUtc"/>) until it applies. A configuration hold (a mapping or an
    /// employee is missing) is retried indefinitely with a capped exponential backoff; a transient or unexplained failure is bounded (then <see cref="Failed"/>);
    /// an event held only because an EARLIER event of the same queue ticket is unresolved has no retry of its own - it is released, in order, when that event settles.
    /// </summary>
    Unresolved = 3,

    /// <summary>Older than the ticket's applied state: recorded for the audit trail, deliberately not applied.</summary>
    Superseded = 4,

    /// <summary>Valid but not applicable (e.g. a state change after the ticket is closed). Recorded, never retried.</summary>
    Rejected = 5,

    /// <summary>An <see cref="Unresolved"/> event of a bounded failure class that exhausted its attempts. Never retried automatically and never skipped: every later event of the same queue ticket stays held until a person reprocesses it (audited administrator action).</summary>
    Failed = 6
}

/// <summary>
/// One row per event the Queueing System ever delivered - the inbox. <c>(Source, EventId)</c> is unique at the database, so a duplicate or two
/// concurrent deliveries of the same event are one row, and the stored outcome is what every redelivery is answered with. The payload is kept
/// verbatim so an unresolved event can be retried exactly as it arrived.
/// </summary>
public class QueueingEvent
{
    public const string SourceName = "QueueingSystem";
    public const int EventIdMaxLength = 100;
    public const int DetailMaxLength = 1000;

    public long QueueingEventId { get; private set; }
    public string Source { get; private set; } = SourceName;
    public string EventId { get; private set; } = string.Empty;
    public QueueingEventType EventType { get; private set; }
    public string ExternalTicketId { get; private set; } = string.Empty;
    public long EventSequence { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>
    /// The event as received (canonical JSON), kept so an unresolved event can be retried exactly as sent. Null once it has been purged: a successfully
    /// processed event's payload is erased after the retention period (<see cref="PayloadPurgedAtUtc"/>); the row, its identifiers and
    /// <see cref="PayloadHash"/> stay, so idempotency and the audit trail never depend on the payload.
    /// </summary>
    public string? Payload { get; private set; }

    /// <summary>SHA-256 (lower-case hex) of the canonical payload. Written once at receipt and never erased: a redelivery of the same EventId is compared by hash, so a purged payload still detects an EventId reused with different content.</summary>
    public string PayloadHash { get; private set; } = string.Empty;

    public DateTime? PayloadPurgedAtUtc { get; private set; }

    public QueueingEventStatus Status { get; private set; }

    /// <summary>A stable machine-readable code for why the event is not <see cref="QueueingEventStatus.Processed"/> (see <c>QueueingOutcomeCodes</c>), or the success outcome.</summary>
    public string? OutcomeCode { get; private set; }
    public string? OutcomeDetail { get; private set; }
    public long? TicketId { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? NextRetryAtUtc { get; private set; }
    public DateTime ReceivedAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }

    private QueueingEvent() { }

    public QueueingEvent(
        string eventId, QueueingEventType eventType, string externalTicketId, long eventSequence, DateTime occurredAtUtc, string payload,
        DateTime receivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTicketId);
        EventId = eventId.Trim();
        EventType = eventType;
        ExternalTicketId = externalTicketId.Trim();
        EventSequence = eventSequence;
        OccurredAtUtc = occurredAtUtc;
        Payload = payload;
        PayloadHash = HashOf(payload);
        ReceivedAtUtc = receivedAtUtc;
        Status = QueueingEventStatus.Received;
    }

    public static string HashOf(string payload) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    /// <summary>True when <paramref name="canonicalPayload"/> is the content this EventId was first received with (compared by hash, so it works after a purge).</summary>
    public bool HasSamePayloadAs(string canonicalPayload) => string.Equals(PayloadHash, HashOf(canonicalPayload), StringComparison.Ordinal);

    /// <summary>
    /// Erases the payload of a <b>successfully processed</b> event (the retention rule). Everything that gives the event its identity - the ids, sequence, type,
    /// timestamps, outcome, ticket link and <see cref="PayloadHash"/> - is kept. Refused for every other status: an unresolved event is still waiting to be retried
    /// from its payload and must never be purged. Returns false when there was nothing to purge.
    /// </summary>
    public bool PurgePayload(DateTime nowUtc)
    {
        if (Status != QueueingEventStatus.Processed)
        {
            throw new InvalidOperationException("Only the payload of a successfully processed event can be purged.");
        }

        if (Payload is null)
        {
            return false;
        }

        Payload = null;
        PayloadPurgedAtUtc = nowUtc;
        return true;
    }

    public void MarkProcessed(string outcomeCode, string? detail, long? ticketId, DateTime nowUtc)
    {
        Status = QueueingEventStatus.Processed;
        Settle(outcomeCode, detail, ticketId);
        NextRetryAtUtc = null;
        ProcessedAtUtc = nowUtc;
    }

    public void MarkSuperseded(string outcomeCode, string? detail, long? ticketId, DateTime nowUtc)
    {
        Status = QueueingEventStatus.Superseded;
        Settle(outcomeCode, detail, ticketId);
        NextRetryAtUtc = null;
        ProcessedAtUtc = nowUtc;
    }

    public void MarkRejected(string outcomeCode, string? detail, long? ticketId, DateTime nowUtc)
    {
        Status = QueueingEventStatus.Rejected;
        Settle(outcomeCode, detail, ticketId);
        NextRetryAtUtc = null;
        ProcessedAtUtc = nowUtc;
    }

    /// <summary>
    /// Unresolved, with the next retry. <paramref name="maxAttempts"/> bounds a transient or unexplained failure: after that many attempts the event becomes
    /// <see cref="QueueingEventStatus.Failed"/> and is no longer retried automatically. <c>null</c> (a configuration hold: a mapping or an employee is missing) never
    /// fails the event - it is retried for as long as it takes, at the capped backoff, and monitoring raises the alert.
    /// </summary>
    public void MarkUnresolved(string outcomeCode, string? detail, long? ticketId, DateTime nowUtc, DateTime nextRetryAtUtc, int? maxAttempts)
    {
        AttemptCount++;
        Settle(outcomeCode, detail, ticketId);
        if (maxAttempts is { } max && AttemptCount >= max)
        {
            Status = QueueingEventStatus.Failed;
            NextRetryAtUtc = null;
        }
        else
        {
            Status = QueueingEventStatus.Unresolved;
            NextRetryAtUtc = nextRetryAtUtc;
        }
    }

    /// <summary>
    /// Held only because an earlier event of the same queue ticket (or the ticket's creation) is not applied yet. It has no retry of its own and consumes no attempt:
    /// it is applied, in sequence order, the moment the earlier event settles.
    /// </summary>
    public void MarkHeldForOrdering(string outcomeCode, string? detail, long? ticketId)
    {
        Settle(outcomeCode, detail, ticketId);
        Status = QueueingEventStatus.Unresolved;
        NextRetryAtUtc = null;
    }

    /// <summary>A redelivery of a Failed event, or an administrator's reprocess, starts a fresh retry cycle (the attempt counter restarts; the payload and the EventId never change).</summary>
    public void Reopen()
    {
        Status = QueueingEventStatus.Received;
        AttemptCount = 0;
        NextRetryAtUtc = null;
    }

    public bool IsSettled => Status is QueueingEventStatus.Processed or QueueingEventStatus.Superseded or QueueingEventStatus.Rejected;

    private void Settle(string outcomeCode, string? detail, long? ticketId)
    {
        OutcomeCode = outcomeCode;
        OutcomeDetail = detail is { Length: > DetailMaxLength } ? detail[..DetailMaxLength] : detail;
        TicketId = ticketId ?? TicketId;
    }
}

/// <summary>Stable outcome codes recorded on <see cref="QueueingEvent.OutcomeCode"/> and returned to the Queueing System.</summary>
public static class QueueingOutcomeCodes
{
    public const string TicketCreated = "TICKET_CREATED";
    public const string TicketAlreadyExists = "TICKET_ALREADY_EXISTS";
    public const string Applied = "APPLIED";
    public const string AlreadyInState = "ALREADY_IN_STATE";
    public const string StaleEvent = "STALE_EVENT";
    public const string TicketTerminal = "TICKET_ALREADY_CLOSED";
    public const string AwaitingTicket = "AWAITING_TICKET_CREATED";
    public const string CreationInProgress = "CREATION_IN_PROGRESS";
    public const string CompanyNotMapped = "COMPANY_NOT_MAPPED";
    public const string TicketTypeNotMapped = "TICKET_TYPE_NOT_MAPPED";
    public const string RequestTypeUnusable = "REQUEST_TYPE_UNUSABLE";
    public const string UserNotMapped = "USER_NOT_MAPPED";
    public const string UserInactive = "USER_INACTIVE";
    public const string UserNotInDepartment = "USER_NOT_IN_DEPARTMENT";
    public const string ChannelNotConfigured = "CHANNEL_NOT_CONFIGURED";
    public const string ServiceAccountNotConfigured = "SERVICE_ACCOUNT_NOT_CONFIGURED";
    public const string PriorityNotConfigured = "PRIORITY_NOT_CONFIGURED";
    public const string ConcurrentUpdate = "CONCURRENT_UPDATE";
    public const string ProcessingFailed = "PROCESSING_FAILED";
    public const string NotEligible = "TICKET_NOT_ELIGIBLE";
    public const string AwaitingPreviousEvent = "AWAITING_PREVIOUS_EVENT";

    /// <summary>
    /// How a hold is retried. <see cref="QueueingFailureClass.Ordering"/>: released in order, no retry of its own. <see cref="QueueingFailureClass.Configuration"/>: a
    /// mapping / employee / setting is missing - retried indefinitely at the capped backoff. Everything else is bounded.
    /// </summary>
    public static QueueingFailureClass Classify(string? outcomeCode) => outcomeCode switch
    {
        AwaitingPreviousEvent or CreationInProgress => QueueingFailureClass.Ordering,
        AwaitingTicket or CompanyNotMapped or TicketTypeNotMapped or RequestTypeUnusable or UserNotMapped or UserInactive or UserNotInDepartment
            or ChannelNotConfigured or ServiceAccountNotConfigured or PriorityNotConfigured => QueueingFailureClass.Configuration,
        ConcurrentUpdate => QueueingFailureClass.Transient,
        _ => QueueingFailureClass.Unexplained
    };
}

/// <summary>How an unresolved event is retried (see <see cref="QueueingOutcomeCodes.Classify"/>).</summary>
public enum QueueingFailureClass : byte
{
    /// <summary>An earlier event of the same queue ticket is unresolved: released in order when it settles, no retry or attempt of its own (the sweep picks it up only if it was orphaned by a race).</summary>
    Ordering = 1,

    /// <summary>Recoverable by configuration, or by the missing ticket-creation event arriving (a mapping, an employee, a setting): retried indefinitely at a capped exponential backoff; monitoring alerts.</summary>
    Configuration = 2,

    /// <summary>A race with another writer: retried at the backoff, bounded.</summary>
    Transient = 3,

    /// <summary>Not understood (an unexpected exception, an operation TigerCS refuses): bounded retries, then <see cref="QueueingEventStatus.Failed"/>; only an administrator's audited reprocess restarts it.</summary>
    Unexplained = 4
}
