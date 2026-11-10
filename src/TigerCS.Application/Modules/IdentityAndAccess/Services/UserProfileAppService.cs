using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>MVP-API-Contracts.md §1.3 GET /api/users/me.</summary>
public sealed class UserProfileAppService(
    IEmployeeRepository employeeRepository,
    IUserRoleReader roleReader,
    IUserDepartmentAssignmentRepository assignmentRepository,
    IUserPermissionOverrideRepository? permissionOverrides = null,
    IDepartmentRepository? departmentRepository = null)
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

        // The Tickets Department filter must offer exactly the departments the ticket queries will let this caller see - not just where they
        // are a member. A cross-department role (CS Supervisor/Manager, executives, the System Administrator override) sees every department,
        // so it is offered every active one; everyone else sees their memberships. Same decision as TicketQueryAppService.ResolveVisibleDepartmentIdsAsync.
        var accessible = departments;
        var seesEveryDepartment = !TicketVisibilityRule.IsRestrictedCsAgent(roles)
            && AuthorizationGate.Evaluate(roles, () => roles.Any(TicketRoleSets.CrossDepartmentView.Contains));
        if (seesEveryDepartment && departmentRepository is not null)
        {
            accessible = (await departmentRepository.ListAsync(activeOnly: true, cancellationToken))
                .Select(d => new DepartmentMembershipDto(d.DepartmentId, d.Name, departments.Any(m => m.DepartmentId == d.DepartmentId && m.IsPrimary)))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new CurrentUserResponseDto(
            employee.EmployeeId, employee.DisplayName, roles, departments, employee.IsGeynessStaff,
            EffectivePermissions.Compute(roles, effects).Order(StringComparer.Ordinal).ToList(), granted, denied, accessible);
    }
}
