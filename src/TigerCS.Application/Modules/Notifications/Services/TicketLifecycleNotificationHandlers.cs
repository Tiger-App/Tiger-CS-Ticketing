using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerReviews.Services;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications.Templates;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Customer Email Notifications increment — "your request has been
/// resolved", consumed from the <c>TicketResolved</c> Outbox event that
/// <c>TicketLifecycleAppService.ResolveAsync</c> writes in the resolving
/// transaction.
///
/// <para>
/// The resolution note is quoted only when
/// <see cref="CustomerNotificationPolicy.IncludeResolutionNote"/> allows it;
/// internal ticket notes are never read here at all.
/// </para>
/// </summary>
public sealed class TicketResolvedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy,
    ITicketResolutionRepository resolutionRepository,
    CustomerTicketSubjectResolver subjectResolver,
    TicketReviewInvitationService reviewInvitations)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider, modePolicy)
{
    public override string EventType => OutboxEventTypes.TicketResolved;

    protected override NotificationType NotificationType => NotificationType.Resolved;

    protected override long? ParseTicketId(string payload) => TicketLifecycleEventPayload.FromJson(payload)?.TicketId;

    /// <summary>A resolution with the Cancelled outcome is the "cancelled" notification: its own wording, its own delivery row.</summary>
    protected override async Task<NotificationType> ResolveNotificationTypeAsync(Ticket ticket, CancellationToken cancellationToken) =>
        await OutcomeAsync(ticket, cancellationToken) == ResolutionOutcome.Cancelled ? NotificationType.Cancelled : NotificationType.Resolved;

    /// <summary>Resolved immediately followed by Closed (the same cycle): the Closed email carries the outcome, this one is not sent.</summary>
    protected override string? SupersededReason(Ticket ticket, OutboxMessage message) =>
        ticket.TicketStatus == TicketStatus.Closed && ticket.ReopenCount == TicketLifecycleEventPayload.FromJson(message.Payload)?.ReopenCount
            ? CustomerEmailSkipReasons.SupersededByClosure
            : null;

    private async Task<ResolutionOutcome?> OutcomeAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        // The current resolution when the ticket is still Resolved/Closed;
        // null once it has been reopened (the resolution is archived), in
        // which case the ticket's own outcome byte — also cleared on reopen
        // — leaves the generic "Resolved" wording.
        var resolution = await resolutionRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        return resolution?.ResolutionOutcome ?? (ticket.ResolutionOutcome is { } stored ? (ResolutionOutcome?)stored : null);
    }

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, OutboxMessage message, CancellationToken cancellationToken)
    {
        var resolution = await resolutionRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        var outcome = await OutcomeAsync(ticket, cancellationToken);

        var model = new CustomerEmailTicketModel(
            ticket.TicketNumber,
            contact.DisplayName,
            resolution?.ResolvedAtUtc ?? message.OccurredAtUtc,
            ResolutionStatus: CustomerEmailTemplates.DescribeResolution(outcome),
            ResolutionMessage: Policy.IncludeResolutionNote ? resolution?.ResolutionNote : null,
            TicketSubject: await subjectResolver.ResolveAsync(ticket, cancellationToken),
            Language: contact.Language,
            Branding: ModePolicy.Branding,
            // Null when the customer has already reviewed this ticket or no public base URL is configured.
            Review: await reviewInvitations.PrepareAsync(ticket, cancellationToken),
            IsTest: ModePolicy.IsTest);

        return outcome == ResolutionOutcome.Cancelled
            ? CustomerEmailTemplates.TicketCancelled(model with { Review = null })
            : CustomerEmailTemplates.TicketResolved(model);
    }
}

