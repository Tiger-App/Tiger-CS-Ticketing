using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The one update contract Genesys consumes — a <b>facade</b> over the
/// conversation-end and human-handoff services, not a reimplementation of
/// either.
///
/// <para>
/// <b>Why a facade.</b> Genesys asked for three externally consumable
/// contracts: create, look up, update. The behaviour behind "update" already
/// existed as separate services with their own rules, idempotency and tests;
/// splitting one integration concept across three routes made the external
/// surface wider than the integration actually is. This composes them, so
/// there is exactly one place Genesys sends changes and exactly one place
/// each rule lives.
/// </para>
///
/// <para>
/// <b>Genesys owns the conversation; TigerCS owns the ticket.</b> The only
/// things this accepts are conversation facts — who is handling it, when it
/// ended, what was said, whether it needs a human. Category, request type,
/// priority, status, owner, department, resolution and closure are absent
/// from the contract by design: they move through their own TigerCS
/// operations, with their own authorization and SLA consequences. Genesys
/// cannot reach them from here at all.
/// </para>
///
/// <para>
/// <b>Nothing in this call closes a ticket.</b> Ending a conversation finalizes the
/// interaction and stores the transcript; the ticket carries on under the
/// existing lifecycle. The response echoes the ticket's unchanged status to
/// make that visible on every call. The one automatic closure — a chatbot
/// that waited more than the configured time for a customer reply — happens
/// later, in <c>ChatbotInactivityCloseAppService</c> driven by a background
/// job; this call only starts or cancels the persisted timer it reads.
/// </para>
///
/// <para>
/// <b>Validated before anything is written.</b> The conversation must resolve
/// to an interaction on the ticket in the route — that cross-check is what
/// stops a transcript being applied to the wrong ticket — and a malformed
/// transcript is refused by the end service before it writes, so a bad
/// update never leaves a half-applied conversation.
/// </para>
/// </summary>
public sealed class GenesysTicketUpdateAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    IDepartmentRepository departmentRepository,
    ITicketAgentHandoffRepository handoffRepository,
    ITicketingUnitOfWork unitOfWork,
    GenesysConversationEndAppService conversationEndAppService,
    GenesysAgentHandoffAppService agentHandoffAppService,
    GenesysAgentResolutionAppService agentResolution,
    IAuditEntryWriter auditWriter,
    ITicketWorkflowEventRepository workflowEventRepository,
    TimeProvider timeProvider,
    GenesysRequestTypeClassificationAppService classificationAppService,
    TicketLifecycleAppService lifecycleAppService,
    GenesysAgentHandlingHistory handlingHistory,
    GenesysInteractionHandlerRecorder? handlerRecorder = null,
    GenesysAnsweringAgentAssignmentService? answeringAgentAssignment = null)
{
    /// <summary>The exact closure reason recorded when the AI finishes the conversation.</summary>
    public const string ClosedByAiReason = "Closed by AI";

    public async Task<GenesysTicketUpdateResult> UpdateAsync(
        Guid callerEmployeeId, long ticketId, GenesysTicketUpdateDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConversationIdRequired);
        }

        var conversationId = request.ConversationId.Trim();

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.TicketNotFound);
        }

        // The interaction is addressed by BOTH identifiers: a conversation may carry several tickets (one per distinct service request),
        // and an update to one of them must land on that ticket's own interaction, never on a sibling's.
        var interaction = await conversationRepository.GetByConversationAndTicketAsync(conversationId, ticketId, cancellationToken);
        if (interaction is null)
        {
            // The cross-check. Both identifiers are the caller's; agreeing with one of them and guessing about the other would
            // silently attach a transcript, or pending human work, to somebody else's ticket.
            if (await conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken) is { } other)
            {
                return GenesysTicketUpdateResult.Failure(
                    GenesysTicketUpdateOutcome.ConversationTicketMismatch,
                    $"Conversation '{conversationId}' belongs to ticket {other.TicketId}, not {ticketId}.");
            }

            return GenesysTicketUpdateResult.Failure(
                GenesysTicketUpdateOutcome.ConversationNotFound,
                "This conversation never produced a ticket, so there is nothing to update.");
        }

        // Checked before anything is written, like the transcript: a
        // confirmation that does not actually confirm must not leave the rest
        // of the update half-applied.
        if (request.CustomerConfirmation is { ConfirmedResolved: not true })
        {
            return GenesysTicketUpdateResult.Failure(
                GenesysTicketUpdateOutcome.InvalidCustomerConfirmation,
                "customerConfirmation records only an explicit confirmation — send confirmedResolved: true, or omit the part.");
        }

        // The handoff vocabulary is checked up front too: a request that will be refused for an unrecognized mode or trigger must not
        // leave its classification applied behind it (no partial writes on a refused update).
        if (request.Handoff is { Required: true } handoffToCheck)
        {
            if (!string.IsNullOrWhiteSpace(handoffToCheck.Mode) && !NamedEnum.TryParse<AgentHandoffMode>(handoffToCheck.Mode, out _))
            {
                return GenesysTicketUpdateResult.Failure(
                    GenesysTicketUpdateOutcome.InvalidHandoffMode,
                    $"'{handoffToCheck.Mode}' is not a recognized follow-up mode. Use Callback, ContinueChat, ReplyInChannel or HumanTakeover, or omit it.");
            }

            if (!string.IsNullOrWhiteSpace(handoffToCheck.Trigger) && !NamedEnum.TryParse<HandoffTrigger>(handoffToCheck.Trigger, out _))
            {
                return GenesysTicketUpdateResult.Failure(
                    GenesysTicketUpdateOutcome.InvalidHandoffTrigger,
                    $"'{handoffToCheck.Trigger}' is not a recognized handoff trigger. Use CustomerRequestedHuman, "
                    + "AiConnectionLost, AiEscalated, RoutingDecision or AgentTransfer, or omit it.");
            }
        }

        // Transcript messages are validated up front for the same reason: a malformed live message must not leave a classification or a
        // handoff applied behind a refused update.
        if (request.Transcript is { Count: > 0 } liveMessages && !GenesysConversationEndAppService.IsValidTranscript(liveMessages, out var liveError))
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidTranscript, liveError);
        }

        if (request.Ended?.Transcript is { Count: > 0 } finalMessages && !GenesysConversationEndAppService.IsValidTranscript(finalMessages, out var finalError))
        {
            return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidTranscript, finalError);
        }

        // The request type the bot identified, validated and applied before
        // anything else: an invalid or conflicting one refuses the update
        // outright, and the department / owner it sets is what the handoff,
        // routing and timer logic below must see. Repeating the same request
        // type is a no-op, so a redelivered update stays idempotent.
        GenesysClassificationResult? classification = null;
        if (request.RequestType is { IsSupplied: true } requestType)
        {
            classification = await classificationAppService.ClassifyAsync(
                callerEmployeeId, ticketId, conversationId, requestType, "GenesysUpdate", cancellationToken);

            var classificationFailure = classification.Outcome switch
            {
                GenesysClassificationOutcome.RequestTypeInvalid => (GenesysTicketUpdateOutcome?)GenesysTicketUpdateOutcome.InvalidRequestType,
                GenesysClassificationOutcome.RequestTypeConflict => GenesysTicketUpdateOutcome.RequestTypeConflict,
                GenesysClassificationOutcome.TicketClosed => GenesysTicketUpdateOutcome.TicketClosed,
                GenesysClassificationOutcome.ConcurrencyConflict => GenesysTicketUpdateOutcome.ConcurrencyConflict,
                GenesysClassificationOutcome.TicketNotFound => GenesysTicketUpdateOutcome.TicketNotFound,
                _ => null
            };

            if (classificationFailure is { } failure)
            {
                return GenesysTicketUpdateResult.Failure(failure, classification.Detail);
            }
        }

        // Live transcript first among the conversation facts: whoever picks the interaction up next (the handoff below may be what asks for
        // them) finds everything said so far already stored, and a human line in it is what satisfies First Response.
        var transcriptCount = (int?)null;
        if (request.Transcript is { Count: > 0 })
        {
            var appended = await conversationEndAppService.AppendTranscriptAsync(
                callerEmployeeId, conversationId, request.Transcript, cancellationToken, ticketId);

            if (appended.Outcome == GenesysTranscriptAppendOutcome.Conflict)
            {
                return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConcurrencyConflict, appended.Detail);
            }

            if (appended.Outcome is GenesysTranscriptAppendOutcome.InvalidTranscript or GenesysTranscriptAppendOutcome.ConversationNotFound
                or GenesysTranscriptAppendOutcome.IntegrationDisabled)
            {
                return GenesysTicketUpdateResult.Failure(
                    appended.Outcome == GenesysTranscriptAppendOutcome.InvalidTranscript
                        ? GenesysTicketUpdateOutcome.InvalidTranscript
                        : GenesysTicketUpdateOutcome.ConversationNotFound,
                    appended.Detail);
            }

            transcriptCount = appended.Total;
        }

        // Human handoff first: a conversation that ends in the same update
        // must still leave its pending work outstanding and actionable, and
        // requesting the handoff before the end event is the order that reads
        // the way the real sequence happened.
        var handoffStatus = (string?)null;
        var handoffId = (long?)null;

        if (request.Handoff is { } handoff)
        {
            if (handoff.Required == true)
            {
                // routing.agentId is the agent Genesys reports as CONNECTED (answered). Passing it lets the handoff give the ticket straight to
                // that agent instead of Round Robin; request.AgentId / handoff.AgentAvailable only say who is assigned, which is not an answer.
                var requested = await agentHandoffAppService.RequestAsync(
                    callerEmployeeId,
                    new GenesysHandoffRequestDto(
                        conversationId, handoff.AgentAvailable, handoff.Mode, handoff.Reason, handoff.Trigger,
                        request.AgentId, request.AgentName, handoff.WorkItemId, TicketId: ticketId),
                    options.AssignOnAnswer ? NullIfBlank(request.Routing?.AgentId) : null,
                    cancellationToken);

                if (requested.Outcome is not (GenesysHandoffOutcome.HandoffRecorded or GenesysHandoffOutcome.AlreadyRequested))
                {
                    return Translate(requested);
                }

                handoffStatus = requested.Status;
                handoffId = requested.TicketAgentHandoffId;
            }
            else if (handoff.Required == false)
            {
                // An EXPLICIT false stands outstanding work down: the AI
                // resumed, or the human is no longer needed. Omitted (null)
                // never reaches here — an update that carries only
                // AssignedAgentId must not cancel the very work it is
                // reporting an assignment for. Nothing outstanding answers
                // AlreadyResolved, which is the idempotent case and not a
                // failure — so the update as a whole still succeeds and
                // simply reports the work's state.
                var cancelled = await agentHandoffAppService.CancelAsync(
                    callerEmployeeId,
                    new GenesysHandoffCancellationDto(conversationId, handoff.Reason, ticketId),
                    cancellationToken);

                if (cancelled.Outcome is not (GenesysHandoffOutcome.HandoffCancelled or GenesysHandoffOutcome.AlreadyResolved))
                {
                    return Translate(cancelled);
                }

                if (cancelled.Outcome == GenesysHandoffOutcome.HandoffCancelled)
                {
                    handoffStatus = cancelled.Status;
                    handoffId = cancelled.TicketAgentHandoffId;
                }
            }

            if (!string.IsNullOrWhiteSpace(handoff.AssignedAgentId))
            {
                var assigned = await agentHandoffAppService.UpdateAssignmentAsync(
                    callerEmployeeId,
                    new GenesysHandoffAssignmentDto(conversationId, handoff.AssignedAgentId, request.AgentName, TicketId: ticketId),
                    cancellationToken);

                if (assigned.Outcome != GenesysHandoffOutcome.AssignmentRecorded)
                {
                    return Translate(assigned);
                }

                handoffStatus = assigned.Status;
                handoffId = assigned.TicketAgentHandoffId;
            }
        }

        // Routing: the queue the conversation is in now and the agent it is
        // connected to — a queue change, an agent connecting, a transfer.
        // Same interaction, same ticket; the ticket itself is not touched.
        if (request.Routing is not null || request.StartedAtUtc is not null)
        {
            await RecordRoutingAsync(callerEmployeeId, interaction, request, cancellationToken);
        }

        // The agent Genesys reports as CONNECTED (answered / accepted - never ringing or merely assigned) owns the ticket: given to them
        // directly, in the Call Center, not to the next agent in the rotation. An agent who cannot take it (unmapped, deactivated, locked
        // out ...) leaves the ticket in the Call Center queue flagged for manual assignment. A human owner, or a person's transfer, stands:
        // later events never overwrite either. Idempotent per agent.
        if (options.AssignOnAnswer && answeringAgentAssignment is not null && NullIfBlank(request.Routing?.AgentId) is { } answeringAgentId)
        {
            var answered = await answeringAgentAssignment.ApplyConnectedAgentAsync(
                callerEmployeeId, ticket, interaction, answeringAgentId, source: "Routing", cancellationToken);
            if (answered.Outcome == AnswerAssignmentOutcome.ConcurrencyConflict)
            {
                return GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConcurrencyConflict, answered.Detail);
            }
        }

        // An agent connecting to a conversation whose human work is still
        // waiting IS the human taking it: recorded on that same work item,
        // through the same assignment path as handoff.assignedAgentId. Only
        // while it is WaitingForAgent — work a TigerCS agent is already
        // handling is never reassigned by a Genesys routing event — and never
        // when this same update states the handoff explicitly.
        if (NullIfBlank(request.Routing?.AgentId) is { } connectedAgentId
            && request.Handoff is not { Required: true } && NullIfBlank(request.Handoff?.AssignedAgentId) is null
            && await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken)
                is { Status: AgentHandoffStatus.WaitingForAgent })
        {
            var taken = await agentHandoffAppService.UpdateAssignmentAsync(
                callerEmployeeId,
                new GenesysHandoffAssignmentDto(conversationId, connectedAgentId, request.Routing!.AgentName, TicketId: ticketId),
                cancellationToken);

            if (taken.Outcome == GenesysHandoffOutcome.AssignmentRecorded)
            {
                handoffStatus = taken.Status;
                handoffId = taken.TicketAgentHandoffId;
            }
        }

        // The conversation's own ending — transcript included. Write-once in
        // the domain, so a redelivered end answers AlreadyEnded without
        // moving the recorded time or re-storing the transcript.
        transcriptCount = await conversationRepository.CountMessagesAsync(interaction.TicketInteractionId, cancellationToken);

        if (request.Ended is { } ended)
        {
            var endResult = await conversationEndAppService.EndAsync(
                callerEmployeeId,
                new GenesysConversationEndDto(
                    conversationId, ended.EndedAtUtc, ended.EndReason,
                    request.AgentId, request.AgentName, ended.Transcript, ticketId),
                cancellationToken);

            if (endResult.Outcome is not (GenesysConversationEndOutcome.Ended or GenesysConversationEndOutcome.AlreadyEnded))
            {
                return Translate(endResult);
            }

            transcriptCount = endResult.TranscriptMessageCount;
        }
        else if (!string.IsNullOrWhiteSpace(request.AgentId) || !string.IsNullOrWhiteSpace(request.AgentName))
        {
            // Agent context without an ending: apply-if-absent on the
            // interaction, so a later, different agent never overwrites the
            // one who actually took the conversation.
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            interaction.RecordAgentIfAbsent(request.AgentId, request.AgentName);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Interaction ownership from the agent this update names — resolved
        // server-side from the Genesys User ID (agentId), apply-if-absent
        // like the verbatim agent context above. An update may name no agent
        // at all, and an unmapped one changes nothing here: the verbatim
        // agentId is still kept, and ownership stays null. The strict path
        // for an agent acting is the agent-context endpoint. The agent a
        // routing change connected counts too: on a voice call that is how
        // Genesys first names the agent at all.
        if ((NullIfBlank(request.AgentId) ?? NullIfBlank(request.Routing?.AgentId)) is { } reportedAgentId
            && interaction.HandledByUserId is null
            && await agentResolution.ResolveByGenesysUserIdAsync(reportedAgentId, cancellationToken) is { IsResolved: true } agent
            && interaction.RecordHandlingAgentIfAbsent(agent.Agent!.GenesysUserId, agent.Agent.UserId))
        {
            await using var ownershipTransaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await handlingHistory.RecordFirstHandlerAsync(ticketId, conversationId, agent.Agent!, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await ownershipTransaction.CommitAsync(cancellationToken);
        }

        // Every agent Genesys reports as connected is a handler of the interaction — not only the first. A transfer arrives as a routing
        // update naming the receiving agent; recording each one is what lets that agent (and the one who transferred away) keep seeing the
        // ticket. An agent that is merely ringing/assigned is never sent here, a bot or unmapped id resolves to nothing and is skipped.
        if (handlerRecorder is not null)
        {
            await handlerRecorder.RecordAsync(
                callerEmployeeId, interaction, NullIfBlank(request.Routing?.AgentId),
                InteractionHandlerSource.Routing, request.Routing?.ConnectedAtUtc, cancellationToken);

            await handlerRecorder.RecordAsync(
                callerEmployeeId, interaction, NullIfBlank(request.AgentId),
                request.Ended is not null ? InteractionHandlerSource.ConversationEnd : InteractionHandlerSource.Update,
                connectedAtUtc: null, cancellationToken);
        }

        if (request.CustomerConfirmation is { } confirmation)
        {
            await RecordCustomerConfirmationAsync(callerEmployeeId, ticket, conversationId, confirmation, cancellationToken);
        }

        if (request.HumanFollowUpRequired == true)
        {
            await MarkHumanFollowUpAsync(callerEmployeeId, ticket, interaction, cancellationToken);
        }

        await ApplyCustomerActivityAsync(callerEmployeeId, ticket, interaction, request, cancellationToken);

        var timerNote = await ApplyAwaitingCustomerReplyAsync(callerEmployeeId, ticket, interaction, request, cancellationToken);

        // The handoff state is reported on EVERY update, not only on one that
        // touched it. Genesys ending a conversation needs to see that the
        // pending human work is still outstanding — that is exactly the
        // customer-disconnected case, where the live session is over and the
        // work is not.
        if (handoffStatus is null
            && await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is { } openHandoff)
        {
            handoffStatus = openHandoff.Status.ToString();
            handoffId = openHandoff.TicketAgentHandoffId;
        }

        // The AI finished the conversation. Last, so everything else this update carries (transcript, end, handoff state) is stored first.
        var (closedByAi, closedByAiNote) = await ApplyClosedByAiAsync(callerEmployeeId, ticket, interaction, request, cancellationToken);

        return new GenesysTicketUpdateResult(
            GenesysTicketUpdateOutcome.Applied,
            ticket.TicketId,
            ticket.TicketNumber,
            // Echoed on every response: the proof that nothing in this
            // contract closed, resolved or otherwise moved the ticket.
            ticket.TicketStatus.ToString(),
            interaction.IsEnded,
            transcriptCount ?? 0,
            handoffStatus,
            handoffId,
            AwaitingCustomerReply: interaction.AwaitingCustomerReplySinceUtc is not null,
            InactivityDeadlineUtc: InactivityDeadline(interaction),
            AwaitingCustomerReplyNote: timerNote,
            Classification: classification,
            ClosedByAi: closedByAi,
            ClosedByAiNote: closedByAiNote);
    }

    /// <summary>
    /// <c>closedByAi: true</c> — the Genesys AI finished the conversation. Closes through the lifecycle (Resolved → Closed, audit
    /// <c>AutoCloseByAi</c>, history, notification) with the exact reason <see cref="ClosedByAiReason"/>, but ONLY while the AI still owns the
    /// conversation: no requested/pending/connected human, no person owning the ticket, not a Call Center ticket, still open. Anything else is
    /// answered with a note and changes nothing — never an error, so a retried webhook stays safe. An already-closed ticket (by a person, document
    /// delivery, inactivity or an earlier AI call) is left exactly as it is: no second write and no overwritten reason.
    /// </summary>
    private async Task<(bool Closed, string? Note)> ApplyClosedByAiAsync(
        Guid callerEmployeeId, Ticket ticket, TicketInteraction interaction, GenesysTicketUpdateDto request, CancellationToken cancellationToken)
    {
        if (request.ClosedByAi != true)
        {
            return (false, null);
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return (false, "Not closed: the ticket is already closed; it was left as it is.");
        }

        if (request.Handoff is { Required: true }
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not null)
        {
            return (false, "Not closed: human follow-up is requested or pending for this conversation.");
        }

        if (interaction.RequiresHumanFollowUp)
        {
            return (false, "Not closed: this ticket needs human follow-up; only the authorized TigerCS workflow may close it.");
        }

        if (ticket.CurrentOwnerEmployeeId is not null)
        {
            return (false, "Not closed: a person owns the ticket.");
        }

        if (interaction.HandledByUserId is not null || NullIfBlank(request.Routing?.AgentId) is not null)
        {
            return (false, "Not closed: a human agent is handling this conversation.");
        }

        if (await CustomerInactivityScope.IsCallCenterTicketAsync(ticket, departmentRepository, cancellationToken))
        {
            return (false, "Not closed: Call Center tickets are managed by people and are never closed by the AI.");
        }

        if (ticket.TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer))
        {
            return (false, $"Not closed: the ticket is {ticket.TicketStatus}.");
        }

        var result = await lifecycleAppService.CloseForAiCompletionAsync(
            callerEmployeeId, ticket.TicketId, ClosedByAiReason, $"ConversationId={interaction.GenesysConversationId}",
            beforeCommit: _ =>
            {
                // The conversation is over: neither inactivity clock may fire for it.
                interaction.StopTrackingCustomerInteraction();
                interaction.CancelAwaitingCustomerReply();
            },
            cancellationToken);

        return result.Outcome switch
        {
            TicketMutationOutcome.Success => (true, null),
            TicketMutationOutcome.TicketClosed => (false, "Not closed: the ticket is already closed; it was left as it is."),
            TicketMutationOutcome.ConcurrencyConflict => (false, "Not closed: the ticket changed at the same moment. Send closedByAi again."),
            _ => (false, "Not closed: the ticket is not in a state the AI may close.")
        };
    }

    private DateTime? InactivityDeadline(TicketInteraction interaction)
    {
        var deadlines = new List<DateTime>(2);
        if (interaction.LastCustomerInteractionAtUtc is { } last && options.CustomerInactivityTimeout is { } timeout)
        {
            deadlines.Add(last + timeout);
        }
        else if (interaction.AwaitingCustomerReplySinceUtc is { } since && options.AwaitingReplyTimeout is { } awaitingTimeout)
        {
            deadlines.Add(since + awaitingTimeout);
        }

        return deadlines.Count == 0 ? null : deadlines.Min();
    }

    /// <summary>
    /// The customer did something (<c>customerActive: true</c>, or <c>awaitingCustomerReply: false</c> — "the customer replied"): restart
    /// the Website/Chatbot inactivity clock from now. Recorded only for a live Website/Chatbot conversation whose ticket is still open
    /// and not human-managed; otherwise it is a no-op. A repeat inside the same instant changes nothing.
    /// </summary>
    private async Task ApplyCustomerActivityAsync(
        Guid callerEmployeeId, Ticket ticket, TicketInteraction interaction, GenesysTicketUpdateDto request, CancellationToken cancellationToken)
    {
        if (request.CustomerActive != true && request.AwaitingCustomerReply != false)
        {
            return;
        }

        if (!interaction.IsWebsiteOrChatbotConversation
            || ticket.TicketStatus is TicketStatus.Closed or TicketStatus.Resolved
            || ticket.CurrentOwnerEmployeeId is not null
            || await CustomerInactivityScope.IsCallCenterTicketAsync(ticket, departmentRepository, cancellationToken))
        {
            return;
        }

        if (interaction.RecordCustomerInteraction(timeProvider.GetUtcNow().UtcDateTime, callerEmployeeId))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The chatbot inactivity timer's only writer on the request path.
    ///
    /// <para>
    /// <b>Only an active chatbot exchange can be waiting.</b> A timer is
    /// never started — and a running one is cancelled — when human follow-up
    /// is outstanding or being requested in this same update (requested
    /// handoff, bot connection failure, takeover), when a person already owns
    /// the ticket, or when the ticket or conversation is over. Those are
    /// exactly the cases the closing job re-checks again at fire time; doing
    /// it here as well keeps the persisted state honest rather than relying
    /// on the job alone.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent.</b> <c>awaitingCustomerReply: true</c> while a timer is
    /// running changes nothing (<see cref="TicketInteraction.BeginAwaitingCustomerReply"/>),
    /// so a redelivered webhook cannot restart it. Returns a note only when a
    /// requested timer was deliberately not started.
    /// </para>
    /// </summary>
    private async Task<string?> ApplyAwaitingCustomerReplyAsync(
        Guid callerEmployeeId, Ticket ticket, TicketInteraction interaction, GenesysTicketUpdateDto request,
        CancellationToken cancellationToken)
    {
        var humanFollowUp = request.Handoff is { Required: true }
            || interaction.RequiresHumanFollowUp
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not null;

        string? note = null;
        var start = false;

        if (humanFollowUp && interaction.StopTrackingCustomerInteraction())
        {
            // A human is requested or pending: the conversation is no longer the bot's to close for silence.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (humanFollowUp)
        {
            // Never waiting on the customer while a human is requested.
            if (request.AwaitingCustomerReply == true)
            {
                note = "Not started: human follow-up is requested or pending for this conversation.";
            }
        }
        else if (request.AwaitingCustomerReply == true)
        {
            if (interaction.IsEnded)
            {
                note = "Not started: the conversation has ended.";
            }
            else if (ticket.TicketStatus == TicketStatus.Closed || interaction.InactivityClosedAtUtc is not null)
            {
                note = "Not started: the ticket is already closed.";
            }
            else if (ticket.CurrentOwnerEmployeeId is not null)
            {
                note = "Not started: a person owns the ticket.";
            }
            else
            {
                start = true;
            }
        }
        else if (request.AwaitingCustomerReply is null)
        {
            return null;
        }

        var before = DescribeTimer(interaction);
        var changed = start
            ? interaction.BeginAwaitingCustomerReply(timeProvider.GetUtcNow().UtcDateTime, callerEmployeeId)
            : interaction.CancelAwaitingCustomerReply();

        if (!changed)
        {
            return note;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId,
            start ? GenesysAuditActions.CustomerReplyTimerStarted : GenesysAuditActions.CustomerReplyTimerCancelled,
            GenesysAuditActions.ConversationEntityType,
            interaction.GenesysConversationId!,
            beforeValue: before,
            afterValue: DescribeTimer(interaction),
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return note;
    }

    private static string DescribeTimer(TicketInteraction interaction) =>
        $"TicketId={interaction.TicketId};AwaitingCustomerReplySinceUtc={interaction.AwaitingCustomerReplySinceUtc?.ToString("O") ?? "(none)"}";

    /// <summary>
    /// Applies a routing change (and a late start time) to the interaction,
    /// and audits it with the before and after values — the audit trail is
    /// where the history of every queue and agent the conversation passed
    /// through is kept, since the interaction itself names only the current
    /// one. A redelivered, unchanged routing event writes nothing at all.
    /// </summary>
    private async Task RecordRoutingAsync(
        Guid callerEmployeeId, TicketInteraction interaction, GenesysTicketUpdateDto request, CancellationToken cancellationToken)
    {
        var before = DescribeRouting(interaction);
        var previousAgentId = interaction.GenesysAgentId;
        var previousAgentName = interaction.GenesysAgentName;

        var routingChanged = request.Routing is { } routing
            && interaction.RecordRouting(routing.QueueId, routing.QueueName, routing.AgentId, routing.AgentName);
        var startRecorded = interaction.RecordStartedAtIfAbsent(request.StartedAtUtc);

        if (!routingChanged && !startRecorded)
        {
            return;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.RoutingChanged,
            GenesysAuditActions.ConversationEntityType,
            interaction.GenesysConversationId!,
            beforeValue: before,
            afterValue: DescribeRouting(interaction),
            correlationId: Guid.NewGuid(),
            cancellationToken);

        // A real call transfer between agents: the conversation was with one agent and Genesys has now connected another.
        // The first agent naming (previous agent absent) is not a transfer. Recorded as handling history only — it says
        // nothing about whether the first agent could resolve the issue, so it is never an escalation.
        if (routingChanged
            && !string.IsNullOrWhiteSpace(previousAgentId)
            && !string.IsNullOrWhiteSpace(interaction.GenesysAgentId)
            && !string.Equals(previousAgentId, interaction.GenesysAgentId, StringComparison.Ordinal))
        {
            await handlingHistory.RecordCallTransferAsync(
                interaction.TicketId, interaction.GenesysConversationId,
                previousAgentId, previousAgentName,
                interaction.GenesysAgentId, interaction.GenesysAgentName,
                interaction.GenesysQueueName, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// <c>humanFollowUpRequired: true</c> — this ticket is human work (a leasing request, a complaint). Sticky and idempotent: both chatbot
    /// inactivity clocks stop for good, and neither the AI nor document delivery may close it afterwards; a person resolves and closes it
    /// through the authorized workflow. Touches only this ticket's own interaction, never a sibling ticket of the same conversation.
    /// </summary>
    private async Task MarkHumanFollowUpAsync(
        Guid callerEmployeeId, Ticket ticket, TicketInteraction interaction, CancellationToken cancellationToken)
    {
        if (!interaction.MarkRequiresHumanFollowUp())
        {
            return;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.HumanFollowUpRequired,
            GenesysAuditActions.ConversationEntityType,
            interaction.GenesysConversationId!,
            beforeValue: null,
            afterValue: $"TicketId={ticket.TicketId};ServiceRequestId={interaction.GenesysServiceRequestId ?? "(none)"};TicketStatus={ticket.TicketStatus}",
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records the customer's confirmation that the issue is resolved — a
    /// typed workflow event on the ticket (so Ticket Details shows it) and an
    /// audit entry — and <b>nothing else</b>. The ticket is only read: Resolve
    /// and Close stay with their own actors and authorization. A resend with
    /// the same <c>confirmedAtUtc</c> finds its event and writes nothing.
    /// </summary>
    private async Task RecordCustomerConfirmationAsync(
        Guid callerEmployeeId, Ticket ticket, string conversationId,
        GenesysCustomerConfirmationUpdateDto confirmation, CancellationToken cancellationToken)
    {
        var confirmedAtUtc = confirmation.ConfirmedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime;

        var existing = await workflowEventRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken);
        if (existing.Any(e => e.EventType == WorkflowEventType.CustomerConfirmedResolved && e.OccurredAtUtc == confirmedAtUtc))
        {
            return;
        }

        var note = NullIfBlank(confirmation.Note);
        var eventNote = $"Genesys conversation {conversationId}" + (note is null ? string.Empty : $": {note}");
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticket.TicketId, WorkflowEventType.CustomerConfirmedResolved, confirmedAtUtc, actorEmployeeId: null,
                ticketApprovalId: null, eventNote.Length <= MaxWorkflowNoteLength ? eventNote : eventNote[..MaxWorkflowNoteLength],
                correlationId),
            cancellationToken);
        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.CustomerConfirmedResolved,
            GenesysAuditActions.ConversationEntityType,
            conversationId,
            beforeValue: $"TicketId={ticket.TicketId};TicketStatus={ticket.TicketStatus}",
            afterValue: $"TicketId={ticket.TicketId};TicketStatus={ticket.TicketStatus};ConfirmedAtUtc={confirmedAtUtc:O};Note={note ?? "(none)"}",
            correlationId,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>TicketWorkflowEvents.Note is nvarchar(500).</summary>
    private const int MaxWorkflowNoteLength = 500;

    private static string DescribeRouting(TicketInteraction interaction) =>
        $"TicketId={interaction.TicketId};QueueId={interaction.GenesysQueueId ?? "(none)"};QueueName={interaction.GenesysQueueName ?? "(none)"};"
        + $"AgentId={interaction.GenesysAgentId ?? "(none)"};AgentName={interaction.GenesysAgentName ?? "(none)"};"
        + $"StartedAtUtc={interaction.InteractionStartedAtUtc?.ToString("O") ?? "(none)"}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static GenesysTicketUpdateResult Translate(GenesysHandoffResult result) => result.Outcome switch
    {
        GenesysHandoffOutcome.IntegrationDisabled => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.IntegrationDisabled),
        GenesysHandoffOutcome.InvalidMode => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidHandoffMode, result.Detail),
        GenesysHandoffOutcome.InvalidTrigger => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidHandoffTrigger, result.Detail),
        GenesysHandoffOutcome.ReasonRequired => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.HandoffReasonRequired, result.Detail),
        GenesysHandoffOutcome.NoOpenHandoff => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.NoOpenHandoff, result.Detail),
        GenesysHandoffOutcome.ConversationNotFound => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConversationNotFound, result.Detail),
        _ => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.NoOpenHandoff, result.Detail)
    };

    private static GenesysTicketUpdateResult Translate(GenesysConversationEndResult result) => result.Outcome switch
    {
        GenesysConversationEndOutcome.IntegrationDisabled => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.IntegrationDisabled),
        GenesysConversationEndOutcome.InvalidTranscript => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.InvalidTranscript, result.Detail),
        _ => GenesysTicketUpdateResult.Failure(GenesysTicketUpdateOutcome.ConversationNotFound, result.Detail)
    };
}
