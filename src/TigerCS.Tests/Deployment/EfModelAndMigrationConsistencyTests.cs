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

    /// <summary>
    /// The Original Human Agent feature applies to tickets created after deployment only: no migration or standalone script may populate
    /// <c>OriginalHumanAgentId</c> (or anything else) for existing tickets - the two migrations are pure schema changes.
    /// </summary>
    [Fact]
    public void TheOriginalHumanAgentMigrations_AndScripts_ContainNoBackfillOfExistingTickets()
    {
        foreach (var file in new[] { "AddTicketOriginalHumanAgent.sql", "AddOriginalAgentWorkflowCutoff.sql" })
        {
            var script = File.ReadAllText(FindRepositoryFile(file));
            Assert.DoesNotContain("UPDATE", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXEC(", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("INSERT INTO [Tickets]", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TicketAssignments", script, StringComparison.OrdinalIgnoreCase);
        }

        using var context = CreateSqlServerContext();
        var migrator = context.GetService<IMigrator>();
        var ef = migrator.GenerateScript(
            fromMigration: "20261110070152_AddUserTransferDepartments",
            toMigration: "20261111132429_AddOriginalAgentWorkflowCutoff",
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.DoesNotContain("UPDATE", ef, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TicketAssignments", ef, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[OriginalAgentWorkflowEnabled] bit NOT NULL DEFAULT CAST(0 AS bit)", ef);
        Assert.Contains("ALTER TABLE [Tickets] ADD [OriginalHumanAgentId] uniqueidentifier NULL", ef);
    }

    /// <summary>
    /// The assignment-end column is a pure schema change: no backfill of existing rows (a historical release has no recorded end time, and none
    /// is invented), and the standalone script matches what EF generates.
    /// </summary>
    [Fact]
    public void TheSupersededAtMigration_IsAPureNullableColumn_WithNoBackfill_AndItsScriptMatchesEf()
    {
        var script = File.ReadAllText(FindRepositoryFile("AddTicketAssignmentSupersededAt.sql"));
        Assert.DoesNotContain("UPDATE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC(", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE [TicketAssignments] ADD [SupersededAtUtc] datetime2 NULL;", script);

        using var context = CreateSqlServerContext();
        var ef = context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261112065853_AddGenesysAssignmentHistorySource",
            toMigration: "20261112071745_AddTicketAssignmentSupersededAt",
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.DoesNotContain("UPDATE", ef, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE [TicketAssignments] ADD [SupersededAtUtc] datetime2 NULL;", ef);
    }

    private const string AuditMigration = "20261119081103_AddCustomerProfileAuditAndProperties";

    /// <summary>
    /// The Customer Audit History migration: the standalone script in the repository root is exactly what EF generates for it (so what is
    /// deployed is what the code expects), it back-fills a stable identifier for every existing override BEFORE the unique index that needs
    /// it, and it installs the append-only trigger. Both statements that touch the new column or create the trigger run through EXEC,
    /// because T-SQL cannot compile them in the same batch as the ALTER / inside an IF block otherwise.
    /// </summary>
    [Fact]
    public void TheCustomerAuditMigration_StandaloneScript_MatchesEf_AndBackfillsTheStableIdBeforeTheUniqueIndex()
    {
        var script = Normalize(File.ReadAllText(FindRepositoryFile("AddCustomerProfileAuditAndProperties.sql")));

        using var context = CreateSqlServerContext();
        var ef = Normalize(context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261118124858_AddTicketAiAgentOwner", toMigration: AuditMigration, options: MigrationsSqlGenerationOptions.Idempotent));
        // The file is `dotnet ef migrations script <from> <to> --idempotent`; the migration is idempotent per statement.
        Assert.Equal(ef.TrimEnd(), script.TrimEnd());

        Assert.Contains("CREATE TABLE [CustomerProfileChanges]", script);
        Assert.Contains("CREATE TABLE [CustomerPropertyAssociations]", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_CustomerProfileChanges_ChangeKey]", script);
        Assert.Contains("EXEC(N'CREATE TRIGGER [TR_CustomerProfileChanges_AppendOnly]", script);
        Assert.Contains("EXEC(N'UPDATE [CustomerProfileOverrides] SET [CustomerProfileId] = NEWID()", script);
        Assert.True(
            script.IndexOf("SET [CustomerProfileId] = NEWID()", StringComparison.Ordinal) < script.IndexOf("[IX_CustomerProfileOverrides_CustomerProfileId]", StringComparison.Ordinal),
            "Existing rows must get their own CustomerProfileId before the unique index is built.");
        Assert.DoesNotContain("DROP ", script, StringComparison.OrdinalIgnoreCase);   // purely additive: nothing is dropped
    }

    /// <summary>
    /// The hand-written Production script must agree with the model: every column of the three tables it touches is named in it, it
    /// defaults to a dry run, refuses any other database, registers the right migration, and is one batch (no GO).
    /// </summary>
    [Fact]
    public void TheCustomerAuditProductionScript_CoversEveryColumnOfTheModel_DefaultsToADryRun_AndRegistersTheMigration()
    {
        var deploy = File.ReadAllText(FindRepositoryFile("Deploy_TigerCS_PROD_AddCustomerProfileAuditAndProperties.sql"));

        Assert.Contains("DECLARE @DryRun bit = 1;", deploy);
        Assert.Contains("DECLARE @ExpectedDb     sysname       = N'TigerCS_Ticketing_Prod';", deploy);
        Assert.Contains($"N'{AuditMigration}'", deploy);
        Assert.Contains("N'10.0.11'", deploy);
        Assert.Contains("INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)", deploy);
        Assert.DoesNotContain("\nGO", deploy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE dbo", deploy.Split("-- ROLLBACK", 2)[1].Split("-- ####", 2)[1], StringComparison.Ordinal);   // the only DROPs are in the comment block

        using var context = CreateSqlServerContext();
        var model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        foreach (var entity in new[] { typeof(TigerCS.Domain.Modules.Ticketing.CustomerProfileOverride), typeof(TigerCS.Domain.Modules.Ticketing.CustomerProfileChange), typeof(TigerCS.Domain.Modules.Ticketing.CustomerPropertyAssociation) })
        {
            var type = model.FindEntityType(entity)!;
            var table = type.GetTableName()!;
            // CustomerProfileOverrides existed before this migration (Deploy_TigerCS_PROD_AddCustomerProfileOverrides.sql); only the ten
            // columns this migration adds are its business. The two new tables are entirely its own.
            var addedHere = new HashSet<string>
            {
                "CustomerProfileId", "FullName", "Email", "LandlineNumber", "AdditionalPhones", "Nationality", "Country", "Emirate", "Location", "PreferredLanguage",
            };
            foreach (var property in type.GetProperties().Where(p => table != "CustomerProfileOverrides" || addedHere.Contains(p.GetColumnName())))
            {
                var column = property.GetColumnName();
                Assert.True(deploy.Contains($"N'{table}'", StringComparison.Ordinal) && deploy.Contains($"N'{column}'", StringComparison.Ordinal),
                    $"The Production script does not mention {table}.{column}.");
            }
        }
    }

    /// <summary>
    /// The verification script lists every migration of this build, so it cannot go stale. (It once listed 43 of them, which is how a
    /// missing CustomerProfileOverrides table went unnoticed until the API threw SQL error 208 in Production.)
    /// </summary>
    [Fact]
    public void EveryMigrationIsListedInTheProductionVerifyScript()
    {
        using var context = CreateSqlServerContext();
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var verify = File.ReadAllText(FindRepositoryFile("Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql"));

        var listed = System.Text.RegularExpressions.Regex.Matches(verify, @"^\s+\(N'(?<id>\d{14}_\w+)'\)[,;]", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups["id"].Value).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(migrations, listed);
        Assert.DoesNotMatch(@"(?im)^\s*(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|EXEC)\b(?!\s+@Repo)", verify.Replace("DECLARE @Repo TABLE", "").Replace("INSERT @Repo", ""));   // read-only
    }

    /// <summary>
    /// The structural fix for the missing-migration incident: from the Customer Audit migration on, no migration may ship without BOTH its
    /// EF-generated standalone script and its Production deployment script (dry-run, validating, registering) in the repository root.
    /// </summary>
    [Fact]
    public void EveryMigrationFromTheAuditMigrationOn_ShipsBothProductionScripts()
    {
        using var context = CreateSqlServerContext();
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Where(k => string.CompareOrdinal(k, AuditMigration) >= 0).ToList();

        Assert.Contains(AuditMigration, migrations);
        foreach (var migration in migrations)
        {
            var name = migration[(migration.IndexOf('_') + 1)..];
            FindRepositoryFile($"{name}.sql");
            var deploy = File.ReadAllText(FindRepositoryFile($"Deploy_TigerCS_PROD_{name}.sql"));
            Assert.Contains("DECLARE @DryRun bit = 1;", deploy, StringComparison.Ordinal);
            Assert.Contains(migration, deploy, StringComparison.Ordinal);
            Assert.Contains("__EFMigrationsHistory", deploy, StringComparison.Ordinal);
        }
    }

    private static string Normalize(string text) => text.TrimStart('\uFEFF').Replace("\r\n", "\n");

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

        // AddUserPermissionOverrides (per-employee permission overrides), AddCollectionsTowerAssignments (Collections tower assignments) and AddCustomerContactDetails (agent-entered customer name/e-mail) were added after
        // these four and are deployed by their own scripts; the Production script for the four must stay exactly these, immediately before them.
        // AddDirectSendInstalments (one nullable column for Direct Send) is the newest and is deployed by its own script.
        // AddCollectionsGenesysDeliveryStats (two new tables for the manual Sync from Genesys) and AddCollectionsExceptionInclusions (one new table for manual GULF_CUSTOMERS inclusions) were added after those and are deployed by their own scripts.
        // AddManualGenesysDispatchItemFields (two columns on CollectionsDispatchItems for the Manual Genesys contact list) is the newest and is deployed by its own script.
        // AddUserTransferDepartments (one new table: the departments an employee may transfer tickets to, separate from membership) is the newest and is deployed by its own script.
        // AddTicketOriginalHumanAgent (one nullable column + index on Tickets: the first eligible Human Agent, used to route a ticket back to the Call Center) is the newest and is deployed by its own script.
        // AddOriginalAgentWorkflowCutoff (one NOT NULL bit column defaulting to 0: the deployment cutoff that keeps every pre-existing ticket on the legacy workflow) is the newest and is deployed by its own script.
        // AddGenesysAssignmentHistorySource (three nullable columns on TicketAssignmentHistory: source, Genesys event time and conversation id for Genesys-driven assignments) was added after that and is deployed by its own script.
        // AddTicketAssignmentSupersededAt (one nullable datetime2 column on TicketAssignments: when an assignment ended, so SLA reporting can tell who held a ticket at a past moment) is the newest and is deployed by its own script.
        // AddQueueingIntegration (the Queueing System receiving API: origin columns on Tickets plus the event inbox, per-ticket state and mapping tables) was added after that and is deployed by its own script.
        // AddCustomerProfileOverrides (one new table: the phone, project and unit an authorised user corrected for a Customers-directory customer) is the newest and is deployed by its own script.
        // AddTicketAiAgentOwner (one nullable column on Tickets: the AI Agent as the ticket's actual assigned owner) was added after that and is deployed by its own script.
        // AddCustomerProfileAuditAndProperties (the append-only CustomerProfileChanges history, manual CustomerPropertyAssociations and the profile detail columns on CustomerProfileOverrides) is the newest and is deployed by its own script.
        // AddAssignmentHistoryPreviousDepartment (one nullable int column on TicketAssignmentHistory: the department a Genesys-handled ticket was in before a department-changing entry) is the newest and is deployed by its own script.
        // AddInteractionEndReceivedAt (one nullable datetime2 column on TicketInteractions: when TigerCS received a conversation's end - the voice no-answer window anchor) is the newest and is deployed by its own script.
        Assert.Equal("20261120074059_AddInteractionEndReceivedAt", migrations[^1]);
        Assert.Equal("20261119120249_AddAssignmentHistoryPreviousDepartment", migrations[^2]);
        Assert.Equal("20261119081103_AddCustomerProfileAuditAndProperties", migrations[^3]);
        Assert.Equal("20261118124858_AddTicketAiAgentOwner", migrations[^4]);
        Assert.Equal("20261118120000_AddCustomerProfileOverrides", migrations[^5]);
        Assert.Equal("20261117101035_AddQueueingIntegration", migrations[^6]);
        Assert.Equal("20261112071745_AddTicketAssignmentSupersededAt", migrations[^7]);
        Assert.Equal("20261112065853_AddGenesysAssignmentHistorySource", migrations[^8]);
        Assert.Equal("20261111132429_AddOriginalAgentWorkflowCutoff", migrations[^9]);
        Assert.Equal("20261111073632_AddTicketOriginalHumanAgent", migrations[^10]);
        Assert.Equal("20261110070152_AddUserTransferDepartments", migrations[^11]);
        Assert.Equal("20261109123253_AddManualGenesysDispatchItemFields", migrations[^12]);
        Assert.Equal("20261109120816_AddCollectionsExceptionInclusions", migrations[^13]);
        Assert.Equal("20261109074858_AddCollectionsGenesysDeliveryStats", migrations[^14]);
        Assert.Equal("20261106044011_AddDirectSendInstalments", migrations[^15]);
        Assert.Equal("20261105094644_AddCustomerContactDetails", migrations[^16]);
        Assert.Equal("20261104130835_AddCollectionsTowerAssignments", migrations[^17]);
        Assert.Equal("20261102065459_AddUserPermissionOverrides", migrations[^18]);
        Assert.Equal(FourPendingMigrations, migrations.SkipLast(18).TakeLast(4));
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
