using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Settings of the VOICE call reconciler (config section <c>VoiceCallReconciliation</c>). It is the ONLY place that decides "nobody answered this call":
/// the end of a call is never that decision on its own, because Genesys can still send the answering agent (<c>routing.agentId</c>) after <c>ended</c>.
///
/// <para>
/// <b>Proposed value and its impact - <see cref="NoAnswerWindowSeconds"/> = 300 (5 minutes).</b> It is measured from the moment TigerCS RECEIVED the end event
/// (<see cref="TicketInteraction.EndReceivedAtUtc"/>), never from the sender's possibly delayed event time. A failed-AI call is therefore dealt to an agent by Round
/// Robin <i>window + up to one run interval</i> (default 5 min + 1 min) after the end was received, instead of one second after it as before. During the window
/// the ticket stays open in the Call Center queue with the AI Agent as the owner; the first-response and resolution SLA clocks are NOT paused or delayed (they run
/// from creation), and an answering agent that arrives inside the window still gets the ticket directly. A shorter window (for example 60 s) assigns sooner but
/// risks dealing a ticket to a second person when the answer event is delayed; a longer one lengthens the unattended period. Change it with
/// <c>VoiceCallReconciliation:NoAnswerWindowSeconds</c> - no deployment is needed (the setting is re-read every run).
/// </para>
/// </summary>
public sealed class VoiceCallReconciliationOptions
{
    public const string SectionName = "VoiceCallReconciliation";
    public const int DefaultNoAnswerWindowSeconds = 300;

    /// <summary>The recurring run is registered only when true. Default true: Round Robin for a failed-AI voice call must not silently stop existing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True classifies and logs what WOULD happen and writes nothing. Default false.</summary>
    public bool DryRun { get; set; }

    /// <summary>Seconds, after the end of a call was received, before "nobody answered" is concluded. See the class remarks. Negative falls back to the default; 0 disables the wait.</summary>
    public int NoAnswerWindowSeconds { get; set; } = DefaultNoAnswerWindowSeconds;

    /// <summary>Only calls whose end was received within this many hours are concluded: a legacy backlog is never swept up by this job. Default 24.</summary>
    public int LookbackHours { get; set; } = 24;

    /// <summary>Seconds between runs. Clamped to 15-3600. Default 60.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Tickets examined per page.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Most tickets WRITTEN per run.</summary>
    public int MaxActionsPerRun { get; set; } = 50;

    public TimeSpan NoAnswerWindow => TimeSpan.FromSeconds(NoAnswerWindowSeconds < 0 ? DefaultNoAnswerWindowSeconds : NoAnswerWindowSeconds);
    public TimeSpan Lookback => TimeSpan.FromHours(LookbackHours > 0 ? LookbackHours : 24);
}

/// <summary>Why a voice ticket is (not) acted on.</summary>
public enum VoiceReconcileClass
{
    /// <summary>Not a phone-only ticket, not Open, or already owned: never this job's business.</summary>
    NotApplicable,

    /// <summary>An approval is pending; the approval process decides first.</summary>
    PendingApproval,

    /// <summary>The call has not ended (or no end was received yet): a human may still answer. Nothing happens.</summary>
    CallInProgress,

    /// <summary>The call ended, but the reconciliation window has not elapsed: the answering agent may still be reported. Nothing happens.</summary>
    WaitingForRoutingWindow,

    /// <summary>The call ended longer ago than the look-back: left alone (never a backlog sweep).</summary>
    OutsideLookback,

    /// <summary>A person deliberately released the ticket to the queue (an audited Unassign by a person): left for them, never redistributed.</summary>
    ReleasedByPerson,

    /// <summary>A person was involved (answered / handled / replied) - a human owner is NOT chosen by Round Robin here.</summary>
    HumanInvolved,

    /// <summary>A person answered but their Genesys id still maps to nobody (or an ineligible account): flagged, retried, never Round Robin.</summary>
    AnsweringAgentMappingPending,

