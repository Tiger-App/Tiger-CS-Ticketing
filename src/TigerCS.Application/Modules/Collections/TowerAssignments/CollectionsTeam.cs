using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

/// <summary>
/// Who the Collections team is and who may do what with it. The department is found by its configured code (<see cref="CollectionsOptions.CollectionsDepartmentCode"/>), never by name, and
/// membership is the existing <c>UserDepartmentAssignments</c>. The tower page and the manual ticket assignment both ask this one class, so the two cannot drift.
/// </summary>
public sealed class CollectionsTeam(
    CollectionsOptions options, IDepartmentRepository departments, IUserDepartmentAssignmentRepository members)
{
    public async Task<Department?> GetDepartmentAsync(CancellationToken cancellationToken = default) =>
        (await departments.ListAsync(activeOnly: true, cancellationToken)).FirstOrDefault(d =>
            string.Equals(d.Code, options.CollectionsDepartmentCode, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> IsCollectionsDepartmentAsync(int departmentId, CancellationToken cancellationToken = default) =>
        await GetDepartmentAsync(cancellationToken) is { } collections && collections.DepartmentId == departmentId;

    /// <summary>Active employees who belong to the Collections department, by display name.</summary>
    public async Task<IReadOnlyList<(Guid EmployeeId, string Name)>> ListActiveMembersAsync(CancellationToken cancellationToken = default)
    {
        if (await GetDepartmentAsync(cancellationToken) is not { } collections) return [];
        return (await members.GetByDepartmentIdAsync(collections.DepartmentId, activeEmployeesOnly: true, cancellationToken))
            .Where(a => a.Employee is null || a.Employee.IsActive)
            .Select(a => (a.EmployeeId, Name: a.Employee?.DisplayName ?? a.EmployeeId.ToString()))
            .DistinctBy(m => m.EmployeeId)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when the employee is an ACTIVE member of the department (membership alone is not enough: a deactivated employee is never an assignee).</summary>
    public async Task<bool> IsActiveMemberAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        (await members.GetByDepartmentIdAsync(departmentId, activeEmployeesOnly: true, cancellationToken))
            .Any(a => a.EmployeeId == employeeId && (a.Employee is null || a.Employee.IsActive));

    /// <summary>
    /// Manual assign / reassign of a Collections ticket by the team itself: a Department Employee or Department Head (the configured Collections department roles) who is a member of
    /// the Collections department. CS Manager and CS Supervisor keep their existing authority in <c>TicketAssignmentAppService</c>; nobody outside Collections gains anything here.
    /// </summary>
    public async Task<bool> CanAssignTicketsAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, int ticketDepartmentId, CancellationToken cancellationToken = default)
    {
        if (Roles.IsReadOnlyCaller(callerRoles)) return false;
        if (!callerRoles.Any(r => options.Authorization.CollectionsDepartmentRoles.Contains(r, StringComparer.Ordinal))) return false;
        return await IsCollectionsDepartmentAsync(ticketDepartmentId, cancellationToken)
            && await members.ExistsAsync(callerEmployeeId, ticketDepartmentId, cancellationToken);
    }

    /// <summary>
    /// Whether the caller can assign / reassign THIS Collections ticket at all, by any of the existing rules (CS Manager, a CS Supervisor or Department Head of Collections, the
    /// System Administrator override) or as a member of the Collections team. What the Ticket Details page uses to offer the control; <c>TicketAssignmentAppService</c> still decides.
    /// </summary>
    public async Task<bool> CanAssignCollectionsTicketAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, int ticketDepartmentId, CancellationToken cancellationToken = default)
    {
        if (Roles.IsReadOnlyCaller(callerRoles) || !await IsCollectionsDepartmentAsync(ticketDepartmentId, cancellationToken)) return false;
        return await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(TicketRoleSets.AssignCrossDepartment.Contains)
            || (callerRoles.Any(TicketRoleSets.AssignWithinOwnDepartment.Contains) && await members.ExistsAsync(callerEmployeeId, ticketDepartmentId, cancellationToken))
            || await CanAssignTicketsAsync(callerEmployeeId, callerRoles, ticketDepartmentId, cancellationToken));
    }

    /// <summary>
    /// Managing the monthly tower assignments: the Collections manager (a Department Head who belongs to the Collections department), the CS Manager, and the System Administrator
    /// through the central override. Collections employees without the manager role can read the page but not change it.
    /// </summary>
    public async Task<bool> CanManageTowersAsync(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken = default)
    {
        if (Roles.IsReadOnlyCaller(callerRoles)) return false;
        return await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Contains(Roles.CsManager)
            || (callerRoles.Contains(Roles.DepartmentHead)
                && await GetDepartmentAsync(cancellationToken) is { } collections
                && await members.ExistsAsync(callerEmployeeId, collections.DepartmentId, cancellationToken)));
    }

    /// <summary>
    /// Reading the page and its history is administration data and needs the same authority as changing it: the Collections manager, the CS Manager and the System Administrator.
    /// Plain members of the Collections department do not read it (their manual ticket Assign / Reassign is unaffected: see <see cref="CanAssignTicketsAsync"/>).
    /// </summary>
    public Task<bool> CanViewTowersAsync(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken = default) =>
        CanManageTowersAsync(callerEmployeeId, callerRoles, cancellationToken);
}
