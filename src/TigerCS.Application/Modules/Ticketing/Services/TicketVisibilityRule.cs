using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The one definition of "may this caller see this ticket at all" —
/// Security-Architecture.md §3's rule that access is "evaluated against the
/// ticket's current data, not cached or assumed from the user's session
/// alone". Three kinds of caller:
/// <list type="bullet">
/// <item>a CS-layer/executive role (<see cref="TicketRoleSets.CrossDepartmentView"/> other than CS Agent: CS Supervisor, CS Manager,
/// General Manager, Chairman/CEO, System Administrator) sees every department;</item>
/// <item>a <b>CS Agent</b> (<see cref="IsRestrictedCsAgent(IReadOnlyCollection{string})"/>) sees only the tickets that are <b>assigned to them</b> right now or on
/// which they <b>actually handled a Genesys interaction</b> (<see cref="ITicketInteractionHandlerRepository"/>) — not every ticket, and
/// not every ticket of their department or Genesys queue;</item>
/// <item>everyone else sees only departments they are actually assigned to.</item>
/// </list>
///
/// <para>
/// The two grants a CS Agent has are independent: reassigning the ticket takes away the previous assignee's assignment grant but never
/// their handling grant, and a ticket handled by several agents (a transfer) is visible to each of them. An unassigned ticket nobody
/// handled is visible to no CS Agent.
/// </para>
///
/// <para>
/// Extracted from <c>TicketQueryAppService</c> (which still owns the read paths and simply delegates here) when the approved Reopen rule
/// added a resource-level check to a mutation. Two call sites evaluating ticket visibility is fine; two call sites each spelling the rule
/// out is how they drift apart, and a visibility rule that drifts is a disclosure bug. The set-based form of the same rule, for list and
/// aggregate queries, is <c>TicketQueryFilters.VisibleTo</c>; the two must be kept in step.
/// </para>
///
/// <para>
/// Routed through <see cref="AuthorizationGate"/>, so the ADR-0024 System Administrator override applies here exactly as it does
/// everywhere else — and, as the gate's async overload guarantees, without a department-membership round trip for an overridden caller.
/// </para>
/// </summary>
public static class TicketVisibilityRule
{
    /// <summary>
    /// True when the caller's ticket visibility is the narrow CS Agent rule: they hold CS Agent and no other role that grants
    /// cross-department view (CS Supervisor, CS Manager, General Manager, Chairman/CEO, System Administrator keep the unchanged,
    /// unrestricted view — a person who is both Agent and Supervisor is a Supervisor for this purpose).
    /// </summary>
    public static bool IsRestrictedCsAgent(IReadOnlyCollection<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);

