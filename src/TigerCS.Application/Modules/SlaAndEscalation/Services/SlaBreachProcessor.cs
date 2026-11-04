using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>What one call to <see cref="SlaBreachProcessor.ProcessDeadlineAsync"/> did.</summary>
public enum SlaBreachProcessingOutcome
{
    /// <summary>The deadline was breached, and this call is what recorded it.</summary>
    BreachRecorded,

    /// <summary>Not breached: the deadline has not fallen due, or it was satisfied in time.</summary>
    NotBreached,

    /// <summary>
    /// This exact due event was already processed — by the scheduled job, by
    /// the safety sweep, or by an earlier retry. Nothing was written
    /// (SLA-Architecture.md §15).
    /// </summary>
    AlreadyProcessed,

    /// <summary>The ticket has no current SLA period — a provisional ticket still awaiting CRM reconciliation (FR-TKT-09).</summary>
    NoSlaPeriod,

    /// <summary>Closed-ticket immutability: a Closed ticket is settled and accepts no further SLA or escalation change.</summary>
    TicketClosed,

    /// <summary>
    /// A Resolution deadline check fired while the Resolution clock is paused
    /// (ISSUE-018). Nothing is breached and nothing is written: the deadline
    /// is held while paused and re-scheduled, extended, on resume.
    /// </summary>
    Paused
}

