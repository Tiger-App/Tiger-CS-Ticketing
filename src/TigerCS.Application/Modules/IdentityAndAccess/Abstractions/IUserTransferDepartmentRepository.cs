using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

/// <summary>Port over <c>UserTransferDepartments</c>: the departments an employee is explicitly authorised to transfer tickets to.</summary>
public interface IUserTransferDepartmentRepository
{
    /// <summary>Untracked read of the department ids (the authorization hot path). Empty when the employee has no explicit list.</summary>
    Task<IReadOnlyList<int>> GetDepartmentIdsAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Tracked rows of one employee (the save path edits them in place).</summary>
    Task<IReadOnlyList<UserTransferDepartment>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task AddAsync(UserTransferDepartment row, CancellationToken cancellationToken = default);

    void Remove(UserTransferDepartment row);
}
