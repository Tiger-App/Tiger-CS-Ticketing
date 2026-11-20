using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The Production deployment of <c>AddInteractionEndReceivedAt</c> (one nullable column on <c>TicketInteractions</c>) on a REAL SQL Server, run as an operator would: from the previous
/// migration, dry run, commit, re-run, the wrong-database guard, and EF finding nothing left to apply. Needs <c>TIGERCS_TEST_SQL_SERVER</c>.
/// </summary>
public sealed class InteractionEndReceivedAtScriptSqlServerTests : IDisposable
{
    private const string Previous = "20261119120249_AddAssignmentHistoryPreviousDepartment";
    private const string Migration = "20261120074059_AddInteractionEndReceivedAt";

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsEndRecvTest_{Guid.NewGuid():N}";

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

    private void CreateProductionLikeDatabase()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using var context = CreateContext();
        context.GetService<IMigrator>().Migrate(Previous);
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

    private string DeployScript(bool dryRun)
    {
        var script = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql"));
        Assert.Contains("N'TigerCS_Ticketing_Prod'", script);
        Assert.Contains("N'TGAESHJDBP02'", script);
        script = script.Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal)
                       .Replace("N'TGAESHJDBP02'", "@@SERVERNAME", StringComparison.Ordinal);
        return dryRun ? script : script.Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;", StringComparison.Ordinal);
    }

    private bool ColumnExists() => Scalar<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketInteractions') AND name = N'EndReceivedAtUtc'") == 1;
    private int Recorded() => Scalar<int>($"SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = N'{Migration}'");

    [VisibilitySqlServerFact]
    public void DryRun_AppliesChecksAndRollsBack_LeavingTheDatabaseExactlyAsItWas()
    {
        CreateProductionLikeDatabase();

        Exec(DeployScript(dryRun: true));   // the default

        Assert.False(ColumnExists());
        Assert.Equal(0, Recorded());
    }

    [VisibilitySqlServerFact]
    public void Commit_AddsOneNullableDateTime2Column_IsIdempotent_AndLeavesEfWithNothingToApply()
    {
        CreateProductionLikeDatabase();

        Exec(DeployScript(dryRun: false));

        Assert.True(ColumnExists());
        Assert.Equal(1, Scalar<int>("SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.TicketInteractions') AND name = N'EndReceivedAtUtc'"));
        Assert.Equal("datetime2", Scalar<string>("SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'dbo.TicketInteractions') AND c.name = N'EndReceivedAtUtc'"));
        Assert.Equal(1, Recorded());

        Exec(DeployScript(dryRun: false));   // re-run: nothing to do
        Exec(DeployScript(dryRun: true));
        Assert.Equal(1, Recorded());

        using var context = CreateContext();
        Assert.Empty(context.Database.GetPendingMigrations());   // exactly the database EF expects
        context.Database.Migrate();
    }

    [VisibilitySqlServerFact]
    public void WrongDatabase_AbortsBeforeAnyChange()
    {
        CreateProductionLikeDatabase();
        var unchanged = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql")).Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;");

        var error = Assert.Throws<SqlException>(() => Exec(unchanged));   // the guard still says Production

        Assert.Contains("ABORTED", error.Message);
        Assert.False(ColumnExists());
        Assert.Equal(0, Recorded());
    }
}
