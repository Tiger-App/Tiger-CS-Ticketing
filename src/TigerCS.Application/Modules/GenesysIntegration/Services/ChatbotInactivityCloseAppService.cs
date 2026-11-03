using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>What one attempt to close an interaction's ticket for customer inactivity did.</summary>
public enum ChatbotInactivityCloseOutcome
{
    /// <summary>The ticket was closed by this call.</summary>
    Closed,

    /// <summary>Nothing to do: the integration/timeout is off, the interaction is unknown, or it is no longer (or not yet) due — including "already closed by an earlier run".</summary>
    NotDue,

    /// <summary>The re-check found the interaction should not be waiting any more (human assigned or requested, conversation ended, ticket not closable); the timer was cancelled and nothing was closed.</summary>
    Excluded,

    /// <summary>A customer reply, another worker or a ticket edit won the race; this call changed nothing. Safe — the other writer's outcome stands.</summary>
    LostRace
}

/// <summary>
/// <b>Two rules, never both on one ticket.</b>
/// <list type="bullet">
/// <item><b>Website/Chatbot (current):</b> a ticket whose Genesys chat conversation is tracked
/// (<see cref="TicketInteraction.LastCustomerInteractionAtUtc"/>) closes when the customer has done nothing for more than
/// <see cref="GenesysOptions.CustomerInactivityTimeout"/> (default 10 minutes) <i>since their last interaction</i> — never since
/// ticket creation. Each customer message restarts it. Reason: <see cref="CustomerInactiveReasonFor"/>.</item>
/// <item><b>Bot-prompt (original):</b> for everything that rule does not track (voice, Call Center, rows from before it), the
/// chatbot waited more than <see cref="GenesysOptions.AwaitingReplyTimeout"/> (5) for an answer. Unchanged.</item>
/// </list>
/// Call Center tickets (originating or current department) and human-managed tickets (owner, handler, open handoff) are never closed by the
/// first rule; a candidate found to be out of scope simply stops being tracked. A ticket already closed — e.g. by document delivery — is
/// never touched again.
///
/// Closes a ticket whose chatbot has been waiting on the customer for longer
/// than the configured time (<see cref="GenesysOptions.CustomerInactivityTimeout"/>,
/// default 5 minutes). Driven by a recurring server-side job — never by a
/// client — reading <c>TicketInteractions.AwaitingCustomerReplySinceUtc</c>,
/// so the timeout survives restarts and deploys.
///
/// <para>
/// <b>Re-checked at fire time, in this order, before anything is written:</b>
/// the integration is on and a timeout is configured; the interaction still
/// carries a timer that is strictly older than the timeout (a customer reply
/// clears it; a repeated webhook never moved it); the conversation has not
/// ended; no human follow-up is open (requested handoff, bot connection
/// failure — both raise a <see cref="TicketAgentHandoff"/>); no person owns the
/// ticket (a human assignment); the ticket is still open for work. Any
/// exclusion cancels the timer so it stops being a candidate, and closes
/// nothing.
/// </para>
///
/// <para>
/// <b>Race with a customer reply.</b> The timer column is a concurrency
/// token. The interaction is loaded, checked, and its timer cleared in the
/// same SaveChanges as the ticket closure; if a reply cleared the timer in
/// between, that write affects zero rows and the whole closure rolls back
/// (<see cref="ChatbotInactivityCloseOutcome.LostRace"/>). The reply wins.
/// </para>
///
/// <para>
/// <b>Idempotent.</b> The interaction's <c>InactivityClosedAtUtc</c> is
/// write-once and the Outbox lifecycle event has its own idempotency key, so a
/// repeated run, an overlapping run or a Hangfire retry closes once and
/// notifies once.
/// </para>
///
/// <para>
/// The closure goes through <see cref="TicketLifecycleAppService.CloseForCustomerInactivityAsync"/>:
/// the lifecycle's Resolved→Closed transitions, with outcome
/// <c>Cancelled</c> — never "Resolved" — and the audit reason below.
/// </para>
/// </summary>
public sealed class ChatbotInactivityCloseAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    ITicketAgentHandoffRepository handoffRepository,
    IDepartmentRepository departmentRepository,
    TicketLifecycleAppService lifecycleAppService,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IAuditEntryWriter? auditWriter = null)
{
    /// <summary>Upper bound on candidates examined per job run; the next run takes the rest.</summary>
    public const int BatchSize = 100;

    /// <summary>The audit/resolution reason, with the configured minutes (5 by default).</summary>
    public static string ReasonFor(int timeoutMinutes) =>
        $"Automatically closed — customer did not respond for more than {timeoutMinutes} minutes.";

    /// <summary>The closure reason of the Website/Chatbot rule, with the configured minutes (10 by default).</summary>
    public static string CustomerInactiveReasonFor(int timeoutMinutes) =>
        $"Automatically closed — customer inactive for more than {timeoutMinutes} minutes.";

    /// <summary>Ids of interactions past either timeout — candidates only; each is re-checked by <see cref="TryCloseAsync"/>.</summary>
    public async Task<IReadOnlyList<long>> ListDueAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var due = new List<long>();
        if (options.CustomerInactivityTimeout is { } inactivity)
        {
            due.AddRange(await conversationRepository.ListCustomerInactiveOlderThanAsync(now - inactivity, BatchSize, cancellationToken));
        }

        if (options.AwaitingReplyTimeout is { } awaiting)
        {
            due.AddRange(await conversationRepository.ListAwaitingReplyOlderThanAsync(now - awaiting, BatchSize, cancellationToken));
        }

        return [.. due.Distinct().Take(BatchSize)];
    }

    public async Task<ChatbotInactivityCloseOutcome> TryCloseAsync(long ticketInteractionId, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return ChatbotInactivityCloseOutcome.NotDue;
        }

        var interaction = await conversationRepository.GetByIdAsync(ticketInteractionId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Gone, ended (End clears both clocks) or already closed by an earlier run: nothing to do, nothing to write.
        if (interaction is null || interaction.IsEnded || interaction.InactivityClosedAtUtc is not null)
        {
            return ChatbotInactivityCloseOutcome.NotDue;
        }

        // A tracked conversation is governed by the customer-interaction clock ONLY; the bot-prompt clock governs the rest.
        // (Otherwise the shorter bot-prompt rule would close a tracked chat at 5 minutes.)
        return interaction.LastCustomerInteractionAtUtc is not null
            ? await TryCloseForCustomerInactivityAsync(interaction, now, cancellationToken)
            : await TryCloseForUnansweredPromptAsync(interaction, now, cancellationToken);
    }

    private async Task<ChatbotInactivityCloseOutcome> TryCloseForCustomerInactivityAsync(
        TicketInteraction interaction, DateTime now, CancellationToken cancellationToken)
    {
        if (options.CustomerInactivityTimeout is not { } timeout || !interaction.IsCustomerInactive(now, timeout))
        {
            return ChatbotInactivityCloseOutcome.NotDue;
        }

        var reporter = interaction.LastCustomerInteractionReportedByEmployeeId;
        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);

        var excluded = reporter is null
            || ticket is null
            || !interaction.IsWebsiteOrChatbotConversation
            || interaction.HandledByUserId is not null
            || ticket.TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || ticket.CurrentOwnerEmployeeId is not null
            || await CustomerInactivityScope.IsCallCenterTicketAsync(ticket!, departmentRepository, cancellationToken)
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not null;

        // A human agent is working the conversation (a HumanAgent line is in the transcript): the customer's silence is not abandonment,
        // so the ticket is never closed for inactivity — even when no owner, handler or handoff has been recorded yet.
        var humanActive = !excluded && await HumanAgentHasSpokenAsync(interaction, cancellationToken);
        if (humanActive)
        {
            return await StandDownForHumanAsync(interaction, ticket!, reporter, cancellationToken);
        }

        if (excluded)
        {
            // Out of scope (closed, human-managed, Call Center): stop tracking so it is not re-examined every run.
            // A later customer interaction starts the clock again if it is back in scope.
            interaction.StopTrackingCustomerInteraction();
            return await SaveExcludedAsync(cancellationToken);
        }

        return await CloseAsync(
            interaction, ticket!, reporter!.Value, CustomerInactiveReasonFor((int)timeout.TotalMinutes), cancellationToken);
    }

    private async Task<ChatbotInactivityCloseOutcome> TryCloseForUnansweredPromptAsync(
        TicketInteraction interaction, DateTime now, CancellationToken cancellationToken)
    {
        if (options.AwaitingReplyTimeout is not { } timeout || !interaction.IsInactivityTimeoutDue(now, timeout))
        {
            return ChatbotInactivityCloseOutcome.NotDue;
        }

        var reporter = interaction.AwaitingCustomerReplyReportedByEmployeeId;
        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);

        var excluded = reporter is null
            || ticket is null
            || ticket.TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || ticket.CurrentOwnerEmployeeId is not null
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not null;

        if (!excluded && await HumanAgentHasSpokenAsync(interaction, cancellationToken))
        {
            return await StandDownForHumanAsync(interaction, ticket!, reporter, cancellationToken);
        }

        if (excluded)
        {
            // Stop re-examining it every run; a later chatbot prompt starts a new timer.
            return await CancelTimerAsync(interaction, cancellationToken);
        }

        return await CloseAsync(interaction, ticket!, reporter!.Value, ReasonFor((int)timeout.TotalMinutes), cancellationToken);
    }

    private async Task<bool> HumanAgentHasSpokenAsync(TicketInteraction interaction, CancellationToken cancellationToken) =>
        (await conversationRepository.ListMessagesAsync(interaction.TicketInteractionId, cancellationToken))
        .Any(m => m.Sender == InteractionMessageSender.HumanAgent);

    /// <summary>
    /// A person has spoken in the conversation: mark the ticket as human work (sticky, stops both clocks) and record why nothing was closed.
    /// Nothing is closed or reopened; the audit entry is the evidence the Activity timeline shows.
    /// </summary>
    private async Task<ChatbotInactivityCloseOutcome> StandDownForHumanAsync(
        TicketInteraction interaction, Ticket ticket, Guid? reporter, CancellationToken cancellationToken)
    {
        interaction.MarkRequiresHumanFollowUp();
        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(
                reporter,
                "ChatbotInactivityCloseSkippedHumanActive",
                "Ticket",
                ticket.TicketId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                beforeValue: null,
                afterValue: $"TicketId={ticket.TicketId};InteractionId={interaction.TicketInteractionId};Reason=HumanAgentActive;TicketStatus={ticket.TicketStatus}",
                correlationId: Guid.NewGuid(),
                cancellationToken);
        }

        return await SaveExcludedAsync(cancellationToken);
    }

    private async Task<ChatbotInactivityCloseOutcome> CloseAsync(
        TicketInteraction interaction, Ticket ticket, Guid reporter, string reason, CancellationToken cancellationToken)
    {
        TicketMutationResult result;
        try
        {
            result = await lifecycleAppService.CloseForCustomerInactivityAsync(
                reporter, ticket.TicketId, reason,
                beforeCommit: closedAtUtc => interaction.RecordInactivityClosure(closedAtUtc),
                cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return ChatbotInactivityCloseOutcome.LostRace;
        }

        return result.Outcome switch
        {
            TicketMutationOutcome.Success => ChatbotInactivityCloseOutcome.Closed,
            TicketMutationOutcome.ConcurrencyConflict => ChatbotInactivityCloseOutcome.LostRace,
            // Closed meanwhile / not eligible: the same exclusion as above.
            _ => await CancelTimerAsync(interaction, cancellationToken)
        };
    }

    private async Task<ChatbotInactivityCloseOutcome> SaveExcludedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return ChatbotInactivityCloseOutcome.LostRace;
        }

        return ChatbotInactivityCloseOutcome.Excluded;
    }

    private async Task<ChatbotInactivityCloseOutcome> CancelTimerAsync(
        Domain.Modules.Ticketing.TicketInteraction interaction, CancellationToken cancellationToken)
    {
        interaction.CancelAwaitingCustomerReply();
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return ChatbotInactivityCloseOutcome.LostRace;
        }

        return ChatbotInactivityCloseOutcome.Excluded;
    }
}
