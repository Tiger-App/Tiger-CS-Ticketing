using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.Reporting.Services;

/// <summary>One finalized SLA clock cycle, reduced to what counting needs.</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="SlaType">Which clock.</param>
/// <param name="Breached">True for a breach event, false for an on-time achievement.</param>
internal readonly record struct SlaOutcomeFact(long TicketId, SlaDeadlineType SlaType, bool Breached);

/// <summary>
/// The SLA counts for a set of finalized clock cycles. ONE aggregation, used by the main table's rows, its totals row and the employee
/// details summary alike, so the three can never disagree.
/// <list type="bullet">
///   <item><description><c>BreachEvents</c> — breach events (one per ticket, clock and deadline).</description></item>
///   <item><description><c>TicketsWithBreach</c> — distinct tickets among them (a ticket whose two clocks both breached counts once).</description></item>
///   <item><description><c>Evaluated</c> = Met + BreachEvents; pending and not-applicable clocks are not facts and so never enter.</description></item>
///   <item><description><c>CompliancePercent</c> = Met / Evaluated × 100, from these combined counts — for a totals row, the combined Met and Evaluated of every employee, never an average of percentages.</description></item>
/// </list>
/// </summary>
internal readonly record struct SlaCounts(
    int TicketsWithBreach, int BreachEvents, int FirstResponseBreaches, int ResolutionBreaches, int Met)
{
    public int Evaluated => Met + BreachEvents;

    public decimal? CompliancePercent => TeamPerformanceAppService.CompliancePercent(Met, BreachEvents);

    /// <summary>Counts the facts. Distinct tickets are distinct across ALL the facts given, so a total over several employees de-duplicates a ticket they share.</summary>
    public static SlaCounts Of(IEnumerable<SlaOutcomeFact> facts)
    {
        var list = facts as IReadOnlyCollection<SlaOutcomeFact> ?? facts.ToList();
        var breaches = list.Where(f => f.Breached).ToList();
        return new SlaCounts(
            breaches.Select(f => f.TicketId).Distinct().Count(),
            breaches.Count,
            breaches.Count(f => f.SlaType == SlaDeadlineType.FirstResponse),
            breaches.Count(f => f.SlaType == SlaDeadlineType.Resolution),
            list.Count(f => !f.Breached));
    }
}
