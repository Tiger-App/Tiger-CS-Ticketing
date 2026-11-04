using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Collections.TowerAssignments;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Collections;

/// <summary>
/// Collections tower assignments and ticket routing, end to end over the real services with in-memory fakes: the monthly mapping, the automatic assignment when a
/// ticket is transferred from the Call Center, manual assign / reassign by the Collections team, and the history both leave behind.
/// </summary>
public class CollectionsTowerAssignmentTests
{
    private static readonly DateTime November = new(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime December = new(2026, 12, 10, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public FakeTicketRepository Tickets = new();
        public FakeTicketAssignmentRepository TicketAssignments = new();
        public FakeUserDepartmentAssignmentRepository Members = new();
        public FakeDepartmentRepository Departments = new();
        public FakeAuditEntryWriter Audit = new();
        public FakeTicketingUnitOfWork UnitOfWork = new();
        public FakeEmployeeRepository Employees = new();
        public FakeCollectionsTowerAssignmentRepository TowerRows = new();
        public FakeTicketAssignmentHistoryRepository History = new();
        public FakeCollectionsTowerCatalog Catalog = new();
        public FakeCollectionsCrmProjectMap ProjectMap = new();
        public FixedTimeProvider Clock = new(November);
        public Department CallCenter = null!, Collections = null!, Maintenance = null!;
        public Guid Manager = Guid.NewGuid();   // Collections department head
        public Guid Alice = Guid.NewGuid();     // Collections employee
        public Guid Bob = Guid.NewGuid();       // Collections employee
        public Guid Outsider = Guid.NewGuid();  // Maintenance employee
        public Guid CsAgent = Guid.NewGuid();
        public TicketAssignmentAppService Assignments = null!;
        public CollectionsTowerAssignmentAppService Towers = null!;
        public CollectionsTicketRoutingService Routing = null!;
        public CollectionsTeam Team = null!;
        public CollectionsOptions Options = new();
        public TicketAutoAssignmentService AutoAssignment = null!;

        public static Fixture Create()
        {
            var f = new Fixture();
            f.CallCenter = f.Departments.AddDepartment("Call Center", "CC");
            f.Collections = f.Departments.AddDepartment("Collections", "COL");
            f.Maintenance = f.Departments.AddDepartment("Maintenance", "MNT");
            f.AddMember(f.Manager, "Collections Manager", f.Collections.DepartmentId);
            f.AddMember(f.Alice, "Alice", f.Collections.DepartmentId);
            f.AddMember(f.Bob, "Bob", f.Collections.DepartmentId);
            f.AddMember(f.Outsider, "Outsider", f.Maintenance.DepartmentId);
            f.AddMember(f.CsAgent, "CS Agent", f.CallCenter.DepartmentId);

            f.Catalog.Towers.Add(new CollectionsTowerDto(1, "140", "Tiger Tower 140", 4, true));
            f.Catalog.Towers.Add(new CollectionsTowerDto(2, "141", "Tiger Tower 141", 4, true));
            f.Catalog.Towers.Add(new CollectionsTowerDto(3, "142", "Tiger Tower 142", 4, true));
            f.ProjectMap.Mappings[79] = [new CrmProjectTowerMapping(79, "TP140", 4, "Manual")];
            f.ProjectMap.Mappings[80] = [new CrmProjectTowerMapping(80, "TP141", 4, "Manual")];

            var workflowSettings = new FakeDepartmentWorkflowSettingsRepository();
            f.Team = new CollectionsTeam(f.Options, f.Departments, f.Members);
            var resolver = new CollectionsTicketTowerResolver(f.ProjectMap, f.Catalog);
            f.Routing = new CollectionsTicketRoutingService(f.Team, resolver, f.TowerRows, f.TicketAssignments, f.History, f.Audit, f.Options);
            var auto = new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), workflowSettings, f.Members, f.TicketAssignments, f.Audit, departmentRepository: f.Departments, collectionsOptions: f.Options);
            f.AutoAssignment = auto;
            f.Assignments = new TicketAssignmentAppService(
                f.Tickets, f.TicketAssignments, f.Members, f.Departments, f.UnitOfWork, f.Audit, f.Clock, workflowSettings, auto,
                collectionsTeam: f.Team, collectionsRouting: f.Routing, collectionsTowerResolver: resolver, assignmentHistory: f.History);
            f.Towers = new CollectionsTowerAssignmentAppService(f.Team, f.TowerRows, f.Catalog, f.Employees, f.UnitOfWork, f.Audit, f.Clock);
            return f;
        }

