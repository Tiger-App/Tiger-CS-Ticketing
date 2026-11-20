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

    public async Task<IReadOnlyDictionary<long, Guid>> GetLastAssignedEmployeesAsync(IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default)
    {
        if (ticketIds.Count == 0)
        {
            return new Dictionary<long, Guid>();
        }

        var wanted = ticketIds.Distinct().ToList();
        var rows = await dbContext.TicketAssignments.AsNoTracking()
            .Where(a => wanted.Contains(a.TicketId))
            .GroupBy(a => a.TicketId)
            .Select(g => new { TicketId = g.Key, LastId = g.Max(a => a.TicketAssignmentId) })
            .ToListAsync(cancellationToken);
        var lastIds = rows.Select(r => r.LastId).ToList();
        var employees = await dbContext.TicketAssignments.AsNoTracking()
            .Where(a => lastIds.Contains(a.TicketAssignmentId))
            .Select(a => new { a.TicketId, a.AssignedEmployeeId })
            .ToListAsync(cancellationToken);
        return employees.ToDictionary(e => e.TicketId, e => e.AssignedEmployeeId);
    }

    public async Task AddAsync(TicketAssignment assignment, CancellationToken cancellationToken = default) =>
        await dbContext.TicketAssignments.AddAsync(assignment, cancellationToken);
}
