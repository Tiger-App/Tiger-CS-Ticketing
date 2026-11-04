using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// The Web-side gate on Administration -> Collections Tower Assignments and its nav entry: the roles that can manage the
/// monthly assignments (CS Manager, Department Head - the Collections manager - and System Administrator). Display-only: the Api
/// (<c>CollectionsTeam.CanManageTowersAsync</c>) is the enforcement point, and additionally requires a Department Head to belong to the
/// Collections department. Collections employees do not reach the page; their manual ticket Assign / Reassign is a separate rule.
/// </summary>
public static class TowerAssignmentsPolicy
{
    public const string Name = "CollectionsTowerAssignments";

    public const string Route = "/Admin/CollectionsTowerAssignments";

    public static readonly string[] AllowedRoles =
    [
        Roles.CsManager,
        Roles.DepartmentHead,
        Roles.SystemAdministrator
    ];

    public static bool AppliesTo(CurrentUser? user) =>
        user is not null && user.Roles.Any(r => AllowedRoles.Contains(r, StringComparer.Ordinal));
}
