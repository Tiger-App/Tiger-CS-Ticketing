using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using P = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Permissions;

public class AdminUserPermissionAppServiceTests
{
    private sealed class FakeOverrideRepository(IEmployeeRepository employees, IUserRoleReader roles) : IUserPermissionOverrideRepository
    {
        public List<UserPermissionOverride> Rows { get; } = [];

        public Task<IReadOnlyList<UserPermissionOverride>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserPermissionOverride>>(Rows.Where(r => r.EmployeeId == employeeId).ToList());

        public Task<IReadOnlyDictionary<string, PermissionEffect>> GetEffectsAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PermissionEffect>>(
                Rows.Where(r => r.EmployeeId == employeeId).ToDictionary(r => r.PermissionKey, r => r.Effect));

        public Task AddAsync(UserPermissionOverride permissionOverride, CancellationToken cancellationToken = default)
        {
            Rows.Add(permissionOverride);
            return Task.CompletedTask;
        }

        public void Remove(UserPermissionOverride permissionOverride) => Rows.Remove(permissionOverride);

        public async Task<int> CountActiveInRoleWithoutDenialAsync(
            string roleName, string permissionKey, Guid excludedEmployeeId, CancellationToken cancellationToken = default)
        {
            var count = 0;
            foreach (var e in await employees.ListAsync(false, null, cancellationToken))
            {
                if (e.EmployeeId != excludedEmployeeId
                    && (await roles.GetRolesAsync(e.EmployeeId, cancellationToken)).Contains(roleName)
                    && !Rows.Any(r => r.EmployeeId == e.EmployeeId && r.PermissionKey == permissionKey && r.Effect == PermissionEffect.Deny))
                {
                    count++;
                }
            }

            return count;
        }

        public Task<IReadOnlyList<PermissionChangeLogEntry>> GetRecentChangesAsync(Guid employeeId, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PermissionChangeLogEntry>>([]);
    }

    private sealed class Registry(params Guid[] ids) : IServiceIdentityRegistry
    {
        public IReadOnlyCollection<Guid> ServiceIdentityIds => ids;
        public bool IsServiceIdentity(Guid employeeId) => ids.Contains(employeeId);
    }

    private sealed class Fixture
    {
        public FakeEmployeeRepository Employees { get; } = new();
        public FakeUserAccountManager Accounts { get; } = new();
        public FakeAuditEntryWriter Audit { get; } = new();
        public FakeOverrideRepository Overrides { get; }
        public List<Guid> ServiceIds { get; } = [];
        public AdminUserPermissionAppService Service => new(
            Employees, Accounts, Accounts, Overrides, new Registry([.. ServiceIds]), new FakeIdentityUnitOfWork(), Audit, TimeProvider.System);

        public Fixture() => Overrides = new FakeOverrideRepository(Employees, Accounts);

        public Guid Seed(string name, params string[] roles)
        {
            var id = Guid.NewGuid();
            Employees.Add(new Employee(id, name, false, DateTime.UtcNow), roles);
            Accounts.Add(id, name.Replace(' ', '.').ToLowerInvariant(), roles);
            return id;
        }
    }

    private static SetUserPermissionsRequestDto Req(params (string Key, string Value)[] items) =>
        new(items.ToDictionary(i => i.Key, i => i.Value), "because");

    private static PermissionStateDto State(UserPermissionsDto dto, string key) =>
        dto.Modules.SelectMany(m => m.Permissions).Single(p => p.Key == key);

    [Fact]
    public async Task Get_shows_roles_the_role_baseline_and_all_modules()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var agent = f.Seed("Alice Agent", Roles.CsAgent);

        var dto = (await f.Service.GetAsync(admin, agent)).Value!;

        Assert.Equal([Roles.CsAgent], dto.Roles);
        Assert.Equal(PermissionModules.Ordered, dto.Modules.Select(m => m.Module));
        Assert.True(State(dto, P.TicketsCreate).IsEffective);
        Assert.Equal("Role", State(dto, P.TicketsCreate).Source);
        Assert.False(State(dto, P.ReportsView).IsEffective);
        Assert.False(State(dto, P.AdminPermissionsManage).CanGrant);
        Assert.False(dto.IsSelf);
    }

