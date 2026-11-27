using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>Why a caller may not take ownership of a ticket right now.</summary>
public enum TicketClaimDenial
{
    None,

    /// <summary>The caller is not an eligible Call Center agent, or an explicit Deny override applies. Says nothing about the ticket.</summary>
    NotPermitted,
    NotFound,
    Terminal,
    Owned,
    ActiveAiConversation,
    PendingApproval
}

public sealed record TicketClaimDecision(TicketClaimDenial Denial)
{
    public bool Allowed => Denial == TicketClaimDenial.None;
}

/// <summary>
/// WHO may take ownership of WHICH ticket - the one policy behind the Take Ownership endpoint, the ticket-details
/// <c>canTakeOwnership</c> flag the UI renders from, and the Call Center agent's read-only visibility of the unassigned pool, so the button
/// and the API cannot disagree.
///
/// <para>
/// <b>Who:</b> an employee holding <see cref="Roles.CsAgent"/> (never a read-only role, never a service identity) who is an active, unlocked
/// member of the Call Center with an operational role - the very test the Genesys answering-agent assignment uses
/// (<see cref="TicketAutoAssignmentService.CheckAnsweringAgentAsync"/>). The System Administrator override does not make someone a Call
/// Center agent, and an explicit <b>Deny</b> of <c>Tickets.Edit</c> or <c>Tickets.Assign</c> always blocks - those are the permissions
/// working and assigning a ticket already hang on, so this adds no new permission to administer and no way around a Deny.
/// </para>
/// <para>
/// <b>Which:</b> <see cref="ITicketClaimPool"/> - active, unassigned, no pending approval, no active AI conversation.
/// </para>
/// </summary>
public sealed class TicketClaimPolicy(
    ITicketClaimPool claimPool,
    TicketAutoAssignmentService autoAssignment,
    IUserPermissionOverrideRepository? permissionOverrides = null,
    IServiceIdentityRegistry? serviceIdentities = null,
    TicketClaimOptions? options = null)
{
    /// <summary>The role half of the rule, with no database: CS Agent, not read-only. (Membership and account state are checked by <see cref="IsEligibleClaimerAsync"/>.)</summary>
    public static bool HoldsClaimRole(IReadOnlyCollection<string> callerRoles)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        return callerRoles.Contains(Roles.CsAgent, StringComparer.Ordinal) && !Roles.IsReadOnlyCaller(callerRoles);
    }

    /// <summary>
    /// Business rule: a Call Agent (CS Agent) may NOT take ownership of an unassigned ticket, and so may not see one either - the unassigned
    /// pool, the Genesys intake queue and the department queue belong to the CS Manager and CS Supervisor, who assign work. An agent only ever
    /// sees the tickets currently assigned to them. Every consumer of this policy (the list scope, the intake queue, the read-only open of an
    /// unassigned ticket, the details <c>canTakeOwnership</c> flag and the Take Ownership endpoint) goes through
    /// <see cref="IsEligibleClaimerAsync"/>, so this one switch closes all of them at the backend. It is deliberately NOT configuration
    /// (unlike <see cref="TicketClaimOptions"/>): an environment variable must not be able to re-open a visibility hole.
    /// </summary>
    private static readonly bool SelfClaimPermitted = false;

    /// <summary>True for an employee who may take ownership of tickets at all (see the type remarks) - independent of any ticket. Currently nobody: see <see cref="SelfClaimPermitted"/>.</summary>
    public async Task<bool> IsEligibleClaimerAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken = default)
    {
        if (!SelfClaimPermitted)
        {
            return false;
        }

        // The operational off-switch (TicketClaimOptions): off means off for the list scope, the details flag and the endpoint alike.
        if (options is { Enabled: false } || !HoldsClaimRole(callerRoles) || serviceIdentities?.IsServiceIdentity(callerEmployeeId) == true)
        {
            return false;
        }

        var effects = permissionOverrides is null
            ? null
            : await permissionOverrides.GetEffectsAsync(callerEmployeeId, cancellationToken);
        if (effects is not null
            && (IsDenied(effects, Permissions.TicketsEdit) || IsDenied(effects, Permissions.TicketsAssign)))
        {
            return false;
        }

        var callCenter = await autoAssignment.ResolveCallCenterAsync(cancellationToken);
        return callCenter is not null
            && await autoAssignment.CheckAnsweringAgentAsync(callerEmployeeId, callCenter.DepartmentId, cancellationToken) == AnsweringAgentIssue.None;
    }

    /// <summary>True when the department is the Call Center (the department an eligible agent's own filter also lists the pool under).</summary>
    public Task<bool> IsCallCenterDepartmentAsync(int departmentId, CancellationToken cancellationToken = default) =>
        autoAssignment.IsCallCenterDepartmentAsync(departmentId, cancellationToken);

    /// <summary>The full decision for one caller and one ticket. Caller eligibility first, so a non-agent learns nothing about the ticket.</summary>
    public async Task<TicketClaimDecision> DecideAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketId, CancellationToken cancellationToken = default)
    {
        if (!await IsEligibleClaimerAsync(callerEmployeeId, callerRoles, cancellationToken))
        {
            return new TicketClaimDecision(TicketClaimDenial.NotPermitted);
        }

        return new TicketClaimDecision(await claimPool.GetStatusAsync(ticketId, cancellationToken) switch
        {
            TicketClaimStatus.Available => TicketClaimDenial.None,
            TicketClaimStatus.NotFound => TicketClaimDenial.NotFound,
            TicketClaimStatus.Terminal => TicketClaimDenial.Terminal,
            TicketClaimStatus.ActiveAiConversation => TicketClaimDenial.ActiveAiConversation,
            TicketClaimStatus.PendingApproval => TicketClaimDenial.PendingApproval,
            _ => TicketClaimDenial.Owned
        });
    }

    private static bool IsDenied(IReadOnlyDictionary<string, PermissionEffect> effects, string permission) =>
        effects.TryGetValue(permission, out var effect) && effect == PermissionEffect.Deny;
}
