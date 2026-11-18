// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCsWeb::TigerCS.Web.Services.Auth;
using P = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Web;

/// <summary>What the navigation and the /Collections page gate decide for the signed-in viewer: the same rule the Api enforces.</summary>
public class CollectionsAccessViewerTests
{
    private static ViewerPermissions Viewer(string[] granted, string[] denied) =>
        new(new HashSet<string>(granted, StringComparer.Ordinal), new HashSet<string>(denied, StringComparer.Ordinal));

    [Theory]
    [InlineData(Roles.SystemAdministrator)]
    [InlineData(Roles.CsManager)]
    public void Always_allowed_roles_see_the_module_with_no_overrides_and_even_when_denied(string role)
    {
        Assert.True(ViewerPermissions.None.CanAccessCollections([role]));
        Assert.True(Viewer([], [P.CollectionsAccess]).CanAccessCollections([role]));
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public void Everyone_else_is_hidden_by_default_shown_when_granted_and_hidden_again_when_revoked_or_denied(string role)
    {
        Assert.False(ViewerPermissions.None.CanAccessCollections([role]));
        Assert.True(Viewer([P.CollectionsAccess], []).CanAccessCollections([role]));
        Assert.False(Viewer([], [P.CollectionsAccess]).CanAccessCollections([role]));

        // Holding the granular View permission alone never opens the module.
        Assert.False(Viewer([P.CollectionsView, P.CampaignsView], []).CanAccessCollections([role]));
    }
}
