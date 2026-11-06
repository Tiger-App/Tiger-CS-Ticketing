using System.Text.Json;
using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.Notifications.Dto;

/// <summary>
/// Payload of the <c>TicketResolved</c> / <c>TicketClosed</c> /
/// <c>TicketReopened</c> Outbox events. Identifiers only — the handler
/// re-reads the ticket, so nothing customer-facing is ever serialised into
/// <c>OutboxMessages.Payload</c>.
/// </summary>
/// <param name="TicketId">The ticket the event concerns.</param>
/// <param name="EventVersion">ADR-0014's event version component.</param>
/// <param name="ReopenCount">
/// The ticket's resolution cycle at the time of the event; mirrors the
/// <c>cycle</c> component of the idempotency key so a payload is
/// self-describing when read from the dead-letter queue.
/// </param>
/// <param name="ClosureKind">
/// For <c>TicketClosed</c> only: which rule closed the ticket when a system did (<see cref="ClosureKinds"/>), so the closed email can
/// explain the automatic closure. Null for a closure by a person and for every other event; absent from rows written before it existed.
/// </param>
public sealed record TicketLifecycleEventPayload(
    [property: JsonPropertyName("ticketId")] long TicketId,
    [property: JsonPropertyName("eventVersion")] int EventVersion,
    [property: JsonPropertyName("reopenCount")] int ReopenCount,
    [property: JsonPropertyName("closureKind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClosureKind = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TicketLifecycleEventPayload? FromJson(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<TicketLifecycleEventPayload>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The automatic closure rules a <c>TicketClosed</c> payload can name. Stable codes: they are serialised into Outbox rows.</summary>
public static class ClosureKinds
{
    /// <summary>The Genesys AI reported it finished the conversation (<c>closedByAi</c>).</summary>
    public const string AiCompletion = "AiCompletion";

    /// <summary>An AI-only ticket stayed open past the AI-only time limit (the 18-hour rule).</summary>
    public const string AiOnlyTimeout = "AiOnlyTimeout";

    /// <summary>The chatbot customer-inactivity rule.</summary>
    public const string CustomerInactivity = "CustomerInactivity";

    /// <summary>A document-delivery workflow ended.</summary>
    public const string DocumentDelivery = "DocumentDelivery";
}
