using System.Globalization;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications.Templates;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// The staff-facing counterpart of <see cref="CustomerTicketEmailHandler"/>: load the ticket, find-or-create the delivery row (with
/// <c>RecipientEmployeeId</c> set), resolve the staff member's account email, render, send through <see cref="IEmailSender"/> as
/// <see cref="EmailAudience.Internal"/> — so the customer test-mode redirect never touches it — and record the outcome.
///
/// <para>
/// Same guarantees as the customer handler: idempotent (one delivery row per message and type, terminal rows never acted on again),
/// skipping is a successful outcome (<c>NoAgentEmail</c>, <c>AgentNotFound</c>, disabled, superseded), nothing is sent inside a
/// transaction and the dispatcher commits. The <c>EmailNotifications:Enabled</c> master switch applies, since the same SMTP account
/// delivers staff mail. No customer contact detail is ever rendered, logged or audited.
/// </para>
/// </summary>
public abstract class InternalTicketEmailHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    IEmailSender emailSender,
    IUserAccountManager accountManager,
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    CustomerContactResolver contactResolver,
    CustomerTicketSubjectResolver subjectResolver,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    CustomerNotificationModePolicy modePolicy,
    TimeProvider timeProvider) : IOutboxEventHandler
{
    public abstract string EventType { get; }

    protected abstract NotificationType NotificationType { get; }

    protected abstract long? ParseTicketId(string payload);

    /// <summary>The staff member to tell, from the payload; null when the payload cannot be parsed.</summary>
    protected abstract Guid? ParseRecipient(string payload);

    /// <summary>A <see cref="CustomerEmailSkipReasons"/> code when the event is no longer worth sending; default: never.</summary>
    protected virtual string? SupersededReason(Ticket ticket, OutboxMessage message) => null;

    protected abstract Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, InternalTicketEmailModel model, OutboxMessage message, CancellationToken cancellationToken);

    public async Task<OutboxHandlingResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var ticketId = ParseTicketId(message.Payload);
        var recipientId = ParseRecipient(message.Payload);
        if (ticketId is null || ticketId <= 0 || recipientId is null || recipientId == Guid.Empty)
        {
            return OutboxHandlingResult.Permanent($"{EventType} payload could not be parsed.");
        }

        var ticket = await ticketRepository.GetByIdAsync(ticketId.Value, cancellationToken);
        if (ticket is null)
        {
            return OutboxHandlingResult.Permanent($"Ticket {ticketId} no longer exists.");
        }

        var notification = await notificationRepository.GetByOutboxMessageAsync(message.OutboxMessageId, NotificationType, cancellationToken);
        if (notification is { IsTerminal: true })
        {
            return notification.DeliveryStatus == NotificationDeliveryStatus.DeadLettered
                ? OutboxHandlingResult.Permanent("The notification for this message was already dead-lettered.")
                : OutboxHandlingResult.Succeeded();
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var account = await accountManager.GetAccountAsync(recipientId.Value, cancellationToken);
        var recipient = CustomerEmailAddress.Normalize(account?.Email);

        if (notification is null)
        {
            notification = new Notification(
                ticket.TicketId, NotificationType, recipientId, recipient, NotificationChannel.Email, message.CorrelationId,
                message.OutboxMessageId, nowUtc);
            await notificationRepository.AddAsync(notification, cancellationToken);
        }

        if (SupersededReason(ticket, message) is { } superseded)
        {
            return await SkipAsync(ticket, notification, message, superseded, cancellationToken);
        }

        if (!policy.Enabled)
        {
            return await SkipAsync(ticket, notification, message, CustomerEmailSkipReasons.NotificationsDisabled, cancellationToken);
        }

        if (policy.MaxEventAge > TimeSpan.Zero && nowUtc - message.OccurredAtUtc > policy.MaxEventAge)
        {
            return await SkipAsync(ticket, notification, message, CustomerEmailSkipReasons.EventTooOld, cancellationToken);
        }

        if (account is null)
        {
            return await SkipAsync(ticket, notification, message, CustomerEmailSkipReasons.AgentNotFound, cancellationToken);
        }

        if (recipient is null || !CustomerEmailAddress.IsValid(recipient))
        {
            return await SkipAsync(ticket, notification, message, CustomerEmailSkipReasons.NoAgentEmail, cancellationToken);
        }

        var contact = await contactResolver.ResolveAsync(ticket, cancellationToken);
        var model = new InternalTicketEmailModel(
            ticket.TicketNumber,
            RecipientName: (await employeeRepository.GetByIdAsync(recipientId.Value, cancellationToken))?.DisplayName ?? account.UserName,
            TicketStatus: ticket.TicketStatus.ToString(),
            OccurredAtUtc: message.OccurredAtUtc,
            TicketSubject: await subjectResolver.ResolveAsync(ticket, cancellationToken),
            CustomerName: contact.DisplayName,
            DepartmentName: (await departmentRepository.GetByIdAsync(ticket.CurrentDepartmentId, cancellationToken))?.Name,
            TicketUrl: modePolicy.PublicBaseUrl is { } baseUrl
                ? $"{baseUrl}/Tickets/{ticket.TicketId.ToString(CultureInfo.InvariantCulture)}"
                : null);

        var content = await BuildContentAsync(ticket, model, message, cancellationToken);

        EmailSendResult sent;
        try
        {
            sent = await emailSender.SendAsync(
                new EmailMessage(recipient, content.Subject, content.TextBody, message.CorrelationId, content.HtmlBody, Audience: EmailAudience.Internal),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sent = EmailSendResult.Transient($"Email provider threw {ex.GetType().Name}.");
        }

        return sent.Outcome switch
        {
            EmailSendOutcome.Sent => await RecordSentAsync(ticket, notification, message, cancellationToken),
            EmailSendOutcome.Suppressed => await SkipAsync(ticket, notification, message, sent.Error ?? CustomerEmailSkipReasons.NotificationsDisabled, cancellationToken),
            EmailSendOutcome.PermanentFailure => await RecordPermanentFailureAsync(ticket, notification, message, sent.Error, cancellationToken),
            _ => await RecordTransientFailureAsync(ticket, notification, message, sent.Error, cancellationToken)
        };
    }

    /// <summary>The display name of an employee, for the email body; null when unknown.</summary>
    protected async Task<string?> EmployeeNameAsync(Guid? employeeId, CancellationToken cancellationToken) =>
        employeeId is { } id ? (await employeeRepository.GetByIdAsync(id, cancellationToken))?.DisplayName : null;

    private async Task<OutboxHandlingResult> RecordSentAsync(Ticket ticket, Notification notification, OutboxMessage message, CancellationToken cancellationToken)
    {
        notification.MarkSent();
        await WriteAuditAsync(
            ticket, NotificationAuditActions.NotificationDeliverySucceeded, "DeliveryStatus=Pending",
            $"DeliveryStatus=Sent;NotificationType={NotificationType};Channel=Email;Audience=Internal;RecipientEmployeeId={notification.RecipientEmployeeId};OutboxMessageId={message.OutboxMessageId}",
            message, cancellationToken);
        return OutboxHandlingResult.Succeeded();
    }

    private async Task<OutboxHandlingResult> SkipAsync(Ticket ticket, Notification notification, OutboxMessage message, string reason, CancellationToken cancellationToken)
    {
        notification.MarkSkipped();
        await WriteAuditAsync(
            ticket, NotificationAuditActions.NotificationSkipped, "DeliveryStatus=Pending",
            $"DeliveryStatus=Skipped;NotificationType={NotificationType};Reason={reason};Audience=Internal;RecipientEmployeeId={notification.RecipientEmployeeId};OutboxMessageId={message.OutboxMessageId}",
            message, cancellationToken);
        return OutboxHandlingResult.Succeeded();
    }

    private async Task<OutboxHandlingResult> RecordTransientFailureAsync(Ticket ticket, Notification notification, OutboxMessage message, string? error, CancellationToken cancellationToken)
    {
        notification.RecordFailedAttempt();
        await WriteAuditAsync(
            ticket, NotificationAuditActions.NotificationDeliveryFailed, null,
            $"DeliveryStatus=Failed;NotificationType={NotificationType};RetryCount={notification.RetryCount};Audience=Internal;OutboxMessageId={message.OutboxMessageId}",
            message, cancellationToken);
        return OutboxHandlingResult.Transient(error ?? "The email provider reported a transient failure.");
    }

    private async Task<OutboxHandlingResult> RecordPermanentFailureAsync(Ticket ticket, Notification notification, OutboxMessage message, string? error, CancellationToken cancellationToken)
    {
        notification.RecordFailedAttempt();
        notification.DeadLetter();
        await WriteAuditAsync(
            ticket, NotificationAuditActions.NotificationDeadLettered, "DeliveryStatus=Failed",
            $"DeliveryStatus=DeadLettered;NotificationType={NotificationType};Reason=PermanentProviderFailure;Audience=Internal;OutboxMessageId={message.OutboxMessageId}",
            message, cancellationToken);
        return OutboxHandlingResult.Permanent(error ?? "The email provider reported a permanent failure.");
    }

    private Task WriteAuditAsync(Ticket ticket, string action, string? beforeValue, string afterValue, OutboxMessage message, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            actorEmployeeId: null, action, NotificationAuditActions.NotificationEntityType,
            ticket.TicketId.ToString(CultureInfo.InvariantCulture), beforeValue, afterValue, message.CorrelationId, cancellationToken);
}

