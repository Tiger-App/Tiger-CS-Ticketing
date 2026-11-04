using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketResolutionRepository(TigerCsDbContext dbContext) : ITicketResolutionRepository
{
    /// <summary>
    /// The ticket's current resolution, including one added in this unit of work that has not been saved yet.
    ///
    /// <para>
    /// A resolve adds its <see cref="TicketResolution"/> and, in the same unit of work and before it saves, asks the SLA breach processor
    /// to judge the resolution against its deadline - which reads the resolution's <c>ResolvedAtUtc</c> through this method. A database query
    /// cannot see a row that is only tracked, so the processor used to find no resolution, treat the Resolution SLA as unachieved and never
    /// record <c>Met</c> for a ticket resolved within SLA (a late one still read as breached only because the clock had passed the deadline).
    /// Tracked entities are therefore consulted first; the database answers for everything already saved.
    /// </para>
    /// </summary>
    public async Task<TicketResolution?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketResolutions.Local.LastOrDefault(r => r.TicketId == ticketId && r.IsCurrent)
        ?? await dbContext.TicketResolutions.FirstOrDefaultAsync(r => r.TicketId == ticketId && r.IsCurrent, cancellationToken);

    public async Task<IReadOnlyDictionary<long, TicketResolution>> ListCurrentByTicketIdsAsync(
        IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.TicketResolutions
            .Where(r => r.IsCurrent && ticketIds.Contains(r.TicketId))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.TicketId);
    }

    public async Task AddAsync(TicketResolution resolution, CancellationToken cancellationToken = default) =>
        await dbContext.TicketResolutions.AddAsync(resolution, cancellationToken);
}
