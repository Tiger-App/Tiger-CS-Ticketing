using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>
/// How the application services consult a caller's employee-specific
/// permission override for the one permission they are about to exercise.
/// <b>Deny</b> always refuses (checked before ownership, role or the System
/// Administrator override). <b>Grant</b> only stands in for the <i>role</i>
/// half of the service's own rule - ownership, department scope, ticket
/// visibility, status and workflow rules are evaluated afterwards, unchanged.
/// A null repository (tests, tools) means "no overrides".
/// </summary>
public static class CallerPermissionOverrides
{
    public static async Task<PermissionEffect?> GetAsync(
        IUserPermissionOverrideRepository? overrides, Guid callerEmployeeId, string permissionKey, CancellationToken cancellationToken)
    {
        if (overrides is null)
        {
            return null;
        }

        var effects = await overrides.GetEffectsAsync(callerEmployeeId, cancellationToken);
        return effects.TryGetValue(permissionKey, out var effect) ? effect : null;
    }
}
