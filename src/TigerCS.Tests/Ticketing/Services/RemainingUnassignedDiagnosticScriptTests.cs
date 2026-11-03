using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>The production diagnostic must stay read-only (it ships to people who run it against Production) and keep the classifier's vocabulary.</summary>
public class RemainingUnassignedDiagnosticScriptTests
{
    private static string Script([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "Diagnose_TigerCS_PROD_RemainingOpenUnassigned_ReadOnly.sql")));

    [Fact]
    public void TheDiagnostic_NeverWritesToARealTable()
    {
        var sql = Regex.Replace(Script(), @"/\*.*?\*/|--[^\r\n]*", string.Empty, RegexOptions.Singleline);

        // Only session temp tables (#...) may be created, filled, updated or dropped.
        Assert.DoesNotMatch(new Regex(@"\b(INSERT\s+(INTO\s+)?(?!#)dbo\.|UPDATE\s+(?!#)\w*\.?\w+\s+SET|DELETE\s+FROM|MERGE\s|TRUNCATE\s|ALTER\s|DROP\s+(TABLE|INDEX|PROCEDURE|VIEW)\s+(?!#)|CREATE\s+(TABLE|INDEX|PROCEDURE|VIEW)\s+(?!#)|EXEC(UTE)?\s)", RegexOptions.IgnoreCase), sql);
        Assert.Contains("READ UNCOMMITTED", sql, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> @ExpectedDb", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiagnostic_UsesTheSameClassesAsTheClassifier()
    {
        var sql = Script();
        foreach (var cls in new[]
        {
            "PENDING_APPROVAL", "TRANSFERRED_OUT_OF_CC", "RELEASED_BY_PERSON", "HUMAN_ANSWERED_MAPPED", "HUMAN_ANSWERED_UNMAPPED", "HUMAN_REQUIRED_NOT_ROUTED",
            "HUMAN_REQUIRED_IN_CC", "MANUAL_OR_INTEGRATION", "AI_ACTIVE", "AI_STALE_NO_END_EVENT", "AI_ENDED",
        })
        {
            Assert.Contains($"'{cls}'", sql, StringComparison.Ordinal);
        }

        // The incident tickets are looked up by number.
        Assert.Contains("TG-LCS-20261102-0021", sql, StringComparison.Ordinal);
        Assert.Contains("TG-REG-20261102-0026", sql, StringComparison.Ordinal);
        Assert.Contains("TG-ACC-20261102-0023", sql, StringComparison.Ordinal);
    }
}
