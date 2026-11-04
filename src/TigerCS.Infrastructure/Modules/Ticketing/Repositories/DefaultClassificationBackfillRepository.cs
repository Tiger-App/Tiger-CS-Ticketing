using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>Candidate tickets for the default-classification backfill: a superset (missing, partial or possibly dangling ids), judged per ticket by the service. Read-only query; the entities are tracked so a live run saves through the unit of work.</summary>
public sealed class DefaultClassificationBackfillRepository(TigerCsDbContext db) : IDefaultClassificationBackfillRepository
{
    public async Task<IReadOnlyList<Ticket>> ListPossiblyUnclassifiedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
        await db.Tickets
            .Where(t => t.TicketId > afterTicketId
                && (t.CategoryId == null || t.RequestTypeId == null
                    || !db.Categories.Any(c => c.CategoryId == t.CategoryId)
                    || !db.RequestTypes.Any(r => r.RequestTypeId == t.RequestTypeId)))
            .OrderBy(t => t.TicketId)
            .Take(limit)
            .ToListAsync(cancellationToken);
}
