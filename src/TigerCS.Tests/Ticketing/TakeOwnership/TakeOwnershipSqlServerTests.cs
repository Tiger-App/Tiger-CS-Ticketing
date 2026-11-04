using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.Ticketing.Fakes;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Ticketing.TakeOwnership;

/// <summary>
/// Take Ownership against a REAL SQL Server, composed from the real repositories and unit of work: (1) the shared eligibility predicate
/// translates to SQL and agrees, ticket by ticket, with the single-ticket decision; (2) two or more agents racing for ONE ticket - truly
/// in parallel, and in a deterministic interleaving - can never both own it, never leave two current assignments, and never leave a
/// half-applied move. Without <c>TIGERCS_TEST_SQL_SERVER</c> they are reported SKIPPED, never "passed" without SQL.
/// </summary>
public sealed class TakeOwnershipSqlServerTests : IDisposable
{
    private static readonly DateTime Now = ClaimTestData.Now;
    private static readonly string[] Agent = [Roles.CsAgent];

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsTakeOwnershipTest_{Guid.NewGuid():N}";
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

    private sealed record Seed(int CallCenterId, int AccountingId, Guid[] Agents, Guid Worker);

    private Seed CreateAndSeedDatabase(int agentCount)
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

        var agents = new List<Guid>();
        for (var i = 0; i < agentCount; i++)
        {
            var id = Guid.NewGuid();
            context.Users.Add(new ApplicationUser
            {
                Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(), Email = $"{id:N}@example.test",
                SecurityStamp = Guid.NewGuid().ToString()
            });
            context.Employees.Add(new Employee(id, $"Agent {i}", isGeynessStaff: false, Now.AddDays(-60)));
            context.SaveChanges();
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(id, cc.DepartmentId, true, Now.AddDays(-50), null));
            context.SaveChanges();
            _accounts.Add(id, $"u-{id:N}", Roles.CsAgent);
            agents.Add(id);
        }

        // A former worker: a real employee (tickets reference employees) who is not a Call Center agent.
        var worker = Guid.NewGuid();
        context.Users.Add(new ApplicationUser
        {
            Id = worker, UserName = $"w-{worker:N}", NormalizedUserName = $"W-{worker:N}".ToUpperInvariant(), Email = $"{worker:N}@example.test",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        context.Employees.Add(new Employee(worker, "Former worker", isGeynessStaff: false, Now.AddDays(-90)));
        context.SaveChanges();

        return new Seed(cc.DepartmentId, accounting.DepartmentId, [.. agents], worker);
    }

    private long AddTicket(Seed seed, int n, Action<Ticket>? shape = null, Action<TigerCsDbContext, long>? after = null, int? department = null)
    {
        using var context = CreateContext();
        var ticket = ClaimTestData.Open($"TG-OWN-20261102-{n:D4}", department ?? seed.AccountingId, ageHours: 24 * 30 + n);
        shape?.Invoke(ticket);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        if (after is not null)
        {
            after(context, ticket.TicketId);
            context.SaveChanges();
        }

        return ticket.TicketId;
    }

    /// <summary>The real service graph over one DbContext - what the API's DI container builds per request.</summary>
    private (TicketClaimAppService Claim, TicketClaimPolicy Policy, TicketQueryAppService Query) Compose(TigerCsDbContext db, ITicketClaimPool? poolOverride = null)
    {
        var userDepartments = new UserDepartmentAssignmentRepository(db);
        var auto = new TicketAutoAssignmentService(
            new RequestTypeAssignmentRuleRepository(db), new DepartmentWorkflowSettingsRepository(db), userDepartments,
            new TicketAssignmentRepository(db), new AuditEntryWriter(db, TimeProvider.System), new AssignmentRotationRepository(db),
            new DepartmentRepository(db), roleReader: _accounts, accountManager: _accounts, serviceIdentities: null,
            workflowEventRepository: new TicketWorkflowEventRepository(db));
        var pool = poolOverride ?? new TicketClaimPool(db);
        var policy = new TicketClaimPolicy(pool, auto, new UserPermissionOverrideRepository(db), serviceIdentities: null);
        var claim = new TicketClaimAppService(
            new TicketRepository(db), new TicketAssignmentRepository(db), new TicketingUnitOfWork(db), new AuditEntryWriter(db, TimeProvider.System),
            TimeProvider.System, policy, auto, new TicketWorkflowEventRepository(db));
        var query = new TicketQueryAppService(
            new TicketRepository(db), userDepartments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(),
            ReopenPolicy.Default, TimeProvider.System, handlerRepository: new TicketInteractionHandlerRepository(db), claimPolicy: policy);
        return (claim, policy, query);
    }

    private (Guid? Owner, int DepartmentId, List<TicketAssignment> Assignments, int TakeAudits, int TransferEvents) Read(long ticketId)
    {
        using var read = CreateContext();
        var ticket = read.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId);
        return (
            ticket.CurrentOwnerEmployeeId,
            ticket.CurrentDepartmentId,
            read.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToList(),
            read.AuditEntries.AsNoTracking().Count(a => a.EntityId == ticketId.ToString() && a.Action == "TakeOwnership"),
            read.TicketWorkflowEvents.AsNoTracking().Count(e => e.TicketId == ticketId && e.EventType == WorkflowEventType.TicketTransferred));
    }

    // =====================================================================
    // The predicate, in SQL
    // =====================================================================

    [VisibilitySqlServerFact]
    public async Task TheSharedPredicate_TranslatesToSql_AndTheListAgreesWithTheSingleTicketDecision_ForEveryShape()
    {
        var seed = CreateAndSeedDatabase(2);
        var me = seed.Agents[0];
        var worker = seed.Worker;
        long n = 0;
        long Next() => ++n;

        var shapes = new Dictionary<string, (long Id, TicketClaimStatus Expected)>();
        void Add(string name, TicketClaimStatus expected, Action<Ticket>? shape = null, Action<TigerCsDbContext, long>? after = null, int? department = null) =>
            shapes[name] = (AddTicket(seed, (int)Next(), shape, after, department), expected);

        Add("open, no owner", TicketClaimStatus.Available);
        Add("in progress, no owner", TicketClaimStatus.Available, t => ClaimTestData.MakeInProgressUnowned(t, worker));
        Add("already in the call center queue", TicketClaimStatus.Available, department: seed.CallCenterId);
        Add("owned by another", TicketClaimStatus.Owned, t => t.AssignTo(worker));
        Add("resolved", TicketClaimStatus.Terminal, t => ClaimTestData.MakeResolvedUnowned(t, worker));
        Add("closed", TicketClaimStatus.Terminal, t => ClaimTestData.MakeClosedUnowned(t, worker));
        Add("active AI conversation", TicketClaimStatus.ActiveAiConversation, after: (db, id) => db.TicketInteractions.Add(ClaimTestData.Genesys(id)));
        Add("AI conversation, human requested", TicketClaimStatus.Available, after: (db, id) => db.TicketInteractions.Add(ClaimTestData.Genesys(id, requiresHuman: true)));
        Add("AI conversation, ended", TicketClaimStatus.Available, after: (db, id) => db.TicketInteractions.Add(ClaimTestData.Genesys(id, ended: true)));
        Add("AI conversation, closed for inactivity", TicketClaimStatus.Available, after: (db, id) => db.TicketInteractions.Add(ClaimTestData.Genesys(id, inactivityClosed: true)));
        Add("AI conversation, taken by an agent", TicketClaimStatus.Available, after: (db, id) => db.TicketInteractions.Add(ClaimTestData.Genesys(id, handledBy: worker)));
        Add("pending approval", TicketClaimStatus.PendingApproval, after: (db, id) => db.TicketApprovals.Add(ClaimTestData.PendingApproval(id, worker)));
        Add("pending approval AND active AI", TicketClaimStatus.PendingApproval, after: (db, id) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id));
            db.TicketApprovals.Add(ClaimTestData.PendingApproval(id, worker));
        });
        Add("open human handoff on a live AI conversation", TicketClaimStatus.Available, after: (db, id) =>
        {
            var interaction = ClaimTestData.Genesys(id);
            db.TicketInteractions.Add(interaction);
            db.SaveChanges();
            db.TicketAgentHandoffs.Add(new TicketAgentHandoff(
                id, interaction.TicketInteractionId, seed.CallCenterId, WellKnownChannels.Phone, Now.AddDays(-1), Now.AddDays(-1)));
        });

        using var db = CreateContext();
        var (_, policy, query) = Compose(db);
        var pool = new TicketClaimPool(db);

        // 1. the single-ticket decision gives the expected reason for every shape
        foreach (var (name, (id, expected)) in shapes)
        {
            Assert.True(expected == await pool.GetStatusAsync(id), $"{name}: expected {expected}");
        }

        // 2. the pool is a separate, explicit view: "available to take" holds exactly the Available ones, while the personal list holds only
        //    the Genesys-originated part of it (the CS intake / classification queue) - never the manual, non-Genesys pool.
        var expectedIds = shapes.Values.Where(s => s.Expected == TicketClaimStatus.Available).Select(s => s.Id).Order().ToArray();
        var genesysOriginated = new[]
        {
            "AI conversation, human requested", "AI conversation, ended", "AI conversation, closed for inactivity",
            "AI conversation, taken by an agent", "open human handoff on a live AI conversation"
        };
        var intakeIds = genesysOriginated.Select(name => shapes[name].Id).Order().ToArray();
        var nonIntakeIds = expectedIds.Except(intakeIds).ToArray();
        var listed = await query.GetQueueAsync(me, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 200));
        Assert.Equal(intakeIds, listed.Items.Select(i => i.TicketId).Intersect(expectedIds).Order());
        Assert.Empty(listed.Items.Select(i => i.TicketId).Intersect(nonIntakeIds));
        Assert.Equal(listed.Items.Count, listed.TotalCount);

        var available = await query.GetQueueAsync(me, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 200, AvailableToTake: true));
        Assert.Equal(expectedIds, available.Items.Select(i => i.TicketId).Order());

        // 3. department filters narrow the personal list; they do not bring the pool back
        var ccFilter = await query.GetQueueAsync(me, Agent, new TicketListRequestDto(seed.CallCenterId, null, null, null, null, null, null, null, null, 1, 200));
        Assert.Empty(ccFilter.Items.Select(i => i.TicketId).Intersect(nonIntakeIds));
        var accountingFilter = await query.GetQueueAsync(me, Agent, new TicketListRequestDto(seed.AccountingId, null, null, null, null, null, null, null, null, 1, 200));
        Assert.All(accountingFilter.Items, i => Assert.Equal(seed.AccountingId, i.CurrentDepartmentId));
        Assert.DoesNotContain(accountingFilter.Items, i => shapes["already in the call center queue"].Id == i.TicketId);

        // 4. every shape's detail flag is the policy's answer
        foreach (var (name, (id, expected)) in shapes.Where(s => s.Value.Expected == TicketClaimStatus.Available))
        {
            var detail = await query.GetDetailAsync(me, Agent, id);
            Assert.True(detail.Response!.CanTakeOwnership, name);
        }

        Assert.Equal(TicketClaimDenial.Owned, (await policy.DecideAsync(me, Agent, shapes["owned by another"].Id)).Denial);

        // 5. an agent who is NOT in the call center sees none of it
        var outsider = Guid.NewGuid();
        _accounts.Add(outsider, "outsider", Roles.CsAgent);
        var outsiderList = await query.GetQueueAsync(outsider, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 200));
        Assert.Empty(outsiderList.Items);
    }

    // =====================================================================
    // Concurrency
    // =====================================================================

    [VisibilitySqlServerFact]
    public async Task EightAgentsRacingForOneTicket_Truly_InParallel_ExactlyOneWins_TwelveRoundsRunning()
    {
        var seed = CreateAndSeedDatabase(8);
        var outcomes = new List<TicketMutationOutcome>();

        for (var round = 0; round < 12; round++)
        {
            var ticketId = AddTicket(seed, round + 1);
            using var gate = new ManualResetEventSlim(false);

            var attempts = seed.Agents.Select(agent => Task.Run(async () =>
            {
                using var db = CreateContext();
                var (claim, _, _) = Compose(db);
                gate.Wait();
                return (Agent: agent, Result: await claim.TakeOwnershipAsync(agent, Agent, ticketId, new TakeOwnershipRequestDto()));
            })).ToArray();

            await Task.Delay(100);
            gate.Set();
            var results = await Task.WhenAll(attempts);
            outcomes.AddRange(results.Select(r => r.Result.Outcome));

            var winners = results.Where(r => r.Result.Outcome == TicketMutationOutcome.Success).ToList();
            var winner = Assert.Single(winners);
            Assert.All(
                results.Where(r => r.Result.Outcome != TicketMutationOutcome.Success),
                r => Assert.Contains(r.Result.Outcome, new[] { TicketMutationOutcome.ConcurrencyConflict, TicketMutationOutcome.TicketAlreadyOwned }));

            var state = Read(ticketId);
            Assert.Equal(winner.Agent, state.Owner);                                   // the winner - and only the winner - owns it
            Assert.Equal(seed.CallCenterId, state.DepartmentId);
            var current = Assert.Single(state.Assignments);                            // exactly one assignment row, and it is current
            Assert.True(current.IsCurrent);
            Assert.Equal(winner.Agent, current.AssignedEmployeeId);
            Assert.Equal(1, state.TakeAudits);                                         // one take-over audit entry, one transfer event: nothing doubled
            Assert.Equal(1, state.TransferEvents);
        }

        Assert.Equal(12, outcomes.Count(o => o == TicketMutationOutcome.Success));
    }

    /// <summary>Wraps the real pool and runs <paramref name="afterStatusRead"/> once, right after the status is read - i.e. after the caller has decided to proceed and before it writes.</summary>
    private sealed class InterleavingPool(ITicketClaimPool inner, Func<Task> afterStatusRead) : ITicketClaimPool
    {
        private int _fired;

        public async Task<TicketClaimStatus> GetStatusAsync(long ticketId, CancellationToken cancellationToken = default)
        {
            var status = await inner.GetStatusAsync(ticketId, cancellationToken);
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await afterStatusRead();
            }

            return status;
        }
    }

    [VisibilitySqlServerFact]
    public async Task ASecondAgentWhoDecidedBeforeTheFirstCommitted_IsRejectedByTheRowVersion_AndNothingOfTheirAttemptIsWritten()
    {
        var seed = CreateAndSeedDatabase(2);
        var (loser, winner) = (seed.Agents[0], seed.Agents[1]);
        var ticketId = AddTicket(seed, 1);

        TicketMutationResult? winnerResult = null;
        using var loserDb = CreateContext();
        var (loserClaim, _, _) = Compose(loserDb, new InterleavingPool(new TicketClaimPool(loserDb), async () =>
        {
            // The loser has loaded the ticket and been told it is Available. Now the other agent takes it, completely, before the loser writes.
            using var winnerDb = CreateContext();
            var (winnerClaim, _, _) = Compose(winnerDb);
            winnerResult = await winnerClaim.TakeOwnershipAsync(winner, Agent, ticketId, new TakeOwnershipRequestDto());
        }));

        var result = await loserClaim.TakeOwnershipAsync(loser, Agent, ticketId, new TakeOwnershipRequestDto());

        Assert.Equal(TicketMutationOutcome.Success, winnerResult!.Outcome);
        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        var state = Read(ticketId);
        Assert.Equal(winner, state.Owner);
        var row = Assert.Single(state.Assignments);
        Assert.Equal(winner, row.AssignedEmployeeId);
        Assert.Equal(1, state.TakeAudits);        // the loser's audit entry and workflow event rolled back with its transaction
        Assert.Equal(1, state.TransferEvents);

        // and a retry by the loser now says so plainly
        using var retryDb = CreateContext();
        var (retryClaim, _, _) = Compose(retryDb);
        Assert.Equal(TicketMutationOutcome.TicketAlreadyOwned, (await retryClaim.TakeOwnershipAsync(loser, Agent, ticketId, new TakeOwnershipRequestDto())).Outcome);
    }

    [VisibilitySqlServerFact]
    public async Task AStaleClientRowVersion_IsRejected_AndChangesNothing()
    {
        var seed = CreateAndSeedDatabase(1);
        var ticketId = AddTicket(seed, 1);
        byte[] staleVersion;
        using (var read = CreateContext())
        {
            staleVersion = read.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId).RowVersion;
        }

        // Someone changes the ticket (a note-less touch: bump the summary) after the agent read it.
        using (var other = CreateContext())
        {
            other.Database.ExecuteSql($"UPDATE Tickets SET RequestSummary = N'edited' WHERE TicketId = {ticketId}");
        }

        using var db = CreateContext();
        var (claim, _, _) = Compose(db);
        var result = await claim.TakeOwnershipAsync(seed.Agents[0], Agent, ticketId, new TakeOwnershipRequestDto(staleVersion));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        var state = Read(ticketId);
        Assert.Null(state.Owner);
        Assert.Equal(seed.AccountingId, state.DepartmentId);
        Assert.Empty(state.Assignments);
        Assert.Equal(0, state.TakeAudits);
    }

    [VisibilitySqlServerFact]
    public void TheDatabaseItself_RefusesASecondCurrentAssignment_EvenIfEveryApplicationCheckFailed()
    {
        var seed = CreateAndSeedDatabase(2);
        var ticketId = AddTicket(seed, 1);
        using var db = CreateContext();
        db.TicketAssignments.Add(new TicketAssignment(ticketId, seed.Agents[0], seed.CallCenterId, Now, seed.Agents[0]));
        db.SaveChanges();
        db.TicketAssignments.Add(new TicketAssignment(ticketId, seed.Agents[1], seed.CallCenterId, Now, seed.Agents[1]));

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
