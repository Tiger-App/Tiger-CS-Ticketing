using TigerCS.Application.Modules.SlaAndEscalation.Dto;

namespace TigerCS.Web.Models;

/// <summary>One SLA clock as the page words it: a heading, a short state, and the colour key to draw it with.</summary>
/// <param name="Name">"First response" or "Resolution".</param>
/// <param name="State">One short phrase: "Not started", "Due 14:05", "Overdue since 14:05", "Met", "Missed", "Paused — Pending Customer".</param>
/// <param name="CssKey">breached, running, met, paused or na — the existing SLA badge colour keys.</param>
public sealed record SlaClockView(string Name, string State, string CssKey);

/// <summary>
/// The two SLA clocks, each read from its own facts.
///
/// <para>
/// The Api's <c>SlaState</c> is ONE ticket-level summary of both clocks, and <c>Breached</c> is sticky: a ticket that missed its First
/// Response and then resolved on time still reports <c>Breached</c> (<c>Ticket.MarkSlaMet</c>), and a reopened ticket keeps it while
/// the new Resolution cycle starts clean. Printing that one word beside "Resolution" therefore called a perfectly healthy resolution
/// breached. This view reads <c>FirstResponseBreached</c> / <c>ResolutionBreached</c> / the due times / the pause facts per clock
/// instead, and uses the combined state only as the headline. It changes no SLA rule and reads nothing the Api does not return.
/// </para>
/// </summary>
public sealed record TicketSlaView(string Headline, string HeadlineCss, SlaClockView FirstResponse, SlaClockView Resolution, string? NotStartedReason)
{
    public static TicketSlaView Build(TicketSlaSummaryResponseDto sla, string ticketStatus, DateTime nowUtc)
    {
        var finished = ticketStatus is "Resolved" or "Closed";
        var started = sla.Explanation is not { HasActivePeriod: false };

        // A finished ticket's breach is a fact, not a running count: "Breached 12d" would keep growing after the ticket was resolved.
        var (headline, css) = finished && sla.SlaState == "Breached" ? ("Breached", "breached") : TicketDisplay.SlaBadgeText(sla, nowUtc);

        return new TicketSlaView(
            headline, css, FirstResponseClock(sla, started), ResolutionClock(sla, started, finished),
            started ? null : sla.Explanation?.NotStartedReason);
    }

    private static SlaClockView FirstResponseClock(TicketSlaSummaryResponseDto sla, bool started)
    {
        const string name = "First response";
        if (!started)
        {
            return new SlaClockView(name, "Not started", "na");
        }

        if (sla.FirstHumanResponseAtUtc is { } answered)
        {
            return sla.FirstResponseBreached
                ? new SlaClockView(name, $"Answered late, {TicketDisplay.UaeShort(answered)}", "breached")
                : new SlaClockView(name, $"Met, answered {TicketDisplay.UaeShort(answered)}", "met");
        }

        if (sla.FirstResponseBreached)
        {
            return new SlaClockView(
                name,
                sla.FirstResponseDueAtUtc is { } missed ? $"Overdue since {TicketDisplay.UaeShort(missed)}" : "Overdue — no person has replied", "breached");
        }

        return new SlaClockView(
            name, sla.FirstResponseDueAtUtc is { } due ? $"Awaiting a person's reply, due {TicketDisplay.UaeShort(due)}" : "Awaiting a person's reply", "running");
    }

    private static SlaClockView ResolutionClock(TicketSlaSummaryResponseDto sla, bool started, bool finished)
    {
        const string name = "Resolution";
        if (!started)
        {
            return new SlaClockView(name, "Not started", "na");
        }

        if (finished)
        {
            return sla.ResolutionBreached
                ? new SlaClockView(name, "Missed", "breached")
                : new SlaClockView(name, "Met", "met");
        }

        if (sla.ResolutionBreached)
        {
            return new SlaClockView(
                name, sla.ResolutionDueAtUtc is { } missed ? $"Overdue since {TicketDisplay.UaeShort(missed)}" : "Overdue", "breached");
        }

        if (sla.IsCurrentlyPaused)
        {
            return new SlaClockView(name, $"Paused — {sla.CurrentPauseReason}", "paused");
        }

        return new SlaClockView(
            name, sla.ResolutionDueAtUtc is { } due ? $"Due {TicketDisplay.UaeShort(due)}" : "Running", "running");
    }
}
