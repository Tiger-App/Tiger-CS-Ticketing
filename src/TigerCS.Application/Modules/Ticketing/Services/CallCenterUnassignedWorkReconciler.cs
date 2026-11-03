using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>Settings of the unassigned-human-work reconciler (config section <c>CallCenterReconciliation</c>). Everything is OFF / dry-run by default.</summary>
public sealed class CallCenterReconciliationOptions
{
    public const string SectionName = "CallCenterReconciliation";

    /// <summary>The recurring job runs only when true (and <c>BackgroundJobs:Enabled</c>).</summary>
    public bool Enabled { get; set; }

    /// <summary>True (default): classify and log what WOULD happen; write nothing.</summary>
    public bool DryRun { get; set; } = true;

    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Most tickets examined per run.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Most tickets WRITTEN per run (the one-time catch-up is thereby throttled).</summary>
    public int MaxAssignmentsPerRun { get; set; } = 50;

    /// <summary>A live AI conversation silent this many hours with no human evidence counts as stale (null = never stale = always protected).</summary>
    public int? StaleAiAfterHours { get; set; }

    /// <summary>Treat stale AI tickets as human work. Default false: nobody asked a person for them.</summary>
    public bool AssignStaleAi { get; set; }

    /// <summary>Round Robin deals only to holders of the CS Agent role (the role that can Transfer as owner). Default true; the answering-agent path is unaffected.</summary>
    public bool CsAgentRoleOnly { get; set; } = true;

    /// <summary>A just-created ticket is given time to be routed by its own request before the reconciler looks at it.</summary>
    public int MinTicketAgeMinutes { get; set; } = 3;
}

public enum ReconcileOutcome
{
    /// <summary>Dry run: this is what would be done.</summary>
    WouldAct,
    AssignedToAnsweringAgent,
    AssignedRoundRobin,
    /// <summary>Left alone by classification (protected / not human work / already owned).</summary>
    Skipped,
    /// <summary>Human work, but no eligible Call Center agent (or no Call Center) - stays queued, reported, retried next run.</summary>
    NoEligibleAgent,
    /// <summary>A concurrent writer got there first; the winner stands.</summary>
    LostRace,
    /// <summary>The ticket changed (owner/status) between listing and acting.</summary>
    NoLongerApplicable,
}

public sealed record ReconcileResult(long TicketId, UnassignedTicketClass Class, ReconcileOutcome Outcome, Guid? AssignedEmployeeId = null);

