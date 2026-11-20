using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Delivery events and customer responses reported for a reminder.
///
/// <para>
/// <b>Idempotent.</b> (reminder, eventId) is unique. A resend with the same
/// body returns the original result; the same eventId or Idempotency-Key with
/// a different body is <c>409 IdempotencyConflict</c>. Delivery statuses are
/// validated per channel and applied in occurrence order, so a delayed event
/// never overwrites a later or final status.
/// </para>
///
/// <para>
/// <b>A customer response in a conversation gets a ticket through the
/// existing Genesys ingestion</b> — <see cref="GenesysInquiryIngestionAppService"/>,
/// keyed by conversationId, routed by the configured Collections queue or
/// department code, created Unclassified under the existing classification
/// and lifecycle rules. AlreadyPaid requests verification and posts nothing;
/// a human request, an AI disconnection, a dispute or an explicit
/// requiresHumanFollowUp keeps human work outstanding through the existing
/// handoff. Nothing here resolves or closes a ticket.
/// </para>
///
/// <para>
/// <b>Durable.</b> The event and an outbox message commit together before the
/// ticket is attempted; if the ticket cannot be created yet the answer is
/// <c>202</c> with <c>ticketResult: "Pending"</c>, and the outbox retries
/// (<see cref="CollectionsResponseTicketHandler"/>).
/// </para>
/// </summary>
public sealed class CollectionsReminderOutcomeAppService(
    CollectionsOptions options,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    ICollectionsReminderRepository reminderRepository,
    ICollectionsUnitOfWork unitOfWork,
    IOutboxWriter outboxWriter,
    IAuditEntryWriter auditWriter,
    GenesysInquiryIngestionAppService ingestion,
    GenesysTicketUpdateAppService ticketUpdates,
    ILogger<CollectionsReminderOutcomeAppService> logger)
{
    public const string ResponseTicketEventType = "CollectionsReminderResponseTicket";

    public async Task<CollectionsResult<RecordReminderOutcomeResponseDto>> RecordAsync(
        CollectionsCaller caller, string reminderId, RecordReminderOutcomeRequestDto request, string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return Fail(CollectionsOutcome.Disabled);
        }

        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReportOutcomes)
        {
            return Fail(CollectionsOutcome.Forbidden, "Only a configured integration account may report reminder outcomes.");
        }

        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        var eventId = request.EventId?.Trim();
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > CollectionsReminderEvent.ExternalEventIdMaxLength
            || eventId.StartsWith(CollectionsReminderEvent.ReservedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(CollectionsOutcome.InvalidRequest,
                $"eventId is required, at most {CollectionsReminderEvent.ExternalEventIdMaxLength} characters, and may not start with '{CollectionsReminderEvent.ReservedPrefix}'.");
        }

        if (key is { Length: > CollectionsReminder.IdempotencyKeyMaxLength })
        {
            return Fail(CollectionsOutcome.InvalidRequest, $"Idempotency-Key is at most {CollectionsReminder.IdempotencyKeyMaxLength} characters.");
        }

        if (!CollectionsEnums.TryParse<ReminderChannel>(request.Channel, out var channel))
        {
            return Fail(CollectionsOutcome.InvalidRequest, $"channel must be one of {CollectionsEnums.Names<ReminderChannel>()}.");
        }

        ChannelStatus? deliveryStatus = null;
        if (!string.IsNullOrWhiteSpace(request.DeliveryStatus))
        {
            if (!CollectionsEnums.TryParse<ChannelStatus>(request.DeliveryStatus, out var parsed) || !ReminderPolicy.IsValidDeliveryStatus(channel, parsed))
            {
                return Fail(CollectionsOutcome.InvalidRequest, channel == ReminderChannel.VoiceBot
                    ? "deliveryStatus for VoiceBot must be Answered, NoAnswer or Failed."
                    : $"deliveryStatus for {channel} must be Sent, Delivered or Failed.");
            }

            deliveryStatus = parsed;
        }

        CustomerIntent? intent = null;
        if (request.CustomerResponded)
        {
            if (!CollectionsEnums.TryParse<CustomerIntent>(request.CustomerIntent, out var parsedIntent))
            {
                return Fail(CollectionsOutcome.InvalidRequest, $"customerIntent is required when customerResponded and must be one of {CollectionsEnums.Names<CustomerIntent>()}.");
            }

            intent = parsedIntent;
            if (channel == ReminderChannel.VoiceBot && string.IsNullOrWhiteSpace(request.ConversationId))
            {
                return Fail(CollectionsOutcome.InvalidRequest, "conversationId is required for a customer voice response.");
            }
        }

        if (deliveryStatus is null && !request.CustomerResponded)
        {
            return Fail(CollectionsOutcome.InvalidRequest, "Report a deliveryStatus, a customer response, or both.");
        }

        if (request.ConversationId is { Length: > CollectionsReminderEvent.ConversationIdMaxLength })
        {
            return Fail(CollectionsOutcome.InvalidRequest, $"conversationId is at most {CollectionsReminderEvent.ConversationIdMaxLength} characters.");
        }

        if (!CollectionsReminder.TryParsePublicId(reminderId, out var id) || await reminderRepository.GetByIdAsync(id, cancellationToken) is not { } reminder)
        {
            return Fail(CollectionsOutcome.ReminderNotFound, $"No reminder {reminderId}.");
        }

        var hash = CollectionsHashing.Hash(request);
        if (reminder.Events.FirstOrDefault(e => e.ExternalEventId == eventId) is { } same)
        {
            return same.RequestHash == hash && (key is null || same.IdempotencyKey is null || same.IdempotencyKey == key)
                ? Replay(reminder, same)
                : Fail(CollectionsOutcome.IdempotencyConflict, $"eventId '{eventId}' was already recorded with different content.");
        }

        if (key is not null && reminder.Events.FirstOrDefault(e => e.IdempotencyKey == key) is { } sameKey)
        {
            return sameKey.RequestHash == hash
                ? Replay(reminder, sameKey)
                : Fail(CollectionsOutcome.IdempotencyConflict, "This Idempotency-Key was already used with a different event.");
        }

        if (reminder.ChannelFor(channel) is not { } channelRow)
        {
            return Fail(CollectionsOutcome.InvalidRequest, $"Reminder {reminder.PublicId} was not queued on {channel}.");
        }

        if (channelRow.Status == ChannelStatus.Suppressed)
        {
            return Fail(CollectionsOutcome.ReminderSuppressed, $"The {channel} reminder was suppressed before dispatch and was never sent; it accepts no outcomes.");
        }

        var now = clock.UtcNow;
        // Zoneless text is UTC (the contract) and an offset names the real instant. DateTime.ToUniversalTime() would read a zoneless value as the HOST's
        // local time - four hours early on the UAE application server.
        var occurredAt = TigerCS.Application.Modules.GenesysIntegration.Services.GenesysTimestamps.ToUtc(request.OccurredAtUtc) ?? now;
        if (deliveryStatus is { } status)
        {
            channelRow.Apply(status, occurredAt, request.ProviderMessageId, null);
        }

        var recorded = reminder.AddEvent(CollectionsReminderEvent.Reported(
            reminder, eventId, key, hash, channel, deliveryStatus, request.ProviderMessageId, request.ConversationId,
            occurredAt, now, caller.EmployeeId, request.CustomerResponded, intent, request.RequiresHumanFollowUp, request.CustomerPhone));

        var correlationId = Guid.NewGuid();
        if (recorded.TicketResult == TicketResult.Pending)
        {
            await outboxWriter.WriteAsync(ResponseTicketEventType,
                JsonSerializer.Serialize(new ResponseTicketPayload(reminder.CollectionsReminderId, eventId, caller.EmployeeId)),
                correlationId, $"collections-response-ticket:{reminder.CollectionsReminderId}:{eventId}", now, cancellationToken);
        }

        await auditWriter.WriteAsync(caller.EmployeeId, "CollectionsReminderOutcomeRecorded", CollectionsReminderAppService.AuditEntityType,
            CollectionsHashing.AuditEntityId(channelRow.DeduplicationKey), null,
            $"Reminder={reminder.PublicId};EventId={eventId};Channel={channel};DeliveryStatus={deliveryStatus?.ToString() ?? "-"};"
            + $"CustomerResponded={request.CustomerResponded};Intent={intent?.ToString() ?? "-"};ChannelStatus={channelRow.Status}",
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // The same event raced in concurrently and won.
            unitOfWork.DiscardPendingChanges();
            var reloaded = await reminderRepository.GetByIdAsync(id, cancellationToken);
            var winner = reloaded?.Events.FirstOrDefault(e => e.ExternalEventId == eventId);
            if (reloaded is null || winner is null)
            {
                throw;
            }

            return winner.RequestHash == hash
                ? Replay(reloaded, winner)
                : Fail(CollectionsOutcome.IdempotencyConflict, $"eventId '{eventId}' was already recorded with different content.");
        }

        if (recorded.TicketResult == TicketResult.Pending)
        {
            // Best effort now; the outbox message already guarantees a retry.
            await ProcessResponseTicketAsync(id, eventId, caller.EmployeeId, cancellationToken);
            reminder = await reminderRepository.GetByIdAsync(id, cancellationToken) ?? reminder;
            recorded = reminder.Events.First(e => e.ExternalEventId == eventId);
        }

        var dto = ToDto(reminder, recorded, replayed: false);
        return CollectionsResult<RecordReminderOutcomeResponseDto>.Ok(dto,
            recorded.TicketResult == TicketResult.Pending ? CollectionsOutcome.Accepted : CollectionsOutcome.Success);
    }

    /// <summary>
    /// Creates or reuses the conversation's ticket, raises human follow-up,
    /// then links the ticket — last, so an event is only Created/Reused once
    /// its follow-up exists. Safe to run any number of times, concurrently
    /// too: ingestion is idempotent on the conversation id, the handoff on the
    /// conversation's outstanding work, and the link on the ticket.
    /// </summary>
    public async Task<ResponseTicketProcessingResult> ProcessResponseTicketAsync(
        long reminderId, string eventId, Guid actorEmployeeId, CancellationToken cancellationToken = default)
    {
        var reminder = await reminderRepository.GetByIdAsync(reminderId, cancellationToken);
        var response = reminder?.Events.FirstOrDefault(e => e.ExternalEventId == eventId);
        if (reminder is null || response is null)
        {
            return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Gone, $"Reminder {reminderId} event '{eventId}' no longer exists.");
        }

        if (response.TicketResult != TicketResult.Pending)
        {
            return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Done);
        }

        string? failure;
        try
        {
            failure = await CreateOrReuseTicketAsync(reminder, response, actorEmployeeId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ticket for Collections reminder {ReminderId} event {EventId} failed; it stays pending for retry.", reminderId, eventId);
            failure = $"Ticket creation threw {ex.GetType().Name}.";
            unitOfWork.DiscardPendingChanges();
            reminder = await reminderRepository.GetByIdAsync(reminderId, cancellationToken);
            response = reminder?.Events.FirstOrDefault(e => e.ExternalEventId == eventId);
            if (response is null)
            {
                return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Pending, failure);
            }
        }

        if (failure is not null)
        {
            response.RecordTicketAttemptFailed(failure);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return failure is null
            ? new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Done)
            : new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Pending, failure);
    }

    private async Task<string?> CreateOrReuseTicketAsync(
        CollectionsReminder reminder, CollectionsReminderEvent response, Guid actorEmployeeId, CancellationToken cancellationToken)
    {
        var routing = options.ResponseTickets;
        var queueId = string.IsNullOrWhiteSpace(routing.QueueId) ? null : routing.QueueId.Trim();
        var departmentCode = queueId is null && !string.IsNullOrWhiteSpace(routing.DepartmentCode) ? routing.DepartmentCode.Trim() : null;
        if (queueId is null && departmentCode is null)
        {
            return "DepartmentNotResolved: no Collections ticket routing is configured (Collections:ResponseTickets:QueueId or DepartmentCode).";
        }

        var ingest = await ingestion.IngestAsync(actorEmployeeId, new GenesysInquiryDto(
            ConversationId: response.ConversationId!,
            Channel: GenesysChannel.Phone,
            Direction: "Outbound",
            CustomerPhone: response.CustomerPhone,
            QueueId: queueId,
            DepartmentCode: departmentCode,
            Subject: $"Collections reminder response: {Describe(response.CustomerIntent!.Value)} (account {reminder.AccountId}, {reminder.PublicId})"),
            cancellationToken);

        if (ingest.Outcome is not (GenesysIngestionOutcome.TicketCreated or GenesysIngestionOutcome.AlreadyIngested) || ingest.Ticket is null)
        {
            var prefix = ingest.Outcome == GenesysIngestionOutcome.DepartmentNotResolved ? "DepartmentNotResolved: " : "";
            return $"{prefix}Genesys ingestion answered {ingest.Outcome}{(ingest.Detail is null ? "" : $" — {ingest.Detail}")}.";
        }

        if (response.RequiresHumanFollowUp)
        {
            var update = await ticketUpdates.UpdateAsync(actorEmployeeId, ingest.Ticket.TicketId, new GenesysTicketUpdateDto(
                response.ConversationId!,
                Handoff: new GenesysHandoffUpdateDto(
                    Required: true,
                    Reason: FollowUpReason(reminder, response),
                    Trigger: response.CustomerIntent switch
                    {
                        CustomerIntent.RequestedHuman => "CustomerRequestedHuman",
                        CustomerIntent.AiDisconnected => "AiConnectionLost",
                        _ => "AiEscalated"
                    })),
                cancellationToken);

            if (update.Outcome != GenesysTicketUpdateOutcome.Applied)
            {
                return $"Human follow-up could not be recorded: {update.Outcome}{(update.Detail is null ? "" : $" — {update.Detail}")}.";
            }
        }

        response.LinkTicket(ingest.Ticket.TicketId, ingest.Ticket.TicketNumber,
            created: ingest.Outcome == GenesysIngestionOutcome.TicketCreated, clock.UtcNow);
        return null;
    }

    private static string FollowUpReason(CollectionsReminder reminder, CollectionsReminderEvent response) => response.CustomerIntent switch
    {
        CustomerIntent.AlreadyPaid =>
            $"Payment verification: the customer says the payment for account {reminder.AccountId} was already made. "
            + "Verify it in the financial source. No payment has been posted and no balance has changed.",
        CustomerIntent.Disputed =>
            $"The customer disputes the reminder amount ({reminder.Amount} {reminder.Currency}) for account {reminder.AccountId}.",
        CustomerIntent.RequestedHuman =>
            $"The customer asked for a person during the payment reminder for account {reminder.AccountId}.",
        CustomerIntent.AiDisconnected =>
            $"The voice bot lost the payment reminder call for account {reminder.AccountId}; follow up with the customer.",
        _ =>
            $"Human follow-up requested on the payment reminder for account {reminder.AccountId}.",
    };

    private static string Describe(CustomerIntent intent) => intent switch
    {
        CustomerIntent.PromiseToPay => "promise to pay",
        CustomerIntent.AlreadyPaid => "customer says already paid",
        CustomerIntent.RequestedHuman => "customer requested a human",
        CustomerIntent.AiDisconnected => "AI disconnected",
        CustomerIntent.Disputed => "amount disputed",
        _ => "other"
    };

    private static RecordReminderOutcomeResponseDto ToDto(CollectionsReminder reminder, CollectionsReminderEvent e, bool replayed) =>
        new(reminder.PublicId, e.ExternalEventId, "Recorded", e.DeliveryStatus?.ToString(),
            reminder.ChannelFor(e.Channel!.Value)?.Status.ToString() ?? "",
            e.TicketId, e.TicketNumber, e.TicketResult.ToString(), e.FollowUpRequired, replayed);

    private static CollectionsResult<RecordReminderOutcomeResponseDto> Replay(CollectionsReminder reminder, CollectionsReminderEvent e) =>
        CollectionsResult<RecordReminderOutcomeResponseDto>.Ok(ToDto(reminder, e, replayed: true),
            e.TicketResult == TicketResult.Pending ? CollectionsOutcome.Accepted : CollectionsOutcome.Success);

    private static CollectionsResult<RecordReminderOutcomeResponseDto> Fail(CollectionsOutcome outcome, string? detail = null) =>
        CollectionsResult<RecordReminderOutcomeResponseDto>.Fail(outcome, detail);

    internal sealed record ResponseTicketPayload(long ReminderId, string EventId, Guid ActorEmployeeId);
}

public enum ResponseTicketProcessingOutcome
{
    /// <summary>Linked (now or earlier), or no ticket was owed.</summary>
    Done,

    /// <summary>Still owed; will be retried.</summary>
    Pending,

    /// <summary>The reminder or event no longer exists — nothing to retry.</summary>
    Gone
}

public sealed record ResponseTicketProcessingResult(ResponseTicketProcessingOutcome Outcome, string? Detail = null);
