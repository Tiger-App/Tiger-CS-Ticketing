using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Web.Models;

/// <summary>
/// The viewer-and-ticket facts the display-side checks in
/// <see cref="TicketActions"/> evaluate, in the same shape the Api's own
/// application services consume them: the caller's roles and employee id,
/// the ticket's current owner and current department, and the caller's
/// department memberships.
///
/// <para>
/// <b><see cref="ViewerDepartmentIds"/> is server-supplied, not inferred.</b>
/// It is the <c>Departments</c> collection of
/// <c>GET /api/users/me</c>, which <c>UserProfileAppService</c> builds from
/// <c>IUserDepartmentAssignmentRepository.GetByEmployeeIdAsync</c> — the very
/// rows the Api's own rules test with
/// <c>IUserDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId,
/// ticket.CurrentDepartmentId)</c>. The UI therefore asks the same question of
/// the same data, rather than approximating membership from the
/// primary-department claim (which would hide the control from a legitimately
/// authorized member of a second department).
/// </para>
///
/// <para>
/// When that call fails the page leaves this empty, so every
/// department-scoped check below answers <c>false</c> — the control is hidden
/// rather than offered on a guess.
/// </para>
/// </summary>
/// <param name="ViewerRoles">The viewer's roles, from their own validated claims.</param>
/// <param name="ViewerEmployeeId">The viewer, from the NameIdentifier claim — the same id the Api reads as callerEmployeeId.</param>
/// <param name="CurrentOwnerEmployeeId">The ticket's current owner, or null when unassigned.</param>
/// <param name="CurrentDepartmentId">The department currently holding the ticket.</param>
/// <param name="ViewerActsAsInteractionHandler">Server-computed (TicketDetailDto): a CS Agent Genesys reported as handling this ticket's interaction, in its department, with nobody else owning it — the backend then lets them Resolve, Close and Transfer.</param>
/// <param name="ViewerDepartmentIds">Every department the viewer is assigned to, per GET /api/users/me.</param>
public sealed record TicketActionContext(
    IReadOnlyCollection<string> ViewerRoles,
    Guid ViewerEmployeeId,
    Guid? CurrentOwnerEmployeeId,
    int CurrentDepartmentId,
    IReadOnlyCollection<int> ViewerDepartmentIds,
    IReadOnlySet<string>? ViewerGrantedPermissions = null,
    IReadOnlySet<string>? ViewerDeniedPermissions = null,
    bool ViewerActsAsInteractionHandler = false)
{
    /// <summary>The viewer's explicit denial of <paramref name="permission"/> (employee override, from GET /api/users/me): the Api refuses it whatever the role, ownership or System Administrator override says, so no control for it is ever offered.</summary>
    public bool IsDenied(string permission) => ViewerDeniedPermissions?.Contains(permission) == true;

    /// <summary>The viewer's explicit grant of <paramref name="permission"/>. A grant stands in for the role requirement only; ownership, department scope, visibility and status rules still apply.</summary>
    public bool IsGranted(string permission) => ViewerGrantedPermissions?.Contains(permission) == true && !IsReadOnlyViewer;

    /// <summary>ADR-0024's override, read from its single central definition rather than naming the role here.</summary>
    public bool OverrideApplies => AuthorizationOverride.AppliesTo(ViewerRoles);

    /// <summary>The Api's <c>ticket.CurrentOwnerEmployeeId == callerEmployeeId</c>.</summary>
    public bool IsCurrentOwner => CurrentOwnerEmployeeId is { } owner && owner == ViewerEmployeeId;

    /// <summary>True for roles that see tickets in every department (CS Supervisor/Manager, General Manager, Chairman/CEO, System Administrator) - a grant used by them is already within their ticket visibility. CS Agent is excluded: its visibility is per ticket.</summary>
    public bool SeesTicketsAcrossDepartments =>
        ViewerRoles.Any(r => r != Roles.CsAgent && TicketRoleSets.CrossDepartmentView.Contains(r));

    /// <summary>Whether a granted permission may be exercised on this ticket: the ticket is in the viewer's department or in a department the viewer's role sees across.</summary>
    public bool TicketWithinGrantScope => BelongsToCurrentDepartment || SeesTicketsAcrossDepartments;

    /// <summary>The Api's <c>userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId)</c>.</summary>
    public bool BelongsToCurrentDepartment => ViewerDepartmentIds.Contains(CurrentDepartmentId);

    /// <summary>True when the viewer holds a department-side role, which the Api scopes to the ticket's own department.</summary>
    public bool IsDepartmentSide => HasRole(Roles.DepartmentEmployee) || HasRole(Roles.DepartmentHead);

    /// <summary>True for a viewer holding only read-only roles (Chairman/CEO, Reporting User): no write control is ever rendered for them, ownership or not.</summary>
    public bool IsReadOnlyViewer => Roles.IsReadOnlyCaller(ViewerRoles);

    public bool HasRole(string role) => ViewerRoles.Contains(role);

    public bool HasAnyRole(IReadOnlyCollection<string> roleSet) => ViewerRoles.Any(roleSet.Contains);
}

