using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Read-only evidence for Open tickets that have no employee owner (see <see cref="UnassignedTicketClassifier"/>).</summary>
public interface ITicketUnassignedWorkRepository
{
    /// <summary>The next page of Open, unowned tickets with TicketId greater than <paramref name="afterTicketId"/>, oldest id first, with their evidence. Never returns an owned ticket.</summary>
    Task<IReadOnlyList<UnassignedTicketEvidence>> ListOpenUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default);

    /// <summary>The evidence for one ticket (null when it does not exist), whatever its state — used to re-check under the transaction before acting.</summary>
    Task<UnassignedTicketEvidence?> GetEvidenceAsync(long ticketId, CancellationToken cancellationToken = default);
}
