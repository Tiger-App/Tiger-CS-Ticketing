using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// The one place <c>Ticket.FirstHumanResponseAtUtc</c> is satisfied, and
/// everything that must happen with it: the lifecycle history row, the audit
/// entry, and the breach finalization that turns a late first response into a
/// recorded breach (and its automatic Level 2 escalation).
///
/// <para>
/// <b>Why this exists rather than a second mechanism.</b> First Response is
/// reached two ways: a person recording it through
/// <c>POST /api/tickets/{id}/sla/first-response</c>
/// (<see cref="SlaFirstResponseAppService"/>, which owns the authorization and
/// optimistic-concurrency half), and the system observing it when the first
/// genuinely human message of a conversation is stored
/// (<c>GenesysConversationEndAppService</c>). Those are different callers with
/// different permission stories, but the <i>consequence</i> is identical, and
/// ISSUE-019's whole point is that this measurement must be written in exactly
/// one way. Both therefore call this.
/// </para>
///
/// <para>
/// <b>What is deliberately NOT here.</b> No authorization — the API path gates
/// itself and the system path has no user to gate. No transaction and no
/// <c>SaveChangesAsync</c> — this enlists in the caller's unit of work, the
/// same contract <c>SlaDueDateService</c> and
/// <c>TicketAutoAssignmentService</c> follow, so the response and whatever
/// prompted it commit together or not at all. No RowVersion handling: the
/// caller decides whether a stale token is a conflict.
/// </para>
///
/// <para>
/// <b>An AI or automated reply can never reach this.</b> The automated
/// acknowledgement has its own column
/// (<c>Ticket.AcknowledgementSentAtUtc</c>), and the transcript path calls
/// this only for <see cref="InteractionMessageSender.HumanAgent"/> lines —
/// never <see cref="InteractionMessageSender.VirtualAgent"/>. FR-SLA-05 /
/// ISSUE-019 exist precisely because a machine responding instantly would
/// otherwise satisfy every first-response target and make the KPI
/// meaningless.
/// </para>
/// </summary>
public sealed class FirstHumanResponseRecorder(
    ITicketStatusHistoryRepository statusHistoryRepository,
    IAuditEntryWriter auditWriter,
    SlaBreachProcessor breachProcessor)
{
    /// <summary>
    /// Records the first human response if the ticket does not already carry
    /// one.
    /// </summary>
    /// <param name="ticket">The ticket. Mutated in place; the caller saves.</param>
    /// <param name="occurredAtUtc">
    /// The moment of human engagement. May precede <c>Ticket.CreatedAtUtc</c> —
    /// SLA-Architecture.md §16's Example E has a call answered before its
    /// ticket existed, and the real moment is the correct value there.
    /// </param>
    /// <param name="nowUtc">The processing instant, used for the history row and breach evaluation.</param>
    /// <param name="actorEmployeeId">Who is credited. For the system path this is the integration caller.</param>
    /// <param name="actorIsSystem">True when no person performed this directly.</param>
    /// <param name="source">How it was established, for the audit trail.</param>
    /// <param name="correlationId">Ties these rows to whatever prompted them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// True when this call recorded it. False when the ticket already had one,
    /// or is Closed — both of which are ordinary, not failures: write-once is
    /// the point, and a Closed ticket's SLA outcome was settled when it was
    /// resolved.
    /// </returns>
    public async Task<bool> TryRecordAsync(
        Ticket ticket,
        DateTime occurredAtUtc,
        DateTime nowUtc,
        Guid actorEmployeeId,
        bool actorIsSystem,
        FirstResponseSource source,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.FirstHumanResponseAtUtc is not null || ticket.TicketStatus == TicketStatus.Closed)
        {
            return false;
        }

        try
        {
            ticket.RecordFirstHumanResponse(occurredAtUtc);
        }
        catch (TicketClosedException)
        {
            return false;
        }
        catch (FirstResponseAlreadyRecordedException)
        {
            // Lost a race with another path. Write-once held, which is the
            // guarantee that matters.
            return false;
        }

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticket.TicketId, TicketStatusDimension.SlaState, (byte)ticket.SlaState, (byte)ticket.SlaState,
                actorEmployeeId, actorIsSystem,
                note: $"First human response recorded ({source}) at {occurredAtUtc:O}; entered at {nowUtc:O}.", correlationId, nowUtc),
            cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "RecordFirstResponse", nameof(Ticket), ticket.TicketId.ToString(),
            beforeValue: "{\"firstHumanResponseAtUtc\":null}",
            afterValue: $"{{\"firstHumanResponseAtUtc\":\"{occurredAtUtc:O}\",\"source\":\"{source}\",\"recordedAtUtc\":\"{nowUtc:O}\","
                + $"\"backdatedMinutes\":{BackdatedMinutes(occurredAtUtc, nowUtc)},\"actorIsSystem\":{(actorIsSystem ? "true" : "false")}}}",
            correlationId, cancellationToken);

        // Finalizes the breach flag if the response landed late — and, if it
        // did, raises the automatic Level 2 escalation exactly as a scheduled
        // job would have. Idempotent on its own key, so a job that already
        // flagged this deadline is not double-counted.
        await breachProcessor.ProcessDeadlineAsync(
            ticket, SlaDeadlineType.FirstResponse, nowUtc, correlationId, cancellationToken);

        return true;
    }

    /// <summary>Whole minutes between the stated moment of the response and the moment it was entered (0 when entered as it happens or stated in the future). Written to the audit trail so an after-the-fact entry is visible whoever made it.</summary>
    internal static int BackdatedMinutes(DateTime occurredAtUtc, DateTime recordedAtUtc) =>
        occurredAtUtc >= recordedAtUtc ? 0 : (int)Math.Min(int.MaxValue, (recordedAtUtc - occurredAtUtc).TotalMinutes);
}
