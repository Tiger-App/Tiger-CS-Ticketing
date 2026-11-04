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
/// <item>a <b>CS Agent</b> (<see cref="IsRestrictedCsAgent(IReadOnlyCollection{string})"/>) sees only the tickets <b>currently assigned to them</b>, plus
/// the tickets a <b>department employee</b> is working for which they are the <b>responsible CS Agent</b> (the CS Agent who last owned the
/// ticket, see <see cref="CanFollowDepartmentWorkAsync"/>) so they can follow up and close them. Never a ticket assigned to another CS Agent,
/// never an unassigned one (except the explicitly authorized Take Ownership pool, <see cref="TicketClaimPolicy"/>), and never because they once
/// created or handled it;</item>
/// <item>a <b>Department Employee</b> (<see cref="IsAssignmentOnlyEmployee"/>) sees only the tickets currently assigned to them, in any department;</item>
/// <item>everyone else (Department Head, Reporting User…) sees only departments they are actually assigned to.</item>
/// </list>
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
    /// A restricted CS Agent who ALSO holds the <b>Department Head</b> role keeps that role's own department scope — it is granted by the other
    /// role, not by CS Agent. CS Agent alone grants nothing by department, and neither does <b>Department Employee</b>: an employee sees
    /// only tickets assigned to them (<see cref="IsAssignmentOnlyEmployee"/>).
    /// </summary>
    public static bool HoldsDepartmentRole(IReadOnlyCollection<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        return callerRoles.Contains(Roles.DepartmentHead, StringComparer.Ordinal);
    }

    /// <summary>
    /// True for a regular employee whose ticket visibility is <b>assignment-only</b>: they hold Department Employee and no other role at all.
    /// They see exactly the tickets currently assigned to them, whatever department those sit in, and never a ticket merely because it is in a
    /// department they belong to or is assigned to a colleague. Anyone holding any other role (Department Head, a CS-layer or executive role,
    /// Reporting User, a read-only role…) keeps that role's existing scope unchanged. The set-based form is <c>TicketQueryFilters.VisibleTo</c>
    /// called with an empty department set and the caller as owner.
    /// </summary>
    public static bool IsAssignmentOnlyEmployee(IReadOnlyCollection<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        return callerRoles.Contains(Roles.DepartmentEmployee, StringComparer.Ordinal)
            && callerRoles.All(role => string.Equals(role, Roles.DepartmentEmployee, StringComparison.Ordinal));
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

    /// <summary>Department-level visibility. A restricted CS Agent has none from CS Agent itself, and an assignment-only employee has none at all — their access is per ticket (<see cref="CanViewTicketAsync"/>), so this fails closed for them.</summary>
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
            // An assignment-only employee has no department-level view at all: their access is per ticket (the one assigned to them).
            IsAssignmentOnlyEmployee(callerRoles)
                ? false
                : IsGenesysIntegration(callerRoles, callerEmployeeId, serviceIdentities)
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
    /// For a restricted CS Agent: Full when the ticket is currently assigned to them, when a department employee holds it and they are the
    /// responsible CS Agent, or when a department role of theirs covers its department; otherwise None (an eligible Call Center agent may
    /// additionally OPEN an unassigned claimable ticket read-only). Having created or handled a ticket grants nothing by itself.
    /// Everyone else keeps exactly their department-based answer (Full or None).
    /// </para>
    /// </summary>
    public static Task<TicketAccessLevel> GetTicketAccessAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        ITicketInteractionHandlerRepository? handlerRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        IServiceIdentityRegistry? serviceIdentities = null,
        CancellationToken cancellationToken = default,
        TicketClaimPolicy? claimPolicy = null,
        DateTime? inactivityReopenCutoffUtc = null)
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

            // The ticket is currently assigned to the caller: they always have full access to it, whatever department it sits in.
            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId && !Roles.IsReadOnlyCaller(callerRoles))
            {
                return TicketAccessLevel.Full;
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

            // A restricted CS Agent has Full access to: a ticket currently assigned to them (above); a ticket a DEPARTMENT employee (not a
            // CS Agent) is working, when they are its responsible CS Agent - the agent who last owned it - so they can follow up and close it;
            // and what a department role of theirs covers. Having created or handled the ticket in the past grants nothing by itself: once
            // another CS Agent owns it, or nobody does, it is not theirs to see.
            if (await CanFollowDepartmentWorkAsync(userDepartmentAssignmentRepository, callerEmployeeId, ticket, cancellationToken)
                || await CanReopenInactivityClosureAsync(
                    userDepartmentAssignmentRepository, callerEmployeeId, ticket, inactivityReopenCutoffUtc, cancellationToken)
                || await CanViewDepartmentAsync(
                    userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, serviceIdentities, cancellationToken))
            {
                return TicketAccessLevel.Full;
            }

            // An eligible Call Center agent may OPEN (never change) an unassigned, human-handling ticket of any department, to review it
            // before taking ownership of it (TicketClaimPolicy). Read paths pass the policy; modification checks never do.
            if (claimPolicy is not null
                && ticket.CurrentOwnerEmployeeId is null
                && (await claimPolicy.DecideAsync(callerEmployeeId, callerRoles, ticket.TicketId, cancellationToken)).Allowed)
            {
                return TicketAccessLevel.ReadOnly;
            }

            return TicketAccessLevel.None;
        }
    }

    /// <summary>
    /// True when a department employee (an owner who is not a CS Agent) holds the ticket and <paramref name="callerEmployeeId"/> is its
    /// responsible CS Agent (<see cref="IUserDepartmentAssignmentRepository.IsResponsibleCsAgentAsync"/>). The set-based twin is
    /// <c>TicketQueryFilters.VisibleTo</c>; keep them in step.
    /// </summary>
    public static async Task<bool> CanFollowDepartmentWorkAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository, Guid callerEmployeeId, Ticket ticket,
        CancellationToken cancellationToken = default) =>
        ticket.CurrentOwnerEmployeeId is { } owner
        && owner != callerEmployeeId
        && !await userDepartmentAssignmentRepository.HoldsRoleAsync(owner, Roles.CsAgent, cancellationToken)
        && await userDepartmentAssignmentRepository.IsResponsibleCsAgentAsync(ticket.TicketId, ticket.CreatedByEmployeeId, callerEmployeeId, cancellationToken);

    /// <summary>
    /// The inactivity-closure exception: an UNOWNED ticket the chatbot closed for customer inactivity, still inside the reopen window
    /// (<paramref name="inactivityReopenCutoffUtc"/> = now minus the window), stays visible to - and reopenable by - its last responsible CS Agent.
    /// Without a cutoff (a caller that does not model it) the exception is off, so this fails closed. Nothing else about the agent's earlier
    /// handling or creation of the ticket is restored. The set-based twin is <c>TicketQueryFilters.VisibleTo</c>.
    /// </summary>
    public static async Task<bool> CanReopenInactivityClosureAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository, Guid callerEmployeeId, Ticket ticket,
        DateTime? inactivityReopenCutoffUtc, CancellationToken cancellationToken = default) =>
        inactivityReopenCutoffUtc is { } cutoff
        && ticket.TicketStatus == TicketStatus.Closed
        && ticket.CurrentOwnerEmployeeId is null
        && await userDepartmentAssignmentRepository.IsResponsibleForInactivityClosureAsync(
            ticket.TicketId, ticket.CreatedByEmployeeId, callerEmployeeId, cutoff, cancellationToken);

    /// <summary>May the caller open and read the ticket — Full or ReadOnly access. Reads (details, history, notes list, SLA, interactions…) use this.</summary>
    public static async Task<bool> CanViewTicketAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        ITicketInteractionHandlerRepository? handlerRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        IServiceIdentityRegistry? serviceIdentities = null,
        CancellationToken cancellationToken = default,
        TicketClaimPolicy? claimPolicy = null,
        DateTime? inactivityReopenCutoffUtc = null) =>
        await GetTicketAccessAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken, claimPolicy,
            inactivityReopenCutoffUtc)
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
        CancellationToken cancellationToken = default,
        DateTime? inactivityReopenCutoffUtc = null) =>
        await GetTicketAccessAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken,
            claimPolicy: null, inactivityReopenCutoffUtc)
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
