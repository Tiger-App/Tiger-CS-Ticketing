using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Read-only evidence for Open tickets that have no employee owner (see <see cref="UnassignedTicketClassifier"/>).</summary>
public interface ITicketUnassignedWorkRepository
{
    /// <summary>The next page of Open, unowned tickets with TicketId greater than <paramref name="afterTicketId"/>, oldest id first, with their evidence. Never returns an owned ticket.</summary>
    Task<IReadOnlyList<UnassignedTicketEvidence>> ListOpenUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The next page of ACTIVE (Open, In Progress, Pending Customer, Pending Third Party) tickets with no employee owner and TicketId greater than
    /// <paramref name="afterTicketId"/>, oldest id first (ids are assigned in creation order), with their evidence. Closed and Resolved tickets and owned tickets are never returned.
    /// </summary>
    Task<IReadOnlyList<UnassignedTicketEvidence>> ListActiveUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default);

    /// <summary>The evidence for one ticket (null when it does not exist), whatever its state — used to re-check under the transaction before acting.</summary>
    Task<UnassignedTicketEvidence?> GetEvidenceAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The evidence for several tickets in one go (display paths: the queue). Tickets that do not exist are omitted. The default implementation
    /// asks one ticket at a time; the EF repository overrides it with a single batched read.
    /// </summary>
    async Task<IReadOnlyList<UnassignedTicketEvidence>> ListEvidenceAsync(IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default)
    {
        var result = new List<UnassignedTicketEvidence>(ticketIds.Count);
        foreach (var id in ticketIds)
        {
            if (await GetEvidenceAsync(id, cancellationToken) is { } evidence)
            {
                result.Add(evidence);
            }
        }

        return result;
    }
}