        return !AuthorizationOverride.AppliesTo(callerRoles)
            && callerRoles.Contains(Roles.CsAgent, StringComparer.Ordinal)
            && !callerRoles.Any(role => !string.Equals(role, Roles.CsAgent, StringComparison.Ordinal)
                && TicketRoleSets.CrossDepartmentView.Contains(role));
    }

    /// <summary>
    /// <see cref="IsRestrictedCsAgent(IReadOnlyCollection{string})"/> for a known caller: a configured <b>service identity</b> (the Genesys
    /// integration account, which Identity models as a "CS Agent" employee) is never restricted. It is not a person who handled anything;
    /// it is confined to the integration surface by <c>ServiceIdentityRestrictionHandler</c> and must keep working across tickets there
    /// (customer lookups, reconciliation), exactly as before.
    /// </summary>
    public static bool IsRestrictedCsAgent(
        IReadOnlyCollection<string> callerRoles, Guid callerEmployeeId, IServiceIdentityRegistry? serviceIdentities) =>
        IsRestrictedCsAgent(callerRoles) && serviceIdentities?.IsServiceIdentity(callerEmployeeId) != true;

    /// <summary>
    /// True for the Genesys integration account (a configured service identity) acting without System Administrator or any other
    /// cross-department role. Its ticket <b>visibility</b> is the Genesys-created tickets only — those it created (<c>CreatedByEmployeeId</c> is
    /// write-once, so a transfer or reassignment never takes the ticket away) — across every department. Owning or handling an interaction on a
    /// manually created ticket does not expose it. The set-based form is <c>TicketQueryFilters.VisibleTo(createdOnly: true)</c>. This is visibility only: it
    /// grants no modification right, and the integration account stays confined to the integration surface by
    /// <c>ServiceIdentityRestrictionHandler</c>.
    /// </summary>
    public static bool IsGenesysIntegration(
        IReadOnlyCollection<string> callerRoles, Guid callerEmployeeId, IServiceIdentityRegistry? serviceIdentities)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);

        return serviceIdentities?.IsServiceIdentity(callerEmployeeId) == true
            && !AuthorizationOverride.AppliesTo(callerRoles)
            && !callerRoles.Any(role => !string.Equals(role, Roles.CsAgent, StringComparison.Ordinal)
                && TicketRoleSets.CrossDepartmentView.Contains(role));
    }

    /// <summary>
    /// A restricted CS Agent who ALSO holds a department role (Department Employee / Head) keeps that role's own department scope —
    /// it is granted by the other role, not by CS Agent. CS Agent alone grants nothing by department.
    /// </summary>
    public static bool HoldsDepartmentRole(IReadOnlyCollection<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        return callerRoles.Contains(Roles.DepartmentEmployee, StringComparer.Ordinal)
            || callerRoles.Contains(Roles.DepartmentHead, StringComparer.Ordinal);
    }

    /// <summary>
    /// For an operation whose role gate has already passed (Close, Classify, Reopen, request a downgrade…): everyone but a restricted CS
    /// Agent passes unchanged — their authority was decided by that role gate — and a restricted CS Agent additionally needs the ticket to
    /// be one they may <b>modify</b> (Full access — a creator's read-only access does not count). This is what stops the CS Agent's cross-department <i>action</i> rights reaching tickets they cannot see.
    /// </summary>
    public static async Task<bool> PassesAgentScopeAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        ITicketInteractionHandlerRepository? handlerRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        IServiceIdentityRegistry? serviceIdentities = null,
        CancellationToken cancellationToken = default) =>
        !IsRestrictedCsAgent(callerRoles, callerEmployeeId, serviceIdentities)
        || await CanModifyTicketAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken);

    /// <summary>Department-level visibility. A restricted CS Agent has none from CS Agent itself — their access is per ticket (<see cref="CanViewTicketAsync"/>), so this fails closed for them.</summary>
    public static Task<bool> CanViewDepartmentAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        int departmentId,
        IServiceIdentityRegistry? serviceIdentities = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userDepartmentAssignmentRepository);

        return AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            IsGenesysIntegration(callerRoles, callerEmployeeId, serviceIdentities)
                ? HoldsDepartmentRole(callerRoles)
                    && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, departmentId, cancellationToken)
                : IsRestrictedCsAgent(callerRoles, callerEmployeeId, serviceIdentities)
                ? HoldsDepartmentRole(callerRoles)
                    && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, departmentId, cancellationToken)
                : callerRoles.Any(TicketRoleSets.CrossDepartmentView.Contains)
                    || await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, departmentId, cancellationToken));
    }

    /// <summary>
    /// How much of a ticket the caller may use. <see cref="TicketAccessLevel.Full"/> is everything the caller's roles allow on it;
    /// <see cref="TicketAccessLevel.ReadOnly"/> is open-and-read only (details, status, owner, department, history, updates) with no
    /// modification of any kind; <see cref="TicketAccessLevel.None"/> is no access.
    ///
    /// <para>
    /// For a restricted CS Agent: Full when the ticket is assigned to them, they handled a Genesys interaction on it, or a department role
    /// of theirs covers its department; otherwise ReadOnly when <b>they created it</b> — which is how a creator keeps following a ticket
    /// after it has moved to another department — otherwise None. Everyone else keeps exactly their department-based answer (Full or None):
    /// roles that can see tickets across departments, and department members, are unchanged.
    /// </para>
    /// </summary>
    public static Task<TicketAccessLevel> GetTicketAccessAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        ITicketInteractionHandlerRepository? handlerRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        IServiceIdentityRegistry? serviceIdentities = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userDepartmentAssignmentRepository);
        ArgumentNullException.ThrowIfNull(ticket);

        if (AuthorizationOverride.AppliesTo(callerRoles))
        {
            return Task.FromResult(TicketAccessLevel.Full);
        }

        return ResolveAsync();

        async Task<TicketAccessLevel> ResolveAsync()
        {
            if (IsGenesysIntegration(callerRoles, callerEmployeeId, serviceIdentities))
            {
                // Genesys-created tickets in any department; nothing else. Owning or handling an interaction on a manually created ticket
                // grants nothing. Full, not ReadOnly, so what the account could already do on its own tickets is unchanged.
                return ticket.CreatedByEmployeeId == callerEmployeeId ? TicketAccessLevel.Full : TicketAccessLevel.None;
            }

            if (!IsRestrictedCsAgent(callerRoles, callerEmployeeId, serviceIdentities))
            {
                // The employee a ticket is currently assigned to can always open and work it (read-only roles never own tickets).
                if (ticket.CurrentOwnerEmployeeId == callerEmployeeId && !Roles.IsReadOnlyCaller(callerRoles))
                {
                    return TicketAccessLevel.Full;
                }

                return await CanViewDepartmentAsync(
                    userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, serviceIdentities, cancellationToken)
                    ? TicketAccessLevel.Full
                    : TicketAccessLevel.None;
            }

            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId
                || (handlerRepository is not null
                    && await handlerRepository.HasHandledAsync(ticket.TicketId, callerEmployeeId, cancellationToken))
                || await CanViewDepartmentAsync(
                    userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, serviceIdentities, cancellationToken))
            {
                return TicketAccessLevel.Full;
            }

            return ticket.CreatedByEmployeeId == callerEmployeeId ? TicketAccessLevel.ReadOnly : TicketAccessLevel.None;
        }
    }

    /// <summary>May the caller open and read the ticket — Full or ReadOnly access. Reads (details, history, notes list, SLA, interactions…) use this.</summary>
    public static async Task<bool> CanViewTicketAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        ITicketInteractionHandlerRepository? handlerRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        IServiceIdentityRegistry? serviceIdentities = null,
        CancellationToken cancellationToken = default) =>
        await GetTicketAccessAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken)
        != TicketAccessLevel.None;

    /// <summary>
    /// May the caller change the ticket (add a note, close, reopen, classify, claim, assign…): only Full access. A creator who is outside the
    /// ticket's department and holds no other grant is ReadOnly and is refused here — the server-side half of "view only", beneath any hidden
    /// button.
    /// </summary>
    public static async Task<bool> CanModifyTicketAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        ITicketInteractionHandlerRepository? handlerRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        IServiceIdentityRegistry? serviceIdentities = null,
        CancellationToken cancellationToken = default) =>
        await GetTicketAccessAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken)
        == TicketAccessLevel.Full;
}

/// <summary>What a caller may do with one ticket. See <see cref="TicketVisibilityRule.GetTicketAccessAsync"/>.</summary>
public enum TicketAccessLevel
{
    /// <summary>No access: the ticket is not visible to the caller.</summary>
    None,

    /// <summary>The ticket creator outside the ticket's department with no other grant: may open and read it, may change nothing.</summary>
    ReadOnly,

    /// <summary>Everything the caller's roles allow on the ticket.</summary>
    Full
}
