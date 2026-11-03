using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Audit follow-up. (1) The Call Center Round Robin pool is CS Agents only; other departments keep the wider operational-role pool.
/// (2) Ticket-list paging is deterministic when many tickets share one CreatedAtUtc, and My Tickets (owner filter) lists only the viewer's tickets.
/// </summary>
public class CallCenterCsAgentOnlyRotationAndPagingTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private sealed class FakeRotation : IAssignmentRotationRepository
    {
        private readonly Dictionary<int, DepartmentAssignmentRotation> _rows = [];

        public Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            if (!_rows.TryGetValue(departmentId, out var row))
            {
                _rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
            }

            return Task.FromResult(row);
        }
    }

    private sealed class Env
    {
        public FakeDepartmentRepository Departments { get; } = new();
        public FakeUserDepartmentAssignmentRepository Members { get; } = new();
        public FakeUserRoleReader RolesReader { get; } = new();
        public Department CallCenter { get; }
        public Department Leasing { get; }
        public TicketAutoAssignmentService Service { get; }

        public Env()
        {
            CallCenter = Departments.AddDepartment("Call Center", "CC");
            Leasing = Departments.AddDepartment("Leasing", "LEA");
            Service = new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), Members,
                new FakeTicketAssignmentRepository(), new FakeAuditEntryWriter(), new FakeRotation(), Departments,
                roleReader: RolesReader);
        }

        public Guid Add(Department department, params string[] roles)
        {
            var id = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(id, department.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
            RolesReader.Add(id, roles);
            return id;
        }

        public Ticket NewTicket(Department department, int n) =>
            Ticket.CreateUnverified($"TG-CC-20261103-{n:D4}", department.DepartmentId, categoryId: 5, (byte)PriorityLevel.Medium, "Call", Now);
    }

    [Fact]
    public async Task CallCenterRotation_DealsOnlyToCsAgents()
    {
        var env = new Env();
        var a = env.Add(env.CallCenter, Roles.CsAgent);
        var b = env.Add(env.CallCenter, Roles.CsAgent);
        env.Add(env.CallCenter, Roles.DepartmentEmployee);
        env.Add(env.CallCenter, Roles.DepartmentHead);
        env.Add(env.CallCenter, Roles.CsSupervisor);

        var owners = new List<Guid>();
        for (var i = 1; i <= 8; i++)
        {
            var t = env.NewTicket(env.CallCenter, i);
            var result = await env.Service.AssignCallCenterIntakeAsync(t, Now, Guid.NewGuid());
            Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, result.Outcome);
            owners.Add(t.CurrentOwnerEmployeeId!.Value);
        }

        Assert.All(owners, o => Assert.Contains(o, new[] { a, b }));
        Assert.Equal(4, owners.Count(o => o == a));
        Assert.Equal(4, owners.Count(o => o == b));
    }

    [Fact]
    public async Task CallCenterRotation_WithNoCsAgent_LeavesTicketUnassigned_NeverGuesses()
    {
        var env = new Env();
        env.Add(env.CallCenter, Roles.DepartmentEmployee);
        env.Add(env.CallCenter, Roles.DepartmentHead);

        var t = env.NewTicket(env.CallCenter, 1);
        var result = await env.Service.AssignCallCenterIntakeAsync(t, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.NoEligibleEmployee, result.Outcome);
        Assert.Null(t.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task OtherDepartments_AlsoGetACsAgent_FromTheSharedCallCenterRotation_NeverTheirOwnEmployees()
    {
        var env = new Env();
        var csAgent = env.Add(env.CallCenter, Roles.CsAgent);
        env.Add(env.Leasing, Roles.DepartmentEmployee);   // a department employee is no longer the rotation's pool

        var t = env.NewTicket(env.Leasing, 1);
        var result = await env.Service.ApplyRoundRobinFallbackAsync(
            t, new AutoAssignmentResult(AutoAssignmentOutcome.NoRequestType), AutoAssignmentTrigger.DepartmentTransfer, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, result.Outcome);
        Assert.Equal(csAgent, t.CurrentOwnerEmployeeId);
        Assert.Equal(env.Leasing.DepartmentId, t.CurrentDepartmentId);   // department and CS Agent are independent
    }

    private static TigerCsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TicketQuery Query(int page, int pageSize, Guid? owner = null, bool descending = true) =>
        new(VisibleDepartmentIds: null, DepartmentId: null, CategoryId: null, PriorityId: null, TicketStatus: null, VerificationStatus: null,
            OwnerEmployeeId: owner, Search: null, TicketSortBy.CreatedAtUtc, descending, page, pageSize);

    [Fact]
    public async Task Paging_WithIdenticalCreatedAt_NeverRepeatsOrSkipsATicket_AndIsOrderedByTicketIdWithinTheTie()
    {
        await using var db = NewDb();
        for (var i = 1; i <= 25; i++)
        {
            await db.Tickets.AddAsync(Ticket.CreateUnverified($"TG-CC-20261103-{i:D4}", 1, 5, (byte)PriorityLevel.Medium, "x", Now));
        }

        await db.SaveChangesAsync();
        var repo = new TicketRepository(db);

        var seen = new List<long>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await repo.SearchAsync(Query(page, 10));
            Assert.Equal(25, result.TotalCount);
            seen.AddRange(result.Items.Select(t => t.TicketId));
        }

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(id => id), seen);
    }

    [Fact]
    public async Task MyTickets_OwnerFilter_ListsOnlyTicketsCurrentlyAssignedToTheViewer_AcrossAllPages()
    {
        await using var db = NewDb();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        for (var i = 1; i <= 30; i++)
        {
            var t = Ticket.CreateUnverified($"TG-CC-20261103-{i:D4}", 1, 5, (byte)PriorityLevel.Medium, "x", Now.AddMinutes(i));
            if (i % 3 != 0)
            {
                t.AssignTo(i % 2 == 0 ? me : other);
            }

            await db.Tickets.AddAsync(t);
        }

        await db.SaveChangesAsync();
        var expected = db.Tickets.Count(t => t.CurrentOwnerEmployeeId == me);
        var repo = new TicketRepository(db);

        var mine = new List<Ticket>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await repo.SearchAsync(Query(page, 5, owner: me));
            Assert.Equal(expected, result.TotalCount);
            mine.AddRange(result.Items);
        }

        Assert.Equal(expected, mine.Count);
        Assert.All(mine, t => Assert.Equal(me, t.CurrentOwnerEmployeeId));
        Assert.Equal(mine.Count, mine.Select(t => t.TicketId).Distinct().Count());
    }
}
