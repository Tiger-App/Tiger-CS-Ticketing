namespace TigerCS.Domain.Modules.Notifications;

/// <summary>
/// MVP-Data-Dictionary.md §2.21 / MVP-ERD.md §2.21 — one row per outbound
/// notification, owned by the Notifications module (Module-Design.md).
///
/// <para>
/// <b>Distinct from the Outbox row that carries it.</b> The
/// <c>OutboxMessages</c> row (§2.23) is the transactional record that <i>an
/// effect is owed</i>, written atomically with the business state change
/// (ADR-0013). This row is the record of <i>a delivery to one recipient on
/// one channel</i> — its status, its address, its retry count. One Outbox
/// message could fan out to several of these; MVP fans out to exactly one,
/// since the only MVP channel is Email (§8 of MVP-API-Contracts.md, and
/// §2.21's own "MVP: Email only").
/// </para>
///
/// <para>
/// <b>No message body is stored here, and none is logged.</b> §2.21 defines
/// no body/subject column, and Security-Architecture.md §11 requires contact
/// details be masked or omitted from log output. The rendered
/// acknowledgement exists only in the value handed to <c>IEmailSender</c>;
/// what survives is the delivery <i>fact</i>, which is what FR-NOT-05 asks
/// be logged and correlation-tracked.
/// </para>
/// </summary>
public class Notification
{
    public long NotificationId { get; private set; }

    /// <summary>
    /// Nullable per §2.21 — "Null only for a non-ticket-specific notice
    /// [ASSUMPTION — at MVP, every notification is ticket-scoped; nullable
    /// kept for forward compatibility]". Every row this increment writes
    /// carries a ticket.
    /// </summary>
    public long? TicketId { get; private set; }

    public NotificationType NotificationType { get; private set; }

    /// <summary>Null for an external recipient — the customer, via email (§2.21). Always null in this increment: the acknowledgement goes to the requester, never to staff.</summary>
    public Guid? RecipientEmployeeId { get; private set; }

