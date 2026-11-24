using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// Whether the signed-in employee's roles carry <see cref="Permissions.EmployeeReviewsView"/> by default — the "role rule" half of the nav
/// decision (<see cref="ViewerPermissions.CanReach"/> adds explicit grants and denials). Display-only; the Api enforces the permission and
/// scopes every figure to what the caller may see.
/// </summary>
public static class EmployeeReviewsPolicy
{
    public static bool AppliesTo(CurrentUser? user) =>
        user is not null && RolePermissionDefaults.ForRoles(user.Roles).Contains(Permissions.EmployeeReviewsView);
}