/// <summary>
/// The single place a breach is decided and recorded (backlog S-10/S-11,
/// FR-SLA-01–04, FR-ESC-02).
///
/// <para>
/// <b>Three callers, one code path.</b> SLA-Architecture.md §13's scheduled
/// job, §14's safety sweep, and the two achievement events (recording the
/// First Human Response, and resolving the ticket) all arrive here. That is
/// what makes the idempotency guarantee real rather than per-caller: every
/// path composes the same <c>TicketId + CheckType + DueTimestamp</c> key
/// (§15/ADR-0014), so whichever arrives second writes nothing at all — no
/// second breach flag, no second escalation row, no second audit entry.
/// </para>
///
/// <para>
/// <b>Boundary semantics.</b> A deadline not yet satisfied is breached once
/// <c>now &gt;= due</c> — the scheduled job fires <i>at</i> the due moment
/// (§13), so an exclusive comparison would mean it could never fire. A
/// deadline that <i>was</i> satisfied is breached only if the satisfying
/// event landed strictly after the due moment, so satisfying it exactly on
/// the deadline is on time. The two rules agree at the boundary instant.
/// </para>
///
/// <para>
/// <b>Participates in the caller's transaction</b> — nothing here calls
/// SaveChanges. The breach flag, the escalation row, the history rows and
/// the audit entries must all commit together or not at all.
/// </para>
/// </summary>
public sealed class SlaBreachProcessor(
    ITicketSlaInstanceRepository slaInstanceRepository,
    ITicketEscalationRepository escalationRepository,
    ITicketResolutionRepository ticketResolutionRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IIdempotencyRecordStore idempotencyStore,
    IAuditEntryWriter auditWriter,
    ITicketSlaPausePeriodRepository? pauseRepository = null)
{
    /// <summary>
    /// Evaluates one deadline for one ticket and records a breach if there is
    /// one.
    /// </summary>
    /// <param name="ticket">The ticket being evaluated. Mutated only when a breach is newly recorded.</param>
    /// <param name="deadlineType">Which of the two independent clocks (ADR-0009) to evaluate.</param>
    /// <param name="nowUtc">The evaluation moment.</param>
    /// <param name="correlationId">Ties every row this call writes to one event (NFR-REL-03).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="resolutionAchievedAtUtc">
    /// The moment the Resolution was achieved, when the CALLER has just staged the resolution row in the same unit of work. That row is
    /// not yet visible to a database query, so reading it back (the default) finds nothing and an on-time resolution was never marked
    /// <c>Met</c>. Ignored for the First Response clock, whose achievement moment is on the ticket itself.
    /// </param>
    /// <param name="settlingClosure">
    /// True only for the system closure that Resolves and Closes in one step: the ticket is already Closed in memory, but this is the single
    /// moment its Resolution SLA outcome can still be settled (nothing may touch a Closed ticket afterwards).
    /// </param>
    public async Task<SlaBreachProcessingOutcome> ProcessDeadlineAsync(
        Ticket ticket,
        SlaDeadlineType deadlineType,
        DateTime nowUtc,
        Guid correlationId,
        CancellationToken cancellationToken = default,
        DateTime? resolutionAchievedAtUtc = null,
        bool settlingClosure = false)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        // Closed-ticket immutability, checked before anything is read or
        // written: a Closed ticket's SLA outcome was settled when it was
        // resolved, and nothing — including a system job — may change it now.
        if (ticket.TicketStatus == TicketStatus.Closed && !settlingClosure)
        {
            return SlaBreachProcessingOutcome.TicketClosed;
        }

        var instance = await slaInstanceRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        if (instance is null)
        {
            return SlaBreachProcessingOutcome.NoSlaPeriod;
        }

        if (instance.IsBreached(deadlineType))
        {
            return SlaBreachProcessingOutcome.AlreadyProcessed;
        }

        // ISSUE-018: a Resolution check that fires while the clock is paused
        // must not breach — the held deadline is not running out. The pause
        // is closed (and the deadline extended and re-scheduled) when the
        // ticket returns to work or is resolved, and the check runs again
        // then. First Response never pauses, so it is never short-circuited.
        if (deadlineType == SlaDeadlineType.Resolution
            && pauseRepository is not null
            && await pauseRepository.GetOpenAsync(ticket.TicketId, cancellationToken) is not null)
        {
            return SlaBreachProcessingOutcome.Paused;
        }

        var dueAtUtc = instance.DueAtUtcFor(deadlineType);
        var achievedAtUtc = deadlineType == SlaDeadlineType.Resolution && resolutionAchievedAtUtc is { } staged
            ? staged
            : await GetAchievementMomentAsync(ticket, deadlineType, cancellationToken);

        if (!IsBreached(dueAtUtc, achievedAtUtc, nowUtc))
        {
            if (deadlineType == SlaDeadlineType.Resolution && achievedAtUtc is not null && !instance.FirstResponseBreached)
            {
                // Resolved within its deadline with nothing breached — the
                // ticket-level summary can report Met (SLA-Architecture.md
                // §11's states). MarkSlaMet never overrides a recorded
                // breach, so a ticket that missed First Response and then
                // resolved on time still reports Breached.
                ticket.MarkSlaMet();
            }

            return SlaBreachProcessingOutcome.NotBreached;
        }

        if (settlingClosure)
        {
            // A late resolution would need a breach flag, an escalation and a status-history row on a ticket that is already Closed, which
            // the closed-ticket immutability rule forbids (the domain throws). Settling a closure may therefore only ever record Met;
            // a late system closure is left exactly as before (nothing written).
            return SlaBreachProcessingOutcome.TicketClosed;
        }

        // SLA-Architecture.md §15 — the claim that makes the scheduled job
        // and the safety sweep safe to overlap. Everything below this line
        // happens exactly once per due event.
        var reserved = await idempotencyStore.TryReserveAsync(
            SlaBreachIdempotencyKey.For(ticket.TicketId, deadlineType, dueAtUtc),
            Domain.Infrastructure.IdempotencyScopes.SlaBreachCheck,
            nowUtc,
            cancellationToken);

        if (!reserved)
        {
            return SlaBreachProcessingOutcome.AlreadyProcessed;
        }

        instance.MarkBreached(deadlineType);

        var previousSlaState = ticket.SlaState;
        ticket.MarkSlaBreached();

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticket.TicketId, TicketStatusDimension.SlaState, (byte)previousSlaState, (byte)ticket.SlaState,
                actorEmployeeId: null, actorIsSystem: true,
                note: $"{deadlineType} SLA breached (due {dueAtUtc:O}).", correlationId, nowUtc),
            cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            action: "DetectSlaBreach",
            entityType: nameof(TicketSlaInstance),
            entityId: ticket.TicketId.ToString(),
            beforeValue: $"{{\"{BreachFlagName(deadlineType)}\":false}}",
            afterValue:
                $"{{\"{BreachFlagName(deadlineType)}\":true,\"dueAtUtc\":\"{dueAtUtc:O}\",\"detectedAtUtc\":\"{nowUtc:O}\","
                + $"\"achievedAtUtc\":{(achievedAtUtc is { } a ? $"\"{a:O}\"" : "null")}}}",
            correlationId,
            cancellationToken);

        await RaiseAutomaticLevel2Async(ticket, dueAtUtc, deadlineType, nowUtc, correlationId, cancellationToken);

        return SlaBreachProcessingOutcome.BreachRecorded;
    }

    /// <summary>
    /// FR-ESC-02 / backlog S-11 — "a breach automatically raises a Level 2
    /// <c>TicketEscalations</c> row".
    ///
    /// <para>
    /// <b>Exactly one per ticket</b>, which is backlog S-11's own stated test
    /// requirement ("exactly one Level 2 escalation, not a repeating one on
    /// every subsequent check"). ADR-0011 models the level as a state rather
    /// than a counter, so a second breach on the other clock re-escalating a
    /// ticket already sitting with the Department Head would be a duplicate,
    /// not new information. The filtered unique index on
    /// <c>TicketEscalations</c> is the concurrent-race backstop behind this
    /// check.
    /// </para>
    ///
    /// <para>
    /// <b>The ticket's own level only rises.</b> If a CS Manager has already
    /// taken the ticket to Level 3 or 4, the automatic escalation is still
    /// recorded — the breach genuinely occurred and the recipient matrix
    /// still applies — but the ticket is not pulled back down to Level 2.
    /// </para>
    ///
    /// <para>
    /// <b>TicketStatus is never touched.</b> Escalation is an independent
    /// dimension (ADR-0008/ADR-0011): an escalated ticket stays exactly as
    /// Open, InProgress or Resolved as it already was.
    /// </para>
    /// </summary>
    private async Task RaiseAutomaticLevel2Async(
        Ticket ticket, DateTime dueAtUtc, SlaDeadlineType deadlineType, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken)
    {
        if (await escalationRepository.HasAutomaticBreachEscalationAsync(ticket.TicketId, cancellationToken))
        {
            return;
        }

        // Reachable only from a breached deadline, which requires an SLA
        // period, which only a classified ticket has — so the priority that
        // decides which roles are notified is always a real one.
        var priorityId = ticket.PriorityId
            ?? throw new InvalidOperationException(
                $"Ticket {ticket.TicketId} breached an SLA deadline without a priority — an SLA period should not exist for an unclassified ticket.");

        var escalation = TicketEscalation.RaiseAutomaticLevel2OnBreach(ticket.TicketId, priorityId, nowUtc);
        await escalationRepository.AddAsync(escalation, cancellationToken);

        var previousLevel = ticket.EscalationLevel;
        if (previousLevel < EscalationLevel.Level2)
        {
            ticket.RaiseEscalationLevel(EscalationLevel.Level2);

            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticket.TicketId, TicketStatusDimension.EscalationLevel, (byte)previousLevel, (byte)ticket.EscalationLevel,
                    actorEmployeeId: null, actorIsSystem: true,
                    note: $"Automatic Level 2 escalation on {deadlineType} SLA breach.", correlationId, nowUtc),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            action: "AutoEscalateOnBreach",
            entityType: nameof(TicketEscalation),
            entityId: ticket.TicketId.ToString(),
            beforeValue: $"{{\"escalationLevel\":\"{previousLevel}\"}}",
            afterValue:
                $"{{\"escalationLevel\":\"{ticket.EscalationLevel}\",\"level\":{escalation.Level},"
                + $"\"triggerType\":\"{escalation.TriggerType}\",\"notifiedRoles\":\"{escalation.NotifiedRoles}\","
                + $"\"deadlineType\":\"{deadlineType}\",\"dueAtUtc\":\"{dueAtUtc:O}\"}}",
            correlationId,
            cancellationToken);
    }

    /// <summary>
    /// The moment the deadline was satisfied, if it has been: ISSUE-019's
    /// <c>FirstHumanResponseAtUtc</c> for First Response, and the current
    /// resolution's <c>ResolvedAtUtc</c> for Resolution — never closure,
    /// which SLA-Architecture.md §2 explicitly excludes as the achievement
    /// event.
    /// </summary>
    private async Task<DateTime?> GetAchievementMomentAsync(
        Ticket ticket, SlaDeadlineType deadlineType, CancellationToken cancellationToken) => deadlineType switch
    {
        SlaDeadlineType.FirstResponse => ticket.FirstHumanResponseAtUtc,
        SlaDeadlineType.Resolution =>
            (await ticketResolutionRepository.GetCurrentAsync(ticket.TicketId, cancellationToken))?.ResolvedAtUtc,
        _ => throw new ArgumentOutOfRangeException(nameof(deadlineType), deadlineType, "Unknown SLA deadline type.")
    };

    /// <summary>See this type's remarks for why the two comparisons differ and why they agree at the boundary.</summary>
    private static bool IsBreached(DateTime dueAtUtc, DateTime? achievedAtUtc, DateTime nowUtc) =>
        achievedAtUtc is { } achieved ? achieved > dueAtUtc : nowUtc >= dueAtUtc;

    private static string BreachFlagName(SlaDeadlineType deadlineType) => deadlineType switch
    {
        SlaDeadlineType.FirstResponse => nameof(TicketSlaInstance.FirstResponseBreached),
        SlaDeadlineType.Resolution => nameof(TicketSlaInstance.ResolutionBreached),
        _ => throw new ArgumentOutOfRangeException(nameof(deadlineType), deadlineType, "Unknown SLA deadline type.")
    };
}
