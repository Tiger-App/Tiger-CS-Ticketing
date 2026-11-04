using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Settings of the one-time CS Agent backfill (config section <c>CsAgentBackfill</c>). There is NO scheduler: it runs only when a System
/// Administrator calls the admin endpoint, and a LIVE run additionally needs <see cref="Enabled"/> = true (default false). A dry run always works.
/// </summary>
public sealed class CsAgentBackfillOptions
{
    public const string SectionName = "CsAgentBackfill";

    /// <summary>Gate for LIVE (writing) runs. Dry runs and previews are always allowed.</summary>
    public bool Enabled { get; set; }

    /// <summary>Tickets examined per call when the caller does not say.</summary>
    public int DefaultBatchSize { get; set; } = 25;

    /// <summary>Hard ceiling on the tickets examined by one call, whatever the caller asks.</summary>
    public int MaxBatchSize { get; set; } = 200;

    /// <summary>Pause after each live assignment, so a large backlog never floods the database or the agents' queues.</summary>
    public int DelayBetweenTicketsMs { get; set; } = 250;

    /// <summary>A just-created ticket is given time to finish its own routing before the backfill looks at it.</summary>
    public int MinTicketAgeMinutes { get; set; } = 3;

    /// <summary>
    /// Tickets whose AI conversation ended without resolving or closing the ticket. Final business rule: they are no longer AI-owned, so they
    /// DO get a CS Agent (default true). Set false only to hold them back for review.
    /// </summary>
    public bool IncludeAiEndedWithoutHuman { get; set; } = true;

    /// <summary>
    /// Stale AI rule. A "live" AI conversation (no end event) with NO confirmed human handling and NO genuine activity (customer or AI transcript
    /// message, Genesys event or interaction stamp) for MORE than this many hours has lost its end event: nobody is serving it, so its ticket becomes
    /// eligible for a CS Agent. <c>null</c> (the default, and pinned null in Production) or a value &lt;= 0 disables the rule: every live AI
    /// conversation stays protected however old it is. 24 is the proposed value, for review only - it is never applied by default.
    /// </summary>
    public int? StaleAiAfterHours { get; set; }

    /// <summary>The configured stale-AI silence as a span, or null when the rule is disabled (null / non-positive).</summary>
    public TimeSpan? StaleAiThreshold => StaleAiAfterHours is { } h and > 0 ? TimeSpan.FromHours(h) : null;
}

/// <summary>Why a ticket is, or is not, part of the backfill.</summary>
public enum CsAgentBackfillClass
{
    /// <summary>Active, no CS Agent, needs a person: gets the next agent of the shared rotation.</summary>
    Eligible,

    /// <summary>Closed or Resolved: never touched.</summary>
    NotActive,

    /// <summary>Already has an owner: never touched, never redistributed.</summary>
    HasOwner,

    /// <summary>An approval is pending; the approval process decides first.</summary>
    PendingApproval,

    /// <summary>A live AI conversation nobody asked a person for and that still has recent activity (or the stale rule is off): stays AI-owned.</summary>
    ActiveAiConversation,

    /// <summary>The AI conversation ended with no human need but the ticket was never closed. Assigned by default (<see cref="CsAgentBackfillOptions.IncludeAiEndedWithoutHuman"/>); an exclusion only when that is switched off.</summary>
    AiEndedNoHumanNeed,

    /// <summary>Created moments ago; its own creation flow may still be assigning it.</summary>
    TooNew,
}

public static class CsAgentBackfillClassifier
{
    public static CsAgentBackfillClass Classify(UnassignedTicketEvidence e, DateTime nowUtc, CsAgentBackfillOptions options)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(options);

        if (e.Status is TicketStatus.Resolved or TicketStatus.Closed) return CsAgentBackfillClass.NotActive;
        if (e.OwnerEmployeeId is not null) return CsAgentBackfillClass.HasOwner;
        if (e.PendingApproval) return CsAgentBackfillClass.PendingApproval;
        if (e.CreatedAtUtc > nowUtc - TimeSpan.FromMinutes(Math.Max(0, options.MinTicketAgeMinutes))) return CsAgentBackfillClass.TooNew;