/// <summary>
/// Makes sure a ticket that genuinely needs a person never silently stays without an owner. It does NOT invent rules: it classifies an Open,
/// unowned ticket (<see cref="UnassignedTicketClassifier"/>) and then uses the SAME engine as live traffic - the answering agent
/// (<see cref="TicketAutoAssignmentService.ApplyAnsweringAgentAsync"/>) or Call Center Round Robin
/// (<see cref="TicketAutoAssignmentService.RouteExistingTicketToCallCenterAsync"/> / <see cref="TicketAutoAssignmentService.AssignCallCenterIntakeAsync"/>).
///
/// <list type="bullet">
/// <item><b>Never redistributes.</b> Only tickets with no owner are listed, and the owner is re-checked inside the transaction.</item>
/// <item><b>Never touches</b> active AI conversations, completed AI tickets, pending approvals, tickets the Call Center transferred onward, or ones a person released.</item>
/// <item><b>Safe to repeat and to race.</b> The rotation cursor is row-locked; a redelivered run finds an owner and does nothing; a lost race is reported, not retried blindly.</item>
/// <item><b>Answered by a person</b> -> that agent; otherwise Round Robin (an unmapped/unusable answering agent also falls back to Round Robin).</item>
/// </list>
/// </summary>
public sealed class CallCenterUnassignedWorkReconciler(
    ITicketUnassignedWorkRepository evidenceRepository,
    ITicketRepository ticketRepository,
    TicketAutoAssignmentService autoAssignment,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>The next page of Open, unowned tickets with their classification (no writes).</summary>
    public async Task<IReadOnlyList<(UnassignedTicketEvidence Evidence, UnassignedTicketClassification Classification)>> ListAsync(
        long afterTicketId, CallCenterReconciliationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var callCenter = await autoAssignment.ResolveCallCenterAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now - TimeSpan.FromMinutes(Math.Max(0, options.MinTicketAgeMinutes));
        var stale = options.StaleAiAfterHours is { } h ? TimeSpan.FromHours(h) : (TimeSpan?)null;

        var page = await evidenceRepository.ListOpenUnownedAsync(afterTicketId, Math.Clamp(options.BatchSize, 1, 1000), cancellationToken);
        return [.. page.Select(e => (e, e.CreatedAtUtc > cutoff
            ? new UnassignedTicketClassification(UnassignedTicketClass.ActiveAiConversation, UnassignedTicketAction.None) // too new: its own creation flow is still running
            : UnassignedTicketClassifier.Classify(e, callCenter?.DepartmentId, now, stale, options.AssignStaleAi)))];
    }

    /// <summary>
    /// Re-checks one ticket under a transaction and, unless <paramref name="dryRun"/>, gives it an owner. Call with a fresh scope per ticket
    /// (a lost race leaves tracked entities dirty).
    /// </summary>
    public async Task<ReconcileResult> TryReconcileAsync(
        long ticketId, CallCenterReconciliationOptions options, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var callCenter = await autoAssignment.ResolveCallCenterAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var stale = options.StaleAiAfterHours is { } h ? TimeSpan.FromHours(h) : (TimeSpan?)null;
        var requiredRole = options.CsAgentRoleOnly ? Roles.CsAgent : null;

        var evidence = await evidenceRepository.GetEvidenceAsync(ticketId, cancellationToken);
        if (evidence is null)
        {
            return new ReconcileResult(ticketId, UnassignedTicketClass.NotOpen, ReconcileOutcome.NoLongerApplicable);
        }

        var classification = UnassignedTicketClassifier.Classify(evidence, callCenter?.DepartmentId, now, stale, options.AssignStaleAi);
        if (classification.Action == UnassignedTicketAction.None)
        {
            return new ReconcileResult(ticketId, classification.Class, ReconcileOutcome.Skipped);
        }

        if (callCenter is null)
        {
            return new ReconcileResult(ticketId, classification.Class, ReconcileOutcome.NoEligibleAgent);
        }

        if (dryRun)
        {
            return new ReconcileResult(ticketId, classification.Class, ReconcileOutcome.WouldAct);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
            if (ticket is null || ticket.TicketStatus != TicketStatus.Open || ticket.CurrentOwnerEmployeeId is not null)
            {
                return new ReconcileResult(ticketId, classification.Class, ReconcileOutcome.NoLongerApplicable);
            }

            var correlationId = Guid.NewGuid();
            Guid? assigned = null;
            var outcome = ReconcileOutcome.NoEligibleAgent;

            if (classification.Action == UnassignedTicketAction.AssignToAnsweringAgent && evidence.AnsweringEmployeeId is { } answeringId)
            {
                var answer = await autoAssignment.ApplyAnsweringAgentAsync(
                    ticket, new AnsweringAgent(evidence.AnsweringGenesysUserId ?? string.Empty, answeringId), "Reconciler", now, correlationId,
                    reportedByEmployeeId: null, routeToCallCenterIfAgentUnavailable: true, cancellationToken);
                if (answer.Outcome == AnswerAssignmentOutcome.AssignedToAnsweringAgent)
                {
                    assigned = answer.AssignedEmployeeId;
                    outcome = ReconcileOutcome.AssignedToAnsweringAgent;
                }
            }

            // Not answered by a usable agent: Round Robin (the answering agent could not take it, or nobody answered).
            if (assigned is null && ticket.CurrentOwnerEmployeeId is null)
            {
                if (ticket.CurrentDepartmentId != callCenter.DepartmentId)
                {
                    await autoAssignment.RouteExistingTicketToCallCenterAsync(
                        ticket, now, correlationId, $"Reconciler: {classification.Class} - routed to Call Center", cancellationToken, requiredRole);
                }
                else
                {
                    await autoAssignment.AssignCallCenterIntakeAsync(ticket, now, correlationId, cancellationToken, requiredRole);
                }

                if (ticket.CurrentOwnerEmployeeId is { } owner)
                {
                    assigned = owner;
                    outcome = ReconcileOutcome.AssignedRoundRobin;
                }
            }
            else if (assigned is null && ticket.CurrentOwnerEmployeeId is { } existing)
            {
                assigned = existing;
                outcome = ReconcileOutcome.AssignedRoundRobin;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReconcileResult(ticketId, classification.Class, outcome, assigned);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            return new ReconcileResult(ticketId, classification.Class, ReconcileOutcome.LostRace);
        }
    }
}