    /// <summary>
    /// Populated for external/email recipients (§2.21). <b>Null when no
    /// deliverable address could be resolved</b> from the verified requester
    /// snapshot — which is a recorded outcome, not a silently dropped
    /// notification: the row still exists, dead-lettered and visible.
    /// </summary>
    public string? RecipientAddress { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public NotificationDeliveryStatus DeliveryStatus { get; private set; }

    public Guid CorrelationId { get; private set; }

    /// <summary>
    /// FK → <c>OutboxMessages</c>, nullable per §2.21 / MVP-ERD.md §2.21
    /// ("a <c>Pending</c> row created before dispatch may briefly have no
    /// Outbox link yet"). Always populated in this increment, because the
    /// row is created by the dispatcher from an Outbox message that already
    /// exists.
    /// </summary>
    public Guid? OutboxMessageId { get; private set; }

    public int RetryCount { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private Notification() { }

    public Notification(
        long? ticketId,
        NotificationType notificationType,
        Guid? recipientEmployeeId,
        string? recipientAddress,
        NotificationChannel channel,
        Guid correlationId,
        Guid? outboxMessageId,
        DateTime createdAtUtc)
    {
        TicketId = ticketId;
        NotificationType = notificationType;
        RecipientEmployeeId = recipientEmployeeId;
        RecipientAddress = recipientAddress;
        Channel = channel;
        DeliveryStatus = NotificationDeliveryStatus.Pending;
        CorrelationId = correlationId;
        OutboxMessageId = outboxMessageId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Terminal success.
    ///
    /// <para>
    /// Refuses to run twice. Together with <c>Ticket.RecordAcknowledgementSent</c>'s
    /// own write-once rule this is the delivery-side half of "a duplicate
    /// dispatch never sends twice": even if a redelivered Outbox message
    /// reaches the handler, the already-<see cref="NotificationDeliveryStatus.Sent"/> row cannot be sent
    /// again.
    /// </para>
    /// </summary>
    public void MarkSent()
    {
        if (DeliveryStatus == NotificationDeliveryStatus.Sent)
        {
            throw new InvalidOperationException($"Notification {NotificationId} has already been sent.");
        }

        DeliveryStatus = NotificationDeliveryStatus.Sent;
    }

    /// <summary>A failed attempt that will be retried — the row stays visible with its retry bookkeeping (FR-NOT-05).</summary>
    public void RecordFailedAttempt()
    {
        DeliveryStatus = NotificationDeliveryStatus.Failed;
        RetryCount++;
    }

    /// <summary>Retries exhausted, or a failure no retry could fix. Kept, never deleted (FR-NOT-05's dead-letter path).</summary>
    public void DeadLetter()
    {
        DeliveryStatus = NotificationDeliveryStatus.DeadLettered;
    }

    /// <summary>
    /// Terminal "not sent on purpose" — see <see cref="NotificationDeliveryStatus.Skipped"/>.
    /// Refuses to overwrite a row that was already delivered.
    /// </summary>
    public void MarkSkipped()
    {
        if (DeliveryStatus == NotificationDeliveryStatus.Sent)
        {
            throw new InvalidOperationException($"Notification {NotificationId} has already been sent and cannot be skipped.");
        }

        DeliveryStatus = NotificationDeliveryStatus.Skipped;
    }

    /// <summary>True once the delivery reached a state no further attempt may change.</summary>
    public bool IsTerminal =>
        DeliveryStatus is NotificationDeliveryStatus.Sent
            or NotificationDeliveryStatus.DeadLettered
            or NotificationDeliveryStatus.Skipped;
}

/// <summary>MVP-Data-Dictionary.md §2.21: "Acknowledgement/Warning/Breach/Escalation". Only <see cref="Acknowledgement"/> is produced by this increment.</summary>
public enum NotificationType : byte
{
    Acknowledgement = 1,
    Warning = 2,
    Breach = 3,
    Escalation = 4,

    /// <summary>Customer Email Notifications increment — the customer-facing "your request has been resolved" email.</summary>
    Resolved = 5,

    /// <summary>Customer Email Notifications increment — the customer-facing "your request has been closed" email.</summary>
    Closed = 6,

    /// <summary>Customer Email Notifications increment — the customer-facing "your request has been reopened" email.</summary>
    Reopened = 7,

    /// <summary>Customer email: the ticket gained an owner (first assignment of the current cycle).</summary>
    Assigned = 8,

    /// <summary>Customer email: work on the ticket has started / resumed (status <c>InProgress</c>).</summary>
    InProgress = 9,

    /// <summary>Customer email: the ticket is waiting for the customer (status <c>PendingCustomer</c>).</summary>
    PendingCustomer = 10,

    /// <summary>Customer email: the request was cancelled (a resolution with the <c>Cancelled</c> outcome).</summary>
    Cancelled = 11,

    /// <summary>Internal email to a staff member: a ticket was assigned or transferred to them.</summary>
    AgentAssigned = 12,

    /// <summary>Internal email to the ticket's owner: the customer replied on the conversation.</summary>
    CustomerReplyReceived = 13
}

/// <summary>MVP-Data-Dictionary.md §2.21: "MVP: Email only". SMS/WhatsApp/push are Phase 2 (Solution-Analysis.md §2.7) and deliberately have no value here.</summary>
public enum NotificationChannel : byte
{
    Email = 1
}

/// <summary>MVP-Data-Dictionary.md §2.21: "Pending/Sent/Failed/DeadLettered".</summary>
public enum NotificationDeliveryStatus : byte
{
    Pending = 1,
    Sent = 2,
    Failed = 3,
    DeadLettered = 4,

    /// <summary>
    /// Customer Email Notifications increment — deliberately not sent, and
    /// never will be: no usable customer email address exists for the
    /// ticket, customer email notifications are disabled by configuration,
    /// or the event was too old to still be worth emailing. Terminal, like
    /// <see cref="Sent"/>; distinct from <see cref="DeadLettered"/> so that
    /// an expected outcome (most phone-only tickets have no email) never
    /// inflates the dead-letter count operators are alerted on.
    /// </summary>
    Skipped = 5
}