        // A confirmed handling agent / a phone pick-up is human work (never "AI-owned"): the service gives it to that agent when eligible,
        // otherwise to the shared rotation.
        // Open Handoff / a follow-up flag mean a person IS required: always eligible, however recent the conversation.
        if (HumanNeeded(e)) return CsAgentBackfillClass.Eligible;

        if (e.HasGenesysInteraction)
        {
            // A live AI conversation stays protected until it has been silent for MORE than the (opt-in) stale threshold.
            if (e.HasLiveGenesysInteraction) return IsStaleAi(e, nowUtc, options) ? CsAgentBackfillClass.Eligible : CsAgentBackfillClass.ActiveAiConversation;
            if (!options.IncludeAiEndedWithoutHuman) return CsAgentBackfillClass.AiEndedNoHumanNeed;
        }

        return CsAgentBackfillClass.Eligible;
    }

    private static bool HumanNeeded(UnassignedTicketEvidence e) =>
        e.RequiresHumanFollowUp || e.OpenHandoff || e.HumanEvidence || e.AnsweringEmployeeId is not null || e.PhonePickupByUnmappedAgent;

    /// <summary>
    /// True when this ticket is eligible ONLY because of the stale-AI rule: a live AI conversation, no human need or handling, and no activity for more
    /// than <see cref="CsAgentBackfillOptions.StaleAiAfterHours"/>. Used for reporting; <see cref="Classify"/> is the single source of eligibility.
    /// </summary>
    public static bool IsStaleAi(UnassignedTicketEvidence e, DateTime nowUtc, CsAgentBackfillOptions options)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(options);
        return options.StaleAiThreshold is { } threshold
            && e.HasGenesysInteraction && e.HasLiveGenesysInteraction && !HumanNeeded(e)
            && nowUtc - e.LastActivityUtc > threshold;
    }
}

public sealed record CsAgentBackfillRow(
    long TicketId, string TicketNumber, TicketStatus Status, int DepartmentId, string? DepartmentName, DateTime CreatedAtUtc,
    CsAgentBackfillClass Class, Guid? ProposedEmployeeId, string? ProposedSource = null, bool StaleAi = false, DateTime? LastActivityUtc = null);

/// <summary>The two ways the backfill picks an agent: <see cref="ConfirmedHandler"/> (priority 2) or <see cref="RoundRobin"/> (priority 4).</summary>
public static class CsAgentBackfillSource
{
    public const string ConfirmedHandler = "ConfirmedHandler";
    public const string RoundRobin = "RoundRobin";
}

/// <summary>A read-only look at what the backfill would do. Nothing is locked, written or advanced.</summary>
public sealed record CsAgentBackfillPreview(
    bool CallCenterConfigured, int PoolSize, Guid? RotationCursor, int Examined, int Eligible,
    IReadOnlyDictionary<string, int> ExclusionCounts, IReadOnlyDictionary<string, int> EligibleByDepartment,
    IReadOnlyList<CsAgentBackfillRow> Rows, long NextAfterTicketId, bool Complete);

public enum CsAgentBackfillOutcome
{
    /// <summary>Dry run: this is what would be done.</summary>
    WouldAssign,
    Assigned,
    Skipped,
    /// <summary>The shared rotation has nobody eligible. The failure is recorded on the ticket (once); the run stops.</summary>
    NoEligibleAgent,
    /// <summary>A concurrent writer got there first; the winner stands.</summary>
    LostRace,
    /// <summary>Changed (owner / status) between listing and acting.</summary>
    NoLongerApplicable,
}

public sealed record CsAgentBackfillTicketResult(
    long TicketId, CsAgentBackfillOutcome Outcome, CsAgentBackfillClass Class, Guid? AssignedEmployeeId = null, string? Source = null, bool StaleAi = false);

