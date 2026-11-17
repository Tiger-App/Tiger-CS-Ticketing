using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueueingSystem.TigerCsSender;

/// <summary>What TigerCS did with an event. Serialised by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueingResultStatus
{
    /// <summary>Applied (or already in that state). Delivered; nothing more to do.</summary>
    Processed,

    /// <summary>This EventId was already received; the original outcome is returned. Delivered.</summary>
    Duplicate,

    /// <summary>Stored but not applied yet (its ticket does not exist yet, or a mapping / employee is not set up). TigerCS retries it itself - do NOT resend.</summary>
    Pending,

    /// <summary>Older than the ticket's applied state; recorded, deliberately not applied. Delivered; do not resend.</summary>
    Superseded,

    /// <summary>Valid but not applicable (for example a state change after the ticket was closed). Do not resend.</summary>
    Rejected,

    /// <summary>Failed validation (HTTP 400). Nothing stored. Fix the event; do not resend as is.</summary>
    Invalid,

    /// <summary>The same EventId was already received with different content (HTTP 409). A bug in the sender.</summary>
    Conflict,

    /// <summary>TigerCS has the integration switched off (HTTP 503). Retry later.</summary>
    Disabled,

    /// <summary>Another request is changing the same ticket (HTTP 503 with Retry-After). Resend the same event.</summary>
    Busy
}

/// <summary>The response body of <c>POST /api/integrations/queueing/events</c> (camelCase on the wire; members with no value are omitted).</summary>
public sealed record QueueingEventResponse
{
    [JsonPropertyName("eventId")] public string? EventId { get; init; }

    [JsonPropertyName("externalTicketId")] public string? ExternalTicketId { get; init; }

    [JsonPropertyName("status")] public QueueingResultStatus Status { get; init; }

    /// <summary>A stable machine-readable code: TICKET_CREATED, APPLIED, TICKET_ALREADY_EXISTS, ALREADY_IN_STATE, STALE_EVENT, TICKET_ALREADY_CLOSED, AWAITING_TICKET_CREATED, COMPANY_NOT_MAPPED, TICKET_TYPE_NOT_MAPPED, REQUEST_TYPE_UNUSABLE, USER_NOT_MAPPED, USER_INACTIVE, USER_NOT_IN_DEPARTMENT, CHANNEL_NOT_CONFIGURED, PRIORITY_NOT_CONFIGURED, SERVICE_ACCOUNT_NOT_CONFIGURED, CONCURRENT_UPDATE, PROCESSING_FAILED, TICKET_NOT_ELIGIBLE, EVENT_ID_REUSED, VALIDATION_FAILED, INTEGRATION_DISABLED.</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }

    [JsonPropertyName("ticketId")] public long? TicketId { get; init; }

    [JsonPropertyName("ticketNumber")] public string? TicketNumber { get; init; }

    /// <summary>The TigerCS ticket status after the event (Open, InProgress, Closed ...).</summary>
    [JsonPropertyName("ticketStatus")] public string? TicketStatus { get; init; }

    [JsonPropertyName("detail")] public string? Detail { get; init; }

    /// <summary>True when TigerCS will retry the event by itself (<see cref="QueueingResultStatus.Pending"/>) or the same event can safely be resent (<see cref="QueueingResultStatus.Busy"/>).</summary>
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }

    /// <summary>Validation errors by member name (<see cref="QueueingResultStatus.Invalid"/> only).</summary>
    [JsonPropertyName("errors")] public Dictionary<string, string[]>? Errors { get; init; }

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
