using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The EF model, its migrations and the hand-written Production deployment script must agree. These run against the SQL Server provider
/// with no connection (script generation and model comparison are offline), so they gate every build.
/// </summary>
public class EfModelAndMigrationConsistencyTests(ITestOutputHelper output)
{
    private static readonly string[] FourPendingMigrations =
    [
        "20261030061255_AddTicketInteractionHandlers",
        "20261030062642_AddTicketTransferTracking",
        "20261030073857_AddTicketCreatedBy",
        "20261031094748_AddDepartmentAssignmentRotation"
    ];

    private static TigerCsDbContext CreateSqlServerContext() => new(
        new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlServer("Server=offline;Database=TigerCS_Offline;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);

    [Fact]
    public void TheEfModel_HasNoChangesThatAreMissingFromTheMigrations()
    {
        using var context = CreateSqlServerContext();

        // False means the latest model snapshot equals the current model: every schema change in the code has a migration.
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TheFourPendingMigrations_AreTheLatestOnes_InOrder()
    {
        using var context = CreateSqlServerContext();
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        // AddUserPermissionOverrides (per-employee permission overrides) and AddCollectionsTowerAssignments (Collections tower assignments) were added after
        // these four and are deployed by their own scripts; the Production script for the four must stay exactly these, immediately before them.
        Assert.Equal("20261104123248_AddCollectionsTowerAssignments", migrations[^1]);
        Assert.Equal("20261102065459_AddUserPermissionOverrides", migrations[^2]);
        Assert.Equal(FourPendingMigrations, migrations.SkipLast(2).TakeLast(4));
    }

    [Fact]
    public void TheEfGeneratedScriptForTheFourMigrations_CreatesExactlyTheObjectsTheProductionScriptDeploys()
    {
        using var context = CreateSqlServerContext();
        var migrator = context.GetService<IMigrator>();
        var ef = migrator.GenerateScript(
            fromMigration: "20261022090000_AddGenesysServiceRequestToTicketInteractions",
            toMigration: FourPendingMigrations[^1],
            options: MigrationsSqlGenerationOptions.Idempotent);
        output.WriteLine(ef);

        var production = File.ReadAllText(FindRepositoryFile("Deploy_TigerCS_Prod_Migrations.sql"));

        // Every migration is recorded by id in the Production script...
        foreach (var id in FourPendingMigrations)
        {
            Assert.Contains(id, ef);
            Assert.Contains(id, production);
        }

        // ...and every table / column / index the EF script creates is created by the Production script too.
        foreach (var objectName in Created(ef))
        {
            Assert.True(
                production.Contains(objectName, StringComparison.OrdinalIgnoreCase),
                $"EF creates '{objectName}' but Deploy_TigerCS_Prod_Migrations.sql does not mention it.");
        }
    }

    /// <summary>Bracketed names created by CREATE TABLE / CREATE INDEX / ADD COLUMN in an EF script.</summary>
    private static IEnumerable<string> Created(string script)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            script, @"CREATE (?:UNIQUE )?(?:TABLE|INDEX) \[(?<n>[^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            yield return m.Groups["n"].Value;
        }

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            script, @"ALTER TABLE \[[^\]]+\] ADD \[(?<n>[^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            yield return m.Groups["n"].Value;
        }
    }

    private static string FindRepositoryFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{name} was not found above {AppContext.BaseDirectory}.");
    }
}
