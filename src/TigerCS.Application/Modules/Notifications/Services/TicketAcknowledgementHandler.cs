using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications.Templates;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// FR-NOT-01 / backlog S-18 — the automated "we have received your request"
/// email, consumed from the <c>TicketCreated</c> Outbox event.
///
/// <para>
/// <b>This path never satisfies the First Response SLA.</b> FR-SLA-05 and
/// ISSUE-019 exist because an acknowledgement fires within seconds of
/// creation and would otherwise "satisfy" every first-response target
/// automatically. A successful send writes exactly one ticket field,
/// <c>AcknowledgementSentAtUtc</c>, via <see cref="OnSent"/>; nothing here
/// references any service that could record a first response.
/// </para>
///
/// <para>
/// <b>Extra idempotency guard.</b> On top of the delivery-row guards in
/// <see cref="CustomerTicketEmailHandler"/>, the write-once
/// <c>Ticket.AcknowledgementSentAtUtc</c> short-circuits a redelivered
/// message before any recipient is resolved or provider touched.
/// </para>
/// </summary>
public sealed class TicketAcknowledgementHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    CustomerNotificationModePolicy modePolicy,
    IRequestTypeRepository requestTypeRepository,
    ICategoryRepository categoryRepository,
    CustomerTicketSubjectResolver? subjectResolver = null)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider, modePolicy)
{
    public override string EventType => OutboxEventTypes.TicketCreated;

    protected override NotificationType NotificationType => NotificationType.Acknowledgement;

    protected override long? ParseTicketId(string payload) => TicketCreatedEventPayload.FromJson(payload)?.TicketId;

    protected override bool IsAlreadyDelivered(Ticket ticket) => ticket.AcknowledgementSentAtUtc is not null;

    protected override void OnSent(Ticket ticket, DateTime sentAtUtc) => ticket.RecordAcknowledgementSent(sentAtUtc);

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        // "Request type/category where appropriate": the request type is
        // the customer-recognisable classification when one was chosen;
        // the category is the coarser fallback. An unclassified ticket
        // simply omits the line rather than showing a placeholder.
        string? requestTypeName = null;
        if (ticket.RequestTypeId is { } requestTypeId)
        {
            requestTypeName = (await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken))?.Name;
        }

        if (string.IsNullOrWhiteSpace(requestTypeName) && ticket.CategoryId is { } categoryId)
        {
            requestTypeName = (await categoryRepository.GetByIdAsync(categoryId, cancellationToken))?.Name;
        }

        return CustomerEmailTemplates.TicketCreated(new CustomerEmailTicketModel(
            ticket.TicketNumber,
            contact.DisplayName,
            ticket.CreatedAtUtc,
            RequestTypeName: requestTypeName,
            TicketSubject: subjectResolver is null ? null : await subjectResolver.ResolveAsync(ticket, cancellationToken),
            Language: contact.Language,
            Branding: ModePolicy.Branding,
            IsTest: ModePolicy.IsTest));
    }
}
