using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public interface IUserDepartmentAssignmentRepository
{
    Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByEmployeeIdAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    Task<UserDepartmentAssignment?> GetPrimaryAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the employee currently holds the named Identity role. Lives here only because every ticket-visibility call site already holds this
    /// repository: <c>TicketVisibilityRule</c> asks it whether a ticket's current owner is a CS Agent (a CS Agent never sees a ticket assigned to
    /// another CS Agent).
    /// </summary>
    Task<bool> HoldsRoleAsync(Guid employeeId, string roleName, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByDepartmentIdAsync(
        int departmentId, bool activeEmployeesOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every membership of every ACTIVE employee in an ACTIVE department, with
    /// the Employee and Department loaded — the cross-department assignee
    /// directory a CS Manager chooses from. Deactivated employees and
    /// deactivated departments are excluded at the source.
    /// </summary>
    Task<IReadOnlyCollection<UserDepartmentAssignment>> ListActiveMembershipsAsync(CancellationToken cancellationToken = default);

    Task AddAsync(UserDepartmentAssignment assignment, CancellationToken cancellationToken = default);

    Task<UserDepartmentAssignment?> GetAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default);

    /// <summary>Ends a membership. Membership rows are not referenced by ticket history (assignments snapshot employee and department ids separately), so removal never breaks a historical record.</summary>
    void Remove(UserDepartmentAssignment assignment);
}

/// <summary>Commits changes made through the Identity and Access repositories in this request.</summary>
public interface IIdentityUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
