using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
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
        public Department CallCenter = null!, Collections = null!, Maintenance = null!, Registration = null!;
        public FakeUserTransferDepartmentRepository TransferLists = new();
        public FakePermissionOverrideRepository Overrides = new();
        public Guid Manager = Guid.NewGuid();   // Collections department head
        public Guid Alice = Guid.NewGuid();     // Collections employee
        public Guid Bob = Guid.NewGuid();       // Collections employee
        public Guid Outsider = Guid.NewGuid();  // Maintenance employee
        public Guid CsAgent = Guid.NewGuid();
        public TicketAssignmentAppService Assignments = null!;
        public CollectionsTowerAssignmentAppService Towers = null!;
        public CollectionsTicketRoutingService Routing = null!;
        public CollectionsTeam Team = null!;
        public FakeCrmBuyerLookupGateway Crm = new();
        public FakePactLookup Pact = new();
        public FakeTicketInteractionRepository Interactions = new();
        public FakeTicketCollectionsUnitRepository SelectionStore = new();
        public CollectionsTransferSelectionService Selection = null!;
        public CollectionsTransferGuard Guard = null!;
        public CollectionsOptions Options = new();
        public TicketAutoAssignmentService AutoAssignment = null!;

        public static Fixture Create()
        {
            var f = new Fixture();
            f.CallCenter = f.Departments.AddDepartment("Call Center", "CC");
            f.Collections = f.Departments.AddDepartment("Collections", "COL");
            f.Maintenance = f.Departments.AddDepartment("Maintenance", "MNT");
            f.Registration = f.Departments.AddDepartment("Registration", "REG");
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
            f.ProjectMap.Mappings[999] = [];   // (kept unmapped: no row)
            f.ProjectMap.Mappings.Remove(999);
            // The customer (CRM id 1) owns three units: 1001 in tower 140's project, 502 in tower 141's project, and 777 in a project with no tower mapping.
            f.Crm.Returns(CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(new CrmCustomerDto(1, "Customer", null, "0501234567", null), [
                CrmUnit(3, "1001", 79), CrmUnit(4, "502", 80), CrmUnit(5, "777", 999)])]));
            f.Selection = new CollectionsTransferSelectionService(
                new CrmBuyerLookupAppService(f.Crm, NullLogger<CrmBuyerLookupAppService>.Instance), f.Pact, f.ProjectMap, f.Catalog, new FakeIntakeRecordRepository(), f.Interactions, f.SelectionStore);
            f.Guard = new CollectionsTransferGuard(f.Team, f.Selection);
            var resolver = new CollectionsTicketTowerResolver(f.ProjectMap, f.Catalog, f.SelectionStore);
            f.Routing = new CollectionsTicketRoutingService(f.Team, resolver, f.TowerRows, f.TicketAssignments, f.History, f.Audit, f.Options);
            var auto = new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), workflowSettings, f.Members, f.TicketAssignments, f.Audit, departmentRepository: f.Departments, collectionsOptions: f.Options);
            f.AutoAssignment = auto;
            f.Assignments = new TicketAssignmentAppService(
                f.Tickets, f.TicketAssignments, f.Members, f.Departments, f.UnitOfWork, f.Audit, f.Clock, workflowSettings, auto,
                collectionsTeam: f.Team, collectionsRouting: f.Routing, collectionsTowerResolver: resolver, assignmentHistory: f.History, collectionsGuard: f.Guard,
                permissionOverrides: f.Overrides, transferDepartments: f.TransferLists);
            f.Towers = new CollectionsTowerAssignmentAppService(f.Team, f.TowerRows, f.Catalog, f.Employees, f.UnitOfWork, f.Audit, f.Clock);
            return f;
        }

        public static CrmBuyerUnitDto CrmUnit(int unitId, string number, int projectId) =>
            new(1, 1, "Sold", unitId, number, 1, 1, null, projectId, $"Project {projectId}", null, 1, "Buyer");

        public void AddMember(Guid id, string name, int departmentId)
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
            await Interactions.AddAsync(TicketInteraction.CreateLocal(ticket.TicketId, 1, "0501234567", November, isOriginatingInteraction: true));
            if (owner is { } o) ticket.AssignTo(o);
            return ticket;
        }

        /// <summary>Transfers to Collections. With no tower/unit given, the ticket's own verified pair is used (as an API caller or an untouched preselection would).</summary>
        public Task<TicketMutationResult> TransferToCollectionsAsync(Ticket ticket, int? towerId = null, string? unitRef = null) =>
            Assignments.TransferAsync(Manager, [Roles.CsManager], ticket.TicketId,
                new TransferTicketRequestDto(Collections.DepartmentId, "Payment follow-up", [], CollectionsTowerId: towerId, CollectionsUnitRef: unitRef));

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
        var unmappedProject = await f.CallCenterTicketAsync(crmProjectId: 999, owner: f.CsAgent);

        var allowed = await f.TransferToCollectionsAsync(noCollector);
        var unmapped = await f.TransferToCollectionsAsync(unmappedProject);     // an unmapped project is not a verified tower: the Tower is optional, so the ticket waits in the queue

        Assert.Equal(TicketMutationOutcome.Success, allowed.Outcome);
        Assert.Equal(f.Collections.DepartmentId, noCollector.CurrentDepartmentId);
        Assert.Null(noCollector.CurrentOwnerEmployeeId);                       // valid tower/unit, nobody holds it: allowed, left in the Collections queue
        Assert.Equal(TicketMutationOutcome.Success, unmapped.Outcome);
        Assert.Equal(f.Collections.DepartmentId, unmappedProject.CurrentDepartmentId);
        Assert.Null(unmappedProject.CurrentOwnerEmployeeId);                   // released to the queue, never given to somebody by guesswork
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
    public async Task ATicketWithOnlyTypedOrPactText_CannotBeGivenAnUnverifiedTowerAndUnit_AndKeepsItsDepartmentAndAssignee()
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
        pact.AssignTo(f.CsAgent);
        var saves = f.UnitOfWork.SaveChangesCallCount;

        var typedWithIds = await f.TransferToCollectionsAsync(typed, towerId: 1, unitRef: "crm:3");  // typed text verifies nothing: a tower and unit named for it are refused
        var pactWithIds = await f.TransferToCollectionsAsync(pact, towerId: 1, unitRef: "crm:3");    // PACT cannot be asked (no phone on record): nothing is verified

        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitInvalid, typedWithIds.Outcome);
        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitUnavailable, pactWithIds.Outcome);
        Assert.All(new[] { typed, pact }, t =>
        {
            Assert.Equal(f.CallCenter.DepartmentId, t.CurrentDepartmentId);
            Assert.Equal(f.CsAgent, t.CurrentOwnerEmployeeId);
        });
        Assert.Empty(f.History.Entries);
        Assert.Empty(f.TicketAssignments.Added);
        Assert.Equal(saves, f.UnitOfWork.SaveChangesCallCount);             // nothing was written
    }

    // ---- Transfer: the Tower is optional (the five scenarios) ----------------------------------------------------------------------------

    [Fact]
    public async Task Scenario1_ValidTower_IsAssignedAccordingToTheTowerMapping_WhetherChosenOrPreselected()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);                       // tower 140 -> Alice
        await f.ActivateAsync(f.Bob, 2026, 11, 2);                         // tower 141 -> Bob
        var preselected = await f.CallCenterTicketAsync(owner: f.CsAgent);               // the ticket's own verified pair is on tower 140
        var chosen = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var untouched = await f.TransferToCollectionsAsync(preselected);                    // modal preselection left as it is
        var picked = await f.TransferToCollectionsAsync(chosen, towerId: 1, unitRef: "crm:3");

        Assert.All(new[] { untouched, picked }, r => Assert.Equal(TicketMutationOutcome.Success, r.Outcome));
        Assert.Equal(f.Alice, preselected.CurrentOwnerEmployeeId);
        Assert.Equal(f.Alice, chosen.CurrentOwnerEmployeeId);
        Assert.Equal(2, f.Audit.Entries.Count(a => a.Action == "AutoAssign" && a.AfterValue!.Contains("Mode=CollectionsTower")));
    }

    [Fact]
    public async Task Scenario2_UnknownTower_GoesToTheCollectionsQueueUnassigned_AndNeverToAGuessedEmployee()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        await f.ActivateAsync(f.Bob, 2026, 11, 2);
        var typed = Ticket.CreateUnverified(
            "TG-MANUAL-0002", f.CallCenter.DepartmentId, 5, (byte)PriorityLevel.Medium, "Payment query", November, manualProjectName: "Tiger Tower 140", manualUnitNumber: "TP140-1001");
        await f.Tickets.AddAsync(typed);
        typed.AssignTo(f.CsAgent);
        var (status, priority, sla) = (typed.TicketStatus, typed.PriorityId, typed.SlaState);

        var result = await f.TransferToCollectionsAsync(typed);                             // no tower, no unit: nothing identifies one

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Collections.DepartmentId, typed.CurrentDepartmentId);
        Assert.Null(typed.CurrentOwnerEmployeeId);                                          // the queue: not Alice, not Bob, not the Call Center agent
        Assert.DoesNotContain(f.TicketAssignments.Added, a => a.IsCurrent);
        Assert.Empty(f.History.Entries);
        Assert.Equal((status, priority, sla), (typed.TicketStatus, typed.PriorityId, typed.SlaState));
        Assert.Null(await f.SelectionStore.GetAsync(typed.TicketId));                       // nothing invented was stored
        Assert.Equal("Tiger Tower 140", typed.ManualProjectName);                           // what the ticket already carried is untouched
        Assert.Equal("TP140-1001", typed.ManualUnitNumber);
        Assert.Contains(f.Audit.Entries, a => a.Action == "Transfer");
        Assert.Contains(f.Audit.Entries, a => a.Action == "CollectionsAutoAssignSkipped" && a.AfterValue!.Contains("ReleasedToCollectionsQueue"));
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action == "AutoAssign");
    }

    [Fact]
    public async Task Scenario2b_UnknownTower_TheQueuedTicketCanBeAssignedLaterByACollectionsEmployee()
    {
        var f = Fixture.Create();
        var typed = Ticket.CreateUnverified("TG-MANUAL-0003", f.CallCenter.DepartmentId, 5, (byte)PriorityLevel.Medium, "Payment query", November);
        await f.Tickets.AddAsync(typed);
        await f.TransferToCollectionsAsync(typed);
        Assert.Null(typed.CurrentOwnerEmployeeId);

        var later = await f.Assignments.AssignAsync(f.Alice, [Roles.DepartmentEmployee], typed.TicketId, new AssignTicketRequestDto(f.Bob, []));

        Assert.Equal(TicketMutationOutcome.Success, later.Outcome);
        Assert.Equal(f.Bob, typed.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task Scenario3_TowerWithoutAnAssignedEmployee_GoesToTheCollectionsQueueUnassigned_AndKeepsTheSelectedTowerAndUnit()
    {
        var f = Fixture.Create();                                                           // nobody holds any tower
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 80, owner: f.CsAgent);      // a valid tower (141) with no collector

        var result = await f.TransferToCollectionsAsync(ticket, towerId: 2, unitRef: "crm:4");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal((f.Collections.DepartmentId, (Guid?)null), (ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId));
        Assert.Empty(f.History.Entries);
        Assert.Contains(f.Audit.Entries, a => a.Action == "CollectionsAutoAssignSkipped" && a.AfterValue!.Contains("NoActiveAssignment"));
        Assert.Contains(f.Audit.Entries, a => a.Action == "Transfer" && a.AfterValue!.Contains("CollectionsTower=141"));   // the chosen tower stays on the transfer record
    }

    [Fact]
    public async Task Scenario4_OtherDepartments_TransferWithoutTowerOrUnit_AndIgnoreAnyTowerSent()
    {
        var f = Fixture.Create();
        var plain = await f.CallCenterTicketAsync(owner: f.CsAgent);
        var withStray = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var without = await f.Assignments.TransferAsync(f.Manager, [Roles.CsManager], plain.TicketId,
            new TransferTicketRequestDto(f.Maintenance.DepartmentId, "Needs maintenance", []));
        var stray = await f.Assignments.TransferAsync(f.Manager, [Roles.CsManager], withStray.TicketId,
            new TransferTicketRequestDto(f.Maintenance.DepartmentId, "Needs maintenance", [], CollectionsTowerId: 1, CollectionsUnitRef: "crm:3"));

        Assert.Equal(TicketMutationOutcome.Success, without.Outcome);
        Assert.Equal(TicketMutationOutcome.Success, stray.Outcome);
        Assert.Equal(f.Maintenance.DepartmentId, plain.CurrentDepartmentId);
        Assert.Equal(f.Maintenance.DepartmentId, withStray.CurrentDepartmentId);
        Assert.Null(await f.SelectionStore.GetAsync(withStray.TicketId));                   // a tower sent for another department is never stored
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action is "AutoAssign" or "CollectionsAutoAssignSkipped" && a.AfterValue!.Contains("Collections"));
    }

    [Fact]
    public async Task Scenario5_ExistingTicketDataHistoryAndPermissions_AreUnchangedByTheOptionalTower()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);
        var before = (ticket.CrmBuyerCustomerId, ticket.CrmBuyerProjectId, ticket.CrmBuyerUnitId, ticket.CrmBuyerUnitNumber, ticket.OriginatingDepartmentId, ticket.TicketNumber);

        var result = await f.TransferToCollectionsAsync(ticket);
        var entry = Assert.Single(f.History.Entries);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(before, (ticket.CrmBuyerCustomerId, ticket.CrmBuyerProjectId, ticket.CrmBuyerUnitId, ticket.CrmBuyerUnitNumber, ticket.OriginatingDepartmentId, ticket.TicketNumber));
        Assert.Equal((f.CsAgent, f.Alice, TicketAssignmentKind.Automatic), (entry.PreviousEmployeeId, entry.NewEmployeeId, entry.Kind));   // assignment history as before
        Assert.Equal(("140", "1001"), (entry.TowerCode, entry.UnitNumber));
        Assert.Contains(f.Audit.Entries, a => a.Action == "Transfer" && a.AfterValue!.Contains("Reason=Payment follow-up"));

        // Permissions are unchanged: a user with no transfer right is still refused, whatever the Tower says, and nothing is written.
        var other = await f.CallCenterTicketAsync(owner: f.CsAgent);
        var saves = f.UnitOfWork.SaveChangesCallCount;
        var forbidden = await f.Assignments.TransferAsync(f.Outsider, [Roles.DepartmentEmployee], other.TicketId,
            new TransferTicketRequestDto(f.Collections.DepartmentId, "Payment follow-up", []));
        Assert.Equal(TicketMutationOutcome.Forbidden, forbidden.Outcome);
        Assert.Equal((f.CallCenter.DepartmentId, (Guid?)f.CsAgent), (other.CurrentDepartmentId, other.CurrentOwnerEmployeeId));
        Assert.Equal(saves, f.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ATowerWithoutAUnit_OrAnInventedUnit_IsStillRefused()
    {
        var f = Fixture.Create();
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);
        var typed = Ticket.CreateUnverified("TG-MANUAL-0004", f.CallCenter.DepartmentId, 5, (byte)PriorityLevel.Medium, "q", November);
        await f.Tickets.AddAsync(typed);

        var half = await f.TransferToCollectionsAsync(typed, towerId: 1);
        var invented = await f.TransferToCollectionsAsync(typed, towerId: 1, unitRef: "crm:1");

        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitRequired, half.Outcome);
        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitInvalid, invented.Outcome);
        Assert.Equal(f.CallCenter.DepartmentId, typed.CurrentDepartmentId);
        Assert.DoesNotContain(f.Audit.Entries, a => a.Action == "AutoAssign");               // neither a tower rule nor the Round Robin picked anyone for Collections
    }

    [Fact]
    public async Task EitherFieldMissing_RefusesTheTransfer_BeforeAnythingIsWritten()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var saves = f.UnitOfWork.SaveChangesCallCount;
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var towerOnly = await f.TransferToCollectionsAsync(ticket, towerId: 1);
        var unitOnly = await f.TransferToCollectionsAsync(ticket, unitRef: "crm:3");

        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitRequired, towerOnly.Outcome);
        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitRequired, unitOnly.Outcome);
        Assert.Equal((f.CallCenter.DepartmentId, (Guid?)f.CsAgent), (ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId));
        Assert.Equal(saves, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.History.Entries);
    }

    [Fact]
    public async Task ACustomerWithSeveralUnits_ChoosesTheTowerThenTheUnitOfTheRequest_AndIsRoutedByIt()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        await f.ActivateAsync(f.Bob, 2026, 11, 2);
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 79, unit: "1001", owner: f.CsAgent);    // raised on unit 1001 (tower 140)...

        var options = await f.Selection.GetOptionsAsync(ticket);
        var result = await f.TransferToCollectionsAsync(ticket, towerId: 2, unitRef: "crm:4");                    // ...but this request is about unit 502 in tower 141

        Assert.Equal([1, 2], options.Towers.Select(t => t.TowerId));                                       // only verified towers: the unmapped project's unit is not offered
        Assert.Equal(["1001"], options.Towers[0].Units.Select(u => u.UnitNumber));
        Assert.Equal((1, "crm:3"), (options.PreselectedTowerId, options.PreselectedUnitRef));                      // the ticket's own verified pair is preselected
        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Bob, ticket.CurrentOwnerEmployeeId);                                                // the collector of tower 141, not of the ticket's own tower
        var entry = Assert.Single(f.History.Entries);
        Assert.Equal(("141", "502", TicketAssignmentKind.Automatic), (entry.TowerCode, entry.UnitNumber, entry.Kind));
        var stored = Assert.Single(f.SelectionStore.Rows);
        Assert.Equal((2, "crm:4", f.Manager), (stored.TowerId, stored.UnitRef, stored.SelectedByEmployeeId));
        Assert.Contains(f.Audit.Entries, a => a.Action == "Transfer" && a.AfterValue!.Contains("CollectionsTower=141;CollectionsUnit=502"));
    }

    [Fact]
    public async Task ATowerAndUnitThatAreNotTheCustomersVerifiedUnits_AreRefused()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var wrongTower = await f.TransferToCollectionsAsync(ticket, towerId: 1, unitRef: "crm:4");    // unit 502 belongs to tower 141, not 140
        var strangersUnit = await f.TransferToCollectionsAsync(ticket, towerId: 1, unitRef: "crm:99");
        var unmappedProjectUnit = await f.TransferToCollectionsAsync(ticket, towerId: 3, unitRef: "crm:5");   // unit 777's project has no verified tower

        Assert.All(new[] { wrongTower, strangersUnit, unmappedProjectUnit }, r => Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitInvalid, r.Outcome));
        Assert.Equal((f.CallCenter.DepartmentId, (Guid?)f.CsAgent), (ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId));
        Assert.Empty(f.SelectionStore.Rows);
    }

    [Fact]
    public async Task WhenCrmCannotBeRead_TheTicketsOwnVerifiedUnitStillWorks_AndAnotherUnitIsReportedUnavailable()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        f.Crm.Returns(CrmBuyerLookupResult.Unavailable());
        var own = await f.CallCenterTicketAsync(owner: f.CsAgent);
        var other = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var ok = await f.TransferToCollectionsAsync(own);                                      // preselected pair, no CRM needed
        var unavailable = await f.TransferToCollectionsAsync(other, towerId: 2, unitRef: "crm:4");

        Assert.Equal(TicketMutationOutcome.Success, ok.Outcome);
        Assert.Equal(f.Alice, own.CurrentOwnerEmployeeId);
        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitUnavailable, unavailable.Outcome);
        Assert.Equal(f.CallCenter.DepartmentId, other.CurrentDepartmentId);
    }

    private static PactCustomerLookupResult PactCustomer(string tenant, params (string unitId, string code, int company)[] units) =>
        PactCustomerLookupResult.Success([new PactCustomerMatchDto(tenant, "Omar", "0501234567", null, null,
            [.. units.Select(u => new PactContractDto(u.unitId, "C-" + u.unitId, u.code[(u.code.IndexOf('-') + 1)..], "Tiger Tower", null, u.company, null, u.code))])]);

    private static async Task<Ticket> PactTicketAsync(Fixture f, string tenant = "T-77", string unitId = "U-9", Guid? owner = null)
    {
        var ticket = Ticket.CreateFromExternalLookup(
            $"TG-PACT-{Guid.NewGuid():N}"[..14], f.CallCenter.DepartmentId, "Pact", tenant, unitId, "Typed Tower", "typed-unit", 5, (byte)PriorityLevel.Medium, "Payment query", November);
        await f.Tickets.AddAsync(ticket);
        await f.Interactions.AddAsync(TicketInteraction.CreateLocal(ticket.TicketId, 1, "0501234567", November, isOriginatingInteraction: true));
        if (owner is { } o) ticket.AssignTo(o);
        return ticket;
    }

    [Fact]
    public async Task APactVerifiedCustomer_CanBeTransferred_WithTheTowerFromPactsOwnUnitCode()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        await f.ActivateAsync(f.Bob, 2026, 11, 2);
        f.Pact.Result = PactCustomer("T-77", ("U-9", "TP140-1001", 4), ("U-10", "TP141-502", 4));
        var ticket = await PactTicketAsync(f, owner: f.CsAgent);

        var options = await f.Selection.GetOptionsAsync(ticket);
        var viaPreselection = await f.TransferToCollectionsAsync(ticket);

        Assert.Equal([1, 2], options.Towers.Select(t => t.TowerId));
        Assert.Equal((1, "pact:U-9"), (options.PreselectedTowerId, options.PreselectedUnitRef));   // the unit the ticket was verified with
        Assert.All(options.Towers.SelectMany(t => t.Units), u => Assert.Equal("Pact", u.Source));
        Assert.Equal(TicketMutationOutcome.Success, viaPreselection.Outcome);
        Assert.Equal(f.Alice, ticket.CurrentOwnerEmployeeId);                                    // routed by the verified unit, not by the typed "Typed Tower"
        Assert.Equal("pact:U-9", Assert.Single(f.SelectionStore.Rows).UnitRef);                    // kept for routing: a PACT unit is not readable from the ticket itself
    }

    [Fact]
    public async Task APactCustomerWithSeveralUnits_ChoosesTheUnitOfTheRequest()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Bob, 2026, 11, 2);
        f.Pact.Result = PactCustomer("T-77", ("U-9", "TP140-1001", 4), ("U-10", "TP141-502", 4));
        var ticket = await PactTicketAsync(f, owner: f.CsAgent);

        var result = await f.TransferToCollectionsAsync(ticket, towerId: 2, unitRef: "pact:U-10");

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Bob, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(("141", "502", "pact:U-10"), (f.History.Entries.Single().TowerCode, f.History.Entries.Single().UnitNumber, f.SelectionStore.Rows.Single().UnitRef));
    }

    [Fact]
    public async Task PactIsOnlyTrustedWhenItReturnsTheSameCustomerTheTicketWasVerifiedWith()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 1);
        f.Pact.Result = PactCustomer("SOMEONE-ELSE", ("U-9", "TP140-1001", 4));        // the phone now belongs to another tenant
        var ticket = await PactTicketAsync(f, owner: f.CsAgent);
        var saves = f.UnitOfWork.SaveChangesCallCount;

        var chosen = await f.TransferToCollectionsAsync(ticket, towerId: 1, unitRef: "pact:U-9");

        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitUnavailable, chosen.Outcome);   // a unit PACT cannot confirm for this customer is refused
        Assert.Equal((f.CallCenter.DepartmentId, (Guid?)f.CsAgent), (ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId));
        Assert.Equal(saves, f.UnitOfWork.SaveChangesCallCount);
        Assert.Equal("The customer's units could not be read from PACT, so they cannot be verified.", (await f.Selection.GetOptionsAsync(ticket)).Message);

        var withoutTower = await f.TransferToCollectionsAsync(ticket);                          // no tower named: the queue, unassigned
        Assert.Equal(TicketMutationOutcome.Success, withoutTower.Outcome);
        Assert.Equal((f.Collections.DepartmentId, (Guid?)null), (ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId));
    }

    [Fact]
    public async Task APactContractWithoutAUnitCode_IsNotOffered_BecauseItsTowerCannotBeVerified()
    {
        var f = Fixture.Create();
        f.Pact.Result = PactCustomerLookupResult.Success([new PactCustomerMatchDto("T-77", "Omar", null, null, null, [new PactContractDto("U-9", "C-9", "1001", "Tiger Tower", null, 4)])]);
        var ticket = await PactTicketAsync(f, owner: f.CsAgent);

        var options = await f.Selection.GetOptionsAsync(ticket);
        var result = await f.TransferToCollectionsAsync(ticket);

        Assert.Empty(options.Towers);
        Assert.Contains("No verified tower and unit were found", options.Message);
        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);                           // the Tower is optional: no verified tower, so the Collections queue
        Assert.Equal((f.Collections.DepartmentId, (Guid?)null), (ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId));
    }

    [Fact]
    public async Task TheGuard_AppliesToEveryMoveIntoCollections_AndToNoOtherDepartment()
    {
        var f = Fixture.Create();
        var unverified = Ticket.CreateUnverified("TG-X-1", f.Maintenance.DepartmentId, 5, (byte)PriorityLevel.Medium, "q", November);
        var verified = await f.CallCenterTicketAsync();

        var intoCollectionsWithout = await f.Guard.CheckAsync(unverified, f.Collections.DepartmentId, null, null);
        var intoCollectionsVerified = await f.Guard.CheckAsync(verified, f.Collections.DepartmentId, null, null);   // preselection untouched
        var elsewhere = await f.Guard.CheckAsync(unverified, f.Maintenance.DepartmentId, null, null);

        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitRequired, intoCollectionsWithout.Refusal);
        Assert.Null(intoCollectionsVerified.Refusal);
        Assert.Equal(1, intoCollectionsVerified.Unit!.Tower.TowerId);
        Assert.Equal(CollectionsTransferDecision.NotCollections, elsewhere);                                     // other departments never need a tower/unit

        // A Transfer lets the Tower stay empty: allowed, with no unit to store; a half selection is still refused. Reopen keeps the mandatory rule above.
        var optionalEmpty = await f.Guard.CheckAsync(unverified, f.Collections.DepartmentId, null, null, towerOptional: true);
        var optionalHalf = await f.Guard.CheckAsync(unverified, f.Collections.DepartmentId, 1, null, towerOptional: true);
        Assert.Equal(new CollectionsTransferDecision(null, null), optionalEmpty);
        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitRequired, optionalHalf.Refusal);
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
    public async Task AValidTowerWithNoCollector_ReleasesTheAgent_AndALaterManualAssignmentStillWorks()
    {
        var f = Fixture.Create();
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 80, owner: f.CsAgent);
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
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 80);
        await f.TransferToCollectionsAsync(ticket);               // valid tower, nobody holds it: stays in the queue

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
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 80);
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
        var ticket = await f.CallCenterTicketAsync(crmProjectId: 80);
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

        var historyByEmployee = await f.Towers.ListHistoryAsync(f.Alice, [Roles.DepartmentEmployee], 2026, 11, null);
        var readByManager = await f.Towers.GetMonthAsync(f.Manager, [Roles.DepartmentHead], 2026, 11);

        // Reading is as restricted as changing: a plain Collections employee neither reads nor changes the assignments.
        Assert.All(new[] { byEmployee.Outcome, byOutsiderHead.Outcome, byCsAgent.Outcome, readByOutsider.Outcome, readByEmployee.Outcome, historyByEmployee.Outcome },
            o => Assert.Equal(CollectionsOutcome.Forbidden, o));
        Assert.True(readByManager.IsSuccess);
        Assert.True(readByManager.Value!.CanManage);
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

    // ======================================================================================================================================
    // Regression: Ticket Transfer / Reassignment validation and the separated transfer-destination permission
    // ======================================================================================================================================

    private static async Task<Ticket> CollectionsTicketAsync(Fixture f, Guid? owner = null, string number = "TG-COL-20261030-0004")
    {
        var ticket = Ticket.CreateVerified(number, f.Collections.DepartmentId, 10, 20, 5, (byte)PriorityLevel.High, "Instalment query", November);
        await f.Tickets.AddAsync(ticket);
        if (owner is { } o)
        {
            ticket.AssignTo(o);
            await f.TicketAssignments.AddAsync(new TicketAssignment(ticket.TicketId, o, f.Collections.DepartmentId, November, null));
            ticket.ChangeStatus(TicketStatus.InProgress);
        }

        return ticket;
    }

    // ---- Issue 1: the destination decides whether a Tower and Unit are needed ----

    [Fact]
    public async Task Transfer_ToRegistration_NeedsNoTowerOrUnit_WhateverTheTicketCarries()
    {
        var f = Fixture.Create();
        var verified = await f.CallCenterTicketAsync(owner: f.CsAgent);                     // a ticket WITH a verified tower and unit
        var typed = Ticket.CreateUnverified("TG-REG-20261109-0018", f.CallCenter.DepartmentId, 5, (byte)PriorityLevel.Medium, "q", November);
        await f.Tickets.AddAsync(typed);                                                    // and one with none at all

        var a = await f.Assignments.TransferAsync(f.Manager, [Roles.CsManager], verified.TicketId,
            new TransferTicketRequestDto(f.Registration.DepartmentId, "Wrong department", [], ReasonCode: TicketTransferReason.WrongDepartment));
        var b = await f.Assignments.TransferAsync(f.Manager, [Roles.CsManager], typed.TicketId,
            new TransferTicketRequestDto(f.Registration.DepartmentId, "Wrong department", [], ReasonCode: TicketTransferReason.WrongDepartment));

        Assert.Equal(TicketMutationOutcome.Success, a.Outcome);
        Assert.Equal(TicketMutationOutcome.Success, b.Outcome);
        Assert.Equal(f.Registration.DepartmentId, verified.CurrentDepartmentId);
        Assert.Equal(f.Registration.DepartmentId, typed.CurrentDepartmentId);
        Assert.Null(await f.SelectionStore.GetAsync(typed.TicketId));                       // no Collections unit is invented or stored
        Assert.DoesNotContain(f.Audit.Entries, e => e.AfterValue is not null && e.AfterValue.Contains("CollectionsTower"));
    }

    [Fact]
    public async Task Transfer_ToRegistration_IsNotBlocked_ByAStaleHalfSelectionFromAnEarlierCollectionsChoice()
    {
        var f = Fixture.Create();
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);

        // A tower (no unit) picked while Collections was selected, then the destination changed to Registration: it is the destination that counts.
        var result = await f.Assignments.TransferAsync(f.Manager, [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(f.Registration.DepartmentId, "Wrong department", [], CollectionsTowerId: 1, CollectionsUnitRef: null));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Registration.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task Transfer_FromCollectionsToRegistration_Works_AndKeepsStatusPriorityAndSla()
    {
        var f = Fixture.Create();
        var ticket = await CollectionsTicketAsync(f, owner: f.Alice, number: "TG-REG-20261109-0018");
        var (status, priority, sla) = (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState);

        var result = await f.Assignments.TransferAsync(f.Manager, [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(f.Registration.DepartmentId, "Belongs to Registration", [], ReasonCode: TicketTransferReason.WrongDepartment));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(f.Registration.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal((status, priority, sla), (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState));
        Assert.Contains(f.Audit.Entries, a => a.Action == "Transfer" && a.AfterValue!.Contains($"DepartmentId={f.Registration.DepartmentId}"));
    }

    [Fact]
    public async Task Transfer_ToCollections_StillRequiresAVerifiedPair_WhenOneHalfIsGiven_AndAcceptsAValidPair()
    {
        var f = Fixture.Create();
        await f.ActivateAsync(f.Alice, 2026, 11, 2);
        var typed = Ticket.CreateUnverified("TG-MANUAL-0099", f.CallCenter.DepartmentId, 5, (byte)PriorityLevel.Medium, "q", November);
        await f.Tickets.AddAsync(typed);
        var ticket = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var towerOnly = await f.TransferToCollectionsAsync(typed, towerId: 1);
        var unitOnly = await f.TransferToCollectionsAsync(typed, unitRef: "crm:3");
        var valid = await f.TransferToCollectionsAsync(ticket, towerId: 2, unitRef: "crm:4");

        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitRequired, towerOnly.Outcome);
        Assert.Equal(TicketMutationOutcome.CollectionsTowerUnitRequired, unitOnly.Outcome);
        Assert.Equal(f.CallCenter.DepartmentId, typed.CurrentDepartmentId);                 // refused before anything was written
        Assert.Equal(TicketMutationOutcome.Success, valid.Outcome);
        Assert.Equal(f.Collections.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(f.Alice, ticket.CurrentOwnerEmployeeId);                                // routed by the chosen tower, as before
    }

    // ---- Issue 2: reassignment inside Collections ----

    [Fact]
    public async Task Reassign_FromCollections8ToCollections2_Works_AndPreservesStatusSlaHistoryAndAudit()
    {
        var f = Fixture.Create();
        var collections8 = Guid.NewGuid();
        var collections2 = Guid.NewGuid();
        f.AddMember(collections8, "Collections 8", f.Collections.DepartmentId);
        f.AddMember(collections2, "Collections 2", f.Collections.DepartmentId);
        var ticket = await CollectionsTicketAsync(f, owner: collections8);
        var before = (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState, ticket.CurrentDepartmentId, ticket.CreatedAtUtc, ticket.TicketNumber, ticket.EscalationLevel);

        var result = await f.Assignments.AssignAsync(collections8, [Roles.DepartmentEmployee], ticket.TicketId,
            new AssignTicketRequestDto(collections2, [], Reason: "Collections 8 is overloaded"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(collections2, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(before, (ticket.TicketStatus, ticket.PriorityId, ticket.SlaState, ticket.CurrentDepartmentId, ticket.CreatedAtUtc, ticket.TicketNumber, ticket.EscalationLevel));

        var rows = f.TicketAssignments.Added.Where(a => a.TicketId == ticket.TicketId).ToList();    // history is appended to, never rewritten
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsCurrent);
        Assert.Equal(collections8, rows[0].AssignedEmployeeId);
        Assert.True(rows[1].IsCurrent);
        Assert.Equal(collections2, rows[1].AssignedEmployeeId);

        var entry = Assert.Single(f.History.Entries);
        Assert.Equal((TicketAssignmentKind.Manual, (Guid?)collections8, collections2, (Guid?)collections8, "Collections 8 is overloaded"),
            (entry.Kind, entry.PreviousEmployeeId, entry.NewEmployeeId, entry.ChangedByEmployeeId, entry.Reason));
        Assert.Contains(f.Audit.Entries, a => a.Action == "Assign" && a.BeforeValue == collections8.ToString() && a.AfterValue == collections2.ToString());
    }

    [Fact]
    public async Task Reassign_IsValidatedOnTheBackend_InactiveOutsiderAndUnknownAssigneesAreRefused_WithNothingWritten()
    {
        var f = Fixture.Create();
        var collections8 = Guid.NewGuid();
        var collections2 = Guid.NewGuid();
        f.AddMember(collections8, "Collections 8", f.Collections.DepartmentId);
        f.AddMember(collections2, "Collections 2", f.Collections.DepartmentId);
        f.Deactivate(collections2);
        var ticket = await CollectionsTicketAsync(f, owner: collections8);
        var saves = f.UnitOfWork.SaveChangesCallCount;

        var inactive = await f.Assignments.AssignAsync(collections8, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(collections2, []));
        var outsider = await f.Assignments.AssignAsync(collections8, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Outsider, []));
        var unknown = await f.Assignments.AssignAsync(collections8, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(Guid.NewGuid(), []));

        Assert.All([inactive, outsider, unknown], r => Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, r.Outcome));
        Assert.Equal(collections8, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(saves, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.History.Entries);
    }

    // ---- Issue 3 / unauthorized attempts ----

    [Fact]
    public async Task Reassign_ByAnEmployeeOutsideCollections_OrWithAssignDenied_IsForbidden_AndChangesNothing()
    {
        var f = Fixture.Create();
        var ticket = await CollectionsTicketAsync(f, owner: f.Alice);
        f.TransferLists.Allow(f.Outsider, f.Collections.DepartmentId);                      // being allowed to TRANSFER to Collections grants no reassignment
        f.Overrides.Set(f.Bob, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.TicketsAssign, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.PermissionEffect.Deny);
        var saves = f.UnitOfWork.SaveChangesCallCount;

        var outsider = await f.Assignments.AssignAsync(f.Outsider, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Bob, []));
        var denied = await f.Assignments.AssignAsync(f.Bob, [Roles.DepartmentEmployee], ticket.TicketId, new AssignTicketRequestDto(f.Bob, []));   // a Collections member, but Assign is denied to them
        var deniedManager = await f.Assignments.AssignAsync(f.Bob, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(f.Bob, []));

        Assert.All([outsider, denied, deniedManager], r => Assert.Equal(TicketMutationOutcome.Forbidden, r.Outcome));
        Assert.Equal(f.Alice, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(saves, f.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Transfer_WithAnExplicitDestinationList_IsLimitedToThatList_AndViewAccessGrantsNothing()
    {
        var f = Fixture.Create();
        var supervisor = Guid.NewGuid();
        f.AddMember(supervisor, "Supervisor", f.CallCenter.DepartmentId);                    // a member of the Call Center: sees it, plus ...
        f.AddMember(supervisor, "Supervisor", f.Registration.DepartmentId);                  // ... also a member (can view) Registration
        f.TransferLists.Allow(supervisor, f.Maintenance.DepartmentId);                       // but may only TRANSFER to Maintenance
        var toRegistration = await f.CallCenterTicketAsync(owner: supervisor);
        var toCollections = await f.CallCenterTicketAsync(owner: supervisor);
        var toMaintenance = await f.CallCenterTicketAsync(owner: supervisor);
        var saves = f.UnitOfWork.SaveChangesCallCount;

        var viewOnly = await f.Assignments.TransferAsync(supervisor, [Roles.CsSupervisor], toRegistration.TicketId,
            new TransferTicketRequestDto(f.Registration.DepartmentId, "Wrong department", []));
        var notListed = await f.Assignments.TransferAsync(supervisor, [Roles.CsSupervisor], toCollections.TicketId,
            new TransferTicketRequestDto(f.Collections.DepartmentId, "Payment follow-up", []));
        Assert.Equal(TicketMutationOutcome.TransferDestinationNotPermitted, viewOnly.Outcome);
        Assert.Equal(TicketMutationOutcome.TransferDestinationNotPermitted, notListed.Outcome);
        Assert.Equal(f.CallCenter.DepartmentId, toRegistration.CurrentDepartmentId);
        Assert.Equal(f.CallCenter.DepartmentId, toCollections.CurrentDepartmentId);
        Assert.Equal(saves, f.UnitOfWork.SaveChangesCallCount);                               // refused before any write

        var listed = await f.Assignments.TransferAsync(supervisor, [Roles.CsSupervisor], toMaintenance.TicketId,
            new TransferTicketRequestDto(f.Maintenance.DepartmentId, "Needs maintenance", []));
        Assert.Equal(TicketMutationOutcome.Success, listed.Outcome);
        Assert.Equal(f.Maintenance.DepartmentId, toMaintenance.CurrentDepartmentId);
    }

    [Fact]
    public async Task Transfer_WithoutAList_KeepsTheRoleBasedBehaviour_AndTheAdministratorIsNeverNarrowed()
    {
        var f = Fixture.Create();
        var noList = Guid.NewGuid();
        f.AddMember(noList, "No list", f.CallCenter.DepartmentId);
        var admin = Guid.NewGuid();
        f.TransferLists.Allow(admin, f.Maintenance.DepartmentId);                            // a list on an administrator does not restrict the central override
        var a = await f.CallCenterTicketAsync(owner: noList);
        var b = await f.CallCenterTicketAsync(owner: f.CsAgent);

        var legacy = await f.Assignments.TransferAsync(noList, [Roles.CsAgent], a.TicketId, new TransferTicketRequestDto(f.Registration.DepartmentId, "Wrong department", []));
        var administrator = await f.Assignments.TransferAsync(admin, [Roles.SystemAdministrator], b.TicketId, new TransferTicketRequestDto(f.Registration.DepartmentId, "Wrong department", []));

        Assert.Equal(TicketMutationOutcome.Success, legacy.Outcome);
        Assert.Equal(TicketMutationOutcome.Success, administrator.Outcome);
    }

    [Fact]
    public async Task Transfer_ByARoleThatMayNotTransfer_IsStillForbidden_EvenWithTheDestinationOnTheirList()
    {
        var f = Fixture.Create();
        var ticket = await CollectionsTicketAsync(f, owner: f.Alice);
        f.TransferLists.Allow(f.Alice, f.Registration.DepartmentId);                         // a list narrows what a role may do; it never grants the role

        var result = await f.Assignments.TransferAsync(f.Alice, [Roles.DepartmentEmployee], ticket.TicketId,
            new TransferTicketRequestDto(f.Registration.DepartmentId, "Wrong department", []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(f.Collections.DepartmentId, ticket.CurrentDepartmentId);
    }
}
