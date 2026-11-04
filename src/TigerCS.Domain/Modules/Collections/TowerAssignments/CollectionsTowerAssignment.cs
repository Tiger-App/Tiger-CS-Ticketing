namespace TigerCS.Domain.Modules.Collections.TowerAssignments;

public enum TowerAssignmentStatus : byte
{
    /// <summary>Saved but not in force: automatic routing ignores it.</summary>
    Draft = 0,

    /// <summary>In force for its month: automatic routing uses it.</summary>
    Active = 1
}

/// <summary>
/// One tower's responsible Collections employee for one calendar month. The tower is identified by the LOCAL <c>dbo.CollectionsTowers.TowerId</c> (the id every Collections tower
/// picker already sends) together with its tower code, never by name alone. A tower has at most ONE primary row per month (unique index on TowerId, Year, Month for primary rows), so two
/// employees can never both own a tower in the same month, Draft or Active. Rows of other months are never touched, which is what keeps the monthly history.
/// </summary>
public class CollectionsTowerAssignment
{
    public long CollectionsTowerAssignmentId { get; private set; }
    public int TowerId { get; private set; }

    /// <summary>The tower code (PACT tower number, e.g. 140) at the time of saving. Informational: <see cref="TowerId"/> is the key.</summary>
    public string TowerCode { get; private set; } = string.Empty;
    public string TowerName { get; private set; } = string.Empty;
    public int CompanyId { get; private set; }
    public int Year { get; private set; }
    public int Month { get; private set; }
    public Guid EmployeeId { get; private set; }
    public bool IsPrimary { get; private set; }
    public TowerAssignmentStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid UpdatedByEmployeeId { get; private set; }
    public DateTime? ActivatedAtUtc { get; private set; }
    public Guid? ActivatedByEmployeeId { get; private set; }

    private CollectionsTowerAssignment() { }

    public CollectionsTowerAssignment(
        int towerId, string towerCode, string towerName, int companyId, int year, int month, Guid employeeId,
        TowerAssignmentStatus status, DateTime nowUtc, Guid actorEmployeeId)
    {
        if (towerId <= 0) throw new ArgumentOutOfRangeException(nameof(towerId));
        if (string.IsNullOrWhiteSpace(towerCode)) throw new ArgumentException("TowerCode is required.", nameof(towerCode));
        if (year is < 2000 or > 2200) throw new ArgumentOutOfRangeException(nameof(year));
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (employeeId == Guid.Empty) throw new ArgumentException("EmployeeId is required.", nameof(employeeId));

        TowerId = towerId;
        TowerCode = towerCode.Trim();
        TowerName = (towerName ?? string.Empty).Trim();
        CompanyId = companyId;
        Year = year;
        Month = month;
        EmployeeId = employeeId;
        IsPrimary = true;
        Status = status;
        CreatedAtUtc = UpdatedAtUtc = nowUtc;
        CreatedByEmployeeId = UpdatedByEmployeeId = actorEmployeeId;
        if (status == TowerAssignmentStatus.Active)
        {
            ActivatedAtUtc = nowUtc;
            ActivatedByEmployeeId = actorEmployeeId;
        }
    }

    /// <summary>Hands the tower to another employee for this month. Draft/Active is kept: editing a live month is live, editing a draft stays a draft.</summary>
    public void ReassignTo(Guid employeeId, DateTime nowUtc, Guid actorEmployeeId)
    {
        if (employeeId == Guid.Empty) throw new ArgumentException("EmployeeId is required.", nameof(employeeId));
        EmployeeId = employeeId;
        UpdatedAtUtc = nowUtc;
        UpdatedByEmployeeId = actorEmployeeId;
    }

    /// <summary>Puts a draft in force. Returns false (and changes nothing) when it already is.</summary>
    public bool Activate(DateTime nowUtc, Guid actorEmployeeId)
    {
        if (Status == TowerAssignmentStatus.Active) return false;
        Status = TowerAssignmentStatus.Active;
        ActivatedAtUtc = UpdatedAtUtc = nowUtc;
        ActivatedByEmployeeId = UpdatedByEmployeeId = actorEmployeeId;
        return true;
    }
}

public enum TowerAssignmentAction : byte
{
    Added = 1,
    Removed = 2,
    Reassigned = 3,
    Activated = 4
}

/// <summary>Append-only record of a change to the tower mapping: who held the tower before and after, for which month, who changed it and when. Never updated or deleted.</summary>
public class CollectionsTowerAssignmentHistory
{
    public long CollectionsTowerAssignmentHistoryId { get; private set; }
    public int TowerId { get; private set; }
    public string TowerCode { get; private set; } = string.Empty;
    public string TowerName { get; private set; } = string.Empty;
    public int Year { get; private set; }
    public int Month { get; private set; }
    public Guid? PreviousEmployeeId { get; private set; }
    public Guid? NewEmployeeId { get; private set; }
    public TowerAssignmentAction Action { get; private set; }
    public Guid ChangedByEmployeeId { get; private set; }
    public DateTime ChangedAtUtc { get; private set; }

    private CollectionsTowerAssignmentHistory() { }

    public CollectionsTowerAssignmentHistory(
        CollectionsTowerAssignment tower, Guid? previousEmployeeId, Guid? newEmployeeId, TowerAssignmentAction action, DateTime nowUtc, Guid actorEmployeeId)
    {
        ArgumentNullException.ThrowIfNull(tower);
        TowerId = tower.TowerId;
        TowerCode = tower.TowerCode;
        TowerName = tower.TowerName;
        Year = tower.Year;
        Month = tower.Month;
        PreviousEmployeeId = previousEmployeeId;
        NewEmployeeId = newEmployeeId;
        Action = action;
        ChangedByEmployeeId = actorEmployeeId;
        ChangedAtUtc = nowUtc;
    }
}
