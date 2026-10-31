namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// How tickets of one request type are assigned inside their department
/// (Workflow/Automation phase 2). Deliberately an extensible enum, not a
/// hard-coded behavior: future auto-distribution strategies (Round Robin,
/// Least Workload, project-based) are additive values with their own
/// configuration — nothing in the current shape prevents them, and none of
/// them is implemented now.
/// </summary>
public enum AssignmentMode : byte
{
    /// <summary>
    /// The default and the universal fallback: the ticket goes to the
    /// department's queue unassigned, and the department's supervisory level
    /// assigns the specific employee. This is exactly today's behavior for a
    /// ticket with no configured rule — never a random employee.
    /// </summary>
    DepartmentQueue = 1,

    /// <summary>One configured employee always receives tickets of this request type (e.g. NOC for Resale → its responsible officer).</summary>
    SpecificEmployee = 2,

    /// <summary>
    /// A configured group works this request type (e.g. AC Issue → the AC
    /// team). Ownership stays unambiguous: the rule names one primary
    /// assignee — who becomes the ticket's single current owner, preserving
    /// the existing one-owner accountability model — plus the further team
    /// members as configured participants.
    /// </summary>
    Team = 3,

    /// <summary>
    /// Fair automatic distribution: tickets rotate through the department's eligible (active) employees using a persisted
    /// per-department cursor. Needs no configured employee. No eligible employee → department queue, visibly unassigned.
    /// </summary>
    RoundRobin = 4
}
