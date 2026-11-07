using System.Text.Json;
using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.Notifications.Dto;

/// <summary>
/// Payload of the internal <c>CustomerReplyReceived</c> Outbox event: a Genesys transcript delivery appended one or more customer
/// messages to a conversation of a ticket a person is handling. Positions and counts only — never the message text.
/// </summary>
/// <param name="TicketId">The ticket the conversation belongs to.</param>
/// <param name="EventVersion">ADR-0014's event version component.</param>
/// <param name="TicketInteractionId">The conversation (interaction) the messages were appended to.</param>
/// <param name="AssignedEmployeeId">The ticket's owner at the time — the person to tell.</param>
/// <param name="MessageCount">How many customer messages this delivery appended.</param>
/// <param name="LatestSequence">The transcript position of the last appended customer message (also the idempotency occurrence).</param>
/// <param name="LatestSentAtUtc">When the customer sent the last appended message.</param>
public sealed record CustomerReplyEventPayload(
    [property: JsonPropertyName("ticketId")] long TicketId,
    [property: JsonPropertyName("eventVersion")] int EventVersion,
    [property: JsonPropertyName("ticketInteractionId")] long TicketInteractionId,
    [property: JsonPropertyName("assignedEmployeeId")] Guid AssignedEmployeeId,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("latestSequence")] int LatestSequence,
    [property: JsonPropertyName("latestSentAtUtc")] DateTime LatestSentAtUtc)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static CustomerReplyEventPayload? FromJson(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CustomerReplyEventPayload>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