/// <summary>
/// Display-side action affordance checks. These decide only whether a
/// control is worth rendering — the Api's own authorization
/// (TicketRoleSets/SlaRoleSets + the System Administrator override) remains
/// the enforcement point, and a control shown here can still come back 403.
///
/// <para>
/// <b>Every check below reads the Api's own canonical role set and mirrors
/// the same service's resource-scoped half</b> (current ownership, and
/// membership of the ticket's current department). There is deliberately no
/// second role list and no second rule here: the control and the endpoint
/// decide from the same sets and the same facts, so they cannot drift. A
/// role-only display check is what let the Resolve control render for the
/// whole CS layer while the endpoint refused it.
/// </para>
///
/// <para>
/// These remain <i>display</i> checks. They are evaluated against a snapshot
/// of the ticket taken when the page was rendered, so a control can still be
/// refused by a ticket that moved in between; and they deliberately do not
/// reproduce anything that is not a permission — lifecycle eligibility,
/// closed-ticket immutability, department workflow settings and concurrency
/// stay server-side and unduplicated.
/// </para>
/// </summary>
public static class TicketActions
{
    /// <summary>
    /// Reads the Api's own <see cref="TicketRoleSets.Reopen"/> — the CS layer
    /// (CS Agent, CS Supervisor, CS Manager) under the final approved rule —
    /// rather than a local copy, plus the System
    /// Administrator override (ADR-0024) the Api honors through its
    /// AuthorizationGate. There is deliberately no second role list here: the
    /// control and the endpoint decide from the same set, so they cannot
    /// drift. Lifecycle eligibility (Closed, closed as Resolved,
    /// inside the reopen window) is the server-computed IsReopenEligible flag,
    /// never re-derived here.
    ///
    /// <para>
    /// Display only, and deliberately <i>narrower</i> than the server's rule:
    /// the endpoint additionally requires the caller to have access to the
    /// ticket, which this cannot see. A control shown here can still come back
    /// 403, which is the correct direction for a UI check to be wrong in.
    /// </para>
    /// </summary>
    public static bool CanReopen(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null
        && (viewerRoles.Any(TicketRoleSets.Reopen.Contains) || AuthorizationOverride.AppliesTo(viewerRoles));

    /// <summary>Whether the supervisor escalation-review filter is worth offering — the Api's own <see cref="TicketRoleSets.ReviewEscalations"/> plus ADR-0024's override.</summary>
    public static bool CanReviewEscalations(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null
        && (viewerRoles.Any(TicketRoleSets.ReviewEscalations.Contains) || AuthorizationOverride.AppliesTo(viewerRoles));

    /// <summary>
    /// Whether a Transfer control — and the department picker inside it — is worth rendering. Mirrors
    /// <c>TicketAssignmentAppService.TransferAsync</c> exactly: ADR-0024's override or <see cref="TicketRoleSets.TransferAnyTicket"/>
    /// (CS Manager) for any ticket; otherwise a <see cref="TicketRoleSets.Transfer"/> role (CS Agent/Supervisor) on a ticket the viewer
    /// currently owns, and a CS Supervisor also on a ticket of a department they belong to. Read-only viewers never qualify.
    /// </summary>
    public static bool CanTransfer(TicketActionContext? context)
    {
        if (context is not { } c)
        {
            return false;
        }

        // An explicit denial of Transfer Any Ticket takes the CS Manager's built-in any-ticket reach away (the per-ticket rule below still applies).
        if (c.OverrideApplies || (c.HasAnyRole(TicketRoleSets.TransferAnyTicket) && !c.IsDenied(Permissions.TicketsTransferAny)))
        {
            return true;
        }

        if (c.IsReadOnlyViewer || !c.HasAnyRole(TicketRoleSets.Transfer))
        {
            return false;
        }

        return c.IsCurrentOwner
            || c.ViewerActsAsInteractionHandler
            || (c.HasRole(Roles.CsSupervisor) && c.BelongsToCurrentDepartment);
    }

    /// <summary>
    /// Transfer including the viewer's own overrides: never when Tickets.Transfer is denied; otherwise the role rule above, or an
    /// explicit grant for a viewer who belongs to the ticket's current department (a grant never widens ticket visibility).
    /// </summary>
    public static bool CanTransferTicket(TicketActionContext? context) =>
        context is { } c
        && !c.IsDenied(Permissions.TicketsTransfer)
        && (CanTransfer(c) || HoldsTransferAnyGrant(c) || (c.IsGranted(Permissions.TicketsTransfer) && c.TicketWithinGrantScope));

    /// <summary>
    /// An explicit Transfer Any Ticket grant held by a Call Center employee (CS Agent / Supervisor / Manager): any ticket, any active department,
    /// whatever the viewer's departments. Mirrors <c>TransferAnyTicketPolicy.IsGrantedAsync</c>; read-only viewers never qualify.
    /// </summary>
    public static bool HoldsTransferAnyGrant(TicketActionContext? context) =>
        context is { } c && c.IsGranted(Permissions.TicketsTransferAny) && c.HasAnyRole(TicketRoleSets.Transfer);

    /// <summary>Reopen including the viewer's own overrides (a denial hides it; Reopen has no grant path in the services).</summary>
    public static bool CanReopenTicket(TicketActionContext? context) =>
        context is { } c && !c.IsDenied(Permissions.TicketsReopen) && CanReopen(c.ViewerRoles);

    /// <summary>
    /// Whether a Close control is worth rendering. Reads the Api's own
    /// <see cref="TicketRoleSets.Close"/> (CS Agent/CS Supervisor/CS Manager —
    /// the other half of ISSUE-022's Resolve/Close split) plus ADR-0024's
    /// override.
    ///
    /// <para>
    /// Role-only, because <c>TicketLifecycleAppService.CloseAsync</c>'s own
    /// permission rule is role-only. Whether the ticket is Resolved yet is
    /// lifecycle, not permission, and stays the view's existing separate
    /// condition.
    /// </para>
    /// </summary>
    public static bool CanClose(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null
        && (viewerRoles.Any(TicketRoleSets.Close.Contains) || AuthorizationOverride.AppliesTo(viewerRoles));

    /// <summary>
    /// Mirrors <c>TicketLifecycleAppService.CloseAsync</c>'s permission rule: ADR-0024's override, or the ticket's current assignee
    /// whatever their role or department (read-only roles excepted), or a <see cref="TicketRoleSets.Close"/> role. A CS Agent who is not
    /// the assignee additionally needs ticket access the UI cannot see (assigned/handled/department grant), so the control is hidden for
    /// them rather than offered on a guess — the direction a display check should err.
    /// </summary>
    public static bool CanCloseTicket(TicketActionContext? context) =>
        context is { } c
        && !c.IsDenied(Permissions.TicketsClose)
        && (c.OverrideApplies
            || (c.IsCurrentOwner && !c.IsReadOnlyViewer)
            || c.ViewerActsAsInteractionHandler
            || (c.HasAnyRole(TicketRoleSets.Close) && !TicketVisibilityRule.IsRestrictedCsAgent(c.ViewerRoles))
            // A grant replaces the CS-layer role only; the viewer must still belong to the ticket's department.
            || (c.IsGranted(Permissions.TicketsClose) && c.TicketWithinGrantScope));

    /// <summary>
    /// Mirrors <c>TicketLifecycleAppService.IsResolveAuthorizedAsync</c>:
    /// <see cref="TicketRoleSets.Resolve"/> (Department Employee/Department
    /// Head — never the CS layer), and then the resource-scoped half — a
    /// Department Head must belong to the ticket's current department, anyone
    /// else in the set must be its current owner. ADR-0024's override first,
    /// exactly as the AuthorizationGate applies it.
    ///
    /// <para>
    /// Deliberately NOT role-only. CS Agent, CS Supervisor and CS Manager hold
    /// Close and Reopen, not Resolve, so a role-only check here would render
    /// the control for the entire CS layer and every one of them would be
    /// refused by the endpoint.
    /// </para>
    /// </summary>
    public static bool CanResolve(TicketActionContext? context)
    {
        if (context is not { } c)
        {
            return false;
        }

        if (c.IsDenied(Permissions.TicketsResolve))
        {
            return false;
        }

        if (c.OverrideApplies)
        {
            return true;
        }

        if ((c.IsCurrentOwner && !c.IsReadOnlyViewer) || c.ViewerActsAsInteractionHandler)
        {
            return true;
        }

        var hasRole = c.HasAnyRole(TicketRoleSets.Resolve);
        if (!hasRole && !c.IsGranted(Permissions.TicketsResolve))
        {
            return false;
        }

        // Role or grant satisfies the permission; scope still applies (a grant alone is scoped like a Department Head).
        return c.HasRole(Roles.DepartmentHead) || !hasRole ? c.BelongsToCurrentDepartment : c.IsCurrentOwner;
    }

    /// <summary>
    /// Mirrors <c>TicketAssignmentAppService.AssignAsync</c>'s permission rule:
    /// <see cref="TicketRoleSets.AssignCrossDepartment"/> (CS Manager) outright,
    /// or <see cref="TicketRoleSets.AssignWithinOwnDepartment"/> (CS Supervisor,
    /// Department Head) when the viewer belongs to the ticket's current
    /// department. CS Agent and Department Employee hold no assignment
    /// capability at all, not even self-claim.
    ///
    /// <para>
    /// The department's own workflow settings (<c>AllowAssignment</c>,
    /// <c>AllowInternalReassignment</c>) can narrow this further server-side.
    /// That is configuration rather than permission and is not mirrored here,
    /// so an assignment-disabled department still shows the control and the
    /// Api still refuses it — the acceptable direction.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether the viewer may place a ticket with someone OUTSIDE its current
    /// department — the Api's <see cref="TicketRoleSets.AssignCrossDepartment"/>
    /// (CS Manager) plus ADR-0024's override. Such a viewer is offered the
    /// whole assignee directory and the transfer-and-assign path; everyone
    /// else sees only the ticket's own department.
    /// </summary>
    public static bool CanAssignAcrossDepartments(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null
        && (viewerRoles.Any(TicketRoleSets.AssignCrossDepartment.Contains) || AuthorizationOverride.AppliesTo(viewerRoles));

    /// <summary>
    /// Mirrors <c>TicketNoteAppService.AddNoteAsync</c>: anyone who can view the
    /// ticket may add an internal note except a read-only viewer (Chairman/CEO,
    /// Reporting User).
    /// </summary>
    public static bool CanAddNote(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null && !Roles.IsReadOnlyCaller(viewerRoles);

    public static bool CanAssign(TicketActionContext? context) =>
        context is { } c
        && !c.IsDenied(Permissions.TicketsAssign)
        && (c.OverrideApplies
            || c.HasAnyRole(TicketRoleSets.AssignCrossDepartment)
            || (c.HasAnyRole(TicketRoleSets.AssignWithinOwnDepartment) && c.BelongsToCurrentDepartment));

    /// <summary>
    /// Mirrors <c>TicketLifecycleAppService.IsCurrentOwnerOrDepartmentAuthorityAsync</c>,
    /// the rule behind ChangeStatus: the ticket's current owner, or
    /// <see cref="TicketRoleSets.CrossDepartmentSupervisory"/>, or a Department
    /// Head who belongs to the ticket's current department.
    ///
    /// <para>
    /// Note the shape differs from Resolve's: ownership alone authorizes here
    /// regardless of role, which is why this cannot reuse a single generic
    /// helper without misstating one of the two rules.
    /// </para>
    /// </summary>
    public static bool CanChangeStatus(TicketActionContext? context) =>
        context is { } c
        && !c.IsDenied(Permissions.TicketsEdit)
        && (c.OverrideApplies
            || (c.IsCurrentOwner && !c.IsReadOnlyViewer)
            || c.HasAnyRole(TicketRoleSets.CrossDepartmentSupervisory)
            || (c.HasRole(Roles.DepartmentHead) && c.BelongsToCurrentDepartment));

    /// <summary>
    /// Mirrors <c>SlaFirstResponseAppService.IsAuthorizedAsync</c> (the "Record first response" action): the ticket's current owner, a
    /// supervisory role (<see cref="SlaRoleSets.RecordFirstResponse"/>), or a Department Head of the ticket's own department - and never
    /// a read-only viewer or someone denied <c>Tickets.Escalate</c> (the endpoint's permission). The page previously offered it to anyone
    /// who could resolve or escalate, which includes non-owner agents the Api answers with 403.
    /// </summary>
    public static bool CanRecordFirstResponse(TicketActionContext? context)
    {
        if (context is not { } c || c.IsReadOnlyViewer || c.IsDenied(Permissions.TicketsEscalate))
        {
            return false;
        }

        return c.OverrideApplies
            || c.IsCurrentOwner
            || c.HasAnyRole(SlaRoleSets.RecordFirstResponse)
            || (c.HasRole(Roles.DepartmentHead) && c.BelongsToCurrentDepartment);
    }

    /// <summary>
    /// Whether an Escalate control is worth rendering at all — true when the
    /// viewer could raise EITHER tier, since the control carries both.
    /// </summary>
    public static bool CanEscalate(TicketActionContext? context) =>
        CanRaiseManualFlag(context) || CanEscalateToLevel4(context);

    /// <summary>
    /// Mirrors the <c>ManualFlag</c> tier of
    /// <c>TicketEscalationAppService.IsAuthorizedAsync</c> (levels 1–3):
    /// <see cref="SlaRoleSets.ManualEscalate"/>, then the ticket's current
    /// owner outright, and otherwise the ticket's own department for the
    /// department-side roles — the CS layer being inherently
    /// cross-department.
    /// </summary>
    public static bool CanRaiseManualFlag(TicketActionContext? context)
    {
        if (context is not { } c)
        {
            return false;
        }

        if (c.OverrideApplies)
        {
            return true;
        }

        if (!c.HasAnyRole(SlaRoleSets.ManualEscalate))
        {
            return false;
        }

        if (c.IsReadOnlyViewer)
        {
            return false;
        }

        return c.IsCurrentOwner || !c.IsDepartmentSide || c.BelongsToCurrentDepartment;
    }

    /// <summary>
    /// Mirrors the <c>ManualLevel4</c> tier of
    /// <c>TicketEscalationAppService.IsAuthorizedAsync</c>:
    /// <see cref="SlaRoleSets.ManualLevel4Escalate"/> (CS Manager or General
    /// Manager) and nothing else — MVP-ERD.md §2.17 names those two roles and
    /// no other basis, so ownership and department membership deliberately do
    /// not widen it.
    /// </summary>
    public static bool CanEscalateToLevel4(TicketActionContext? context) =>
        context is { } c && (c.OverrideApplies || c.HasAnyRole(SlaRoleSets.ManualLevel4Escalate));

    /// <summary>
    /// Mirrors <c>PriorityDowngradeAppService.RequestAsync</c>'s authorization:
    /// the ticket's owner, a cross-department requester role, or a
    /// department-side role scoped to the ticket's own department (or the
    /// System Administrator override).
    /// </summary>
    public static bool CanRequestPriorityDowngrade(TicketActionContext? context) =>
        context is { } c
        && !c.IsReadOnlyViewer   // every other write control is hidden from read-only roles, ownership or not; this one was not
        && (c.OverrideApplies
            || c.IsCurrentOwner
            || c.HasAnyRole(PriorityDowngradeAppService.CrossDepartmentRequesterRoles)
            || (c.HasAnyRole(PriorityDowngradeAppService.DepartmentRequesterRoles) && c.BelongsToCurrentDepartment));

    /// <summary>
    /// Mirrors the decision rule of <c>PriorityDowngradeAppService</c>: a
    /// Department Head of the ticket's current department, a CS Manager /
    /// General Manager, or the System Administrator override. The requester
    /// is excluded by the caller, which knows who requested.
    /// </summary>
    public static bool CanDecidePriorityDowngrade(TicketActionContext? context) =>
        context is { } c
        && (c.OverrideApplies
            || c.HasAnyRole(PriorityDowngradeAppService.CrossDepartmentDeciderRoles)
            || (c.HasRole(Roles.DepartmentHead) && c.BelongsToCurrentDepartment));
}
