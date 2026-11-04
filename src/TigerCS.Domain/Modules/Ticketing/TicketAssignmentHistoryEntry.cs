namespace TigerCS.Domain.Modules.Ticketing;

public enum TicketAssignmentKind : byte
{
    /// <summary>The system chose the assignee (Collections tower routing).</summary>
    Automatic = 1,

    /// <summary>A person chose the assignee (assign / reassign).</summary>
    Manual = 2
}

/// <summary>
/// Append-only record of one assignment event of a ticket in a department that keeps this richer history (Collections): previous and new assignee, department,
/// the tower and unit the decision was based on (when known), whether it was automatic or manual, who did it and why. <c>TicketAssignments</c> stays the
/// "who owns it now" table; this is the "what happened" log. Never updated or deleted.
/// </summary>
public class TicketAssignmentHistoryEntry
{
    public long TicketAssignmentHistoryEntryId { get; private set; }
    public long TicketId { get; private set; }
    public int DepartmentId { get; private set; }
    public Guid? PreviousEmployeeId { get; private set; }
    public Guid NewEmployeeId { get; private set; }
    public TicketAssignmentKind Kind { get; private set; }
    public int? TowerId { get; private set; }
    public string? TowerCode { get; private set; }
    public string? TowerName { get; private set; }
    public string? UnitNumber { get; private set; }

    /// <summary>Null when the system acted.</summary>
    public Guid? ChangedByEmployeeId { get; private set; }
    public DateTime ChangedAtUtc { get; private set; }
    public string? Reason { get; private set; }

    private TicketAssignmentHistoryEntry() { }

    public TicketAssignmentHistoryEntry(
        long ticketId, int departmentId, Guid? previousEmployeeId, Guid newEmployeeId, TicketAssignmentKind kind, Guid? changedByEmployeeId,
        DateTime changedAtUtc, string? reason, int? towerId = null, string? towerCode = null, string? towerName = null, string? unitNumber = null)
    {
        if (newEmployeeId == Guid.Empty) throw new ArgumentException("NewEmployeeId is required.", nameof(newEmployeeId));
        TicketId = ticketId;
        DepartmentId = departmentId;
        PreviousEmployeeId = previousEmployeeId;
        NewEmployeeId = newEmployeeId;
        Kind = kind;
        ChangedByEmployeeId = changedByEmployeeId;
        ChangedAtUtc = changedAtUtc;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        TowerId = towerId;
        TowerCode = towerCode;
        TowerName = towerName;
        UnitNumber = unitNumber;
    }
}
