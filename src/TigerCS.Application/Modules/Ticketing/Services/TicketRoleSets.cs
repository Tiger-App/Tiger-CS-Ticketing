using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Per-action role sets for ticket-operations authorization
/// (Solution-Analysis.md §4.1's permission matrix, ISSUE-022's approved
/// Resolve/Close split, Security-Architecture.md §3's CS-layer
/// cross-department scoping). Each action gets its own set rather than
/// reusing one blanket list — <c>DepartmentScopedRequirement</c>
/// was already built constructor-parameterized for exactly this reason, per
/// its own remarks ("flagged for confirmation before a real ticket endpoint
/// consumes this policy") — this is that endpoint, and this is the
/// resolution.
///
/// <para>
/// <b>System Administrator is deliberately absent from the operational sets
/// below, and is authorized anyway.</b> Every consumer of this class routes
/// its decision through <c>AuthorizationGate</c>, which applies the
/// <c>AuthorizationOverride</c> (ADR-0024 — confirmed management decision)
/// before the set is consulted. Adding the role to these lists instead would
/// be the per-call-site duplication that correction's requirement 4
/// explicitly rules out, and would lose the distinction these sets exist to
/// record: what each role is granted <i>by the permission matrix</i>, as
/// against what the override grants on top of it.
/// </para>
/// </summary>
public static class TicketRoleSets
{
    /// <summary>
    /// View (queue/detail): cross-department for CS-layer roles
    /// (Security-Architecture.md §3, already the exact role list
    /// PolicyNames.DepartmentScoped was defined with) plus GM/Chairman/
    /// SysAdmin, who hold "View: All tickets"/"All (technical)" in
    /// Solution-Analysis.md §4.1. Department Employee/Head are scoped to
    /// their own department membership(s), not included here.
    /// </summary>
    public static readonly IReadOnlyCollection<string> CrossDepartmentView =
    [
        Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager,
        Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator
    ];

    /// <summary>
    /// Status change ("work" sub-machine — Open→InProgress, InProgress↔Pending*):
    /// cross-department authority for Supervisor+, on top of the ticket's
    /// current owner (checked separately in TicketLifecycleAppService).
    /// Chairman/CEO is deliberately absent: the role is read-only by agreed
    /// management decision (audit F-5) and holds no status-change,
    /// classification, approval-operation or first-response authority.
    /// Department Head is department-scoped instead (checked separately,
    /// against the specific department), not cross-department. Unaffected
    /// by the Assign/Transfer correction below — status-change authority was
    /// not part of that correction.
    /// </summary>
    public static readonly IReadOnlyCollection<string> CrossDepartmentSupervisory =
    [
        Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.SystemAdministrator
    ];

    /// <summary>
    /// Assign/reassign, department-scoped (PR correction — explicit task
    /// instruction, superseding this file's original Assign design):
    /// CS Supervisor and Department Head may assign/reassign only within a
    /// department they themselves belong to (checked separately against the
    /// ticket's current department). CS Agent and Department Employee hold
    /// no assignment capability at all — not even self-claim.
    /// </summary>
    public static readonly IReadOnlyCollection<string> AssignWithinOwnDepartment = [Roles.CsSupervisor, Roles.DepartmentHead];

    /// <summary>
    /// Review of tickets escalated because the handling agent could not
    /// resolve them (the <c>escalated</c> queue filter). Supervisory roles
    /// only; it narrows the list and never widens visibility — the caller's
    /// department scope still applies, so a Department Head reviews only
    /// their own department's escalations. System Administrator is
    /// authorized by the ADR-0024 override, not listed here.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ReviewEscalations =
        [Roles.CsSupervisor, Roles.CsManager, Roles.DepartmentHead, Roles.GeneralManager, Roles.ChairmanCeo];

    /// <summary>
    /// Assign/reassign, cross-department (PR correction): CS Manager only.
    /// General Manager, Chairman/CEO, and System Administrator explicitly
    /// hold no operational assignment capability, despite being
    /// cross-department for View/status-change elsewhere in this class.
    /// </summary>
    public static readonly IReadOnlyCollection<string> AssignCrossDepartment = [Roles.CsManager];

