using Microsoft.AspNetCore.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// The signed-in employee's own permission overrides, read once per request
/// from <c>GET api/users/me</c> (shared with <see cref="TicketNameResolver"/>
/// so a page makes one call, not two). It drives what the Web <i>displays</i>
/// - navigation, page gates and ticket actions - so a denied permission is
/// never offered. It is never the security boundary: every Api endpoint
/// enforces the same overrides, and when /me cannot be read the Web falls back
/// to the role rules and the Api still refuses what it must.
/// </summary>
public sealed class ViewerAccessService(UsersApiClient usersApiClient)
{
    private Task<ApiResult<TigerCS.Application.Modules.IdentityAndAccess.Dto.CurrentUserResponseDto>>? _me;

    private IReadOnlySet<string> _granted = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlySet<string> _denied = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The one /me call of this request.</summary>
    public Task<ApiResult<TigerCS.Application.Modules.IdentityAndAccess.Dto.CurrentUserResponseDto>> GetMeAsync(CancellationToken cancellationToken) =>
        _me ??= LoadAsync(cancellationToken);

    private async Task<ApiResult<TigerCS.Application.Modules.IdentityAndAccess.Dto.CurrentUserResponseDto>> LoadAsync(CancellationToken cancellationToken)
    {
        var me = await usersApiClient.GetMeAsync(cancellationToken);
        if (me.IsSuccess && me.Value is { } value)
        {
            _granted = new HashSet<string>(value.GrantedPermissions ?? [], StringComparer.Ordinal);
            _denied = new HashSet<string>(value.DeniedPermissions ?? [], StringComparer.Ordinal);
        }

        return me;
    }

    public async Task<ViewerPermissions> GetAsync(CancellationToken cancellationToken = default)
    {
        await GetMeAsync(cancellationToken);
        return new ViewerPermissions(_granted, _denied);
    }
}

/// <summary>The viewer's explicit grants and denials. Empty when none exist or /me could not be read.</summary>
public sealed record ViewerPermissions(IReadOnlySet<string> Granted, IReadOnlySet<string> Denied)
{
    public static readonly ViewerPermissions None =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    public bool IsDenied(string permission) => Denied.Contains(permission);

    /// <summary>
    /// The same decision the Api's endpoint makes: never when denied; otherwise through the
    /// role/policy rule (<paramref name="roleRuleSatisfied"/>) or an explicit grant of a grantable permission.
    /// </summary>
    public bool CanReach(string permission, bool roleRuleSatisfied) =>
        !IsDenied(permission)
        && (roleRuleSatisfied || (Granted.Contains(permission) && PermissionCatalog.Find(permission) is { IsReserved: false }));
}

/// <summary>A Web page-gate requirement: the viewer reaches the page when any of the permissions is reachable (<see cref="ViewerPermissions.CanReach"/>) through the allowed roles or a grant.</summary>
public sealed class ViewerPermissionRequirement(IReadOnlyCollection<string> allowedRoles, params string[] permissions) : IAuthorizationRequirement
{
    public IReadOnlyCollection<string> AllowedRoles { get; } = allowedRoles;

    public IReadOnlyCollection<string> Permissions { get; } = permissions;
}

public sealed class ViewerPermissionHandler(ViewerAccessService access) : AuthorizationHandler<ViewerPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ViewerPermissionRequirement requirement)
    {
        var holdsRole = requirement.AllowedRoles.Any(r => context.User.IsInRole(r));
        var permissions = await access.GetAsync();
        if (requirement.Permissions.Any(p => permissions.CanReach(p, holdsRole)))
        {
            context.Succeed(requirement);
        }
    }
}
