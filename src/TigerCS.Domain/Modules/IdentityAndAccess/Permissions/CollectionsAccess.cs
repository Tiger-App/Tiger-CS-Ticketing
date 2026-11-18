namespace TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

/// <summary>
/// The one rule for who may enter the Collections module, shared by the Api (endpoint authorization), the Web (page gate and
/// navigation) and Administration (what the Edit User page shows), so they cannot drift apart.
///
/// <list type="bullet">
///   <item><description>System Administrator and CS Manager: always allowed. An override cannot take it away - the role is the entitlement.</description></item>
///   <item><description>Anyone else: allowed only through an explicit <c>Collections.Access</c> grant, and never when it is explicitly denied.</description></item>
///   <item><description>No role other than the two above carries the permission, so everyone else is denied by default.</description></item>
/// </list>
///
/// It is independent of department membership: belonging to, or being assigned tickets of, the Collections department neither grants
/// nor removes module access. The granular permissions (<see cref="Permissions.CollectionsView"/>, <see cref="Permissions.CollectionsEdit"/>,
/// <see cref="Permissions.CollectionsSend"/>, <see cref="Permissions.CampaignsView"/>, <see cref="Permissions.CampaignsExport"/>) are
/// evaluated separately, exactly as before, inside the module.
/// </summary>
public static class CollectionsAccess
{
    /// <summary>The roles that always hold module access, whatever the user-level overrides say.</summary>
    public static readonly IReadOnlyList<string> AlwaysAllowedRoles = [Roles.SystemAdministrator, Roles.CsManager];

    public static bool IsAlwaysAllowed(IEnumerable<string> roles) =>
        roles.Any(r => AlwaysAllowedRoles.Contains(r, StringComparer.Ordinal));

    /// <summary>True when the employee, with these roles and overrides, may use the Collections module.</summary>
    public static bool IsAllowed(IEnumerable<string> roles, IReadOnlyDictionary<string, PermissionEffect> overrides)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(overrides);

        var roleList = roles as IReadOnlyCollection<string> ?? roles.ToList();
        if (IsAlwaysAllowed(roleList))
        {
            return true;
        }

        return EffectivePermissions.IsEffective(EffectivePermissions.Resolve(
            Permissions.CollectionsAccess, RolePermissionDefaults.ForRoles(roleList), overrides));
    }
}
