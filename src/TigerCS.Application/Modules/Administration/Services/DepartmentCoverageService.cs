using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Administration.Services;

public enum DepartmentStaffingStatus
{
    /// <summary>At least one active employee is eligible to receive automatically assigned tickets.</summary>
    Staffed,

    /// <summary>No active employee is a member of the department. Tickets cannot be assigned; needs an employee assignment.</summary>
    Unstaffed,

    /// <summary>Active members exist but none can receive automatic assignment (e.g. only supervisors, read-only users, locked-out accounts).</summary>
    NoAssignableEmployee
}

public sealed record DepartmentCoverageLine(
    int DepartmentId,
    string Name,
    string Code,
    int ActiveEmployeeCount,
    int AssignableEmployeeCount,
    DepartmentStaffingStatus Status,
    IReadOnlyList<string> ActiveEmployees)
{
    public bool NeedsStaffing => Status != DepartmentStaffingStatus.Staffed;
}

/// <summary>
/// Read-only department coverage: for every ACTIVE department, how many active employees belong to it and how many of them are eligible
/// for automatic (Round Robin) assignment. The eligibility test mirrors <c>TicketAutoAssignmentService</c> exactly (not a service identity,
/// not locked out, an operational role — CS Agents only in the Call Center — and not read-only), so "Staffed" here means the rotation has
/// somebody to pick. Nothing is created or changed; "Unstaffed" is derived from the existing department/user relationships, not stored.
/// </summary>
public sealed class DepartmentCoverageService(
    IDepartmentRepository departmentRepository,
    IUserDepartmentAssignmentRepository assignmentRepository,
    IEmployeeRepository employeeRepository,
    IUserRoleReader roleReader,
    IUserAccountManager accountManager,
    IServiceIdentityRegistry? serviceIdentities = null)
{
    public const string CallCenterCode = "CC";

    public async Task<IReadOnlyList<DepartmentCoverageLine>> GetAsync(CancellationToken cancellationToken = default)
    {
        var departments = (await departmentRepository.ListAsync(activeOnly: true, cancellationToken))
            .OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var lines = new List<DepartmentCoverageLine>(departments.Count);

        foreach (var department in departments)
        {
            var callCenter = string.Equals(department.Code, CallCenterCode, StringComparison.OrdinalIgnoreCase);
            var memberships = await assignmentRepository.GetByDepartmentIdAsync(department.DepartmentId, activeEmployeesOnly: true, cancellationToken);

            var names = new List<string>();
            var assignable = 0;
            foreach (var employeeId in memberships.Select(m => m.EmployeeId).Distinct())
            {
                var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
                if (employee is not { IsActive: true })
                {
                    continue;
                }

                names.Add(employee.DisplayName);
                if (await IsAssignableAsync(employeeId, callCenter, cancellationToken))
                {
                    assignable++;
                }
            }

            var status = names.Count == 0 ? DepartmentStaffingStatus.Unstaffed
                : assignable == 0 ? DepartmentStaffingStatus.NoAssignableEmployee
                : DepartmentStaffingStatus.Staffed;
            lines.Add(new DepartmentCoverageLine(
                department.DepartmentId, department.Name, department.Code, names.Count, assignable, status, names.Order(StringComparer.Ordinal).ToList()));
        }

        return lines;
    }

    private async Task<bool> IsAssignableAsync(Guid employeeId, bool callCenter, CancellationToken cancellationToken)
    {
        if (serviceIdentities?.IsServiceIdentity(employeeId) == true)
        {
            return false;
        }

        if (await accountManager.GetAccountAsync(employeeId, cancellationToken) is not { IsLockedOut: false })
        {
            return false;
        }

        var roles = await roleReader.GetRolesAsync(employeeId, cancellationToken);
        var eligibleRoles = callCenter ? TicketRoleSets.CallCenterAutoAssignable : TicketRoleSets.RoundRobinEligible;
        return !Roles.IsReadOnlyCaller(roles) && roles.Any(eligibleRoles.Contains);
    }
}