    /// <summary>The answering agent's mapping is now valid (or a recorded handler never got the ticket): give the ticket to THAT agent.</summary>
    AnsweringAgentResolvable,

    /// <summary>
    /// The call ended, nobody answered, the window passed (or the ticket was already flagged as needing a person), the AI did not close the ticket: the ticket is
    /// dealt to an eligible Call Center agent by the existing Round Robin, and retried every run until somebody is eligible.
    /// </summary>
    NoAnswerConcluded,
}

public enum VoiceReconcileAction
{
    None,
    AssignAnsweringAgent,
    ConcludeNoAnswer,
}

public sealed record VoiceReconcileClassification(VoiceReconcileClass Class, VoiceReconcileAction Action);

/// <summary>Pure classification of a voice ticket - no I/O, so every rule is unit-testable without a database.</summary>
public static class VoiceCallClassifier
{
    public static VoiceReconcileClassification Classify(UnassignedTicketEvidence e, DateTime nowUtc, VoiceCallReconciliationOptions options)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(options);

        VoiceReconcileClassification Of(VoiceReconcileClass c, VoiceReconcileAction a = VoiceReconcileAction.None) => new(c, a);

        if (!e.GenesysIsPhoneOnly || e.Status != TicketStatus.Open || e.OwnerEmployeeId is not null)
        {
            return Of(VoiceReconcileClass.NotApplicable);
        }

        if (e.PendingApproval)
        {
            return Of(VoiceReconcileClass.PendingApproval);
        }

        // A person deliberately released it to the queue: it is theirs to re-assign, never dealt again automatically.
        if (e.ReleasedByPerson)
        {
            return Of(VoiceReconcileClass.ReleasedByPerson);
        }

        // A person answered but the ticket was never given to them (unmapped id then, or an ineligible account): reconcile once the mapping is valid; until
        // then it stays flagged in the queue. NEVER Round Robin.
        if (e.FlaggedAnsweringGenesysUserId is not null)
        {
            return e.FlaggedAnsweringEmployeeId is not null
                ? Of(VoiceReconcileClass.AnsweringAgentResolvable, VoiceReconcileAction.AssignAnsweringAgent)
                : Of(VoiceReconcileClass.AnsweringAgentMappingPending);
        }

        if (e.AnsweringEmployeeId is not null)
        {
            return Of(VoiceReconcileClass.AnsweringAgentResolvable, VoiceReconcileAction.AssignAnsweringAgent);
        }

        // Confirmed human involvement only (a handler, a human line / first response, a connect / transfer / handoff-started event). An agent id that was merely
        // offered, assigned or ringing is not evidence (UnassignedTicketEvidence.HumanAnswered), so such a call that ends unanswered is still dealt by Round Robin.
        if (e.HumanAnswered)
        {
            return Of(VoiceReconcileClass.HumanInvolved);
        }

        if (e.VoiceEndReceivedAtUtc is not { } endReceived)
        {
            return Of(VoiceReconcileClass.CallInProgress);
        }

        // The look-back only stops a legacy backlog of AI-closed / AI-handled calls from being swept up. A ticket that is KNOWN to need a person (the interaction is
        // flagged RequiresHumanFollowUp: Genesys asked for a human, or an earlier run concluded "nobody answered") and still has no owner is never abandoned however
        // old: that is exactly a ticket stuck on the AI Agent, and it is retried every run until an eligible Call Center agent exists.
        if (endReceived < nowUtc - options.Lookback && !e.RequiresHumanFollowUp)
        {
            return Of(VoiceReconcileClass.OutsideLookback);
        }

        return nowUtc - endReceived < options.NoAnswerWindow
            ? Of(VoiceReconcileClass.WaitingForRoutingWindow)
            : Of(VoiceReconcileClass.NoAnswerConcluded, VoiceReconcileAction.ConcludeNoAnswer);
    }
}

public enum VoiceReconcileOutcome
{
    WouldAct,
    AssignedToAnsweringAgent,

