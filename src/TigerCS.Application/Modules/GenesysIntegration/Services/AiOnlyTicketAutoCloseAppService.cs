using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Settings of the AI-only time-limit closure (<c>AiOnlyAutoClose</c> section). <b>Off by default in every environment</b>: the recurring job is
/// registered only where <see cref="Enabled"/> is set to true, so shipping this code enables nothing in Production.
/// </summary>
public sealed class AiOnlyAutoCloseOptions
{
    public const string SectionName = "AiOnlyAutoClose";
    public const int DefaultThresholdHours = 18;
    public const int DefaultBatchSize = 50;
    public const int DefaultMaxBatchesPerRun = 4;

    /// <summary>Whether the recurring job is registered at all. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long after creation (UTC) an AI-only ticket may stay open. Default 18; zero or negative falls back to the default.</summary>
    public int ThresholdHours { get; set; } = DefaultThresholdHours;

    /// <summary>Candidates examined per batch (small, to keep CPU and database load low). Clamped to 1–200.</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>Batches per run; the next run (15 minutes later) continues. Clamped to 1–20.</summary>
    public int MaxBatchesPerRun { get; set; } = DefaultMaxBatchesPerRun;

    public TimeSpan Threshold => TimeSpan.FromHours(ThresholdHours > 0 ? ThresholdHours : DefaultThresholdHours);
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 200);
    public int EffectiveMaxBatches => Math.Clamp(MaxBatchesPerRun, 1, 20);
}

/// <summary>What one attempt to close a ticket for the AI-only time limit did.</summary>
public enum AiOnlyAutoCloseOutcome
{
    /// <summary>Closed by this call.</summary>
    Closed,

    /// <summary>Left alone: not old enough, already closed/not open, or a person is (or may be) involved. Nothing was written.</summary>
    NotEligible,

    /// <summary>Another writer changed the ticket at the same moment; this call changed nothing and the next run re-evaluates it.</summary>
    LostRace
}

