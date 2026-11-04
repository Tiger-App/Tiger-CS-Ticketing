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

    private static string StaleAiScript([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "Diagnose_TigerCS_PROD_StaleAiBackfill_12_24_48_ReadOnly.sql")));

    [Fact]
    public void TheStaleAiDiagnostic_IsReadOnly_AndComparesTwelveTwentyFourAndFortyEightHours()
    {
        var raw = StaleAiScript();
        var sql = Regex.Replace(raw, @"/\*.*?\*/|--[^\r\n]*", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotMatch(new Regex(@"\b(INSERT\s+(INTO\s+)?(?!#)dbo\.|UPDATE\s+(?!#)\w*\.?\w+\s+SET|DELETE\s+FROM|MERGE\s|TRUNCATE\s|ALTER\s|DROP\s+(TABLE|INDEX|PROCEDURE|VIEW)\s+(?!#)|CREATE\s+(TABLE|INDEX|PROCEDURE|VIEW)\s+(?!#)|EXEC(UTE)?\s)", RegexOptions.IgnoreCase), sql);
        Assert.Contains("READ UNCOMMITTED", sql, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> @ExpectedDb", sql, StringComparison.Ordinal);
        Assert.Contains("FROM (VALUES (12), (24), (48)) th(Hours)", sql, StringComparison.Ordinal);
        // Same protections as the application: pending approvals out, open handoffs and human evidence always eligible, "more than" the threshold.
        Assert.Contains("ap.Status = 1", sql, StringComparison.Ordinal);
        Assert.Contains("h.Status IN (2, 3, 4)", sql, StringComparison.Ordinal);
        Assert.Contains("> th.Hours * 3600", sql, StringComparison.Ordinal);
    }

    private static string RootScript(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", name)));

    private static readonly Regex WriteStatement = new(@"\b(INSERT\s+(INTO\s+)?(?!#)dbo\.|UPDATE\s+(?!#)\w*\.?\w+\s+SET|DELETE\s+FROM|MERGE\s|TRUNCATE\s|ALTER\s|DROP\s+(TABLE|INDEX|PROCEDURE|VIEW)\s+(?!#)|CREATE\s+(TABLE|INDEX|PROCEDURE|VIEW)\s+(?!#)|EXEC(UTE)?\s)", RegexOptions.IgnoreCase);

    [Fact]
    public void ThePhoneTicketDiagnostic_IsReadOnly_AndNamesTheExampleTicketAndEveryRootCause()
    {
        var raw = RootScript("Diagnose_TigerCS_PROD_PhoneTicketUnassigned_ReadOnly.sql");
        var sql = Regex.Replace(raw, @"/\*.*?\*/|--[^\r\n]*", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotMatch(WriteStatement, sql);
        Assert.Contains("READ UNCOMMITTED", sql, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> @ExpectedDb", sql, StringComparison.Ordinal);
        Assert.Contains("TG-ACC-20261104-0005", sql, StringComparison.Ordinal);
        foreach (var cls in new[]
        {
            "PHONE_NO_AGENT_NO_HUMAN_FLAG", "PHONE_AGENT_ID_NOT_MAPPED", "PHONE_AGENT_MAPPED_NOT_ELIGIBLE", "HUMAN_NOT_IN_CALL_CENTER",
            "HUMAN_IN_CC_EMPTY_POOL", "HUMAN_IN_CC_POOL_AVAILABLE",
        })
        {
            Assert.Contains($"'{cls}'", sql, StringComparison.Ordinal);
        }

        // No customer identity is ever selected.
        Assert.DoesNotMatch(new Regex(@"\b(i\.)?(CustomerPhone|CustomerName|CustomerEmail)\b", RegexOptions.IgnoreCase), sql);
    }

    [Fact]
    public void TheShortThresholdDiagnostic_IsReadOnly_AndComparesFifteenThirtySixtyAndOneTwentyMinutes()
    {
        var raw = RootScript("Diagnose_TigerCS_PROD_StaleAiThreshold_15m_30m_1h_2h_ReadOnly.sql");
        var sql = Regex.Replace(raw, @"/\*.*?\*/|--[^\r\n]*", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotMatch(WriteStatement, sql);
        Assert.Contains("READ UNCOMMITTED", sql, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> @ExpectedDb", sql, StringComparison.Ordinal);
        Assert.Contains("(15), (30), (60), (120)", sql, StringComparison.Ordinal);
        // Same protections as the application: pending approvals out, open handoffs and human evidence always eligible, "more than" the threshold.
        Assert.Contains("ap.Status = 1", sql, StringComparison.Ordinal);
        Assert.Contains("h.Status IN (2, 3, 4)", sql, StringComparison.Ordinal);
        Assert.Contains("> th.Minutes * 60", sql, StringComparison.Ordinal);
    }
}
