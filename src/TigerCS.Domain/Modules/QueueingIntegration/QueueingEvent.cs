namespace TigerCS.Domain.Modules.QueueingIntegration;

public enum QueueingEventStatus : byte
{
    /// <summary>Received and recorded; processing has not completed (in flight, or interrupted - a redelivery or the retry sweep finishes it).</summary>
    Received = 1,

    /// <summary>Applied to the ticket.</summary>
    Processed = 2,

    /// <summary>
    /// Could not be applied YET - the ticket does not exist yet (out of order), or a mapping / configuration it needs is missing. The event is
    /// kept, nothing was changed, and it is retried (<see cref="QueueingEvent.NextRetryAtUtc"/>) until it applies or runs out of attempts.
    /// </summary>
    Unresolved = 3,

    /// <summary>Older than the ticket's applied state: recorded for the audit trail, deliberately not applied.</summary>
    Superseded = 4,

    /// <summary>Valid but not applicable (e.g. a state change after the ticket is closed). Recorded, never retried.</summary>
    Rejected = 5,

    /// <summary>An <see cref="Unresolved"/> event that exhausted its attempts. Needs a person; redelivering the same EventId starts it again.</summary>
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
    public string Payload { get; private set; } = string.Empty;
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
        ReceivedAtUtc = receivedAtUtc;
        Status = QueueingEventStatus.Received;
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

    /// <summary>Unresolved, with the next retry; after <paramref name="maxAttempts"/> it becomes <see cref="QueueingEventStatus.Failed"/> and stops being retried.</summary>
    public void MarkUnresolved(string outcomeCode, string? detail, long? ticketId, DateTime nowUtc, DateTime nextRetryAtUtc, int maxAttempts)
    {
        AttemptCount++;
        Settle(outcomeCode, detail, ticketId);
        if (AttemptCount >= maxAttempts)
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

    /// <summary>A redelivery of a Failed event starts a fresh retry cycle.</summary>
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
}