        private void AddMember(Guid id, string name, int departmentId)
        {
            var employee = new Employee(id, name, false, DateTime.UtcNow);
            Employees.Add(employee);
            var assignment = new UserDepartmentAssignment(id, departmentId, true, DateTime.UtcNow, null);
            typeof(UserDepartmentAssignment).GetProperty(nameof(UserDepartmentAssignment.Employee))!.SetValue(assignment, employee);
            Members.Assignments.Add(assignment);
        }

        public void Deactivate(Guid id) => Employees.GetByIdAsync(id).Result!.Deactivate(DateTime.UtcNow);

        public async Task<Ticket> CallCenterTicketAsync(int crmProjectId = 79, string unit = "1001", Guid? owner = null)
        {
            var ticket = Ticket.CreateVerifiedFromCrmBuyer(
                $"TG-{Guid.NewGuid():N}"[..14], CallCenter.DepartmentId, 1, 2, 3, crmProjectId, "Customer", "Project", unit, 5, (byte)PriorityLevel.Medium, "Payment query", November);
            await Tickets.AddAsync(ticket);
            if (owner is { } o) ticket.AssignTo(o);
            return ticket;
        }

        public Task<TicketMutationResult> TransferToCollectionsAsync(Ticket ticket) =>
            Assignments.TransferAsync(Manager, [Roles.CsManager], ticket.TicketId, new TransferTicketRequestDto(Collections.DepartmentId, "Payment follow-up", []));

