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
using TigerCS.Tests.Ticketing;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// Closed is terminal, on a REAL SQL Server: a closure that commits while a Genesys event is in flight wins (the row version rejects the stale writer),
/// stale and duplicate events find the Closed ticket and write nothing, only the explicit Reopen opens it again, and the AI-owner column persists and
/// clears. A throw-away database is created and dropped per test; without <c>TIGERCS_TEST_SQL_SERVER</c> they are reported SKIPPED.
/// </summary>
public sealed class ClosedTicketSqlServerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Integration = Guid.NewGuid();

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsClosedTest_{Guid.NewGuid():N}";
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
    private (long TicketId, string ConversationId) AddTicket(Seed seed, int n, bool inCallCenter = false)
    {
        using var context = CreateContext();
        var ticket = Ticket.CreateUnclassified($"TG-ANS-20261102-{n:D4}", inCallCenter ? seed.CallCenterId : seed.CustomerServiceId, $"Answer {n}", Now.AddHours(-n));
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

    private static void Close(TigerCsDbContext db, long ticketId, Guid owner)
    {
        var ticket = db.Tickets.Single(t => t.TicketId == ticketId);
        ticket.AssignTo(owner);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();
        db.SaveChanges();
    }

    [VisibilitySqlServerFact]
    public async Task AClosureThatCommitsWhileAConnectEventIsInFlight_Wins_AndTheStaleEventIsRejected()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1, inCallCenter: true);

        using var eventContext = CreateContext();
        var staleTicket = await eventContext.Tickets.SingleAsync(t => t.TicketId == ticketId);      // loaded while still Open
        var staleInteraction = await eventContext.TicketInteractions.FirstAsync(i => i.TicketId == ticketId);

        using (var closer = CreateContext())
        {
            Close(closer, ticketId, seed.AgentB);                                                   // the closure commits first
        }

        var result = await Compose(eventContext).ApplyConnectedAgentAsync(Integration, staleTicket, staleInteraction, seed.GenesysA, "Routing");

        Assert.Equal(AnswerAssignmentOutcome.ConcurrencyConflict, result.Outcome);                  // nothing applied
        using var read = CreateContext();
        var stored = await read.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        Assert.Equal(TicketStatus.Closed, stored.TicketStatus);
        Assert.Null(stored.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(read.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId), a => a.AssignedEmployeeId == seed.AgentA);
    }

    [VisibilitySqlServerFact]
    public async Task StaleAndDuplicateEvents_OnAClosedTicket_WriteNothing()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);
        using (var closer = CreateContext())
        {
            Close(closer, ticketId, seed.AgentB);
        }

        var before = Read(ticketId);
        for (var delivery = 0; delivery < 3; delivery++)      // the same late event, replayed
        {
            using var db = CreateContext();
            var outcome = await ConnectAsync(Compose(db), db, ticketId, seed.GenesysA);
            Assert.Equal(AnswerAssignmentOutcome.NotApplicable, outcome.Outcome);
        }

        var after = Read(ticketId);
        Assert.Equal(before.Rows.Count, after.Rows.Count);
        Assert.Equal(before.Owner, after.Owner);
        using var read = CreateContext();
        var stored = await read.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        Assert.Equal(TicketStatus.Closed, stored.TicketStatus);
        Assert.Equal(CallCenterOrOriginal(seed, stored), stored.CurrentDepartmentId);               // never moved into the Call Center
    }

    private static int CallCenterOrOriginal(Seed seed, Ticket ticket) => ticket.OriginatingDepartmentId == seed.CustomerServiceId ? seed.CustomerServiceId : ticket.CurrentDepartmentId;

    [VisibilitySqlServerFact]
    public async Task OnlyTheExplicitReopen_OpensAClosedTicket_AndItIsRecorded()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);
        using (var closer = CreateContext())
        {
            Close(closer, ticketId, seed.AgentB);
        }

        using (var reopen = CreateContext())
        {
            var ticket = await reopen.Tickets.SingleAsync(t => t.TicketId == ticketId);
            LegacyReopenedTicket.Apply(ticket, seed.CustomerServiceId);
            await reopen.SaveChangesAsync();
        }

        using var read = CreateContext();
        var stored = await read.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        Assert.Equal(TicketStatus.InProgress, stored.TicketStatus);
        Assert.Equal(1, stored.ReopenCount);
    }

    [VisibilitySqlServerFact]
    public async Task TheAiOwnerColumn_Persists_AndAPersonsAssignmentClearsIt()
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, _) = AddTicket(seed, 1);

        using (var db = CreateContext())
        {
            var ticket = await db.Tickets.SingleAsync(t => t.TicketId == ticketId);
            Assert.True(ticket.AssignToAiAgent(Integration));
            await db.SaveChangesAsync();
        }

        using (var read = CreateContext())
        {
            var stored = await read.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
            Assert.Equal(Integration, stored.AiAgentOwnerEmployeeId);
            Assert.Null(stored.CurrentOwnerEmployeeId);
        }

        using (var db = CreateContext())
        {
            var outcome = await ConnectAsync(Compose(db), db, ticketId, seed.GenesysA);
            Assert.Equal(AnswerAssignmentOutcome.AssignedToAnsweringAgent, outcome.Outcome);
        }

        using var after = CreateContext();
        var final = await after.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        Assert.Equal(seed.AgentA, final.CurrentOwnerEmployeeId);
        Assert.Null(final.AiAgentOwnerEmployeeId);
    }
}
