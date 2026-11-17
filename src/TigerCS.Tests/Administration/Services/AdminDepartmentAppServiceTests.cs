using TigerCS.Application.Modules.Administration.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Administration.Services;

public class AdminDepartmentAppServiceTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Fixture(
        AdminDepartmentAppService Service,
        FakeDepartmentRepository Departments,
        FakeEmployeeRepository Employees,
        FakeUserAccountManager Accounts,
        DepartmentDirectoryAppService Directory,
        FakeDeactivationDependencyReader Dependencies);

    private static Fixture Create()
    {
        var departments = new FakeDepartmentRepository();
        var employees = new FakeEmployeeRepository();
        var accounts = new FakeUserAccountManager();
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var unitOfWork = new FakeIdentityUnitOfWork();
        var assignmentService = new DepartmentAssignmentService(employees, departments, assignments, unitOfWork, TimeProvider.System);
        var dependencies = new FakeDeactivationDependencyReader();
        var service = new AdminDepartmentAppService(
            departments, assignments, employees, accounts, new FakeRequestTypeRepository(), assignmentService, unitOfWork, new FakeAuditEntryWriter(), dependencies);
        return new Fixture(service, departments, employees, accounts, new DepartmentDirectoryAppService(departments), dependencies);
    }

    [Fact]
    public async Task Add_edit_and_deactivate_keep_the_department_resolvable_by_name()
    {
        var f = Create();

        var created = await f.Service.CreateAsync(Admin, new SaveDepartmentRequestDto("Collections", "col"));
        Assert.Equal(AdminOutcome.Success, created.Outcome);
        Assert.Equal("COL", created.Value!.Code);

        var duplicate = await f.Service.CreateAsync(Admin, new SaveDepartmentRequestDto("Collections", "XX"));
        Assert.Equal(AdminOutcome.ValidationFailed, duplicate.Outcome);

        var edited = await f.Service.UpdateAsync(Admin, created.Value.DepartmentId, new SaveDepartmentRequestDto("Collections & Recovery", "COL"));
        Assert.Equal("Collections & Recovery", edited.Value!.Name);

        // Referenced by tickets: still only deactivation, never deletion.
        f.Departments.TicketReferences[created.Value.DepartmentId] = 42;
        var deactivated = await f.Service.SetActivationAsync(Admin, created.Value.DepartmentId, new SetActiveRequestDto(false, "merged"));
        Assert.Equal(AdminOutcome.Success, deactivated.Outcome);
        Assert.False(deactivated.Value!.IsActive);
        Assert.Equal(42, deactivated.Value.TicketReferenceCount);

        // Historical displays resolve the name; new-work pickers exclude it.
        Assert.Equal("Collections & Recovery", (await f.Departments.GetByIdAsync(created.Value.DepartmentId))!.Name);
        Assert.DoesNotContain(await f.Directory.ListAsync(activeOnly: true), d => d.DepartmentId == created.Value.DepartmentId);
        Assert.Contains(await f.Directory.ListAsync(activeOnly: false), d => d.DepartmentId == created.Value.DepartmentId);
        Assert.Contains(await f.Service.ListAsync(includeInactive: true), d => d.DepartmentId == created.Value.DepartmentId);
        Assert.DoesNotContain(await f.Service.ListAsync(includeInactive: false), d => d.DepartmentId == created.Value.DepartmentId);
    }

    [Fact]
    public async Task Members_are_added_and_removed_through_the_existing_membership_model()
    {
        var f = Create();
        var department = (await f.Service.CreateAsync(Admin, new SaveDepartmentRequestDto("Registration", "REG"))).Value!;
        var employeeId = Guid.NewGuid();
        f.Employees.Add(new Employee(employeeId, "Reem T", false, DateTime.UtcNow), Roles.DepartmentEmployee);
        f.Accounts.Add(employeeId, "reem.t", Roles.DepartmentEmployee);

        var added = await f.Service.AddMemberAsync(Admin, department.DepartmentId, new AddDepartmentMemberRequestDto(employeeId, true));
        Assert.Equal(AdminOutcome.Success, added.Outcome);
        var member = Assert.Single(added.Value!.Members);
        Assert.Equal("Reem T", member.DisplayName);
        Assert.Equal([Roles.DepartmentEmployee], member.Roles);

        Assert.Equal(AdminOutcome.Conflict, (await f.Service.AddMemberAsync(Admin, department.DepartmentId, new AddDepartmentMemberRequestDto(employeeId, false))).Outcome);
        Assert.Equal(AdminOutcome.ValidationFailed, (await f.Service.AddMemberAsync(Admin, department.DepartmentId, new AddDepartmentMemberRequestDto(Guid.NewGuid(), false))).Outcome);

        var removed = await f.Service.RemoveMemberAsync(Admin, department.DepartmentId, employeeId);
        Assert.Equal(AdminOutcome.Success, removed.Outcome);
        Assert.Empty(removed.Value!.Members);
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.RemoveMemberAsync(Admin, department.DepartmentId, employeeId)).Outcome);
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.GetAsync(999)) is null ? AdminOutcome.NotFound : AdminOutcome.Success);
    }

    [Fact]
    public async Task Inactive_department_cannot_receive_new_members()
    {
        var f = Create();
        var department = (await f.Service.CreateAsync(Admin, new SaveDepartmentRequestDto("Legacy", "LEG"))).Value!;
        await f.Service.SetActivationAsync(Admin, department.DepartmentId, new SetActiveRequestDto(false));
        var employeeId = Guid.NewGuid();
        f.Employees.Add(new Employee(employeeId, "Someone", false, DateTime.UtcNow));
        f.Accounts.Add(employeeId, "someone");

        var result = await f.Service.AddMemberAsync(Admin, department.DepartmentId, new AddDepartmentMemberRequestDto(employeeId, false));

        Assert.Equal(AdminOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task Deactivation_is_refused_while_live_routing_or_approval_dependencies_remain_and_nothing_changes()
    {
        var f = Create();
        var department = (await f.Service.CreateAsync(Admin, new SaveDepartmentRequestDto("Accounting", "ACC"))).Value!;
        f.Dependencies.Blockers[department.DepartmentId] = ["5 active approval requirement(s) target this department."];

        var refused = await f.Service.SetActivationAsync(Admin, department.DepartmentId, new SetActiveRequestDto(false, "retire"));

        Assert.Equal(AdminOutcome.Conflict, refused.Outcome);
        Assert.Contains(refused.Errors!, e => e.Contains("approval requirement", StringComparison.Ordinal));
        Assert.True((await f.Departments.GetByIdAsync(department.DepartmentId))!.IsActive);

        f.Dependencies.Blockers.Clear();
        var allowed = await f.Service.SetActivationAsync(Admin, department.DepartmentId, new SetActiveRequestDto(false, "retire"));
        Assert.Equal(AdminOutcome.Success, allowed.Outcome);
        Assert.False(allowed.Value!.IsActive);
    }

    [Fact]
    public async Task Reactivation_is_never_blocked_by_the_deactivation_guard()
    {
        var f = Create();
        var department = (await f.Service.CreateAsync(Admin, new SaveDepartmentRequestDto("Accounting", "ACC"))).Value!;
        await f.Service.SetActivationAsync(Admin, department.DepartmentId, new SetActiveRequestDto(false, "retire"));
        f.Dependencies.Blockers[department.DepartmentId] = ["anything"];

        var result = await f.Service.SetActivationAsync(Admin, department.DepartmentId, new SetActiveRequestDto(true, "restore"));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task An_inactive_department_cannot_receive_new_members()
    {
        var f = Create();
        var department = (await f.Service.CreateAsync(Admin, new SaveDepartmentRequestDto("Accounting", "ACC"))).Value!;
        var employeeId = Guid.NewGuid();
        f.Employees.Add(new Employee(employeeId, "Reem T", false, DateTime.UtcNow), Roles.DepartmentEmployee);
        f.Accounts.Add(employeeId, "reem.t", Roles.DepartmentEmployee);
        await f.Service.SetActivationAsync(Admin, department.DepartmentId, new SetActiveRequestDto(false, "retire"));

        var result = await f.Service.AddMemberAsync(Admin, department.DepartmentId, new AddDepartmentMemberRequestDto(employeeId, false));

        Assert.Equal(AdminOutcome.Conflict, result.Outcome);
    }

    public sealed class FakeDeactivationDependencyReader : IDepartmentDeactivationDependencyReader
    {
        public Dictionary<int, IReadOnlyList<string>> Blockers { get; } = [];

        public Task<IReadOnlyList<string>> GetBlockersAsync(int departmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Blockers.TryGetValue(departmentId, out var list) ? list : (IReadOnlyList<string>)[]);
    }
}