    /// <summary>Concluded unanswered: the ticket was given to the LAST agent Genesys offered the call to (not Round Robin, not an answer).</summary>
    AssignedToLastOfferedAgent,

    /// <summary>
    /// Concluded unanswered and dealt to the next eligible Call Center CS Agent by the existing Round Robin (or, for an explicit workflow department, by that
    /// department's Round Robin). The AI Agent no longer owns the ticket; the agent can edit, follow up and close it without Take Ownership.
    /// </summary>
    AssignedRoundRobin,

    /// <summary>
    /// Concluded unanswered but nobody is eligible right now: the ticket is flagged and queued in the Call Center (the AI Agent stays the recorded owner, the queue
    /// is claimable) and is retried on every run until an eligible agent exists.
    /// </summary>
    LeftUnassignedForFollowUp,

    /// <summary>No active Call Center department / no eligible agent exists: nothing was written; retried next run.</summary>
    QueuedNoEligibleAgent,

    /// <summary>The answering agent still cannot take the ticket (unmapped / ineligible): left flagged in the queue.</summary>
    FlaggedMappingPending,
    Skipped,
    LostRace,
    NoLongerApplicable,
}

public sealed record VoiceReconcileResult(long TicketId, VoiceReconcileClass Class, VoiceReconcileOutcome Outcome, Guid? AssignedEmployeeId = null);

