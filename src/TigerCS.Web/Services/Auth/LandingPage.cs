using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// Where a signed-in employee lands when no page was asked for: the Dashboard for everyone who can reach it (every role holds <c>Dashboard.View</c> by default).
/// Only a person whose Dashboard access was explicitly denied gets the previous landing instead (the Collections Campaign List when reachable, else the fallback),
/// so nobody is sent to a page they cannot open. Uses the same decision as the navigation (<see cref="ViewerPermissions.CanReach"/>): never when the permission is
/// denied; otherwise through the role baseline or an explicit grant. It only chooses a page: every page and every Api endpoint still enforces its own authorization.
/// </summary>
public static class LandingPage
{
    public const string Collections = "/Collections/CampaignList";

    public const string Dashboard = "/Dashboard";

    public static string For(IEnumerable<string> roles, ViewerPermissions permissions, string fallback)
    {
        var baseline = RolePermissionDefaults.ForRoles(roles);
        if (permissions.CanReach(Permissions.DashboardView, baseline.Contains(Permissions.DashboardView)))
        {
            return Dashboard;
        }

        return permissions.CanReach(Permissions.CollectionsView, baseline.Contains(Permissions.CollectionsView)) ? Collections : fallback;
    }

    /// <summary>
    /// True for a returnUrl worth following after sign-in: a local URL that is not the site root (the root only decides a landing, so following it would cost an
    /// extra hop to the same Dashboard) and not the Login page itself (which would show the sign-in form again to a person who just signed in).
    /// </summary>
    public static bool IsFollowable(string? returnUrl, Func<string, bool> isLocalUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !isLocalUrl(returnUrl))
        {
            return false;
        }

        var path = returnUrl.Split('?', '#')[0].TrimEnd('/');
        return path.Length > 0 && !path.Equals("/Login", StringComparison.OrdinalIgnoreCase);
    }
}
