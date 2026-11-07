using System.Globalization;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// The one place a ticket operation records "the customer (or a colleague) should hear about this" — as a transactional Outbox event
/// (ADR-0013) plus its <c>NotificationQueued</c> audit entry, inside the caller's own unit of work. Nothing is sent here: the Outbox
/// dispatcher renders and delivers later, so an email problem can never fail or roll back a ticket operation, and a rolled-back
/// operation leaves no event behind.
///
/// <para>
/// <b>Events are written only for meaningful lifecycle moments</b>, never for every save: creation, assignment, the status changes a
/// customer cares about (<c>InProgress</c>, <c>PendingCustomer</c>), resolution, closure, reopening, and — internally — a customer
/// reply or an assignment to a colleague. The rules that decide whether an occurrence is meaningful live here, so every path that
/// performs the same action (a person, Genesys, a job) produces the same event.
/// </para>
///
/// <para>
/// <b>Duplicates.</b> Each occurrence has one idempotency key, so a retried request for the same occurrence (a repeated API call, a
/// redelivered Genesys event) collapses to one message. Transitions that happen in quick succession are not coalesced here — that is
/// the handlers' job at dispatch time, where the ticket's current state is known (see <c>CustomerTicketEmailHandler</c>).
/// </para>
///
/// <para>
/// A plain helper over the two ports every app service already has, so a service that takes <see cref="IOutboxWriter"/> and
/// <see cref="IAuditEntryWriter"/> can create one with <c>new</c> and no further wiring. <see cref="CreateOrNull"/> lets a service
/// whose Outbox writer is optional (older test fixtures) keep working without one — it then records no events.
/// </para>
/// </summary>
public sealed class TicketNotificationEvents(IOutboxWriter outboxWriter, IAuditEntryWriter auditWriter)
{
    public static TicketNotificationEvents? CreateOrNull(IOutboxWriter? outboxWriter, IAuditEntryWriter auditWriter) =>
        outboxWriter is null ? null : new TicketNotificationEvents(outboxWriter, auditWriter);

