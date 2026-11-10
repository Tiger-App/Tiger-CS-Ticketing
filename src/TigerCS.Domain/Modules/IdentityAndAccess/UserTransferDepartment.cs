namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// One department an employee is explicitly authorised to TRANSFER tickets to. It is deliberately separate from
/// <see cref="UserDepartmentAssignment"/> (the departments the employee belongs to, i.e. can view) and from the reassignment permission
/// (<c>Tickets.Assign</c>): being able to see a department never lets an employee send tickets there, and the reverse. At most one row per
/// (employee, department). An employee with no rows keeps the role-based behaviour that existed before this list was introduced.
/// </summary>
public class UserTransferDepartment
{
    public int UserTransferDepartmentId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public int DepartmentId { get; private set; }
    public DateTime GrantedAtUtc { get; private set; }
    public Guid? GrantedByEmployeeId { get; private set; }

    private UserTransferDepartment() { }

    public UserTransferDepartment(Guid employeeId, int departmentId, Guid? grantedByEmployeeId, DateTime grantedAtUtc)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("EmployeeId is required.", nameof(employeeId));
        }

        if (departmentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(departmentId), "DepartmentId is required.");
        }

        EmployeeId = employeeId;
        DepartmentId = departmentId;
        GrantedByEmployeeId = grantedByEmployeeId;
        GrantedAtUtc = grantedAtUtc;
    }
}
