using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Administration.Services;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Department transfer with immediate Round Robin assignment against a REAL SQL Server (a throw-away database per test, created by the real
/// migrations, composed from the real repositories / unit of work / outbox - what the API builds per request). It proves what the in-memory tests can only
/// model: the cursor's <c>UPDLOCK</c> row lock serializes truly parallel transfers, a lost race rolls the cursor back, every department keeps its own
/// cursor row, one notification per assignment, and a transferred ticket left Unassigned is picked up later by the reconciler and never sent back to the
/// Call Center. Without <c>TIGERCS_TEST_SQL_SERVER</c> they are reported SKIPPED, never "passed" without SQL.
/// </summary>
public sealed class TransferRoundRobinSqlServerTests : IDisposable
{
    private static readonly DateTime Past = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Manager = [Roles.CsManager];

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsTransferRrTest_{Guid.NewGuid():N}";
    private readonly FakeUserAccountManager _accounts = new();

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    private TigerCsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_master))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }

    private sealed record World(int CallCenterId, int LeasingId, int FacilitiesId, int MaintenanceId, Guid ManagerId);

    private World CreateDatabase()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using var context = CreateContext();
        context.Database.Migrate();

        var cc = new Department("Call Center", "CC");
        var leasing = new Department("Leasing", "LEA");
        var facilities = new Department("Facilities Management", "FM");
        var maintenance = new Department("Maintenance", "MNT");
        context.Departments.AddRange(cc, leasing, facilities, maintenance);
        context.SaveChanges();

        var manager = AddEmployee(context, null, Roles.CsManager);
        return new World(cc.DepartmentId, leasing.DepartmentId, facilities.DepartmentId, maintenance.DepartmentId, manager);
    }

    /// <summary>A real user + employee (+ active membership of <paramref name="departmentId"/>) with <paramref name="role"/> registered for eligibility checks.</summary>
    private Guid AddEmployee(TigerCsDbContext context, int? departmentId, string role)
    {
        var id = Guid.NewGuid();
        context.Users.Add(new ApplicationUser
        {
            Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(), Email = $"{id:N}@example.test",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        context.Employees.Add(new Employee(id, $"Employee {id:N}"[..16], isGeynessStaff: false, Past.AddDays(-60)));
        context.SaveChanges();
        if (departmentId is { } department)
        {
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(id, department, true, Past.AddDays(-50), null));
            context.SaveChanges();
        }

        _accounts.Add(id, $"u-{id:N}", role);
        return id;
    }

    private Guid[] AddStaff(int departmentId, int count, string role = Roles.DepartmentEmployee)
    {
        using var context = CreateContext();
        return [.. Enumerable.Range(0, count).Select(_ => AddEmployee(context, departmentId, role)).Order()];
    }

    /// <summary>An open ticket in <paramref name="departmentId"/> owned by a fresh previous employee of that department (with its current assignment row).</summary>
    private (long TicketId, Guid Previous) AddOwnedTicket(int departmentId, int n)
    {
        using var context = CreateContext();
        var previous = AddEmployee(context, departmentId, Roles.DepartmentEmployee);
        var ticket = Ticket.CreateUnclassified($"TG-RR-20261116-{n:D4}", departmentId, "AC not cooling", Past);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        ticket.AssignTo(previous);
        context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, previous, departmentId, Past, assigningActorEmployeeId: null));
        context.SaveChanges();
        return (ticket.TicketId, previous);
    }

    private long AddUnownedTicket(int departmentId, int n)
    {
        using var context = CreateContext();
        var ticket = Ticket.CreateUnclassified($"TG-RR-20261116-{n:D4}", departmentId, "AC not cooling", Past);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        return ticket.TicketId;
    }

    private TicketAutoAssignmentService ComposeAuto(TigerCsDbContext db) =>
        new(new RequestTypeAssignmentRuleRepository(db), new DepartmentWorkflowSettingsRepository(db), new UserDepartmentAssignmentRepository(db),
            new TicketAssignmentRepository(db), new AuditEntryWriter(db, TimeProvider.System), new AssignmentRotationRepository(db),
            new DepartmentRepository(db), roleReader: _accounts, accountManager: _accounts, serviceIdentities: null,
            workflowEventRepository: new TicketWorkflowEventRepository(db), outboxWriter: new OutboxWriter(db));

    /// <summary>The real service graph over one DbContext.</summary>
    private TicketAssignmentAppService ComposeTransfer(TigerCsDbContext db) =>
        new(new TicketRepository(db), new TicketAssignmentRepository(db), new UserDepartmentAssignmentRepository(db), new DepartmentRepository(db),
            new TicketingUnitOfWork(db), new AuditEntryWriter(db, TimeProvider.System), TimeProvider.System, new DepartmentWorkflowSettingsRepository(db),
            ComposeAuto(db), new TicketWorkflowEventRepository(db), outboxWriter: new OutboxWriter(db),
            unassignedWork: new TicketUnassignedWorkRepository(db));

    private CallCenterUnassignedWorkReconciler ComposeReconciler(TigerCsDbContext db) =>
        new(new TicketUnassignedWorkRepository(db), new TicketRepository(db), ComposeAuto(db), new TicketingUnitOfWork(db), TimeProvider.System);

    /// <summary>Like the API: the caller sends the ticket's current row version (read just before the call), never an empty one.</summary>
    private async Task<TicketMutationResult> TransferAsync(World w, long ticketId, int toDepartmentId, Guid? assignTo = null)
    {
        byte[] rowVersion;
        using (var read = CreateContext())
        {
            rowVersion = read.Tickets.AsNoTracking().Where(t => t.TicketId == ticketId).Select(t => t.RowVersion).Single();
        }

        using var db = CreateContext();
        return await ComposeTransfer(db).TransferAsync(
            w.ManagerId, Manager, ticketId, new TransferTicketRequestDto(toDepartmentId, "Belongs to the other team", rowVersion, assignTo));
    }

    private sealed record State(Guid? Owner, int DepartmentId, List<TicketAssignment> Assignments);

    private State Read(long ticketId)
    {
        using var read = CreateContext();
        var ticket = read.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId);
        return new State(ticket.CurrentOwnerEmployeeId, ticket.CurrentDepartmentId, read.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToList());
    }

    private Guid? Cursor(int departmentId)
    {
        using var read = CreateContext();
        return read.DepartmentAssignmentRotations.AsNoTracking().SingleOrDefault(r => r.DepartmentId == departmentId)?.LastAssignedEmployeeId;
    }

    private int Count(Func<TigerCsDbContext, int> count)
    {
        using var read = CreateContext();
        return count(read);
    }

    // =====================================================================
    // Sequential behaviour on real SQL (cursor persistence, per-department independence)
    // =====================================================================

    [VisibilitySqlServerFact]
    public async Task Sequential_Transfers_DealTheDestinationsNextEmployee_PersistTheCursor_AndKeepEachDepartmentsOrderIndependent()
    {
        var w = CreateDatabase();
        var facilities = AddStaff(w.FacilitiesId, 3);
        var maintenance = AddStaff(w.MaintenanceId, 2);

        var plan = new (int Department, Guid[] Staff)[]
        {
            (w.FacilitiesId, facilities), (w.MaintenanceId, maintenance), (w.FacilitiesId, facilities), (w.FacilitiesId, facilities),
            (w.MaintenanceId, maintenance), (w.FacilitiesId, facilities), (w.MaintenanceId, maintenance)
        };
        var expectedIndex = new Dictionary<int, int> { [w.FacilitiesId] = 0, [w.MaintenanceId] = 0 };

        for (var i = 0; i < plan.Length; i++)
        {
            var (ticketId, previous) = AddOwnedTicket(w.LeasingId, i + 1);
            var result = await TransferAsync(w, ticketId, plan[i].Department);
            Assert.Equal(TicketMutationOutcome.Success, result.Outcome);

            var state = Read(ticketId);
            var staff = plan[i].Staff;
            Assert.Equal(staff[expectedIndex[plan[i].Department] % staff.Length], state.Owner);
            expectedIndex[plan[i].Department]++;
            Assert.Equal(plan[i].Department, state.DepartmentId);
            Assert.Equal(state.Owner, Assert.Single(state.Assignments, a => a.IsCurrent).AssignedEmployeeId);       // exactly one current row
            Assert.Contains(state.Assignments, a => a.AssignedEmployeeId == previous && !a.IsCurrent && a.SupersededAtUtc is not null);   // previous owner's row kept, superseded + stamped
        }

        Assert.Equal(2, Count(db => db.DepartmentAssignmentRotations.Count()));                                      // one cursor row per destination
        Assert.Equal(facilities[0], Cursor(w.FacilitiesId));                                                         // 4 dealt: [0,1,2,0]
        Assert.Equal(maintenance[0], Cursor(w.MaintenanceId));                                                       // 3 dealt: [0,1,0]
    }

    // =====================================================================
    // Truly parallel transfers
    // =====================================================================

    [VisibilitySqlServerFact]
    public async Task EightTransfers_Truly_InParallel_IntoOneDepartment_AreDealtEvenly_WithNoDoubleDealing_AndOneNotificationEach()
    {
        var w = CreateDatabase();
        var staff = AddStaff(w.FacilitiesId, 4);
        var tickets = Enumerable.Range(1, 8).Select(n => AddOwnedTicket(w.LeasingId, n)).ToList();

        using var gate = new ManualResetEventSlim(false);
        var attempts = tickets.Select(t => Task.Run(async () =>
        {
            gate.Wait();
            return await TransferAsync(w, t.TicketId, w.FacilitiesId);
        })).ToArray();
        await Task.Delay(150);
        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, r => Assert.Equal(TicketMutationOutcome.Success, r.Outcome));
        var owners = tickets.Select(t => Read(t.TicketId).Owner!.Value).ToList();
        foreach (var member in staff)
        {
            Assert.Equal(2, owners.Count(o => o == member));                                                      // 8 tickets / 4 staff: nobody dealt twice while another waited
        }

        foreach (var t in tickets)
        {
            var state = Read(t.TicketId);
            Assert.Equal(w.FacilitiesId, state.DepartmentId);
            Assert.Single(state.Assignments, a => a.IsCurrent);
            Assert.Equal(2, state.Assignments.Count);                                                             // the previous owner's (superseded) row + the new one
        }

        Assert.Equal(8, Count(db => db.AuditEntries.Count(a => a.Action == "AutoAssign")));
        var notified = Count(db => db.OutboxMessages.Count(m => m.EventType == OutboxEventTypes.TicketAssignedToAgent));
        Assert.Equal(8, notified);                                                                                // exactly one "assigned to you" per transfer
        Assert.Equal(1, Count(db => db.DepartmentAssignmentRotations.Count()));
        Assert.Contains(Cursor(w.FacilitiesId), staff.Select(s => (Guid?)s));
    }

    [VisibilitySqlServerFact]
    public async Task ParallelTransfers_IntoTwoDepartments_KeepTwoIndependentCursors()
    {
        var w = CreateDatabase();
        var facilities = AddStaff(w.FacilitiesId, 3);
        var maintenance = AddStaff(w.MaintenanceId, 3);
        var toFacilities = Enumerable.Range(1, 6).Select(n => AddOwnedTicket(w.LeasingId, n)).ToList();
        var toMaintenance = Enumerable.Range(7, 6).Select(n => AddOwnedTicket(w.LeasingId, n)).ToList();

        using var gate = new ManualResetEventSlim(false);
        var attempts = toFacilities.Select(t => (t.TicketId, Department: w.FacilitiesId))
            .Concat(toMaintenance.Select(t => (t.TicketId, Department: w.MaintenanceId)))
            .Select(x => Task.Run(async () =>
            {
                gate.Wait();
                return await TransferAsync(w, x.TicketId, x.Department);
            })).ToArray();
        await Task.Delay(150);
        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, r => Assert.Equal(TicketMutationOutcome.Success, r.Outcome));
        foreach (var (tickets, staff) in new[] { (toFacilities, facilities), (toMaintenance, maintenance) })
        {
            var owners = tickets.Select(t => Read(t.TicketId).Owner!.Value).ToList();
            Assert.All(staff, member => Assert.Equal(2, owners.Count(o => o == member)));                          // each department dealt only its own staff, evenly
        }

        Assert.Equal(2, Count(db => db.DepartmentAssignmentRotations.Count()));
    }

    [VisibilitySqlServerFact]
    public async Task TwoManagersTransferringTheSameTicketAtOnce_OneWins_TheLoserRollsBack_AndTheCursorSlotIsNotConsumed()
    {
        var w = CreateDatabase();
        var staff = AddStaff(w.FacilitiesId, 3);

        for (var round = 1; round <= 6; round++)
        {
            var (ticketId, _) = AddOwnedTicket(w.LeasingId, round);
            var before = Cursor(w.FacilitiesId);

            using var gate = new ManualResetEventSlim(false);
            var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                return await TransferAsync(w, ticketId, w.FacilitiesId);
            })).ToArray();
            await Task.Delay(100);
            gate.Set();
            var results = await Task.WhenAll(attempts);

            var winner = Assert.Single(results, r => r.Outcome == TicketMutationOutcome.Success);
            var loser = Assert.Single(results, r => r.Outcome != TicketMutationOutcome.Success);
            Assert.Contains(loser.Outcome, new[] { TicketMutationOutcome.ConcurrencyConflict, TicketMutationOutcome.AlreadyInTargetDepartment });

            var state = Read(ticketId);
            Assert.NotNull(state.Owner);
            Assert.Equal(state.Owner, Assert.Single(state.Assignments, a => a.IsCurrent).AssignedEmployeeId);        // one owner, one current row
            Assert.Equal(2, state.Assignments.Count);                                                             // previous (superseded) + exactly one new row
            Assert.Equal(1, Count(db => db.TicketWorkflowEvents.Count(e => e.TicketId == ticketId && e.EventType == WorkflowEventType.TicketTransferred)));
            // Exactly ONE "assigned to you" message for THIS ticket (matched on the payload's own ticketId field, not a substring of any number).
            Assert.Equal(1, Count(db => db.OutboxMessages.Count(m => m.EventType == OutboxEventTypes.TicketAssignedToAgent
                && m.Payload.Contains($"\"ticketId\":{ticketId},") && m.Payload.Contains($"\"assignedEmployeeId\":\"{state.Owner}\""))));
            Assert.Equal(1, Count(db => db.OutboxMessages.Count(m => m.EventType == OutboxEventTypes.TicketAssignedToAgent && m.Payload.Contains($"\"ticketId\":{ticketId},"))));

            // The cursor moved by exactly ONE slot per round: the loser's slot was rolled back with its transaction.
            var expected = before is null ? staff[0] : staff[(Array.IndexOf(staff, before.Value) + 1) % staff.Length];
            Assert.Equal(expected, Cursor(w.FacilitiesId));
            Assert.Equal(expected, state.Owner);
        }
    }

    // =====================================================================
    // Manual assignment, no eligible employee
    // =====================================================================

    [VisibilitySqlServerFact]
    public async Task ManualAssignment_OnTransfer_IsPreserved_AndTheCursorIsNeitherReadNorAdvanced()
    {
        var w = CreateDatabase();
        var staff = AddStaff(w.FacilitiesId, 3);
        var (ticketId, _) = AddOwnedTicket(w.LeasingId, 1);

        var result = await TransferAsync(w, ticketId, w.FacilitiesId, assignTo: staff[2]);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(staff[2], Read(ticketId).Owner);
        Assert.Equal(0, Count(db => db.DepartmentAssignmentRotations.Count()));
        Assert.Equal(0, Count(db => db.AuditEntries.Count(a => a.Action == "AutoAssign")));
    }

    [VisibilitySqlServerFact]
    public async Task NoEligibleEmployee_LeavesTheTicketUnassignedInTheDestination_WithTheReasonRecorded()
    {
        var w = CreateDatabase();
        var (ticketId, previous) = AddOwnedTicket(w.LeasingId, 1);

        var result = await TransferAsync(w, ticketId, w.FacilitiesId);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var state = Read(ticketId);
        Assert.Null(state.Owner);
        Assert.Equal(w.FacilitiesId, state.DepartmentId);
        Assert.DoesNotContain(state.Assignments, a => a.IsCurrent);
        Assert.Contains(state.Assignments, a => a.AssignedEmployeeId == previous && !a.IsCurrent);
        Assert.Equal(1, Count(db => db.TicketWorkflowEvents.Count(e => e.TicketId == ticketId && e.EventType == WorkflowEventType.ManualAssignmentRequired)));
        Assert.Equal(1, Count(db => db.AuditEntries.Count(a => a.EntityId == ticketId.ToString() && a.Action == "Unassign")));
        Assert.Equal(0, Count(db => db.OutboxMessages.Count(m => m.EventType == OutboxEventTypes.TicketAssignedToAgent)));
    }

    // =====================================================================
    // Later automatic assignment (reconciler) and "never back to the Call Center"
    // =====================================================================

    [VisibilitySqlServerFact]
    public async Task ATransferredUnassignedTicket_IsDealtLater_ByTheDestinationsRoundRobin_WhenSomeoneBecomesEligible_AndNeverGoesBackToTheCallCenter()
    {
        var w = CreateDatabase();
        AddStaff(w.CallCenterId, 2, Roles.CsAgent);                                           // a Call Center with agents: the tempting wrong answer
        var (ticketId, _) = AddOwnedTicket(w.LeasingId, 1);                                   // Leasing -> Facilities: it never touched the Call Center
        var options = new CallCenterReconciliationOptions { MinTicketAgeMinutes = 0 };

        Assert.Equal(TicketMutationOutcome.Success, (await TransferAsync(w, ticketId, w.FacilitiesId)).Outcome);
        Assert.Null(Read(ticketId).Owner);

        // The evidence says "waiting for its department", and nothing is written while the department still has nobody.
        using (var db = CreateContext())
        {
            var evidence = await new TicketUnassignedWorkRepository(db).GetEvidenceAsync(ticketId);
            Assert.True(evidence!.AwaitingDepartmentAssignment);
            Assert.Equal(UnassignedTicketClass.TransferredAwaitingDepartmentAssignment, UnassignedTicketClassifier.Classify(evidence, w.CallCenterId, DateTime.UtcNow).Class);
        }

        var auditBefore = Count(db => db.AuditEntries.Count());
        for (var run = 0; run < 3; run++)
        {
            using var db = CreateContext();
            var idle = await ComposeReconciler(db).TryReconcileAsync(ticketId, options, dryRun: false);
            Assert.Equal(ReconcileOutcome.NoEligibleAgent, idle.Outcome);
        }

        Assert.Equal(auditBefore, Count(db => db.AuditEntries.Count()));                      // no audit spam from repeated polling
        Assert.Equal(w.FacilitiesId, Read(ticketId).DepartmentId);                            // and it was NOT pulled into the Call Center

        // An eligible employee joins Facilities: the next run deals the ticket by Facilities' Round Robin.
        var newcomer = AddStaff(w.FacilitiesId, 1)[0];
        using (var db = CreateContext())
        {
            var dry = await ComposeReconciler(db).TryReconcileAsync(ticketId, options, dryRun: true);
            Assert.Equal(ReconcileOutcome.WouldAct, dry.Outcome);
            Assert.Null(Read(ticketId).Owner);                                                // a dry run writes nothing
        }

        using (var db = CreateContext())
        {
            var run = await ComposeReconciler(db).TryReconcileAsync(ticketId, options, dryRun: false);
            Assert.Equal(ReconcileOutcome.AssignedRoundRobin, run.Outcome);
            Assert.Equal(newcomer, run.AssignedEmployeeId);
        }

        var state = Read(ticketId);
        Assert.Equal(newcomer, state.Owner);
        Assert.Equal(w.FacilitiesId, state.DepartmentId);
        Assert.Equal(newcomer, Assert.Single(state.Assignments, a => a.IsCurrent).AssignedEmployeeId);
        Assert.Equal(newcomer, Cursor(w.FacilitiesId));
        Assert.Equal(1, Count(db => db.OutboxMessages.Count(m => m.EventType == OutboxEventTypes.TicketAssignedToAgent)));

        // Done: a further run finds nothing to do and does not rotate.
        using (var db = CreateContext())
        {
            var again = await ComposeReconciler(db).TryReconcileAsync(ticketId, options, dryRun: false);
            Assert.Equal(ReconcileOutcome.Skipped, again.Outcome);
        }

        Assert.Equal(newcomer, Cursor(w.FacilitiesId));
        Assert.Equal(1, Count(db => db.OutboxMessages.Count(m => m.EventType == OutboxEventTypes.TicketAssignedToAgent)));
    }

    [VisibilitySqlServerFact]
    public async Task ATicketThatNeverWasTransferred_ButSitsUnassignedInADepartment_IsNotTouchedByTheDepartmentRule()
    {
        var w = CreateDatabase();
        AddStaff(w.FacilitiesId, 2);
        var ticketId = AddUnownedTicket(w.FacilitiesId, 1);

        using var db = CreateContext();
        var evidence = await new TicketUnassignedWorkRepository(db).GetEvidenceAsync(ticketId);

        Assert.False(evidence!.AwaitingDepartmentAssignment);                                 // only a transfer that found nobody qualifies
        Assert.NotEqual(UnassignedTicketAction.RoundRobinDepartment, UnassignedTicketClassifier.Classify(evidence, w.CallCenterId, DateTime.UtcNow).Action);
    }

    [VisibilitySqlServerFact]
    public async Task ARetryOfATransferredUnassignedTicket_UsesTheDestinationsRoundRobin_NotTheSharedCallCenterPool()
    {
        var w = CreateDatabase();
        AddStaff(w.CallCenterId, 2, Roles.CsAgent);
        var (ticketId, _) = AddOwnedTicket(w.LeasingId, 1);
        Assert.Equal(TicketMutationOutcome.Success, (await TransferAsync(w, ticketId, w.FacilitiesId)).Outcome);
        Assert.Null(Read(ticketId).Owner);

        // Still nobody in Facilities: the retry reports "no eligible", and certainly does not hand it to a Call Center agent.
        using (var db = CreateContext())
        {
            var none = await ComposeTransfer(db).RetryCsAgentAssignmentAsync(w.ManagerId, Manager, ticketId);
            Assert.Equal(TicketMutationOutcome.NoEligibleCsAgent, none.Outcome);
        }

        Assert.Null(Read(ticketId).Owner);

        var member = AddStaff(w.FacilitiesId, 1)[0];
        using (var db = CreateContext())
        {
            var retried = await ComposeTransfer(db).RetryCsAgentAssignmentAsync(w.ManagerId, Manager, ticketId);
            Assert.Equal(TicketMutationOutcome.Success, retried.Outcome);
        }

        Assert.Equal(member, Read(ticketId).Owner);
        Assert.Equal(w.FacilitiesId, Read(ticketId).DepartmentId);
    }
}
