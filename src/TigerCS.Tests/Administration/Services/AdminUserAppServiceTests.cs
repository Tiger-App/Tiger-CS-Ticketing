using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Administration.Services;

public class AdminUserAppServiceTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Fixture(
        AdminUserAppService Service,
        FakeEmployeeRepository Employees,
        FakeUserAccountManager Accounts,
        FakeDepartmentRepository Departments,
        FakeUserDepartmentAssignmentRepository Assignments,
        FakeAuditEntryWriter Audit);

    private static Fixture Create()
    {
        var employees = new FakeEmployeeRepository();
        var accounts = new FakeUserAccountManager();
        var departments = new FakeDepartmentRepository();
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var unitOfWork = new FakeIdentityUnitOfWork();
        var audit = new FakeAuditEntryWriter();

        var assignmentService = new DepartmentAssignmentService(employees, departments, assignments, unitOfWork, TimeProvider.System);
        var activation = new UserActivationAppService(employees, accounts, unitOfWork, TimeProvider.System);
        var service = new AdminUserAppService(
            employees, accounts, accounts, assignments, departments, assignmentService, activation, unitOfWork, audit, TimeProvider.System);
        return new Fixture(service, employees, accounts, departments, assignments, audit);
    }

    private static Guid SeedUser(Fixture f, string name, params string[] roles)
    {
        var id = Guid.NewGuid();
        f.Employees.Add(new Employee(id, name, false, DateTime.UtcNow), roles);
        f.Accounts.Add(id, name.Replace(' ', '.').ToLowerInvariant(), roles);
        return id;
    }

    [Fact]
    public async Task Create_uses_identity_for_the_account_and_records_roles_and_primary_department()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");

        var result = await f.Service.CreateAsync(Admin, new CreateUserRequestDto(
            "sara.k", "sara@example.test", "Sara K", false, "Str0ng-Pass!", [Roles.DepartmentEmployee], collections.DepartmentId));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        var user = result.Value!;
        Assert.Equal("sara.k", user.UserName);
        Assert.Equal([Roles.DepartmentEmployee], user.Roles);
        var membership = Assert.Single(user.Departments);
        Assert.Equal("Collections", membership.Name);
        Assert.True(membership.IsPrimary);
        Assert.True(user.IsActive);
        Assert.Contains(f.Audit.Entries, e => e.Action == "AdminCreateUser" && e.ActorEmployeeId == Admin);
    }

    [Fact]
    public async Task Create_rejects_identity_password_policy_failures_and_unknown_roles_without_touching_state()
    {
        var f = Create();

        var weak = await f.Service.CreateAsync(Admin, new CreateUserRequestDto("x", null, "X", false, "short", [], null));
        Assert.Equal(AdminOutcome.ValidationFailed, weak.Outcome);
        Assert.Contains(weak.Errors!, e => e.Contains("8 characters", StringComparison.Ordinal));

        var badRole = await f.Service.CreateAsync(Admin, new CreateUserRequestDto("y", null, "Y", false, "Str0ng-Pass!", ["Wizard"], null));
        Assert.Equal(AdminOutcome.ValidationFailed, badRole.Outcome);

        Assert.Empty((await f.Service.ListAsync(null, true, 1, 10)).Items);
    }

    [Fact]
    public async Task Deactivate_keeps_the_employee_and_its_history_and_never_deletes()
    {
        var f = Create();
        var id = SeedUser(f, "Omar H", Roles.CsAgent);
        f.Employees.ReferencedByHistory.Add(id);

        var result = await f.Service.SetActivationAsync(Admin, id, new SetActiveRequestDto(false, "Left the company"));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.False(result.Value!.IsActive);
        Assert.True(result.Value.HasHistory);
        Assert.NotNull(await f.Employees.GetByIdAsync(id));
        Assert.Contains(await f.Service.ListAsync(null, includeInactive: true, 1, 10) is { } list ? list.Items : [], u => u.EmployeeId == id);
        Assert.DoesNotContain((await f.Service.ListAsync(null, includeInactive: false, 1, 10)).Items, u => u.EmployeeId == id);

        var reactivated = await f.Service.SetActivationAsync(Admin, id, new SetActiveRequestDto(true));
        Assert.True(reactivated.Value!.IsActive);
    }

    [Fact]
    public async Task Last_active_administrator_cannot_be_deactivated_or_stripped_of_the_role()
    {
        var f = Create();
        var onlyAdmin = SeedUser(f, "Root Admin", Roles.SystemAdministrator);

        var deactivate = await f.Service.SetActivationAsync(Admin, onlyAdmin, new SetActiveRequestDto(false));
        Assert.Equal(AdminOutcome.Conflict, deactivate.Outcome);

        var strip = await f.Service.SetRolesAsync(Admin, onlyAdmin, new SetUserRolesRequestDto([Roles.CsManager]));
        Assert.Equal(AdminOutcome.Conflict, strip.Outcome);
        Assert.Equal([Roles.SystemAdministrator], (await f.Service.GetAsync(onlyAdmin))!.Roles);
    }

    [Fact]
    public async Task Roles_are_replaced_with_fixed_roles_only()
    {
        var f = Create();
        SeedUser(f, "Root Admin", Roles.SystemAdministrator);
        var id = SeedUser(f, "Lina M", Roles.CsAgent);

        var result = await f.Service.SetRolesAsync(Admin, id, new SetUserRolesRequestDto([Roles.CsSupervisor, Roles.ReportingUser]));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.Equal([Roles.CsSupervisor, Roles.ReportingUser], result.Value!.Roles);
        Assert.Equal(AdminOutcome.ValidationFailed, (await f.Service.SetRolesAsync(Admin, id, new SetUserRolesRequestDto(["Nope"]))).Outcome);
    }

    [Fact]
    public async Task Membership_changes_use_the_existing_assignment_model_and_protect_the_primary()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var registration = f.Departments.AddDepartment("Registration", "REG");
        var inactive = f.Departments.AddDepartment("Legacy", "LEG", isActive: false);

        var first = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(collections.DepartmentId, true));
        Assert.Equal(AdminOutcome.Success, first.Outcome);

        var duplicate = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(collections.DepartmentId, false));
        Assert.Equal(AdminOutcome.Conflict, duplicate.Outcome);

        var toInactive = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(inactive.DepartmentId, false));
        Assert.Equal(AdminOutcome.Conflict, toInactive.Outcome);

        var second = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(registration.DepartmentId, false));
        Assert.Equal(2, second.Value!.Departments.Count);

        var removePrimary = await f.Service.RemoveDepartmentAsync(Admin, id, collections.DepartmentId);
        Assert.Equal(AdminOutcome.Conflict, removePrimary.Outcome);

        var removeSecondary = await f.Service.RemoveDepartmentAsync(Admin, id, registration.DepartmentId);
        Assert.Equal(AdminOutcome.Success, removeSecondary.Outcome);
        Assert.Single(removeSecondary.Value!.Departments);
    }

    [Fact]
    public async Task SetAccessibleDepartments_adds_and_removes_in_one_save_and_never_touches_the_primary()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var registration = f.Departments.AddDepartment("Registration", "REG");
        var maintenance = f.Departments.AddDepartment("Maintenance", "MNT");
        await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(collections.DepartmentId, true));
        await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(registration.DepartmentId, false));

        // The list omits the primary (Collections) and swaps Registration for Maintenance: the primary must survive regardless.
        var result = await f.Service.SetAccessibleDepartmentsAsync(Admin, id, new SetAccessibleDepartmentsRequestDto([maintenance.DepartmentId]));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.Equal(["Collections", "Maintenance"], result.Value!.Departments.Select(d => d.Name).Order());
        Assert.True(result.Value.Departments.Single(d => d.Name == "Collections").IsPrimary);
        Assert.False(result.Value.Departments.Single(d => d.Name == "Maintenance").IsPrimary);
        Assert.Contains(f.Audit.Entries, e => e.Action == "AdminSetAccessibleDepartments" && e.ActorEmployeeId == Admin);

        // Clearing the list leaves only the primary; repeating the same set changes (and audits) nothing more.
        var cleared = await f.Service.SetAccessibleDepartmentsAsync(Admin, id, new SetAccessibleDepartmentsRequestDto([]));
        var auditCount = f.Audit.Entries.Count;
        var again = await f.Service.SetAccessibleDepartmentsAsync(Admin, id, new SetAccessibleDepartmentsRequestDto([]));
        Assert.Equal(["Collections"], cleared.Value!.Departments.Select(d => d.Name));
        Assert.Equal(AdminOutcome.Success, again.Outcome);
        Assert.Equal(auditCount, f.Audit.Entries.Count);
    }

    [Fact]
    public async Task SetAccessibleDepartments_rejects_unknown_or_inactive_departments_and_changes_nothing()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var registration = f.Departments.AddDepartment("Registration", "REG");
        var legacy = f.Departments.AddDepartment("Legacy", "LEG", isActive: false);
        await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(collections.DepartmentId, true));

        var inactive = await f.Service.SetAccessibleDepartmentsAsync(Admin, id, new SetAccessibleDepartmentsRequestDto([registration.DepartmentId, legacy.DepartmentId]));
        var unknown = await f.Service.SetAccessibleDepartmentsAsync(Admin, id, new SetAccessibleDepartmentsRequestDto([9999]));
        var missingUser = await f.Service.SetAccessibleDepartmentsAsync(Admin, Guid.NewGuid(), new SetAccessibleDepartmentsRequestDto([]));

        Assert.Equal(AdminOutcome.ValidationFailed, inactive.Outcome);
        Assert.Equal(AdminOutcome.ValidationFailed, unknown.Outcome);
        Assert.Equal(AdminOutcome.NotFound, missingUser.Outcome);
        Assert.Equal(["Collections"], (await f.Service.GetAsync(id))!.Departments.Select(d => d.Name));   // not even the valid Registration was applied
    }

    [Fact]
    public async Task ResetPassword_replaces_the_password_lifts_lockout_rotates_the_stamp_and_audits_the_reason_only()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);
        f.Accounts.WithPassword(id, "Forgotten-1!").LockOut(id);
        var stampBefore = f.Accounts.SecurityStampOf(id);

        var result = await f.Service.ResetPasswordAsync(Admin, id, new ResetUserPasswordRequestDto("Fresh-Pass-9!", "Forgot password"));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.Equal("Fresh-Pass-9!", f.Accounts.PasswordOf(id));
        Assert.False(f.Accounts.IsLockedOut(id));
        Assert.NotEqual(stampBefore, f.Accounts.SecurityStampOf(id));

        var entry = Assert.Single(f.Audit.Entries, e => e.Action == "AdminResetUserPassword");
        Assert.Equal(Admin, entry.ActorEmployeeId);
        Assert.Equal("User", entry.EntityType);
        Assert.Equal(id.ToString(), entry.EntityId);
        Assert.Null(entry.BeforeValue);
        Assert.Equal("Reason=Forgot password;LockoutCleared=true;SecurityStampRotated=true", entry.AfterValue);
        Assert.DoesNotContain("Fresh-Pass-9!", entry.AfterValue);
    }

    [Fact]
    public async Task ResetPassword_rejects_a_policy_failure_with_identitys_reasons_and_changes_nothing()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);
        f.Accounts.WithPassword(id, "Forgotten-1!");
        var stampBefore = f.Accounts.SecurityStampOf(id);

        var weak = await f.Service.ResetPasswordAsync(Admin, id, new ResetUserPasswordRequestDto("short", null));
        var blank = await f.Service.ResetPasswordAsync(Admin, id, new ResetUserPasswordRequestDto("   ", null));

        Assert.Equal(AdminOutcome.PasswordPolicyViolation, weak.Outcome);
        Assert.Contains("Passwords must be at least 8 characters.", weak.Errors!);
        Assert.Equal(AdminOutcome.PasswordPolicyViolation, blank.Outcome);
        Assert.Equal("Forgotten-1!", f.Accounts.PasswordOf(id));
        Assert.Equal(stampBefore, f.Accounts.SecurityStampOf(id));
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AdminResetUserPassword");
    }

    [Fact]
    public async Task ResetPassword_for_an_unknown_user_is_not_found()
    {
        var f = Create();

        var result = await f.Service.ResetPasswordAsync(Admin, Guid.NewGuid(), new ResetUserPasswordRequestDto("Fresh-Pass-9!", null));

        Assert.Equal(AdminOutcome.NotFound, result.Outcome);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public void GetPasswordPolicy_exposes_the_account_managers_configured_rules()
    {
        var f = Create();

        var policy = f.Service.GetPasswordPolicy();

        Assert.Equal(new PasswordPolicyDto(8, true, true, true, true, 4), policy);
    }

    [Fact]
    public async Task Unlock_lifts_the_lockout_keeps_the_password_and_audits()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);
        f.Accounts.WithPassword(id, "Forgotten-1!").LockOut(id);
        var stampBefore = f.Accounts.SecurityStampOf(id);

        var result = await f.Service.UnlockAsync(Admin, id);

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.False(result.Value!.IsLockedOut);
        Assert.False(f.Accounts.IsLockedOut(id));
        Assert.Equal("Forgotten-1!", f.Accounts.PasswordOf(id));
        Assert.Equal(stampBefore, f.Accounts.SecurityStampOf(id));
        var entry = Assert.Single(f.Audit.Entries, e => e.Action == "AdminUnlockUser");
        Assert.Equal(Admin, entry.ActorEmployeeId);
        Assert.Equal(id.ToString(), entry.EntityId);
    }

    [Fact]
    public async Task Unlock_rejects_an_unlocked_or_unknown_user_without_auditing()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);

        Assert.Equal(AdminOutcome.Conflict, (await f.Service.UnlockAsync(Admin, id)).Outcome);
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.UnlockAsync(Admin, Guid.NewGuid())).Outcome);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task Search_matches_display_name_user_name_and_email()
    {
        var f = Create();
        SeedUser(f, "Ahmed Saleh", Roles.CsAgent);
        var lina = SeedUser(f, "Lina M", Roles.CsAgent);
        await f.Accounts.UpdateEmailAsync(lina, "lina@tiger.test");

        Assert.Single((await f.Service.ListAsync("ahmed", true, 1, 10)).Items);
        Assert.Single((await f.Service.ListAsync("tiger.test", true, 1, 10)).Items);
        Assert.Equal(2, (await f.Service.ListAsync(null, true, 1, 10)).TotalCount);
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.UpdateProfileAsync(Admin, Guid.NewGuid(), new UpdateUserProfileRequestDto("x", null, false))).Outcome);
    }
}