        public Task<TowerSaveResult> ActivateAsync(Guid employee, int year, int month, params int[] towerIds) =>
            Towers.SaveEmployeeTowersAsync(Manager, [Roles.DepartmentHead], employee, new SaveEmployeeTowersRequest(year, month, towerIds, Activate: true))
                .ContinueWith(t => new TowerSaveResult(t.Result.Outcome));
    }

    private sealed record TowerSaveResult(CollectionsOutcome Outcome);

    // 1 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task OneCollector_CanHoldSeveralTowers_AndTheMonthListsThemAll()
    {
        var f = Fixture.Create();
        var saved = await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Alice, new(2026, 11, [1, 2, 3], Activate: true));

        Assert.True(saved.IsSuccess, saved.Detail);
        var alice = saved.Value!.Collectors.Single(c => c.EmployeeId == f.Alice);
        Assert.Equal(["140", "141", "142"], alice.Towers.Select(t => t.TowerCode));
        Assert.All(alice.Towers, t => Assert.Equal("Active", t.Status));
        Assert.Empty(saved.Value.UnassignedTowerIds);
        Assert.Equal(3, f.TowerRows.History.Count(h => h.Action == TowerAssignmentAction.Added));
        Assert.Equal(3, f.TowerRows.History.Count(h => h.Action == TowerAssignmentAction.Activated));
    }

    // 2 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task MonthlyChange_RoutesByTheMonthOfTheTransfer_AndKeepsTheEarlierMonth()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        await f.ActivateAsync(f.Bob, 2026, 12, 1);

        var novemberTicket = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(novemberTicket);
        f.Clock.Now = December;
        var decemberTicket = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(decemberTicket);

        Assert.Equal(f.Alice, novemberTicket.CurrentOwnerEmployeeId);
        Assert.Equal(f.Bob, decemberTicket.CurrentOwnerEmployeeId);
        Assert.Contains(f.TowerRows.Rows, r => r.Year == 2026 && r.Month == 11 && r.EmployeeId == f.Alice);   // November is untouched
    }

    [Fact]
    public async Task ChangingTheMapping_DoesNotReassignTicketsThatAreAlreadyInCollections()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(ticket);

        await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Bob, new(2026, 11, [1], Activate: true, MoveFromOthers: true));

        Assert.Equal(f.Alice, ticket.CurrentOwnerEmployeeId);
        Assert.Contains(f.TowerRows.History, h => h.Action == TowerAssignmentAction.Reassigned && h.PreviousEmployeeId == f.Alice && h.NewEmployeeId == f.Bob);
    }

    // 3 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task CustomerWithUnitsInTwoTowers_IsRoutedByTheUnitOfEachRequest()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        await f.ActivateAsync(f.Bob, 2026, 11, 2);

        var inTower140 = await f.CallCenterTicketAsync(crmProjectId: 79, unit: "1001");
        var inTower141 = await f.CallCenterTicketAsync(crmProjectId: 80, unit: "502");
        await f.TransferToCollectionsAsync(inTower140);
        await f.TransferToCollectionsAsync(inTower141);

        Assert.Equal(f.Alice, inTower140.CurrentOwnerEmployeeId);
        Assert.Equal(f.Bob, inTower141.CurrentOwnerEmployeeId);
        var entry = f.History.Entries.Single(e => e.TicketId == inTower141.TicketId);
        Assert.Equal("141", entry.TowerCode);
        Assert.Equal("502", entry.UnitNumber);
    }

    // 4, 5 ----------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task CallCenterTransfer_AssignsTheTowerCollector_RecordsHistory_AndKeepsStatusPriorityAndSla()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);
        var (status, priority, sla) = (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState);

        var result = await f.TransferToCollectionsAsync(ticket);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Collections.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(f.Alice, ticket.CurrentOwnerEmployeeId);
        Assert.Equal((status, priority, sla), (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState));
        Assert.Equal(f.Alice, f.TicketAssignments.Added.Single(a => a.IsCurrent).AssignedEmployeeId);
        var entry = Assert.Single(f.History.Entries);
        Assert.Equal((TicketAssignmentKind.Automatic, f.CsAgent, f.Alice, (Guid?)null), (entry.Kind, entry.PreviousEmployeeId, entry.NewEmployeeId, entry.ChangedByEmployeeId));
        Assert.Equal(f.Collections.DepartmentId, entry.DepartmentId);
        Assert.Equal(November, entry.ChangedAtUtc);                      // when
        Assert.Contains(f.Audit.Entries, a => a.Action == "AutoAssign" && a.AfterValue!.Contains("Mode=CollectionsTower"));
    }

    // 6 -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task NoMapping_LeavesTheTicketInTheCollectionsQueue_AndDoesNotGuess()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);                 // tower 140 only; tower 141 has nobody
        var noCollector = await f.CallCenterTicketAsync(crmProjectId: 80);
        var unmappedProject = await f.CallCenterTicketAsync(crmProjectId: 999);

        await f.TransferToCollectionsAsync(noCollector);
        await f.TransferToCollectionsAsync(unmappedProject);

        Assert.All(new[] { noCollector, unmappedProject }, t =>
        {
            Assert.Equal(f.Collections.DepartmentId, t.CurrentDepartmentId);
            Assert.Null(t.CurrentOwnerEmployeeId);
        });
        Assert.Empty(f.History.Entries);
        Assert.Equal(2, f.Audit.Entries.Count(a => a.Action == "CollectionsAutoAssignSkipped"));
    }

    [Fact]
    public async Task ADraftAssignment_IsNotUsedForRouting_UntilItIsActivated()
    {
        var f = Fixture.Create();
        await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Alice, new(2026, 11, [1], Activate: false));
        var first = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(first);
        Assert.Null(first.CurrentOwnerEmployeeId);

        await f.Towers.ActivateAsync(f.Manager, [Roles.DepartmentHead], new(2026, 11));
        var second = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(second);
        Assert.Equal(f.Alice, second.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ATicketWithoutAVerifiedProjectAndUnit_IsNotRoutedByName_OrByATypedUnit()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var typed = Ticket.CreateUnverified(
            "TG-MANUAL-0001", f.CallCenter.DepartmentId, 5, (byte)PriorityLevel.Medium, "Payment query", November, manualProjectName: "Tiger Tower 140", manualUnitNumber: "TP140-1001");
        var pact = Ticket.CreateFromExternalLookup(
            "TG-PACT-0001", f.CallCenter.DepartmentId, "Pact", "T-77", "U-9", "Tiger Tower 140", "TP140-1001", 5, (byte)PriorityLevel.Medium, "Payment query", November);
        await f.Tickets.AddAsync(typed);
        await f.Tickets.AddAsync(pact);
        typed.AssignTo(f.CsAgent);

        await f.TransferToCollectionsAsync(typed);
        await f.TransferToCollectionsAsync(pact);

        Assert.Null(typed.CurrentOwnerEmployeeId);                       // not routed, and not left with the Call Center agent either
        Assert.Null(pact.CurrentOwnerEmployeeId);
        Assert.Empty(f.History.Entries);
    }

    [Fact]
    public async Task NoValidTowerAssignment_ReleasesTheCallCenterAgent_ToTheCollectionsQueue()
    {
        var f = Fixture.Create();                                         // nobody holds any tower
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);
        var (status, priority, sla) = (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState);

        var result = await f.TransferToCollectionsAsync(ticket);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Collections.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                       // in the Collections queue, not with the Call Center agent
        Assert.Empty(f.TicketAssignments.Added);                          // (the agent's own row is superseded; no new owner row exists)
        Assert.Equal((status, priority, sla), (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState));
        Assert.Contains(f.Audit.Entries, a => a.Action == "CollectionsAutoAssignSkipped" && a.BeforeValue == f.CsAgent.ToString() && a.AfterValue!.Contains("ReleasedToCollectionsQueue"));
        Assert.Contains(f.Audit.Entries, a => a.Action == "Transfer");   // the transfer audit is unchanged
    }

    [Fact]
    public async Task AnUnmappedProject_ReleasesTheAgent_AndALaterManualAssignmentStillWorks()
    {
        var f = Fixture.Create();
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 999, owner: f.CsAgent);
        await f.TransferToCollectionsAsync(ticket);
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        var result = await f.Assignments.AssignAsync(f.Alice, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Bob, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Bob, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task EnteringCollectionsFromAnotherDepartment_KeepsItsOwner_AndRoutesAnUnownedTicketByTower()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var owned = await f.CallCenterTicketAsync(owner: f.Outsider);
        var unowned = await f.CallCenterTicketAsync();
        foreach (var t in new[] { owned, unowned }) t.TransferToDepartment(f.Collections.DepartmentId, preserveOwner: true);

        var keep = await f.Routing.RouteAsync(owned, departmentEntry: true, fromCallCenter: false, November, Guid.NewGuid());
        var route = await f.Routing.RouteAsync(unowned, departmentEntry: true, fromCallCenter: false, November, Guid.NewGuid());

        Assert.Equal(CollectionsRoutingOutcome.OwnerKept, keep.Outcome);
        Assert.Equal(f.Outsider, owned.CurrentOwnerEmployeeId);
        Assert.Equal(CollectionsRoutingOutcome.Assigned, route.Outcome);
        Assert.Equal(f.Alice, unowned.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task TheCsAgentRotation_NeverGivesACollectionsTicketToACallCenterAgent_ButStillWorksElsewhere()
    {
        var f = Fixture.Create();
        var collectionsTicket = await f.CallCenterTicketAsync();
        collectionsTicket.TransferToDepartment(f.Collections.DepartmentId);

        var result = await f.AutoAssignment.EnsureCsAgentAsync(collectionsTicket, null, AutoAssignmentTrigger.Backfill, November, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.NotApplicable, result.Outcome);
        Assert.Null(collectionsTicket.CurrentOwnerEmployeeId);
        Assert.Empty(f.TicketAssignments.Added);
    }

    [Fact]
    public async Task ACollectorWhoLeftTheTeam_IsNotAssigned()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        f.Deactivate(f.Alice);
        var ticket = await f.CallCenterTicketAsync();

        await f.TransferToCollectionsAsync(ticket);

        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    // 7, 8 ----------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task CollectionsEmployee_CanReassignToAnotherCollector_RegardlessOfTowerOwnership_AndTheRoutingNeverOverwritesIt()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);                 // Alice owns tower 140
        var ticket = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(ticket);
        Assert.Equal(f.Alice, ticket.CurrentOwnerEmployeeId);

        var result = await f.Assignments.AssignAsync(f.Alice, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Bob, [], Reason: "Alice is on leave"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Bob, ticket.CurrentOwnerEmployeeId);          // Bob does not own the tower: manual assignment overrides it
        var manual = f.History.Entries.Last();
        Assert.Equal((TicketAssignmentKind.Manual, f.Alice, f.Bob, (Guid?)f.Alice, "Alice is on leave"), (manual.Kind, manual.PreviousEmployeeId, manual.NewEmployeeId, manual.ChangedByEmployeeId, manual.Reason));
        Assert.Equal("140", manual.TowerCode);
        Assert.Equal(November, manual.ChangedAtUtc);

        // A later automatic attempt while the ticket stays in Collections leaves the manual choice alone.
        var retry = await f.Routing.RouteAsync(ticket, departmentEntry: false, fromCallCenter: false, November, Guid.NewGuid());
        Assert.Equal(CollectionsRoutingOutcome.ManualAssignmentKept, retry.Outcome);
        Assert.Equal(f.Bob, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task CollectionsEmployee_CanAssignAnUnassignedTicket_WithoutAReason()
    {
        var f = Fixture.Create();
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 999);
        await f.TransferToCollectionsAsync(ticket);               // no mapping: stays in the queue

        var result = await f.Assignments.AssignAsync(f.Bob, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Alice, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Alice, ticket.CurrentOwnerEmployeeId);
        Assert.Null(f.History.Entries.Single().Reason);
    }

    [Fact]
    public async Task CollectionsManager_CanReassign_AndMayLeaveTheReasonEmpty()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(ticket);

        var result = await f.Assignments.AssignAsync(f.Manager, [Roles.DepartmentHead], ticket.TicketId, new AssignTicketRequestDto(f.Bob, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Bob, ticket.CurrentOwnerEmployeeId);
    }

    // 9 -------------------------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("outsider-department-employee")]
    [InlineData("call-center-agent")]
    [InlineData("outsider-department-head")]
    public async Task UsersOutsideCollections_CannotAssignACollectionsTicket(string who)
    {
        var f = Fixture.Create();
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 999);
        await f.TransferToCollectionsAsync(ticket);
        var (caller, role) = who switch
        {
            "call-center-agent" => (f.CsAgent, Roles.CsAgent),
            "outsider-department-head" => (f.Outsider, Roles.DepartmentHead),
            _ => (f.Outsider, Roles.DepartmentEmployee)
        };

        var result = await f.Assignments.AssignAsync(caller, [role], ticket.TicketId, new AssignTicketRequestDto(f.Alice, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.History.Entries);
    }

    [Fact]
    public async Task ACollectionsAssignment_CanOnlyGoToAnActiveCollectionsMember()
    {
        var f = Fixture.Create();
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 999);
        await f.TransferToCollectionsAsync(ticket);

        var toOutsider = await f.Assignments.AssignAsync(f.Alice, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Outsider, []));
        f.Deactivate(f.Bob);
        var toInactive = await f.Assignments.AssignAsync(f.Alice, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Bob, []));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, toOutsider.Outcome);
        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, toInactive.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ACollectionsEmployee_GainsNothingOnTicketsOfOtherDepartments()
    {
        var f = Fixture.Create();
        var ticket = Ticket.CreateVerified("TG-MNT-0001", f.Maintenance.DepartmentId, 10, 20, 5, (byte)PriorityLevel.High, "AC", November);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Assignments.AssignAsync(f.Alice, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Outsider, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task OnlyTheCollectionsManager_CanChangeTowerAssignments()
    {
        var f = Fixture.Create();

        var byEmployee = await f.Towers.SaveEmployeeTowersAsync(f.Alice, [Roles.DepartmentEmployee], f.Alice, new(2026, 11, [1], Activate: true));
        var byOutsiderHead = await f.Towers.SaveEmployeeTowersAsync(f.Outsider, [Roles.DepartmentHead], f.Alice, new(2026, 11, [1], Activate: true));
        var byCsAgent = await f.Towers.SaveEmployeeTowersAsync(f.CsAgent, [Roles.CsAgent], f.Alice, new(2026, 11, [1], Activate: true));
        var readByEmployee = await f.Towers.GetMonthAsync(f.Alice, [Roles.DepartmentEmployee], 2026, 11);
        var readByOutsider = await f.Towers.GetMonthAsync(f.Outsider, [Roles.DepartmentEmployee], 2026, 11);

        Assert.All(new[] { byEmployee.Outcome, byOutsiderHead.Outcome, byCsAgent.Outcome, readByOutsider.Outcome }, o => Assert.Equal(CollectionsOutcome.Forbidden, o));
        Assert.True(readByEmployee.IsSuccess);
        Assert.False(readByEmployee.Value!.CanManage);
        Assert.Empty(f.TowerRows.Rows);
    }

    // tower page rules ----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATowerCannotHaveTwoCollectorsInTheSameMonth_UnlessMovedExplicitly()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);

        var clash = await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Bob, new(2026, 11, [1, 2], Activate: true));

        Assert.Equal(CollectionsOutcome.Conflict, clash.Outcome);
        Assert.Single(f.TowerRows.Rows);                              // nothing was changed, not even tower 2
        Assert.Equal(f.Alice, f.TowerRows.Rows[0].EmployeeId);
    }

    [Fact]
    public async Task ARaceOnTheUniqueIndex_IsReportedAsAConflict()
    {
        var f = Fixture.Create();
        f.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Alice, new(2026, 11, [1], Activate: true));

        Assert.Equal(CollectionsOutcome.Conflict, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task OnlyActiveCollectionsEmployees_CanBeGivenTowers()
    {
        var f = Fixture.Create();

        var outsider = await f.ActivateAsync(f.Outsider, 2026, 11, 1);
        var agent = await f.ActivateAsync(f.CsAgent, 2026, 11, 1);
        f.Deactivate(f.Bob);
        var inactive = await f.ActivateAsync(f.Bob, 2026, 11, 1);
        var unknownTower = await f.ActivateAsync(f.Alice, 2026, 11, 77);

        Assert.All(new[] { outsider, agent, inactive, unknownTower }, r => Assert.Equal(CollectionsOutcome.InvalidRequest, r.Outcome));
        Assert.Empty(f.TowerRows.Rows);
    }

    [Fact]
    public async Task EditingSavesTheCompleteSet_RemovedTowersAreDeletedFromTheMonthButKeptInHistory()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1, 2);

        await f.ActivateAsync(f.Alice, 2026, 11, 2, 3);

        Assert.Equal([2, 3], f.TowerRows.Rows.Select(r => r.TowerId).Order());
        Assert.Contains(f.TowerRows.History, h => h.Action == TowerAssignmentAction.Removed && h.TowerId == 1 && h.PreviousEmployeeId == f.Alice);
    }

    [Fact]
    public async Task CopyPreviousMonth_CreatesDrafts_SkipsAssignedTowersAndUnavailableCollectors()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 10, 1, 3);
        await f.ActivateAsync(f.Bob, 2026, 10, 2);
        f.Deactivate(f.Bob);
        await f.ActivateAsync(f.Alice, 2026, 11, 3);                  // already assigned this month

        var copied = await f.Towers.CopyPreviousMonthAsync(f.Manager, [Roles.DepartmentHead], new(2026, 11));

        Assert.Equal((1, 1, 1), (copied.Value!.Copied, copied.Value.SkippedAlreadyAssigned, copied.Value.SkippedUnavailable));
        var draft = Assert.Single(f.TowerRows.Rows, r => r.Year == 2026 && r.Month == 11 && r.TowerId == 1);
        Assert.Equal((f.Alice, TowerAssignmentStatus.Draft), (draft.EmployeeId, draft.Status));
        Assert.Equal(3, f.TowerRows.Rows.Count(r => r.Month == 10));  // October is untouched
    }

    // 10 ------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task TowerHistory_RecordsAddedReassignedRemovedAndActivated_WithWhoAndWhen()
    {
        var f = Fixture.Create();
        await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Alice, new(2026, 11, [1, 2]));          // draft
        await f.Towers.ActivateAsync(f.Manager, [Roles.DepartmentHead], new(2026, 11));
        await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Bob, new(2026, 11, [2], Activate: true, MoveFromOthers: true));
        await f.Towers.SaveEmployeeTowersAsync(f.Manager, [Roles.DepartmentHead], f.Alice, new(2026, 11, [], Activate: true));

        var history = await f.Towers.ListHistoryAsync(f.Manager, [Roles.DepartmentHead], 2026, 11, null);

        var actions = history.Value!.Select(h => h.Action).ToList();
        Assert.Contains("Added", actions);
        Assert.Contains("Activated", actions);
        Assert.Contains("Reassigned", actions);
        Assert.Contains("Removed", actions);
        Assert.All(history.Value!, h => Assert.Equal(f.Manager, h.ChangedByEmployeeId));
        Assert.Contains(history.Value!, h => h.Action == "Reassigned" && h.PreviousEmployeeName == "Alice" && h.NewEmployeeName == "Bob");
    }

    [Fact]
    public async Task TicketAssignmentHistory_KeepsEveryEventInOrder()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync();
        await f.TransferToCollectionsAsync(ticket);
        await f.Assignments.AssignAsync(f.Alice, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Bob, [], Reason: "Leave"));
        await f.Assignments.AssignAsync(f.Bob, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Manager, [], Reason: "Escalated"));

        var kinds = f.History.Entries.Select(e => (e.Kind, e.PreviousEmployeeId, e.NewEmployeeId)).ToList();

        Assert.Equal(
            [(TicketAssignmentKind.Automatic, (Guid?)null, f.Alice), (TicketAssignmentKind.Manual, f.Alice, f.Bob), (TicketAssignmentKind.Manual, f.Bob, f.Manager)],
            kinds);
        Assert.Equal(3, f.TicketAssignments.Added.Count);               // the ownership rows are kept too; only the last is current
        Assert.Single(f.TicketAssignments.Added, a => a.IsCurrent);
    }

    // 11 ------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task TransfersToOtherDepartments_AreUnaffected()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var result = await f.Assignments.TransferAsync(
            f.Manager, [Roles.CsManager], ticket.TicketId, new TransferTicketRequestDto(f.Maintenance.DepartmentId, "Needs maintenance", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Maintenance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(f.CsAgent, ticket.CurrentOwnerEmployeeId);        // the existing rule: the CS agent is kept
        Assert.Empty(f.History.Entries);
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action is "AutoAssign" or "CollectionsAutoAssignSkipped");
    }

    [Fact]
    public async Task AnExplicitAssigneeChosenOnTransfer_IsAManualAssignment_AndTowerRoutingDoesNotRun()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync();

        var result = await f.Assignments.TransferAsync(
            f.Manager, [Roles.CsManager], ticket.TicketId, new TransferTicketRequestDto(f.Collections.DepartmentId, "Chosen by hand", [], AssignToEmployeeId: f.Bob));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Bob, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketAssignmentKind.Manual, Assert.Single(f.History.Entries).Kind);
    }

    [Fact]
    public async Task ManualAssignment_OfATicketOutsideCollections_StaysUnchanged_AndWritesNoCollectionsHistory()
    {
        var f = Fixture.Create();
        var ticket = Ticket.CreateVerified("TG-MNT-0002", f.Maintenance.DepartmentId, 10, 20, 5, (byte)PriorityLevel.High, "AC", November);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Assignments.AssignAsync(f.Manager, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(f.Outsider, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Empty(f.History.Entries);
    }
}
