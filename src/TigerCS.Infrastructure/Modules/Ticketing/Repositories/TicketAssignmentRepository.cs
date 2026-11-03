using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketAssignmentRepository(TigerCsDbContext dbContext) : ITicketAssignmentRepository
{
    public Task<TicketAssignment?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketAssignments.FirstOrDefaultAsync(a => a.TicketId == ticketId && a.IsCurrent, cancellationToken);

    public Task<bool> HasSupersededAssignmentAsync(long ticketId, Guid employeeId, CancellationToken cancellationToken = default) =>
        dbContext.TicketAssignments.AnyAsync(a => a.TicketId == ticketId && a.AssignedEmployeeId == employeeId && !a.IsCurrent, cancellationToken);

    public async Task<IReadOnlyList<TicketAssignment>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketAssignments.AsNoTracking()
            .Where(a => a.TicketId == ticketId)
            .OrderBy(a => a.AssignedAtUtc).ThenBy(a => a.TicketAssignmentId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TicketAssignment assignment, CancellationToken cancellationToken = default) =>
        await dbContext.TicketAssignments.AddAsync(assignment, cancellationToken);
}
