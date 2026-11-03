using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.Ticketing.Integration;
using TigerCS.Tests.Ticketing.TakeOwnership;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The Call Center reconciler against a REAL SQL Server with the real repositories, unit of work and migrations: it deals only Open,
/// unowned, human-required tickets to CS Agents; every already-assigned ticket (the production backlog distribution) keeps its owner and
/// assignment rows; AI / released / transferred tickets are untouched; parallel reconcilers never create two owners; and the SQL application
/// lock keeps runs from overlapping. Without <c>TIGERCS_TEST_SQL_SERVER</c> they are SKIPPED, never "passed".
/// </summary>
public sealed class CallCenterReconciliationSqlServerTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsReconcileTest_{Guid.NewGuid():N}";
    private readonly FakeUserAccountManager _accounts = new();
    private int _seq;

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;
    private TigerCsDbContext CreateContext() => new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_master)) return;
        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }

    private sealed record Seed(int CallCenterId, int AccountingId, Guid[] CsAgents, Guid DepartmentEmployee, Guid Worker);

    private Seed CreateAndSeed(int csAgents)
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
        var accounting = new Department("Accounting", "ACCT");
        context.Departments.AddRange(cc, accounting);
        context.SaveChanges();

        Guid NewEmployee(string name, Department? member, string role)
        {
            var id = Guid.NewGuid();
            context.Users.Add(new ApplicationUser { Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(), Email = $"{id:N}@example.test", SecurityStamp = Guid.NewGuid().ToString() });
            context.Employees.Add(new Employee(id, name, isGeynessStaff: false, Now.AddDays(-60)));
            context.SaveChanges();
            if (member is not null)
            {
                context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(id, member.DepartmentId, true, Now.AddDays(-50), null));
                context.SaveChanges();
            }
            _accounts.Add(id, $"u-{id:N}", role);
            return id;
        }

        var agents = Enumerable.Range(0, csAgents).Select(i => NewEmployee($"Agent {i}", cc, Roles.CsAgent)).ToArray();
        var departmentEmployee = NewEmployee("CC Department Employee", cc, Roles.DepartmentEmployee);
        var worker = NewEmployee("Accounting worker", accounting, Roles.DepartmentEmployee);
        return new Seed(cc.DepartmentId, accounting.DepartmentId, agents, departmentEmployee, worker);
    }

    private long AddTicket(int departmentId, Action<Ticket>? shape = null, Action<TigerCsDbContext, Ticket>? after = null)
    {
        using var context = CreateContext();
        var ticket = ClaimTestData.Open($"TG-REC-20261103-{Interlocked.Increment(ref _seq):D4}", departmentId, ageHours: 48);
        shape?.Invoke(ticket);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        if (after is not null) { after(context, ticket); context.SaveChanges(); }
        return ticket.TicketId;
    }

    private CallCenterUnassignedWorkReconciler Reconciler(TigerCsDbContext db)
    {
        var auto = new TicketAutoAssignmentService(
            new RequestTypeAssignmentRuleRepository(db), new DepartmentWorkflowSettingsRepository(db), new UserDepartmentAssignmentRepository(db),
            new TicketAssignmentRepository(db), new AuditEntryWriter(db, TimeProvider.System), new AssignmentRotationRepository(db),
            new DepartmentRepository(db), roleReader: _accounts, accountManager: _accounts, serviceIdentities: null,
            workflowEventRepository: new TicketWorkflowEventRepository(db));
        return new CallCenterUnassignedWorkReconciler(new TicketUnassignedWorkRepository(db), new TicketRepository(db), auto, new TicketingUnitOfWork(db), TimeProvider.System);
    }

    private static CallCenterReconciliationOptions Live() => new() { Enabled = true, DryRun = false, MinTicketAgeMinutes = 0 };

    private async Task<ReconcileResult> ReconcileAsync(long ticketId, CallCenterReconciliationOptions? options = null)
    {
        using var db = CreateContext();
        return await Reconciler(db).TryReconcileAsync(ticketId, options ?? Live(), dryRun: false);
    }

    private async Task<IReadOnlyList<long>> ListEligibleIdsAsync()
    {
        using var db = CreateContext();
        var rows = await Reconciler(db).ListAsync(0, Live());
        return [.. rows.Where(r => r.Classification.Action != UnassignedTicketAction.None).Select(r => r.Evidence.TicketId)];
    }

    [VisibilitySqlServerFact]
    public async Task OnlyOpenUnownedHumanTickets_AreDealt_ToCsAgents_AndEveryAssignedTicketIsPreserved()
    {
        var seed = CreateAndSeed(csAgents: 2);

        // The production backlog distribution: tickets already owned by Call Center agents, with their assignment rows.
        var preOwned = Enumerable.Range(0, 3).Select(i => AddTicket(seed.CallCenterId, t => t.AssignTo(seed.CsAgents[i % 2]),
            (db, t) => db.TicketAssignments.Add(new TicketAssignment(t.TicketId, seed.CsAgents[i % 2], seed.CallCenterId, Now.AddHours(-5), assigningActorEmployeeId: null)))).ToList();
        var ownedElsewhere = AddTicket(seed.AccountingId, t => t.AssignTo(seed.Worker));

        var manualStuck = AddTicket(seed.AccountingId);
        var flaggedStuck = AddTicket(seed.AccountingId, after: (db, t) =>
        {
            var i = Interaction(t); i.MarkRequiresHumanFollowUp(); db.TicketInteractions.Add(i);
        });
        var activeAi = AddTicket(seed.AccountingId, after: (db, t) => db.TicketInteractions.Add(Interaction(t)));
        var endedAi = AddTicket(seed.AccountingId, after: (db, t) => { var i = Interaction(t); i.End(Now.AddMinutes(-20), "done"); db.TicketInteractions.Add(i); });
        var closed = AddTicket(seed.AccountingId, t => ClaimTestData.MakeClosedUnowned(t, seed.Worker));
        var transferredOut = AddTicket(seed.AccountingId, after: (db, t) =>
            db.TicketWorkflowEvents.Add(TicketWorkflowEvent.ForTransfer(t.TicketId, Now.AddHours(-3), seed.CsAgents[0], TicketTransferReason.NeedsEscalation, seed.CallCenterId, seed.AccountingId, null, "onward", Guid.NewGuid())));

        var before = SnapshotAssignments(preOwned.Append(ownedElsewhere));

        var eligible = await ListEligibleIdsAsync();
        Assert.Equal([manualStuck, flaggedStuck], eligible.Order().ToArray());

        foreach (var id in eligible) Assert.True((await ReconcileAsync(id)).Outcome is ReconcileOutcome.AssignedRoundRobin);

        using var read = CreateContext();
        foreach (var id in new[] { manualStuck, flaggedStuck })
        {
            var t = read.Tickets.AsNoTracking().Single(x => x.TicketId == id);
            Assert.Equal(seed.CallCenterId, t.CurrentDepartmentId);
            Assert.Contains(t.CurrentOwnerEmployeeId!.Value, seed.CsAgents);
            Assert.NotEqual(seed.DepartmentEmployee, t.CurrentOwnerEmployeeId);              // restricted to CS Agents
            Assert.Equal(1, read.TicketAssignments.Count(a => a.TicketId == id && a.IsCurrent));
        }
        Assert.NotEqual(
            read.Tickets.Single(x => x.TicketId == manualStuck).CurrentOwnerEmployeeId,
            read.Tickets.Single(x => x.TicketId == flaggedStuck).CurrentOwnerEmployeeId);   // Round Robin alternates

        Assert.Equal(before, SnapshotAssignments(preOwned.Append(ownedElsewhere)));          // owners, departments and assignment rows byte-for-byte unchanged
        foreach (var id in new[] { activeAi, endedAi, closed, transferredOut })
            Assert.Null(read.Tickets.AsNoTracking().Single(x => x.TicketId == id).CurrentOwnerEmployeeId);
        Assert.Equal(seed.AccountingId, read.Tickets.AsNoTracking().Single(x => x.TicketId == transferredOut).CurrentDepartmentId);
    }

    [VisibilitySqlServerFact]
    public async Task TheAgentWhoAnswered_GetsTheTicket_AndTheRotationIsNotAdvanced()
    {
        var seed = CreateAndSeed(csAgents: 3);
        var answering = seed.CsAgents[2];
        var id = AddTicket(seed.AccountingId, after: (db, t) =>
        {
            db.TicketInteractions.Add(Interaction(t, WellKnownChannels.Phone));
            db.SaveChanges();
            var interaction = db.TicketInteractions.First(i => i.TicketId == t.TicketId);
            db.TicketInteractionHandlers.Add(TicketInteractionHandler.Create(interaction.TicketInteractionId, t.TicketId, answering, "genesys-answering", InteractionHandlerSource.Routing, null, Now));
        });

        var result = await ReconcileAsync(id);

        Assert.Equal(ReconcileOutcome.AssignedToAnsweringAgent, result.Outcome);
        using var read = CreateContext();
        Assert.Equal(answering, read.Tickets.AsNoTracking().Single(t => t.TicketId == id).CurrentOwnerEmployeeId);
        Assert.Null(read.DepartmentAssignmentRotations.AsNoTracking().SingleOrDefault(r => r.DepartmentId == seed.CallCenterId)?.LastAssignedEmployeeId);
    }

    [VisibilitySqlServerFact]
    public async Task ParallelReconcilers_OnTheSameTickets_NeverCreateTwoOwners_OrTwoCurrentAssignments()
    {
        var seed = CreateAndSeed(csAgents: 3);
        var ids = Enumerable.Range(0, 8).Select(_ => AddTicket(seed.AccountingId)).ToList();

        // Four workers all try every ticket at the same time.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            foreach (var id in ids)
            {
                try { await ReconcileAsync(id); } catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException) { /* a lost race that surfaced as a provider exception: nothing committed */ }
            }
        })));

        using var read = CreateContext();
        foreach (var id in ids)
        {
            var t = read.Tickets.AsNoTracking().Single(x => x.TicketId == id);
            Assert.NotNull(t.CurrentOwnerEmployeeId);
            Assert.Contains(t.CurrentOwnerEmployeeId!.Value, seed.CsAgents);
            Assert.Equal(1, read.TicketAssignments.Count(a => a.TicketId == id && a.IsCurrent));
            Assert.Equal(1, read.TicketAssignments.Count(a => a.TicketId == id && a.AssignedEmployeeId == t.CurrentOwnerEmployeeId));
        }
    }

    [VisibilitySqlServerFact]
    public async Task TheSqlApplicationLock_StopsAnotherProcessFromRunningAtTheSameTime()
    {
        var seed = CreateAndSeed(csAgents: 1);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();

        // Another process (here: a raw session) holds the application lock.
        await using var other = new SqlConnection(ConnectionString);
        await other.OpenAsync();
        await using (var cmd = other.CreateCommand())
        {
            cmd.CommandText = $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'{CallCenterReconciliationRunLock.Resource}', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
            Assert.True(Convert.ToInt32(await cmd.ExecuteScalarAsync()) >= 0);
        }

        var runLock = new CallCenterReconciliationRunLock(config);
        Assert.Null(await runLock.TryAcquireAsync());   // refused, not queued

        await other.CloseAsync();                       // the other process ends: the lock vanishes with its session
        SqlConnection.ClearAllPools();
        await using var handle = await runLock.TryAcquireAsync();
        Assert.NotNull(handle);
        _ = seed;
    }

    private (Guid? Owner, int Dept, string Rows)[] SnapshotAssignments(IEnumerable<long> ids)
    {
        using var read = CreateContext();
        return [.. ids.OrderBy(i => i).Select(id =>
        {
            var t = read.Tickets.AsNoTracking().Single(x => x.TicketId == id);
            var rows = string.Join("|", read.TicketAssignments.AsNoTracking().Where(a => a.TicketId == id).OrderBy(a => a.TicketAssignmentId)
                .Select(a => $"{a.TicketAssignmentId}:{a.AssignedEmployeeId}:{a.AssignedDepartmentId}:{a.IsCurrent}"));
            return (t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, rows);
        })];
    }

    private static TicketInteraction Interaction(Ticket ticket, byte channel = WellKnownChannels.Website) =>
        TicketInteraction.CreateFromGenesys(
            ticket.TicketId, channel, "+971500000000", "conv-" + Guid.NewGuid().ToString("N")[..12], calledNumber: null, genesysQueueId: null,
            genesysQueueName: null, genesysAgentId: null, genesysAgentName: null, interactionStartedAtUtc: DateTime.UtcNow.AddMinutes(-2),
            direction: "Inbound", createdAtUtc: DateTime.UtcNow.AddMinutes(-2), isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8]);
}
