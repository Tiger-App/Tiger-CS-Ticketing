using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The one definition of the <c>Tickets.TransferAny</c> ("Transfer Any Ticket") permission.
///
/// <para>
/// It is deliberately its own permission: it is not Accessible Departments (department membership), not Tickets.Assign (reassigning to an
/// employee), not Manage Users and not the System Administrator override. Holding it never adds the holder to a department and never lets
/// them assign, reassign or administer anything.
/// </para>
///
/// <para>
/// An <b>explicit grant</b> (Administration → Users → Manage Permissions) lets a Call Center employee (CS Agent, CS Supervisor, CS Manager -
/// <see cref="TicketRoleSets.Transfer"/>) transfer any open ticket, whatever its current department, to any active department, and open that
/// ticket read-only to do it. It never reaches a read-only role, a Department Employee/Head, or a closed ticket. An explicit <b>denial</b>
/// takes the CS Manager's built-in any-ticket reach away (they keep the ordinary per-ticket Call Center transfer rule). Tickets.Transfer
/// itself, when denied, still blocks everything, exactly as before.
/// </para>
/// </summary>
public static class TransferAnyTicketPolicy
{
    /// <summary>True when the caller is a Call Center employee who may use a Transfer Any Ticket grant at all (never a read-only caller).</summary>
    public static bool IsEligibleRole(IReadOnlyCollection<string> callerRoles) =>
        !Roles.IsReadOnlyCaller(callerRoles) && callerRoles.Any(TicketRoleSets.Transfer.Contains);

    /// <summary>The caller's own employee-specific Transfer Any Ticket override (grant, deny or none).</summary>
    public static Task<PermissionEffect?> GetOverrideAsync(
        IUserPermissionOverrideRepository? overrides, Guid callerEmployeeId, CancellationToken cancellationToken) =>
        CallerPermissionOverrides.GetAsync(overrides, callerEmployeeId, Permissions.TicketsTransferAny, cancellationToken);

    /// <summary>True when the caller holds an explicit Transfer Any Ticket grant and is a Call Center employee.</summary>
    public static async Task<bool> IsGrantedAsync(
        IUserPermissionOverrideRepository? overrides, Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles,
        CancellationToken cancellationToken) =>
        IsEligibleRole(callerRoles)
        && await GetOverrideAsync(overrides, callerEmployeeId, cancellationToken) == PermissionEffect.Grant;
}
