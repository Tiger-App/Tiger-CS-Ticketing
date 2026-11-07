using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// Where a signed-in employee lands when no page was asked for: Collections for anyone who can reach it, otherwise the landing they already had.
/// Uses the same decision as the navigation (<see cref="ViewerPermissions.CanReach"/>): never when the permission is denied; otherwise through the role baseline
/// or an explicit grant. It only chooses a page: every page and every Api endpoint still enforces its own authorization.
/// </summary>
public static class LandingPage
{
    public const string Collections = "/Collections/Receivables";

    public static string For(IEnumerable<string> roles, ViewerPermissions permissions, string fallback) =>
        permissions.CanReach(Permissions.CollectionsView, RolePermissionDefaults.ForRoles(roles).Contains(Permissions.CollectionsView)) ? Collections : fallback;
}
