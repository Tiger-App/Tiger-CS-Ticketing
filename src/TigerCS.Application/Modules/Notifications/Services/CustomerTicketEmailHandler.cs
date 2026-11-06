using System.Globalization;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// The shared shape of every customer-facing ticket email consumed from the
/// Outbox: load the ticket, find-or-create the delivery row, resolve the
/// customer, render, send through <see cref="ICustomerEmailSender"/>, record
/// the outcome. Subclasses contribute only what differs — the event type,
/// the notification type, the template, and any extra guard or ticket
/// write-back.
///
/// <para>
/// <b>Idempotent, as ADR-0013 requires of every Outbox consumer.</b> The
/// dispatcher delivers at least once, so a handler can run twice for one
/// event. The <c>Notifications</c> row is found rather than recreated and a
/// terminal one (<c>Sent</c>, <c>Skipped</c>, <c>DeadLettered</c>) is never
/// acted on again; a unique index on <c>(OutboxMessageId, NotificationType)</c>
/// turns a genuine race into a constraint violation rather than two rows.
/// Subclasses may add a domain guard via <see cref="IsAlreadyDelivered"/>.
/// </para>
///
/// <para>
/// <b>Skipping is a first-class, successful outcome.</b> Disabled
/// notifications, a ticket with no usable email, or an event too old to
/// still be worth sending all end as <see cref="NotificationDeliveryStatus.Skipped"/>
/// with a <see cref="NotificationAuditActions.NotificationSkipped"/> audit
/// entry naming the reason, and the Outbox message is <c>Processed</c>. They
/// are expected, countable, and never inflate the dead-letter alerts.
/// </para>
///
/// <para>
/// <b>Never sends inside a transaction and never saves.</b> The dispatcher
/// commits once per message after this returns.
/// </para>
/// </summary>
public abstract class CustomerTicketEmailHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy) : IOutboxEventHandler
{
    public abstract string EventType { get; }

    protected abstract NotificationType NotificationType { get; }

    /// <summary>
    /// The notification type this specific occurrence records — normally <see cref="NotificationType"/>; a handler whose wording depends
    /// on the ticket (a Cancelled resolution under the <c>TicketResolved</c> event) overrides it so the delivery row and the audit trail
    /// name what was actually sent.
    /// </summary>
    protected virtual Task<NotificationType> ResolveNotificationTypeAsync(Ticket ticket, CancellationToken cancellationToken) =>
        Task.FromResult(NotificationType);

    /// <summary>
    /// Dispatch-time coalescing. Several transitions can happen within one dispatch window (Resolved then Closed; assigned then started;
    /// waiting then resumed). The customer should receive the email that describes where the ticket is now, not one per intermediate
    /// step. A handler returns a <see cref="CustomerEmailSkipReasons"/> code when the ticket has already moved past the state its event
    /// announces and a later event carries that state; the notification is then recorded as Skipped with that reason. Default: never.
    /// </summary>
    protected virtual string? SupersededReason(Ticket ticket, OutboxMessage message) => null;

    /// <summary>Extracts the ticket identifier from the message payload; <c>null</c> when the payload cannot be parsed.</summary>
    protected abstract long? ParseTicketId(string payload);

