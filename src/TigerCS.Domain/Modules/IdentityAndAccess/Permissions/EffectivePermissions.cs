namespace TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

/// <summary>Why a permission is (not) effective for a user - what the Manage Permissions screen shows.</summary>
public enum PermissionSource
{
    /// <summary>Not carried by any role and not granted.</summary>
    None,
    /// <summary>Carried by a role, no override.</summary>
    Role,
    /// <summary>Not carried by a role, allowed by an explicit grant.</summary>
    Granted,
    /// <summary>Refused by an explicit denial (whether or not a role carries it).</summary>
    Denied
}

/// <summary>
/// The pure combination rule. Effective = not denied AND (granted OR carried
/// by a role). A denial therefore beats both a grant and any role, and a
/// grant of a <see cref="PermissionDefinition.IsReserved"/> permission is
/// ignored unless a role already carries it (defence in depth: such a row can
/// never be written through the API, but a row inserted by hand must not
/// escalate anyone).
/// </summary>
public static class EffectivePermissions
{
    public static PermissionSource Resolve(
        string permissionKey, IReadOnlySet<string> roleBaseline, IReadOnlyDictionary<string, PermissionEffect> overrides)
    {
        var fromRole = roleBaseline.Contains(permissionKey);
        if (overrides.TryGetValue(permissionKey, out var effect))
        {
            if (effect == PermissionEffect.Deny)
            {
                return PermissionSource.Denied;
            }

            var reserved = PermissionCatalog.Find(permissionKey)?.IsReserved == true;
            if (!fromRole && !reserved)
            {
                return PermissionSource.Granted;
            }
        }

        return fromRole ? PermissionSource.Role : PermissionSource.None;
    }

    public static bool IsEffective(PermissionSource source) => source is PermissionSource.Role or PermissionSource.Granted;

    /// <summary>The set of effective permission keys for the given roles and overrides.</summary>
    public static IReadOnlySet<string> Compute(
        IEnumerable<string> roles, IReadOnlyDictionary<string, PermissionEffect> overrides)
    {
        var baseline = RolePermissionDefaults.ForRoles(roles);
        var effective = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in PermissionCatalog.All)
        {
            if (IsEffective(Resolve(permission.Key, baseline, overrides)))
            {
                effective.Add(permission.Key);
            }
        }

        return effective;
    }
}
