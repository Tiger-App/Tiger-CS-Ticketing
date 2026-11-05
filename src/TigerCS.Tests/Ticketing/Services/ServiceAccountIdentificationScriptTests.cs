using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The service-account identification script ships to people who run it against Production: it must stay read-only, must never derive the exclusion list
/// from evidence, and the live backfill's service-account gate must stay exactly as strict as it is.
/// </summary>
public class ServiceAccountIdentificationScriptTests
{
    private static string Root(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", name)));

    private static readonly Regex WriteStatement = new(
        @"\b(INSERT\s+(INTO\s+)?(?!#|@)\w*\.|UPDATE\s+(?!#|@)\w*\.?\w+\s+SET|DELETE\s+FROM|MERGE\s|TRUNCATE\s|ALTER\s|DROP\s|CREATE\s|EXEC(UTE)?\s)", RegexOptions.IgnoreCase);

    [Fact]
    public void TheIdentificationScript_IsReadOnly_GuardedToProduction_AndNeverDerivesTheListFromEvidence()
    {
        var sql = Regex.Replace(Root("Identify_TigerCS_PROD_ServiceAccounts_ReadOnly.sql"), @"/\*.*?\*/|--[^\r\n]*", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotMatch(WriteStatement, sql);
        Assert.Contains("READ UNCOMMITTED", sql, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> @ExpectedDb", sql, StringComparison.Ordinal);

        // The only rows ever added to the configured list are the ones the operator pastes: no INSERT into @ConfiguredIds reads from the database.
        Assert.DoesNotMatch(new Regex(@"INSERT\s+@ConfiguredIds\s+(SELECT|EXEC)", RegexOptions.IgnoreCase), sql);
        // The paste lines come from @Cfg (the pasted ids) only, never from the evidence table.
        Assert.Matches(new Regex(@"PasteIntoBackfillScript\s+FROM\s+@Cfg", RegexOptions.IgnoreCase), sql);

        // Both authoritative settings are named for the operator.
        var raw = Root("Identify_TigerCS_PROD_ServiceAccounts_ReadOnly.sql");
        Assert.Contains("Authorization:ServiceIdentity:EmployeeIds", raw, StringComparison.Ordinal);
        Assert.Contains("Collections:Authorization:IntegrationEmployeeIds", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGenesysIntegrationAccountScript_IsReadOnly_AndOnlyConfirmsOnTwoIndependentFacts()
    {
        var raw = Root("Identify_TigerCS_PROD_GenesysIntegrationAccount_ReadOnly.sql");
        var sql = Regex.Replace(raw, @"/\*.*?\*/|--[^\r\n]*", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotMatch(WriteStatement, sql);
        Assert.Contains("READ UNCOMMITTED", sql, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> @ExpectedDb", sql, StringComparison.Ordinal);

        // The name is only a look-up key: an account is CONFIRMED only when the name matches AND it creates Genesys tickets AND no person handled a conversation as it.
        Assert.Contains("NameMatch = 1 AND GenesysTicketsCreated > 0 AND InteractionsHandled = 0", sql, StringComparison.Ordinal);
        Assert.Contains("Tickets", sql, StringComparison.Ordinal);
        Assert.Contains("CreatedByEmployeeId", sql, StringComparison.Ordinal);
        Assert.Contains("N'genesys.integration'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveBackfillGate_IsStillInPlace_NothingWasRelaxed()
    {
        var sql = Root("Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql");

        foreach (var code in new[] { "50803", "50804", "50805", "50806", "50807" })
        {
            Assert.Contains(code, sql, StringComparison.Ordinal);
        }

        Assert.Contains("the service-account list is empty", sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @ServiceIdsConfirmed          bit              = 0;", sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @NoServiceAccountsExist       bit              = 0;", sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @DryRun                       bit              = 1;", sql, StringComparison.Ordinal);
    }
}
