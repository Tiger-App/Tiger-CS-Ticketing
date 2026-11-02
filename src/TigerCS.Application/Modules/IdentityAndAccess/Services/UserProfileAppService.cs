using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>MVP-API-Contracts.md §1.3 GET /api/users/me.</summary>
public sealed class UserProfileAppService(
    IEmployeeRepository employeeRepository,
    IUserRoleReader roleReader,
    IUserDepartmentAssignmentRepository assignmentRepository,
    IUserPermissionOverrideRepository? permissionOverrides = null)
{
    public async Task<CurrentUserResponseDto?> GetCurrentUserAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return null;
        }

        var roles = await roleReader.GetRolesAsync(employeeId, cancellationToken);
        var assignments = await assignmentRepository.GetByEmployeeIdAsync(employeeId, cancellationToken);

        var departments = assignments
            .Select(a => new DepartmentMembershipDto(a.DepartmentId, a.Department.Name, a.IsPrimary))
            .ToList();

        var effects = permissionOverrides is null
            ? new Dictionary<string, PermissionEffect>()
            : (IReadOnlyDictionary<string, PermissionEffect>)await permissionOverrides.GetEffectsAsync(employeeId, cancellationToken);
        var baseline = RolePermissionDefaults.ForRoles(roles);
        var granted = PermissionCatalog.All.Select(p => p.Key)
            .Where(k => EffectivePermissions.Resolve(k, baseline, effects) == PermissionSource.Granted).ToList();
        var denied = effects.Where(e => e.Value == PermissionEffect.Deny).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();

        return new CurrentUserResponseDto(
            employee.EmployeeId, employee.DisplayName, roles, departments, employee.IsGeynessStaff,
            EffectivePermissions.Compute(roles, effects).Order(StringComparer.Ordinal).ToList(), granted, denied);
    }
}