    /// <summary>The <c>TicketCreated</c> event — the acknowledgement email. One per ticket, ever.</summary>
    public Task QueueTicketCreatedAsync(Ticket ticket, Guid? actorEmployeeId, Guid correlationId, DateTime nowUtc, CancellationToken cancellationToken) =>
        WriteAsync(
            OutboxEventTypes.TicketCreated,
            new TicketCreatedEventPayload(ticket.TicketId, OutboxEventTypes.TicketCreatedVersion).ToJson(),
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, ticket.TicketId, OutboxEventTypes.TicketCreatedVersion),
            ticket, actorEmployeeId, correlationId, nowUtc, detail: null, cancellationToken);

    /// <summary>
    /// <c>TicketResolved</c>, <c>TicketClosed</c> or <c>TicketReopened</c>: one per resolution cycle. A system closure that goes straight
    /// from working to Closed records only <c>TicketClosed</c> — the Closed email carries the outcome, and a separate "resolved" email
    /// seconds earlier would be noise (the handler also skips a Resolved event whose ticket is already Closed).
    /// </summary>
    public Task QueueLifecycleAsync(
        string eventType, int eventVersion, Ticket ticket, Guid? actorEmployeeId, Guid correlationId, DateTime nowUtc, CancellationToken cancellationToken,
        string? closureKind = null) =>
        WriteAsync(
            eventType,
            new TicketLifecycleEventPayload(ticket.TicketId, eventVersion, ticket.ReopenCount, closureKind).ToJson(),
            OutboxEventTypes.LifecycleIdempotencyKeyFor(eventType, ticket.TicketId, eventVersion, ticket.ReopenCount),
            ticket, actorEmployeeId, correlationId, nowUtc, detail: closureKind is null ? null : $"ClosureKind={closureKind}", cancellationToken);

    /// <summary>
    /// A Change Status operation. Only the two targets a customer cares about produce an event: <see cref="TicketStatus.InProgress"/>
    /// ("we are working on it", also after a pending period) and <see cref="TicketStatus.PendingCustomer"/> ("we need something from
    /// you"). Resolved and Closed have their own operations and events; nothing else is customer-visible.
    /// </summary>
    public Task QueueStatusChangedAsync(
        Ticket ticket, TicketStatus previousStatus, TicketStatus newStatus, Guid? actorEmployeeId, Guid correlationId, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var (eventType, version) = newStatus switch
        {
            TicketStatus.InProgress => (OutboxEventTypes.TicketInProgress, OutboxEventTypes.TicketInProgressVersion),
            TicketStatus.PendingCustomer => (OutboxEventTypes.TicketPendingCustomer, OutboxEventTypes.TicketPendingCustomerVersion),
            _ => (null, 0)
        };

        if (eventType is null || previousStatus == newStatus || ticket.TicketId <= 0)
        {
            return Task.CompletedTask;
        }

        var payload = new TicketStatusChangedEventPayload(ticket.TicketId, version, ticket.ReopenCount, (byte)previousStatus, (byte)newStatus);
        var occurrence = $"from{(byte)previousStatus}:at{Stamp(nowUtc)}";

        return WriteAsync(
            eventType, payload.ToJson(),
            OutboxEventTypes.OccurrenceIdempotencyKeyFor(eventType, ticket.TicketId, version, ticket.ReopenCount, occurrence),
            ticket, actorEmployeeId, correlationId, nowUtc, detail: $"From={previousStatus};To={newStatus}", cancellationToken);
    }

    /// <summary>
    /// An assignment or transfer to a person, whoever performed it (a Supervisor, the agent themself, a routing rule, Round Robin,
    /// Genesys answering). Produces up to two events:
    /// <list type="bullet">
    /// <item><b><c>TicketAssigned</c> (customer)</b> — only when the ticket gains an owner it did not have (first assignment of the
    /// cycle), outside the creation transaction (the acknowledgement already says the request is being looked after), and while the
    /// ticket is still being worked. A reassignment between colleagues is internal routing the customer is not told about.</item>
    /// <item><b><c>TicketAssignedToAgent</c> (internal)</b> — whenever somebody other than the assignee (a person or automation) put
    /// the ticket on their desk. A self-assignment (claim, handoff acceptance, answering the call) tells nobody.</item>
    /// </list>
    /// Assigning a ticket to the person who already owns it is not an event.
    /// </summary>
    public async Task QueueAssignedAsync(
        Ticket ticket, Guid? previousOwnerEmployeeId, Guid assignedEmployeeId, Guid? actorEmployeeId, Guid correlationId, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (ticket.TicketId <= 0 || assignedEmployeeId == Guid.Empty || previousOwnerEmployeeId == assignedEmployeeId)
        {
            return;
        }

        var occurrence = $"to{assignedEmployeeId:N}:from{(previousOwnerEmployeeId is { } p ? p.ToString("N") : "queue")}:at{Stamp(nowUtc)}";
        var detail = $"AssignedEmployeeId={assignedEmployeeId};PreviousOwnerEmployeeId={previousOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}";

        var createdInThisTransaction = ticket.CreatedAtUtc == nowUtc;
        var gainedOwner = previousOwnerEmployeeId is null && ticket.TicketStatus is not (TicketStatus.Resolved or TicketStatus.Closed);
        if (gainedOwner && !createdInThisTransaction)
        {
            var payload = new TicketAssignedEventPayload(
                ticket.TicketId, OutboxEventTypes.TicketAssignedVersion, ticket.ReopenCount, assignedEmployeeId, previousOwnerEmployeeId,
                actorEmployeeId, (byte)ticket.TicketStatus, ticket.CurrentDepartmentId);

            await WriteAsync(
                OutboxEventTypes.TicketAssigned, payload.ToJson(),
                OutboxEventTypes.OccurrenceIdempotencyKeyFor(
                    OutboxEventTypes.TicketAssigned, ticket.TicketId, OutboxEventTypes.TicketAssignedVersion, ticket.ReopenCount, occurrence),
                ticket, actorEmployeeId, correlationId, nowUtc, detail, cancellationToken);
        }

        if (actorEmployeeId != assignedEmployeeId)
        {
            var payload = new TicketAssignedEventPayload(
                ticket.TicketId, OutboxEventTypes.TicketAssignedToAgentVersion, ticket.ReopenCount, assignedEmployeeId, previousOwnerEmployeeId,
                actorEmployeeId, (byte)ticket.TicketStatus, ticket.CurrentDepartmentId);

            await WriteAsync(
                OutboxEventTypes.TicketAssignedToAgent, payload.ToJson(),
                OutboxEventTypes.OccurrenceIdempotencyKeyFor(
                    OutboxEventTypes.TicketAssignedToAgent, ticket.TicketId, OutboxEventTypes.TicketAssignedToAgentVersion, ticket.ReopenCount, occurrence),
                ticket, actorEmployeeId, correlationId, nowUtc, detail + ";Audience=Internal", cancellationToken);
        }
    }

    /// <summary>
    /// Customer messages appended to a conversation by a Genesys transcript delivery. Tells the ticket's owner — but only when a person
    /// is actually handling the ticket (it is InProgress or waiting for the customer, or a human agent holds the conversation). While the
    /// AI virtual agent is still talking to the customer on an Open ticket, nobody is told: the transcript lands on the ticket and that is
    /// enough. A redelivered transcript appends nothing new and therefore produces no event.
    /// </summary>
    public Task QueueCustomerReplyAsync(
        Ticket ticket, TicketInteraction interaction, IReadOnlyList<TicketInteractionMessage> appendedCustomerMessages, Guid? actorEmployeeId,
        Guid correlationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(appendedCustomerMessages);

        if (appendedCustomerMessages.Count == 0 || ticket.TicketId <= 0 || ticket.CurrentOwnerEmployeeId is not { } owner)
        {
            return Task.CompletedTask;
        }

        var humanHandled = ticket.TicketStatus is TicketStatus.InProgress or TicketStatus.PendingCustomer || interaction.HandledByUserId is not null;
        if (!humanHandled || ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed)
        {
            return Task.CompletedTask;
        }

        var latest = appendedCustomerMessages.OrderBy(m => m.Sequence).Last();
        var payload = new CustomerReplyEventPayload(
            ticket.TicketId, OutboxEventTypes.CustomerReplyReceivedVersion, interaction.TicketInteractionId, owner,
            appendedCustomerMessages.Count, latest.Sequence, latest.SentAtUtc);

        return WriteAsync(
            OutboxEventTypes.CustomerReplyReceived, payload.ToJson(),
            OutboxEventTypes.OccurrenceIdempotencyKeyFor(
                OutboxEventTypes.CustomerReplyReceived, ticket.TicketId, OutboxEventTypes.CustomerReplyReceivedVersion, ticket.ReopenCount,
                $"interaction{interaction.TicketInteractionId}:seq{latest.Sequence}"),
            ticket, actorEmployeeId, correlationId, nowUtc,
            $"InteractionId={interaction.TicketInteractionId};Messages={appendedCustomerMessages.Count};RecipientEmployeeId={owner};Audience=Internal",
            cancellationToken);
    }

    private async Task WriteAsync(
        string eventType, string payload, string idempotencyKey, Ticket ticket, Guid? actorEmployeeId, Guid correlationId, DateTime nowUtc,
        string? detail, CancellationToken cancellationToken)
    {
        var message = await outboxWriter.WriteAsync(eventType, payload, correlationId, idempotencyKey, nowUtc, cancellationToken);
        if (message is null)
        {
            // Already enqueued for this occurrence (a retried request, a redelivered event); the first row stands.
            return;
        }

        // Keyed by the ticket — like every delivery-outcome entry — so Ticket Details can list "email queued" next to "email sent".
        // The Outbox message id stays in the value for the operator who follows one delivery end-to-end.
        await auditWriter.WriteAsync(
            actorEmployeeId,
            NotificationAuditActions.NotificationQueued,
            NotificationAuditActions.NotificationEntityType,
            ticket.TicketId.ToString(CultureInfo.InvariantCulture),
            beforeValue: null,
            afterValue: $"EventType={eventType};TicketId={ticket.TicketId};OutboxMessageId={message.OutboxMessageId};Status=Pending"
                + (detail is null ? string.Empty : ";" + detail),
            correlationId,
            cancellationToken);
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
}