/// <summary>
/// Closes tickets that only the AI virtual agent ever handled and that have stayed open for more than
/// <see cref="AiOnlyAutoCloseOptions.Threshold"/> (18 hours) since creation (<see cref="Ticket.CreatedAtUtc"/>, UTC).
///
/// <para>
/// <b>"AI only" is judged from stored facts, never from display text.</b> It mirrors what the ticket page shows as
/// <c>Handled by = AI (virtual agent) — no human involved</c> (<see cref="TicketInteractionQueryAppService"/>: handling state <c>Ai</c>) and is
/// deliberately stricter wherever a person might be involved. ALL must hold, for every interaction of the ticket: it is a Genesys interaction
/// (a locally created one is a person's work); no <c>HandledByUserId</c>; no <c>RequiresHumanFollowUp</c>; no <c>HumanAgent</c> transcript line;
/// on the phone no Genesys agent. For the ticket: no owner, no recorded interaction handler, no handoff other than a cancelled / not-required
/// one (waiting, assigned, in progress and completed all mean a human was asked for or took it) and no internal note (written by people only).
/// </para>
///
/// <para>
/// <b>Closure</b> goes through <see cref="TicketLifecycleAppService.CloseForAiOnlyTimeoutAsync"/>: the existing system Resolved → Closed path,
/// outcome Cancelled, reason <see cref="ClosureReason"/>, status history and an <c>AutoCloseAiOnlyTimeout</c> audit entry, in one transaction.
/// No customer notification is enqueued. Conversation history, interactions and every other ticket field are untouched.
/// </para>
///
/// <para>
/// <b>Safe to repeat.</b> Every fact is re-read at close time; a Closed ticket is never touched again (the lifecycle refuses it and writes
/// nothing), and the ticket's concurrency token makes an overlapping run or a late human action win over the job.
/// </para>
/// </summary>
public sealed class AiOnlyTicketAutoCloseAppService(
    AiOnlyAutoCloseOptions options,
    ITicketRepository ticketRepository,
    ITicketInteractionRepository interactionRepository,
    IGenesysConversationRepository conversationRepository,
    ITicketInteractionHandlerRepository handlerRepository,
    ITicketAgentHandoffRepository handoffRepository,
    ITicketNoteRepository noteRepository,
    TicketLifecycleAppService lifecycleAppService,
    TimeProvider timeProvider)
{
    /// <summary>The exact closure reason recorded on the resolution, status history and audit entry.</summary>
    public const string ClosureReason = "Automatically closed - AI only (18 hours)";

    public AiOnlyAutoCloseOptions Options => options;

    /// <summary>Next batch of candidate ticket ids after <paramref name="afterTicketId"/> (0 to start). Candidates only — see <see cref="TryCloseAsync"/>.</summary>
    public Task<IReadOnlyList<long>> ListCandidatesAsync(long afterTicketId, CancellationToken cancellationToken = default) =>
        ticketRepository.ListAiOnlyAutoCloseCandidateIdsAsync(
            timeProvider.GetUtcNow().UtcDateTime - options.Threshold, afterTicketId, options.EffectiveBatchSize, cancellationToken);

    public async Task<AiOnlyAutoCloseOutcome> TryCloseAsync(long ticketId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);

        // Older than the threshold (strictly), still open for work and nobody's: anything else is not this rule's business.
        if (ticket is null
            || now - ticket.CreatedAtUtc <= options.Threshold
            || ticket.TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || ticket.CurrentOwnerEmployeeId is not null)
        {
            return AiOnlyAutoCloseOutcome.NotEligible;
        }

        var interactions = await interactionRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        if (interactions.Count == 0 || !await IsAiOnlyAsync(ticket, interactions, cancellationToken))
        {
            return AiOnlyAutoCloseOutcome.NotEligible;
        }

        // The closure is attributed to the integration account that created the ticket (resolution rows need a real user).
        var actor = ticket.CreatedByEmployeeId
            ?? interactions.Select(i => i.LastCustomerInteractionReportedByEmployeeId ?? i.AwaitingCustomerReplyReportedByEmployeeId).FirstOrDefault(id => id is not null);
        if (actor is null)
        {
            return AiOnlyAutoCloseOutcome.NotEligible;
        }

        var detail = $"Handling=AI;ThresholdHours={(int)options.Threshold.TotalHours};CreatedAtUtc={ticket.CreatedAtUtc:O};"
            + $"InteractionIds={string.Join(',', interactions.Select(i => i.TicketInteractionId))}";

        TicketMutationResult result;
        try
        {
            result = await lifecycleAppService.CloseForAiOnlyTimeoutAsync(actor.Value, ticketId, ClosureReason, detail, cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return AiOnlyAutoCloseOutcome.LostRace;
        }

        return result.Outcome switch
        {
            TicketMutationOutcome.Success => AiOnlyAutoCloseOutcome.Closed,
            TicketMutationOutcome.ConcurrencyConflict => AiOnlyAutoCloseOutcome.LostRace,
            _ => AiOnlyAutoCloseOutcome.NotEligible
        };
    }

    /// <summary>The stored-fact definition of "handled by the AI alone" — see the class remarks.</summary>
    private async Task<bool> IsAiOnlyAsync(Ticket ticket, IReadOnlyList<TicketInteraction> interactions, CancellationToken cancellationToken)
    {
        foreach (var i in interactions)
        {
            if (i.Source != InteractionContextSource.Genesys
                || i.HandledByUserId is not null
                || i.RequiresHumanFollowUp
                || (i.ChannelId == WellKnownChannels.Phone && !string.IsNullOrWhiteSpace(i.GenesysAgentId)))
            {
                return false;
            }
        }

        if ((await handlerRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Count > 0
            || (await handoffRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken))
                .Any(h => h.Status is not (AgentHandoffStatus.Cancelled or AgentHandoffStatus.NotRequired))
            || await noteRepository.CountByTicketIdAsync(ticket.TicketId, cancellationToken) > 0)
        {
            return false;
        }

        var messages = await conversationRepository.ListMessagesForInteractionsAsync(
            [.. interactions.Select(i => i.TicketInteractionId)], cancellationToken);
        return !messages.Values.Any(list => list.Any(m => m.Sender == InteractionMessageSender.HumanAgent));
    }
}
