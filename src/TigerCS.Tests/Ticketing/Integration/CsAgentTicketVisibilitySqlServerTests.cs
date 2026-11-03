using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>A test that needs a REAL SQL Server. Without <see cref="Variable"/> it is reported SKIPPED, never "passed" without having run any SQL.</summary>
public sealed class VisibilitySqlServerFactAttribute : FactAttribute
{
    /// <summary>A connection string to a server whose login may CREATE and DROP databases; each test makes (and drops) a throw-away database of its own.</summary>
    public const string Variable = "TIGERCS_TEST_SQL_SERVER";

    public VisibilitySqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Not executed: {Variable} is not set (real SQL Server required).";
        }
    }
}

/// <summary>
/// The CS Agent visibility rules and the two visibility migrations against a real SQL Server: the access levels
/// (assigned / handled → Full, created only → ReadOnly, anything else → None), the list filters, and the
/// <c>AddTicketInteractionHandlers</c> / <c>AddTicketCreatedBy</c> migrations applied over existing rows. A throw-away database is
/// created and dropped per test; the connection string is never a shared or production database.
/// </summary>
public sealed class CsAgentTicketVisibilitySqlServerTests : IDisposable
{
    private const string BeforeVisibilityMigrations = "20261022090000_AddGenesysServiceRequestToTicketInteractions";
    private static readonly string[] Agent = [Roles.CsAgent];
    private static readonly DateTime Now = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsVisibilityTest_{Guid.NewGuid():N}";

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    private TigerCsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    private void CreateDatabase()
    {
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{_database}]";
        command.ExecuteNonQuery();
    }

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

    private static void AddPerson(TigerCsDbContext context, Guid id, string name)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(),
            Email = $"{id:N}@example.test", SecurityStamp = Guid.NewGuid().ToString()
        });
        context.Employees.Add(new Employee(id, name, isGeynessStaff: false, Now.AddDays(-60)));
    }

    [VisibilitySqlServerFact]
    public async Task AccessLevels_AndListFilters_HoldOnARealSqlServer_AssignedAndDepartmentWorkOnly()
    {
        CreateDatabase();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var departmentEmployee = Guid.NewGuid();
        long assigned, handled, createdOnly, others, createdThenTransferred, departmentWork, anotherAgents;
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            var cs = new Department("Customer Service", "CSV");
            var facilities = new Department("Facilities", "FACV");
            context.Departments.AddRange(cs, facilities);
            AddPerson(context, me, "Me");
            AddPerson(context, other, "Other");
            AddPerson(context, departmentEmployee, "Department Employee");
            context.SaveChanges();
            // Me and Other are CS Agents (the rule asks who holds the CS Agent role); the department employee is not.
            var csAgentRole = context.Roles.FirstOrDefault(r => r.Name == Roles.CsAgent);
            if (csAgentRole is null)
            {
                csAgentRole = new ApplicationRole(Roles.CsAgent, "CS Agent") { Id = Guid.NewGuid(), NormalizedName = Roles.CsAgent.ToUpperInvariant() };
                context.Roles.Add(csAgentRole);
                context.SaveChanges();
            }

            context.UserRoles.AddRange(
                new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = me, RoleId = csAgentRole.Id },
                new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = other, RoleId = csAgentRole.Id });
            context.SaveChanges();
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(me, cs.DepartmentId, true, Now.AddDays(-50), null));
            context.SaveChanges();

            Ticket Add(int n, int department, Guid? owner = null, Guid? creator = null)
            {
                var ticket = Ticket.CreateUnclassified($"TG-SQL-20261030-{n:D4}", department, $"Visibility {n}", Now.AddHours(-n));
                if (creator is { } c)
                {
                    ticket.RecordCreator(c);
                }

                context.Tickets.Add(ticket);
                context.SaveChanges();
                if (owner is { } o)
                {
                    ticket.AssignTo(o);
                    context.SaveChanges();
                }

                return ticket;
            }

            assigned = Add(1, cs.DepartmentId, owner: me, creator: other).TicketId;
            var handledTicket = Add(2, cs.DepartmentId, creator: other);
            handled = handledTicket.TicketId;
            var interaction = TicketInteraction.CreateFromGenesys(
                handledTicket.TicketId, WellKnownChannels.Phone, "+971500000000", "conv-" + Guid.NewGuid().ToString("N")[..12],
                calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
                interactionStartedAtUtc: Now.AddHours(-2), direction: "Inbound", createdAtUtc: Now.AddHours(-2),
                isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8]);
            context.TicketInteractions.Add(interaction);
            context.SaveChanges();
            context.TicketInteractionHandlers.Add(TicketInteractionHandler.Create(
                interaction.TicketInteractionId, handledTicket.TicketId, me, "genesys-me", InteractionHandlerSource.Routing, null, Now.AddHours(-2)));
            context.SaveChanges();
            createdOnly = Add(3, facilities.DepartmentId, creator: me).TicketId;
            others = Add(4, facilities.DepartmentId, creator: other).TicketId;
            createdThenTransferred = Add(5, cs.DepartmentId, creator: me).TicketId;
            context.Tickets.Find(createdThenTransferred)!.TransferToDepartment(facilities.DepartmentId);
            context.SaveChanges();

            // Department work: a department employee holds it; I held it before and am the latest CS Agent on its history.
            departmentWork = Add(6, facilities.DepartmentId, owner: departmentEmployee, creator: me).TicketId;
            var history = new TicketAssignment(departmentWork, me, cs.DepartmentId, Now.AddHours(-3), assigningActorEmployeeId: null);
            history.MarkSuperseded();
            context.TicketAssignments.Add(history);
            context.SaveChanges();

            // Another CS Agent's ticket that I created AND handled: not mine.
            var theirs = Add(7, cs.DepartmentId, owner: other, creator: me);
            anotherAgents = theirs.TicketId;
            var theirInteraction = TicketInteraction.CreateFromGenesys(
                theirs.TicketId, WellKnownChannels.Phone, "+971500000001", "conv-" + Guid.NewGuid().ToString("N")[..12],
                calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
                interactionStartedAtUtc: Now.AddHours(-2), direction: "Inbound", createdAtUtc: Now.AddHours(-2),
                isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8]);
            context.TicketInteractions.Add(theirInteraction);
            context.SaveChanges();
            context.TicketInteractionHandlers.Add(TicketInteractionHandler.Create(
                theirInteraction.TicketInteractionId, theirs.TicketId, me, "genesys-me", InteractionHandlerSource.Routing, null, Now.AddHours(-2)));
            context.SaveChanges();
        }

        using var read = CreateContext();
        var assignments = new UserDepartmentAssignmentRepository(read);
        var handlers = new TicketInteractionHandlerRepository(read);

        async Task<TicketAccessLevel> AccessAsync(long id) => await TicketVisibilityRule.GetTicketAccessAsync(
            assignments, handlers, me, Agent, (await read.Tickets.FindAsync(id))!);

        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(assigned));
        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(departmentWork));      // a department employee works it; I am responsible
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(handled));             // handled on Genesys, but unassigned
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(createdOnly));         // created by me, unassigned
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(createdThenTransferred));
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(anotherAgents));       // another CS Agent's, though I created and handled it
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(others));

        var query = new TicketQueryAppService(
            new TicketRepository(read), assignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(),
            ReopenPolicy.Default, TimeProvider.System, handlerRepository: handlers);
        var all = await query.GetQueueAsync(me, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100));
        Assert.Equal(new[] { assigned, departmentWork }.Order(), all.Items.Select(i => i.TicketId).Order());
        Assert.Equal(all.Items.Count, all.TotalCount);

        var mine = await query.GetQueueAsync(me, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100, CreatedByMe: true));
        Assert.Equal(new[] { departmentWork }.Order(), mine.Items.Select(i => i.TicketId).Order());
    }

    [VisibilitySqlServerFact]
    public async Task AnInactivityClosedTicket_IsVisibleToItsLastResponsibleCsAgent_OnlyInsideTheReopenWindow_OnARealSqlServer()
    {
        CreateDatabase();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        long inside, outside, normalClosure;
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            var cs = new Department("Customer Service", "CSI");
            context.Departments.Add(cs);
            AddPerson(context, me, "Me");
            AddPerson(context, other, "Other");
            context.SaveChanges();
            var csAgentRole = context.Roles.FirstOrDefault(r => r.Name == Roles.CsAgent);
            if (csAgentRole is null)
            {
                csAgentRole = new ApplicationRole(Roles.CsAgent, "CS Agent") { Id = Guid.NewGuid(), NormalizedName = Roles.CsAgent.ToUpperInvariant() };
                context.Roles.Add(csAgentRole);
                context.SaveChanges();
            }

            context.UserRoles.AddRange(
                new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = me, RoleId = csAgentRole.Id },
                new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = other, RoleId = csAgentRole.Id });
            context.SaveChanges();

            long Closed(int n, DateTime closedAtUtc, bool inactivity)
            {
                // Created by me and never assigned to a CS Agent, so I am the responsible CS Agent (creator fallback).
                var ticket = Ticket.CreateUnclassified($"TG-SQL-20261103-{n:D4}", cs.DepartmentId, $"Inactivity {n}", closedAtUtc.AddDays(-1));
                ticket.RecordCreator(me);
                context.Tickets.Add(ticket);
                context.SaveChanges();
                var previous = ticket.CloseForCustomerInactivity();
                context.SaveChanges();
                context.TicketResolutions.Add(inactivity
                    ? TicketResolution.ForCustomerInactivity(ticket.TicketId, "No reply.", other, closedAtUtc)
                    : new TicketResolution(ticket.TicketId, ResolutionOutcome.Cancelled, "Cancelled by staff.", null, null, other, closedAtUtc));
                context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                    ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)previous, (byte)TicketStatus.Closed,
                    actorEmployeeId: null, actorIsSystem: true, note: null, Guid.NewGuid(), closedAtUtc));
                context.SaveChanges();
                return ticket.TicketId;
            }

            inside = Closed(1, nowUtc.AddDays(-2), inactivity: true);
            outside = Closed(2, nowUtc.AddDays(-9), inactivity: true);
            normalClosure = Closed(3, nowUtc.AddDays(-1), inactivity: false);   // not an inactivity closure: no exception
        }

        using var read = CreateContext();
        var assignments = new UserDepartmentAssignmentRepository(read);
        var cutoff = nowUtc.AddDays(-ReopenPolicy.DefaultWindowDays);

        async Task<TicketAccessLevel> AccessAsync(Guid caller, long id) => await TicketVisibilityRule.GetTicketAccessAsync(
            assignments, handlerRepository: null, caller, Agent, (await read.Tickets.FindAsync(id))!, inactivityReopenCutoffUtc: cutoff);

        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(me, inside));
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(me, outside));
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(me, normalClosure));
        Assert.Equal(TicketAccessLevel.None, await AccessAsync(other, inside));

        var query = new TicketQueryAppService(
            new TicketRepository(read), assignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(),
            ReopenPolicy.Default, TimeProvider.System);
        var mine = await query.GetQueueAsync(me, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100));
        Assert.Equal(new[] { inside }, mine.Items.Select(i => i.TicketId).ToArray());
        Assert.Equal(1, mine.TotalCount);
        var theirs = await query.GetQueueAsync(other, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100));
        Assert.Empty(theirs.Items);
    }

    [VisibilitySqlServerFact]
    public void BothMigrations_PreserveExistingRows_Backfill_AndTolerateBeingReRun()
    {
        CreateDatabase();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();
        migrator.Migrate(BeforeVisibilityMigrations);

        context.Database.ExecuteSqlRaw("INSERT INTO Departments(Name,Code,IsActive) VALUES (N'CS',N'CSM',1)");
        foreach (var (id, genesys) in new[] { (a, "g-a"), (b, "g-b") })
        {
            context.Database.ExecuteSql($"INSERT INTO AspNetUsers(Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,GenesysUserId) VALUES ({id},{genesys},{genesys},{genesys},{genesys},1,0,0,0,0,{genesys})");
            context.Database.ExecuteSql($"INSERT INTO Employees(EmployeeId,DisplayName,IsGeynessStaff,CreatedAtUtc) VALUES ({id},{genesys},0,SYSUTCDATETIME())");
        }

        var departmentId = context.Database.SqlQueryRaw<int>("SELECT DepartmentId AS Value FROM Departments").Single();
        var channelId = context.Database.SqlQueryRaw<byte>("SELECT TOP 1 ChannelId AS Value FROM Channels ORDER BY ChannelId").Single();
        foreach (var number in new[] { "M1", "M2", "M3" })
        {
            context.Database.ExecuteSql($"INSERT INTO Tickets(TicketNumber,OriginatingDepartmentId,CurrentDepartmentId,TicketStatus,VerificationStatus,EscalationLevel,SlaState,RequestSummary,ReopenCount,CreatedAtUtc) VALUES ({number},{departmentId},{departmentId},1,0,0,0,{number},0,SYSUTCDATETIME())");
        }

        long Id(string number) => context.Database.SqlQuery<long>($"SELECT TicketId AS Value FROM Tickets WHERE TicketNumber = {number}").Single();
        long m1 = Id("M1"), m2 = Id("M2"), m3 = Id("M3");
        // M1 is handled by a mapped agent; M2 carries a handler but no Genesys id (not backfilled by design).
        context.Database.ExecuteSql($"INSERT INTO TicketInteractions(TicketId,IsOriginatingInteraction,Source,ChannelId,CustomerPhone,CreatedAtUtc,RequiresHumanFollowUp,HandledByUserId,GenesysAgentUserId) VALUES ({m1},1,1,{channelId},N'1',SYSUTCDATETIME(),0,{a},N'g-a')");
        context.Database.ExecuteSql($"INSERT INTO TicketInteractions(TicketId,IsOriginatingInteraction,Source,ChannelId,CustomerPhone,CreatedAtUtc,RequiresHumanFollowUp,HandledByUserId,GenesysAgentUserId) VALUES ({m2},1,1,{channelId},N'2',SYSUTCDATETIME(),0,{b},NULL)");
        // Two creation entries for M1 (the first wins), one for M2 (later non-create entries are ignored), none for M3.
        foreach (var (ticket, actor, action) in new[] { (m1, a, "Create"), (m1, b, "Create"), (m2, b, "Create"), (m2, a, "Update") })
        {
            var entityId = ticket.ToString();
            context.Database.ExecuteSql($"INSERT INTO AuditEntries(ActorEmployeeId,Action,EntityType,EntityId,CorrelationId,OccurredAtUtc) VALUES ({actor},{action},N'Ticket',{entityId},NEWID(),SYSUTCDATETIME())");
        }

        var before = context.Database.SqlQueryRaw<int>("SELECT CHECKSUM_AGG(CHECKSUM(TicketId,TicketNumber,CurrentDepartmentId,RequestSummary)) AS Value FROM Tickets").Single();

        migrator.Migrate(); // both visibility migrations, over existing rows

        Assert.Equal(before, context.Database.SqlQueryRaw<int>("SELECT CHECKSUM_AGG(CHECKSUM(TicketId,TicketNumber,CurrentDepartmentId,RequestSummary)) AS Value FROM Tickets").Single());
        Assert.Equal(3, context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Tickets").Single());
        var handlerRows = context.Database.SqlQueryRaw<Guid>("SELECT HandledByUserId AS Value FROM TicketInteractionHandlers").ToList();
        Assert.Equal([a], handlerRows);
        Assert.Equal(
            (byte)InteractionHandlerSource.Legacy,
            context.Database.SqlQueryRaw<byte>("SELECT Source AS Value FROM TicketInteractionHandlers").Single());
        Assert.Equal(a, context.Database.SqlQuery<Guid>($"SELECT CreatedByEmployeeId AS Value FROM Tickets WHERE TicketId = {m1}").Single());
        Assert.Equal(b, context.Database.SqlQuery<Guid>($"SELECT CreatedByEmployeeId AS Value FROM Tickets WHERE TicketId = {m2}").Single());
        Assert.Equal(0, context.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM Tickets WHERE TicketId = {m3} AND CreatedByEmployeeId IS NOT NULL").Single());

        migrator.Migrate(); // re-running changes nothing
        Assert.Equal(1, context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM TicketInteractionHandlers").Single());
        Assert.Empty(context.Database.GetPendingMigrations());

        migrator.Migrate(BeforeVisibilityMigrations); // and both can be rolled back without touching the tickets
        Assert.Equal(before, context.Database.SqlQueryRaw<int>("SELECT CHECKSUM_AGG(CHECKSUM(TicketId,TicketNumber,CurrentDepartmentId,RequestSummary)) AS Value FROM Tickets").Single());
    }
}
