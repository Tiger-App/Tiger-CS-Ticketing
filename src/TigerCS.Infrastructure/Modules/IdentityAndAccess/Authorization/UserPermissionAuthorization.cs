using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// Declares the permission an endpoint (or controller) exercises. It is plain
/// endpoint metadata: the existing <c>[Authorize(Policy = ...)]</c> policies
/// keep deciding by role exactly as before, and
/// <see cref="UserPermissionAuthorizationHandler"/> applies the employee's
/// overrides to them - a denial refuses the request whatever the role says,
/// a grant lets the employee through a role-gated policy. Several
/// attributes on one endpoint must all be satisfied.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Marks an endpoint that is deliberately outside permission management
/// (self-service account endpoints, public routes, the Genesys/CRM
/// integration surface, reference data every signed-in user needs). The
/// endpoint-coverage test requires every action to carry either this or a
/// <see cref="RequirePermissionAttribute"/>, so a new endpoint cannot be
/// added unprotected by accident.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class NoPermissionRequiredAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Applies employee-specific permission overrides on top of the role-based
/// authorization that already exists - a bare <see cref="IAuthorizationHandler"/>
/// in the same style as <see cref="SystemAdministratorOverrideHandler"/>, so
/// it runs for every policy without any policy knowing about it.
///
/// <list type="number">
///   <item><description><b>Deny</b> - an explicit denial of a permission the endpoint requires <i>fails</i> the
///   evaluation. <see cref="AuthorizationHandlerContext.Fail()"/> beats every <c>Succeed</c>, including the
///   System Administrator override, so a denial really revokes.</description></item>
///   <item><description><b>Grant</b> - when the employee holds an explicit grant for every permission the endpoint
///   requires, the policy's <i>role</i> requirement is satisfied. Only <see cref="RolesAuthorizationRequirement"/> is
///   satisfied: the identity gate (active employee), the service-identity confinement, the read-only write guard and
///   department scoping are restrictions, and a grant never lifts a restriction. Administrator-only permissions are
///   never honoured as grants.</description></item>
/// </list>
///
/// <para>
/// <b>Module gates</b> (<see cref="PermissionDefinition.IsModuleGate"/>, today <see cref="Permissions.CollectionsAccess"/>) are the one
/// exception to "overrides only adjust the role policies": a gate is evaluated on its own terms by <see cref="CollectionsAccess"/> -
/// open for System Administrator and CS Manager (always), otherwise only through an explicit grant - and <i>fails</i> the evaluation
/// when closed, even for a caller with no overrides at all. Because it fails rather than succeeds, it also refuses a caller the System
/// Administrator override would not have admitted (nobody else), and it never lifts any other restriction.
/// </para>
///
/// An endpoint without <see cref="RequirePermissionAttribute"/> is not affected at all, and for ordinary (non-gate) permissions a
/// caller without overrides is not affected either - existing roles keep working unchanged.
/// </summary>
public sealed class UserPermissionAuthorizationHandler(IUserPermissionOverrideRepository overrides) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.Identity?.IsAuthenticated is not true || context.Resource is not HttpContext http)
        {
            return;
        }

        var required = http.GetEndpoint()?.Metadata.GetOrderedMetadata<RequirePermissionAttribute>();
        if (required is null || required.Count == 0
            || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var employeeId))
        {
            return;
        }

        var effects = await overrides.GetEffectsAsync(employeeId, http.RequestAborted);

        // Module gates first: closed unless the role carries it or it was granted, and never open against an explicit denial
        // (except for the roles that always hold it).
        var roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        foreach (var gate in required.Where(a => IsEnforcedOnItsOwnTerms(a.Permission)))
        {
            if (!IsGateOpen(gate.Permission, roles, effects))
            {
                context.Fail(new AuthorizationFailureReason(this, $"Access to the module behind '{gate.Permission}' has not been granted to this user."));
                return;
            }
        }

        if (effects.Count == 0)
        {
            return;
        }

        foreach (var attribute in required.Where(a => !IsEnforcedOnItsOwnTerms(a.Permission)))
        {
            if (effects.TryGetValue(attribute.Permission, out var effect) && effect == PermissionEffect.Deny)
            {
                context.Fail(new AuthorizationFailureReason(this, $"Permission '{attribute.Permission}' is denied for this user."));
                return;
            }
        }

        var allGranted = required.Where(a => !IsEnforcedOnItsOwnTerms(a.Permission)).ToList() is { Count: > 0 } granular
            && granular.All(a =>
            effects.TryGetValue(a.Permission, out var effect)
            && effect == PermissionEffect.Grant
            && PermissionCatalog.Find(a.Permission) is { IsReserved: false });
        if (!allGranted)
        {
            return;
        }

        foreach (var requirement in context.PendingRequirements.OfType<RolesAuthorizationRequirement>().ToList())
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>A module gate (<see cref="PermissionDefinition.IsModuleGate"/>) or a permission flagged <see cref="PermissionDefinition.EnforceEffective"/>: decided by role baseline plus explicit grant, never merely by adjusting a role policy.</summary>
    private static bool IsEnforcedOnItsOwnTerms(string permission) =>
        PermissionCatalog.Find(permission) is { IsModuleGate: true } or { EnforceEffective: true };

    /// <summary>The gate decision for one module-gate permission. Collections is the only gate; a future one falls back to the generic effective-permission rule (role baseline or grant, never when denied).</summary>
    private static bool IsGateOpen(string permission, IReadOnlyCollection<string> roles, IReadOnlyDictionary<string, PermissionEffect> effects) =>
        string.Equals(permission, Permissions.CollectionsAccess, StringComparison.Ordinal)
            ? CollectionsAccess.IsAllowed(roles, effects)
            : EffectivePermissions.IsEffective(EffectivePermissions.Resolve(permission, RolePermissionDefaults.ForRoles(roles), effects));
}
