using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueueingSystem.TigerCsSender;

/// <summary>
/// The one request body of <c>POST /api/integrations/queueing/events</c>. Member names are PascalCase on the wire (exactly as declared here). Members with no
/// value are omitted from the JSON. Which members are required depends on <see cref="EventType"/>: see <see cref="Validate"/>.
/// </summary>
public sealed record QueueingEventRequest
{
    /// <summary>Unique id of this event, chosen by the Queueing System (a GUID string is ideal). The idempotency key: the same EventId with the same content is applied once, however often it is sent. Never reuse an EventId for a different event. Max 100 characters.</summary>
    public required string EventId { get; init; }

    /// <summary>One of <see cref="QueueingEventType"/>, written by name.</summary>
    public required QueueingEventType EventType { get; init; }

    /// <summary>The queue ticket's id. One TigerCS ticket exists per value. Max 100 characters.</summary>
    public required string ExternalTicketId { get; init; }

    /// <summary>
    /// The event's position in this queue ticket's own history: 1 for <see cref="QueueingEventType.TicketCreated"/>, then strictly increasing for every later event of the
    /// same queue ticket. See the sequence rules in the contract document.
    /// </summary>
    public required long EventSequence { get; init; }

    /// <summary>When the event happened in the Queueing System, in UTC (ISO-8601, ending in Z). Not more than 5 minutes in the future. Preserved by TigerCS on everything it records.</summary>
    public required DateTime OccurredAtUtc { get; init; }

    /// <summary>The number shown to the customer (e.g. A-017). Required on TicketCreated. Max 100.</summary>
    public string? QueueNumber { get; init; }

    /// <summary>The Queueing System's company id. Required on TicketCreated. Mapped by TigerCS to a department. Max 100.</summary>
    public string? CompanyId { get; init; }

    /// <summary>The Queueing System's ticket type id. Required on TicketCreated. Mapped by TigerCS to a request type. Max 100.</summary>
    public string? TicketTypeId { get; init; }

    /// <summary>Optional. Max 200.</summary>
    public string? CustomerName { get; init; }

    /// <summary>The customer's mobile number. <b>Required on TicketCreated</b> (used for the CRM lookup). Must contain digits; international format (+971...) preferred. Max 32.</summary>
    public string? CustomerPhone { get; init; }

    /// <summary>Optional. Max 256.</summary>
    public string? CustomerEmail { get; init; }

    /// <summary>The Queueing System's id of the employee who now serves the ticket. Required on TicketAssigned and TicketReassigned. Max 100.</summary>
    public string? AssignedUserId { get; init; }

    /// <summary>The Queueing System's id of the employee who performed the action, when known. Optional. Recorded in the TigerCS history. Max 100.</summary>
    public string? PerformedByUserId { get; init; }

    /// <summary>The counter involved. Optional. Recorded in the TigerCS history. Max 100.</summary>
    public string? CounterId { get; init; }

    /// <summary>The comment text. Required on TicketCommentAdded. Max 4000.</summary>
    public string? Comment { get; init; }

    /// <summary>Why (reassigned, returned, recalled, closed, cancelled). Optional. Recorded as the history note. Max 1000.</summary>
    public string? Reason { get; init; }

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    /// <summary>The exact JSON that goes on the wire.</summary>
    public string ToJson() => JsonSerializer.Serialize(this with { OccurredAtUtc = DateTime.SpecifyKind(OccurredAtUtc, DateTimeKind.Utc) }, WireOptions);

    /// <summary>
    /// The members each event type needs beyond the five always-required ones (<c>EventId</c>, <c>EventType</c>, <c>ExternalTicketId</c>, <c>EventSequence</c>,
    /// <c>OccurredAtUtc</c>). The same table TigerCS validates against.
    /// </summary>
    public static IReadOnlyList<string> RequiredMembers(QueueingEventType type) => type switch
    {
        QueueingEventType.TicketCreated => ["QueueNumber", "CompanyId", "TicketTypeId", "CustomerPhone"],
        QueueingEventType.TicketAssigned or QueueingEventType.TicketReassigned => ["AssignedUserId"],
        QueueingEventType.TicketCommentAdded => ["Comment"],
        _ => []
    };

    /// <summary>Checks the event against the contract before it is ever sent. Returns the problems by member name; empty when valid. TigerCS answers 400 for the same problems.</summary>
    public IReadOnlyDictionary<string, string> Validate(DateTime? nowUtc = null)
    {
        var errors = new Dictionary<string, string>();

        void Text(string member, string? value, int max, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                {
                    errors[member] = $"{member} is required.";
                }
            }
            else if (value.Trim().Length > max)
            {
                errors[member] = $"{member} must be at most {max} characters.";
            }
        }

        Text(nameof(EventId), EventId, 100, true);
        Text(nameof(ExternalTicketId), ExternalTicketId, 100, true);
        if (!Enum.IsDefined(EventType))
        {
            errors[nameof(EventType)] = "EventType is not a supported event.";
        }

        if (EventSequence < 1)
        {
            errors[nameof(EventSequence)] = "EventSequence must be 1 or greater.";
        }

        if (OccurredAtUtc == default)
        {
            errors[nameof(OccurredAtUtc)] = "OccurredAtUtc is required.";
        }
        else if (OccurredAtUtc.Kind == DateTimeKind.Local)
        {
            errors[nameof(OccurredAtUtc)] = "OccurredAtUtc must be UTC.";
        }
        else if (OccurredAtUtc > (nowUtc ?? DateTime.UtcNow).AddMinutes(5))
        {
            errors[nameof(OccurredAtUtc)] = "OccurredAtUtc is in the future.";
        }

        var required = RequiredMembers(EventType);
        Text(nameof(QueueNumber), QueueNumber, 100, required.Contains(nameof(QueueNumber)));
        Text(nameof(CompanyId), CompanyId, 100, required.Contains(nameof(CompanyId)));
        Text(nameof(TicketTypeId), TicketTypeId, 100, required.Contains(nameof(TicketTypeId)));
        Text(nameof(CustomerName), CustomerName, 200, false);
        Text(nameof(CustomerPhone), CustomerPhone, 32, required.Contains(nameof(CustomerPhone)));
        if (!string.IsNullOrWhiteSpace(CustomerPhone) && !CustomerPhone.Any(char.IsAsciiDigit))
        {
            errors[nameof(CustomerPhone)] = "CustomerPhone must be a phone number.";
        }

        Text(nameof(CustomerEmail), CustomerEmail, 256, false);
        Text(nameof(AssignedUserId), AssignedUserId, 100, required.Contains(nameof(AssignedUserId)));
        Text(nameof(PerformedByUserId), PerformedByUserId, 100, false);
        Text(nameof(CounterId), CounterId, 100, false);
        Text(nameof(Comment), Comment, 4000, required.Contains(nameof(Comment)));
        Text(nameof(Reason), Reason, 1000, false);
        return errors;
    }
}
