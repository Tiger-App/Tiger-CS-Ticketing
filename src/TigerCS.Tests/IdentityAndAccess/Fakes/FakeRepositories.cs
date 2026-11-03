using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.IdentityAndAccess.Fakes;

public sealed class FakeEmployeeRepository : IEmployeeRepository
{
    private readonly Dictionary<Guid, Employee> _employees = [];
    private readonly Dictionary<Guid, List<string>> _rolesByEmployeeId = [];

    public FakeEmployeeRepository Add(Employee employee, params string[] roles)
    {
        _employees[employee.EmployeeId] = employee;
        _rolesByEmployeeId[employee.EmployeeId] = roles.ToList();
        return this;
    }

    public Task<Employee?> GetByIdAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_employees.GetValueOrDefault(employeeId));

    public Task AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        _employees[employee.EmployeeId] = employee;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Employee>> ListAsync(
        bool includeInactive, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Employee>>(_employees.Values
            .Where(e => includeInactive || e.IsActive)
            .Where(e => employeeIds is null || employeeIds.Contains(e.EmployeeId))
            .OrderBy(e => e.DisplayName)
            .ToList());

    public HashSet<Guid> ReferencedByHistory { get; } = [];

    public Task<bool> IsReferencedByHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ReferencedByHistory.Contains(employeeId));

    public Task<int> CountActiveInRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        var count = _employees.Values.Count(e =>
            e.IsActive && _rolesByEmployeeId.TryGetValue(e.EmployeeId, out var roles) && roles.Contains(roleName));
        return Task.FromResult(count);
    }
}

public sealed class FakeDepartmentRepository : IDepartmentRepository
{
    private readonly Dictionary<int, Department> _departments = [];
    private int _nextId = 1;

    public Department AddDepartment(string name, string code, bool isActive = true)
    {
        var department = new Department(name, code, isActive);
        typeof(Department).GetProperty(nameof(Department.DepartmentId))!.SetValue(department, _nextId++);
        _departments[department.DepartmentId] = department;
        return department;
    }

    public Task<Department?> GetByIdAsync(int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_departments.GetValueOrDefault(departmentId));

    public Dictionary<int, int> TicketReferences { get; } = [];

    public Task AddAsync(Department department, CancellationToken cancellationToken = default)
    {
        typeof(Department).GetProperty(nameof(Department.DepartmentId))!.SetValue(department, _nextId++);
        _departments[department.DepartmentId] = department;
        return Task.CompletedTask;
    }

    public Task<bool> NameExistsAsync(string name, int? excludeDepartmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_departments.Values.Any(d => d.Name == name && (excludeDepartmentId is null || d.DepartmentId != excludeDepartmentId)));

    public Task<bool> CodeExistsAsync(string code, int? excludeDepartmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_departments.Values.Any(d => d.Code == code && (excludeDepartmentId is null || d.DepartmentId != excludeDepartmentId)));

    public Task<int> CountTicketReferencesAsync(int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TicketReferences.GetValueOrDefault(departmentId));

    public Task<IReadOnlyCollection<Department>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Department>>(
            _departments.Values.Where(d => !activeOnly || d.IsActive).ToList());
}

public sealed class FakeUserDepartmentAssignmentRepository : IUserDepartmentAssignmentRepository
{
    public readonly List<UserDepartmentAssignment> Assignments = [];

    /// <summary>Identity roles per employee, for <see cref="HoldsRoleAsync"/> (tests that model "the ticket's owner is a CS Agent").</summary>
    public readonly Dictionary<Guid, HashSet<string>> RolesByEmployee = [];

    /// <summary>The responsible CS Agent per ticket, for <see cref="IsResponsibleCsAgentAsync"/> (tests that model department work on a ticket).</summary>
    public readonly Dictionary<long, Guid> ResponsibleCsAgentByTicket = [];

    /// <summary>
    /// When true every user counts as the responsible CS Agent of every ticket a non-CS-Agent owns. For tests whose subject is some OTHER rule
    /// (reopen windows, SLA clocks, classification…) and that use a CS Agent as an arbitrary actor on a ticket a department employee holds.
    /// Tests OF visibility never use it.
    /// </summary>
    public bool EveryUserIsResponsible { get; init; }

    public Task<bool> IsResponsibleCsAgentAsync(long ticketId, Guid? creatorEmployeeId, Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EveryUserIsResponsible
            || (ResponsibleCsAgentByTicket.TryGetValue(ticketId, out var agent) && agent == employeeId));

    /// <summary>(ticket, employee) pairs that are the responsible CS Agent of an inactivity-closed ticket still inside the reopen window.</summary>
    public readonly HashSet<(long TicketId, Guid EmployeeId)> InactivityClosureResponsible = [];

    public Task<bool> IsResponsibleForInactivityClosureAsync(
        long ticketId, Guid? creatorEmployeeId, Guid employeeId, DateTime closedNotBeforeUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(InactivityClosureResponsible.Contains((ticketId, employeeId)));

    public Task<bool> HoldsRoleAsync(Guid employeeId, string roleName, CancellationToken cancellationToken = default) =>
        Task.FromResult(RolesByEmployee.TryGetValue(employeeId, out var roles) && roles.Contains(roleName));

    public Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByEmployeeIdAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<UserDepartmentAssignment>>(
            Assignments.Where(a => a.EmployeeId == employeeId).ToList());

    public Task<UserDepartmentAssignment?> GetPrimaryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.EmployeeId == employeeId && a.IsPrimary));

    public Task<bool> ExistsAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Assignments.Any(a => a.EmployeeId == employeeId && a.DepartmentId == departmentId));

    public Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByDepartmentIdAsync(
        int departmentId, bool activeEmployeesOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<UserDepartmentAssignment>>(
            Assignments.Where(a => a.DepartmentId == departmentId).ToList());

    public Task<UserDepartmentAssignment?> GetAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.EmployeeId == employeeId && a.DepartmentId == departmentId));

    public void Remove(UserDepartmentAssignment assignment) => Assignments.Remove(assignment);

    public Task<IReadOnlyCollection<UserDepartmentAssignment>> ListActiveMembershipsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<UserDepartmentAssignment>>(
            Assignments.Where(a => a.Employee is null || a.Employee.IsActive).ToList());

    public Task AddAsync(UserDepartmentAssignment assignment, CancellationToken cancellationToken = default)
    {
        Assignments.Add(assignment);
        return Task.CompletedTask;
    }
}

public sealed class FakeIdentityUnitOfWork : IIdentityUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeUserRoleReader : IUserRoleReader
{
    private readonly Dictionary<Guid, List<string>> _roles = [];

    public FakeUserRoleReader Add(Guid employeeId, params string[] roles)
    {
        _roles[employeeId] = roles.ToList();
        return this;
    }

    public Task<IReadOnlyCollection<string>> GetRolesAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(_roles.GetValueOrDefault(employeeId, []));
}
