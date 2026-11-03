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

/// <summary>A theory that needs a REAL SQL Server (same switch as <see cref="VisibilitySqlServerFactAttribute"/>); otherwise reported SKIPPED.</summary>
public sealed class VisibilitySqlServerTheoryAttribute : TheoryAttribute
{
    public VisibilitySqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable)))
        {
            Skip = $"Not executed: {VisibilitySqlServerFactAttribute.Variable} is not set (real SQL Server required).";
        }
    }
}

/// <summary>
/// The provisional-owner lifecycle against a REAL SQL Server and the real repositories, for every digital channel: Call Center Round Robin
/// gives a ticket to one eligible agent; Genesys then confirms a (different) eligible agent CONNECTED and that agent becomes the owner. Proves on
/// real tables: the superseded assignment row and the audit trail are kept, SLA/lifecycle columns are untouched, a person's work blocks the
/// replacement, only eligible agents are ever picked (deactivated employees included, which only SQL can exclude), and truly parallel confirmations
/// never leave two owners or two current rows. Without <c>TIGERCS_TEST_SQL_SERVER</c> they are reported SKIPPED.
/// </summary>
public sealed class ProvisionalOwnerSqlServerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Integration = Guid.NewGuid();

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsProvisionalTest_{Guid.NewGuid():N}";
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

    public static TheoryData<byte> DigitalChannelIds => new(
        WellKnownChannels.WhatsApp, WellKnownChannels.LiveChat, WellKnownChannels.Instagram,
        WellKnownChannels.Facebook, WellKnownChannels.SocialMediaDirectMessage);

    private sealed record Agent(Guid Id, string GenesysId);

    private sealed record Seed(int CallCenterId, int CustomerServiceId, Agent[] Eligible, Agent Deactivated, Agent Supervisor, Agent Locked, Agent NotMember);

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

        Agent Add(string n, string role, bool member = true, bool deactivated = false, bool locked = false)
        {
            var id = Guid.NewGuid();
            var genesysId = $"genesys-{n}";
            context.Users.Add(new ApplicationUser
            {
                Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(), Email = $"{id:N}@example.test",
                SecurityStamp = Guid.NewGuid().ToString(), GenesysUserId = genesysId
            });
            var employee = new Employee(id, n, isGeynessStaff: false, Now.AddDays(-60));
            if (deactivated)
            {
                employee.Deactivate(Now.AddDays(-1));
            }

            context.Employees.Add(employee);
            context.SaveChanges();
            if (member)
            {
                context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(id, cc.DepartmentId, true, Now.AddDays(-50), null));
                context.SaveChanges();
            }

            _accounts.Add(id, $"u-{id:N}", role);
            if (locked)
            {
                _accounts.LockOut(id);
            }

            return new Agent(id, genesysId);
        }

        var eligible = new[] { Add("A", Roles.CsAgent), Add("B", Roles.CsAgent), Add("C", Roles.CsAgent) };
        return new Seed(
            cc.DepartmentId, cs.DepartmentId, eligible,
            Deactivated: Add("Gone", Roles.CsAgent, deactivated: true),
            Supervisor: Add("Sup", Roles.CsSupervisor),
            Locked: Add("Locked", Roles.CsAgent, locked: true),
            NotMember: Add("Outsider", Roles.CsAgent, member: false));
    }

    /// <summary>A Call Center ticket with its originating Genesys interaction on the given channel, given a PROVISIONAL Round Robin owner.</summary>
    private async Task<(long TicketId, Guid Provisional)> AddProvisionallyAssignedTicketAsync(Seed seed, int n, byte channelId)
    {
        long ticketId;
        using (var context = CreateContext())
        {
            var ticket = Ticket.CreateUnclassified($"TG-PRV-20261103-{n:D4}", seed.CallCenterId, $"Provisional {n}", Now.AddHours(-n));
            context.Tickets.Add(ticket);
            context.SaveChanges();
            ticketId = ticket.TicketId;
            context.TicketInteractions.Add(TicketInteraction.CreateFromGenesys(
                ticket.TicketId, channelId, "+971500000000", $"conv-{Guid.NewGuid():N}"[..20],
                calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
                interactionStartedAtUtc: Now.AddHours(-n), direction: "Inbound", createdAtUtc: Now.AddHours(-n),
                isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8]));
            context.SaveChanges();
        }

        using var db = CreateContext();
        var ticketToAssign = await db.Tickets.SingleAsync(t => t.TicketId == ticketId);
        var result = await Auto(db).AssignCallCenterIntakeAsync(ticketToAssign, Now, Guid.NewGuid());
        Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, result.Outcome);
        await new TicketingUnitOfWork(db).SaveChangesAsync();
        return (ticketId, ticketToAssign.CurrentOwnerEmployeeId!.Value);
    }

    private TicketAutoAssignmentService Auto(TigerCsDbContext db) => new(
        new RequestTypeAssignmentRuleRepository(db), new DepartmentWorkflowSettingsRepository(db), new UserDepartmentAssignmentRepository(db),
        new TicketAssignmentRepository(db), new AuditEntryWriter(db, TimeProvider.System), new AssignmentRotationRepository(db),
        new DepartmentRepository(db), roleReader: _accounts, accountManager: _accounts, serviceIdentities: null,
        workflowEventRepository: new TicketWorkflowEventRepository(db));

    private GenesysAnsweringAgentAssignmentService Compose(TigerCsDbContext db)
    {
        var userDepartments = new UserDepartmentAssignmentRepository(db);
        var resolution = new GenesysAgentResolutionAppService(new GenesysAgentMappingRepository(db), _accounts, userDepartments);
        return new GenesysAnsweringAgentAssignmentService(
            resolution, Auto(db), new TicketAgentHandoffRepository(db), new TicketingUnitOfWork(db), TimeProvider.System);
    }

    private static async Task<AnswerAssignmentResult> ConnectAsync(
        GenesysAnsweringAgentAssignmentService service, TigerCsDbContext db, long ticketId, string genesysAgentId)
    {
        var ticket = await db.Tickets.SingleAsync(t => t.TicketId == ticketId);
        var interaction = await db.TicketInteractions.FirstAsync(i => i.TicketId == ticketId);
        return await service.ApplyConnectedAgentAsync(Integration, ticket, interaction, genesysAgentId, "Routing");
    }

    private Agent Another(Seed seed, Guid provisional) => seed.Eligible.First(a => a.Id != provisional);

    [VisibilitySqlServerTheory]
    [MemberData(nameof(DigitalChannelIds))]
    public async Task ConfirmedAgent_ReplacesTheProvisionalOwner_KeepingRowsAuditAndSla(byte channelId)
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, provisional) = await AddProvisionallyAssignedTicketAsync(seed, 1, channelId);
        Assert.Contains(provisional, seed.Eligible.Select(a => a.Id));
        var confirmed = Another(seed, provisional);

        Ticket before;
        using (var read = CreateContext())
        {
            before = await read.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        }

        using (var db = CreateContext())
        {
            var result = await ConnectAsync(Compose(db), db, ticketId, confirmed.GenesysId);
            Assert.Equal(AnswerAssignmentOutcome.AssignedToAnsweringAgent, result.Outcome);
        }

        using var after = CreateContext();
        var ticket = await after.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        Assert.Equal(confirmed.Id, ticket.CurrentOwnerEmployeeId);
        Assert.Equal((before.TicketStatus, before.PriorityId, before.CreatedAtUtc, before.SlaState, before.OriginatingDepartmentId, before.CurrentDepartmentId),
            (ticket.TicketStatus, ticket.PriorityId, ticket.CreatedAtUtc, ticket.SlaState, ticket.OriginatingDepartmentId, ticket.CurrentDepartmentId));

        var rows = await after.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).OrderBy(a => a.TicketAssignmentId).ToListAsync();
        Assert.Equal(2, rows.Count);                                         // nothing deleted: provisional row + confirmed row
        Assert.False(rows[0].IsCurrent);
        Assert.Equal(provisional, rows[0].AssignedEmployeeId);
        Assert.True(rows[1].IsCurrent);
        Assert.Equal(confirmed.Id, rows[1].AssignedEmployeeId);

        var audits = await after.AuditEntries.AsNoTracking().Where(a => a.EntityId == ticketId.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains("AutoAssign", audits);
        Assert.Contains("AssignOnAnswer", audits);
    }

    [VisibilitySqlServerTheory]
    [MemberData(nameof(DigitalChannelIds))]
    public async Task APersonsWork_BlocksTheReplacement(byte channelId)
    {
        var seed = CreateAndSeedDatabase();
        var (ticketId, provisional) = await AddProvisionallyAssignedTicketAsync(seed, 1, channelId);
        using (var db = CreateContext())
        {
            db.Tickets.Single(t => t.TicketId == ticketId).RecordFirstHumanResponse(Now);
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var result = await ConnectAsync(Compose(db), db, ticketId, Another(seed, provisional).GenesysId);
            Assert.Equal(AnswerAssignmentOutcome.HumanOwnerKept, result.Outcome);
        }

        using var after = CreateContext();
        Assert.Equal(provisional, after.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId).CurrentOwnerEmployeeId);
        Assert.Single(after.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId));
    }

    [VisibilitySqlServerFact]
    public async Task IneligibleMembers_AreNeverPicked_ByCallCenterRoundRobin()
    {
        var seed = CreateAndSeedDatabase();
        var owners = new List<Guid>();
        for (var i = 1; i <= 12; i++)
        {
            owners.Add((await AddProvisionallyAssignedTicketAsync(seed, i, WellKnownChannels.WhatsApp)).Provisional);
        }

        Assert.All(owners, o => Assert.Contains(o, seed.Eligible.Select(a => a.Id)));   // not the deactivated, locked, supervisor or non-member
        foreach (var agent in seed.Eligible)
        {
            Assert.Equal(4, owners.Count(o => o == agent.Id));                      // 12 tickets over 3 eligible agents: strictly 4 each
        }
    }

    [VisibilitySqlServerFact]
    public async Task ParallelConfirmations_OfDifferentAgents_OnAProvisionallyOwnedTicket_LeaveOneOwnerAndOneCurrentRow()
    {
        var seed = CreateAndSeedDatabase();

        for (var round = 1; round <= 12; round++)
        {
            var (ticketId, provisional) = await AddProvisionallyAssignedTicketAsync(seed, round, WellKnownChannels.Instagram);
            var contenders = seed.Eligible.Where(a => a.Id != provisional).ToArray();
            using var dbA = CreateContext();
            using var dbB = CreateContext();
            var start = new TaskCompletionSource();

            async Task Run(TigerCsDbContext db, string genesysId)
            {
                await start.Task;
                try { await ConnectAsync(Compose(db), db, ticketId, genesysId); } catch (Exception) { /* a deadlock victim or loser: the invariants below are what matter */ }
            }

            var a = Run(dbA, contenders[0].GenesysId);
            var b = Run(dbB, contenders[1].GenesysId);
            start.SetResult();
            await Task.WhenAll(a, b);

            using var read = CreateContext();
            var owner = read.Tickets.AsNoTracking().Single(t => t.TicketId == ticketId).CurrentOwnerEmployeeId;
            var rows = read.TicketAssignments.AsNoTracking().Where(r => r.TicketId == ticketId).ToList();
            Assert.Contains(owner, contenders.Select(c => (Guid?)c.Id));                 // one of the confirmed agents, never left on the stale provisional pick
            var current = Assert.Single(rows, r => r.IsCurrent);                        // never two current owners
            Assert.Equal(owner, current.AssignedEmployeeId);
            Assert.Contains(rows, r => r.AssignedEmployeeId == provisional && !r.IsCurrent);   // provisional history retained
        }
    }
}