    [Fact]
    public async Task Deny_overrides_the_role_for_that_employee_only_and_is_audited_with_before_and_after()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var a = f.Seed("Agent A", Roles.CsAgent);
        var b = f.Seed("Agent B", Roles.CsAgent);

        var result = await f.Service.SetAsync(admin, a, Req((P.TicketsReopen, "Deny")));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.False(State(result.Value!, P.TicketsReopen).IsEffective);
        Assert.Equal("Denied", State(result.Value!, P.TicketsReopen).Source);
        Assert.True(State((await f.Service.GetAsync(admin, b)).Value!, P.TicketsReopen).IsEffective);

        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal(admin, entry.ActorEmployeeId);
        Assert.Equal(AdminUserPermissionAppService.AuditAction, entry.Action);
        Assert.Equal(a.ToString(), entry.EntityId);
        Assert.Equal("Permission=Tickets.Reopen;Override=Inherit;Effective=true", entry.BeforeValue);
        Assert.Equal("Permission=Tickets.Reopen;Override=Deny;Effective=false;Reason=because", entry.AfterValue);
    }

    [Fact]
    public async Task Grant_adds_a_permission_and_returning_to_inherit_removes_the_row()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var agent = f.Seed("Alice Agent", Roles.CsAgent);

        var granted = await f.Service.SetAsync(admin, agent, Req((P.ReportsView, "Grant")));
        Assert.True(State(granted.Value!, P.ReportsView).IsEffective);
        Assert.Single(f.Overrides.Rows);

        var reverted = await f.Service.SetAsync(admin, agent, Req());
        Assert.False(State(reverted.Value!, P.ReportsView).IsEffective);
        Assert.Empty(f.Overrides.Rows);
        Assert.Equal(2, f.Audit.Entries.Count);
        Assert.Contains("Override=Inherit", f.Audit.Entries[1].AfterValue);
    }

    [Fact]
    public async Task Saving_an_unchanged_state_writes_nothing()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var agent = f.Seed("Alice Agent", Roles.CsAgent);

        await f.Service.SetAsync(admin, agent, Req((P.ReportsView, "Grant")));
        f.Audit.Entries.Clear();
        await f.Service.SetAsync(admin, agent, Req((P.ReportsView, "Grant")));

        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task Nobody_can_change_their_own_permissions()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        f.Seed("Second Admin", Roles.SystemAdministrator);

        var result = await f.Service.SetAsync(admin, admin, Req((P.TicketsView, "Deny")));

        Assert.Equal(AdminOutcome.Forbidden, result.Outcome);
        Assert.Empty(f.Overrides.Rows);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task Administrator_only_permissions_cannot_be_granted_to_other_roles()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var manager = f.Seed("Mona Manager", Roles.CsManager);

        foreach (var key in new[] { P.AdminPermissionsManage, P.AdminRolesAssign, P.AdminUsersEdit, P.AdminUsersCreate })
        {
            var result = await f.Service.SetAsync(admin, manager, Req((key, "Grant")));
            Assert.Equal(AdminOutcome.Forbidden, result.Outcome);
        }

        Assert.Empty(f.Overrides.Rows);
    }

    [Fact]
    public async Task A_caller_cannot_grant_what_they_do_not_hold_themselves()
    {
        var f = new Fixture();
        var admin = f.Seed("Limited Admin", Roles.SystemAdministrator);
        var agent = f.Seed("Alice Agent", Roles.CsAgent);
        var other = f.Seed("Other Admin", Roles.SystemAdministrator);
        // The caller was personally denied Reports.View.
        await f.Service.SetAsync(other, admin, Req((P.ReportsView, "Deny")));

        var result = await f.Service.SetAsync(admin, agent, Req((P.ReportsView, "Grant")));

        Assert.Equal(AdminOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task A_caller_denied_manage_permissions_cannot_use_the_service()
    {
        var f = new Fixture();
        var admin = f.Seed("Admin One", Roles.SystemAdministrator);
        var other = f.Seed("Admin Two", Roles.SystemAdministrator);
        var agent = f.Seed("Alice Agent", Roles.CsAgent);
        await f.Service.SetAsync(other, admin, Req((P.AdminPermissionsManage, "Deny")));

        var result = await f.Service.SetAsync(admin, agent, Req((P.TicketsView, "Deny")));

        Assert.Equal(AdminOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task The_last_administrator_who_can_manage_permissions_cannot_be_denied_it()
    {
        var f = new Fixture();
        var caller = f.Seed("Caller", Roles.SystemAdministrator);
        var sole = f.Seed("Sole Admin", Roles.SystemAdministrator);
        // Caller loses the permission, leaving 'sole' as the last holder.
        Assert.Equal(AdminOutcome.Success, (await f.Service.SetAsync(sole, caller, Req((P.AdminRolesAssign, "Deny")))).Outcome);

        var result = await f.Service.SetAsync(caller, sole, Req((P.AdminRolesAssign, "Deny")));

        Assert.Equal(AdminOutcome.Conflict, result.Outcome);
        Assert.DoesNotContain(f.Overrides.Rows, r => r.EmployeeId == sole);
    }

    [Fact]
    public async Task An_administrator_permission_can_be_denied_while_another_administrator_keeps_it()
    {
        var f = new Fixture();
        var a = f.Seed("Admin A", Roles.SystemAdministrator);
        var b = f.Seed("Admin B", Roles.SystemAdministrator);

        var result = await f.Service.SetAsync(a, b, Req((P.AdminRolesAssign, "Deny")));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Read_only_roles_cannot_be_granted_mutating_permissions_but_can_be_granted_views()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var ceo = f.Seed("The CEO", Roles.ChairmanCeo);

        var edit = await f.Service.SetAsync(admin, ceo, Req((P.TicketsEdit, "Grant")));
        Assert.Equal(AdminOutcome.ValidationFailed, edit.Outcome);

        var view = await f.Service.SetAsync(admin, ceo, Req((P.InteractionsView, "Grant")));
        Assert.Equal(AdminOutcome.Success, view.Outcome);
    }

    [Fact]
    public async Task Integration_service_accounts_cannot_be_given_overrides()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var genesys = f.Seed("Genesys Integration", Roles.CsAgent);
        f.ServiceIds.Add(genesys);

        var result = await f.Service.SetAsync(admin, genesys, Req((P.TicketsCreate, "Deny")));

        Assert.Equal(AdminOutcome.Conflict, result.Outcome);
        Assert.Empty(f.Overrides.Rows);
        Assert.True((await f.Service.GetAsync(admin, genesys)).Value!.IsServiceIdentity);
    }

    [Fact]
    public async Task Unknown_permissions_and_settings_are_rejected_without_changes()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);
        var agent = f.Seed("Alice Agent", Roles.CsAgent);

        var unknown = await f.Service.SetAsync(admin, agent, Req(("Tickets.Teleport", "Grant")));
        var badValue = await f.Service.SetAsync(admin, agent, Req((P.TicketsView, "Maybe")));

        Assert.Equal(AdminOutcome.ValidationFailed, unknown.Outcome);
        Assert.Equal(AdminOutcome.ValidationFailed, badValue.Outcome);
        Assert.Empty(f.Overrides.Rows);
    }

    [Fact]
    public async Task Unknown_employee_is_not_found()
    {
        var f = new Fixture();
        var admin = f.Seed("Root Admin", Roles.SystemAdministrator);

        Assert.Equal(AdminOutcome.NotFound, (await f.Service.GetAsync(admin, Guid.NewGuid())).Outcome);
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.SetAsync(admin, Guid.NewGuid(), Req())).Outcome);
    }
}
