using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// <b>Closed is terminal for every automated workflow.</b> Once a ticket is Closed — by a person, the AI, the system, the inactivity job, document
/// delivery, the Queueing System or any background job — nothing automatic may reopen it or change its status, owner, department, SLA, closure
/// date or closure reason. A late, duplicate, replayed or out-of-order event for its conversation is recorded as evidence
/// (<see cref="GenesysAuditActions.EventIgnoredOnClosedTicket"/>, the transcript, the interaction) and the ticket stays exactly as it was. The only way
/// back is the explicit Reopen action of an authorized person (<c>TicketLifecycleAppService.ReopenAsync</c>), the sole caller of
/// <see cref="Ticket.Reopen"/>; a regression test fails the build if any other caller appears.
/// </summary>
public static class ClosedTicketGuard
{
    public static bool IsTerminal(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return ticket.TicketStatus == TicketStatus.Closed;
    }
}
