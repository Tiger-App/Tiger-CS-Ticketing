namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The persisted Round Robin cursor of one department: the employee who most
/// recently received an automatically rotated ticket. One row per department,
/// so the rotation survives application restarts and is shared by every
/// instance. The next assignee is always derived from this cursor against the
/// department's current eligible employees, so members joining or leaving never
/// corrupt the rotation.
/// </summary>
public class DepartmentAssignmentRotation
{
    public int DepartmentId { get; private set; }

    public Guid? LastAssignedEmployeeId { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic-concurrency token; the repository additionally row-locks the cursor on relational stores so concurrent assignments serialize instead of failing.</summary>
    public byte[] RowVersion { get; private set; } = [];

    private DepartmentAssignmentRotation() { }

    public DepartmentAssignmentRotation(int departmentId, DateTime nowUtc)
    {
        DepartmentId = departmentId;
        UpdatedAtUtc = nowUtc;
    }

    public void Advance(Guid employeeId, DateTime nowUtc)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("EmployeeId is required.", nameof(employeeId));
        }

        LastAssignedEmployeeId = employeeId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// The next employee after the cursor in a stable (id-ordered) cycle. A cursor that is unset or no longer eligible
    /// restarts at the first eligible employee; null only when nobody is eligible.
    /// </summary>
    public static Guid? PickNext(Guid? lastAssignedEmployeeId, IReadOnlyCollection<Guid> eligibleEmployeeIds)
    {
        var ordered = eligibleEmployeeIds.Distinct().OrderBy(id => id).ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        if (lastAssignedEmployeeId is { } last)
        {
            foreach (var id in ordered)
            {
                if (id.CompareTo(last) > 0)
                {
                    return id;
                }
            }
        }

        return ordered[0];
    }
}
