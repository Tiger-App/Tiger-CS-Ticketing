using System.Text.Json.Serialization;

namespace QueueingSystem.TigerCsSender;

/// <summary>
/// The eight events the Queueing System sends to TigerCS. <b>Serialised by name</b> (the wire value of <c>EventType</c> is exactly the member name, e.g.
/// <c>"TicketAssigned"</c>) - never by number.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueueingEventType
{
    /// <summary>A customer took a queue ticket (QR code). TigerCS creates one unassigned ticket.</summary>
    TicketCreated = 1,

    /// <summary>The queue ticket was called by an employee at a counter. TigerCS assigns it to that employee.</summary>
    TicketAssigned = 2,

    /// <summary>The queue ticket moved to another employee. TigerCS reassigns it.</summary>
    TicketReassigned = 3,

    /// <summary>The employee returned the queue ticket to the waiting queue. TigerCS releases the owner and the ticket goes back to Open.</summary>
    TicketReturned = 4,

    /// <summary>A comment was written on the queue ticket. TigerCS adds a ticket note.</summary>
    TicketCommentAdded = 5,

    /// <summary>The customer was called again. TigerCS records it in the ticket history.</summary>
    TicketRecalled = 6,

    /// <summary>The customer was served. TigerCS closes the ticket as Resolved.</summary>
    TicketClosed = 7,

    /// <summary>The queue ticket was cancelled. TigerCS closes the ticket as Cancelled.</summary>
    TicketCancelled = 8
}
