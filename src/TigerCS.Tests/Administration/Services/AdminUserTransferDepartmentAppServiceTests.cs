using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.Administration.Services;

/// <summary>Administration → Users: the transfer-destination list is its own thing - separate from membership (view access) and audited.</summary>
public class AdminUserTransferDepartmentAppServiceTests
{
    private sealed class Fixture
    {
        public FakeEmployeeRepository Employees = new();
        public FakeDepartmentRepository Departments = new();
        public FakeUserTransferDepartmentRepository Lists = new();
        public FakeUserDepartmentAssignmentRepository Members = new();
        public FakeAuditEntryWriter Audit = new();
        public Guid Admin = Guid.NewGuid();
        public Guid User = Guid.NewGuid();
        public Department Reg, Col, Mnt, Retired;
        public AdminUserTransferDepartmentAppService Service;

        public Fixture()
        {
            Employees.Add(new Employee(User, "Agent", false, DateTime.UtcNow));
            Reg = Departments.AddDepartment("Registration", "REG");
            Col = Departments.AddDepartment("Collections", "COL");
            Mnt = Departments.AddDepartment("Maintenance", "MNT");
            Retired = Departments.AddDepartment("Retired", "OLD", isActive: false);
            Members.Assignments.Add(new UserDepartmentAssignment(User, Reg.DepartmentId, true, DateTime.UtcNow, null));   // a MEMBER (can view) of Registration only
            Service = new(Employees, Departments, Lists, new FakeIdentityUnitOfWork(), Audit, TimeProvider.System);
        }
    }

    [Fact]
    public async Task Saving_TheList_StoresExactlyThoseDepartments_AuditsTheChange_AndLeavesMembershipAlone()
    {
        var f = new Fixture();

        var saved = await f.Service.SetAsync(f.Admin, f.User, new([f.Col.DepartmentId, f.Mnt.DepartmentId, f.Col.DepartmentId]));

        Assert.Equal(AdminOutcome.Success, saved.Outcome);
        Assert.Equal([f.Col.DepartmentId, f.Mnt.DepartmentId], saved.Value!.DepartmentIds);
        Assert.Equal([f.Col.DepartmentId, f.Mnt.DepartmentId], (await f.Service.GetAsync(f.User)).Value!.DepartmentIds);
        var audit = Assert.Single(f.Audit.Entries, a => a.Action == AdminUserTransferDepartmentAppService.AuditAction);
        Assert.Equal(f.Admin, audit.ActorEmployeeId);
        Assert.Equal("DepartmentIds=", audit.BeforeValue);
        Assert.Equal($"DepartmentIds={f.Col.DepartmentId},{f.Mnt.DepartmentId}", audit.AfterValue);
        // Membership (view access) is unchanged: still only Registration - and Registration was NOT added to the transfer list because they can view it.
        Assert.Equal([f.Reg.DepartmentId], f.Members.Assignments.Where(a => a.EmployeeId == f.User).Select(a => a.DepartmentId));
        Assert.DoesNotContain(f.Reg.DepartmentId, (await f.Service.GetAsync(f.User)).Value!.DepartmentIds);
    }

    [Fact]
    public async Task Membership_NeverCountsAsATransferDestination()
    {
        var f = new Fixture();
        var allowed = await TransferDestinationPolicy.IsPermittedAsync(f.Lists.Allow(f.User, f.Mnt.DepartmentId), f.User, [Roles.CsSupervisor], f.Reg.DepartmentId, default);
        Assert.False(allowed);       // a member of Registration with a list that omits it
    }

    [Fact]
    public async Task Replacing_AndClearing_TheList_AddsAndRemovesRows_AndAnUnchangedSaveWritesNoAudit()
    {
        var f = new Fixture();
        await f.Service.SetAsync(f.Admin, f.User, new([f.Col.DepartmentId, f.Mnt.DepartmentId]));
        f.Audit.Entries.Clear();

        var same = await f.Service.SetAsync(f.Admin, f.User, new([f.Mnt.DepartmentId, f.Col.DepartmentId]));
        Assert.Equal(AdminOutcome.Success, same.Outcome);
        Assert.Empty(f.Audit.Entries);

        var narrowed = await f.Service.SetAsync(f.Admin, f.User, new([f.Reg.DepartmentId]));
        Assert.Equal([f.Reg.DepartmentId], narrowed.Value!.DepartmentIds);
        Assert.Equal([f.Reg.DepartmentId], f.Lists.Rows.Select(r => r.DepartmentId));

        var cleared = await f.Service.SetAsync(f.Admin, f.User, new([]));
        Assert.Empty(cleared.Value!.DepartmentIds);
        Assert.Empty(f.Lists.Rows);
        Assert.Equal(2, f.Audit.Entries.Count);
    }

    [Fact]
    public async Task UnknownInactiveOrMissing_AreRefused_WithoutChangingAnything()
    {
        var f = new Fixture();
        await f.Service.SetAsync(f.Admin, f.User, new([f.Col.DepartmentId]));

        var inactive = await f.Service.SetAsync(f.Admin, f.User, new([f.Retired.DepartmentId]));
        var unknown = await f.Service.SetAsync(f.Admin, f.User, new([999]));
        var noUser = await f.Service.SetAsync(f.Admin, Guid.NewGuid(), new([f.Col.DepartmentId]));

        Assert.Equal(AdminOutcome.ValidationFailed, inactive.Outcome);
        Assert.Equal(AdminOutcome.ValidationFailed, unknown.Outcome);
        Assert.Equal(AdminOutcome.NotFound, noUser.Outcome);
        Assert.Equal([f.Col.DepartmentId], f.Lists.Rows.Select(r => r.DepartmentId));
    }
}