/// <summary>Customer Email Notifications increment — "your request has been closed", from the <c>TicketClosed</c> Outbox event.</summary>
public sealed class TicketClosedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy,
    ITicketResolutionRepository resolutionRepository,
    CustomerTicketSubjectResolver subjectResolver,
    TicketReviewInvitationService reviewInvitations)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider, modePolicy)
{
    public override string EventType => OutboxEventTypes.TicketClosed;

    protected override NotificationType NotificationType => NotificationType.Closed;

    protected override long? ParseTicketId(string payload) => TicketLifecycleEventPayload.FromJson(payload)?.TicketId;

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, OutboxMessage message, CancellationToken cancellationToken)
    {
        // The outcome is always rendered (a system closure sends only this email); the agent's note only when configuration allows.
        var resolution = await resolutionRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        var outcome = resolution?.ResolutionOutcome ?? (ticket.ResolutionOutcome is { } stored ? (ResolutionOutcome?)stored : null);
        var closureKind = TicketLifecycleEventPayload.FromJson(message.Payload)?.ClosureKind;

        return CustomerEmailTemplates.TicketClosed(new CustomerEmailTicketModel(
            ticket.TicketNumber,
            contact.DisplayName,
            message.OccurredAtUtc,
            ResolutionStatus: CustomerEmailTemplates.DescribeResolution(outcome),
            ResolutionMessage: Policy.IncludeResolutionNote ? resolution?.ResolutionNote : null,
            ClosedForCustomerInactivity: resolution?.ClosedForCustomerInactivity == true || closureKind == ClosureKinds.CustomerInactivity,
            ClosedForAiOnlyTimeout: closureKind == ClosureKinds.AiOnlyTimeout,
            TicketSubject: await subjectResolver.ResolveAsync(ticket, cancellationToken),
            Language: contact.Language,
            Branding: ModePolicy.Branding,
            // A review the customer already gave is preserved and not asked for again: PrepareAsync returns null for it.
            Review: await reviewInvitations.PrepareAsync(ticket, cancellationToken),
            IsTest: ModePolicy.IsTest));
    }
}

/// <summary>Customer Email Notifications increment — "your request has been reopened", from the <c>TicketReopened</c> Outbox event.</summary>
public sealed class TicketReopenedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy,
    CustomerTicketSubjectResolver subjectResolver)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider, modePolicy)
{
    public override string EventType => OutboxEventTypes.TicketReopened;

    protected override NotificationType NotificationType => NotificationType.Reopened;

    protected override long? ParseTicketId(string payload) => TicketLifecycleEventPayload.FromJson(payload)?.TicketId;

    /// <summary>Reopened and closed again before delivery (the same new cycle): the customer hears about the closure only.</summary>
    protected override string? SupersededReason(Ticket ticket, OutboxMessage message) =>
        ticket.TicketStatus == TicketStatus.Closed && ticket.ReopenCount == TicketLifecycleEventPayload.FromJson(message.Payload)?.ReopenCount
            ? CustomerEmailSkipReasons.SupersededByClosure
            : null;

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, OutboxMessage message, CancellationToken cancellationToken) =>
        CustomerEmailTemplates.TicketReopened(new CustomerEmailTicketModel(
            ticket.TicketNumber, contact.DisplayName, message.OccurredAtUtc,
            TicketSubject: await subjectResolver.ResolveAsync(ticket, cancellationToken),
            Language: contact.Language, Branding: ModePolicy.Branding, IsTest: ModePolicy.IsTest));
}

/// <summary>
/// "Your request has been assigned" — from the <c>TicketAssigned</c> Outbox event, written when a ticket gains an owner. Skipped when
/// the ticket has moved on since (another status, another owner, a new cycle): the later state's email is the one worth sending.
/// </summary>
public sealed class TicketAssignedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy,
    CustomerTicketSubjectResolver subjectResolver)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider, modePolicy)
{
    public override string EventType => OutboxEventTypes.TicketAssigned;

    protected override NotificationType NotificationType => NotificationType.Assigned;

    protected override long? ParseTicketId(string payload) => TicketAssignedEventPayload.FromJson(payload)?.TicketId;

    protected override string? SupersededReason(Ticket ticket, OutboxMessage message)
    {
        var payload = TicketAssignedEventPayload.FromJson(message.Payload);
        if (payload is null)
        {
            return null;
        }

        if (ticket.TicketStatus == TicketStatus.Closed && ticket.ReopenCount == payload.ReopenCount)
        {
            return CustomerEmailSkipReasons.SupersededByClosure;
        }

        return ticket.ReopenCount != payload.ReopenCount
            || ticket.CurrentOwnerEmployeeId != payload.AssignedEmployeeId
            || (byte)ticket.TicketStatus != payload.TicketStatus
            ? CustomerEmailSkipReasons.SupersededByLaterUpdate
            : null;
    }

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, OutboxMessage message, CancellationToken cancellationToken) =>
        CustomerEmailTemplates.TicketAssigned(new CustomerEmailTicketModel(
            ticket.TicketNumber, contact.DisplayName, message.OccurredAtUtc,
            TicketSubject: await subjectResolver.ResolveAsync(ticket, cancellationToken),
            TicketStatus: ticket.TicketStatus.ToString(),
            Language: contact.Language, Branding: ModePolicy.Branding, IsTest: ModePolicy.IsTest));
}

