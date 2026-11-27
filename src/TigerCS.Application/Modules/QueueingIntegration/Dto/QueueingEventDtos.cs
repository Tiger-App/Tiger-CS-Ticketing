using System.Text.Json;
using System.Text.Json.Serialization;
using TigerCS.Domain.Modules.QueueingIntegration;

namespace TigerCS.Application.Modules.QueueingIntegration.Dto;

/// <summary>
/// The one request body of <c>POST /api/integrations/queueing/events</c>. Property names are matched case-insensitively (the Queueing System
/// sends PascalCase). Every field is optional on the wire because which ones are required depends on <see cref="EventType"/>
/// (<see cref="QueueingEventValidator"/>), so a missing field is reported by name, per event, instead of as a generic binding error.
/// </summary>
/// <param name="EventId">Unique id of this event, chosen by the Queueing System. Idempotency key: the same EventId is applied once, however often it is delivered.</param>
/// <param name="EventType">TicketCreated, TicketAssigned, TicketReassigned, TicketReturned, TicketCommentAdded, TicketRecalled, TicketClosed or TicketCancelled.</param>
/// <param name="ExternalTicketId">The queue ticket's id. One TigerCS ticket per ExternalTicketId, enforced by the database.</param>
/// <param name="EventSequence">Position of the event in the queue ticket's own history (1, 2, 3 ...). A state-changing event is never allowed to overwrite a newer one.</param>
/// <param name="QueueNumber">The number the customer was shown (e.g. A-017). Required on TicketCreated.</param>
/// <param name="CompanyId">Queueing System company. Mapped to a TigerCS department. Required on TicketCreated.</param>
/// <param name="TicketTypeId">Queueing System ticket type. Optionally mapped to a TigerCS request type (an explicit mapping overrides the department's General request type). Required on TicketCreated.</param>
/// <param name="CustomerName">Optional. Display only.</param>
/// <param name="CustomerPhone">The customer's mobile number. Used for the CRM lookup. Optional.</param>
/// <param name="CustomerEmail">Optional. Display only.</param>
/// <param name="AssignedUserId">The Queueing System id of the receiving employee. Required on TicketAssigned and TicketReassigned.</param>
/// <param name="PerformedByUserId">The Queueing System id of the employee who performed the action, when different from the assignee. Optional; recorded in the history.</param>
/// <param name="CounterId">The counter the ticket was called to. Optional; recorded in the history.</param>
/// <param name="Comment">The comment text. Required on TicketCommentAdded.</param>
/// <param name="Reason">Why (reassigned, returned, recalled, closed, cancelled). Optional; recorded as the history note.</param>
/// <param name="OccurredAtUtc">When the event happened in the Queueing System (UTC). Preserved on every record TigerCS writes for it.</param>
/// <param name="CompanyName">Optional display name of the company. Recorded in the audit.</param>
/// <param name="TicketTypeName">Optional. The Queueing System's own name for the service (ticket type), kept and shown by TigerCS exactly as sent. Display only: it never routes, and it is not part of the event's idempotency identity. Read from <c>TicketCreated</c> only. Max 200.</param>
/// <param name="ProjectCode">Optional project code (text only, not matched to a TigerCS unit).</param>
/// <param name="ProjectName">Optional project name. Shown in the ticket summary.</param>
/// <param name="UnitCode">Optional unit code. Shown in the ticket summary.</param>
public sealed record QueueingEventRequest(
    string? EventId,
    string? EventType,
    string? ExternalTicketId,
    long? EventSequence,
    string? QueueNumber = null,
    string? CompanyId = null,
    string? TicketTypeId = null,
    string? CustomerName = null,
    string? CustomerPhone = null,
    string? CustomerEmail = null,
    string? AssignedUserId = null,
    string? PerformedByUserId = null,
    string? CounterId = null,
    string? Comment = null,
    string? Reason = null,
    DateTime? OccurredAtUtc = null,
    string? CompanyName = null,
    string? TicketTypeName = null,
    string? ProjectCode = null,
    string? ProjectName = null,
    string? UnitCode = null)
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The request in one stable form, stored with the event: the retry sweep and a redelivery re-read it, so it carries every field the Queueing System sent.</summary>
    public string ToCanonicalJson() => JsonSerializer.Serialize(Normalised(), Canonical);

    /// <summary>
    /// The request in the form its idempotency hash is taken over: <see cref="ToCanonicalJson"/> without the display-only <see cref="TicketTypeName"/>. The same EventId with a
    /// different body is a conflict, never a silent overwrite - and for every request that carries no name this is byte-for-byte the canonical JSON every event stored before the
    /// field existed was hashed from, so those hashes stay valid. Any other changed field still changes this string.
    /// </summary>
    public string ToIdentityJson() => JsonSerializer.Serialize(Normalised() with { TicketTypeName = null }, Canonical);

    private QueueingEventRequest Normalised() => this with
    {
        EventId = EventId?.Trim(),
        ExternalTicketId = ExternalTicketId?.Trim(),
        OccurredAtUtc = OccurredAtUtc is { } at ? QueueingEventValidator.AsUtc(at) : null
    };
}

