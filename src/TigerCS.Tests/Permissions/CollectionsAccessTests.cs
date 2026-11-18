using System.Reflection;
using TigerCS.Api.Controllers;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using P = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Permissions;

/// <summary>
/// The Collections module gate: System Administrator and CS Manager always; anyone else only through an explicit grant; everyone else
/// denied by default - and the granular Collections / Campaign permissions untouched.
/// </summary>
public class CollectionsAccessTests
{
    private static readonly IReadOnlyDictionary<string, PermissionEffect> NoOverrides = new Dictionary<string, PermissionEffect>();

    private static IReadOnlyDictionary<string, PermissionEffect> With(PermissionEffect effect) =>
        new Dictionary<string, PermissionEffect> { [P.CollectionsAccess] = effect };

    public static IEnumerable<object[]> AlwaysAllowed => [[Roles.SystemAdministrator], [Roles.CsManager]];

    public static IEnumerable<object[]> OtherRoles => Roles.All
        .Where(r => r != Roles.SystemAdministrator && r != Roles.CsManager)
        .Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(AlwaysAllowed))]
    public void System_administrator_and_cs_manager_are_always_allowed_even_against_an_override(string role)
    {
        Assert.True(CollectionsAccess.IsAllowed([role], NoOverrides));
        Assert.True(CollectionsAccess.IsAllowed([role], With(PermissionEffect.Grant)));
        Assert.True(CollectionsAccess.IsAllowed([role], With(PermissionEffect.Deny)));
    }

    [Theory]
    [MemberData(nameof(OtherRoles))]
    public void Every_other_role_is_denied_by_default(string role)
    {
        Assert.False(CollectionsAccess.IsAllowed([role], NoOverrides));
        Assert.DoesNotContain(P.CollectionsAccess, RolePermissionDefaults.ForRole(role));
    }

    [Theory]
    [MemberData(nameof(OtherRoles))]
    public void An_explicit_grant_allows_any_other_role_and_removing_it_revokes(string role)
    {
        Assert.True(CollectionsAccess.IsAllowed([role], With(PermissionEffect.Grant)));

        // Revocation = the override row is removed: back to the role default, which is "no".
        Assert.False(CollectionsAccess.IsAllowed([role], NoOverrides));
    }

    [Theory]
    [MemberData(nameof(OtherRoles))]
    public void An_explicit_denial_refuses_any_other_role(string role) =>
        Assert.False(CollectionsAccess.IsAllowed([role], With(PermissionEffect.Deny)));

    [Fact]
    public void A_user_holding_several_roles_is_allowed_when_any_of_them_always_is()
    {
        Assert.True(CollectionsAccess.IsAllowed([Roles.CsAgent, Roles.CsManager], NoOverrides));
        Assert.False(CollectionsAccess.IsAllowed([Roles.CsAgent, Roles.DepartmentHead], NoOverrides));
    }

    [Fact]
    public void The_gate_is_a_dedicated_catalog_permission_carried_only_by_the_always_allowed_roles()
    {
        var definition = PermissionCatalog.Find(P.CollectionsAccess);
        Assert.NotNull(definition);
        Assert.True(definition!.IsModuleGate);
        Assert.Equal(PermissionModules.Collections, definition.Module);
        Assert.False(definition.IsReserved);
        Assert.Single(PermissionCatalog.All, p => p.IsModuleGate);

        var carriers = Roles.All.Where(r => RolePermissionDefaults.ForRole(r).Contains(P.CollectionsAccess)).ToList();
        Assert.Equivalent(CollectionsAccess.AlwaysAllowedRoles, carriers);
    }

    [Fact]
    public void The_granular_collections_and_campaign_permissions_are_unchanged_for_every_role()
    {
        // Module access opens the door; these still decide what each role may do inside. None of them was added or removed.
        Assert.Contains(P.CollectionsView, RolePermissionDefaults.ForRole(Roles.CsAgent));
        Assert.DoesNotContain(P.CollectionsEdit, RolePermissionDefaults.ForRole(Roles.CsAgent));
        Assert.DoesNotContain(P.CollectionsSend, RolePermissionDefaults.ForRole(Roles.CsAgent));

        var supervisor = RolePermissionDefaults.ForRole(Roles.CsSupervisor);
        Assert.Contains(P.CollectionsView, supervisor);
        Assert.Contains(P.CollectionsSend, supervisor);
        Assert.DoesNotContain(P.CollectionsEdit, supervisor);
        Assert.Contains(P.CampaignsView, supervisor);
        Assert.DoesNotContain(P.CampaignsExport, supervisor);

        var manager = RolePermissionDefaults.ForRole(Roles.CsManager);
        Assert.All(
            [P.CollectionsView, P.CollectionsEdit, P.CollectionsSend, P.CampaignsView, P.CampaignsExport],
            k => Assert.Contains(k, manager));

        Assert.Contains(P.CampaignsExport, RolePermissionDefaults.ForRole(Roles.GeneralManager));
        Assert.Equal(PermissionCatalog.All.Count, RolePermissionDefaults.ForRole(Roles.SystemAdministrator).Count);
    }

    // ----------------------------------------------------------------- endpoint coverage

    private static readonly Type[] ModuleOnlyControllers =
    [
        typeof(CollectionsCampaignsController),
        typeof(CollectionsReviewController),
        typeof(CollectionsReconciliationController),
        typeof(CollectionsGenesysHistoryController),
        typeof(CollectionsTowerAssignmentsController),
        typeof(PactReceivablesController)
    ];

    private static IEnumerable<string> RequiredPermissions(MemberInfo member) =>
        member.GetCustomAttributes<RequirePermissionAttribute>(inherit: true).Select(a => a.Permission);

    [Fact]
    public void Every_module_only_collections_controller_requires_module_access_on_every_action()
    {
        foreach (var controller in ModuleOnlyControllers)
        {
            Assert.Contains(P.CollectionsAccess, RequiredPermissions(controller));
            Assert.NotEmpty(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name.StartsWith("Http", StringComparison.Ordinal))));
        }
    }

    [Fact]
    public void Surfaces_shared_with_tickets_customers_and_genesys_are_not_behind_the_module_gate()
    {
        // The Payment tab on customers/tickets, the unit/customer payment lookups and the Genesys-facing Collections routes keep their
        // own (granular) permissions: module access is separate from handling tickets and from the integrations.
        Type[] shared = [typeof(CollectionsController), typeof(CollectionsUnitsController), typeof(GenesysCollectionsController)];
        foreach (var controller in shared)
        {
            Assert.DoesNotContain(P.CollectionsAccess, RequiredPermissions(controller));
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(P.CollectionsAccess, RequiredPermissions(method));
            }
        }
    }
}