/// <summary>
/// "We are working on your request" — from the <c>TicketInProgress</c> Outbox event (Change Status to InProgress, including the return
/// from waiting on the customer). Skipped when the ticket is no longer InProgress in that cycle: whatever it moved to has its own email.
/// </summary>
public sealed class TicketInProgressNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy,
    CustomerTicketSubjectResolver subjectResolver)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider, modePolicy)
{
    public override string EventType => OutboxEventTypes.TicketInProgress;

    protected override NotificationType NotificationType => NotificationType.InProgress;

    protected override long? ParseTicketId(string payload) => TicketStatusChangedEventPayload.FromJson(payload)?.TicketId;

    protected override string? SupersededReason(Ticket ticket, OutboxMessage message) =>
        StatusEventSupersession.For(ticket, TicketStatusChangedEventPayload.FromJson(message.Payload), TicketStatus.InProgress);

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, OutboxMessage message, CancellationToken cancellationToken) =>
        CustomerEmailTemplates.TicketInProgress(new CustomerEmailTicketModel(
            ticket.TicketNumber, contact.DisplayName, message.OccurredAtUtc,
            TicketSubject: await subjectResolver.ResolveAsync(ticket, cancellationToken),
            TicketStatus: nameof(TicketStatus.InProgress),
            Language: contact.Language, Branding: ModePolicy.Branding, IsTest: ModePolicy.IsTest));
}

/// <summary>
/// "We need your input" — from the <c>TicketPendingCustomer</c> Outbox event. The agent's pending reason is quoted only under the same
/// configuration switch as the resolution note (agent-written text is internal until the business says otherwise). Skipped when the
/// ticket has already left PendingCustomer: the customer replied, or the ticket was resolved or closed.
/// </summary>
public sealed class TicketPendingCustomerNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy,
    CustomerTicketSubjectResolver subjectResolver,
    ITicketPendingRecordRepository pendingRecordRepository)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider, modePolicy)
{
    public override string EventType => OutboxEventTypes.TicketPendingCustomer;

    protected override NotificationType NotificationType => NotificationType.PendingCustomer;

    protected override long? ParseTicketId(string payload) => TicketStatusChangedEventPayload.FromJson(payload)?.TicketId;

    protected override string? SupersededReason(Ticket ticket, OutboxMessage message) =>
        StatusEventSupersession.For(ticket, TicketStatusChangedEventPayload.FromJson(message.Payload), TicketStatus.PendingCustomer);

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, OutboxMessage message, CancellationToken cancellationToken)
    {
        var pending = Policy.IncludeResolutionNote ? await pendingRecordRepository.GetOpenAsync(ticket.TicketId, cancellationToken) : null;

        return CustomerEmailTemplates.TicketWaitingForCustomer(new CustomerEmailTicketModel(
            ticket.TicketNumber, contact.DisplayName, message.OccurredAtUtc,
            TicketSubject: await subjectResolver.ResolveAsync(ticket, cancellationToken),
            TicketStatus: nameof(TicketStatus.PendingCustomer),
            PendingReason: pending?.Reason,
            Language: contact.Language, Branding: ModePolicy.Branding, IsTest: ModePolicy.IsTest));
    }
}

/// <summary>The one coalescing rule every status-change email shares: send only while the ticket is still in the announced status of the announced cycle.</summary>
internal static class StatusEventSupersession
{
    public static string? For(Ticket ticket, TicketStatusChangedEventPayload? payload, TicketStatus announced)
    {
        if (payload is null)
        {
            return null;
        }

        if (ticket.TicketStatus == TicketStatus.Closed && ticket.ReopenCount == payload.ReopenCount)
        {
            return CustomerEmailSkipReasons.SupersededByClosure;
        }

        return ticket.ReopenCount != payload.ReopenCount || ticket.TicketStatus != announced
            ? CustomerEmailSkipReasons.SupersededByLaterUpdate
            : null;
    }
}