/// <summary>"A ticket has been assigned to you" — from the internal <c>TicketAssignedToAgent</c> event. Skipped once the ticket has moved to somebody else or has been closed.</summary>
public sealed class AgentAssignedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    IEmailSender emailSender,
    IUserAccountManager accountManager,
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    CustomerContactResolver contactResolver,
    CustomerTicketSubjectResolver subjectResolver,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    CustomerNotificationModePolicy modePolicy,
    TimeProvider timeProvider)
    : InternalTicketEmailHandler(
        ticketRepository, notificationRepository, emailSender, accountManager, employeeRepository, departmentRepository, contactResolver,
        subjectResolver, auditWriter, policy, modePolicy, timeProvider)
{
    public override string EventType => OutboxEventTypes.TicketAssignedToAgent;

    protected override NotificationType NotificationType => NotificationType.AgentAssigned;

    protected override long? ParseTicketId(string payload) => TicketAssignedEventPayload.FromJson(payload)?.TicketId;

    protected override Guid? ParseRecipient(string payload) => TicketAssignedEventPayload.FromJson(payload)?.AssignedEmployeeId;

    protected override string? SupersededReason(Ticket ticket, OutboxMessage message)
    {
        var payload = TicketAssignedEventPayload.FromJson(message.Payload);
        if (payload is null)
        {
            return null;
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return CustomerEmailSkipReasons.SupersededByClosure;
        }

        return ticket.CurrentOwnerEmployeeId != payload.AssignedEmployeeId ? CustomerEmailSkipReasons.SupersededByLaterUpdate : null;
    }

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, InternalTicketEmailModel model, OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = TicketAssignedEventPayload.FromJson(message.Payload)!;
        return InternalEmailTemplates.TicketAssignedToYou(model with
        {
            ActorName = await EmployeeNameAsync(payload.ActorEmployeeId, cancellationToken),
            PreviousOwnerName = await EmployeeNameAsync(payload.PreviousOwnerEmployeeId, cancellationToken)
        });
    }
}

