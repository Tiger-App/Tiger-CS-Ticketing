using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The inbound half of pending human work: Genesys reporting that an
/// interaction needs a human agent, and later that one has taken it.
///
/// <para>
/// <b>This is not "a callback request".</b> Genesys carries phone, website
/// chat, chatbot, WhatsApp, social media and more. On every one of them the
/// same thing happens — routing or a virtual agent decides a human is needed
/// — and the business rule is identical: <i>do not lose the inquiry, and make
/// it visible to agents</i>. Only the continuation
/// <see cref="Domain.Modules.Ticketing.AgentHandoffMode"/> differs, and a
/// callback is one of those modes rather than the concept.
/// </para>
///
/// <para>
/// <b>Never a second ticket.</b> The conversation already has one, created
/// when the inquiry arrived. A handoff attaches pending work to that ticket
/// and its interaction — whether a human takes over live or the customer
/// waits. That holds for the AI-first journey too: customer → bot → human is
/// one ticket throughout.
/// </para>
///
/// <para>
/// <b>Idempotent on the conversation.</b> A conversation with outstanding
/// human work answers <see cref="GenesysHandoffOutcome.AlreadyRequested"/>
/// with that same work item; a filtered unique index over (interaction,
/// unresolved) makes it hold under concurrent redelivery rather than only
/// under sequential retries. When Genesys supplies its own work-item id, that
/// is checked first as the stronger key — but nothing is invented in its
/// absence.
/// </para>
///
/// <para>
/// <b>TigerCS executes nothing.</b> No dialling, no chat transport, no
/// WhatsApp or social sending, no queue scheduling. Genesys owns all of it;
/// this records the business state so agents can see the work and act on the
/// ticket behind it.
/// </para>
/// </summary>
public sealed class GenesysAgentHandoffAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketAgentHandoffRepository handoffRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    ITicketRepository ticketRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    IDepartmentRepository? departmentRepository = null,
    ITicketAssignmentRepository? ticketAssignmentRepository = null)
{
    public async Task<GenesysHandoffResult> RequestAsync(
        Guid callerEmployeeId, GenesysHandoffRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.ConversationIdRequired);
        }

        // Normalized into TigerCS' own vocabulary, or refused. Absent is
        // fine — an unstated mode is recorded as unstated, never guessed
        // from the channel.
        AgentHandoffMode? mode = null;
        if (!string.IsNullOrWhiteSpace(request.Mode))
        {
            if (!NamedEnum.TryParse<AgentHandoffMode>(request.Mode, out var parsed))
            {
                return GenesysHandoffResult.Failure(
                    GenesysHandoffOutcome.InvalidMode,
                    $"'{request.Mode}' is not a recognized follow-up mode. Use Callback, ContinueChat, ReplyInChannel or HumanTakeover, or omit it.");
            }

            mode = parsed;
        }

        // Same rule as Mode: normalized into TigerCS' own vocabulary or
        // refused, and absent stays absent. Never derived from the channel or
        // from the conversation's end reason.
        HandoffTrigger? trigger = null;
        if (!string.IsNullOrWhiteSpace(request.Trigger))
        {
            if (!NamedEnum.TryParse<HandoffTrigger>(request.Trigger, out var parsedTrigger))
            {
                return GenesysHandoffResult.Failure(
                    GenesysHandoffOutcome.InvalidTrigger,
                    $"'{request.Trigger}' is not a recognized handoff trigger. Use CustomerRequestedHuman, "
                    + "AiConnectionLost, AiEscalated, RoutingDecision or AgentTransfer, or omit it.");
            }

            trigger = parsedTrigger;
        }

        var conversationId = request.ConversationId.Trim();

        var interaction = await conversationRepository.ResolveAsync(conversationId, request.TicketId, cancellationToken);
        if (interaction is null)
        {
            return GenesysHandoffResult.Failure(
                GenesysHandoffOutcome.ConversationNotFound,
                "This conversation never produced a ticket, so there is nothing to attach human follow-up to.");
        }

        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);
        if (ticket is null)
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.ConversationNotFound);
        }

        // Idempotency, before anything is written. Genesys' own work-item id
        // is the stronger key when supplied; the conversation's outstanding
        // work is the key that always exists.
        var workItemId = NullIfBlank(request.WorkItemId);
        if (workItemId is not null
            && await handoffRepository.GetByExternalWorkItemIdAsync(workItemId, cancellationToken) is { } byWorkItem)
        {
            return Existing(byWorkItem, ticket);
        }

        if (await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is { } alreadyOpen)
        {
            return Existing(alreadyOpen, ticket);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var requestedAtUtc = request.RequestedAtUtc ?? now;

        // A live transfer names the agent, so the work is Assigned from the
        // start and never appears as "waiting". Nobody available means
        // WaitingForAgent — and the work list.
        var agentId = request.AgentAvailable ? NullIfBlank(request.AgentId) : null;

        // A human was requested: Call Center is the first human queue. A ticket nobody owns yet (and that is not already a
        // deliberate agent-to-agent transfer) moves there; its previous department stays in the audit trail. A ticket a person
        // already owns is left with them.
        var callCenter = await ResolveCallCenterRoutingAsync(ticket, trigger, cancellationToken);
        var routedFromDepartmentId = ticket.CurrentDepartmentId;
        if (callCenter is not null)
        {
            ticket.TransferToDepartment(callCenter.DepartmentId);
        }

        // The ticket's CURRENT department, not its originating one: work
        // follows a transferred ticket.
        var handoff = new TicketAgentHandoff(
            ticket.TicketId,
            interaction.TicketInteractionId,
            ticket.CurrentDepartmentId,
            interaction.ChannelId,
            requestedAtUtc,
            now,
            mode,
            trigger,
            request.Reason,
            workItemId,
            assignedEmployeeId: null,
            genesysAgentId: agentId,
            assignedAtUtc: agentId is not null ? requestedAtUtc : null);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await handoffRepository.AddAsync(handoff, cancellationToken);
        interaction.RecordAgentIfAbsent(request.AgentId, request.AgentName);

        var requestCorrelationId = Guid.NewGuid();
        if (callCenter is not null)
        {
            await auditWriter.WriteAsync(
                actorEmployeeId: null, "Transfer", "Ticket", ticket.TicketId.ToString(),
                beforeValue: $"DepartmentId={routedFromDepartmentId};AssignedEmployeeId=DepartmentQueue",
                afterValue: $"DepartmentId={callCenter.DepartmentId};Reason=Human agent requested — routed to Call Center",
                requestCorrelationId, cancellationToken);

            if (ticketAssignmentRepository is not null
                && await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken) is { } currentAssignment)
            {
                currentAssignment.MarkSuperseded();
            }

            await workflowEventRepository.AddAsync(
                TicketWorkflowEvent.ForTransfer(
                    ticket.TicketId, requestedAtUtc, actorEmployeeId: null, TicketTransferReason.SystemRouting,
                    routedFromDepartmentId, callCenter.DepartmentId, toEmployeeId: null,
                    "Human agent requested — Call Center first", requestCorrelationId),
                cancellationToken);
        }

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticket.TicketId, WorkflowEventType.HandoffRequested, requestedAtUtc, actorEmployeeId: null,
                ticketApprovalId: null,
                note: $"{handoff.Trigger?.ToString() ?? "Handoff requested"}"
                    + (handoff.RequestReason is { } why ? $" — {why}" : string.Empty),
                requestCorrelationId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "RequestAgentHandoff", nameof(TicketAgentHandoff), conversationId,
            beforeValue: null,
            afterValue:
                $"TicketId={ticket.TicketId};ChannelId={interaction.ChannelId};Status={handoff.Status};"
                + $"Mode={handoff.Mode?.ToString() ?? "(unstated)"};"
                + $"Trigger={handoff.Trigger?.ToString() ?? "(unstated)"};"
                + $"WorkItemId={workItemId ?? "(none)"}",
            requestCorrelationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // A concurrent redelivery won the race. The winner's work item is
            // the answer — exactly as a duplicated inquiry returns the
            // winner's ticket.
            var winner = await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken);
            return winner is not null
                ? Existing(winner, ticket)
                : GenesysHandoffResult.Failure(GenesysHandoffOutcome.AlreadyRequested);
        }

        await transaction.CommitAsync(cancellationToken);

        return new GenesysHandoffResult(
            GenesysHandoffOutcome.HandoffRecorded, handoff.TicketAgentHandoffId,
            ticket.TicketId, ticket.TicketNumber, handoff.Status.ToString());
    }

    /// <summary>The Call Center department when this handoff must route the ticket there; null when it must not (already there, owned by a person, closed, an explicit agent transfer, or no Call Center configured).</summary>
    private async Task<Domain.Modules.IdentityAndAccess.Department?> ResolveCallCenterRoutingAsync(
        Ticket ticket, HandoffTrigger? trigger, CancellationToken cancellationToken)
    {
        if (departmentRepository is null
            || ticket.TicketStatus == TicketStatus.Closed
            || ticket.CurrentOwnerEmployeeId is not null
            || trigger == HandoffTrigger.AgentTransfer)
        {
            return null;
        }

        var callCenter = (await departmentRepository.ListAsync(activeOnly: true, cancellationToken)).FirstOrDefault(d =>
            string.Equals(d.Code, Reporting.Services.TeamPerformanceAppService.CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase));
        return callCenter is null || callCenter.DepartmentId == ticket.CurrentDepartmentId ? null : callCenter;
    }

    /// <summary>
    /// Genesys reporting which agent has taken the pending work. Applies to
    /// the existing work item — never a second one — and is idempotent: the
    /// same agent reported twice changes nothing.
    /// </summary>
    public async Task<GenesysHandoffResult> UpdateAssignmentAsync(
        Guid callerEmployeeId, GenesysHandoffAssignmentDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.ConversationIdRequired);
        }

        var agentId = NullIfBlank(request.AgentId);
        if (agentId is null)
        {
            return GenesysHandoffResult.Failure(
                GenesysHandoffOutcome.AgentRequired, "An assignment must name the agent who took the work.");
        }

        var interaction = await conversationRepository.ResolveAsync(request.ConversationId.Trim(), request.TicketId, cancellationToken);
        if (interaction is null)
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.ConversationNotFound);
        }

        var handoff = await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken);
        if (handoff is null)
        {
            return GenesysHandoffResult.Failure(
                GenesysHandoffOutcome.NoOpenHandoff,
                "This conversation has no outstanding human work to assign.");
        }

        var ticket = await ticketRepository.GetByIdAsync(handoff.TicketId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var beforeStatus = handoff.Status;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Genesys names agents in its own identifier space. TigerCS does not
        // invent an employee mapping for them — the id is recorded as the
        // external string it is, and AssignedEmployeeId stays null until a
        // TigerCS agent takes the work here.
        handoff.AssignTo(employeeId: null, genesysAgentId: agentId, assignedAtUtc: now);
        interaction.RecordAgentIfAbsent(agentId, request.AgentName);

        var assignCorrelationId = Guid.NewGuid();
        if (beforeStatus != handoff.Status)
        {
            await workflowEventRepository.AddAsync(
                new TicketWorkflowEvent(
                    handoff.TicketId, WorkflowEventType.HandoffAssigned, now, actorEmployeeId: null,
                    ticketApprovalId: null, note: $"Genesys agent {agentId} took the interaction.",
                    assignCorrelationId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerEmployeeId, "UpdateAgentHandoffAssignment", nameof(TicketAgentHandoff),
            handoff.TicketAgentHandoffId.ToString(),
            beforeValue: $"Status={beforeStatus}",
            afterValue: $"Status={handoff.Status};GenesysAgentId={agentId}",
            assignCorrelationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GenesysHandoffResult(
            GenesysHandoffOutcome.AssignmentRecorded, handoff.TicketAgentHandoffId,
            handoff.TicketId, ticket?.TicketNumber, handoff.Status.ToString());
    }

    /// <summary>
    /// Genesys standing the pending human work down — the AI reconnected and
    /// resumed, the customer left, or the business case went away. The inbound
    /// counterpart to the agent-facing Cancel action.
    ///
    /// <para>
    /// <b>The ticket is deliberately untouched.</b> Standing down the human
    /// work says nothing about the case: no status change, no owner change, no
    /// resolution, no closure. Exactly as completing the work says nothing —
    /// the two lifecycles are read side by side and never conflated.
    /// </para>
    ///
    /// <para>
    /// <b>A reason is required</b>, matching <see cref="TicketAgentHandoff.Cancel"/>'s
    /// own rule: pending customer work is never dropped without a recorded
    /// why. Genesys supplies it in the same <c>Reason</c> field it uses when
    /// asking for a human.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent per the existing terminal-state convention.</b> Nothing
    /// outstanding, or work already Completed/Cancelled, answers
    /// <see cref="GenesysHandoffOutcome.AlreadyResolved"/> and writes nothing —
    /// the same shape as a redelivered request answering
    /// <see cref="GenesysHandoffOutcome.AlreadyRequested"/>. A redelivered
    /// stand-down therefore never overwrites the first one's reason or moves
    /// its timestamp.
    /// </para>
    /// </summary>
    public async Task<GenesysHandoffResult> CancelAsync(
        Guid callerEmployeeId, GenesysHandoffCancellationDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.ConversationIdRequired);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return GenesysHandoffResult.Failure(
                GenesysHandoffOutcome.ReasonRequired,
                "Standing down pending human work needs a reason — work is never dropped without a recorded why.");
        }

        var interaction = await conversationRepository.ResolveAsync(request.ConversationId.Trim(), request.TicketId, cancellationToken);
        if (interaction is null)
        {
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.ConversationNotFound);
        }

        var handoff = await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken);
        if (handoff is null)
        {
            // Nothing outstanding. Not an error: a redelivered stand-down, or
            // one arriving after an agent already finished the work.
            return GenesysHandoffResult.Failure(
                GenesysHandoffOutcome.AlreadyResolved,
                "This conversation has no outstanding human work to stand down.");
        }

        var ticket = await ticketRepository.GetByIdAsync(handoff.TicketId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var beforeStatus = handoff.Status;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            handoff.Cancel(now, request.Reason);
        }
        catch (AgentHandoffAlreadyResolvedException)
        {
            // Lost a race with a concurrent completion/cancellation. The
            // winner's terminal state stands.
            return GenesysHandoffResult.Failure(GenesysHandoffOutcome.AlreadyResolved);
        }

        var cancelCorrelationId = Guid.NewGuid();
        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                handoff.TicketId, WorkflowEventType.HandoffCancelled, now, actorEmployeeId: null,
                ticketApprovalId: null, handoff.ResolutionNote, cancelCorrelationId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "CancelAgentHandoffFromGenesys", nameof(TicketAgentHandoff),
            handoff.TicketAgentHandoffId.ToString(),
            beforeValue: $"Status={beforeStatus}",
            afterValue:
                $"Status={handoff.Status};Reason={handoff.ResolutionNote};"
                + $"TicketId={handoff.TicketId};TicketStatus={ticket?.TicketStatus.ToString() ?? "(unknown)"}",
            cancelCorrelationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GenesysHandoffResult(
            GenesysHandoffOutcome.HandoffCancelled, handoff.TicketAgentHandoffId,
            handoff.TicketId, ticket?.TicketNumber, handoff.Status.ToString());
    }

    private static GenesysHandoffResult Existing(TicketAgentHandoff handoff, Ticket ticket) =>
        new(GenesysHandoffOutcome.AlreadyRequested, handoff.TicketAgentHandoffId,
            ticket.TicketId, ticket.TicketNumber, handoff.Status.ToString());

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