    /// <summary>
    /// Transfer to another department: the Call Center layer (CS Agent, CS Supervisor, CS Manager). Role membership is only the first
    /// half — <see cref="TicketAssignmentAppService"/> additionally scopes it: a CS Manager may transfer any ticket
    /// (<see cref="TransferAnyTicket"/>); a CS Agent or CS Supervisor only a ticket they currently own, and a CS Supervisor also a ticket
    /// of a department they belong to. Department Head and Department Employee remain refused.
    /// </summary>
    public static readonly IReadOnlyCollection<string> Transfer = [Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager];

    /// <summary>Transfer without a per-ticket scope: CS Manager only (and the ADR-0024 override). No other CS role gets cross-department transfer reach.</summary>
    public static readonly IReadOnlyCollection<string> TransferAnyTicket = [Roles.CsManager];

    /// <summary>
    /// Roles that count as operational handlers for automatic Round Robin: they actually work tickets. Supervisors, managers, read-only
    /// roles and administrators oversee or only read, so they are never handed work by rotation.
    /// </summary>
    public static readonly IReadOnlyCollection<string> RoundRobinEligible = [Roles.CsAgent, Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>Business decision: automatic assignment in the Call Center (department code CC) - Round Robin AND the confirmed Genesys answering agent - goes to CS Agents only. Does not affect any other department.</summary>
    public static readonly IReadOnlyCollection<string> CallCenterAutoAssignable = [Roles.CsAgent];

    /// <summary>Resolve by role: Department Employee/Head (a Head must belong to the ticket's department). Independently of this set, whoever the ticket is currently assigned to may Resolve and Close it, whatever their role or department (<c>TicketLifecycleAppService</c>); the CS layer has no role-based Resolve beyond that.</summary>
    public static readonly IReadOnlyCollection<string> Resolve = [Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>Close: CS Agent/CS Supervisor/CS Manager only (ISSUE-022, Management-Decisions.md — task's explicit instruction). Cross-department — CS is inherently cross-department (Security-Architecture.md §3).</summary>
    public static readonly IReadOnlyCollection<string> Close = [Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager];

    /// <summary>
    /// Reopen: <b>the CS layer — CS Agent, CS Supervisor, CS Manager</b>,
    /// per the final approved rule. This restores Supervisor and CS Manager,
    /// who were briefly excluded while the rule read "the Agent reopens";
    /// management has confirmed the reopening authority is the CS layer as a
    /// whole. It is still deliberately <i>not</i> an alias of
    /// <see cref="Close"/> — the two sets happen to coincide today, and the
    /// separation is what lets either move without dragging the other with
    /// it (ISSUE-022's Resolve/Close split is the standing example).
    ///
    /// <para>
    /// <b>System Administrator is absent here and authorized anyway</b>,
    /// through ADR-0024's central override, exactly as this class's own
    /// summary describes. It is not listed locally: doing so would be the
    /// per-call-site duplication that ADR-0024 rules out, and would lose the
    /// distinction between what the permission matrix grants and what the
    /// override grants on top of it.
    /// </para>
    ///
    /// <para>
    /// <b>Every other role is refused</b> — Department Employee, Department
    /// Head, General Manager, Chairman/CEO and Reporting User hold no direct
    /// Reopen, neither the action nor the endpoint. They have no direct reopen
    /// capability; the Reopen Approval path (ApprovalType.ReopenApproval, which
    /// applies to Resolved tickets only, never to an inactivity closure) is the
    /// route for a reopen request that CS then decides.
    /// </para>
    ///
    /// <para>
    /// <b>Role membership alone is not authorization for Reopen.</b> Unlike
    /// the other sets here, <c>TicketLifecycleAppService.ReopenAsync</c>
    /// additionally requires the caller to have access to the ticket itself
    /// under the existing visibility rules (<see cref="CrossDepartmentView"/>
    /// or department membership, via <c>TicketQueryAppService</c>'s own
    /// check) — the resource-level half of the decision, so a CS user can only
    /// reopen a ticket they were entitled to see in the first place.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> Reopen = [Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager];
}
