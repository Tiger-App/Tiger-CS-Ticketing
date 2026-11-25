using System.Globalization;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.QueueingIntegration.Abstractions;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.QueueingIntegration.Services;

/// <summary>
/// Applies the Queueing System's events to TigerCS tickets. <b>The Queueing System is the source of truth for the lifecycle of the tickets it
/// originates</b>; this service only mirrors what it reports, by calling the existing ticketing services (creation, assignment, lifecycle) - there is no
/// second workflow here.
///
/// <para>
/// <b>Mapping to existing TigerCS behaviour</b> (no status is added):
/// <c>TicketCreated</c> = a classified, <b>unassigned</b>, Open ticket on the Walk in channel, in the mapped department, with the mapped request type
/// (the SLA starts with the default priority at the event's original time); <c>TicketAssigned</c> / <c>TicketReassigned</c> = the existing assignment (to the
/// real, mapped employee - never the integration account) and, on the first assignment of an Open ticket, the existing Open -> InProgress transition performed
/// as that employee; <c>TicketReturned</c> = the owner is released and the ticket returns to its department's queue (status unchanged - InProgress -> Open is not a
/// TigerCS transition); <c>TicketCommentAdded</c> = a ticket note; <c>TicketRecalled</c> = an audited history entry; <c>TicketClosed</c> = the existing system
/// closure with outcome Resolved; <c>TicketCancelled</c> = the same closure with outcome Cancelled.
/// </para>
///
/// <para>
/// <b>Reliability.</b> Every delivery is first written to the event inbox (unique on EventId at the database), so a duplicate or concurrent delivery is
/// one row and is answered with the stored outcome. State-changing events carry the queue ticket's own sequence: an event older than the applied state is
/// recorded as superseded and never overwrites it; comments and recalls only add records, so a late one is still recorded. An event that cannot be applied
/// <i>yet</i> - the ticket does not exist yet, or a mapping is missing - is kept Unresolved with a backoff and retried (by the retry sweep, by redelivery, and
/// as soon as the ticket is created); nothing is guessed. The integration's own state (link, inbox row) is staged on the same unit of work as the ticket
/// change, so it commits in the very transaction of that change; if the change is refused or fails, the staged state is discarded and only the failure is recorded.
/// </para>
/// </summary>
public sealed class QueueingEventProcessor(
    QueueingIntegrationOptions options,
    IQueueingStore store,
    IQueueingEventLock eventLock,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IChannelRepository channelRepository,
    IDepartmentRepository departmentRepository,
    IPriorityRepository priorityRepository,
    TicketRequestTypeRoutingService requestTypeRouting,
    IntakeRecordAppService intakeRecordAppService,
    TicketCreationAppService ticketCreationAppService,
    CustomerSearchAppService customerSearchAppService,
    ICustomerContactDetailRepository contactDetailRepository,
    ICustomerProfileOverrideRepository profileOverrides,
    CustomerProfileAuditService profileAudit,
    DefaultClassificationService? defaultClassification,
    ITicketRepository ticketRepository,
    TicketAssignmentAppService assignmentAppService,
    TicketLifecycleAppService lifecycleAppService,
    ITicketNoteRepository noteRepository,
    IEmployeeRepository employeeRepository,
    IUserRoleReader roleReader,
    IAuditEntryWriter auditWriter,
    ITicketInteractionRepository interactionRepository,
    ILogger<QueueingEventProcessor> logger)
{
    private const string SourceLabel = "Queueing System";

    /// <summary>How long a request waits for another request on the same queue ticket before answering "busy, resend".</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------ entry points

    public async Task<QueueingEventResponse> ReceiveAsync(QueueingEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return new QueueingEventResponse(request.EventId, request.ExternalTicketId, QueueingResultStatus.Disabled, "INTEGRATION_DISABLED",
                Detail: "QueueingIntegration:Enabled is false - no queueing event is processed while the integration is switched off.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!QueueingEventValidator.TryValidate(request, options, now, out var message, out var errors))
        {
            return new QueueingEventResponse(request.EventId, request.ExternalTicketId, QueueingResultStatus.Invalid, "VALIDATION_FAILED",
                Detail: "The event failed validation. Nothing was stored.", Errors: errors);
        }

        var payload = request.ToCanonicalJson();

        // Idempotency on EventId, first: a duplicate never reaches the ticket.
        var existing = await store.GetEventAsync(message!.EventId, cancellationToken);
        if (existing is null)
        {
            var row = new QueueingEvent(
                message.EventId, message.EventType, message.ExternalTicketId, message.EventSequence, message.OccurredAtUtc, payload, now);
            await store.AddEventAsync(row, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                existing = row;
            }
            catch (DuplicateWriteException)
            {
                // A concurrent delivery of the same EventId won the race at the unique index: that is the guarantee working. Answer from its row.
                store.DiscardPendingChanges();
                existing = await store.GetEventAsync(message.EventId, cancellationToken);
                if (existing is null)
                {
                    return Busy(message, "The event is being recorded by another request.");
                }
            }
        }

        if (!existing.HasSamePayloadAs(payload))
        {
            return new QueueingEventResponse(message.EventId, message.ExternalTicketId, QueueingResultStatus.Conflict, "EVENT_ID_REUSED",
                existing.TicketId, Detail: $"EventId '{message.EventId}' was already received with different content. Events are immutable; send a new EventId for a new event.");
        }

        if (existing.IsSettled)
        {
            return await FromRowAsync(existing, QueueingResultStatus.Duplicate, cancellationToken);
        }

        if (existing.Status == QueueingEventStatus.Failed)
        {
            // A Failed event is restarted only by an administrator's audited reprocess (ReprocessAsync) - never by a redelivery, so a sender that keeps resending cannot
            // turn a permanently invalid event into an uncontrolled retry loop. Answered from the stored outcome; everything behind it stays held.
            return await FromRowAsync(existing, QueueingResultStatus.Pending, cancellationToken);
        }

        return await ProcessWithLockAsync(existing, message, cancellationToken);
    }

    /// <summary>
    /// The retry sweep: applies Unresolved events that are due and events whose processing was interrupted. Safe to run concurrently with live
    /// traffic and with itself - each event is applied under the same per-ticket lock and the same database guards. Returns how many were settled.
    /// </summary>
    public async Task<int> RetryDueAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return 0;
        }

        // Only the ids are kept from the list query. Every event is then re-read just before it is applied: a failure of an earlier event discards the
        // unit of work's tracked state, which would silently detach any row still held from this list and lose its "processed" mark.
        var dueIds = (await store.ListDueForRetryAsync(timeProvider.GetUtcNow().UtcDateTime, batchSize, cancellationToken)).Select(e => e.EventId).ToList();
        var settled = 0;
        foreach (var eventId in dueIds)
        {
            var row = await store.GetEventAsync(eventId, cancellationToken);
            if (row is null || row.IsSettled || row.Payload is null)
            {
                continue;
            }

            if (!QueueingEventValidator.TryValidate(
                    System.Text.Json.JsonSerializer.Deserialize<QueueingEventRequest>(row.Payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!,
                    options, DateTime.MaxValue.AddDays(-1), out var message, out _))
            {
                row.MarkRejected(QueueingOutcomeCodes.ProcessingFailed, "The stored event no longer validates.", row.TicketId, Now());
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            var response = await ProcessWithLockAsync(row, message!, cancellationToken);
            if (response.Status is not (QueueingResultStatus.Pending or QueueingResultStatus.Busy))
            {
                settled++;
            }
        }

        return settled;
    }

    /// <summary>
    /// The retention sweep: erases the payload of every <b>successfully processed</b> event older than <c>PayloadRetentionDays</c> (default 90). The row stays -
    /// EventId, ExternalTicketId, sequence, type, times, outcome, ticket link and the payload hash - so idempotency and the ticket history are untouched; an
    /// unresolved, failed, superseded or rejected event is never purged. Returns how many payloads were erased. Disabled when the retention is zero or negative.
    /// </summary>
    public async Task<int> PurgeExpiredPayloadsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (options.PayloadRetentionDays <= 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var purged = 0;
        foreach (var row in await store.ListPurgeablePayloadsAsync(now.AddDays(-options.PayloadRetentionDays), batchSize, cancellationToken))
        {
            if (row.PurgePayload(now))
            {
                purged++;
            }
        }

        if (purged > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return purged;
    }

    /// <summary>
    /// The administrator's reprocess of one stuck event (a Failed event, or a held one whose cause was fixed): audited first (who, which event, what it was), then its
    /// retry cycle restarts (attempts reset - the payload and the EventId never change), it is applied again under the ticket's lock and, if it settles, the events held behind it
    /// are released in sequence order. Never skips, edits or deletes an event, and sends nothing to the Queueing System. Returns null when the event does not exist, and a
    /// result with status <c>NotReprocessable</c> when it is neither Failed nor Unresolved.
    /// </summary>
    public async Task<QueueingReprocessResult?> ReprocessAsync(Guid administratorEmployeeId, string eventId, CancellationToken cancellationToken = default)
    {
        var row = await store.GetEventAsync(eventId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var previousStatus = row.Status;
        var previousOutcome = row.OutcomeCode;
        if (previousStatus is not (QueueingEventStatus.Failed or QueueingEventStatus.Unresolved) || row.Payload is null)
        {
            return new QueueingReprocessResult(row.EventId, row.ExternalTicketId, previousStatus.ToString(), previousOutcome, "NotReprocessable", previousOutcome, null);
        }

        if (!QueueingEventValidator.TryValidate(
                System.Text.Json.JsonSerializer.Deserialize<QueueingEventRequest>(row.Payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!,
                options, DateTime.MaxValue.AddDays(-1), out var message, out _))
        {
            return new QueueingReprocessResult(row.EventId, row.ExternalTicketId, previousStatus.ToString(), previousOutcome, "NotReprocessable", previousOutcome,
                "The stored event no longer validates.");
        }

        await auditWriter.WriteAsync(
            administratorEmployeeId, QueueingAuditActions.EventReprocessed, "QueueingEvent", row.EventId,
            beforeValue: $"Status={previousStatus};Outcome={previousOutcome ?? "(none)"};Attempts={row.AttemptCount};Sequence={row.EventSequence};Type={row.EventType};ExternalTicketId={row.ExternalTicketId}",
            afterValue: "ReprocessRequested;AttemptsReset",
            Guid.NewGuid(), cancellationToken);
        row.Reopen();   // saved together with the audit entry, so a later discard cannot undo the restart
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var response = await ProcessWithLockAsync(row, message!, cancellationToken);

        var after = await store.GetEventAsync(row.EventId, cancellationToken);
        return new QueueingReprocessResult(
            row.EventId, row.ExternalTicketId, previousStatus.ToString(), previousOutcome,
            after?.Status.ToString() ?? response.Status.ToString(), after?.OutcomeCode ?? response.Outcome, response.Detail);
    }

    // ------------------------------------------------------------------ locking and failure containment

    private async Task<QueueingEventResponse> ProcessWithLockAsync(QueueingEvent row, QueueingEventMessage message, CancellationToken cancellationToken)
    {
        using var held = await eventLock.TryAcquireAsync(message.ExternalTicketId, LockTimeout, cancellationToken);
        if (held is null)
        {
            return Busy(message, "Another request is changing the same queue ticket. Resend the same event.");
        }

        var response = await ProcessContainedAsync(row, message, cancellationToken);

        // Strict per-ticket order: once an event has settled (applied, superseded or rejected), the events that were held behind it are released, in sequence order, under
        // this same lock. A held or failed event releases nothing - everything behind it stays held.
        if (QueueingEventTypes.IsStateChange(message.EventType)
            && response.Status is QueueingResultStatus.Processed or QueueingResultStatus.Superseded or QueueingResultStatus.Rejected)
        {
            await ReleaseHeldFollowersAsync(message.ExternalTicketId, row.EventId, cancellationToken);
        }

        return response;
    }

    private async Task<QueueingEventResponse> ProcessContainedAsync(QueueingEvent row, QueueingEventMessage message, CancellationToken cancellationToken)
    {
        try
        {
            // The event's own time, never later than now: every record TigerCS writes while applying it carries when it happened in the queue.
            var realNow = timeProvider.GetUtcNow().UtcDateTime;
            var effective = message.OccurredAtUtc > realNow ? realNow : message.OccurredAtUtc;

            // EventSequence is the only ordering authority. A reconciled event (derived from the queue's current state) may carry an OccurredAtUtc that is earlier than an event
            // already applied - or earlier than the ticket's own creation. It is never rejected or reordered for that; but the ticket's history must not run backwards (a closure
            // before the creation, a negative SLA span), so the time its records are stamped with is floored at the ticket's creation and at the latest time already applied.
            if (message.EventType != QueueingEventType.TicketCreated
                && await store.FindTicketAsync(TicketIntegrationSources.QueueingSystem, message.ExternalTicketId, cancellationToken) is { } existingTicket)
            {
                var floor = existingTicket.CreatedAtUtc;
                if (await store.GetLinkAsync(existingTicket.TicketId, cancellationToken) is { } existingLink && existingLink.LastAppliedOccurredAtUtc > floor)
                {
                    floor = existingLink.LastAppliedOccurredAtUtc;
                }

                if (floor > realNow)
                {
                    floor = realNow;
                }

                if (effective < floor)
                {
                    effective = floor;
                }
            }

            using var scope = IntegrationEventScope.Begin(effective, realNow);
            return await ApplyAsync(row, message, effective, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            store.DiscardPendingChanges();
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ConcurrentUpdate,
                "The ticket was changed by another request while this event was being applied. It will be retried.", null, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never lose an event to an unexpected fault: record it for a controlled retry. Only the exception TYPE is stored - its message could carry customer data.
            logger.LogError(ex, "Queueing event {EventId} ({EventType}) for {ExternalTicketId} failed unexpectedly.", row.EventId, row.EventType, row.ExternalTicketId);
            store.DiscardPendingChanges();
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ProcessingFailed,
                $"Unexpected failure ({ex.GetType().Name}). It will be retried.", null, cancellationToken);
        }
    }

    /// <summary>
    /// Releases the events held behind an event that has just settled, strictly in sequence order. The chain stops at the first event that cannot be applied (it becomes the
    /// new head; everything after it stays held) or whose own backoff has not elapsed (it is retried by the sweep, as the head). Never skips an event.
    /// </summary>
    private async Task ReleaseHeldFollowersAsync(string externalTicketId, string exceptEventId, CancellationToken cancellationToken)
    {
        // Ids only, then each event is re-read right before it is applied (see RetryDueAsync): an earlier event's failure must not detach the later ones.
        var waitingIds = (await store.ListWaitingForTicketAsync(externalTicketId, cancellationToken)).Select(e => e.EventId).ToList();
        foreach (var waitingId in waitingIds)
        {
            if (string.Equals(waitingId, exceptEventId, StringComparison.Ordinal))
            {
                continue;
            }

            var waiting = await store.GetEventAsync(waitingId, cancellationToken);
            if (waiting is null || waiting.IsSettled || waiting.Payload is null)
            {
                continue;
            }

            // A hold that is waiting for an EVENT (the ticket's creation, or an earlier event) is released now - the event it waited for has just settled. A hold waiting
            // for configuration (a missing mapping / employee) keeps its own backoff: the sweep retries it as the head, and nothing behind it may pass.
            if (waiting.Status == QueueingEventStatus.Unresolved && waiting.NextRetryAtUtc is { } due && due > Now()
                && waiting.OutcomeCode is not (QueueingOutcomeCodes.AwaitingTicket or QueueingOutcomeCodes.AwaitingPreviousEvent or QueueingOutcomeCodes.CreationInProgress))
            {
                break;
            }

            if (!QueueingEventValidator.TryValidate(
                    System.Text.Json.JsonSerializer.Deserialize<QueueingEventRequest>(waiting.Payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!,
                    options, DateTime.MaxValue.AddDays(-1), out var message, out _))
            {
                waiting.MarkRejected(QueueingOutcomeCodes.ProcessingFailed, "The stored event no longer validates.", waiting.TicketId, Now());
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            var response = await ProcessContainedAsync(waiting, message!, cancellationToken);
            if (response.Status is QueueingResultStatus.Pending or QueueingResultStatus.Busy)
            {
                break;
            }
        }
    }

    // ------------------------------------------------------------------ application

    private async Task<QueueingEventResponse> ApplyAsync(
        QueueingEvent row, QueueingEventMessage message, DateTime effectiveAtUtc, CancellationToken cancellationToken)
    {
        var ticket = await store.FindTicketAsync(TicketIntegrationSources.QueueingSystem, message.ExternalTicketId, cancellationToken);

        if (message.EventType == QueueingEventType.TicketCreated)
        {
            return ticket is not null
                ? await ConfirmExistingCreationAsync(row, message, ticket, cancellationToken)
                : await CreateTicketAsync(row, message, cancellationToken);
        }

        if (ticket is null)
        {
            // Out of order: the event arrived before its ticket exists. Kept, and applied the moment TicketCreated is processed.
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.AwaitingTicket,
                $"No TigerCS ticket exists yet for queue ticket '{message.ExternalTicketId}'. The event is stored and will be applied when TicketCreated arrives.",
                null, cancellationToken);
        }

        var link = await EnsureLinkAsync(ticket, message, cancellationToken);

        if (QueueingEventTypes.IsAdditive(message.EventType))
        {
            return message.EventType == QueueingEventType.TicketCommentAdded
                ? await AddCommentAsync(row, message, ticket, cancellationToken)
                : await RecordRecallAsync(row, message, ticket, cancellationToken);
        }

        // Strict per-ticket order: while an EARLIER state-changing event of this queue ticket is unresolved (a mapping is missing, an employee is not set up, the event Failed,
        // or it is still being processed) this one is held behind it - never applied around it, never skipped. It is released, in sequence order, the moment the earlier one
        // settles (ReleaseHeldFollowersAsync), and it consumes no retry attempt meanwhile. Other queue tickets are unaffected.
        var blocker = await store.FindEarlierUnsettledAsync(message.ExternalTicketId, message.EventSequence, row.EventId, cancellationToken);
        if (blocker is not null)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.AwaitingPreviousEvent,
                $"Held behind event sequence {blocker.EventSequence} ({blocker.EventType}) of queue ticket '{message.ExternalTicketId}', which is {blocker.Status}"
                + (blocker.OutcomeCode is null ? string.Empty : $" ({blocker.OutcomeCode})")
                + ". Events are applied strictly in sequence order and nothing is skipped; this event is applied right after it.",
                ticket.TicketId, cancellationToken);
        }

        // State-changing events: the newest state wins.
        if (link.IsStale(message.EventSequence))
        {
            return await SupersedeAsync(row, message, ticket, link, cancellationToken);
        }

        if (link.State == QueueingTicketState.Cancelled && message.EventType == QueueingEventType.TicketClosed)
        {
            return await MirrorCancelledThenClosedAsync(row, message, ticket, link, cancellationToken);
        }

        if (link.IsTerminal && message.EventType is QueueingEventType.TicketAssigned or QueueingEventType.TicketReassigned)
        {
            // The Queueing System (source of truth) took a closed / cancelled queue ticket back into service: an integration-controlled reopen,
            // then the assignment itself. The employee is resolved FIRST so an unmapped employee never leaves a reopened, unassigned ticket behind.
            if (options.ServiceAccountEmployeeId is not { } reopenAccount || reopenAccount == Guid.Empty)
            {
                return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                    "QueueingIntegration:ServiceAccountEmployeeId is not configured.", ticket.TicketId, cancellationToken);
            }

            var assignee = await ResolveEmployeeAsync(message.AssignedUserId!, reopenAccount, cancellationToken);
            if (assignee.EmployeeId is null)
            {
                return await UnresolveAsync(row.EventId, assignee.Code!, assignee.Detail, ticket.TicketId, cancellationToken);
            }

            var reopenReason = $"{SourceLabel}: reopened - the queue ticket was assigned again after it was {link.State}"
                + (message.CounterId is null ? string.Empty : $" (counter {message.CounterId})");
            var reopened = await lifecycleAppService.ReopenForQueueingAsync(
                reopenAccount, ticket.TicketId, reopenReason, _ => link.ReopenToWaiting(), cancellationToken);
            if (reopened.Outcome != TicketMutationOutcome.Success)
            {
                store.DiscardPendingChanges();
                return await FromMutationFailureAsync(row.EventId, reopened.Outcome, ticket.TicketId, cancellationToken);
            }

            ticket = (await ticketRepository.GetByIdAsync(ticket.TicketId, cancellationToken))!;
            link = (await store.GetLinkAsync(ticket.TicketId, cancellationToken))!;
            return await AssignAsync(row, message, ticket, link, cancellationToken);
        }

        if (link.IsTerminal)
        {
            return await RejectAsync(row, ticket, QueueingOutcomeCodes.TicketTerminal,
                $"The queue ticket is already {link.State}; a {message.EventType} event cannot change it.", cancellationToken);
        }

        return message.EventType switch
        {
            QueueingEventType.TicketAssigned or QueueingEventType.TicketReassigned => await AssignAsync(row, message, ticket, link, cancellationToken),
            QueueingEventType.TicketReturned => await ReturnAsync(row, message, ticket, link, cancellationToken),
            QueueingEventType.TicketClosed => await CloseAsync(row, message, ticket, link, ResolutionOutcome.Resolved, QueueingTicketState.Closed, cancellationToken),
            QueueingEventType.TicketCancelled => await CloseAsync(row, message, ticket, link, ResolutionOutcome.Cancelled, QueueingTicketState.Cancelled, cancellationToken),
            _ => await RejectAsync(row, ticket, QueueingOutcomeCodes.NotEligible, $"Unsupported event {message.EventType}.", cancellationToken)
        };
    }

    // ------------------------------------------------------------------ TicketCreated

    private async Task<QueueingEventResponse> ConfirmExistingCreationAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, CancellationToken cancellationToken)
    {
        // One TigerCS ticket per queue ticket: a second TicketCreated (a new EventId for the same ExternalTicketId, or a retry after a crash between
        // the ticket commit and the link) creates nothing; it completes the link if it is missing and is answered with the existing ticket.
        await EnsureLinkAsync(ticket, message, cancellationToken);
        row.MarkProcessed(QueueingOutcomeCodes.TicketAlreadyExists, "The queue ticket already has a TigerCS ticket; nothing was created.", ticket.TicketId, Now());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Respond(row, ticket, QueueingResultStatus.Processed);
    }

    private async Task<QueueingEventResponse> CreateTicketAsync(QueueingEvent row, QueueingEventMessage message, CancellationToken cancellationToken)
    {
        if (options.ServiceAccountEmployeeId is not { } serviceAccount || serviceAccount == Guid.Empty
            || await employeeRepository.GetByIdAsync(serviceAccount, cancellationToken) is not { IsActive: true })
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                "QueueingIntegration:ServiceAccountEmployeeId is not configured or is not an active employee.", null, cancellationToken);
        }

        var channel = await channelRepository.GetByCodeAsync(options.ChannelCode, cancellationToken);
        if (channel is null || !channel.IsActive)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ChannelNotConfigured,
                $"No active channel has the code '{options.ChannelCode}' (QueueingIntegration:ChannelCode).", null, cancellationToken);
        }

        // Routing precedence:
        //  1. An explicit, active (company, ticket type) mapping decides the request type AND, through it, the department. One company may therefore
        //     serve ticket types of several departments; the company mapping is not consulted and need not exist.
        //  2. Only when no active explicit mapping exists does the company mapping supply the default department, and the department's General
        //     request type classifies the ticket.
        // An explicit mapping that exists but cannot be used (request type missing / inactive / no published workflow / inactive department / no
        // category pairing) HOLDS the event: it never silently falls back to the company default, which would misroute the ticket.
        Department department;
        RequestType? requestType;
        int? categoryId;
        string? classificationNote = null;
        var typeMapping = await store.GetTicketTypeMappingAsync(message.CompanyId!, message.TicketTypeId!, cancellationToken);
        if (typeMapping is { IsActive: true })
        {
            var resolution = await requestTypeRouting.ResolveAsync(typeMapping.RequestTypeId, null, cancellationToken);
            if (!resolution.IsValid || resolution.RequestType is null)
            {
                return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.RequestTypeUnusable,
                    $"Ticket type '{message.TicketTypeId}' of company '{message.CompanyId}' maps to request type {typeMapping.RequestTypeId}, which cannot be used: {resolution.Detail}", null, cancellationToken);
            }

            categoryId = await requestTypeRouting.ResolveCategoryIdAsync(resolution.RequestType, cancellationToken);
            if (categoryId is null)
            {
                return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.RequestTypeUnusable,
                    $"Request type '{resolution.RequestType.Name}' has no unique matching category in its department, so a complete classification is impossible.", null, cancellationToken);
            }

            // ResolveAsync has already proven the request type's department exists and is active.
            var requestTypeDepartment = await departmentRepository.GetByIdAsync(resolution.RequestType.DepartmentId, cancellationToken);
            if (requestTypeDepartment is not { IsActive: true })
            {
                return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.RequestTypeUnusable,
                    $"Request type '{resolution.RequestType.Name}' belongs to department {resolution.RequestType.DepartmentId}, which does not exist or is inactive.", null, cancellationToken);
            }

            department = requestTypeDepartment;
            requestType = resolution.RequestType;
        }
        else
        {
            var companyMapping = await store.GetCompanyMappingAsync(message.CompanyId!, cancellationToken);
            if (companyMapping is not { IsActive: true })
            {
                return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.CompanyNotMapped,
                    $"Ticket type '{message.TicketTypeId}' of company '{message.CompanyId}' has no explicit request-type mapping and the company has no default department. Add the ticket type to QueueingTicketTypeMappings or the company to QueueingCompanyMappings; the event is retried automatically.", null, cancellationToken);
            }

            var defaultDepartment = await departmentRepository.GetByIdAsync(companyMapping.DepartmentId, cancellationToken);
            if (defaultDepartment is not { IsActive: true })
            {
                return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.CompanyNotMapped,
                    $"Queueing company '{message.CompanyId}' is mapped to department {companyMapping.DepartmentId}, which does not exist or is inactive.", null, cancellationToken);
            }

            department = defaultDepartment;

            // The company's default department is known (never guessed) but the ticket type has no usable request-type mapping: the ticket takes the
            // department's own configured General request type - the existing default-classification rule - and the gap is logged and audited. A
            // department with no General configured (or one the rule excludes) holds the event instead: no request type is ever invented.
            var general = defaultClassification is null || DefaultClassificationService.IsExcluded(department)
                ? null
                : (await defaultClassification.ResolveForDepartmentAsync(department.DepartmentId, cancellationToken)).Value;
            if (general is null)
            {
                logger.LogWarning(
                    "Queueing ticket type {TicketTypeId} of company {CompanyId} has no request-type mapping and department {DepartmentId} has no usable General request type; event {EventId} is held.",
                    message.TicketTypeId, message.CompanyId, department.DepartmentId, row.EventId);
                return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.TicketTypeNotMapped,
                    $"Queueing ticket type '{message.TicketTypeId}' of company '{message.CompanyId}' is not mapped to a request type, and department '{department.Code}' has no usable General request type to fall back to. "
                    + "Add the mapping to QueueingTicketTypeMappings (or configure the department's General request type); the event is retried automatically.",
                    null, cancellationToken);
            }

            logger.LogWarning(
                "Queueing ticket type {TicketTypeId} of company {CompanyId} has no request-type mapping; ticket for {ExternalTicketId} uses department {DepartmentId}'s General request type.",
                message.TicketTypeId, message.CompanyId, message.ExternalTicketId, department.DepartmentId);
            requestType = general.RequestType;
            categoryId = general.Category.CategoryId;
            classificationNote = $"RequestTypeMapping=Missing(company={message.CompanyId};ticketType={message.TicketTypeId});Applied=General(RequestTypeId={general.RequestType.RequestTypeId})";
        }

        var priority = await ResolvePriorityAsync(cancellationToken);
        if (priority is null)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.PriorityNotConfigured,
                $"QueueingIntegration:DefaultTicketPriority '{options.DefaultTicketPriority}' matches no priority.", null, cancellationToken);
        }

        var phone = CustomerPhoneNumber.FromTelephonyAddress(message.CustomerPhone);
        var lookup = await LookUpCustomerAsync(phone, cancellationToken);

        var intake = await intakeRecordAppService.CreateAsync(
            serviceAccount,
            new CreateIntakeRecordRequestDto(
                ChannelId: channel.ChannelId.ToString(CultureInfo.InvariantCulture),
                PhoneNumber: phone ?? string.Empty,
                DepartmentId: department.DepartmentId,
                IsUnitRelated: false,
                RawUnitNumberEntered: null,
                PriorityHint: null),
            cancellationToken,
            enforceChannelPhoneRequirement: false);
        if (intake.Outcome != IntakeRecordOutcome.Success || intake.Response is null)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ProcessingFailed,
                $"The intake record could not be created ({intake.Outcome}).", null, cancellationToken);
        }

        var customerName = FirstNonBlank(message.CustomerName, lookup.CustomerName);
        var request = new CreateTicketRequestDto(
            IntakeRecordId: intake.Response.IntakeRecordId,
            UnitReferenceId: null,
            ContactReferenceId: null,
            CategoryId: categoryId,
            PriorityId: priority.PriorityId,
            RequestSummary: BuildSummary(message, requestType, customerName),
            DepartmentId: department.DepartmentId,
            RequestTypeId: requestType?.RequestTypeId,
            ExternalOrigin: new ExternalTicketOriginDto(TicketIntegrationSources.QueueingSystem, message.ExternalTicketId, message.QueueNumber));

        // The queue-state row, the customer contact, the history entry and the inbox row are staged INSIDE ticket creation, immediately before its
        // final save: they commit in the very transaction that creates the ticket and roll back with it. There is no moment at which a ticket exists
        // without its queue state, so no later event is ever needed to repair an incomplete creation.
        Ticket? created = null;
        async Task StageQueueStateAsync(Ticket newTicket, CancellationToken token)
        {
            created = newTicket;
            await store.AddLinkAsync(new QueueingTicketLink(
                newTicket.TicketId, message.ExternalTicketId, message.QueueNumber, message.CompanyId, message.TicketTypeId,
                message.EventSequence, message.OccurredAtUtc, Now()), token);

            await SaveCustomerContactIfNewAsync(phone, customerName, message.CustomerEmail, serviceAccount, token);

            await auditWriter.WriteAsync(
                serviceAccount, QueueingAuditActions.TicketCreated, "Ticket", newTicket.TicketId.ToString(CultureInfo.InvariantCulture),
                beforeValue: null,
                afterValue: $"TicketId={newTicket.TicketId};{EventFacts(message)};QueueNumber={message.QueueNumber};CompanyId={message.CompanyId};TicketTypeId={message.TicketTypeId};"
                    + $"DepartmentId={department.DepartmentId};RequestTypeId={requestType?.RequestTypeId.ToString(CultureInfo.InvariantCulture) ?? "(none)"};"
                    + $"CompanyName={message.CompanyName};TicketTypeName={message.TicketTypeName};ProjectCode={message.ProjectCode};ProjectName={message.ProjectName};UnitCode={message.UnitCode};CustomerEmail={message.CustomerEmail};"
                    + $"Channel={channel.Code};CustomerLookup={lookup.Status};{classificationNote ?? "RequestTypeMapping=Resolved"}",
                Guid.NewGuid(), token);

            row.MarkProcessed(QueueingOutcomeCodes.TicketCreated, null, newTicket.TicketId, Now());
        }

        TicketCreationResult creation = TicketCreationResult.Failure(TicketCreationOutcome.TicketNumberCollision);
        for (var attempt = 0; attempt < 3 && creation.Outcome == TicketCreationOutcome.TicketNumberCollision; attempt++)
        {
            creation = await ticketCreationAppService.CreateAsync(serviceAccount, request, cancellationToken, beforeCommit: StageQueueStateAsync);
            if (creation.Outcome == TicketCreationOutcome.TicketNumberCollision
                && await store.FindTicketAsync(TicketIntegrationSources.QueueingSystem, message.ExternalTicketId, cancellationToken) is { } winner)
            {
                // The unique (source, external id) index: a concurrent creation of the same queue ticket won. Answer with its ticket.
                store.DiscardPendingChanges();
                var existing = await store.GetEventAsync(row.EventId, cancellationToken) ?? row;
                return await ConfirmExistingCreationAsync(existing, message, winner, cancellationToken);
            }
        }

        if (creation.Outcome != TicketCreationOutcome.Success || creation.Response is null || created is null)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ProcessingFailed,
                $"Ticket creation was refused ({creation.Outcome}).", null, cancellationToken);
        }

        return Respond(row, created, QueueingResultStatus.Processed) with { CreatedTicket = true };
    }

    // ------------------------------------------------------------------ assignment

    private async Task<QueueingEventResponse> AssignAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, QueueingTicketLink link, CancellationToken cancellationToken)
    {
        if (options.ServiceAccountEmployeeId is not { } serviceAccount || serviceAccount == Guid.Empty)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                "QueueingIntegration:ServiceAccountEmployeeId is not configured.", ticket.TicketId, cancellationToken);
        }

        var employee = await ResolveEmployeeAsync(message.AssignedUserId!, serviceAccount, cancellationToken);
        if (employee.EmployeeId is not { } employeeId)
        {
            // The ticket is NOT assigned to anybody else, and not to the integration account: it stays exactly as it was.
            return await UnresolveAsync(row.EventId, employee.Code!, employee.Detail, ticket.TicketId, cancellationToken);
        }

        var reason = message.Reason ?? (message.EventType == QueueingEventType.TicketReassigned ? "Reassigned in the Queueing System" : "Assigned in the Queueing System");
        var note = $"{SourceLabel}: {reason}" + (message.CounterId is null ? string.Empty : $" (counter {message.CounterId})")
            + (message.PerformedByUserId is null ? string.Empty : $" by queue user {message.PerformedByUserId}");

        // Step 1 - the assignment itself, by the existing assignment service, to the REAL employee. Skipped when they already own the ticket (redelivery / resume).
        if (ticket.CurrentOwnerEmployeeId != employeeId)
        {
            var assigned = await assignmentAppService.AssignAsync(
                serviceAccount, [], ticket.TicketId,
                new AssignTicketRequestDto(
                    employeeId, ticket.RowVersion,
                    message.EventType == QueueingEventType.TicketReassigned && ticket.CurrentOwnerEmployeeId is not null ? TicketTransferReason.Other : null,
                    note),
                cancellationToken, ticketAccessScope: true);
            if (assigned.Outcome != TicketMutationOutcome.Success)
            {
                store.DiscardPendingChanges();
                return await FromMutationFailureAsync(row.EventId, assigned.Outcome, ticket.TicketId, cancellationToken);
            }

            ticket = (await ticketRepository.GetByIdAsync(ticket.TicketId, cancellationToken))!;
        }

        // Step 2 - the work starts: the existing Open -> InProgress transition, performed AS the receiving employee (their own authority as the owner),
        // with the integration's state staged on this same save. Assignment is deliberately not a first response: nothing here touches the SLA first-response clock.
        link = (await store.GetLinkAsync(ticket.TicketId, cancellationToken))!;
        StageApplied(link, row, message, QueueingTicketState.Assigned, QueueingOutcomeCodes.Applied, note);
        await WriteEventAuditAsync(serviceAccount, ticket, message, QueueingAuditActions.EventApplied, $"Owner={employeeId}", cancellationToken);

        if (ticket.TicketStatus == TicketStatus.Open)
        {
            var roles = await roleReader.GetRolesAsync(employeeId, cancellationToken);
            var started = await lifecycleAppService.ChangeStatusAsync(
                employeeId, roles, ticket.TicketId, new ChangeStatusRequestDto(nameof(TicketStatus.InProgress), ticket.RowVersion), cancellationToken);
            if (started.Outcome != TicketMutationOutcome.Success)
            {
                store.DiscardPendingChanges();
                return await FromMutationFailureAsync(row.EventId, started.Outcome, ticket.TicketId, cancellationToken);
            }
        }
        else
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var current = (await ticketRepository.GetByIdAsync(ticket.TicketId, cancellationToken))!;
        return Respond(row, current, QueueingResultStatus.Processed);
    }

    private async Task<QueueingEventResponse> ReturnAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, QueueingTicketLink link, CancellationToken cancellationToken)
    {
        if (options.ServiceAccountEmployeeId is not { } serviceAccount || serviceAccount == Guid.Empty)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                "QueueingIntegration:ServiceAccountEmployeeId is not configured.", ticket.TicketId, cancellationToken);
        }

        var reason = message.Reason ?? "Returned to the waiting queue in the Queueing System";
        await WriteEventAuditAsync(serviceAccount, ticket, message, QueueingAuditActions.TicketReturned,
            $"PreviousOwner={ticket.CurrentOwnerEmployeeId?.ToString() ?? "none"};Reason={reason}", cancellationToken);

        var result = await assignmentAppService.ReturnToDepartmentQueueAsync(
            serviceAccount, ticket.TicketId, reason,
            _ => StageApplied(link, row, message, QueueingTicketState.Waiting, QueueingOutcomeCodes.Applied, reason),
            cancellationToken);
        if (result.Outcome != TicketMutationOutcome.Success)
        {
            store.DiscardPendingChanges();
            return await FromMutationFailureAsync(row.EventId, result.Outcome, ticket.TicketId, cancellationToken);
        }

        return Respond(row, (await ticketRepository.GetByIdAsync(ticket.TicketId, cancellationToken))!, QueueingResultStatus.Processed);
    }

    // ------------------------------------------------------------------ closure

    private async Task<QueueingEventResponse> CloseAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, QueueingTicketLink link,
        ResolutionOutcome outcome, QueueingTicketState finalState, CancellationToken cancellationToken)
    {
        if (options.ServiceAccountEmployeeId is not { } serviceAccount || serviceAccount == Guid.Empty)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                "QueueingIntegration:ServiceAccountEmployeeId is not configured.", ticket.TicketId, cancellationToken);
        }

        var reason = message.Reason ?? message.Comment
            ?? (outcome == ResolutionOutcome.Cancelled ? "Cancelled in the Queueing System" : "Served and closed in the Queueing System");

        var endReason = outcome == ResolutionOutcome.Cancelled ? "QueueTicketCancelled" : "QueueTicketClosed";

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            // Already closed in TigerCS (for example by an allowed manual change): the queue's state still has to be mirrored, and nothing is written twice.
            foreach (var interaction in (await interactionRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Where(i => !i.IsEnded))
            {
                interaction.End(EventNow(), endReason);
            }

            StageApplied(link, row, message, finalState, QueueingOutcomeCodes.AlreadyInState, "The ticket was already closed.");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Respond(row, ticket, QueueingResultStatus.Processed);
        }

        // Who closed it in the queue: PerformedByUserId through the same user mapping as assignment. Resolved and active -> that employee is the closure actor.
        // Not identifiable -> the closure stays a Queueing System action; it is NEVER attributed to the integration account and never guessed.
        var closedBy = message.PerformedByUserId is { } performer
            ? (await ResolveEmployeeAsync(performer, serviceAccount, cancellationToken)).EmployeeId
            : null;

        var liveInteractions = (await interactionRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Where(i => !i.IsEnded).ToList();

        var detail = $"{EventFacts(message)};PerformedByUserId={message.PerformedByUserId ?? "(none)"};CounterId={message.CounterId ?? "(none)"}";
        var closed = await lifecycleAppService.CloseForQueueingAsync(
            serviceAccount, ticket.TicketId, outcome, $"{SourceLabel}: {reason}", detail,
            _ =>
            {
                // Staged on the closure's own unit of work: the interaction ends in the very transaction that closes the ticket.
                foreach (var interaction in liveInteractions)
                {
                    interaction.End(EventNow(), endReason);
                }

                StageApplied(link, row, message, finalState, QueueingOutcomeCodes.Applied, reason);
            },
            cancellationToken,
            closedBy);
        if (closed.Outcome != TicketMutationOutcome.Success)
        {
            store.DiscardPendingChanges();
            return await FromMutationFailureAsync(row.EventId, closed.Outcome, ticket.TicketId, cancellationToken);
        }

        return Respond(row, (await ticketRepository.GetByIdAsync(ticket.TicketId, cancellationToken))!, QueueingResultStatus.Processed);
    }

    /// <summary>
    /// The Queueing System closed, as served, a queue ticket that was cancelled (its CloseSpecificTicket has no guard, so CANCELED -&gt; CLOSED really happens): TigerCS mirrors
    /// the transition - the same closed ticket changes its outcome Cancelled -&gt; Resolved, history and audit kept. The reverse (Closed -&gt; Cancelled) cannot occur in the
    /// Queueing System (cancelling is only possible from WAITING / INPROGRESS) and stays rejected.
    /// </summary>
    private async Task<QueueingEventResponse> MirrorCancelledThenClosedAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, QueueingTicketLink link, CancellationToken cancellationToken)
    {
        if (options.ServiceAccountEmployeeId is not { } serviceAccount || serviceAccount == Guid.Empty)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                "QueueingIntegration:ServiceAccountEmployeeId is not configured.", ticket.TicketId, cancellationToken);
        }

        var reason = message.Reason ?? message.Comment ?? "Closed as served in the Queueing System after it was cancelled";
        var closedBy = message.PerformedByUserId is { } performer
            ? (await ResolveEmployeeAsync(performer, serviceAccount, cancellationToken)).EmployeeId
            : null;
        var detail = $"{EventFacts(message)};PerformedByUserId={message.PerformedByUserId ?? "(none)"};CounterId={message.CounterId ?? "(none)"};PreviousState=Cancelled";

        var result = await lifecycleAppService.ChangeQueueClosureOutcomeAsync(
            serviceAccount, ticket.TicketId, $"{SourceLabel}: {reason}", detail,
            _ => StageApplied(link, row, message, QueueingTicketState.Closed, QueueingOutcomeCodes.Applied, reason),
            cancellationToken, closedBy);
        if (result.Outcome != TicketMutationOutcome.Success)
        {
            store.DiscardPendingChanges();
            return await FromMutationFailureAsync(row.EventId, result.Outcome, ticket.TicketId, cancellationToken);
        }

        return Respond(row, (await ticketRepository.GetByIdAsync(ticket.TicketId, cancellationToken))!, QueueingResultStatus.Processed);
    }

    // ------------------------------------------------------------------ additive events

    private async Task<QueueingEventResponse> AddCommentAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, CancellationToken cancellationToken)
    {
        if (options.ServiceAccountEmployeeId is not { } serviceAccount || serviceAccount == Guid.Empty)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                "QueueingIntegration:ServiceAccountEmployeeId is not configured.", ticket.TicketId, cancellationToken);
        }

        // Authored by the employee who wrote it in the queue when they can be identified; otherwise by the integration account, and the text says who.
        (Guid? EmployeeId, string? Code, string? Detail)? author = message.PerformedByUserId is { } performer
            ? await ResolveEmployeeAsync(performer, serviceAccount, cancellationToken)
            : null;
        var text = author?.EmployeeId is not null
            ? message.Comment!
            : $"[{SourceLabel}{(message.PerformedByUserId is null ? string.Empty : $" - queue user {message.PerformedByUserId}")}] {message.Comment}";

        await noteRepository.AddAsync(new TicketNote(ticket.TicketId, text, author?.EmployeeId ?? serviceAccount, EventNow()), cancellationToken);
        await WriteEventAuditAsync(serviceAccount, ticket, message, QueueingAuditActions.CommentAdded, "Note added to the ticket", cancellationToken);
        row.MarkProcessed(QueueingOutcomeCodes.Applied, null, ticket.TicketId, Now());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Respond(row, ticket, QueueingResultStatus.Processed);
    }

    private async Task<QueueingEventResponse> RecordRecallAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, CancellationToken cancellationToken)
    {
        if (options.ServiceAccountEmployeeId is not { } serviceAccount || serviceAccount == Guid.Empty)
        {
            return await UnresolveAsync(row.EventId, QueueingOutcomeCodes.ServiceAccountNotConfigured,
                "QueueingIntegration:ServiceAccountEmployeeId is not configured.", ticket.TicketId, cancellationToken);
        }

        // A recall (the customer was called again) changes nothing about the ticket: it is a history entry, nothing else.
        await WriteEventAuditAsync(serviceAccount, ticket, message, QueueingAuditActions.TicketRecalled,
            $"Reason={message.Reason ?? "(none)"}", cancellationToken);
        row.MarkProcessed(QueueingOutcomeCodes.Applied, null, ticket.TicketId, Now());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Respond(row, ticket, QueueingResultStatus.Processed);
    }

    // ------------------------------------------------------------------ ordering outcomes

    private async Task<QueueingEventResponse> SupersedeAsync(
        QueueingEvent row, QueueingEventMessage message, Ticket ticket, QueueingTicketLink link, CancellationToken cancellationToken)
    {
        var detail = $"Sequence {message.EventSequence} is not newer than the applied sequence {link.LastAppliedSequence}; the ticket was not changed.";
        if (options.ServiceAccountEmployeeId is { } serviceAccount && serviceAccount != Guid.Empty)
        {
            await WriteEventAuditAsync(serviceAccount, ticket, message, QueueingAuditActions.EventSuperseded, detail, cancellationToken);
        }

        row.MarkSuperseded(QueueingOutcomeCodes.StaleEvent, detail, ticket.TicketId, Now());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Respond(row, ticket, QueueingResultStatus.Superseded);
    }

    private async Task<QueueingEventResponse> RejectAsync(
        QueueingEvent row, Ticket ticket, string code, string detail, CancellationToken cancellationToken)
    {
        row.MarkRejected(code, detail, ticket.TicketId, Now());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Respond(row, ticket, QueueingResultStatus.Rejected);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The real time: for the integration's own bookkeeping (processed at, link created at, retry schedule).</summary>
    private DateTime Now() => IntegrationEventScope.RealAtUtc ?? timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The event's original time (inside a scope): for records that describe the ticket's own history.</summary>
    private DateTime EventNow() => timeProvider.GetUtcNow().UtcDateTime;

    private void StageApplied(
        QueueingTicketLink link, QueueingEvent row, QueueingEventMessage message, QueueingTicketState state, string outcome, string? detail)
    {
        link.Apply(state, message.EventSequence, message.OccurredAtUtc, message.CounterId, message.AssignedUserId);
        row.MarkProcessed(outcome, detail, link.TicketId, Now());
    }

    private async Task<QueueingTicketLink> EnsureLinkAsync(Ticket ticket, QueueingEventMessage message, CancellationToken cancellationToken)
    {
        if (await store.GetLinkAsync(ticket.TicketId, cancellationToken) is { } existing)
        {
            if (message.EventType == QueueingEventType.TicketCreated)
            {
                existing.ReconcileCreation(message.EventSequence, message.OccurredAtUtc);
            }

            return existing;
        }

        // The ticket committed but the link did not (a crash in between): rebuilt from the ticket itself.
        var link = new QueueingTicketLink(
            ticket.TicketId, ticket.ExternalTicketId ?? message.ExternalTicketId, ticket.ExternalReference,
            message.CompanyId, message.TicketTypeId,
            message.EventType == QueueingEventType.TicketCreated ? message.EventSequence : 0,
            message.EventType == QueueingEventType.TicketCreated ? message.OccurredAtUtc : ticket.CreatedAtUtc, Now());
        await store.AddLinkAsync(link, cancellationToken);
        return link;
    }

    private async Task<(Guid? EmployeeId, string? Code, string? Detail)> ResolveEmployeeAsync(
        string externalUserId, Guid serviceAccount, CancellationToken cancellationToken)
    {
        Guid? employeeId;
        if (options.UserLookupMode == QueueingUserLookupMode.EmployeeId)
        {
            employeeId = Guid.TryParse(externalUserId, out var parsed) && parsed != Guid.Empty ? parsed : null;
        }
        else
        {
            var mapping = await store.GetUserMappingAsync(externalUserId, cancellationToken);
            employeeId = mapping is { IsActive: true } ? mapping.EmployeeId : null;
        }

        if (employeeId is null)
        {
            return (null, QueueingOutcomeCodes.UserNotMapped,
                $"Queue user '{externalUserId}' is not mapped to a TigerCS employee. Add it to QueueingUserMappings; the event is retried automatically. The ticket was not assigned to anyone.");
        }

        if (employeeId == serviceAccount)
        {
            return (null, QueueingOutcomeCodes.UserNotMapped,
                $"Queue user '{externalUserId}' resolves to the integration account, which is never a ticket assignee.");
        }

        var employee = await employeeRepository.GetByIdAsync(employeeId.Value, cancellationToken);
        if (employee is null)
        {
            return (null, QueueingOutcomeCodes.UserNotMapped, $"Queue user '{externalUserId}' maps to employee {employeeId}, who does not exist.");
        }

        return employee.IsActive
            ? (employee.EmployeeId, null, null)
            : (null, QueueingOutcomeCodes.UserInactive, $"Queue user '{externalUserId}' maps to a deactivated employee. The ticket was not assigned.");
    }

    private async Task<Priority?> ResolvePriorityAsync(CancellationToken cancellationToken)
    {
        var name = options.DefaultTicketPriority;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (await priorityRepository.GetByNameAsync(name, cancellationToken) is { } byName)
        {
            return byName;
        }

        return PriorityAliases.TryResolve(name, out var level) ? await priorityRepository.GetByIdAsync((byte)level, cancellationToken) : null;
    }

    private async Task<(string Status, string? CustomerName)> LookUpCustomerAsync(string? phone, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return ("NoPhoneSupplied", null);
        }

        try
        {
            // The same lookup the New Ticket wizard and Genesys use. Enrichment only: it never gates the ticket, and a match is never auto-selected as the
            // verified customer - that stays an agent's explicit act.
            var result = await customerSearchAppService.SearchByPhoneAsync(phone, cancellationToken);
            var names = result.CrmBuyers.Select(b => b.Customer.FullNameEnglish ?? b.Customer.FullNameArabic)
                .Concat(result.ExternalSources.SelectMany(s => s.Customers).Select(c => (string?)c.DisplayName))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .DistinctBy(n => n!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var found = result.CrmStatus == "Found" || result.ExternalSources.Any(s => s.Status == "Found");
            if (names.Count > 1)
            {
                return ($"Found(Ambiguous:{names.Count} customers share this number)", null);
            }

            if (found)
            {
                return ("Found", names.FirstOrDefault());
            }

            var failed = result.CrmStatus == "Failed" || result.ExternalSources.Any(s => s.Status == "Failed");
            return (failed ? "LookupFailed" : $"NotFound(Crm={result.CrmStatus})", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ("LookupFailed(TimedOut)", null);
        }
        catch (Exception ex)
        {
            return ($"LookupFailed({ex.GetType().Name})", null);
        }
    }

    private async Task SaveCustomerContactIfNewAsync(
        string? phone, string? name, string? email, Guid serviceAccount, CancellationToken cancellationToken)
    {
        // Only fills a gap: contact details an agent already typed for this number are never overwritten.
        var key = CustomerPhoneNumber.Normalize(phone);
        if (key.Length == 0 || key.Length > CustomerContactDetail.PhoneKeyMaxLength || string.IsNullOrWhiteSpace(name)
            || name.Trim().Length > CustomerContactDetail.NameMaxLength
            || await contactDetailRepository.GetByPhoneKeyAsync(key, cancellationToken) is not null)
        {
            return;
        }

        var safeEmail = string.IsNullOrWhiteSpace(email) || email.Trim().Length > CustomerContactDetail.EmailMaxLength ? null : email.Trim();
        var now = Now();
        await contactDetailRepository.AddAsync(new CustomerContactDetail(key, name.Trim(), safeEmail, serviceAccount, now), cancellationToken);

        // Customer information created by the Queueing integration is audited like any other change - source Integration, actor the
        // service account - in the same unit of work as the contact record itself.
        var identity = CustomerIdentity.Phone(key);
        var anchor = await profileOverrides.GetByKeyAsync(identity.Key, cancellationToken);
        if (anchor is null)
        {
            anchor = new CustomerProfileOverride((identity.Key), (int)identity.Kind, null, null, null, identity.PhoneNumber, serviceAccount, now);
            await profileOverrides.AddAsync(anchor, cancellationToken);
        }

        await profileAudit.RecordAsync(
            anchor.CustomerProfileId, identity.Key,
            new CustomerChangeContext(serviceAccount, CustomerChangeSources.Integration, "Created from a Queueing System event", null, Guid.NewGuid()),
            [new CustomerFieldChange(CustomerProfileFields.FullName, null, name.Trim()), new CustomerFieldChange(CustomerProfileFields.Email, null, safeEmail)],
            cancellationToken);
    }

    private static string BuildSummary(QueueingEventMessage message, RequestType? requestType, string? customerName)
    {
        var summary = $"Queue ticket {message.QueueNumber} (Queueing System)";
        if (requestType is not null)
        {
            summary += $" - {requestType.Name}";
        }

        if (!string.IsNullOrWhiteSpace(customerName))
        {
            summary += $" - {customerName.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(message.ProjectName ?? message.ProjectCode))
        {
            summary += $" - Project {(message.ProjectName ?? message.ProjectCode)!.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(message.UnitCode))
        {
            summary += $" - Unit {message.UnitCode.Trim()}";
        }

        return summary.Length <= 500 ? summary : summary[..500];
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string EventFacts(QueueingEventMessage message) =>
        $"EventId={message.EventId};EventType={message.EventType};ExternalTicketId={message.ExternalTicketId};EventSequence={message.EventSequence};OccurredAtUtc={message.OccurredAtUtc:O}"
        + (message.PerformedByUserId is null ? string.Empty : $";PerformedByUserId={message.PerformedByUserId}")
        + (message.CounterId is null ? string.Empty : $";CounterId={message.CounterId}");

    private Task WriteEventAuditAsync(
        Guid serviceAccount, Ticket ticket, QueueingEventMessage message, string action, string detail, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            serviceAccount, action, "Ticket", ticket.TicketId.ToString(CultureInfo.InvariantCulture),
            beforeValue: $"TicketId={ticket.TicketId};Status={ticket.TicketStatus}",
            afterValue: $"TicketId={ticket.TicketId};{EventFacts(message)};{detail}",
            Guid.NewGuid(), cancellationToken);

    // ------------------------------------------------------------------ results

    private async Task<QueueingEventResponse> UnresolveAsync(
        string eventId, string code, string? detail, long? ticketId, CancellationToken cancellationToken)
    {
        // Re-read so the failure is recorded on a clean row, whatever was staged before.
        store.DiscardPendingChanges();
        var row = (await store.GetEventAsync(eventId, cancellationToken))!;
        var now = Now();
        switch (QueueingOutcomeCodes.Classify(code))
        {
            case QueueingFailureClass.Ordering:
                // Waiting for an earlier event (or the ticket's creation): no retry of its own, no attempt consumed - released in order when that event settles.
                row.MarkHeldForOrdering(code, detail, ticketId);
                break;

            case var failureClass:
                // Configuration holds (a mapping / employee / setting is missing) retry indefinitely at a capped exponential backoff - they never "give up", monitoring
                // alerts on their age. Transient and unexplained failures are bounded: after MaxRetryAttempts the event is Failed and waits for an administrator.
                var attempt = row.AttemptCount + 1;
                var delay = Math.Min(options.MaxRetryDelaySeconds, options.RetryBaseDelaySeconds * Math.Pow(2, Math.Min(attempt - 1, 20)));
                int? bound = failureClass == QueueingFailureClass.Configuration ? null : Math.Max(1, options.MaxRetryAttempts);
                row.MarkUnresolved(code, detail, ticketId, now, now.AddSeconds(delay), bound);
                if (row.Status == QueueingEventStatus.Failed)
                {
                    logger.LogError(
                        "Queueing event {EventId} ({EventType}) for {ExternalTicketId} FAILED after {Attempts} attempts ({OutcomeCode}). Every later event of the queue ticket is held until an administrator reprocesses it.",
                        row.EventId, row.EventType, row.ExternalTicketId, row.AttemptCount, code);
                }

                break;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await FromRowAsync(row, QueueingResultStatus.Pending, cancellationToken);
    }

    private async Task<QueueingEventResponse> FromMutationFailureAsync(
        string eventId, TicketMutationOutcome outcome, long ticketId, CancellationToken cancellationToken)
    {
        var (code, detail) = outcome switch
        {
            TicketMutationOutcome.EmployeeNotInDepartment => (QueueingOutcomeCodes.UserNotInDepartment,
                "The receiving employee is not an active member of the ticket's department. Add them to the department; the event is retried automatically. The ticket was not assigned to anyone else."),
            TicketMutationOutcome.ConcurrencyConflict => (QueueingOutcomeCodes.ConcurrentUpdate,
                "The ticket was changed by another request while this event was being applied. It will be retried."),
            _ => (QueueingOutcomeCodes.NotEligible, $"TigerCS refused the change ({outcome}). The event is kept and retried.")
        };
        return await UnresolveAsync(eventId, code, detail, ticketId, cancellationToken);
    }

    private async Task<QueueingEventResponse> FromRowAsync(QueueingEvent row, QueueingResultStatus status, CancellationToken cancellationToken)
    {
        Ticket? ticket = row.TicketId is { } id ? await ticketRepository.GetByIdAsync(id, cancellationToken) : null;
        var mapped = status == QueueingResultStatus.Duplicate
            ? status
            : row.Status switch
            {
                // A Failed event is still unresolved (nothing was applied and everything behind it waits): it is stored, TigerCS will not retry it by itself, and the
                // sender must not resend it - an administrator reprocesses it. Hence Pending, not retryable.
                QueueingEventStatus.Unresolved or QueueingEventStatus.Received or QueueingEventStatus.Failed => QueueingResultStatus.Pending,
                _ => status
            };
        return new QueueingEventResponse(
            row.EventId, row.ExternalTicketId, mapped, row.OutcomeCode, row.TicketId, ticket?.TicketNumber, ticket?.TicketStatus.ToString(),
            DetailFor(row), Retryable: row.Status is QueueingEventStatus.Unresolved or QueueingEventStatus.Received);
    }

    private static QueueingEventResponse Respond(QueueingEvent row, Ticket ticket, QueueingResultStatus status) =>
        new(row.EventId, row.ExternalTicketId, status, row.OutcomeCode, ticket.TicketId, ticket.TicketNumber, ticket.TicketStatus.ToString(), DetailFor(row));

    /// <summary>The history note of a plain success stays in TigerCS's history; the response explains only what the caller needs to act on.</summary>
    private static string? DetailFor(QueueingEvent row) =>
        row.OutcomeCode is QueueingOutcomeCodes.Applied or QueueingOutcomeCodes.TicketCreated ? null : row.OutcomeDetail;

    private static QueueingEventResponse Busy(QueueingEventMessage message, string detail) =>
        new(message.EventId, message.ExternalTicketId, QueueingResultStatus.Busy, QueueingOutcomeCodes.ConcurrentUpdate, Detail: detail, Retryable: true);
}

/// <summary>The audit vocabulary of the queueing integration.</summary>
public static class QueueingAuditActions
{
    public const string TicketCreated = "QueueingTicketCreated";
    public const string EventApplied = "QueueingEventApplied";
    public const string TicketReturned = "QueueingTicketReturned";
    public const string TicketRecalled = "QueueingTicketRecalled";
    public const string CommentAdded = "QueueingCommentAdded";
    public const string EventSuperseded = "QueueingEventSuperseded";

    /// <summary>An administrator restarted a stuck (Failed or held) event. Entity type <c>QueueingEvent</c>, entity id the EventId.</summary>
    public const string EventReprocessed = "QueueingEventReprocessed";
}
