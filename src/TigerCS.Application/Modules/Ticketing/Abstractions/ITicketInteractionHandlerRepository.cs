using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// The Ticketing users who actually handled a ticket's Genesys interactions (<see cref="TicketInteractionHandler"/>). Written by the Genesys
/// integration; read by ticket visibility, which is why it lives with Ticketing rather than with the integration.
/// </summary>
public interface ITicketInteractionHandlerRepository
{
    /// <summary>
    /// Stages <paramref name="handler"/> unless a handler for the same (interaction, user) already exists — saved or pending in this unit of
    /// work. Returns false (and stages nothing) when it does. Persisted by the caller's next <c>SaveChanges</c>.
    /// </summary>
    Task<bool> AddIfAbsentAsync(TicketInteractionHandler handler, CancellationToken cancellationToken = default);

    /// <summary>Drops a staged handler that could not be saved (a concurrent delivery inserted the same (interaction, user) first), so a later save in the same request does not retry it.</summary>
    void Discard(TicketInteractionHandler handler);

    /// <summary>True when <paramref name="userId"/> handled at least one Genesys interaction on <paramref name="ticketId"/>.</summary>
    Task<bool> HasHandledAsync(long ticketId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the user handled an interaction on the ticket AFTER its latest manual transfer (a Transfer that is not system routing).
    /// A handler whose handling predates a transfer keeps read access (<see cref="HasHandledAsync"/>) but no longer modifies the ticket:
    /// ordinary Resolve, Close and Transfer move with it. Handling recorded after the transfer counts again.
    /// </summary>
    Task<bool> HasActiveHandlerGrantAsync(long ticketId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Every handler recorded for a ticket, oldest first.</summary>
    Task<IReadOnlyList<TicketInteractionHandler>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);
}