/// <summary>
/// <b>Voice call reconciliation.</b> The recurring half of the voice ownership rules (see <see cref="VoiceCallReconciliationOptions"/> for the window):
/// <list type="bullet">
/// <item>The call ENDED, no person answered, the AI did not close the ticket, and the reconciliation window has passed -> conclude "AI failed, nobody answered":
/// flag the ticket for human follow-up (once, audited) and deal it by the EXISTING workflow and Round Robin (Call Center first; an explicit workflow
/// department keeps its own Round Robin; Collections tower routing is left to Collections).</item>
/// <item>A person answered but the ticket was never given to them because their Genesys id was unmapped / ineligible: once the mapping is corrected (and the ticket is
/// still Open and unowned) the ticket goes to THAT person, dated at the real connect time. Until then it stays queued and flagged - never Round Robin.</item>
/// </list>
/// A ticket that is closed, owned by a person, awaiting approval, still ringing or inside the window is never touched. Safe to repeat and to race (the ticket's
/// row version and the "one current assignment" index decide; a lost race is reported, not retried blindly).
/// </summary>
public sealed class VoiceCallReconciler(
    ITicketUnassignedWorkRepository evidenceRepository,
    ITicketRepository ticketRepository,
    ITicketInteractionRepository interactionRepository,
    TicketAutoAssignmentService autoAssignment,
    GenesysAnsweringAgentAssignmentService answeringAgentAssignment,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>The next page of voice candidates with their classification (no writes).</summary>
    public async Task<IReadOnlyList<(UnassignedTicketEvidence Evidence, VoiceReconcileClassification Classification)>> ListAsync(
        long afterTicketId, VoiceCallReconciliationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var page = await evidenceRepository.ListVoiceCandidatesAsync(now - options.Lookback, afterTicketId, Math.Clamp(options.BatchSize, 1, 1000), cancellationToken);
        return [.. page.Select(e => (e, VoiceCallClassifier.Classify(e, now, options)))];
    }

    /// <summary>Re-checks one ticket and, unless <paramref name="dryRun"/>, acts. Call with a fresh scope per ticket (a lost race leaves tracked entities dirty).</summary>
    public async Task<VoiceReconcileResult> TryReconcileAsync(
        long ticketId, VoiceCallReconciliationOptions options, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var evidence = await evidenceRepository.GetEvidenceAsync(ticketId, cancellationToken);
        if (evidence is null)
        {
            return new VoiceReconcileResult(ticketId, VoiceReconcileClass.NotApplicable, VoiceReconcileOutcome.NoLongerApplicable);
        }

        var classification = VoiceCallClassifier.Classify(evidence, now, options);
        if (classification.Action == VoiceReconcileAction.None)
        {
            return new VoiceReconcileResult(
                ticketId, classification.Class,
                classification.Class == VoiceReconcileClass.AnsweringAgentMappingPending ? VoiceReconcileOutcome.FlaggedMappingPending : VoiceReconcileOutcome.Skipped);
        }

        if (dryRun)
        {
            return new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.WouldAct);
        }

        return classification.Action == VoiceReconcileAction.AssignAnsweringAgent
            ? await AssignAnsweringAgentAsync(ticketId, evidence, classification, cancellationToken)
            : await ConcludeNoAnswerAsync(ticketId, evidence, classification, options, now, cancellationToken);
    }

    private async Task<VoiceReconcileResult> AssignAnsweringAgentAsync(
        long ticketId, UnassignedTicketEvidence evidence, VoiceReconcileClassification classification, CancellationToken cancellationToken)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null || ticket.TicketStatus != TicketStatus.Open || ticket.CurrentOwnerEmployeeId is not null)
        {
            return new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.NoLongerApplicable);
        }

        var phone = (await interactionRepository.ListByTicketIdAsync(ticketId, cancellationToken))
            .Where(i => i.Source == InteractionContextSource.Genesys && i.ChannelId == WellKnownChannels.Phone)
            .OrderByDescending(i => i.CreatedAtUtc).FirstOrDefault();
        var genesysId = evidence.FlaggedAnsweringGenesysUserId ?? evidence.AnsweringGenesysUserId;
        if (phone is null || string.IsNullOrWhiteSpace(genesysId))
        {
            return new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.NoLongerApplicable);
        }

        // The same engine as the live connect event: eligibility is re-checked there, the first response is dated at the REAL connect time, the Original Human
        // Agent is recorded, and a still-unusable agent leaves the ticket flagged in the queue (no Round Robin for voice).
        var answered = await answeringAgentAssignment.ApplyConnectedAgentAsync(
            ticket.CreatedByEmployeeId ?? Guid.Empty, ticket, phone, genesysId, "VoiceReconciler", cancellationToken,
            connectedAtUtc: evidence.FlaggedAnsweringConnectedAtUtc ?? evidence.AnsweringConnectedAtUtc, allowOwnerTransfer: false);

        return answered.Outcome switch
        {
            AnswerAssignmentOutcome.AssignedToAnsweringAgent => new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.AssignedToAnsweringAgent, answered.AssignedEmployeeId),
            AnswerAssignmentOutcome.ConcurrencyConflict => new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.LostRace),
            AnswerAssignmentOutcome.QueuedForManualAssignment or AnswerAssignmentOutcome.AlreadyFlagged => new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.FlaggedMappingPending),
            _ => new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.NoLongerApplicable)
        };
    }

    private async Task<VoiceReconcileResult> ConcludeNoAnswerAsync(
        long ticketId, UnassignedTicketEvidence evidence, VoiceReconcileClassification classification, VoiceCallReconciliationOptions options, DateTime now,
        CancellationToken cancellationToken)
    {
        var callCenter = await autoAssignment.ResolveCallCenterAsync(cancellationToken);
        if (callCenter is null)
        {
            return new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.QueuedNoEligibleAgent);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
            if (ticket is null || ticket.TicketStatus != TicketStatus.Open || ticket.CurrentOwnerEmployeeId is not null)
            {
                return new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.NoLongerApplicable);
            }

            var correlationId = Guid.NewGuid();

            // The conclusion is final and audited ONCE: from here the ticket needs a person (sticky, like an explicit human-follow-up request).
            var phone = (await interactionRepository.ListByTicketIdAsync(ticketId, cancellationToken))
                .Where(i => i.Source == InteractionContextSource.Genesys && i.ChannelId == WellKnownChannels.Phone).ToList();
            var flagged = false;
            foreach (var interaction in phone)
            {
                flagged |= interaction.MarkRequiresHumanFollowUp();
            }

            if (flagged)
            {
                await auditWriter.WriteAsync(
                    actorEmployeeId: null, GenesysAuditActions.VoiceNoAnswerConcluded, "Ticket", ticketId.ToString(),
                    beforeValue: null,
                    afterValue: $"TicketId={ticketId};Conversations={string.Join(',', phone.Select(i => i.GenesysConversationId ?? "(none)"))};"
                        + $"EndReceivedAtUtc={evidence.VoiceEndReceivedAtUtc:O};WindowSeconds={(int)options.NoAnswerWindow.TotalSeconds};ConcludedAtUtc={now:O};"
                        + "Reason=The call ended, no person answered, the AI did not close the ticket and the reconciliation window passed; the ticket needs a person: dealt to an eligible Call Center agent by Round Robin (never to an agent who was merely offered the call), or queued and retried until one is eligible",
                    correlationId, cancellationToken);
            }

            // An offer / ringing is not an answer: the agent a call was merely OFFERED to is never given the ticket because of the offer. The offer is recorded for
            // diagnostics only. Who gets the ticket is decided below by the existing Round Robin.
            if (flagged && evidence.VoiceLastOfferedGenesysUserId is { } offeredGenesysId)
            {
                await auditWriter.WriteAsync(
                    actorEmployeeId: null, GenesysAuditActions.VoiceLastOfferedAgentUnavailable, "Ticket", ticketId.ToString(),
                    beforeValue: null,
                    afterValue: $"TicketId={ticketId};GenesysUserId={offeredGenesysId};Issue=OfferedNotConnected;Action=not assigned from the offer (an offer is not a confirmed connection); dealt by Round Robin instead",
                    correlationId, cancellationToken);
            }

            const string reason = "Voice call ended unanswered - human follow-up required: queued in the Call Center and dealt to an eligible agent by Round Robin";

            // Queue routing first (Call Center first; an explicit workflow department / Collections keeps its own department). The AI Agent stays the recorded owner
            // until a person actually receives the ticket below.
            var routed = ticket.CurrentDepartmentId != callCenter.DepartmentId
                && await autoAssignment.RouteExistingTicketToCallCenterAsync(
                    ticket, now, correlationId, reason, cancellationToken, requiredRole: null, dealToAgent: false, voiceOwnership: true);

            // Round Robin through the EXISTING engine (persisted, row-locked cursor; CS Agent role, active, not locked out, not a service identity, Call Center member;
            // one current assignment row; AutoAssign audit; assignment history; "assigned to you" notification). Ticket.AssignTo clears the AI Agent as owner, so the
            // employee owns it outright and needs no Take Ownership. The read-only eligibility pre-check means a run that finds nobody writes nothing, so a retry every
            // minute leaves no audit noise.
            Guid? assignedEmployeeId = null;
            if (ticket.CurrentOwnerEmployeeId is null
                && await autoAssignment.HasEligibleDepartmentEmployeeAsync(ticket.CurrentDepartmentId, cancellationToken))
            {
                var dealt = ticket.CurrentDepartmentId == callCenter.DepartmentId
                    ? await autoAssignment.AssignCallCenterIntakeAsync(ticket, now, correlationId, cancellationToken)
                    : await autoAssignment.AssignDepartmentRoundRobinAsync(ticket, AutoAssignmentTrigger.Reconciler, now, correlationId, carriedOverOwnerEmployeeId: null, cancellationToken);
                if (dealt.Outcome == AutoAssignmentOutcome.AssignedRoundRobin)
                {
                    assignedEmployeeId = dealt.AssignedEmployeeId;
                }
            }

            if (!flagged && !routed && assignedEmployeeId is null)
            {
                return new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.QueuedNoEligibleAgent);   // already flagged and queued, still nobody eligible: retried next run
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return assignedEmployeeId is { } owner
                ? new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.AssignedRoundRobin, owner)
                : new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.LeftUnassignedForFollowUp);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            return new VoiceReconcileResult(ticketId, classification.Class, VoiceReconcileOutcome.LostRace);
        }
    }
}
