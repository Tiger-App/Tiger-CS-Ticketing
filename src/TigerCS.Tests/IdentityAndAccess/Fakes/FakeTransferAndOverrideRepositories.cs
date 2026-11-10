using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Tests.IdentityAndAccess.Fakes;

/// <summary>In-memory transfer-destination lists (Administration → Users).</summary>
public sealed class FakeUserTransferDepartmentRepository : IUserTransferDepartmentRepository
{
    public List<UserTransferDepartment> Rows { get; } = [];

    public FakeUserTransferDepartmentRepository Allow(Guid employeeId, params int[] departmentIds)
    {
        foreach (var id in departmentIds) Rows.Add(new UserTransferDepartment(employeeId, id, null, DateTime.UtcNow));
        return this;
    }

    public Task<IReadOnlyList<int>> GetDepartmentIdsAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<int>>([.. Rows.Where(r => r.EmployeeId == employeeId).Select(r => r.DepartmentId)]);

    public Task<IReadOnlyList<UserTransferDepartment>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserTransferDepartment>>([.. Rows.Where(r => r.EmployeeId == employeeId)]);

    public Task AddAsync(UserTransferDepartment row, CancellationToken cancellationToken = default)
    {
        Rows.Add(row);
        return Task.CompletedTask;
    }

    public void Remove(UserTransferDepartment row) => Rows.Remove(row);
}

/// <summary>In-memory permission overrides, for services that only read an employee's effects.</summary>
public sealed class FakePermissionOverrideRepository : IUserPermissionOverrideRepository
{
    private readonly List<UserPermissionOverride> _rows = [];

    public FakePermissionOverrideRepository Set(Guid employeeId, string permissionKey, PermissionEffect effect)
    {
        _rows.Add(new UserPermissionOverride(employeeId, permissionKey, effect, null, DateTime.UtcNow));
        return this;
    }

    public Task<IReadOnlyList<UserPermissionOverride>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserPermissionOverride>>([.. _rows.Where(r => r.EmployeeId == employeeId)]);

    public Task<IReadOnlyDictionary<string, PermissionEffect>> GetEffectsAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, PermissionEffect>>(_rows.Where(r => r.EmployeeId == employeeId).ToDictionary(r => r.PermissionKey, r => r.Effect));

    public Task AddAsync(UserPermissionOverride permissionOverride, CancellationToken cancellationToken = default)
    {
        _rows.Add(permissionOverride);
        return Task.CompletedTask;
    }

    public void Remove(UserPermissionOverride permissionOverride) => _rows.Remove(permissionOverride);

    public Task<int> CountActiveInRoleWithoutDenialAsync(string roleName, string permissionKey, Guid excludedEmployeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<PermissionChangeLogEntry>> GetRecentChangesAsync(Guid employeeId, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PermissionChangeLogEntry>>([]);
}
