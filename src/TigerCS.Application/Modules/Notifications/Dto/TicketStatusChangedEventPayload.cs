using System.Text.Json;
using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.Notifications.Dto;

/// <summary>
/// Payload of the <c>TicketInProgress</c> and <c>TicketPendingCustomer</c> Outbox events (a Change Status operation the customer is told
/// about). Identifiers and the transition only; the handler re-reads the ticket and, for a pending status, its open pending record.
/// </summary>
/// <param name="TicketId">The ticket the event concerns.</param>
/// <param name="EventVersion">ADR-0014's event version component.</param>
/// <param name="ReopenCount">The resolution cycle the transition happened in.</param>
/// <param name="PreviousStatus">The <c>TicketStatus</c> value the ticket left.</param>
/// <param name="NewStatus">The <c>TicketStatus</c> value the ticket entered — the state the email describes.</param>
public sealed record TicketStatusChangedEventPayload(
    [property: JsonPropertyName("ticketId")] long TicketId,
    [property: JsonPropertyName("eventVersion")] int EventVersion,
    [property: JsonPropertyName("reopenCount")] int ReopenCount,
    [property: JsonPropertyName("previousStatus")] byte PreviousStatus,
    [property: JsonPropertyName("newStatus")] byte NewStatus)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TicketStatusChangedEventPayload? FromJson(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<TicketStatusChangedEventPayload>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
