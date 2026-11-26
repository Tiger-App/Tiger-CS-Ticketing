using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The two historical-timestamp scripts (read-only diagnostic; remediation with DryRun on by default) executed on a REAL SQL Server against a fully migrated database that holds
/// historical conversations of several kinds: one PROVEN_START_OFFSET, one PROVEN_FULL_OFFSET, one that is correct, and several that only LOOK suspicious (single evidence).
/// The remediation may move ONLY InteractionStartedAtUtc of the PROVEN_START_OFFSET conversation; the full-offset one is reported but never touched, and ticket dates, end dates,
/// transcript lines, connect times, status, owner, closure, SLA and every existing audit row must stay byte-for-byte as they were. The original start is preserved in the audit row and restorable.
/// </summary>
public sealed class GenesysHistoricalTimestampScriptsSqlServerTests : IDisposable
{
    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsHistTsTest_{Guid.NewGuid():N}";
    private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Unspecified);

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

    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(name);
    }

    private static string Lit(DateTime d) => $"'{d:yyyy-MM-ddTHH:mm:ss.fffffff}'";

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

    private T Scalar<T>(string sql) => (T)Convert.ChangeType(Run(sql)[0][0][0]!, typeof(T));

    /// <summary>
    /// Inserts one row giving values only for <paramref name="values"/>; every other required column gets a neutral default of its type, so the test does not depend on the
    /// full shape of tables it does not care about. Returns the identity value.
    /// </summary>
    private long Insert(string table, Dictionary<string, string> values)
    {
        var columns = Run($"""
            SELECT c.name, t.name, c.is_nullable, c.is_identity, c.is_computed, CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'dbo.{table}')
            """)[0];
        var names = new List<string>();
        var literals = new List<string>();
        foreach (var c in columns)
        {
            var name = (string)c[0]!; var type = (string)c[1]!;
            if ((bool)c[3]! || (bool)c[4]! || type == "timestamp") continue;
            if (values.TryGetValue(name, out var literal)) { names.Add($"[{name}]"); literals.Add(literal); continue; }
            if ((bool)c[2]! || Convert.ToInt32(c[5]) == 1) continue;
            names.Add($"[{name}]");
            literals.Add(type switch
            {
                "uniqueidentifier" => "NEWID()",
                "datetime2" or "datetime" => Lit(T0),
                "nvarchar" or "varchar" => $"N'{Guid.NewGuid():N}'",
                "varbinary" => "0x00",
                _ => "0",
            });
        }

        return Convert.ToInt64(Run($"ALTER TABLE dbo.{table} NOCHECK CONSTRAINT ALL; INSERT dbo.{table} ({string.Join(',', names)}) VALUES ({string.Join(',', literals)}); SELECT CAST(ISNULL(SCOPE_IDENTITY(), 0) AS bigint);")[0][0][0]);
    }

    private long Ticket(DateTime? firstHumanResponse)
        => Insert("Tickets", new() { ["FirstHumanResponseAtUtc"] = firstHumanResponse is { } f ? Lit(f) : "NULL", ["CreatedAtUtc"] = Lit(T0) });

    private long Conversation(long ticketId, string conv, DateTime createdServer, DateTime start, DateTime? end)
        => Insert("TicketInteractions", new()
        {
            ["TicketId"] = ticketId.ToString(), ["GenesysConversationId"] = $"N'{conv}'", ["CreatedAtUtc"] = Lit(createdServer),
            ["InteractionStartedAtUtc"] = Lit(start), ["EndedAtUtc"] = end is { } e ? Lit(e) : "NULL",
        });

    private void EndReceipt(long ticketId, string conv, DateTime receivedServer)
        => Insert("AuditEntries", new()
        {
            ["Action"] = "N'GenesysConversationEnded'", ["EntityType"] = "N'GenesysConversation'", ["EntityId"] = $"N'{conv}'",
            ["AfterValue"] = $"N'TicketId={ticketId};Reason=ended'", ["OccurredAtUtc"] = Lit(receivedServer),
        });

    private void Line(long interactionId, int sequence, DateTime sent)
        => Insert("TicketInteractionMessages", new() { ["TicketInteractionId"] = interactionId.ToString(), ["Sequence"] = sequence.ToString(), ["SentAtUtc"] = Lit(sent), ["Sender"] = "2" });

    private void Handler(long interactionId, DateTime recorded, DateTime connected)
        => Insert("TicketInteractionHandlers", new() { ["TicketInteractionId"] = interactionId.ToString(), ["RecordedAtUtc"] = Lit(recorded), ["ConnectedAtUtc"] = Lit(connected) });

    private sealed record Seeded(long Full, long StartProven, long Correct, long SuspectLineMismatch, long SuspectEndOnly, long SuspectNoEvidence, long SuspectNot4h, long SuspectFourHourAlone)
    {
        public long[] Proven => [Full, StartProven];
        public long[] NotProven => [Correct, SuspectLineMismatch, SuspectEndOnly, SuspectNoEvidence, SuspectNot4h, SuspectFourHourAlone];
    }

    private void CreateDatabase()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using var context = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);
        context.Database.Migrate();
    }

    /// <summary>PROVEN_FULL_OFFSET (the 1878 shape): ticket created 08:00, "ended" received 08:10; stored start/end 12:00/12:10, lines 12:01/12:05, connect 12:01.</summary>
    private long AddFull()
    {
        var full = Ticket(T0.AddHours(4).AddMinutes(1));
        var fi = Conversation(full, "conv-full", T0.AddSeconds(5), T0.AddHours(4), T0.AddHours(4).AddMinutes(10));
        EndReceipt(full, "conv-full", T0.AddMinutes(10).AddSeconds(2));
        Line(fi, 1, T0.AddHours(4).AddMinutes(1)); Line(fi, 2, T0.AddHours(4).AddMinutes(5));
        Handler(fi, T0.AddMinutes(2), T0.AddHours(4).AddMinutes(1));
        Insert("TicketSlaInstances", new()
        {
            ["TicketId"] = full.ToString(), ["PeriodStartAtUtc"] = Lit(T0), ["FirstResponseDueAtUtc"] = Lit(T0.AddHours(2)), ["FirstResponseBreached"] = "1",
        });
        return full;
    }

    /// <summary>
    /// PROVEN_START_OFFSET (the 24 Nov 2026 shape): only the start is +4h. The conversation really started 07:40, its first line is 07:40:01, the ticket was created 08:02,
    /// the end (08:12) and lines are correct; the stored start is 11:40 (UAE 15:40) - 3h38m after the ticket was created, 4h after the first line.
    /// </summary>
    private long AddStartProven(string conv = "conv-start-proven")
    {
        var t = Ticket(null);
        var i = Conversation(t, conv, T0.AddMinutes(2), T0.AddHours(3).AddMinutes(40), T0.AddMinutes(12));
        EndReceipt(t, conv, T0.AddMinutes(12).AddSeconds(1));
        Line(i, 1, T0.AddMinutes(-20).AddSeconds(1)); Line(i, 2, T0.AddMinutes(5));
        Handler(i, T0.AddMinutes(2), T0.AddMinutes(3));   // plausible connect time: no evidence, must be reported NOT PROPOSED
        return t;
    }

    private long AddCorrect()
    {
        var correct = Ticket(T0.AddMinutes(1));
        var ki = Conversation(correct, "conv-correct", T0.AddSeconds(3), T0, T0.AddMinutes(10));
        EndReceipt(correct, "conv-correct", T0.AddMinutes(10).AddSeconds(1));
        Line(ki, 1, T0.AddMinutes(1));
        return correct;
    }

    private (long LineMismatch, long EndOnly, long NoEvidence, long Not4h, long FourHourAlone) AddSuspects()
    {
        // start looks +4h, a line exists but is itself 4h late and there is no end receipt: nothing independent proves the start
        var lineMismatch = Ticket(null);
        var si = Conversation(lineMismatch, "conv-line-mismatch", T0.AddSeconds(3), T0.AddHours(4), null);
        Line(si, 1, T0.AddHours(4).AddMinutes(1));

        // the end looks +4h but the start is fine (mixed): single evidence
        var endOnly = Ticket(null);
        var ei = Conversation(endOnly, "conv-end-only", T0.AddSeconds(3), T0, T0.AddHours(4).AddMinutes(10));
        EndReceipt(endOnly, "conv-end-only", T0.AddMinutes(10).AddSeconds(1));
        Line(ei, 1, T0.AddMinutes(1));

        // impossible start (after its own ticket) with NO transcript and NO end receipt: no independent evidence at all
        var noEvidence = Ticket(null);
        Conversation(noEvidence, "conv-no-evidence", T0.AddSeconds(3), T0.AddHours(3), null);

        // later than its ticket by 6h: a 4h shift cannot explain it
        var not4h = Ticket(null);
        var ni = Conversation(not4h, "conv-not-4h", T0.AddSeconds(3), T0.AddHours(6), null);
        Line(ni, 1, T0.AddHours(6).AddMinutes(1));

        // exactly +4h later than its ticket, but the first line sits an hour after the would-be start and the end is plausible: "4h alone" is not proof
        var alone = Ticket(null);
        var ai = Conversation(alone, "conv-4h-alone", T0.AddSeconds(3), T0.AddHours(4), T0.AddHours(2));
        EndReceipt(alone, "conv-4h-alone", T0.AddHours(2).AddSeconds(1));
        Line(ai, 1, T0.AddHours(1));
        return (lineMismatch, endOnly, noEvidence, not4h, alone);
    }

    private Seeded Seed()
    {
        CreateDatabase();
        var full = AddFull();
        var startProven = AddStartProven();
        var correct = AddCorrect();
        var (lineMismatch, endOnly, noEvidence, not4h, alone) = AddSuspects();
        return new Seeded(full, startProven, correct, lineMismatch, endOnly, noEvidence, not4h, alone);
    }

    private string Snapshot(params string[] tables)
        => string.Join("\n", tables.Select(t => Run($"SELECT * FROM dbo.{t} ORDER BY 1 FOR JSON AUTO")[0].Select(r => (string?)r[0]).Aggregate("", (a, b) => a + b)));

    private static readonly string[] Everything = ["Tickets", "TicketInteractions", "TicketInteractionMessages", "TicketInteractionHandlers", "AuditEntries", "TicketSlaInstances", "TicketAssignments", "TicketStatusHistory", "TicketResolutions"];
    private static readonly string[] ProtectedTables = ["Tickets", "TicketSlaInstances", "TicketAssignments", "TicketStatusHistory", "TicketResolutions"];

    private string Remediate(bool dryRun, long? ticketId = null)
    {
        var script = File.ReadAllText(RepoFile("Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql"));
        Assert.Contains("DECLARE @DryRun bit = 1;", script);   // DryRun is the default
        script = script.Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal).Replace("N'TGAESHJDBP02'", "@@SERVERNAME", StringComparison.Ordinal);
        if (ticketId is { } t) script = script.Replace("DECLARE @TicketId bigint = NULL;", $"DECLARE @TicketId bigint = {t};", StringComparison.Ordinal);
        return dryRun ? script : script.Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;", StringComparison.Ordinal);
    }

    private string Diagnose() => File.ReadAllText(RepoFile("Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql"));

    private static string Cell(object?[] row, int i) => Convert.ToString(row[i]) ?? string.Empty;

    [VisibilitySqlServerFact]
    public void TheDiagnostic_IsReadOnly_SeparatesProvenFromSuspectAndCorrect_AndCountsEachReason()
    {
        var s = Seed();
        var before = Snapshot(Everything);

        var sets = Run(Diagnose());
        var dump = string.Join("\n", sets.Select((set, i) => $"[{i}] " + string.Join(" || ", set.Select(r => string.Join(",", r)))));

        Assert.Equal(before, Snapshot(Everything));   // read-only in fact

        // 1. summary: Classification, SubType, ReasonCode, Conversations, ProposedCorrections, Reason
        int Conversations(string reason) => sets[0].Where(r => Cell(r, 2) == reason).Sum(r => Convert.ToInt32(r[3]));
        int Proposed(string reason) => sets[0].Where(r => Cell(r, 2) == reason).Sum(r => Convert.ToInt32(r[4]));
        Assert.True(Conversations("START_LATER_THAN_TICKET_AND_FIRST_LINE") == 1, dump);
        Assert.Equal(1, Proposed("START_LATER_THAN_TICKET_AND_FIRST_LINE"));                       // start only: end and lines stay
        Assert.Equal(1, Conversations("START_LATER_THAN_TICKET_AND_END_LATER_THAN_RECEIPT"));
        Assert.Equal(5, Proposed("START_LATER_THAN_TICKET_AND_END_LATER_THAN_RECEIPT"));           // start + end + 2 lines + 1 connect
        Assert.Equal(1, Conversations("NO_INCONSISTENCY_FOUND"));
        Assert.Equal(0, Proposed("NO_INCONSISTENCY_FOUND"));
        Assert.Equal(2, Conversations("START_IMPOSSIBLE_FIRST_LINE_DOES_NOT_CORROBORATE"));         // line mismatch + "4h alone"
        Assert.Equal(1, Conversations("END_OR_LINE_IMPOSSIBLE_START_PLAUSIBLE"));
        Assert.Equal(1, Conversations("START_IMPOSSIBLE_NO_TRANSCRIPT_NO_END_RECEIPT"));
        Assert.Equal(1, Conversations("START_NOT_4H_SHAPED"));
        Assert.All(sets[0].Where(r => Cell(r, 0) != "PROVEN"), r => Assert.Equal(0, Convert.ToInt32(r[4])));   // nothing suspect/correct is ever proposed

        // 2. verdict
        var v = Assert.Single(sets[1]);
        Assert.Equal(8, Convert.ToInt32(v[0])); Assert.Equal(2, Convert.ToInt32(v[1])); Assert.Equal(5, Convert.ToInt32(v[2])); Assert.Equal(1, Convert.ToInt32(v[3]));
        Assert.Equal(2, Convert.ToInt32(v[4])); Assert.Equal(6, Convert.ToInt32(v[5]));

        // 3. inconsistency types are counted
        Assert.Contains(sets[2], r => Cell(r, 0) == "START_AFTER_TICKET_CREATED" && Convert.ToInt32(r[1]) == 6);
        Assert.Contains(sets[2], r => Cell(r, 0) == "START_AFTER_TICKET_CREATED_BY_MORE_THAN_4H" && Convert.ToInt32(r[1]) == 1);
        Assert.Contains(sets[2], r => Cell(r, 0) == "END_AFTER_END_RECEIPT" && Convert.ToInt32(r[1]) == 2);

        // 5. preview: proven tickets ONLY; exactly the two start values are the old -> proposed 4h earlier
        Assert.Equal(6, sets[4].Count);
        Assert.All(sets[4], r => Assert.Contains(Convert.ToInt64(r[0]), s.Proven));
        var startPreview = Assert.Single(sets[4], r => Convert.ToInt64(r[0]) == s.StartProven);
        Assert.Equal("TicketInteractions.InteractionStartedAtUtc", Cell(startPreview, 3));
        Assert.Equal(T0.AddHours(3).AddMinutes(40), (DateTime)startPreview[5]!);
        Assert.Equal(T0.AddMinutes(-20), (DateTime)startPreview[6]!);
        Assert.Equal(T0.AddHours(4).AddMinutes(-20), (DateTime)startPreview[8]!);   // as the UI would show it (UAE = UTC+4)
        Assert.False(string.IsNullOrWhiteSpace(Cell(startPreview, 9)));

        // 6. suspects: the five not-proven ones, never the correct or proven tickets
        Assert.Equal(5, sets[5].Count);
        Assert.All(sets[5], r => Assert.DoesNotContain(Convert.ToInt64(r[0]), new[] { s.Correct, s.Full, s.StartProven }));
        // 7. the plausible connect time of the proven start-only conversation is reported, not proposed
        Assert.Single(sets[6], r => Convert.ToInt64(r[0]) == s.StartProven);
    }

    [VisibilitySqlServerFact]
    public void NoProvenRecords_ReportsZeroSafeCorrections_AndProposesNothing()
    {
        CreateDatabase();
        AddCorrect();
        AddSuspects();

        var sets = Run(Diagnose());

        var v = Assert.Single(sets[1]);
        Assert.Equal(0, Convert.ToInt32(v[1]));
        Assert.Equal(0, Convert.ToInt32(v[4])); Assert.Equal(0, Convert.ToInt32(v[5]));
        Assert.StartsWith("ZERO safe automatic corrections", Cell(v, 6));
        Assert.Empty(sets[4]);                                                       // no preview rows

        var dryRun = Run(Remediate(dryRun: true));
        Assert.DoesNotContain(dryRun, set => set.Count > 0 && set[0][0] is "PLAN: conversations");
        var before = Snapshot(Everything);
        Run(Remediate(dryRun: false));
        Assert.Equal(before, Snapshot(Everything));                                  // a real run with nothing proven writes nothing
    }

    [Fact]
    public void TheDiagnostic_ContainsNoWriteStatements()
    {
        var sql = Regex.Replace(Diagnose(), @"--[^\r\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotMatch(new Regex(@"\b(INSERT\s+(INTO\s+)?(dbo\.)?(?!@)\w+|UPDATE\s+(dbo\.)?\w+\s+SET|DELETE\s+(FROM\s+)?(dbo\.)|MERGE|DROP\s|ALTER\s|TRUNCATE|EXEC(UTE)?\s)", RegexOptions.IgnoreCase), sql);
    }

    [Fact]
    public void BothScripts_UseTheSameEvidenceBlock_AndNeverTreatFourHoursAsProof()
    {
        // the remediation's evidence block is a verbatim copy of the diagnostic's (only the table name differs)
        string Block(string sql, string table)
        {
            var from = sql.IndexOf("DECLARE " + table + " TABLE", StringComparison.Ordinal);
            var to = sql.IndexOf("CROSS APPLY (SELECT cl.Classification AS Classification", StringComparison.Ordinal);
            Assert.True(from >= 0 && to > from);
            return Regex.Replace(sql[from..to].Replace(table, "@X"), @"\s+", " ");
        }

        Assert.Equal(Block(Diagnose(), "@Plan"), Block(File.ReadAllText(RepoFile("Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql")), "@All"));
    }

    private string InteractionsExceptStart()
        => string.Join("\n", Run("SELECT TicketInteractionId, TicketId, GenesysConversationId, CreatedAtUtc, EndedAtUtc FROM dbo.TicketInteractions ORDER BY 1")[0].Select(r => string.Join(',', r)));

    private DateTime StartOf(long ticketId) => Scalar<DateTime>($"SELECT InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {ticketId}");

    [VisibilitySqlServerFact]
    public void DryRun_IsTheDefault_RevalidatesLiveData_ShowsOldAndProposedStart_ForProvenStartOnly_AndWritesNothing()
    {
        var s = Seed();
        var before = Snapshot(Everything);

        var sets = Run(Remediate(dryRun: true));

        Assert.Equal(before, Snapshot(Everything));

        // revalidation on the live data: Metric, Approved (diagnostic), Live, Status
        var reval = sets.First(x => x.Count > 0 && x[0][0] is string h && h.StartsWith("REVALIDATION"));
        Assert.Equal(6, reval.Count);
        Assert.Equal(8, Convert.ToInt32(reval[0][3]));   // conversations
        Assert.Equal(1, Convert.ToInt32(reval[1][3]));   // PROVEN_START_OFFSET
        Assert.Equal(5, Convert.ToInt32(reval[2][3]));   // SUSPECT
        Assert.Equal(1, Convert.ToInt32(reval[3][3]));   // CORRECT
        Assert.Equal(1, Convert.ToInt32(reval[4][3]));   // PROVEN_FULL_OFFSET: reported, never corrected here
        Assert.Equal(0, Convert.ToInt32(reval[5][3]));   // previous correction audit records

        // the plan holds the start-only conversation and NOT the full-offset one
        var plan = sets.First(x => x.Count > 0 && x[0].Length > 0 && x[0][0] is "PLAN: conversations");
        var start = Assert.Single(plan);
        Assert.Equal(s.StartProven, Convert.ToInt64(start[1]));
        Assert.Equal(T0.AddHours(3).AddMinutes(40), (DateTime)start[6]!);   // old
        Assert.Equal(T0.AddMinutes(-20), (DateTime)start[7]!);              // proposed
        var preview = sets.First(x => x.Count > 0 && x[0][0] is "PLAN: column preview");
        Assert.Equal("TicketInteractions.InteractionStartedAtUtc", Cell(Assert.Single(preview), 3));   // one value, one column
        var rollback = sets.First(x => x.Count > 0 && x[0][0] is "ROLLBACK STATEMENTS");
        Assert.Contains("SET InteractionStartedAtUtc = '2026-11-10T11:40:00", Cell(Assert.Single(rollback), 1));
        Assert.DoesNotContain(sets, x => x.Count > 0 && x[0][0] is string h && h.StartsWith("SLA REVIEW"));
    }

    [VisibilitySqlServerFact]
    public void DiagnosticPreview_AndRemediationPlan_AgreeValueForValue_ForTheStartOnlyConversation()
    {
        var s = Seed();
        var diagnostic = Run(Diagnose())[4]
            .Where(r => Convert.ToInt64(r[0]) == s.StartProven && Cell(r, 3) == "TicketInteractions.InteractionStartedAtUtc")
            .Select(r => $"{Cell(r, 0)}|{Cell(r, 3)}|{Cell(r, 4)}|{((DateTime)r[5]!):O}|{((DateTime)r[6]!):O}").ToList();
        var plan = Run(Remediate(dryRun: true)).First(x => x.Count > 0 && x[0][0] is "PLAN: column preview")
            .Select(r => $"{Cell(r, 1)}|{Cell(r, 3)}|{Cell(r, 4)}|{((DateTime)r[5]!):O}|{((DateTime)r[6]!):O}").ToList();

        Assert.Single(diagnostic);
        Assert.Equal(diagnostic, plan);
    }

    [VisibilitySqlServerFact]
    public void RealRun_CorrectsOnlyTheProvenStart_ChangesNothingElse_PreservesTheOriginal_AndIsIdempotent()
    {
        var s = Seed();
        var protectedBefore = Snapshot(ProtectedTables);                                         // ticket dates/status/owner, SLA, assignments, history, resolutions
        var transcriptBefore = Snapshot("TicketInteractionMessages", "TicketInteractionHandlers");  // transcript lines and connect times
        var interactionsBefore = InteractionsExceptStart();                                       // ends, creation dates, ids
        var otherStartsBefore = Run($"SELECT TicketInteractionId, InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId <> {s.StartProven} ORDER BY 1")[0].Select(r => string.Join(',', r)).ToList();
        var maxAudit = Scalar<long>("SELECT ISNULL(MAX(AuditEntryId), 0) FROM dbo.AuditEntries");
        var existingAuditBefore = Run($"SELECT * FROM dbo.AuditEntries WHERE AuditEntryId <= {maxAudit} ORDER BY 1 FOR JSON AUTO")[0].Select(r => (string?)r[0]).Aggregate("", (a, b) => a + b);

        Run(Remediate(dryRun: false));

        Assert.Equal(T0.AddMinutes(-20), StartOf(s.StartProven));                                // the one proven start moved, 4h earlier
        Assert.Equal(protectedBefore, Snapshot(ProtectedTables));
        Assert.Equal(transcriptBefore, Snapshot("TicketInteractionMessages", "TicketInteractionHandlers"));
        Assert.Equal(interactionsBefore, InteractionsExceptStart());                             // no end date, creation date or id moved
        Assert.Equal(otherStartsBefore, Run($"SELECT TicketInteractionId, InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId <> {s.StartProven} ORDER BY 1")[0].Select(r => string.Join(',', r)).ToList());
        Assert.Equal(T0.AddHours(4), StartOf(s.Full));                                           // PROVEN_FULL_OFFSET is reported, never corrected by this script
        Assert.Equal(T0.AddHours(4).AddMinutes(1), Scalar<DateTime>($"SELECT FirstHumanResponseAtUtc FROM dbo.Tickets WHERE TicketId = {s.Full}"));

        // audit: existing rows untouched, exactly one new row, holding the ORIGINAL and the new start
        Assert.Equal(existingAuditBefore, Run($"SELECT * FROM dbo.AuditEntries WHERE AuditEntryId <= {maxAudit} ORDER BY 1 FOR JSON AUTO")[0].Select(r => (string?)r[0]).Aggregate("", (a, b) => a + b));
        Assert.Equal(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.AuditEntries WHERE AuditEntryId > {maxAudit}"));
        var audit = Run($"SELECT BeforeValue, AfterValue FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected' AND EntityId = N'{s.StartProven}'")[0].Single();
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T11:40:00", (string)audit[0]!);      // original
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T07:40:00", (string)audit[1]!);      // corrected
        Assert.Contains("Proof=PROVEN_START_OFFSET", (string)audit[1]!);
        Assert.Contains("Column=TicketInteractions.InteractionStartedAtUtc", (string)audit[1]!);
        Assert.Equal(0, Scalar<int>($"SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected' AND EntityId = N'{s.Full}'"));

        // idempotent: a second real run finds nothing left to correct and writes nothing
        var snapshot = Snapshot(Everything);
        Run(Remediate(dryRun: false));
        Assert.Equal(snapshot, Snapshot(Everything));
    }

    [VisibilitySqlServerFact]
    public void AConversationWithACorrectionAuditRow_IsNeverPlannedAgain_EvenIfItsStartLooksShiftedAgain()
    {
        var s = Seed();
        Run(Remediate(dryRun: false, ticketId: s.StartProven));
        Run($"UPDATE dbo.TicketInteractions SET InteractionStartedAtUtc = {Lit(T0.AddHours(3).AddMinutes(40))} WHERE TicketId = {s.StartProven}");   // looks proven again
        var auditBefore = Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries");

        Run(Remediate(dryRun: false, ticketId: s.StartProven));

        Assert.Equal(auditBefore, Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries"));
        Assert.Equal(T0.AddHours(3).AddMinutes(40), StartOf(s.StartProven));
    }

    [VisibilitySqlServerFact]
    public void Rollback_RestoresTheOriginalStartFromTheAuditRows_Optimistically_AndIsNotCorrectedAgain()
    {
        var s = Seed();
        var original = T0.AddHours(3).AddMinutes(40);
        Run(Remediate(dryRun: false));
        var runId = (Guid)Run("SELECT TOP (1) CorrelationId FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected'")[0][0][0]!;
        string Rollback(bool dryRun) => Remediate(dryRun).Replace("DECLARE @RollbackRunId uniqueidentifier = NULL;", $"DECLARE @RollbackRunId uniqueidentifier = '{runId}';", StringComparison.Ordinal);

        var afterCorrection = Snapshot(Everything);
        var plan = Run(Rollback(dryRun: true)).First(x => x.Count > 0 && x[0][0] is "ROLLBACK PLAN");
        Assert.Equal("WILL RESTORE", Cell(Assert.Single(plan), 6));
        Assert.Equal(afterCorrection, Snapshot(Everything));                                     // a rollback dry run writes nothing

        Run(Rollback(dryRun: false));
        Assert.Equal(original, StartOf(s.StartProven));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrectionRolledBack'"));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected'"));   // the correction record is kept

        var afterRollback = Snapshot(Everything);
        var again = Assert.Throws<SqlException>(() => Run(Rollback(dryRun: false)));             // the row no longer holds the corrected value
        Assert.Equal(50342, again.Number);
        Assert.Equal(afterRollback, Snapshot(Everything));

        Run(Remediate(dryRun: false));                                                           // a rolled-back conversation is not corrected again automatically
        Assert.Equal(original, StartOf(s.StartProven));
    }

    [VisibilitySqlServerFact]
    public void PilotOnOneTicket_TouchesOnlyThatTicket()
    {
        var s = Seed();
        var other = AddStartProven("conv-start-proven-other");   // a second proven conversation that the pilot must NOT touch

        Run(Remediate(dryRun: false, ticketId: s.StartProven));

        Assert.Equal(T0.AddMinutes(-20), StartOf(s.StartProven));
        Assert.Equal(T0.AddHours(3).AddMinutes(40), StartOf(other));
        Assert.Equal(T0.AddHours(4), StartOf(s.Full));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected'"));
    }

    [VisibilitySqlServerFact]
    public void RealRunOverAllTickets_Aborts_WhenMoreArePROVENThanWereApproved_AndWritesNothing()
    {
        Seed();
        var before = Snapshot(Everything);
        var script = Remediate(dryRun: false).Replace("@ApprovedProvenStart int = 1693", "@ApprovedProvenStart int = 0", StringComparison.Ordinal);
        Assert.Contains("@ApprovedProvenStart int = 0", script);

        var error = Assert.Throws<SqlException>(() => Run(script));

        Assert.Equal(50330, error.Number);
        Assert.Equal(before, Snapshot(Everything));
    }

    [VisibilitySqlServerFact]
    public void WrongDatabase_AbortsBeforeReadingOrChangingAnything()
    {
        Seed();
        var before = Snapshot(Everything);
        var script = File.ReadAllText(RepoFile("Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql")).Replace("DECLARE @DryRun bit = 1;", "DECLARE @DryRun bit = 0;");

        var error = Assert.Throws<SqlException>(() => Run(script));

        Assert.Contains("ABORTED", error.Message);
        Assert.Equal(before, Snapshot(Everything));
    }

    [Fact]
    public void TheRemediation_HasOptimisticChecks_ValidationAndRollbackOnEveryFailurePath_AndCanWriteOnlyTheStart()
    {
        var script = File.ReadAllText(RepoFile("Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql"));
        foreach (var number in new[] { "50310", "50320", "50321", "50322", "50323", "50330", "50340", "50341", "50342" })
            Assert.Contains($"THROW {number}", script);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", script);
        Assert.Contains("SET XACT_ABORT ON", script);
        Assert.Contains("DECLARE @DryRun bit = 1;", script);
        Assert.Contains("DECLARE @RollbackRunId uniqueidentifier = NULL;", script);

        // the only column the script can write is InteractionStartedAtUtc (correction + rollback); the only table it can insert into is the append-only audit table
        var sql = Regex.Replace(Regex.Replace(script, @"--[^\r\n]*", string.Empty), @"N?'(?:[^']|'')*'", "''");
        var updates = Regex.Matches(sql, @"UPDATE\s+(\w+)\s+SET\s+([\w\.]+)\s*=", RegexOptions.IgnoreCase);
        Assert.Equal(2, updates.Count);
        Assert.All(updates, m => Assert.Equal("i.InteractionStartedAtUtc", m.Groups[2].Value));
        Assert.DoesNotMatch(new Regex(@"\b(DELETE|MERGE|TRUNCATE|DROP|ALTER)\b", RegexOptions.IgnoreCase), sql);
        Assert.All(Regex.Matches(sql, @"INSERT\s+(dbo\.\w+)", RegexOptions.IgnoreCase), m => Assert.Equal("dbo.AuditEntries", m.Groups[1].Value, ignoreCase: true));
        Assert.DoesNotMatch(new Regex(@"SET\s+[\w\.]*\b(EndedAtUtc|SentAtUtc|ConnectedAtUtc|CreatedAtUtc|FirstHumanResponseAtUtc)\b\s*=", RegexOptions.IgnoreCase), sql);
    }
}
