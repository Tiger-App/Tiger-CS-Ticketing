namespace TigerCS.Domain.Modules.QueueingIntegration;

/// <summary>The Queueing System's view of where a queue ticket is. Mirrors the queue's own lifecycle; the TigerCS status is derived from it, never the other way round.</summary>
public enum QueueingTicketState : byte
{
    /// <summary>Waiting in the queue: no employee is serving it (newly created, or returned to the waiting queue).</summary>
    Waiting = 1,

    /// <summary>Called/assigned to an employee at a counter.</summary>
    Assigned = 2,

    Closed = 3,
    Cancelled = 4
}

/// <summary>
/// The queue-specific state of a queue-originated TigerCS ticket (one row per ticket). The ticket's identity - its external id, queue number and
/// source - lives on the <see cref="TigerCS.Domain.Modules.Ticketing.Ticket"/> itself, written in the same transaction as the ticket; this row holds what only
/// the integration needs: how far the event stream has been applied. <see cref="LastAppliedSequence"/> is the ordering guard - an event whose
/// sequence is not greater than it can never overwrite the ticket - and <see cref="RowVersion"/> turns two concurrent applications of events for the
/// same ticket into one winner and one retry.
/// </summary>
public class QueueingTicketLink
{
    public const int ExternalIdMaxLength = 100;
    public const int ExternalTicketTypeNameMaxLength = 200;

    public long QueueingTicketLinkId { get; private set; }
    public long TicketId { get; private set; }
    public string ExternalTicketId { get; private set; } = string.Empty;
    public string? QueueNumber { get; private set; }
    public string? CompanyId { get; private set; }
    public string? ExternalTicketTypeId { get; private set; }

    /// <summary>
    /// The Queueing System's own name for the service (ticket type), exactly as the Queueing System sent it on <c>TicketCreated</c>. Display and audit only:
    /// it never routes anything. Null for a ticket whose creating event carried no name (every ticket created before the field existed).
    /// </summary>
    public string? ExternalTicketTypeName { get; private set; }

    /// <summary>The highest <c>EventSequence</c> of a state-changing event applied to the ticket.</summary>
    public long LastAppliedSequence { get; private set; }

    /// <summary>The original (Queueing System) time of the last applied state-changing event.</summary>
    public DateTime LastAppliedOccurredAtUtc { get; private set; }

    public QueueingTicketState State { get; private set; }
    public string? CounterId { get; private set; }

    /// <summary>The Queueing System's id of the employee currently serving the ticket, when it is assigned.</summary>
    public string? AssignedExternalUserId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private QueueingTicketLink() { }

    public QueueingTicketLink(
        long ticketId, string externalTicketId, string? queueNumber, string? companyId, string? externalTicketTypeId,
        long sequence, DateTime occurredAtUtc, DateTime createdAtUtc, string? externalTicketTypeName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTicketId);
        TicketId = ticketId;
        ExternalTicketId = externalTicketId.Trim();
        QueueNumber = queueNumber;
        CompanyId = companyId;
        ExternalTicketTypeId = externalTicketTypeId;
        ExternalTicketTypeName = string.IsNullOrWhiteSpace(externalTicketTypeName) ? null : externalTicketTypeName.Trim();
        LastAppliedSequence = sequence;
        LastAppliedOccurredAtUtc = occurredAtUtc;
        State = QueueingTicketState.Waiting;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>True when a state-changing event with this sequence is older than (or the same as) what was already applied.</summary>
    public bool IsStale(long sequence) => sequence <= LastAppliedSequence;

    public bool IsTerminal => State is QueueingTicketState.Closed or QueueingTicketState.Cancelled;

    /// <summary>The ticket was reopened by the Queueing System: waiting again. The applied sequence is deliberately unchanged - the assignment that
    /// caused the reopen is applied next and advances it, so a retry of that assignment is never mistaken for a stale event.</summary>
    public void ReopenToWaiting()
    {
        State = QueueingTicketState.Waiting;
        AssignedExternalUserId = null;
    }

    public void Apply(QueueingTicketState state, long sequence, DateTime occurredAtUtc, string? counterId, string? assignedExternalUserId)
    {
        State = state;
        LastAppliedSequence = sequence;
        // OccurredAtUtc is not an ordering authority (a reconciled event may carry an earlier time than one already applied): the sequence orders, and this only ever moves forward,
        // so it is the floor for the time the ticket's own history is stamped with.
        LastAppliedOccurredAtUtc = occurredAtUtc > LastAppliedOccurredAtUtc ? occurredAtUtc : LastAppliedOccurredAtUtc;
        CounterId = counterId;
        AssignedExternalUserId = state == QueueingTicketState.Assigned ? assignedExternalUserId : null;
    }

    public void ReconcileCreation(long sequence, DateTime occurredAtUtc)
    {
        if (sequence > LastAppliedSequence)
        {
            LastAppliedSequence = sequence;
            LastAppliedOccurredAtUtc = occurredAtUtc;
        }
    }
}
