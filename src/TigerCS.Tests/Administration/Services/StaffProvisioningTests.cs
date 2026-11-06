using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.ProductionSeeder;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Administration.Services;

public class StaffProvisioningTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Fixture(
        StaffProvisioningService Provisioning,
        DepartmentCoverageService Coverage,
        FakeEmployeeRepository Employees,
        FakeUserAccountManager Accounts,
        FakeDepartmentRepository Departments,
        FakeUserDepartmentAssignmentRepository Assignments,
        FakeAuditEntryWriter Audit,
        Department Cs,
        Department Reg);

    private static Fixture Create(bool withRegistration = true)
    {
        var employees = new FakeEmployeeRepository();
        var accounts = new FakeUserAccountManager();
        var departments = new FakeDepartmentRepository();
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var unitOfWork = new FakeIdentityUnitOfWork();
        var audit = new FakeAuditEntryWriter();
        var cs = departments.AddDepartment("Customer Service", "CS");
        var reg = withRegistration ? departments.AddDepartment("Registration", "REG") : null!;

        var assignmentService = new DepartmentAssignmentService(employees, departments, assignments, unitOfWork, TimeProvider.System);
        var activation = new UserActivationAppService(employees, accounts, unitOfWork, TimeProvider.System);
        var users = new AdminUserAppService(employees, accounts, accounts, assignments, departments, assignmentService, activation, unitOfWork, audit, TimeProvider.System);
        return new Fixture(
            new StaffProvisioningService(users, accounts, accounts, employees, departments, assignments),
            new DepartmentCoverageService(departments, assignments, employees, accounts, accounts),
            employees, accounts, departments, assignments, audit, cs, reg);
    }

    private static string Password(StaffRosterEntry _) => "Initial-Pass-1!";

    [Fact]
    public async Task Roster_is_the_seven_requested_users_with_existing_roles_and_no_administrative_role()
    {
        Assert.Equal(7, StaffRoster.RequestedStaff.Count);
        Assert.Equal(3, StaffRoster.RequestedStaff.Count(e => e.DepartmentCode == "CS" && e.Role == Roles.CsAgent));
        Assert.Equal(4, StaffRoster.RequestedStaff.Count(e => e.DepartmentCode == "REG" && e.Role == Roles.DepartmentEmployee));
        Assert.All(StaffRoster.RequestedStaff, e => Assert.Contains(e.Role, Roles.All));
        Assert.DoesNotContain(StaffRoster.RequestedStaff, e => e.Role is Roles.SystemAdministrator or Roles.CsManager or Roles.CsSupervisor or Roles.DepartmentHead);
        Assert.Equal(7, StaffRoster.RequestedStaff.Select(e => e.Email.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public async Task Plan_is_read_only_and_reports_would_create()
    {
        var f = Create();
        var lines = await f.Provisioning.PlanAsync(StaffRoster.RequestedStaff);

        Assert.All(lines, l => Assert.Equal(StaffProvisioningStatus.WouldCreate, l.Status));
        Assert.Empty(await f.Employees.ListAsync(true, null));
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task Apply_creates_each_user_once_with_department_role_and_audit_and_is_idempotent()
    {
        var f = Create();
        var first = await f.Provisioning.ApplyAsync(StaffRoster.RequestedStaff, Admin, Password);

        Assert.All(first, l => Assert.Equal(StaffProvisioningStatus.Created, l.Status));
        Assert.Equal(7, (await f.Employees.ListAsync(true, null)).Count);
        foreach (var line in first)
        {
            var expectedDept = line.Entry.DepartmentCode == "CS" ? f.Cs : f.Reg;
            Assert.Equal(expectedDept.Name, line.DepartmentName);
            Assert.Equal([line.Entry.Role], line.Roles);
            Assert.True(await f.Assignments.ExistsAsync(line.EmployeeId!.Value, expectedDept.DepartmentId));
            Assert.True((await f.Assignments.GetPrimaryAsync(line.EmployeeId!.Value))!.IsPrimary);
        }

        Assert.Equal(7, f.Audit.Entries.Count(e => e.Action == "AdminCreateUser" && e.ActorEmployeeId == Admin));

        var second = await f.Provisioning.ApplyAsync(StaffRoster.RequestedStaff, Admin, _ => throw new InvalidOperationException("no password may be requested for existing users"));
        Assert.All(second, l => Assert.Equal(StaffProvisioningStatus.AlreadyExists, l.Status));
        Assert.Equal(7, (await f.Employees.ListAsync(true, null)).Count);
        Assert.Equal(7, f.Audit.Entries.Count(e => e.Action == "AdminCreateUser"));
    }

    [Fact]
    public async Task Email_match_is_case_insensitive_and_an_existing_user_is_never_modified()
    {
        var f = Create();
        var id = Guid.NewGuid();
        f.Employees.Add(new Employee(id, "Someone Else", false, DateTime.UtcNow), Roles.DepartmentHead);
        f.Accounts.Add(id, "CUSTOMERSERVICE1@TIGERGROUP.AE", Roles.DepartmentHead);

        var lines = await f.Provisioning.ApplyAsync([StaffRoster.RequestedStaff[0]], Admin, Password);

        var line = Assert.Single(lines);
        Assert.Equal(StaffProvisioningStatus.Skipped, line.Status);   // exists, but different role/department: reported, not touched
        Assert.Equal([Roles.DepartmentHead], (await f.Accounts.GetRolesAsync(id)).ToList());
        Assert.Single(await f.Employees.ListAsync(true, null));
        Assert.False(await f.Assignments.ExistsAsync(id, f.Cs.DepartmentId));
    }

    [Fact]
    public async Task Deactivated_existing_user_is_skipped_not_reactivated()
    {
        var f = Create();
        var id = Guid.NewGuid();
        var employee = new Employee(id, "Nada Emad", false, DateTime.UtcNow);
        employee.Deactivate(DateTime.UtcNow);
        f.Employees.Add(employee, Roles.DepartmentEmployee);
        f.Accounts.Add(id, "n.eid@tigergroup.ae", Roles.DepartmentEmployee);

        var line = Assert.Single(await f.Provisioning.PlanAsync([StaffRoster.RequestedStaff[3]]));

        Assert.Equal(StaffProvisioningStatus.Skipped, line.Status);
        Assert.False(employee.IsActive);
    }

    [Fact]
    public async Task Missing_department_skips_the_user_and_creates_no_department()
    {
        var f = Create(withRegistration: false);

        var lines = await f.Provisioning.ApplyAsync(StaffRoster.RequestedStaff, Admin, Password);

        Assert.Equal(3, lines.Count(l => l.Status == StaffProvisioningStatus.Created));
        Assert.Equal(4, lines.Count(l => l.Status == StaffProvisioningStatus.Skipped && l.Detail.Contains("does not exist")));
        Assert.Single(await f.Departments.ListAsync(false));
    }

    [Fact]
    public async Task Duplicate_email_in_the_roster_is_created_once()
    {
        var f = Create();
        var entry = StaffRoster.RequestedStaff[0];
        var lines = await f.Provisioning.ApplyAsync([entry, entry with { Email = entry.Email.ToUpperInvariant() }], Admin, Password);

        Assert.Equal([StaffProvisioningStatus.Created, StaffProvisioningStatus.Skipped], lines.Select(l => l.Status));
        Assert.Single(await f.Employees.ListAsync(true, null));
    }

    [Fact]
    public async Task Coverage_flags_unstaffed_and_non_assignable_departments_and_counts_active_members_only()
    {
        var f = Create();
        var empty = f.Departments.AddDepartment("Accounting", "ACC");
        var supervisorOnly = f.Departments.AddDepartment("Handover", "HO");
        f.Departments.AddDepartment("Old", "OLD", isActive: false);

        await f.Provisioning.ApplyAsync(StaffRoster.RequestedStaff, Admin, Password);

        var sup = Guid.NewGuid();
        f.Employees.Add(new Employee(sup, "Sup", false, DateTime.UtcNow), Roles.CsSupervisor);
        f.Accounts.Add(sup, "sup", Roles.CsSupervisor);
        f.Assignments.Assignments.Add(new UserDepartmentAssignment(sup, supervisorOnly.DepartmentId, true, DateTime.UtcNow, null));

        // One Registration user is deactivated: counted out.
        var deactivated = (await f.Employees.ListAsync(true, null)).First(e => e.DisplayName == "Nada Emad");
        deactivated.Deactivate(DateTime.UtcNow);

        var coverage = (await f.Coverage.GetAsync()).ToDictionary(c => c.Code);

        Assert.DoesNotContain("OLD", coverage.Keys);
        Assert.Equal((3, 3, DepartmentStaffingStatus.Staffed), (coverage["CS"].ActiveEmployeeCount, coverage["CS"].AssignableEmployeeCount, coverage["CS"].Status));
        Assert.Equal((3, 3, DepartmentStaffingStatus.Staffed), (coverage["REG"].ActiveEmployeeCount, coverage["REG"].AssignableEmployeeCount, coverage["REG"].Status));
        Assert.Equal(DepartmentStaffingStatus.Unstaffed, coverage["ACC"].Status);
        Assert.Equal(DepartmentStaffingStatus.NoAssignableEmployee, coverage["HO"].Status);
        Assert.True(coverage["ACC"].NeedsStaffing && coverage["HO"].NeedsStaffing && !coverage["CS"].NeedsStaffing);
        Assert.Equal(empty.DepartmentId, coverage["ACC"].DepartmentId);
    }

    [Fact]
    public async Task Call_center_counts_only_cs_agents_as_assignable_and_locked_out_accounts_are_not()
    {
        var f = Create();
        var cc = f.Departments.AddDepartment("Call Center", "CC");
        var employee = Guid.NewGuid();
        var agent = Guid.NewGuid();
        foreach (var (id, role) in new[] { (employee, Roles.DepartmentEmployee), (agent, Roles.CsAgent) })
        {
            f.Employees.Add(new Employee(id, role, false, DateTime.UtcNow), role);
            f.Accounts.Add(id, role.Replace(' ', '.'), role);
            f.Assignments.Assignments.Add(new UserDepartmentAssignment(id, cc.DepartmentId, false, DateTime.UtcNow, null));
        }

        var line = (await f.Coverage.GetAsync()).Single(c => c.Code == "CC");
        Assert.Equal((2, 1), (line.ActiveEmployeeCount, line.AssignableEmployeeCount));

        f.Accounts.LockOut(agent);
        line = (await f.Coverage.GetAsync()).Single(c => c.Code == "CC");
        Assert.Equal(DepartmentStaffingStatus.NoAssignableEmployee, line.Status);
    }

    [Fact]
    public void Generated_passwords_are_policy_compliant_and_unique()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => StaffProvisioningCommand.GeneratePassword()).ToList();
        Assert.Equal(50, passwords.Distinct().Count());
        Assert.All(passwords, p =>
        {
            Assert.Equal(20, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
        });
    }

    [Theory]
    [InlineData("provision-staff")]
    [InlineData("provision-staff", "--target", "production", "--apply")]
    [InlineData("provision-staff", "--target", "production", "--apply", "--actor-email", "a@b.c", "--credentials-out", "/nonexistent-dir/x.txt")]
    [InlineData("provision-staff", "--target", "seedtest", "--confirm-seed-test")]
    [InlineData("provision-staff", "--target", "seedtest", "--apply", "--confirm-seed-test", "--actor-email", "a@b.c")]
    public void Command_line_refuses_incomplete_or_unsafe_requests(params string[] args) =>
        Assert.NotNull(StaffProvisioningCommand.Parse(args).Error);

    [Fact]
    public void Command_line_defaults_to_a_read_only_dry_run()
    {
        var request = StaffProvisioningCommand.Parse(["provision-staff", "--target", "production"]);
        Assert.Null(request.Error);
        Assert.False(request.Apply);
    }
}
