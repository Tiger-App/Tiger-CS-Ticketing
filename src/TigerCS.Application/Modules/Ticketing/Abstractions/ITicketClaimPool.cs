namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Why a ticket can or cannot be taken over by a Call Center agent. <see cref="Available"/> is the only claimable answer.</summary>
public enum TicketClaimStatus
{
    /// <summary>Unassigned, active, human-handling and not waiting on an approval: may be taken.</summary>
    Available,

    /// <summary>No such ticket.</summary>
    NotFound,

    /// <summary>Resolved or Closed - terminal tickets are never taken over.</summary>
    Terminal,

    /// <summary>Somebody already owns it (or holds its current assignment). Never taken from them.</summary>
    Owned,

    /// <summary>An AI conversation is still handling it; taking it would switch off AI closure / chatbot inactivity handling.</summary>
    ActiveAiConversation,

    /// <summary>An approval is pending; the ticket stays with its approvers until it is decided.</summary>
    PendingApproval
}

/// <summary>
/// The pool of unassigned, human-handling tickets a Call Center agent may take ownership of. ONE definition backs the ticket list scope,
/// the "available to take" filter and the single-ticket decision (see <c>TicketQueryFilters.AvailableToTake</c>), and it mirrors the
/// held-back rules of the Call Center backlog distribution scripts.
/// </summary>
public interface ITicketClaimPool
{
    /// <summary>Reads the ticket's current state fresh from the database and says whether (and if not, why) it can be taken over.</summary>
    Task<TicketClaimStatus> GetStatusAsync(long ticketId, CancellationToken cancellationToken = default);
}
