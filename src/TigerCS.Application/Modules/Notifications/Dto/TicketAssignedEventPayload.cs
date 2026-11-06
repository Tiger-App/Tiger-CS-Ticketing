using System.Text.Json;
using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.Notifications.Dto;

/// <summary>
/// Payload of the <c>TicketAssigned</c> (customer) and <c>TicketAssignedToAgent</c> (internal) Outbox events. Identifiers only — the
/// handlers re-read the ticket, the customer and the staff account at dispatch time, so nothing customer- or staff-facing is serialised
/// into <c>OutboxMessages.Payload</c>.
/// </summary>
/// <param name="TicketId">The ticket the event concerns.</param>
/// <param name="EventVersion">ADR-0014's event version component.</param>
/// <param name="ReopenCount">The resolution cycle the assignment happened in.</param>
/// <param name="AssignedEmployeeId">Who the ticket was assigned to.</param>
/// <param name="PreviousOwnerEmployeeId">Who owned it before, or <c>null</c> for a ticket taken from the department queue.</param>
/// <param name="ActorEmployeeId">Who assigned it; <c>null</c> for an automatic (system) assignment.</param>
/// <param name="TicketStatus">The ticket's status right after the assignment — the state the customer email describes.</param>
/// <param name="DepartmentId">The department the ticket was in when assigned.</param>
public sealed record TicketAssignedEventPayload(
    [property: JsonPropertyName("ticketId")] long TicketId,
    [property: JsonPropertyName("eventVersion")] int EventVersion,
    [property: JsonPropertyName("reopenCount")] int ReopenCount,
    [property: JsonPropertyName("assignedEmployeeId")] Guid AssignedEmployeeId,
    [property: JsonPropertyName("previousOwnerEmployeeId")] Guid? PreviousOwnerEmployeeId,
    [property: JsonPropertyName("actorEmployeeId")] Guid? ActorEmployeeId,
    [property: JsonPropertyName("ticketStatus")] byte TicketStatus,
    [property: JsonPropertyName("departmentId")] int DepartmentId)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TicketAssignedEventPayload? FromJson(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<TicketAssignedEventPayload>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
