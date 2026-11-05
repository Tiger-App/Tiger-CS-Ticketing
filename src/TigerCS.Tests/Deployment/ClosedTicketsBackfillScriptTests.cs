using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Deployment;

/// <summary>Static safety checks on the one-time Closed-ticket owner backfill. The script is never executed by tests (and never against Production without approval).</summary>
public sealed class ClosedTicketsBackfillScriptTests
{
    private static string Script([CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", "..")), "Backfill_TigerCS_PROD_ClosedTickets_ClearOwner.sql"));

    [Fact]
    public void TheBackfill_IsDryRunByDefault_GuardsTheDatabase_AndNeedsAnApprovedCountToCommit()
    {
        var sql = Script();

        Assert.Matches(@"DECLARE @DryRun\s+bit\s*=\s*1;", sql);                                   // dry run is the default
        Assert.Contains("DB_NAME() <> @ExpectedDb", sql);
        Assert.Matches(@"DECLARE @ApprovedTicketCount\s+int\s*=\s*NULL;", sql);                 // commit gate is closed by default
        Assert.Contains("@DryRun = 0 AND (@ApprovedTicketCount IS NULL", sql);
        Assert.Contains("ROLLBACK TRANSACTION", sql);
    }

    [Fact]
    public void TheBackfill_OnlyClearsOwnersOfClosedTickets_KeepsHistory_AndPreservesThePreviousOwnerInTheAudit()
    {
        var sql = Script();

        Assert.Contains("t.TicketStatus = @ClosedStatus AND t.CurrentOwnerEmployeeId IS NOT NULL", sql);
        Assert.Matches(@"DECLARE @ClosedStatus\s+tinyint\s*=\s*6;", sql);                         // TicketStatus.Closed
        Assert.DoesNotMatch(new Regex(@"\bDELETE\b|\bTRUNCATE\b|\bDROP\b", RegexOptions.IgnoreCase), StripComments(sql));   // history is never removed
        Assert.Contains("UPDATE a SET IsCurrent = 0", sql);                                        // assignment rows are ended, not deleted
        Assert.Contains("N'ClearAssignmentOnClosure'", sql);                                       // same audit action the application writes
        Assert.Contains("N'CurrentOwnerEmployeeId=' + CONVERT(nvarchar(36), p.PreviousOwnerId)", sql);
        Assert.DoesNotContain("TicketStatus <>", StripComments(sql).Replace("t.TicketStatus <> @ClosedStatus", string.Empty));  // never widened to other statuses
    }

    private static string StripComments(string sql) =>
        Regex.Replace(Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"--[^\r\n]*", string.Empty);
}
