using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Infrastructure.Persistence.Migrations;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The <c>AddTicketOriginalHumanAgent</c> backfill over realistic historical assignment data on a REAL SQL Server (skipped without
/// <c>TIGERCS_TEST_SQL_SERVER</c>). It must pick the earliest verified, eligible Call Center Human Agent - and otherwise leave the field NULL:
/// never an integration account, a system-dealt owner nobody verified, a collector, an inactive / locked-out / non-CS-Agent / former Call Center
/// employee, and never a later assignee standing in for an unverifiable first one. It must never overwrite an existing value.
/// </summary>
public sealed class OriginalHumanAgentBackfillSqlServerTests : IDisposable
{
    private const string BeforeMigration = "20261110070152_AddUserTransferDepartments";
    private const string TheMigration = "20261111073632_AddTicketOriginalHumanAgent";

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsOriginalAgentTest_{Guid.NewGuid():N}";
    private int _seq;
    private int _cc, _col;
    private DateTime _t = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

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

    private static void Exec(TigerCsDbContext c, string sql, params object[] p) =>
        c.Database.ExecuteSqlRaw(sql, p.Select((v, i) => (object)new SqlParameter($"@p{i}", v ?? DBNull.Value)).ToArray());

    private Guid Person(TigerCsDbContext c, string name, string? role = "CS Agent", bool inCallCenter = true, bool deactivated = false,
        bool lockedOut = false, string? genesysUserId = null)
    {
        var id = Guid.NewGuid();
        var user = $"u{++_seq}-{id:N}";
        Exec(c, @"INSERT INTO AspNetUsers(Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,GenesysUserId,LockoutEnd)
                  VALUES ({0},{1},{1},{2},{2},1,0,0,1,0,{3},{4})",
            id, user, $"{user}@example.test", (object?)genesysUserId ?? DBNull.Value, lockedOut ? DateTimeOffset.UtcNow.AddDays(30) : DBNull.Value);
        Exec(c, "INSERT INTO Employees(EmployeeId,DisplayName,IsGeynessStaff,CreatedAtUtc,DeactivatedAtUtc) VALUES ({0},{1},0,SYSUTCDATETIME(),{2})",
            id, name, deactivated ? DateTime.UtcNow : DBNull.Value);
        if (inCallCenter)
        {
            Exec(c, "INSERT INTO UserDepartmentAssignments(EmployeeId,DepartmentId,IsPrimary,AssignedAtUtc) VALUES ({0},{1},1,SYSUTCDATETIME())", id, _cc);
        }

        if (role is not null)
        {
            Exec(c, "INSERT INTO AspNetUserRoles(UserId,RoleId) SELECT {0}, Id FROM AspNetRoles WHERE Name = {1}", id, role);
        }

        return id;
    }

    private long Ticket(TigerCsDbContext c, int department)
    {
        var number = $"BF-{++_seq}";
        Exec(c, "INSERT INTO Tickets(TicketNumber,OriginatingDepartmentId,CurrentDepartmentId,TicketStatus,VerificationStatus,EscalationLevel,SlaState,RequestSummary,ReopenCount,CreatedAtUtc) VALUES ({0},{1},{1},1,0,0,0,{0},0,SYSUTCDATETIME())", number, department);
        return c.Database.SqlQuery<long>($"SELECT TicketId AS Value FROM Tickets WHERE TicketNumber = {number}").Single();
    }

    /// <summary>One historical assignment row, in the order given (each a minute after the previous).</summary>
    private void Assign(TigerCsDbContext c, long ticket, Guid employee, int department, Guid? actor)
    {
        _t = _t.AddMinutes(1);
        Exec(c, "UPDATE TicketAssignments SET IsCurrent = 0 WHERE TicketId = {0}", ticket);
        Exec(c, "INSERT INTO TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) VALUES ({0},{1},{2},{3},{4},1)",
            ticket, employee, department, _t, (object?)actor ?? DBNull.Value);
    }

    private static void Audit(TigerCsDbContext c, long ticket, Guid actor) =>
        Exec(c, "INSERT INTO AuditEntries(ActorEmployeeId,Action,EntityType,EntityId,CorrelationId,OccurredAtUtc) VALUES ({0},N'Update',N'Ticket',{1},NEWID(),SYSUTCDATETIME())", actor, ticket.ToString());

    private static Guid? Original(TigerCsDbContext c, long ticket) =>
        c.Database.SqlQuery<Guid?>($"SELECT OriginalHumanAgentId AS Value FROM Tickets WHERE TicketId = {ticket}").Single();

    [VisibilitySqlServerFact]
    public void Backfill_PicksOnlyTheEarliestVerifiedEligibleCallCenterAgent_AndLeavesEverythingElseNull()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_database}]";
            create.ExecuteNonQuery();
        }

        using var c = CreateContext();
        var migrator = c.GetService<IMigrator>();
        migrator.Migrate(BeforeMigration);

        Exec(c, "INSERT INTO Departments(Name,Code,IsActive) VALUES (N'Call Center',N'CC',1),(N'Collections',N'COL',1),(N'Registration',N'REG',1)");
        _cc = c.Database.SqlQuery<int>($"SELECT DepartmentId AS Value FROM Departments WHERE Code = N'CC'").Single();
        _col = c.Database.SqlQuery<int>($"SELECT DepartmentId AS Value FROM Departments WHERE Code = N'COL'").Single();
        var reg = c.Database.SqlQuery<int>($"SELECT DepartmentId AS Value FROM Departments WHERE Code = N'REG'").Single();
        foreach (var role in new[] { "CS Agent", "Department Employee", "CS Manager" })
        {
            Exec(c, "IF NOT EXISTS (SELECT 1 FROM AspNetRoles WHERE Name = {0}) INSERT INTO AspNetRoles(Id,Name,NormalizedName,Description) VALUES (NEWID(),{0},UPPER({0}),N'role')", role);
        }

        var kawthar = Person(c, "Kawthar", genesysUserId: "g-kawthar");
        var bader = Person(c, "Bader");
        var manager = Person(c, "CS Manager", role: "CS Manager", inCallCenter: false);
        var collector = Person(c, "Collector One", role: "Department Employee", inCallCenter: false);

        // 1. Kawthar created and self-assigned it; it went to Collections (collector) and back to Bader. Original = Kawthar, the EARLIEST.
        var t1 = Ticket(c, _cc);
        Assign(c, t1, kawthar, _cc, actor: kawthar);
        Assign(c, t1, collector, _col, actor: null);
        Assign(c, t1, bader, _cc, actor: null);

        // 2. Dealt by the system to Bader and no one has ever acted on it: not a verified human owner -> NULL.
        var t2 = Ticket(c, _cc);
        Assign(c, t2, bader, _cc, actor: null);

        // 3. Dealt by the system to Bader, who then worked it (audit entry as actor) -> Bader.
        var t3 = Ticket(c, _cc);
        Assign(c, t3, bader, _cc, actor: null);
        Audit(c, t3, bader);

        // 4. Assigned by a manager (a person) to Kawthar -> Kawthar.
        var t4 = Ticket(c, _cc);
        Assign(c, t4, kawthar, _cc, actor: manager);

        // 5. The earliest Call Center agent was deactivated; a later eligible agent must NOT be credited in their place -> NULL.
        var gone = Person(c, "Former Agent", deactivated: true);
        var t5 = Ticket(c, _cc);
        Assign(c, t5, gone, _cc, actor: gone);
        Assign(c, t5, bader, _cc, actor: bader);

        // 6. Locked-out earliest agent -> NULL.
        var locked = Person(c, "Locked Agent", lockedOut: true);
        var t6 = Ticket(c, _cc);
        Assign(c, t6, locked, _cc, actor: locked);

        // 7. A named integration account that holds the CS Agent role and sits in the Call Center -> NULL.
        var integration = Person(c, "Genesys Integration");
        var t7 = Ticket(c, _cc);
        Assign(c, t7, integration, _cc, actor: integration);

        // 8. An unnamed integration account: creates Genesys tickets, never handled an interaction, no Genesys user mapping -> NULL.
        var unnamed = Person(c, "Account Nine");
        var channelId = c.Database.SqlQuery<byte>($"SELECT TOP 1 ChannelId AS Value FROM Channels ORDER BY ChannelId").Single();
        var t8 = Ticket(c, _cc);
        Exec(c, "UPDATE Tickets SET CreatedByEmployeeId = {0} WHERE TicketId = {1}", unnamed, t8);
        Exec(c, "INSERT INTO TicketInteractions(TicketId,IsOriginatingInteraction,Source,ChannelId,CustomerPhone,CreatedAtUtc,RequiresHumanFollowUp,GenesysConversationId) VALUES ({0},1,2,{1},N'1',SYSUTCDATETIME(),0,N'conv-1')", t8, channelId);
        Assign(c, t8, unnamed, _cc, actor: unnamed);

        // 9. A Collections collector who is also (wrongly) in the Call Center with the CS Agent role -> NULL.
        var hybrid = Person(c, "Hybrid Collector");
        Exec(c, "INSERT INTO CollectionsTowerAssignments(TowerId,TowerCode,TowerName,CompanyId,[Year],[Month],EmployeeId,IsPrimary,Status,CreatedAtUtc,CreatedByEmployeeId,UpdatedAtUtc,UpdatedByEmployeeId) VALUES (1,N'T1',N'Tower 1',1,2026,10,{0},1,1,SYSUTCDATETIME(),{1},SYSUTCDATETIME(),{1})", hybrid, manager);
        var t9 = Ticket(c, _cc);
        Assign(c, t9, hybrid, _cc, actor: hybrid);

        // 10. Earliest assignee does not hold the CS Agent role -> NULL.
        var plain = Person(c, "Plain Employee", role: "Department Employee");
        var t10 = Ticket(c, _cc);
        Assign(c, t10, plain, _cc, actor: plain);

        // 11. Earliest assignee has since left the Call Center -> NULL.
        var left = Person(c, "Moved Agent", inCallCenter: false);
        var t11 = Ticket(c, _cc);
        Assign(c, t11, left, _cc, actor: left);

        // 12. Never in the Call Center (created and handled in Registration only) -> NULL.
        var t12 = Ticket(c, reg);
        Assign(c, t12, bader, reg, actor: bader);

        // 13. A Genesys-mapped human who created Genesys tickets but also handled interactions is NOT mistaken for an integration account -> Kawthar.
        var t13 = Ticket(c, _cc);
        Exec(c, "UPDATE Tickets SET CreatedByEmployeeId = {0} WHERE TicketId = {1}", kawthar, t13);
        Exec(c, "INSERT INTO TicketInteractions(TicketId,IsOriginatingInteraction,Source,ChannelId,CustomerPhone,CreatedAtUtc,RequiresHumanFollowUp,GenesysConversationId,HandledByUserId) VALUES ({0},1,2,{1},N'1',SYSUTCDATETIME(),0,N'conv-13',{2})", t13, channelId, kawthar);
        Assign(c, t13, kawthar, _cc, actor: kawthar);

        // 14. No assignment at all (never owned) -> NULL.
        var t14 = Ticket(c, _cc);

        migrator.Migrate(TheMigration);

        Assert.Equal(kawthar, Original(c, t1));
        Assert.Null(Original(c, t2));
        Assert.Equal(bader, Original(c, t3));
        Assert.Equal(kawthar, Original(c, t4));
        Assert.Null(Original(c, t5));
        Assert.Null(Original(c, t6));
        Assert.Null(Original(c, t7));
        Assert.Null(Original(c, t8));
        Assert.Null(Original(c, t9));
        Assert.Null(Original(c, t10));
        Assert.Null(Original(c, t11));
        Assert.Null(Original(c, t12));
        Assert.Equal(kawthar, Original(c, t13));
        Assert.Null(Original(c, t14));

        // Never overwrites: pre-set a value, run the backfill again, and it stays.
        var preset = Guid.NewGuid();
        Exec(c, "UPDATE Tickets SET OriginalHumanAgentId = {0} WHERE TicketId = {1}", preset, t1);
        var rerun = "EXEC(N'" + AddTicketOriginalHumanAgent.BackfillSql.Replace("'", "''") + "')";
        Exec(c, rerun.Replace("{", "{{").Replace("}", "}}"));
        Assert.Equal(preset, Original(c, t1));
        Assert.Equal(kawthar, Original(c, t4));
        Assert.Null(Original(c, t2));

        // Re-running the whole migration set is a no-op and the migration can be rolled back.
        migrator.Migrate();
        Assert.Empty(c.Database.GetPendingMigrations());
        migrator.Migrate(BeforeMigration);
    }
}
