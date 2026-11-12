using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The Original Human Agent migrations on a REAL SQL Server (skipped without <c>TIGERCS_TEST_SQL_SERVER</c>): applied over existing tickets they add the two columns and
/// change nothing else - no ticket, assignment, status or department is touched, <c>OriginalHumanAgentId</c> stays NULL for every legacy ticket (nothing is inferred
/// from assignment history), and the cutoff column marks every one of them legacy. A ticket inserted afterwards without the flag is legacy too (the column default).
/// </summary>
public sealed class OriginalAgentCutoffSqlServerTests : IDisposable
{
    private const string Before = "20261110070152_AddUserTransferDepartments";

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsCutoffTest_{Guid.NewGuid():N}";

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

    [VisibilitySqlServerFact]
    public void TheMigrations_LeaveEveryExistingTicketUntouched_AndMarkItLegacy_WithNoOriginalAgentInferred()
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
        migrator.Migrate(Before);

        Exec(c, "INSERT INTO Departments(Name,Code,IsActive) VALUES (N'Call Center',N'CC',1),(N'Collections',N'COL',1)");
        var cc = c.Database.SqlQuery<int>($"SELECT DepartmentId AS Value FROM Departments WHERE Code = N'CC'").Single();
        var col = c.Database.SqlQuery<int>($"SELECT DepartmentId AS Value FROM Departments WHERE Code = N'COL'").Single();
        Exec(c, "IF NOT EXISTS (SELECT 1 FROM AspNetRoles WHERE Name = N'CS Agent') INSERT INTO AspNetRoles(Id,Name,NormalizedName,Description) VALUES (NEWID(),N'CS Agent',N'CS AGENT',N'role')");

        var agent = Guid.NewGuid();
        Exec(c, "INSERT INTO AspNetUsers(Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount) VALUES ({0},N'kawthar',N'KAWTHAR',N'k@example.test',N'K@EXAMPLE.TEST',1,0,0,0,0)", agent);
        Exec(c, "INSERT INTO Employees(EmployeeId,DisplayName,IsGeynessStaff,CreatedAtUtc) VALUES ({0},N'Kawthar',0,SYSUTCDATETIME())", agent);
        Exec(c, "INSERT INTO UserDepartmentAssignments(EmployeeId,DepartmentId,IsPrimary,AssignedAtUtc) VALUES ({0},{1},1,SYSUTCDATETIME())", agent, cc);
        Exec(c, "INSERT INTO AspNetUserRoles(UserId,RoleId) SELECT {0}, Id FROM AspNetRoles WHERE Name = N'CS Agent'", agent);

        // The very history an inferring backfill would have used: a verified CS Agent, self-assigned in the Call Center, then Collections, open and closed tickets.
        foreach (var (number, department, status) in new[] { ("L1", cc, 1), ("L2", col, 2), ("L3", cc, 5), ("L4", cc, 1) })
        {
            Exec(c, "INSERT INTO Tickets(TicketNumber,OriginatingDepartmentId,CurrentDepartmentId,TicketStatus,VerificationStatus,EscalationLevel,SlaState,RequestSummary,ReopenCount,CreatedAtUtc) VALUES ({0},{1},{1},{2},0,0,0,{0},0,SYSUTCDATETIME())", number, department, status);
            var id = c.Database.SqlQuery<long>($"SELECT TicketId AS Value FROM Tickets WHERE TicketNumber = {number}").Single();
            Exec(c, "INSERT INTO TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) VALUES ({0},{1},{2},SYSUTCDATETIME(),{1},1)", id, agent, cc);
            Exec(c, "INSERT INTO AuditEntries(ActorEmployeeId,Action,EntityType,EntityId,CorrelationId,OccurredAtUtc) VALUES ({0},N'Update',N'Ticket',{1},NEWID(),SYSUTCDATETIME())", agent, id.ToString());
        }

        const string TicketChecksum = "SELECT CHECKSUM_AGG(CHECKSUM(TicketId,TicketNumber,OriginatingDepartmentId,CurrentDepartmentId,CurrentOwnerEmployeeId,TicketStatus,RequestSummary,CreatedAtUtc,SlaState,EscalationLevel,ReopenCount)) AS Value FROM Tickets";
        const string AssignmentChecksum = "SELECT CHECKSUM_AGG(CHECKSUM(TicketAssignmentId,TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent)) AS Value FROM TicketAssignments";
        var ticketsBefore = c.Database.SqlQueryRaw<int>(TicketChecksum).Single();
        var assignmentsBefore = c.Database.SqlQueryRaw<int>(AssignmentChecksum).Single();
        var auditBefore = c.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM AuditEntries").Single();

        migrator.Migrate();   // AddTicketOriginalHumanAgent + AddOriginalAgentWorkflowCutoff

        Assert.Equal(ticketsBefore, c.Database.SqlQueryRaw<int>(TicketChecksum).Single());
        Assert.Equal(assignmentsBefore, c.Database.SqlQueryRaw<int>(AssignmentChecksum).Single());
        Assert.Equal(auditBefore, c.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM AuditEntries").Single());
        Assert.Equal(4, c.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Tickets WHERE OriginalHumanAgentId IS NULL AND OriginalAgentWorkflowEnabled = 0").Single());
        Assert.Equal(0, c.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Tickets WHERE OriginalHumanAgentId IS NOT NULL OR OriginalAgentWorkflowEnabled <> 0").Single());

        // A row inserted by anything that does not know the flag (an old application instance mid-deployment) is legacy as well: the safe direction.
        Exec(c, "INSERT INTO Tickets(TicketNumber,OriginatingDepartmentId,CurrentDepartmentId,TicketStatus,VerificationStatus,EscalationLevel,SlaState,RequestSummary,ReopenCount,CreatedAtUtc) VALUES (N'L5',{0},{0},1,0,0,0,N'L5',0,SYSUTCDATETIME())", cc);
        Assert.Equal(0, c.Database.SqlQueryRaw<int>("SELECT CAST(OriginalAgentWorkflowEnabled AS int) AS Value FROM Tickets WHERE TicketNumber = N'L5'").Single());

        migrator.Migrate();   // re-running changes nothing
        Assert.Empty(c.Database.GetPendingMigrations());
        migrator.Migrate(Before);   // and both roll back without touching the tickets
        Assert.Equal(5, c.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Tickets").Single());
    }
}