/// <summary>
/// The one-time backfill of active tickets that have no CS Agent. It runs the SAME engine as new tickets
/// (<see cref="TicketAutoAssignmentService.EnsureCsAgentAsync"/>): the shared Call Center CS Agent pool, the same eligibility rules and the same
/// persisted, row-locked cursor - so it continues the rotation instead of resetting it. It never moves a ticket: department, request type,
/// priority, status, SLA and every history row are untouched; only the owner, one assignment row and audit entries are added.
/// </summary>
public sealed class CsAgentBackfillService(
    ITicketUnassignedWorkRepository evidenceRepository,
    ITicketRepository ticketRepository,
    TicketAutoAssignmentService autoAssignment,
    IDepartmentRepository departmentRepository,
    IAuditEntryWriter auditWriter,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Read-only preview of the next <paramref name="limit"/> active unowned tickets after <paramref name="afterTicketId"/>, oldest first.</summary>
    public async Task<CsAgentBackfillPreview> PreviewAsync(
        long afterTicketId, int limit, CsAgentBackfillOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        limit = Math.Clamp(limit, 1, Math.Max(1, options.MaxBatchSize));
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var (callCenter, pool, cursor) = await autoAssignment.GetSharedPoolAsync(cancellationToken);
        var names = (await departmentRepository.ListAsync(activeOnly: false, cancellationToken)).ToDictionary(d => d.DepartmentId, d => d.Name);
        var page = await evidenceRepository.ListActiveUnownedAsync(afterTicketId, limit, cancellationToken);

        // The proposal follows the real cursor, advancing in memory only - exactly the order a live run would deal them.
        var simulated = cursor;
        var rows = new List<CsAgentBackfillRow>(page.Count);
        var exclusions = new Dictionary<string, int>();
        var byDepartment = new Dictionary<string, int>();
        foreach (var e in page)
        {
            var cls = CsAgentBackfillClassifier.Classify(e, now, options);
            Guid? proposed = null;
            var departmentName = names.GetValueOrDefault(e.CurrentDepartmentId);
            string? source = null;
            if (cls == CsAgentBackfillClass.Eligible)
            {
                if (await ConfirmedHandlerAsync(e, callCenter, cancellationToken) is { } handler)
                {
                    proposed = handler;                                  // priority 2: the handling agent; the rotation does not move
                    source = CsAgentBackfillSource.ConfirmedHandler;
                }
                else
                {
                    proposed = DepartmentAssignmentRotation.PickNext(simulated, pool);
                    if (proposed is not null) simulated = proposed;
                    source = CsAgentBackfillSource.RoundRobin;
                }

                var key = departmentName ?? e.CurrentDepartmentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                byDepartment[key] = byDepartment.GetValueOrDefault(key) + 1;
            }
            else
            {
                exclusions[cls.ToString()] = exclusions.GetValueOrDefault(cls.ToString()) + 1;
            }

            rows.Add(new CsAgentBackfillRow(e.TicketId, e.TicketNumber, e.Status, e.CurrentDepartmentId, departmentName, e.CreatedAtUtc, cls, proposed, source,
                cls == CsAgentBackfillClass.Eligible && CsAgentBackfillClassifier.IsStaleAi(e, now, options), e.LastActivityUtc));
        }

        return new CsAgentBackfillPreview(
            callCenter is not null, pool.Count, cursor, page.Count, rows.Count(r => r.Class == CsAgentBackfillClass.Eligible),
            exclusions, byDepartment, rows, page.Count == 0 ? afterTicketId : page[^1].TicketId, Complete: page.Count < limit);
    }

    /// <summary>The confirmed handling CS Agent when there is one AND they are an eligible Call Center CS Agent; otherwise null (shared rotation).</summary>
    private async Task<Guid?> ConfirmedHandlerAsync(UnassignedTicketEvidence e, Domain.Modules.IdentityAndAccess.Department? callCenter, CancellationToken cancellationToken) =>
        e.AnsweringEmployeeId is { } handler && callCenter is not null
        && await autoAssignment.CheckAnsweringAgentAsync(handler, callCenter.DepartmentId, cancellationToken) == AnsweringAgentIssue.None
            ? handler
            : null;

    /// <summary>
    /// Backfills ONE ticket in its own transaction (call with a fresh scope per ticket). Re-checks everything under the transaction, so it is
    /// idempotent (an owned ticket is skipped, nothing duplicated) and race-safe (the cursor row is locked by the engine; the ticket row version
    /// and the unique current-assignment index make a concurrent writer win or lose cleanly).
    /// </summary>
    public async Task<CsAgentBackfillTicketResult> TryBackfillAsync(
        long ticketId, string batchId, CsAgentBackfillOptions options, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var evidence = await evidenceRepository.GetEvidenceAsync(ticketId, cancellationToken);
        if (evidence is null)
        {
            return new CsAgentBackfillTicketResult(ticketId, CsAgentBackfillOutcome.NoLongerApplicable, CsAgentBackfillClass.NotActive);
        }

        var cls = CsAgentBackfillClassifier.Classify(evidence, now, options);
        if (cls != CsAgentBackfillClass.Eligible)
        {
            return new CsAgentBackfillTicketResult(ticketId, CsAgentBackfillOutcome.Skipped, cls);
        }

        var handlerId = await ConfirmedHandlerAsync(evidence, await autoAssignment.ResolveCallCenterAsync(cancellationToken), cancellationToken);
        if (dryRun)
        {
            return new CsAgentBackfillTicketResult(
                ticketId, CsAgentBackfillOutcome.WouldAssign, cls, handlerId, handlerId is null ? CsAgentBackfillSource.RoundRobin : CsAgentBackfillSource.ConfirmedHandler,
                CsAgentBackfillClassifier.IsStaleAi(evidence, now, options));
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
            if (ticket is null || ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed || ticket.CurrentOwnerEmployeeId is not null)
            {
                return new CsAgentBackfillTicketResult(ticketId, CsAgentBackfillOutcome.NoLongerApplicable, cls);
            }

            // Re-check eligibility INSIDE the transaction with the same classifier: a customer message, Genesys event, human pick-up or approval that
            // arrived after the listing (or after the preview) protects the ticket again. Nothing is written for a ticket that is no longer eligible.
            var fresh = await evidenceRepository.GetEvidenceAsync(ticketId, cancellationToken);
            var freshNow = timeProvider.GetUtcNow().UtcDateTime;
            var freshClass = fresh is null ? CsAgentBackfillClass.NotActive : CsAgentBackfillClassifier.Classify(fresh, freshNow, options);
            if (fresh is null || freshClass != CsAgentBackfillClass.Eligible)
            {
                return new CsAgentBackfillTicketResult(ticketId, CsAgentBackfillOutcome.Skipped, freshClass);
            }

            evidence = fresh;
            now = freshNow;
            handlerId = await ConfirmedHandlerAsync(evidence, await autoAssignment.ResolveCallCenterAsync(cancellationToken), cancellationToken);
            var staleAi = CsAgentBackfillClassifier.IsStaleAi(evidence, now, options);

            var correlationId = Guid.NewGuid();
            var source = CsAgentBackfillSource.ConfirmedHandler;
            var result = handlerId is { } handler
                ? await autoAssignment.AssignConfirmedHandlerAsync(ticket, handler, AutoAssignmentTrigger.Backfill, now, correlationId, cancellationToken)
                : null;
            if (result is null)
            {
                source = CsAgentBackfillSource.RoundRobin;
                result = await autoAssignment.EnsureCsAgentAsync(ticket, null, AutoAssignmentTrigger.Backfill, now, correlationId, cancellationToken);
            }

            if (result.AssignedEmployeeId is not { } employeeId)
            {
                // The engine has recorded the failure (audit + Manual Assignment Required, once); persist it and stop the caller.
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new CsAgentBackfillTicketResult(ticketId, CsAgentBackfillOutcome.NoEligibleAgent, cls);
            }

            await auditWriter.WriteAsync(
                actorEmployeeId: null, "BackfillAssign", "Ticket", ticketId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                beforeValue: "Unassigned",
                afterValue: $"AssignedEmployeeId={employeeId};DepartmentId={ticket.CurrentDepartmentId};Status={ticket.TicketStatus};Source={source};BatchId={batchId}{(staleAi ? ";Reason=StaleAi;LastActivityUtc=" + evidence.LastActivityUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}",
                correlationId, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CsAgentBackfillTicketResult(ticketId, CsAgentBackfillOutcome.Assigned, cls, employeeId, source, staleAi);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            return new CsAgentBackfillTicketResult(ticketId, CsAgentBackfillOutcome.LostRace, cls);
        }
    }
}
