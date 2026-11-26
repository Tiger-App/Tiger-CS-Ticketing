using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The Customer Audit History migration and its guarantees, on a REAL SQL Server (these need <c>TIGERCS_TEST_SQL_SERVER</c>; CI provides it
/// and fails the build if they are skipped): the Production deployment script - dry run, commit, re-run - against a database that already
/// holds customer overrides; EF's own migration over the same data; the database-level append-only trigger; the unique ChangeKey that makes
/// retries idempotent; the row version that rejects a lost update; and the unique property association under a race.
/// </summary>
public sealed class CustomerProfileAuditSqlServerTests : IDisposable
{
    private const string Previous = "20261118124858_AddTicketAiAgentOwner";
    private const string Migration = "20261119081103_AddCustomerProfileAuditAndProperties";

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsAuditTest_{Guid.NewGuid():N}";

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

    // ----------------------------------------------------------------------------------------------- helpers

    private void CreateDatabase()
    {
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{_database}]";
        command.ExecuteNonQuery();
    }

    /// <summary>The database as Production is today: everything up to the previous migration applied, and two customers already edited.</summary>
    private void CreateProductionLikeDatabase()
    {
        CreateDatabase();
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(Previous);
        }

        Exec(
            """
            INSERT INTO CustomerProfileOverrides (CustomerKey, IdentityKind, CrmBuyerCustomerId, IdentityPhone, PhoneNumber, PhoneLocation, ProjectName, UnitNumber, CreatedAtUtc, UpdatedAtUtc, UpdatedByEmployeeId)
            VALUES (N'crm:101', 1, 101, NULL, N'+971501234567', 1, N'Tiger Tower', N'T-1', SYSUTCDATETIME(), SYSUTCDATETIME(), NEWID()),
                   (N'phone:971559998888', 3, NULL, N'971559998888', NULL, NULL, N'Palm', N'P-8', SYSUTCDATETIME(), SYSUTCDATETIME(), NEWID());
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
        var script = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddCustomerProfileAuditAndProperties.sql"));
        Assert.Contains("N'TigerCS_Ticketing_Prod'", script);
        script = script.Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal);
        return dryRun ? script : script.Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;", StringComparison.Ordinal);
    }

    private bool TableExists(string table) => Scalar<int>($"SELECT COUNT(*) FROM sys.tables WHERE name = N'{table}'") == 1;
    private bool ColumnExists(string table, string column) => Scalar<int>($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = N'{column}'") == 1;
    private bool Recorded() => Scalar<int>($"SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = N'{Migration}'") == 1;

    private static CustomerProfileChange Change(Guid profile, Guid correlation, string field, string? oldValue, string? newValue) =>
        new(profile, "crm:101", field, oldValue, newValue, Guid.NewGuid(), "Hala Manager", DateTime.UtcNow, "UI", null, null, correlation);

    // ------------------------------------------------------------------------------------- the Production script

    [VisibilitySqlServerFact]
    public void ProductionScript_DryRun_AppliesChecksAndRollsBack_LeavingTheDatabaseExactlyAsItWas()
    {
        CreateProductionLikeDatabase();
        var historyBefore = Scalar<int>("SELECT COUNT(*) FROM __EFMigrationsHistory");

        Exec(DeployScript(dryRun: true));   // the default: apply, validate, roll back

        Assert.False(TableExists("CustomerProfileChanges"));
        Assert.False(TableExists("CustomerPropertyAssociations"));
        Assert.False(ColumnExists("CustomerProfileOverrides", "CustomerProfileId"));
        Assert.False(ColumnExists("CustomerProfileOverrides", "FullName"));
        Assert.False(Recorded());
        Assert.Equal(historyBefore, Scalar<int>("SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM CustomerProfileOverrides"));
    }

    [VisibilitySqlServerFact]
    public void ProductionScript_Commit_GivesEveryExistingCustomerItsOwnStableId_IsIdempotent_AndLeavesEfWithNothingToApply()
    {
        CreateProductionLikeDatabase();

        Exec(DeployScript(dryRun: false));

        Assert.True(TableExists("CustomerProfileChanges"));
        Assert.True(TableExists("CustomerPropertyAssociations"));
        foreach (var column in new[] { "CustomerProfileId", "FullName", "Email", "LandlineNumber", "AdditionalPhones", "Nationality", "Country", "Emirate", "Location", "PreferredLanguage" })
        {
            Assert.True(ColumnExists("CustomerProfileOverrides", column), column);
        }

        Assert.True(Recorded());

        // The two customers already edited each got their own identifier (none left at the all-zero default), and what they were edited
        // to is untouched.
        Assert.Equal(2, Scalar<int>("SELECT COUNT(DISTINCT CustomerProfileId) FROM CustomerProfileOverrides"));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM CustomerProfileOverrides WHERE CustomerProfileId = '00000000-0000-0000-0000-000000000000'"));
        Assert.Equal("+971501234567", Scalar<string>("SELECT PhoneNumber FROM CustomerProfileOverrides WHERE CustomerKey = N'crm:101'"));
        var ids = new[] { Scalar<Guid>("SELECT CustomerProfileId FROM CustomerProfileOverrides WHERE CustomerKey = N'crm:101'") };

        // Run again: nothing to do, nothing changes - not the identifiers, not the history.
        Exec(DeployScript(dryRun: false));
        Exec(DeployScript(dryRun: true));
        Assert.Equal(ids[0], Scalar<Guid>("SELECT CustomerProfileId FROM CustomerProfileOverrides WHERE CustomerKey = N'crm:101'"));
        Assert.Equal(1, Scalar<int>($"SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = N'{Migration}'"));

        // The database the script produced is exactly the one EF expects: nothing left to migrate except the migrations that came AFTER this one
        // (AddAssignmentHistoryPreviousDepartment, AddInteractionEndReceivedAt, AddEmployeeReviews and AddQueueingTicketTypeName, each deployed by its own script), and the model works against it.
        using var context = CreateContext();
        Assert.Equal(["20261119120249_AddAssignmentHistoryPreviousDepartment", "20261120074059_AddInteractionEndReceivedAt", "20261124071552_AddEmployeeReviews", "20261126154428_AddEmployeeReviewActivationCodes", "20261126175759_AddQueueingTicketTypeName"], context.Database.GetPendingMigrations());
        context.Database.Migrate();
        Assert.Equal(2, context.CustomerProfileOverrides.Count());
        var customer = context.CustomerProfileOverrides.Single(o => o.CustomerKey == "crm:101");
        Assert.Equal(ids[0], customer.CustomerProfileId);
        context.CustomerProfileChanges.Add(Change(customer.CustomerProfileId, Guid.NewGuid(), "FullName", null, "Aisha"));
        context.CustomerPropertyAssociations.Add(new CustomerPropertyAssociation("crm:101", customer.CustomerProfileId, "Tiger Tower", "T-2", PropertyRelationship.Tenant, Guid.NewGuid(), DateTime.UtcNow));
        context.SaveChanges();
    }

    [VisibilitySqlServerFact]
    public void ProductionScript_RefusesAnyOtherDatabase_AndADatabaseMissingThePreviousMigration()
    {
        CreateDatabase();
        using (var context = CreateContext()) context.GetService<IMigrator>().Migrate("20261117101035_AddQueueingIntegration");   // before AddCustomerProfileOverrides

        var wrongDatabase = File.ReadAllText(RepoFile("Deploy_TigerCS_PROD_AddCustomerProfileAuditAndProperties.sql"));   // guard still says Production
        var guard = Assert.Throws<SqlException>(() => Exec(wrongDatabase));
        Assert.Contains("only runs against [TigerCS_Ticketing_Prod]", guard.Message);

        var missingPrevious = Assert.Throws<SqlException>(() => Exec(DeployScript(dryRun: false)));
        Assert.Contains("AddCustomerProfileOverrides", missingPrevious.Message);   // tells the operator what to apply first
        Assert.False(TableExists("CustomerProfileChanges"));
    }

    // ------------------------------------------------------------------------------------------- EF's migration

    [VisibilitySqlServerFact]
    public void EfMigration_OverExistingCustomers_AlsoGivesEachItsOwnStableId_BeforeTheUniqueIndex()
    {
        CreateProductionLikeDatabase();

        using (var context = CreateContext())
        {
            context.Database.Migrate();   // the unique index on CustomerProfileId would fail if both rows still held the all-zero default
        }

        Assert.Equal(2, Scalar<int>("SELECT COUNT(DISTINCT CustomerProfileId) FROM CustomerProfileOverrides"));
        Assert.True(Recorded());

        // The standalone EF script (what the repository ships) does the same.
        RollBackTheAuditMigrationAndApplyTheStandaloneScript(RepoFile("AddCustomerProfileAuditAndProperties.sql"));
    }

    private void RollBackTheAuditMigrationAndApplyTheStandaloneScript(string scriptPath)
    {
        Exec("DELETE FROM __EFMigrationsHistory WHERE MigrationId = N'" + Migration + "';");
        Exec("DROP TABLE CustomerPropertyAssociations; DROP TABLE CustomerProfileChanges; DROP INDEX IX_CustomerProfileOverrides_CustomerProfileId ON CustomerProfileOverrides;");
        Exec("DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id WHERE dc.parent_object_id = OBJECT_ID(N'dbo.CustomerProfileOverrides') AND c.name = N'CustomerProfileId'); EXEC(N'ALTER TABLE CustomerProfileOverrides DROP CONSTRAINT ' + @df);");
        Exec("ALTER TABLE CustomerProfileOverrides DROP COLUMN AdditionalPhones, Country, CustomerProfileId, Email, Emirate, FullName, LandlineNumber, Location, Nationality, PreferredLanguage;");
        Assert.False(ColumnExists("CustomerProfileOverrides", "CustomerProfileId"));

        // The EF script is split on GO lines exactly as sqlcmd would.
        foreach (var batch in Regex.Split(File.ReadAllText(scriptPath), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            Exec(batch);
        }

        Assert.True(Recorded());
        Assert.Equal(2, Scalar<int>("SELECT COUNT(DISTINCT CustomerProfileId) FROM CustomerProfileOverrides"));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_CustomerProfileChanges_AppendOnly' AND is_disabled = 0"));
    }

    // -------------------------------------------------------------------------------- the guarantees themselves

    [VisibilitySqlServerFact]
    public void TheChangeHistory_IsAppendOnly_TheDatabaseRefusesAnyUpdateOrDelete_WhoeverAsks()
    {
        CreateDatabase();
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.CustomerProfileChanges.Add(Change(Guid.NewGuid(), Guid.NewGuid(), "FullName", "Old", "New"));
            context.SaveChanges();
        }

        var update = Assert.Throws<SqlException>(() => Exec("UPDATE CustomerProfileChanges SET NewValue = N'forged'"));
        var delete = Assert.Throws<SqlException>(() => Exec("DELETE FROM CustomerProfileChanges"));
        var truncateStyle = Assert.Throws<SqlException>(() => Exec("DELETE FROM CustomerProfileChanges WHERE 1 = 1"));
        Assert.All(new[] { update, delete, truncateStyle }, e =>
        {
            Assert.Equal(51000, e.Number);
            Assert.Contains("append-only", e.Message);
        });

        // Through EF as well: an application bug cannot rewrite history either.
        using (var context = CreateContext())
        {
            var row = context.CustomerProfileChanges.Single();
            context.CustomerProfileChanges.Remove(row);
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        }

        using (var context = CreateContext())
        {
            Assert.Equal("New", context.CustomerProfileChanges.Single().NewValue);
        }

        // Adding is of course allowed - history is append-only, not frozen.
        using (var context = CreateContext())
        {
            context.CustomerProfileChanges.Add(Change(Guid.NewGuid(), Guid.NewGuid(), "Email", null, "a@b.co"));
            context.SaveChanges();
            Assert.Equal(2, context.CustomerProfileChanges.Count());
        }
    }

    [VisibilitySqlServerFact]
    public async Task ARetriedOperation_RacingItself_IsRecordedOnce_TheUniqueChangeKeyDecides()
    {
        CreateDatabase();
        using (var context = CreateContext()) context.Database.Migrate();
        var profile = Guid.NewGuid();
        var correlation = Guid.NewGuid();

        // Eight identical requests at once (a double-clicked Save, a retrying client).
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var context = CreateContext();
            context.CustomerProfileChanges.Add(Change(profile, correlation, "FullName", "Old", "New"));
            try
            {
                await context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                return false;   // the unique index refused the duplicate
            }
        }));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, Scalar<int>($"SELECT COUNT(*) FROM CustomerProfileChanges WHERE CustomerProfileId = '{profile}'"));
    }

    [VisibilitySqlServerFact]
    public void TwoEmployeesEditingTheSameCustomer_TheSecondSaveIsRejected_NotSilentlyOverwritten()
    {
        CreateDatabase();
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            var row = new CustomerProfileOverride("crm:101", 1, 101, null, null, null, Guid.NewGuid(), DateTime.UtcNow);
            context.CustomerProfileOverrides.Add(row);
            context.SaveChanges();
        }

        using var first = CreateContext();
        using var second = CreateContext();
        var a = first.CustomerProfileOverrides.Single();
        var b = second.CustomerProfileOverrides.Single();   // both read the same version
        a.SetDetails("First Editor", null, null, null, null, null, null, null, null, Guid.NewGuid(), DateTime.UtcNow);
        b.SetDetails("Second Editor", null, null, null, null, null, null, null, null, Guid.NewGuid(), DateTime.UtcNow);

        first.SaveChanges();
        Assert.Throws<DbUpdateConcurrencyException>(() => second.SaveChanges());   // the row version moved: the lost update is refused

        using var check = CreateContext();
        Assert.Equal("First Editor", check.CustomerProfileOverrides.Single().FullName);
    }

    [VisibilitySqlServerFact]
    public async Task TheSamePropertyAddedTwiceAtOnce_IsStoredOnce_TheUniqueIndexDecides()
    {
        CreateDatabase();
        var profile = Guid.NewGuid();
        using (var context = CreateContext()) context.Database.Migrate();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var context = CreateContext();
            context.CustomerPropertyAssociations.Add(new CustomerPropertyAssociation("crm:101", profile, "Tiger Tower", "T-1", PropertyRelationship.Owner, Guid.NewGuid(), DateTime.UtcNow));
            try
            {
                await context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                return false;
            }
        }));

        Assert.Equal(1, results.Count(r => r));

        // The database's (case-insensitive) collation makes the same property written differently the same property.
        using var again = CreateContext();
        again.CustomerPropertyAssociations.Add(new CustomerPropertyAssociation("crm:101", profile, "TIGER TOWER", "t-1", PropertyRelationship.Tenant, Guid.NewGuid(), DateTime.UtcNow));
        Assert.Throws<DbUpdateException>(() => again.SaveChanges());
    }
}
