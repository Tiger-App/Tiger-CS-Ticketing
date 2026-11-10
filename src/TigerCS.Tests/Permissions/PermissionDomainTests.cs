using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Tests.Permissions;

public class PermissionDomainTests
{
    private static readonly IReadOnlyDictionary<string, PermissionEffect> None = new Dictionary<string, PermissionEffect>();

    [Fact]
    public void Catalog_keys_are_unique_known_and_grouped_into_declared_modules()
    {
        var keys = PermissionCatalog.All.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PermissionCatalog.All, p => Assert.Contains(p.Module, PermissionModules.Ordered));
        Assert.All(PermissionModules.Ordered, m => Assert.Contains(PermissionCatalog.All, p => p.Module == m));
        Assert.All(keys, k => Assert.True(PermissionCatalog.IsKnown(k)));
    }

    [Fact]
    public void Every_role_baseline_only_names_catalog_permissions_and_covers_every_role()
    {
        foreach (var role in Roles.All)
        {
            var baseline = RolePermissionDefaults.ForRole(role);
            Assert.NotEmpty(baseline);
            Assert.All(baseline, k => Assert.True(PermissionCatalog.IsKnown(k), $"{role}: {k}"));
        }
    }

    [Fact]
    public void System_administrator_holds_every_permission_and_read_only_roles_hold_view_only()
    {
        Assert.Equal(PermissionCatalog.All.Count, RolePermissionDefaults.ForRole(Roles.SystemAdministrator).Count);

        foreach (var role in Roles.ReadOnly)
        {
            Assert.All(RolePermissionDefaults.ForRole(role),
                k => Assert.False(PermissionCatalog.Find(k)!.IsMutating, $"{role} must not hold {k}"));
        }
    }

    [Fact]
    public void Only_the_system_administrator_baseline_carries_administration_permissions()
    {
        var adminKeys = PermissionCatalog.All.Where(p => p.Module == PermissionModules.Administration).Select(p => p.Key).ToList();
        foreach (var role in Roles.All.Where(r => r != Roles.SystemAdministrator))
        {
            Assert.Empty(RolePermissionDefaults.ForRole(role).Intersect(adminKeys));
        }
    }

    [Fact]
    public void Roles_combine_as_a_union()
    {
        var union = RolePermissionDefaults.ForRoles([Roles.ChairmanCeo, Roles.CsAgent]);
        Assert.Contains(Permissions_ReportsView, union);
        Assert.Contains(TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsCreate, union);
    }

    private const string Permissions_ReportsView = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.ReportsView;

    [Fact]
    public void Without_overrides_the_effective_set_is_exactly_the_role_baseline()
    {
        var effective = EffectivePermissions.Compute([Roles.CsManager], None);
        Assert.True(effective.SetEquals(RolePermissionDefaults.ForRole(Roles.CsManager)));
    }

    [Fact]
    public void A_grant_adds_a_permission_the_role_lacks()
    {
        var overrides = new Dictionary<string, PermissionEffect> { [TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.ReportsView] = PermissionEffect.Grant };
        var effective = EffectivePermissions.Compute([Roles.CsAgent], overrides);
        Assert.Contains(Permissions_ReportsView, effective);
    }

    [Fact]
    public void A_denial_removes_a_permission_the_role_carries_and_beats_a_grant()
    {
        var key = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsReopen;
        var denied = new Dictionary<string, PermissionEffect> { [key] = PermissionEffect.Deny };
        Assert.DoesNotContain(key, EffectivePermissions.Compute([Roles.CsAgent, Roles.CsManager], denied));
        Assert.Equal(PermissionSource.Denied, EffectivePermissions.Resolve(key, RolePermissionDefaults.ForRole(Roles.CsAgent), denied));
    }

    [Fact]
    public void A_denial_is_per_user_and_leaves_other_permissions_alone()
    {
        var denied = new Dictionary<string, PermissionEffect> { [TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsClose] = PermissionEffect.Deny };
        var effective = EffectivePermissions.Compute([Roles.CsSupervisor], denied);
        var baseline = RolePermissionDefaults.ForRole(Roles.CsSupervisor);
        Assert.Equal(baseline.Count - 1, effective.Count);
    }

    [Fact]
    public void Granting_an_administrator_only_permission_is_ignored_unless_a_role_carries_it()
    {
        var key = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.AdminPermissionsManage;
        var grant = new Dictionary<string, PermissionEffect> { [key] = PermissionEffect.Grant };
        Assert.DoesNotContain(key, EffectivePermissions.Compute([Roles.CsManager], grant));
        Assert.Contains(key, EffectivePermissions.Compute([Roles.SystemAdministrator], grant));
    }

    [Fact]
    public void An_override_row_rejects_unknown_permissions()
    {
        Assert.Throws<ArgumentException>(() =>
            new UserPermissionOverride(Guid.NewGuid(), "Nope.Nope", PermissionEffect.Deny, null, DateTime.UtcNow));
    }

    [Fact]
    public void The_catalog_has_the_expected_29_permissions_including_resolve_and_transfer()
    {
        Assert.Equal(29, PermissionCatalog.All.Count);
        Assert.True(PermissionCatalog.IsKnown(TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsResolve));
        Assert.True(PermissionCatalog.IsKnown(TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsTransfer));
    }

    private static IEnumerable<string> RolesWith(string permission) =>
        Roles.All.Where(r => RolePermissionDefaults.ForRole(r).Contains(permission));

    /// <summary>The baselines must restate the role sets the services enforce - if one drifts, this fails.</summary>
    [Fact]
    public void Ticket_action_baselines_match_the_roles_the_services_enforce()
    {
        const string ns = "TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions";
        _ = ns;
        var sets = new (string Permission, IEnumerable<string> Enforced)[]
        {
            (TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsResolve, TigerCS.Application.Modules.Ticketing.Services.TicketRoleSets.Resolve),
            (TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsClose, TigerCS.Application.Modules.Ticketing.Services.TicketRoleSets.Close),
            (TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsTransfer, TigerCS.Application.Modules.Ticketing.Services.TicketRoleSets.Transfer),
            (TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsReopen, TigerCS.Application.Modules.Ticketing.Services.TicketRoleSets.Reopen),
            (TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsAssign,
                TigerCS.Application.Modules.Ticketing.Services.TicketRoleSets.AssignCrossDepartment
                    .Concat(TigerCS.Application.Modules.Ticketing.Services.TicketRoleSets.AssignWithinOwnDepartment))
        };

        foreach (var (permission, enforced) in sets)
        {
            Assert.True(
                enforced.OrderBy(r => r).SequenceEqual(RolesWith(permission).Where(r => r != Roles.SystemAdministrator).OrderBy(r => r)),
                $"{permission}: baseline {string.Join(", ", RolesWith(permission))} != enforced {string.Join(", ", enforced)} (+System Administrator)");
        }
    }
}