    /// <summary>Renders the email for this ticket and contact. Runs only once a deliverable recipient is known.</summary>
    protected abstract Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, DateTime occurredAtUtc, CancellationToken cancellationToken);

    /// <summary>A domain-level "already done" guard evaluated before anything else is touched. Default: none.</summary>
    protected virtual bool IsAlreadyDelivered(Ticket ticket) => false;

    /// <summary>Ticket write-back after a successful send (e.g. the acknowledgement timestamp). Default: none.</summary>
    protected virtual void OnSent(Ticket ticket, DateTime sentAtUtc)
    {
    }

    protected CustomerNotificationPolicy Policy => policy;

    protected CustomerNotificationModePolicy ModePolicy => modePolicy;

    public async Task<OutboxHandlingResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var ticketId = ParseTicketId(message.Payload);
        if (ticketId is null || ticketId <= 0)
        {
            return OutboxHandlingResult.Permanent($"{EventType} payload could not be parsed.");
        }

        var ticket = await ticketRepository.GetByIdAsync(ticketId.Value, cancellationToken);
        if (ticket is null)
        {
            // Nothing deletes a ticket, so a missing one is a permanent
            // inconsistency to surface, not a race to wait out.
            return OutboxHandlingResult.Permanent($"Ticket {ticketId} no longer exists.");
        }

        if (IsAlreadyDelivered(ticket))
        {
            return OutboxHandlingResult.Succeeded();
        }

        var notificationType = await ResolveNotificationTypeAsync(ticket, cancellationToken);
        var notification = await notificationRepository.GetByOutboxMessageAsync(
            message.OutboxMessageId, notificationType, cancellationToken);

        if (notification is { IsTerminal: true })
        {
            return notification.DeliveryStatus == NotificationDeliveryStatus.DeadLettered
                ? OutboxHandlingResult.Permanent("The notification for this message was already dead-lettered.")
                : OutboxHandlingResult.Succeeded();
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var contact = await contactResolver.ResolveAsync(ticket, cancellationToken);

        // CustomerNotifications:IsTestMode: in test mode every customer email goes to TestEmail (skipped if there is none); otherwise to the
        // customer. The sender and the delivery guard apply the same rule; deciding here lets the audit trail say why nothing was sent.
        var decision = modePolicy.DecideEmail();

        if (notification is null)
        {
            notification = new Notification(
                ticket.TicketId,
                notificationType,
                // Always null: customer notifications go to the external
                // requester, never to a staff mailbox.
                recipientEmployeeId: null,
                decision.Recipient ?? contact.EmailAddress,
                NotificationChannel.Email,
                message.CorrelationId,
                message.OutboxMessageId,
                nowUtc);

            await notificationRepository.AddAsync(notification, cancellationToken);
        }

        if (SupersededReason(ticket, message) is { } superseded)
        {
            // Checked before anything provider-related: a superseded event is skipped whatever the configuration, so the reason is the
            // informative one ("the ticket had moved on"), not "notifications disabled".
            return await SkipAsync(ticket, notification, message, superseded, cancellationToken);
        }

        if (!policy.Enabled)
        {
            return await SkipAsync(ticket, notification, message, CustomerEmailSkipReasons.NotificationsDisabled, cancellationToken);
        }

        if (!decision.Deliver)
        {
            return await SkipAsync(
                ticket, notification, message, decision.SuppressReason ?? CustomerEmailSkipReasons.TestEmailMissing, cancellationToken);
        }

        if (policy.MaxEventAge > TimeSpan.Zero && nowUtc - message.OccurredAtUtc > policy.MaxEventAge)
        {
            return await SkipAsync(ticket, notification, message, CustomerEmailSkipReasons.EventTooOld, cancellationToken);
        }

        // In Test mode the customer's own address is irrelevant (the test mailbox receives the message), so a phone-only or
        // unverified ticket can still be tested.
        if (!contact.IsDeliverable && decision.Recipient is null)
        {
            return await SkipAsync(
                ticket, notification, message, contact.SkipReason ?? CustomerEmailSkipReasons.NoCustomerEmail, cancellationToken);
        }

        var content = await BuildContentAsync(ticket, contact, message.OccurredAtUtc, cancellationToken);

        var result = await emailSender.SendAsync(
            new CustomerEmailRequest(
                notificationType, ticket.TicketId, ticket.TicketNumber, decision.Recipient ?? contact.EmailAddress, content, message.CorrelationId),
            cancellationToken);

        return result.Outcome switch
        {
            CustomerEmailSendOutcome.Sent => await RecordSentAsync(ticket, notification, message, nowUtc, cancellationToken),
            CustomerEmailSendOutcome.Skipped => await SkipAsync(
                ticket, notification, message, result.Reason ?? CustomerEmailSkipReasons.NoCustomerEmail, cancellationToken),
            CustomerEmailSendOutcome.PermanentFailure => await RecordPermanentFailureAsync(
                ticket, notification, message, result.Reason, cancellationToken),
            _ => await RecordTransientFailureAsync(ticket, notification, message, result.Reason, cancellationToken)
        };
    }

    private async Task<OutboxHandlingResult> RecordSentAsync(
        Ticket ticket, Notification notification, OutboxMessage message, DateTime nowUtc, CancellationToken cancellationToken)
    {
        OnSent(ticket, nowUtc);
        notification.MarkSent();

        await WriteAuditAsync(
            ticket,
            NotificationAuditActions.NotificationDeliverySucceeded,
            beforeValue: "DeliveryStatus=Pending",
            afterValue: $"DeliveryStatus=Sent;NotificationType={notification.NotificationType};Channel=Email;OutboxMessageId={message.OutboxMessageId}",
            message,
            cancellationToken);

        return OutboxHandlingResult.Succeeded();
    }

    private async Task<OutboxHandlingResult> SkipAsync(
        Ticket ticket, Notification notification, OutboxMessage message, string reason, CancellationToken cancellationToken)
    {
        notification.MarkSkipped();

        await WriteAuditAsync(
            ticket,
            NotificationAuditActions.NotificationSkipped,
            beforeValue: "DeliveryStatus=Pending",
            afterValue: $"DeliveryStatus=Skipped;NotificationType={notification.NotificationType};Reason={reason};OutboxMessageId={message.OutboxMessageId}",
            message,
            cancellationToken);

        // Processed, not dead-lettered: there is nothing a retry could change.
        return OutboxHandlingResult.Succeeded();
    }

    private async Task<OutboxHandlingResult> RecordTransientFailureAsync(
        Ticket ticket, Notification notification, OutboxMessage message, string? error, CancellationToken cancellationToken)
    {
        notification.RecordFailedAttempt();

        await WriteAuditAsync(
            ticket,
            NotificationAuditActions.NotificationDeliveryFailed,
            beforeValue: null,
            afterValue: $"DeliveryStatus=Failed;NotificationType={notification.NotificationType};RetryCount={notification.RetryCount};OutboxMessageId={message.OutboxMessageId}",
            message,
            cancellationToken);

        return OutboxHandlingResult.Transient(error ?? "The email provider reported a transient failure.");
    }

    private async Task<OutboxHandlingResult> RecordPermanentFailureAsync(
        Ticket ticket, Notification notification, OutboxMessage message, string? error, CancellationToken cancellationToken)
    {
        notification.RecordFailedAttempt();
        notification.DeadLetter();

        await WriteAuditAsync(
            ticket,
            NotificationAuditActions.NotificationDeadLettered,
            beforeValue: "DeliveryStatus=Failed",
            afterValue: $"DeliveryStatus=DeadLettered;NotificationType={notification.NotificationType};Reason=PermanentProviderFailure;OutboxMessageId={message.OutboxMessageId}",
            message,
            cancellationToken);

        return OutboxHandlingResult.Permanent(error ?? "The email provider reported a permanent failure.");
    }

    private Task WriteAuditAsync(
        Ticket ticket, string action, string? beforeValue, string afterValue, OutboxMessage message, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            actorEmployeeId: null,
            action,
            NotificationAuditActions.NotificationEntityType,
            ticket.TicketId.ToString(CultureInfo.InvariantCulture),
            beforeValue,
            afterValue,
            message.CorrelationId,
            cancellationToken);
}