/// <summary>A request that passed <see cref="QueueingEventValidator"/>: typed, trimmed, every required field present.</summary>
public sealed record QueueingEventMessage(
    string EventId,
    QueueingEventType EventType,
    string ExternalTicketId,
    long EventSequence,
    DateTime OccurredAtUtc,
    string? QueueNumber,
    string? CompanyId,
    string? TicketTypeId,
    string? CustomerName,
    string? CustomerPhone,
    string? CustomerEmail,
    string? AssignedUserId,
    string? PerformedByUserId,
    string? CounterId,
    string? Comment,
    string? Reason,
    string? CompanyName = null,
    string? TicketTypeName = null,
    string? ProjectCode = null,
    string? ProjectName = null,
    string? UnitCode = null);

/// <summary>What happened to the event, as the Queueing System sees it. Serialised by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueingResultStatus
{
    /// <summary>Applied (or a no-op because the ticket was already in that state).</summary>
    Processed,

    /// <summary>The EventId was already received; the original outcome is returned and nothing is applied again.</summary>
    Duplicate,

    /// <summary>Stored, not applied yet (ticket not created yet / a mapping missing). TigerCS retries it; the Queueing System need not resend.</summary>
    Pending,

    /// <summary>Older than the ticket's current state; recorded, deliberately not applied.</summary>
    Superseded,

    /// <summary>Valid but cannot apply (for example a state change on a closed ticket).</summary>
    Rejected,

    /// <summary>Failed validation; nothing was stored.</summary>
    Invalid,

    /// <summary>The same EventId arrived with a different body; nothing was changed.</summary>
    Conflict,

    /// <summary>The integration is switched off.</summary>
    Disabled,

    /// <summary>Another request is changing the same ticket. Safe to resend the same event.</summary>
    Busy
}

/// <summary>The response body.</summary>
/// <param name="EventId">Echoed.</param>
/// <param name="ExternalTicketId">Echoed.</param>
/// <param name="Status">See <see cref="QueueingResultStatus"/>.</param>
/// <param name="Outcome">A stable machine-readable code (see <c>QueueingOutcomeCodes</c>).</param>
/// <param name="TicketId">The TigerCS ticket, when it exists.</param>
/// <param name="TicketNumber">The TigerCS ticket number, when it exists.</param>
/// <param name="TicketStatus">The TigerCS ticket status after the event.</param>
/// <param name="Detail">A human-readable explanation, for anything other than a plain success.</param>
/// <param name="Retryable">True when TigerCS will retry it by itself, or the same event can safely be resent.</param>
/// <param name="Errors">Field-level validation errors (Invalid only).</param>
public sealed record QueueingEventResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EventId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExternalTicketId,
    QueueingResultStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TicketId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TicketNumber = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TicketStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    bool Retryable = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Errors = null)
{
    /// <summary>True only for the response that created the ticket (HTTP 201).</summary>
    [JsonIgnore]
    public bool CreatedTicket { get; init; }
}

/// <summary>One held (Unresolved) or Failed event, for monitoring. Carries identifiers and the reason only - never the customer payload.</summary>
public sealed record QueueingMonitoringEventDto(
    string EventId,
    string EventType,
    string ExternalTicketId,
    long EventSequence,
    string Status,
    string? OutcomeCode,
    string? OutcomeDetail,
    int AttemptCount,
    DateTime ReceivedAtUtc,
    DateTime? NextRetryAtUtc);

/// <summary>A read-only snapshot of the integration's health: how many events are in each state and which ones need attention.</summary>
public sealed record QueueingMonitoringSnapshot(
    DateTime AsOfUtc,
    IReadOnlyDictionary<string, int> CountsByStatus,
    IReadOnlyDictionary<string, int> HeldOrFailedByOutcome,
    DateTime? OldestUnresolvedReceivedAtUtc,
    IReadOnlyList<QueueingMonitoringEventDto> Attention)
{
    /// <summary>How many queue tickets have an unresolved (held or Failed) state event, i.e. are blocked: their later events wait behind it.</summary>
    public int BlockedTicketCount { get; init; }

    /// <summary>How many of those are alerts: blocked longer than <c>QueueingIntegration:BlockedAlertAfterMinutes</c>, or Failed.</summary>
    public int AlertCount { get; init; }

    /// <summary>Age in seconds of the longest-blocked ticket's head event, or null when nothing is blocked.</summary>
    public int? OldestBlockedAgeSeconds { get; init; }

    /// <summary>One row per blocked queue ticket (oldest first), described by its head event. Identifiers and reasons only - never customer data.</summary>
    public IReadOnlyList<QueueingBlockedTicketDto> BlockedTickets { get; init; } = [];
}

/// <summary>
/// A queue ticket whose events are blocked, described by its HEAD event (the lowest-sequence unresolved one - everything behind it waits for it). FailureClass is
/// Ordering / Configuration / Transient / Unexplained (how the head is retried, see <c>QueueingFailureClass</c>); HeldBehind is how many later events wait behind the head;
/// Alert is true when the head has been blocked longer than the alert threshold, or has Failed.
/// </summary>
public sealed record QueueingBlockedTicketDto(
    string ExternalTicketId,
    string HeadEventId,
    long HeadEventSequence,
    string HeadStatus,
    string? HeadOutcomeCode,
    string FailureClass,
    int AttemptCount,
    DateTime HeadReceivedAtUtc,
    int AgeSeconds,
    int HeldBehind,
    bool Alert);

/// <summary>The result of an administrator's reprocess of one event: the event as it stands afterwards, and what the processor answered.</summary>
public sealed record QueueingReprocessResult(
    string EventId,
    string ExternalTicketId,
    string PreviousStatus,
    string? PreviousOutcomeCode,
    string Status,
    string? OutcomeCode,
    string? Detail);
