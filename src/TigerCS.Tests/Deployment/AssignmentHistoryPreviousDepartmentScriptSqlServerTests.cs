using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The Production deployment of <c>AddAssignmentHistoryPreviousDepartment</c> (one nullable column on <c>TicketAssignmentHistory</c>) on a REAL SQL Server, run exactly as an
/// operator would: a database that is at the previous migration and already holds Assignment History rows; the dry run; the commit; the re-run; the wrong-database guard; and the
/// updated read-only verification script. Needs <c>TIGERCS_TEST_SQL_SERVER</c> (CI provides it and fails the build if these are skipped).
/// </summary>
public sealed class AssignmentHistoryPreviousDepartmentScriptSqlServerTests : IDisposable
{
    private const string Previous = "20261119081103_AddCustomerProfileAuditAndProperties";
    private const string Migration = "20261119120249_AddAssignmentHistoryPreviousDepartment";

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsPrevDeptTest_{Guid.NewGuid():N}";

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

    /// <summary>Production as it is today: everything up to the previous migration, with Assignment History rows already written (Genesys and Collections ones).</summary>
    private void CreateProductionLikeDatabase()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(Previous);
        }

        Exec("""
             INSERT INTO Departments (Name, Code, IsActive) VALUES (N'Call Center', N'CC', 1);
             DECLARE @d int = SCOPE_IDENTITY();
             ALTER TABLE TicketAssignmentHistory NOCHECK CONSTRAINT ALL;   -- only the column is under test, not the employees/tickets graph
             INSERT INTO TicketAssignmentHistory (TicketId, DepartmentId, PreviousEmployeeId, NewEmployeeId, Kind, ChangedAtUtc, Reason, Source, GenesysConversationId)
             VALUES (1001, @d, NULL, NEWID(), 1, SYSUTCDATETIME(), N'AI Agent handled the conversation', 1, N'conv-1'),
                    (1002, @d, NEWID(), NEWID(), 2, SYSUTCDATETIME(), N'Assigned manually', NULL, NULL);
             """);
    }

    private void Exec(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(name);
    }

    /// <summary>The Production deployment script, as it will be run, with only the database-name guard pointed at this test database.</summary>
    private string DeployScript(bool dryRun)
    {
        var script = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql"));
        Assert.Contains("N'TigerCS_Ticketing_Prod'", script);
        Assert.Contains("N'TGAESHJDBP02'", script);
        script = script.Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal)
                       .Replace("N'TGAESHJDBP02'", "@@SERVERNAME", StringComparison.Ordinal);
        return dryRun ? script : script.Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;", StringComparison.Ordinal);
    }

    private bool ColumnExists() => Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND name = N'PreviousDepartmentId'") == 1;
    private int Recorded() => Scalar<int>($"SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = N'{Migration}'");

    [VisibilitySqlServerFact]
    public void DryRun_AppliesChecksAndRollsBack_LeavingTheDatabaseExactlyAsItWas()
    {
        CreateProductionLikeDatabase();
        var history = Scalar<int>("SELECT COUNT(*) FROM __EFMigrationsHistory");

        Exec(DeployScript(dryRun: true));   // the default

        Assert.False(ColumnExists());
        Assert.Equal(0, Recorded());
        Assert.Equal(history, Scalar<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketAssignmentHistory"));
    }

    [VisibilitySqlServerFact]
    public void Commit_AddsOneNullableColumn_KeepsEveryExistingRow_IsIdempotent_AndLeavesEfWithNothingToApply()
    {
        CreateProductionLikeDatabase();

        Exec(DeployScript(dryRun: false));

        Assert.True(ColumnExists());
        Assert.Equal(1, Scalar<int>("SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND name = N'PreviousDepartmentId'"));
        Assert.Equal("int", Scalar<string>("SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'dbo.TicketAssignmentHistory') AND c.name = N'PreviousDepartmentId'"));
        Assert.Equal(1, Recorded());
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketAssignmentHistory"));                                  // no row lost
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketAssignmentHistory WHERE PreviousDepartmentId IS NULL")); // no backfill
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM TicketAssignmentHistory WHERE Source = 1 AND GenesysConversationId = N'conv-1'"));

        Exec(DeployScript(dryRun: false));   // re-run: nothing to do, nothing changes
        Exec(DeployScript(dryRun: true));
        Assert.Equal(1, Recorded());
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketAssignmentHistory"));

        using var context = CreateContext();
        // After THIS script only the migrations that come later in the release are still pending (AddInteractionEndReceivedAt, AddEmployeeReviews and AddQueueingTicketTypeName, each deployed by its own script);
        // nothing earlier, and nothing unknown.
        Assert.Equal(["20261120074059_AddInteractionEndReceivedAt", "20261124071552_AddEmployeeReviews", "20261126154428_AddEmployeeReviewActivationCodes", "20261126175759_AddQueueingTicketTypeName"], context.Database.GetPendingMigrations());
        context.Database.Migrate();
        Assert.Equal(2, context.TicketAssignmentHistory.Count());
    }

    [VisibilitySqlServerFact]
    public void TheNewColumn_IsWritableByTheApplicationModel_AfterTheScript()
    {
        CreateProductionLikeDatabase();
        Exec(DeployScript(dryRun: false));

        using var context = CreateContext();
        var department = Scalar<int>("SELECT TOP 1 DepartmentId FROM Departments");
        var entry = TigerCS.Domain.Modules.Ticketing.TicketAssignmentHistoryEntry.ForGenesys(
            1001, department, Guid.NewGuid(), Guid.NewGuid(), TigerCS.Domain.Modules.Ticketing.TicketAssignmentSource.GenesysQueueRouting, null,
            DateTime.UtcNow, null, "conv-1", "queue routing", previousDepartmentId: 7);
        context.TicketAssignmentHistory.Add(entry);
        context.SaveChanges();

        Assert.Equal(7, Scalar<int>("SELECT PreviousDepartmentId FROM TicketAssignmentHistory WHERE Source = 6"));
    }

    [VisibilitySqlServerFact]
    public void WrongDatabase_AbortsBeforeAnyChange()
    {
        CreateProductionLikeDatabase();
        var unchanged = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql")).Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;");

        var error = Assert.Throws<SqlException>(() => Exec(unchanged));   // the guard still says Production

        Assert.Contains("ABORTED", error.Message);
        Assert.False(ColumnExists());
        Assert.Equal(0, Recorded());
    }

    [VisibilitySqlServerFact]
    public void TheVerificationScript_ReportsTheMigrationPendingBefore_AndAppliedWithItsColumnPresent_After()
    {
        CreateProductionLikeDatabase();
        var verify = File.ReadAllText(RepoFile("Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql")).Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal);

        var before = Run(verify);
        Assert.Contains(before[0], r => (string?)r[0] == Migration);                                          // 1. Pending lists it
        Assert.Contains(before[2], r => (string?)r[0] == "AddAssignmentHistoryPreviousDepartment" && Convert.ToInt32(r[2]) == 0);   // 3. column MISSING

        Exec(DeployScript(dryRun: false));

        var after = Run(verify);
        Assert.DoesNotContain(after[0], r => (string?)r[0] == Migration);                                     // no longer pending
        Assert.Empty(after[1]);                                                                              // 2. Unknown: nothing unexpected
        Assert.Contains(after[2], r => (string?)r[0] == "AddAssignmentHistoryPreviousDepartment" && Convert.ToInt32(r[2]) == 1);    // column present
    }

    /// <summary>Runs a script and returns each result set as rows of values.</summary>
    private List<List<object?[]>> Run(string sql)
    {
        var sets = new List<List<object?[]>>();
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        using var reader = command.ExecuteReader();
        do
        {
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }

            sets.Add(rows);
        }
        while (reader.NextResult());
        return sets;
    }
}
