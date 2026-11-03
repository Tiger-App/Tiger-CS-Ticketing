using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketNoteRepository(TigerCsDbContext dbContext) : ITicketNoteRepository
{
    public async Task AddAsync(TicketNote note, CancellationToken cancellationToken = default) =>
        await dbContext.TicketNotes.AddAsync(note, cancellationToken);

    public async Task<IReadOnlyList<TicketNote>> ListByTicketIdAsync(
        long ticketId, int page, int pageSize, CancellationToken cancellationToken = default) =>
        await dbContext.TicketNotes
            .Where(n => n.TicketId == ticketId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.TicketNoteId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

    public Task<int> CountByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketNotes.CountAsync(n => n.TicketId == ticketId, cancellationToken);
}