/// <summary>"The customer has replied" — from the internal <c>CustomerReplyReceived</c> event, to the owner at the time. Skipped once the ticket has changed hands or been closed.</summary>
public sealed class CustomerReplyNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    IEmailSender emailSender,
    IUserAccountManager accountManager,
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    CustomerContactResolver contactResolver,
    CustomerTicketSubjectResolver subjectResolver,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    CustomerNotificationModePolicy modePolicy,
    TimeProvider timeProvider)
    : InternalTicketEmailHandler(
        ticketRepository, notificationRepository, emailSender, accountManager, employeeRepository, departmentRepository, contactResolver,
        subjectResolver, auditWriter, policy, modePolicy, timeProvider)
{
    public override string EventType => OutboxEventTypes.CustomerReplyReceived;

    protected override NotificationType NotificationType => NotificationType.CustomerReplyReceived;

    protected override long? ParseTicketId(string payload) => CustomerReplyEventPayload.FromJson(payload)?.TicketId;

    protected override Guid? ParseRecipient(string payload) => CustomerReplyEventPayload.FromJson(payload)?.AssignedEmployeeId;

    protected override string? SupersededReason(Ticket ticket, OutboxMessage message)
    {
        var payload = CustomerReplyEventPayload.FromJson(message.Payload);
        if (payload is null)
        {
            return null;
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return CustomerEmailSkipReasons.SupersededByClosure;
        }

        return ticket.CurrentOwnerEmployeeId != payload.AssignedEmployeeId ? CustomerEmailSkipReasons.SupersededByLaterUpdate : null;
    }

    protected override Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, InternalTicketEmailModel model, OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = CustomerReplyEventPayload.FromJson(message.Payload)!;
        return Task.FromResult(InternalEmailTemplates.CustomerReplyReceived(model with { MessageCount = payload.MessageCount }));
    }
}
