namespace TigerCS.Domain.Modules.QueueingIntegration;

/// <summary>The events the Queueing System sends. Stable wire names (the enum member names) - they are stored on <see cref="QueueingEvent"/>.</summary>
public enum QueueingEventType : byte
{
    TicketCreated = 1,
    TicketAssigned = 2,
    TicketReassigned = 3,
    TicketReturned = 4,
    TicketCommentAdded = 5,
    TicketRecalled = 6,
    TicketClosed = 7,
    TicketCancelled = 8
}

public static class QueueingEventTypes
{
    /// <summary>Events that move the queue ticket between states and therefore take part in sequence ordering ("the newest state wins").</summary>
    public static bool IsStateChange(QueueingEventType type) =>
        type is QueueingEventType.TicketCreated or QueueingEventType.TicketAssigned or QueueingEventType.TicketReassigned
            or QueueingEventType.TicketReturned or QueueingEventType.TicketClosed or QueueingEventType.TicketCancelled;

    /// <summary>Events that only add a record (a comment, a recall). They never overwrite state, so a late one is still recorded.</summary>
    public static bool IsAdditive(QueueingEventType type) => !IsStateChange(type);

    public static bool IsTerminal(QueueingEventType type) => type is QueueingEventType.TicketClosed or QueueingEventType.TicketCancelled;
}
