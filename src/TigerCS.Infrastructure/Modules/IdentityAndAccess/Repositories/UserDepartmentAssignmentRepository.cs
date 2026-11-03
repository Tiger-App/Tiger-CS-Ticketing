using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;

public sealed class UserDepartmentAssignmentRepository(TigerCsDbContext dbContext) : IUserDepartmentAssignmentRepository
{
    public async Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByEmployeeIdAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.UserDepartmentAssignments
            .Include(a => a.Department)
            .Where(a => a.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public Task<UserDepartmentAssignment?> GetPrimaryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        dbContext.UserDepartmentAssignments
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.IsPrimary, cancellationToken);

    public Task<bool> ExistsAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        dbContext.UserDepartmentAssignments
            .AnyAsync(a => a.EmployeeId == employeeId && a.DepartmentId == departmentId, cancellationToken);

    public Task<bool> HoldsRoleAsync(Guid employeeId, string roleName, CancellationToken cancellationToken = default) =>
        dbContext.UserRoles.AnyAsync(
            ur => ur.UserId == employeeId && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.Name == roleName), cancellationToken);

    public async Task<bool> IsResponsibleCsAgentAsync(long ticketId, Guid? creatorEmployeeId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var csAgentRole = Roles.CsAgent;
        var latest = await dbContext.TicketAssignments
            .Where(a => a.TicketId == ticketId
                && dbContext.UserRoles.Any(ur => ur.UserId == a.AssignedEmployeeId
                    && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.Name == csAgentRole)))
            .OrderByDescending(a => a.TicketAssignmentId)
            .Select(a => (Guid?)a.AssignedEmployeeId)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null)
        {
            return latest == employeeId;
        }

        return creatorEmployeeId == employeeId && await HoldsRoleAsync(employeeId, csAgentRole, cancellationToken);
    }

    public async Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByDepartmentIdAsync(
        int departmentId, bool activeEmployeesOnly, CancellationToken cancellationToken = default)
    {
        var query = dbContext.UserDepartmentAssignments
            .Include(a => a.Employee)
            .Include(a => a.Department)
            .Where(a => a.DepartmentId == departmentId);

        if (activeEmployeesOnly)
        {
            query = query.Where(a => a.Employee.DeactivatedAtUtc == null);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<UserDepartmentAssignment>> ListActiveMembershipsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.UserDepartmentAssignments
            .Include(a => a.Employee)
            .Include(a => a.Department)
            .Where(a => a.Employee.DeactivatedAtUtc == null && a.Department.IsActive)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(UserDepartmentAssignment assignment, CancellationToken cancellationToken = default) =>
        await dbContext.UserDepartmentAssignments.AddAsync(assignment, cancellationToken);

    public Task<UserDepartmentAssignment?> GetAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        dbContext.UserDepartmentAssignments
            .Include(a => a.Department)
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.DepartmentId == departmentId, cancellationToken);

    public void Remove(UserDepartmentAssignment assignment) => dbContext.UserDepartmentAssignments.Remove(assignment);
}

public sealed class IdentityUnitOfWork(TigerCsDbContext dbContext) : IIdentityUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
