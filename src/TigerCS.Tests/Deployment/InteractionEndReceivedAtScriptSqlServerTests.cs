using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The Production deployment of <c>AddInteractionEndReceivedAt</c> (one nullable datetime2 on <c>TicketInteractions</c>) on a REAL SQL Server, run exactly as an operator
/// would: a database that is at the previous migration and already holds interactions; the dry run; the commit; the re-run; the wrong-database and missing-dependency guards;
/// and the updated read-only verification script. Needs <c>TIGERCS_TEST_SQL_SERVER</c> (CI provides it).
/// </summary>
public sealed class InteractionEndReceivedAtScriptSqlServerTests : IDisposable
{
    private const string Previous = "20261119120249_AddAssignmentHistoryPreviousDepartment";
    private const string Migration = "20261120074059_AddInteractionEndReceivedAt";

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsEndRecvTest_{Guid.NewGuid():N}";

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

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

    private void CreateProductionLikeDatabase(string migrateTo = Previous)
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using (var context = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options))
        {
            context.GetService<IMigrator>().Migrate(migrateTo);
        }

        // two existing interactions with neutral values for every required column (only the new column is under test)
        for (var i = 0; i < 2; i++) InsertNeutralRow("TicketInteractions");
    }

    /// <summary>Inserts one row with a neutral default for every required column of <paramref name="table"/>.</summary>
    private void InsertNeutralRow(string table)
    {
        var columns = Run($"""
            SELECT c.name, t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'dbo.{table}') AND c.is_nullable = 0 AND c.is_identity = 0 AND c.is_computed = 0 AND c.default_object_id = 0 AND t.name <> 'timestamp'
            """)[0];
        var names = columns.Select(c => $"[{c[0]}]");
        var values = columns.Select(c => (string)c[1]! switch
        {
            "uniqueidentifier" => "NEWID()", "datetime2" or "datetime" => "SYSUTCDATETIME()", "nvarchar" or "varchar" => $"N'{Guid.NewGuid():N}'", "varbinary" => "0x00", _ => "0",
        });
        Run($"ALTER TABLE dbo.{table} NOCHECK CONSTRAINT ALL; INSERT dbo.{table} ({string.Join(',', names)}) VALUES ({string.Join(',', values)});");
    }

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

    private void Exec(string sql) => Run(sql);
    private T Scalar<T>(string sql) => (T)Convert.ChangeType(Run(sql)[0][0][0]!, typeof(T));

    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(name);
    }

    private string DeployScript(bool dryRun)
    {
        var script = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql"));
        Assert.Contains("N'TigerCS_Ticketing_Prod'", script);
        Assert.Contains("N'TGAESHJDBP02'", script);
        script = script.Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal).Replace("N'TGAESHJDBP02'", "@@SERVERNAME", StringComparison.Ordinal);
        return dryRun ? script : script.Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;", StringComparison.Ordinal);
    }

    private bool ColumnExists() => Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketInteractions') AND name = N'EndReceivedAtUtc'") == 1;
    private int Recorded() => Scalar<int>($"SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = N'{Migration}'");

    [VisibilitySqlServerFact]
    public void DryRun_IsTheDefault_AppliesChecksAndRollsBack_LeavingTheDatabaseExactlyAsItWas()
    {
        CreateProductionLikeDatabase();
        var history = Scalar<int>("SELECT COUNT(*) FROM __EFMigrationsHistory");

        Exec(DeployScript(dryRun: true));

        Assert.False(ColumnExists());
        Assert.Equal(0, Recorded());
        Assert.Equal(history, Scalar<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketInteractions"));
    }

    [VisibilitySqlServerFact]
    public void Commit_AddsOneNullableDatetime2_KeepsEveryRow_IsIdempotent_AndLeavesEfWithNothingToApply()
    {
        CreateProductionLikeDatabase();

        Exec(DeployScript(dryRun: false));

        Assert.True(ColumnExists());
        Assert.Equal(1, Scalar<int>("SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketInteractions') AND name = N'EndReceivedAtUtc'"));
        Assert.Equal("datetime2", Scalar<string>("SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc'"));
        Assert.Equal(1, Recorded());
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketInteractions"));
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketInteractions WHERE EndReceivedAtUtc IS NULL"));   // no backfill

        Exec(DeployScript(dryRun: false));   // re-run: nothing to do, nothing changes
        Exec(DeployScript(dryRun: true));
        Assert.Equal(1, Recorded());
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM TicketInteractions"));

        using var context = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);
        // Only the migration that comes after this one in the release (AddEmployeeReviews, deployed by its own script) is still pending.
        Assert.Equal(["20261124071552_AddEmployeeReviews"], context.Database.GetPendingMigrations());
    }

    [VisibilitySqlServerFact]
    public void TheNewColumn_CanBeWrittenAndRead_AfterTheScript()
    {
        CreateProductionLikeDatabase();
        Exec(DeployScript(dryRun: false));

        Exec("UPDATE TOP (1) TicketInteractions SET EndReceivedAtUtc = '2026-11-20T08:00:00';");

        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM TicketInteractions WHERE EndReceivedAtUtc = '2026-11-20T08:00:00'"));
    }

    [VisibilitySqlServerFact]
    public void WrongDatabase_AbortsBeforeAnyChange()
    {
        CreateProductionLikeDatabase();
        var unchanged = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql")).Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;");

        var error = Assert.Throws<SqlException>(() => Exec(unchanged));

        Assert.Contains("ABORTED", error.Message);
        Assert.False(ColumnExists());
        Assert.Equal(0, Recorded());
    }

    [VisibilitySqlServerFact]
    public void WithoutThePreviousMigration_TheScriptRefusesToRun()
    {
        CreateProductionLikeDatabase(migrateTo: "20261119081103_AddCustomerProfileAuditAndProperties");   // PreviousDepartmentId not applied yet

        var error = Assert.Throws<SqlException>(() => Exec(DeployScript(dryRun: false)));

        Assert.Contains("AddAssignmentHistoryPreviousDepartment", error.Message);
        Assert.False(ColumnExists());
        Assert.Equal(0, Recorded());
    }

    [VisibilitySqlServerFact]
    public void TheVerificationScript_ReportsBothMigrationsPendingBefore_AndNothingPending_WithBothColumnsPresent_After()
    {
        CreateProductionLikeDatabase(migrateTo: "20261119081103_AddCustomerProfileAuditAndProperties");
        var verify = File.ReadAllText(RepoFile("Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql")).Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal);

        var before = Run(verify);
        Assert.Contains(before[0], r => (string?)r[0] == Migration);
        Assert.Contains(before[0], r => (string?)r[0] == Previous);

        Exec(File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql"))
            .Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal).Replace("N'TGAESHJDBP02'", "@@SERVERNAME", StringComparison.Ordinal)
            .Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;", StringComparison.Ordinal));
        Exec(DeployScript(dryRun: false));

        var after = Run(verify);
        Assert.DoesNotContain(after[0], r => (string?)r[0] == Migration || (string?)r[0] == Previous);
        Assert.Empty(after[1]);
        Assert.Contains(after[2], r => (string?)r[0] == "AddInteractionEndReceivedAt" && Convert.ToInt32(r[2]) == 1);
        Assert.Contains(after[2], r => (string?)r[0] == "AddAssignmentHistoryPreviousDepartment" && Convert.ToInt32(r[2]) == 1);
    }
}
