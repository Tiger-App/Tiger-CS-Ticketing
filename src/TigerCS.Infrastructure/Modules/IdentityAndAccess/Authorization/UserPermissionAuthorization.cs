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
/// An endpoint without <see cref="RequirePermissionAttribute"/>, or a caller without overrides, is not affected at all -
/// existing roles keep working unchanged.
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
        if (effects.Count == 0)
        {
            return;
        }

        foreach (var attribute in required)
        {
            if (effects.TryGetValue(attribute.Permission, out var effect) && effect == PermissionEffect.Deny)
            {
                context.Fail(new AuthorizationFailureReason(this, $"Permission '{attribute.Permission}' is denied for this user."));
                return;
            }
        }

        var allGranted = required.All(a =>
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
}
