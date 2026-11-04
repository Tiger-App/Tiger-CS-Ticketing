using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketCollectionsUnitRepository
{
    /// <summary>The ticket's verified Collections tower/unit selection, tracked for update; null when none was made.</summary>
    Task<TicketCollectionsUnit?> GetAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketCollectionsUnit unit, CancellationToken cancellationToken = default);
}
