namespace TigerCS.Domain.Modules.Ticketing;

public enum TicketAssignmentKind : byte
{
    /// <summary>The system chose the assignee (Collections tower routing).</summary>
    Automatic = 1,

    /// <summary>A person chose the assignee (assign / reassign).</summary>
    Manual = 2
}

/// <summary>Where a history entry came from, when it is a Genesys-driven assignment. Null on entries written by Collections routing and by people.</summary>
public enum TicketAssignmentSource : byte
{
    /// <summary>The AI Agent (the Genesys integration service account) handled the conversation first. History only: the ticket keeps no owner.</summary>
    GenesysAiAgent = 1,

    /// <summary>A human agent answered a call directly, with no previous assignee.</summary>
    GenesysDirectCall = 2,

    /// <summary>Genesys connected a different human agent: a call transfer (AI to human, or human to human).</summary>
    GenesysCallTransfer = 3,

    /// <summary>
    /// The system - not Genesys, not a person - gave a ticket that had been handled by the AI Agent to a person for human follow-up (the AI conversation ended
    /// unresolved, human follow-up was flagged, or the request type was classified onto a department rotation). A <b>provisional</b> assignment: it says nothing
    /// about the person having accepted or answered anything.
    /// </summary>
    GenesysHumanFollowUp = 4,

    /// <summary>A person (a supervisor, the department, the owner themselves) assigned or reassigned a ticket the Genesys integration had been handling.</summary>
    GenesysManualAssignment = 5,

    /// <summary>The system moved an AI-handled ticket into the Call Center queue for human follow-up. Ownership is unchanged (the AI Agent keeps it): only the department moved.</summary>
    GenesysQueueRouting = 6,

    /// <summary>A department transfer of a Genesys-handled ticket (by a person, or a workflow), with or without a new assignee.</summary>
    GenesysDepartmentTransfer = 7
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

    /// <summary>The department the ticket was in before this entry, when the entry is (or includes) a department change; null otherwise and on rows written before the column existed.</summary>
    public int? PreviousDepartmentId { get; private set; }
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

    /// <summary>Set only for Genesys-driven entries (see <see cref="TicketAssignmentSource"/>).</summary>
    public TicketAssignmentSource? Source { get; private set; }

    /// <summary>When the Genesys event happened as Genesys reports it (connected time); used to ignore older, out-of-order events. Not the arrival time, which is <see cref="ChangedAtUtc"/>.</summary>
    public DateTime? SourceEventAtUtc { get; private set; }

    public string? GenesysConversationId { get; private set; }

    private TicketAssignmentHistoryEntry() { }

    /// <summary>A Genesys-driven entry. <paramref name="previousEmployeeId"/> is the AI Agent account for an AI-to-human transfer.</summary>
    public static TicketAssignmentHistoryEntry ForGenesys(
        long ticketId, int departmentId, Guid? previousEmployeeId, Guid newEmployeeId, TicketAssignmentSource source, Guid? changedByEmployeeId,
        DateTime changedAtUtc, DateTime? sourceEventAtUtc, string? genesysConversationId, string? reason,
        TicketAssignmentKind kind = TicketAssignmentKind.Automatic, int? previousDepartmentId = null)
    {
        var entry = new TicketAssignmentHistoryEntry(
            ticketId, departmentId, previousEmployeeId, newEmployeeId, kind, changedByEmployeeId, changedAtUtc, reason)
        {
            Source = source,
            PreviousDepartmentId = previousDepartmentId,
            SourceEventAtUtc = sourceEventAtUtc,
            GenesysConversationId = string.IsNullOrWhiteSpace(genesysConversationId) ? null : genesysConversationId.Trim()
        };
        return entry;
    }

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
