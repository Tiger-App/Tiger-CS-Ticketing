using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>Round Robin assignment, Call Center category routing, and owner-based Resolve/Close permission.</summary>
public class RoundRobinAndOwnerPermissionTests
{
    private const int DepartmentId = 2;
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private sealed class FakeRotationRepository : IAssignmentRotationRepository
    {
        public Dictionary<int, DepartmentAssignmentRotation> Rows { get; } = [];

        public Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            if (!Rows.TryGetValue(departmentId, out var row))
            {
                Rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
            }

            return Task.FromResult(row);
        }
    }

    private sealed record RrFixture(
        TicketAutoAssignmentService Service,
        FakeUserDepartmentAssignmentRepository Members,
        FakeTicketAssignmentRepository Assignments,
        FakeAuditEntryWriter Audit,
        FakeRotationRepository Rotation);

    private static RrFixture CreateRr(FakeRotationRepository? rotation = null, FakeUserDepartmentAssignmentRepository? members = null)
    {
        rotation ??= new FakeRotationRepository();
        members ??= new FakeUserDepartmentAssignmentRepository();
        var assignments = new FakeTicketAssignmentRepository();
        var audit = new FakeAuditEntryWriter();
        var service = new TicketAutoAssignmentService(
            new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(),
            members, assignments, audit, rotation);
        return new RrFixture(service, members, assignments, audit, rotation);
    }

    private static Guid AddMember(FakeUserDepartmentAssignmentRepository members, int departmentId = DepartmentId)
    {
        var id = Guid.NewGuid();
        members.Assignments.Add(new UserDepartmentAssignment(id, departmentId, isPrimary: true, Now, assignedByEmployeeId: null));
        return id;
    }

    private static Ticket NewTicket(int n, int departmentId = DepartmentId) =>
        Ticket.CreateUnverified($"TG-CS-20261031-{n:D4}", departmentId, categoryId: 5, (byte)PriorityLevel.Medium, "Payment", Now);

    private static async Task<AutoAssignmentResult> RoundRobinAsync(RrFixture f, Ticket ticket) =>
        await f.Service.ApplyCategoryRoutingAsync(
            ticket, new AutoAssignmentResult(AutoAssignmentOutcome.NoRequestType), Now, Guid.NewGuid());

    [Fact]
    public async Task RoundRobin_RotatesFairlyThroughEligibleEmployees()
    {
        var f = CreateRr();
        var a = AddMember(f.Members);
        var b = AddMember(f.Members);
        var c = AddMember(f.Members);

        var owners = new List<Guid>();
        for (var i = 1; i <= 6; i++)
        {
            var t = NewTicket(i);
            Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, (await RoundRobinAsync(f, t)).Outcome);
            owners.Add(t.CurrentOwnerEmployeeId!.Value);
        }

        // Every employee gets exactly two of six, and no employee gets two in a row.
        Assert.All(new[] { a, b, c }, id => Assert.Equal(2, owners.Count(o => o == id)));
        for (var i = 1; i < owners.Count; i++)
        {
            Assert.NotEqual(owners[i - 1], owners[i]);
        }

        Assert.Equal(6, f.Audit.Written.Count(w => w.Action == "AutoAssign" && w.ActorEmployeeId is null));
    }

    [Fact]
    public async Task RoundRobin_CursorIsPersisted_ASecondServiceInstanceContinuesTheRotation()
    {
        var rotation = new FakeRotationRepository();
        var members = new FakeUserDepartmentAssignmentRepository();
        AddMember(members);
        AddMember(members);

        var first = CreateRr(rotation, members);
        var t1 = NewTicket(1);
        await RoundRobinAsync(first, t1);

        // "Application restart": a new service over the same persisted cursor.
        var second = CreateRr(rotation, members);
        var t2 = NewTicket(2);
        await RoundRobinAsync(second, t2);

        Assert.NotEqual(t1.CurrentOwnerEmployeeId, t2.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task RoundRobin_NoEligibleEmployee_LeavesTicketUnassignedInTheDepartmentQueue_Audited()
    {
        var f = CreateRr();
        var ticket = NewTicket(1);

        var result = await RoundRobinAsync(f, ticket);

        Assert.Equal(AutoAssignmentOutcome.NoEligibleEmployee, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.Assignments.Added);
        var audit = Assert.Single(f.Audit.Written, w => w.Action == "AutoAssign");
        Assert.Null(audit.ActorEmployeeId);
    }

    [Fact]
    public async Task RoundRobin_NeverAssignsAnEmployeeOfAnotherDepartment()
    {
        var f = CreateRr();
        AddMember(f.Members, departmentId: 99);
        var ticket = NewTicket(1);

        Assert.Equal(AutoAssignmentOutcome.NoEligibleEmployee, (await RoundRobinAsync(f, ticket)).Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task RoundRobin_ADecidedRuleOutcomeIsNotOverridden()
    {
        var f = CreateRr();
        AddMember(f.Members);
        var ticket = NewTicket(1);

        var decided = new AutoAssignmentResult(AutoAssignmentOutcome.DepartmentQueueByRule);
        var result = await f.Service.ApplyCategoryRoutingAsync(ticket, decided, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.DepartmentQueueByRule, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public void PickNext_IsAStableCycle_AndRestartsWhenTheCursorEmployeeLeft()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).OrderBy(g => g).ToList();

        Assert.Equal(ids[0], DepartmentAssignmentRotation.PickNext(null, ids));
        Assert.Equal(ids[1], DepartmentAssignmentRotation.PickNext(ids[0], ids));
        Assert.Equal(ids[0], DepartmentAssignmentRotation.PickNext(ids[3], ids));
        Assert.Equal(ids[2], DepartmentAssignmentRotation.PickNext(ids[1], ids.Where(i => i != ids[1]).ToList()));
        Assert.Null(DepartmentAssignmentRotation.PickNext(ids[0], []));
    }

    [Fact]
    public async Task RoundRobin_PersistsCursorThroughTheRealRepository()
    {
        await using var db = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repo = new AssignmentRotationRepository(db);

        var row = await repo.GetForUpdateAsync(DepartmentId, Now);
        var employee = Guid.NewGuid();
        row.Advance(employee, Now);
        await db.SaveChangesAsync();

        var reloaded = await new AssignmentRotationRepository(db).GetForUpdateAsync(DepartmentId, Now);
        Assert.Equal(employee, reloaded.LastAssignedEmployeeId);
        Assert.Single(db.DepartmentAssignmentRotations);
    }

    // ---- Scenario 3: Call Center selects Collection -> Department Collection -> next employee in rotation ----

    [Fact]
    public async Task CallCenterSelectsCollectionCategory_RoutesToCollection_AndAssignsNextInRotation()
    {
        var tickets = new FakeTicketRepository();
        var categories = new FakeCategoryRepository();
        var departments = new FakeDepartmentRepository();
        var members = new FakeUserDepartmentAssignmentRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var events = new FakeTicketWorkflowEventRepository();
        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var callCenter = departments.AddDepartment("Call Center", "CC");
        var collections = departments.AddDepartment("Collections", "COL");
        var first = AddMember(members, collections.DepartmentId);
        var second = AddMember(members, collections.DepartmentId);
        var category = categories.Seed(collections.DepartmentId, "Collection");

        var auto = new TicketAutoAssignmentService(
            new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(),
            members, new FakeTicketAssignmentRepository(), audit, new FakeRotationRepository());
        var service = new TicketClassificationAppService(
            tickets, categories, new FakePriorityRepository(), new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(),
            members, statusHistory, unitOfWork, audit, sla.DueDates, new FakeTimeProvider(Now),
            handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(),
            departmentRepository: departments, autoAssignmentService: auto, workflowEventRepository: events);

        var owners = new List<Guid?>();
        for (var i = 1; i <= 2; i++)
        {
            var ticket = Ticket.CreateUnclassified($"TG-CC-20261031-{i:D4}", callCenter.DepartmentId, "Customer asks about payment", Now);
            await tickets.AddAsync(ticket);

            var result = await service.ClassifyAsync(
                Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId,
                new ClassifyTicketRequestDto(category.CategoryId, (byte)PriorityLevel.High, null, RowVersion: [1, 2, 3, 4, 5, 6, 7, 8]));

            Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
            Assert.Equal(collections.DepartmentId, ticket.CurrentDepartmentId);
            Assert.Equal(callCenter.DepartmentId, ticket.OriginatingDepartmentId);
            owners.Add(ticket.CurrentOwnerEmployeeId);
        }

        Assert.Equivalent(new[] { first, second }, owners.Cast<Guid>().ToArray());
        Assert.NotEqual(owners[0], owners[1]);
        Assert.Contains(audit.Written, w => w.Action == "Transfer");
        Assert.Contains(audit.Written, w => w.Action == "AutoAssign" && w.ActorEmployeeId is null);
        Assert.Equal(2, events.All.Count);
    }

    [Fact]
    public async Task CategoryOfAnotherDepartment_StillRejectedOutsideTheCallCenter()
    {
        var tickets = new FakeTicketRepository();
        var categories = new FakeCategoryRepository();
        var departments = new FakeDepartmentRepository();
        var members = new FakeUserDepartmentAssignmentRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var cs = departments.AddDepartment("Customer Service", "CS");
        var collections = departments.AddDepartment("Collections", "COL");
        var category = categories.Seed(collections.DepartmentId, "Collection");
        var auto = new TicketAutoAssignmentService(
            new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(),
            members, new FakeTicketAssignmentRepository(), audit, new FakeRotationRepository());
        var service = new TicketClassificationAppService(
            tickets, categories, new FakePriorityRepository(), new FakeRequestTypeRepository(), new FakeWorkflowTemplateRepository(),
            members, statusHistory, unitOfWork, audit, sla.DueDates, new FakeTimeProvider(Now),
            handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(),
            departmentRepository: departments, autoAssignmentService: auto);

        var ticket = Ticket.CreateUnclassified("TG-CS-20261031-0001", cs.DepartmentId, "Question", Now);
        await tickets.AddAsync(ticket);

        var result = await service.ClassifyAsync(
            Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId,
            new ClassifyTicketRequestDto(category.CategoryId, (byte)PriorityLevel.High, null, RowVersion: [1, 2, 3, 4, 5, 6, 7, 8]));

        Assert.Equal(TicketMutationOutcome.CategoryDepartmentMismatch, result.Outcome);
        Assert.Equal(cs.DepartmentId, ticket.CurrentDepartmentId);
    }
}
