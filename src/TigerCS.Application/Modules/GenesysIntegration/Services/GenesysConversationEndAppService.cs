using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Genesys integration phase 1 — finalizing a conversation and preserving its
/// transcript.
///
/// <para>
/// <b>Ending a conversation never closes a ticket.</b> This is the phase's
/// load-bearing business rule and the reason this service touches the
/// <see cref="Ticket"/> aggregate only to read it: the chat ends, the
/// interaction is marked ended, and the ticket carries on under the existing
/// TigerCS lifecycle — a customer who asked for an NOC has an open workflow
/// long after the chat window closed. No status, SLA, resolution or
/// escalation state is written here.
/// </para>
///
/// <para>
/// <b>Every disconnection is an ending.</b> The customer closing the browser,
/// losing signal, the agent ending the interaction, or Genesys terminating
/// it are all the same call: whatever end time and reason are supplied are
/// recorded, and the transcript available up to that moment is stored. A
/// conversation that ended abruptly still leaves a complete record of what
/// was said.
/// </para>
///
/// <para>
/// <b>Idempotent, without losing anything.</b>
/// <see cref="TicketInteraction.End"/> is write-once, so a redelivered end
/// answers <see cref="GenesysConversationEndOutcome.AlreadyEnded"/> and never
/// moves the recorded end time or reason. Its transcript is still read,
/// though, and any message not already stored is appended — deduplicated on
/// Genesys' own message id where one is supplied, and otherwise on
/// sender+timestamp+body. That is what makes "the COMPLETE transcript is
/// persisted" true rather than "whatever the first delivery happened to
/// carry": a chat stored from a truncated first delivery is completed by the
/// retry instead of staying permanently short.
/// The transcript is validated in full <i>before</i> anything is written, so
/// a malformed message can never leave a half-stored conversation.
/// </para>
///
/// <para>
/// <b>Ending a conversation is not a request for a human.</b> The AI finishing,
/// the customer leaving, a closed browser tab and a dropped connection all end
/// a conversation, and none of them says a person is needed — so by default no
/// handoff is raised here. Human work exists only when Genesys signals it
/// explicitly (<c>handoff.required = true</c>). The previous "safety net"
/// that raised an <see cref="HandoffTrigger.AiConnectionLost"/> handoff for any
/// AI conversation ended without a human is kept behind
/// <c>Genesys:AutoRaiseHandoffOnConversationEnd</c> (default off) as a rollback
/// lever; see <see cref="TryAutoRaiseHandoffAsync"/> for its conditions.
/// </para>
/// </summary>
public sealed class GenesysConversationEndAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    ITicketAgentHandoffRepository handoffRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    FirstHumanResponseRecorder firstHumanResponseRecorder,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<GenesysConversationEndResult> EndAsync(
        Guid callerEmployeeId, GenesysConversationEndDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.ConversationIdRequired);
        }

        var conversationId = request.ConversationId.Trim();

        var interaction = await conversationRepository.ResolveAsync(conversationId, request.TicketId, cancellationToken);
        if (interaction is null)
        {
            // A conversation that never produced a ticket — most commonly a
            // call that rang and was never answered. Reported plainly rather
            // than fabricating an interaction to hang the end event on.
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.ConversationNotFound);
        }

        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);

        // Validate the whole transcript before writing any of it.
        if (!TryNormalizeTranscript(request.Transcript, out var messages, out var transcriptError))
        {
            return GenesysConversationEndResult.Failure(GenesysConversationEndOutcome.InvalidTranscript, transcriptError);
        }

        var alreadyEnded = interaction.IsEnded;
        var endedAtUtc = request.EndedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // The end time and reason are write-once: a redelivered end never
        // moves them. The TRANSCRIPT is not skipped along with them, though —
        // a redelivery that carries messages the first one did not must still
        // land them, or a conversation stored from a truncated first delivery
        // would stay permanently incomplete.
        if (!alreadyEnded)
        {
            interaction.End(endedAtUtc, request.EndReason);
        }

        interaction.RecordAgentIfAbsent(request.AgentId, request.AgentName);

        var stored = await conversationRepository.ListMessagesAsync(interaction.TicketInteractionId, cancellationToken);
        var pendingMessages = await AppendNewMessagesAsync(interaction, stored, messages, cancellationToken);
        var appended = pendingMessages.Count;

        // The whole transcript as it now stands — this delivery's messages
        // included. Deciding on `stored` alone would miss the case the rule
        // exists for: a first delivery that carries the bot conversation AND
        // the end event in one call.
        var transcript = stored.Concat(pendingMessages).ToList();

        var autoRaisedHandoff = options.AutoRaiseHandoffOnConversationEnd
            ? await TryAutoRaiseHandoffAsync(callerEmployeeId, interaction, ticket, transcript, endedAtUtc, cancellationToken)
            : null;

        var firstHumanResponseRecordedAtUtc = await TryRecordFirstHumanResponseAsync(
            callerEmployeeId, ticket, transcript, cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.ConversationEnded,
            GenesysAuditActions.ConversationEntityType,
            conversationId,
            beforeValue: null,
            afterValue:
                $"TicketId={interaction.TicketId};EndedAtUtc={endedAtUtc:O};"
                + $"EndReason={interaction.EndReason ?? "(none)"};TranscriptMessagesAppended={appended};"
                + $"TicketStatus={ticket?.TicketStatus.ToString() ?? "(unknown)"}"
                + $";AutoRaisedHandoffId={autoRaisedHandoff?.TicketAgentHandoffId.ToString() ?? "(none)"}"
                + $";FirstHumanResponseRecordedAtUtc={firstHumanResponseRecordedAtUtc?.ToString("O") ?? "(none)"}",
            correlationId: Guid.NewGuid(),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GenesysConversationEndResult(
            alreadyEnded ? GenesysConversationEndOutcome.AlreadyEnded : GenesysConversationEndOutcome.Ended,
            interaction.TicketId,
            ticket?.TicketNumber,
            // Read back from the ticket, deliberately: the whole point is
            // that this is whatever the workflow says, untouched by the chat
            // ending.
            ticket?.TicketStatus.ToString(),
            // The conversation's WHOLE transcript, not just this delivery's
            // share of it — what the caller wants to know is how much is
            // stored.
            stored.Count + appended);
    }

    /// <summary>
    /// Stores messages of a conversation that is still going on (or has just ended) — the incremental half of the transcript, so a human
    /// who takes the interaction can read what the customer and the AI already said <i>before</i> replying.
    ///
    /// <para>
    /// <b>Additive and idempotent.</b> Messages are only ever appended, in delivered order, and never edited or removed. A message is
    /// recognised as already stored by Genesys' <c>messageId</c> when it has one, and otherwise by sender + timestamp + body; an id-bearing
    /// message also matches a stored row that had no id (the same line first sent without one), so the final transcript on conversation end
    /// reconciles with the incremental deliveries instead of duplicating them. A whole transcript is validated before anything is written.
    /// </para>
    ///
    /// <para>
    /// Nothing about the ticket moves: the interaction is not ended, no handoff is raised, status is untouched. Only a genuinely human line
    /// can record First Response (same rule as the end event); bot lines never can.
    /// </para>
    /// </summary>
    public async Task<GenesysTranscriptAppendResult> AppendTranscriptAsync(
        Guid callerEmployeeId, string conversationId, IReadOnlyList<GenesysTranscriptMessageDto>? transcript,
        CancellationToken cancellationToken = default, long? ticketId = null)
    {
        if (!options.Enabled)
        {
            return GenesysTranscriptAppendResult.Failure(GenesysTranscriptAppendOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return GenesysTranscriptAppendResult.Failure(GenesysTranscriptAppendOutcome.ConversationNotFound);
        }

        var interaction = await conversationRepository.ResolveAsync(conversationId.Trim(), ticketId, cancellationToken);
        if (interaction is null)
        {
            return GenesysTranscriptAppendResult.Failure(GenesysTranscriptAppendOutcome.ConversationNotFound);
        }

        if (!TryNormalizeTranscript(transcript, out var messages, out var transcriptError))
        {
            return GenesysTranscriptAppendResult.Failure(GenesysTranscriptAppendOutcome.InvalidTranscript, transcriptError);
        }

        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);
        var stored = await conversationRepository.ListMessagesAsync(interaction.TicketInteractionId, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var pending = await AppendNewMessagesAsync(interaction, stored, messages, cancellationToken);
        if (pending.Count == 0)
        {
            return new GenesysTranscriptAppendResult(GenesysTranscriptAppendOutcome.Unchanged, 0, stored.Count);
        }

        var all = stored.Concat(pending).ToList();
        var firstHumanAt = await TryRecordFirstHumanResponseAsync(callerEmployeeId, ticket, all, cancellationToken);
        await ApplyHumanActivityAsync(callerEmployeeId, ticket, interaction, pending, cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.TranscriptAppended,
            GenesysAuditActions.ConversationEntityType,
            interaction.GenesysConversationId ?? conversationId.Trim(),
            beforeValue: $"TicketId={interaction.TicketId};Messages={stored.Count}",
            afterValue:
                $"TicketId={interaction.TicketId};Messages={all.Count};Appended={pending.Count};"
                + $"FirstHumanResponseRecordedAtUtc={firstHumanAt?.ToString("O") ?? "(none)"}",
            correlationId: Guid.NewGuid(),
            cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DuplicateWriteException or TicketConcurrentlyModifiedException)
        {
            // A concurrent delivery stored a message at the same position (or the inactivity job won a race for this interaction's clocks).
            // Nothing of this delivery was kept; resending is safe because
            // storage is idempotent.
            return GenesysTranscriptAppendResult.Failure(
                GenesysTranscriptAppendOutcome.Conflict,
                "Another delivery for this conversation was being stored at the same moment. Nothing from this request was kept; send it again.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new GenesysTranscriptAppendResult(GenesysTranscriptAppendOutcome.Appended, pending.Count, all.Count);
    }

    /// <summary>
    /// A human agent's line arrived: the conversation is no longer the bot's to close for silence, so both inactivity clocks stop at once
    /// (the closing job re-checks for human lines too, so a later customer message cannot restart a clock that would close it). If the ticket
    /// is already Closed, the arrival is recorded in the audit trail — forward-compatible evidence of when TigerCS received the reply, which
    /// the Activity timeline uses to tell a delayed event from a premature closure. Nothing is reopened and no history is rewritten.
    /// </summary>
    private async Task ApplyHumanActivityAsync(
        Guid callerEmployeeId, Ticket? ticket, TicketInteraction interaction, IReadOnlyList<TicketInteractionMessage> appended,
        CancellationToken cancellationToken)
    {
        var human = appended.Where(m => m.Sender == InteractionMessageSender.HumanAgent).OrderBy(m => m.SentAtUtc).ToList();
        if (human.Count == 0)
        {
            return;
        }

        interaction.StopTrackingCustomerInteraction();
        interaction.CancelAwaitingCustomerReply();

        if (ticket is { TicketStatus: TicketStatus.Closed })
        {
            await auditWriter.WriteAsync(
                callerEmployeeId,
                "TicketHumanActivityAfterClosure",
                "Ticket",
                ticket.TicketId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                beforeValue: null,
                afterValue:
                    $"TicketId={ticket.TicketId};ConversationId={interaction.GenesysConversationId};InteractionId={interaction.TicketInteractionId};"
                    + $"AgentName={human[0].SenderName ?? "(none)"};HumanMessages={human.Count};FirstSentAtUtc={human[0].SentAtUtc:O};TicketStatus={ticket.TicketStatus}",
                correlationId: Guid.NewGuid(),
                cancellationToken);
        }
    }

    /// <summary>Adds every message not already stored, in delivered order, continuing the stored sequence.</summary>
    private async Task<List<TicketInteractionMessage>> AppendNewMessagesAsync(
        TicketInteraction interaction, IReadOnlyList<TicketInteractionMessage> stored,
        IReadOnlyList<NormalizedMessage> messages, CancellationToken cancellationToken)
    {
        var known = stored.ToList();
        var nextSequence = stored.Count == 0 ? 1 : stored.Max(m => m.Sequence) + 1;
        var added = new List<TicketInteractionMessage>();

        foreach (var message in messages)
        {
            if (IsAlreadyStored(message, known))
            {
                continue;
            }

            var row = new TicketInteractionMessage(
                interaction.TicketInteractionId, nextSequence++, message.Sender, message.SenderName, message.SenderId,
                message.SentAtUtc, message.Body, message.ExternalMessageId);

            await conversationRepository.AddMessageAsync(row, cancellationToken);
            known.Add(row);   // the same message twice in ONE delivery is stored once
            added.Add(row);
        }

        return added;
    }

    /// <summary>
    /// Satisfies the First Response SLA from the transcript, when the
    /// conversation contains a genuinely human reply and the ticket does not
    /// already carry one.
    ///
    /// <para>
    /// <b>The measured moment is the human's FIRST message</b>, ordered by the
    /// transcript's own <c>Sequence</c> — not the end of the conversation and
    /// not now. What the SLA measures is when the customer first heard from a
    /// person, and that is the timestamp on that line.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="InteractionMessageSender.VirtualAgent"/> lines are
    /// deliberately invisible here</b>, as are
    /// <see cref="InteractionMessageSender.System"/> and
    /// <see cref="InteractionMessageSender.Customer"/> lines. An AI reply is
    /// not a first human response: FR-SLA-05/ISSUE-019 exist because a machine
    /// answering in seconds would satisfy every target automatically and make
    /// the KPI meaningless — which is also why accepting a handoff does not
    /// record it either (confirmed decision 10). Only a person speaking to the
    /// customer does.
    /// </para>
    ///
    /// <para>
    /// Write-once is <see cref="Ticket.RecordFirstHumanResponse"/>'s own
    /// guarantee and <see cref="FirstHumanResponseRecorder"/>'s: a redelivered
    /// transcript re-reads the same first human line and changes nothing.
    /// </para>
    /// </summary>
    private async Task<DateTime?> TryRecordFirstHumanResponseAsync(
        Guid callerEmployeeId,
        Ticket? ticket,
        IReadOnlyList<TicketInteractionMessage> transcript,
        CancellationToken cancellationToken)
    {
        if (ticket is null || ticket.FirstHumanResponseAtUtc is not null)
        {
            return null;
        }

        var firstHumanMessage = transcript
            .Where(m => m.Sender == InteractionMessageSender.HumanAgent)
            // The EARLIEST human line by when it was sent (the sequence breaks a tie): a line delivered late but sent earlier is
            // the real first response, and ordering by delivery sequence alone could pick a later one and report a false breach.
            .OrderBy(m => m.SentAtUtc)
            .ThenBy(m => m.Sequence)
            .FirstOrDefault();

        if (firstHumanMessage is null)
        {
            return null;
        }

        var recorded = await firstHumanResponseRecorder.TryRecordAsync(
            ticket,
            firstHumanMessage.SentAtUtc,
            timeProvider.GetUtcNow().UtcDateTime,
            callerEmployeeId,
            actorIsSystem: true,
            FirstResponseSource.HumanAgentMessage,
            correlationId: Guid.NewGuid(),
            cancellationToken);

        return recorded ? firstHumanMessage.SentAtUtc : null;
    }

    /// <summary>
    /// Raises pending human work when an AI conversation has ended and nobody
    /// human ever took it — the safety net behind "an AI disconnect must never
    /// orphan the ticket".
    ///
    /// <para>
    /// <b>All four conditions must hold</b>, and each rules out a case where
    /// raising work would be wrong:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     The transcript carries <see cref="InteractionMessageSender.VirtualAgent"/>
    ///     activity. Without it this was not an AI conversation at all, and an
    ///     ordinary call ending is not pending human work.
    ///   </description></item>
    ///   <item><description>
    ///     No human ever handled it — no
    ///     <see cref="InteractionMessageSender.HumanAgent"/> line and no
    ///     resolved <c>HandledByUserId</c>. A conversation a person already
    ///     took needs no handoff; it had one.
    ///   </description></item>
    ///   <item><description>
    ///     No outstanding handoff exists. This is what makes the safety net
    ///     invisible when Genesys did its job and sent
    ///     <c>Handoff.Required = true</c>, and what stops a redelivered end
    ///     event raising a second work item.
    ///   </description></item>
    ///   <item><description>
    ///     The ticket is not Closed. Closed-ticket immutability is a standing
    ///     rule in this solution, and pending human work on a closed case is
    ///     work nobody should be asked to do.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>The department is the ticket's CURRENT one</b>, exactly as
    /// <c>GenesysAgentHandoffAppService.RequestAsync</c> reads it: work follows
    /// a transferred ticket. And the requested-at moment is the conversation's
    /// end, not "now" — the customer has been waiting since the AI dropped
    /// them, and the Human Wait metric must measure from there.
    /// </para>
    ///
    /// <para>
    /// A lost race against a concurrent handoff request surfaces at
    /// <c>SaveChangesAsync</c> as the filtered unique index rejecting the
    /// second open row. The caller treats that as "already requested", which
    /// it is — see <c>EndAsync</c>'s handling.
    /// </para>
    /// </summary>
    private async Task<TicketAgentHandoff?> TryAutoRaiseHandoffAsync(
        Guid callerEmployeeId,
        TicketInteraction interaction,
        Ticket? ticket,
        IReadOnlyList<TicketInteractionMessage> transcript,
        DateTime endedAtUtc,
        CancellationToken cancellationToken)
    {
        if (ticket is null || ticket.TicketStatus == TicketStatus.Closed)
        {
            return null;
        }

        var hasVirtualAgentActivity = transcript.Any(m => m.Sender == InteractionMessageSender.VirtualAgent);
        if (!hasVirtualAgentActivity)
        {
            return null;
        }

        var humanAlreadyHandledIt =
            interaction.HandledByUserId is not null
            || transcript.Any(m => m.Sender == InteractionMessageSender.HumanAgent);
        if (humanAlreadyHandledIt)
        {
            return null;
        }

        if (await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not null)
        {
            return null;
        }

        var handoff = new TicketAgentHandoff(
            ticket.TicketId,
            interaction.TicketInteractionId,
            ticket.CurrentDepartmentId,
            interaction.ChannelId,
            requestedAtUtc: endedAtUtc,
            createdAtUtc: timeProvider.GetUtcNow().UtcDateTime,
            mode: null,
            trigger: HandoffTrigger.AiConnectionLost,
            requestReason: "The AI conversation ended without a human agent taking it.");

        await handoffRepository.AddAsync(handoff, cancellationToken);

        var correlationId = Guid.NewGuid();
        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticket.TicketId, WorkflowEventType.HandoffRequested, endedAtUtc, actorEmployeeId: null,
                ticketApprovalId: null,
                note: "AiConnectionLost — the AI conversation ended without a human agent taking it.",
                correlationId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.HandoffAutoRaisedOnConversationEnd,
            nameof(TicketAgentHandoff),
            interaction.GenesysConversationId ?? interaction.TicketInteractionId.ToString(),
            beforeValue: null,
            afterValue:
                $"TicketId={ticket.TicketId};TicketInteractionId={interaction.TicketInteractionId};"
                + $"Status={handoff.Status};Trigger={handoff.Trigger};"
                + $"DepartmentId={ticket.CurrentDepartmentId};ChannelId={interaction.ChannelId};"
                + $"RequestedAtUtc={endedAtUtc:O};TicketStatus={ticket.TicketStatus}",
            correlationId,
            cancellationToken);

        return handoff;
    }

    /// <summary>
    /// Whether this message is already in the stored transcript.
    ///
    /// <para>
    /// Genesys' own message id decides it whenever one is supplied — that is
    /// what <c>messageId</c> is for. Without one there is no identifier to
    /// trust, so the fallback compares the facts that together identify a
    /// line of conversation: same sender, same instant, same body. Two
    /// genuinely distinct messages matching all three would be the same
    /// person saying the same thing at the same timestamp, which is a
    /// redelivery in every case that matters.
    /// </para>
    /// </summary>
    private static bool IsAlreadyStored(NormalizedMessage message, IReadOnlyList<TicketInteractionMessage> stored)
    {
        if (message.ExternalMessageId is { } externalId)
        {
            return stored.Any(m =>
                string.Equals(m.ExternalMessageId, externalId, StringComparison.Ordinal)
                // The same line first delivered without an id (a live push) and now carrying one (the final transcript).
                || (m.ExternalMessageId is null && SameLine(m, message)));
        }

        return stored.Any(m => m.ExternalMessageId is null && SameLine(m, message));
    }

    private static bool SameLine(TicketInteractionMessage stored, NormalizedMessage message) =>
        stored.Sender == message.Sender
        && stored.SentAtUtc == message.SentAtUtc
        && string.Equals(stored.Body, message.Body, StringComparison.Ordinal);

    /// <summary>
    /// Validates and normalizes every supplied message before a single row is
    /// written: an unknown sender or an empty body fails the whole request
    /// rather than being silently dropped or attributed to the wrong party.
    /// Order is the delivered order — never re-sorted by timestamp, because a
    /// provider clock is not guaranteed monotonic and two messages may share
    /// a timestamp.
    /// </summary>
    /// <summary>
    /// The id as it is STORED (blank → none, trimmed, at most the column length), so a retry compares against what was kept: comparing the raw
    /// value would never match a stored id that had to be shortened, and the retry would be stored again.
    /// </summary>
    private static string? NormalizeExternalId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim();
        return trimmed.Length <= TicketInteractionMessage.ExternalMessageIdMaxLength
            ? trimmed
            : trimmed[..TicketInteractionMessage.ExternalMessageIdMaxLength];
    }

    internal static bool IsValidTranscript(IReadOnlyList<GenesysTranscriptMessageDto>? transcript, out string? error) =>
        TryNormalizeTranscript(transcript, out _, out error);

    private static bool TryNormalizeTranscript(
        IReadOnlyList<GenesysTranscriptMessageDto>? transcript,
        out IReadOnlyList<NormalizedMessage> messages,
        out string? error)
    {
        messages = [];
        error = null;

        if (transcript is null || transcript.Count == 0)
        {
            return true;
        }

        var normalized = new List<NormalizedMessage>(transcript.Count);
        for (var index = 0; index < transcript.Count; index++)
        {
            var message = transcript[index];

            if (!NamedEnum.TryParse<InteractionMessageSender>(message.Sender, out var sender))
            {
                error =
                    $"Transcript message {index + 1} has an unrecognized sender '{message.Sender}'. "
                    + "Expected Customer, VirtualAgent, HumanAgent or System.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(message.Body))
            {
                error = $"Transcript message {index + 1} has an empty body.";
                return false;
            }

            normalized.Add(new NormalizedMessage(
                sender, message.SenderName, message.SenderId, message.SentAtUtc, message.Body, NormalizeExternalId(message.ExternalMessageId)));
        }

        messages = normalized;
        return true;
    }

    private sealed record NormalizedMessage(
        InteractionMessageSender Sender,
        string? SenderName,
        string? SenderId,
        DateTime SentAtUtc,
        string Body,
        string? ExternalMessageId);
}
