using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// "Who answered owns the ticket" against a REAL SQL Server, composed from the real repositories and unit of work. The in-memory tests prove the
/// rules; these prove the two database guarantees behind "never create multiple current owners": the ticket's row version (a stale second
/// writer is rejected, not merged) and the filtered unique index on <c>TicketAssignments(TicketId) WHERE IsCurrent = 1</c> (a second current row
/// is impossible even if everything else failed). Without <c>TIGERCS_TEST_SQL_SERVER</c> they are reported SKIPPED, never "passed" without SQL.
/// </summary>
public sealed class AnsweringAgentAssignmentSqlServerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Integration = Guid.NewGuid();

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsAnswerTest_{Guid.NewGuid():N}";
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

    private sealed record Seed(int CallCenterId, int CustomerServiceId, Guid AgentA, string GenesysA, Guid AgentB, string GenesysB);

    private Seed CreateAndSeedDatabase()
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
        var cs = new Department("Customer Service", "CS");
        context.Departments.AddRange(cc, cs);
        context.SaveChanges();

        Guid AddAgent(string name, string genesysUserId)
        {
            var id = Guid.NewGuid();
            context.Users.Add(new ApplicationUser
            {
                Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(), Email = $"{id:N}@example.test",
                SecurityStamp = Guid.NewGuid().ToString(), GenesysUserId = genesysUserId
            });
            context.Employees.Add(new Employee(id, name, isGeynessStaff: false, Now.AddDays(-60)));
            context.SaveChanges();
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(id, cc.DepartmentId, true, Now.AddDays(-50), null));
            context.SaveChanges();
            _accounts.Add(id, $"u-{id:N}", Roles.CsAgent);
            return id;
        }

        var a = AddAgent("Agent A", "genesys-A");
        var b = AddAgent("Agent B", "genesys-B");
        return new Seed(cc.DepartmentId, cs.DepartmentId, a, "genesys-A", b, "genesys-B");
    }

    /// <summary>A Customer Service ticket with its originating Genesys interaction (the shape an AI-handled conversation has).</summary>
    private (long TicketId, string ConversationId) AddTicket(Seed seed, int n)
    {
        using var context = CreateContext();
        var ticket = Ticket.CreateUnclassified($"TG-ANS-20261102-{n:D4}", seed.CustomerServiceId, $"Answer {n}", Now.AddHours(-n));
        context.Tickets.Add(ticket);
        context.SaveChanges();
        var conversation = $"conv-{Guid.NewGuid():N}"[..20];
        context.TicketInteractions.Add(TicketInteraction.CreateFromGenesys(
            ticket.TicketId, WellKnownChannels.Phone, "+971500000000", conversation,
            calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
            interactionStartedAtUtc: Now.AddHours(-n), direction: "Inbound", createdAtUtc: Now.AddHours(-n),
            isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8]));
        context.SaveChanges();
        return (ticket.TicketId, conversation);
    }

    /// <summary>The real service graph over one DbContext - what the API's DI container builds per request.</summary>
    private GenesysAnsweringAgentAssignmentService Compose(TigerCsDbContext db)
    {
        var userDepartments = new UserDepartmentAssignmentRepository(db);
        var auto = new TicketAutoAssignmentService(
            new RequestTypeAssignmentRuleRepository(db), new DepartmentWorkflowSettingsRepository(db), userDepartments,
            new TicketAssignmentRepository(db), new AuditEntryWriter(db, TimeProvider.System), new AssignmentRotationRepository(db),
            new DepartmentRepository(db), roleReader: _accounts, accountManager: _accounts, serviceIdentities: null,
            workflowEventRepository: new TicketWorkflowEventRepository(db));
        var resolution = new GenesysAgentResolutionAppService(new GenesysAgentMappingRepository(db), _accounts, userDepartments);
        return new GenesysAnsweringAgentAssignmentService(
            resolution, auto, new TicketAgentHandoffRepository(db), new TicketingUnitOfWork(db), TimeProvider.System);
    }

    private static async Task<AnswerAssignmentResult> ConnectAsync(
        GenesysAnsweringAgentAssignmentService service, TigerCsDbContext db, long ticketId, string genesysAgentId)
    {
        var ticket = await db.Tickets.SingleAsync(t => t.TicketId == ticketId);
        var interaction = await db.TicketInteractions.FirstAsync(i => i.TicketId == ticketId);
        return await service.ApplyConnectedAgentAsync(Integration, ticket, interaction, genesysAgentId, "Routing");
    }

    private (Guid? Owner, List<TicketAssignment> Rows) Read(long ticketId)
    {
        using var read = CreateContext();
        var owner = read.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId).CurrentOwnerEmployeeId;
        return (owner, read.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToList());
    }

    [VisibilitySqlServerFact]
    public async Task AnsweringAgent_IsAssignedDirectly_AndPersistsAsOneOwnerOneCurrentRow_WithoutTouchingTheRotation()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);

        using (var db = CreateContext())
        {
            var result = await ConnectAsync(Compose(db), db, ticketId, seed.GenesysB);
            Assert.Equal(AnswerAssignmentOutcome.AssignedToAnsweringAgent, result.Outcome);
        }

        var (owner, rows) = Read(ticketId);
        Assert.Equal(seed.AgentB, owner);
        var current = Assert.Single(rows, r => r.IsCurrent);
        Assert.Equal(seed.AgentB, current.AssignedEmployeeId);
        Assert.Equal(seed.AgentB, current.AssigningActorEmployeeId);       // the answering agent's own act => human ownership
        Assert.Equal(seed.CallCenterId, current.AssignedDepartmentId);

        using var read = CreateContext();
        Assert.Equal(seed.CallCenterId, read.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId).CurrentDepartmentId);
        Assert.Contains(read.AuditEntries.AsNoTracking().ToList(), e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString() && e.ActorEmployeeId == seed.AgentB);
        Assert.Contains(read.TicketWorkflowEvents.AsNoTracking().ToList(), e =>
            e.TicketId == ticketId && e.EventType == WorkflowEventType.TicketTransferred && e.TransferReasonCode == (byte)TicketTransferReason.SystemRouting);
        Assert.DoesNotContain(read.DepartmentAssignmentRotations.AsNoTracking().ToList(), r => r.LastAssignedEmployeeId is not null);   // no Round Robin turn spent
    }

    [VisibilitySqlServerFact]
    public async Task DuplicateConnectedEvent_OnARealDatabase_WritesNothingMore()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);

        for (var i = 0; i < 3; i++)
        {
            using var db = CreateContext();
            await ConnectAsync(Compose(db), db, ticketId, seed.GenesysA);
        }

        var (owner, rows) = Read(ticketId);
        Assert.Equal(seed.AgentA, owner);
        Assert.Single(rows);
        using var read = CreateContext();
        Assert.Single(read.AuditEntries.AsNoTracking().ToList(), e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

    [VisibilitySqlServerFact]
    public async Task StaleSecondWriter_IsRejectedByTheRowVersion_NotMergedIntoTwoOwners()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);

        // Two deliveries are in flight: both loaded the ticket while it was still unowned.
        using var first = CreateContext();
        using var second = CreateContext();
        var firstTicket = await first.Tickets.SingleAsync(t => t.TicketId == ticketId);
        var firstInteraction = await first.TicketInteractions.FirstAsync(i => i.TicketId == ticketId);
        var secondTicket = await second.Tickets.SingleAsync(t => t.TicketId == ticketId);
        var secondInteraction = await second.TicketInteractions.FirstAsync(i => i.TicketId == ticketId);

        var won = await Compose(first).ApplyConnectedAgentAsync(Integration, firstTicket, firstInteraction, seed.GenesysA, "Routing");
        var lost = await Compose(second).ApplyConnectedAgentAsync(Integration, secondTicket, secondInteraction, seed.GenesysB, "Routing");

        Assert.Equal(AnswerAssignmentOutcome.AssignedToAnsweringAgent, won.Outcome);
        Assert.Equal(AnswerAssignmentOutcome.ConcurrencyConflict, lost.Outcome);       // nothing applied; the event can be redelivered safely

        var (owner, rows) = Read(ticketId);
        Assert.Equal(seed.AgentA, owner);
        var only = Assert.Single(rows);
        Assert.Equal(seed.AgentA, only.AssignedEmployeeId);
        Assert.True(only.IsCurrent);

        // Redelivery of the loser's event now finds a human owner and changes nothing.
        using (var retry = CreateContext())
        {
            var again = await ConnectAsync(Compose(retry), retry, ticketId, seed.GenesysB);
            Assert.Equal(AnswerAssignmentOutcome.HumanOwnerKept, again.Outcome);
        }

        Assert.Equal(seed.AgentA, Read(ticketId).Owner);
    }

    [VisibilitySqlServerFact]
    public async Task UniqueIndex_RejectsASecondCurrentAssignment_EvenIfEverythingElseFailed()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);
        using (var db = CreateContext())
        {
            await ConnectAsync(Compose(db), db, ticketId, seed.GenesysA);
        }

        using var raw = CreateContext();
        raw.TicketAssignments.Add(new TicketAssignment(ticketId, seed.AgentB, seed.CallCenterId, Now, assigningActorEmployeeId: seed.AgentB));
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => raw.SaveChangesAsync());
        Assert.Contains("duplicate key", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);   // SQL Server 2601: the unique index

        Assert.Single(Read(ticketId).Rows, r => r.IsCurrent);
    }

    [VisibilitySqlServerFact]
    public async Task ParallelConnectedEventsForTwoAgents_AlwaysLeaveExactlyOneOwnerAndOneCurrentRow()
    {
        var seed = CreateAndSeedDatabase();

        for (var round = 1; round <= 12; round++)
        {
            var (ticketId, _) = AddTicket(seed, round);
            using var dbA = CreateContext();
            using var dbB = CreateContext();
            var start = new TaskCompletionSource();

            async Task<AnswerAssignmentOutcome?> Run(TigerCsDbContext db, string genesysId)
            {
                await start.Task;
                try
                {
                    return (await ConnectAsync(Compose(db), db, ticketId, genesysId)).Outcome;
                }
                catch (Exception)
                {
                    return null;     // a deadlock victim or any other loser: the invariant below is what matters
                }
            }

            var a = Run(dbA, seed.GenesysA);
            var b = Run(dbB, seed.GenesysB);
            start.SetResult();
            await Task.WhenAll(a, b);

            var (owner, rows) = Read(ticketId);
            Assert.NotNull(owner);                                          // someone answered, so someone owns it
            var current = Assert.Single(rows, r => r.IsCurrent);            // never two current owners
            Assert.Equal(owner, current.AssignedEmployeeId);                // owner and current assignment agree
            Assert.Contains(owner, new Guid?[] { seed.AgentA, seed.AgentB });
        }
    }

    [VisibilitySqlServerFact]
    public async Task AUnmappedAnsweringAgent_LeavesTheRealTicketUnownedInTheQueue_WithOneFlag_EvenWhenRedelivered()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);

        for (var i = 0; i < 2; i++)
        {
            using var db = CreateContext();
            var result = await ConnectAsync(Compose(db), db, ticketId, "genesys-nobody");
            Assert.True(result.Outcome is AnswerAssignmentOutcome.QueuedForManualAssignment or AnswerAssignmentOutcome.AlreadyFlagged);
        }

        var (owner, rows) = Read(ticketId);
        Assert.Null(owner);
        Assert.DoesNotContain(rows, r => r.IsCurrent);
        using var read = CreateContext();
        Assert.Equal(seed.CallCenterId, read.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId).CurrentDepartmentId);
        Assert.Single(read.TicketWorkflowEvents.AsNoTracking().ToList(), e => e.TicketId == ticketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }
}
