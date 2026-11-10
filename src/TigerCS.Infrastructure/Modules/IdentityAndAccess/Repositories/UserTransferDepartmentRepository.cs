using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;

public sealed class UserTransferDepartmentRepository(TigerCsDbContext dbContext) : IUserTransferDepartmentRepository
{
    public async Task<IReadOnlyList<int>> GetDepartmentIdsAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.UserTransferDepartments.AsNoTracking()
            .Where(t => t.EmployeeId == employeeId)
            .Select(t => t.DepartmentId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<UserTransferDepartment>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.UserTransferDepartments.Where(t => t.EmployeeId == employeeId).ToListAsync(cancellationToken);

    public async Task AddAsync(UserTransferDepartment row, CancellationToken cancellationToken = default) =>
        await dbContext.UserTransferDepartments.AddAsync(row, cancellationToken);

    public void Remove(UserTransferDepartment row) => dbContext.UserTransferDepartments.Remove(row);
}
