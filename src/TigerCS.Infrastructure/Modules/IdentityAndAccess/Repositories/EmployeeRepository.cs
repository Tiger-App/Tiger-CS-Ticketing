using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;

public sealed class EmployeeRepository(TigerCsDbContext dbContext) : IEmployeeRepository
{
    public Task<Employee?> GetByIdAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        dbContext.Employees.FirstOrDefaultAsync(e => e.EmployeeId == employeeId, cancellationToken);

    public async Task AddAsync(Employee employee, CancellationToken cancellationToken = default) =>
        await dbContext.Employees.AddAsync(employee, cancellationToken);

    public async Task<int> CountActiveInRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName, cancellationToken);
        if (role is null)
        {
            return 0;
        }

        var employeeIdsInRole = dbContext.UserRoles
            .Where(ur => ur.RoleId == role.Id)
            .Select(ur => ur.UserId);

        return await dbContext.Employees
            .Where(e => e.DeactivatedAtUtc == null && employeeIdsInRole.Contains(e.EmployeeId))
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Employee>> ListAsync(
        bool includeInactive, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Employees.Include(e => e.DepartmentAssignments).ThenInclude(a => a.Department).AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(e => e.DeactivatedAtUtc == null);
        }

        if (employeeIds is not null)
        {
            query = query.Where(e => employeeIds.Contains(e.EmployeeId));
        }

        return await query.OrderBy(e => e.DisplayName).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveDisplayNamesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        if (employeeIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        // One query: Employees joined to AspNetUsers on the shared key (EmployeeId == AspNetUsers.Id). No Includes, no per-ticket round trips.
        var rows = await (from e in dbContext.Employees.AsNoTracking()
                          where employeeIds.Contains(e.EmployeeId)
                          join u in dbContext.Users.AsNoTracking() on e.EmployeeId equals u.Id into users
                          from u in users.DefaultIfEmpty()
                          select new { e.EmployeeId, e.DisplayName, UserName = u != null ? u.UserName : null, Email = u != null ? u.Email : null })
            .ToListAsync(cancellationToken);

        var names = new Dictionary<Guid, string>(rows.Count);
        foreach (var row in rows)
        {
            var name = !string.IsNullOrWhiteSpace(row.DisplayName) ? row.DisplayName
                : !string.IsNullOrWhiteSpace(row.UserName) ? row.UserName
                : row.Email;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[row.EmployeeId] = name.Trim();
            }
        }

        return names;
    }

    public async Task<bool> IsReferencedByHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.Tickets.AnyAsync(t => t.CurrentOwnerEmployeeId == employeeId, cancellationToken)
        || await dbContext.TicketAssignments.AnyAsync(a => a.AssignedEmployeeId == employeeId || a.AssigningActorEmployeeId == employeeId, cancellationToken)
        || await dbContext.TicketApprovals.AnyAsync(a => a.RequestedByEmployeeId == employeeId || a.DecidedByEmployeeId == employeeId, cancellationToken)
        || await dbContext.TicketStatusHistoryEntries.AnyAsync(h => h.ActorEmployeeId == employeeId, cancellationToken)
        || await dbContext.AuditEntries.AnyAsync(a => a.ActorEmployeeId == employeeId, cancellationToken);
}
