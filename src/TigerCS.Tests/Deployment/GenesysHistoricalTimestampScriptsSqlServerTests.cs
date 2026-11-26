using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The two historical-timestamp scripts (read-only diagnostic; remediation with DryRun on by default) executed on a REAL SQL Server against a fully migrated database that holds
/// four kinds of historical conversation: one proven +4h, one that is correct, and two that only LOOK suspicious (single evidence). Only the proven one may ever move, only its
/// four timestamp columns may change, and ticket status, owner, closure, SLA and every existing audit row must stay byte-for-byte as they were.
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
    private long AddStartProven()
    {
        var t = Ticket(null);
        var i = Conversation(t, "conv-start-proven", T0.AddMinutes(2), T0.AddHours(3).AddMinutes(40), T0.AddMinutes(12));
        EndReceipt(t, "conv-start-proven", T0.AddMinutes(12).AddSeconds(1));
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

    [VisibilitySqlServerFact]
    public void DryRun_IsTheDefault_ShowsOldAndProposed_ForProvenOnly_AndWritesNothing()
    {
        var s = Seed();
        var before = Snapshot(Everything);

        var sets = Run(Remediate(dryRun: true));

        Assert.Equal(before, Snapshot(Everything));
        var plan = sets.First(x => x.Count > 0 && x[0].Length > 0 && x[0][0] is "PLAN: conversations");
        Assert.Equal(2, plan.Count);
        var full = Assert.Single(plan, r => Convert.ToInt64(r[1]) == s.Full);
        Assert.Equal(T0.AddHours(4), (DateTime)full[6]!);   // old
        Assert.Equal(T0, (DateTime)full[7]!);               // proposed
        var start = Assert.Single(plan, r => Convert.ToInt64(r[1]) == s.StartProven);
        Assert.Equal(T0.AddHours(3).AddMinutes(40), (DateTime)start[6]!);
        Assert.Equal(T0.AddMinutes(-20), (DateTime)start[7]!);
        Assert.Equal((DateTime)start[8]!, (DateTime)start[9]!);   // EndProposed = EndStored: a start-only conversation keeps its end
        var preview = sets.First(x => x.Count > 0 && x[0][0] is "PLAN: column preview");
        Assert.Equal(6, preview.Count);                           // 1 start-only + (start, end, 2 lines, 1 connect) full
        Assert.Contains(sets, x => x.Count == 1 && x[0][0] is string h && h.StartsWith("SLA REVIEW"));
        var rollback = sets.First(x => x.Count > 0 && x[0][0] is "ROLLBACK STATEMENTS");
        Assert.Equal(5, rollback.Count);                          // full: conversation + 2 lines + 1 connect; start-only: conversation
    }

    [VisibilitySqlServerFact]
    public void DiagnosticPreview_AndRemediationPlan_AgreeValueForValue()
    {
        Seed();
        var previewValues = Run(Diagnose())[4].Select(r => $"{Cell(r, 0)}|{Cell(r, 3)}|{Cell(r, 4)}|{((DateTime)r[5]!):O}|{((DateTime)r[6]!):O}").OrderBy(x => x).ToList();
        var planValues = Run(Remediate(dryRun: true)).First(x => x.Count > 0 && x[0][0] is "PLAN: column preview")
            .Select(r => $"{Cell(r, 1)}|{Cell(r, 3)}|{Cell(r, 4)}|{((DateTime)r[5]!):O}|{((DateTime)r[6]!):O}").OrderBy(x => x).ToList();

        Assert.Equal(previewValues, planValues);
    }

    [VisibilitySqlServerFact]
    public void RealRun_CorrectsOnlyProvenConversations_ChangesOnlyTimestamps_AppendsAudit_AndIsIdempotent()
    {
        var s = Seed();
        var protectedBefore = Snapshot(ProtectedTables);
        var auditBefore = Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries");
        var provenList = string.Join(",", s.Proven);
        var othersBefore = Run($"SELECT TicketInteractionId, InteractionStartedAtUtc, EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId NOT IN ({provenList}) ORDER BY 1")[0];
        var otherLinesBefore = Run($"SELECT m.TicketInteractionMessageId, m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId NOT IN ({provenList}) ORDER BY 1")[0];
        var startProvenEndBefore = Scalar<DateTime>($"SELECT EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {s.StartProven}");
        var startProvenLinesBefore = Run($"SELECT m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId = {s.StartProven} ORDER BY m.Sequence")[0];
        var startProvenConnectBefore = Run($"SELECT h.ConnectedAtUtc FROM dbo.TicketInteractionHandlers h JOIN dbo.TicketInteractions i ON i.TicketInteractionId = h.TicketInteractionId WHERE i.TicketId = {s.StartProven}")[0];

        Run(Remediate(dryRun: false));

        // PROVEN_FULL_OFFSET: every shifted value is exactly 4h earlier
        var c = Run($"SELECT InteractionStartedAtUtc, EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {s.Full}")[0].Single();
        Assert.Equal(T0, (DateTime)c[0]!);
        Assert.Equal(T0.AddMinutes(10), (DateTime)c[1]!);
        var lines = Run($"SELECT m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId = {s.Full} ORDER BY m.Sequence")[0];
        Assert.Equal([T0.AddMinutes(1), T0.AddMinutes(5)], lines.Select(r => (DateTime)r[0]!).ToArray());
        var connect = Run($"SELECT h.ConnectedAtUtc FROM dbo.TicketInteractionHandlers h JOIN dbo.TicketInteractions i ON i.TicketInteractionId = h.TicketInteractionId WHERE i.TicketId = {s.Full}")[0].Single();
        Assert.Equal(T0.AddMinutes(1), (DateTime)connect[0]!);

        // PROVEN_START_OFFSET: only the start moved; end, lines and connect time are byte-for-byte the same
        Assert.Equal(T0.AddMinutes(-20), Scalar<DateTime>($"SELECT InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {s.StartProven}"));
        Assert.Equal(startProvenEndBefore, Scalar<DateTime>($"SELECT EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {s.StartProven}"));
        Assert.Equal(startProvenLinesBefore.Select(r => (DateTime)r[0]!), Run($"SELECT m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId = {s.StartProven} ORDER BY m.Sequence")[0].Select(r => (DateTime)r[0]!));
        Assert.Equal(startProvenConnectBefore.Select(r => (DateTime)r[0]!), Run($"SELECT h.ConnectedAtUtc FROM dbo.TicketInteractionHandlers h JOIN dbo.TicketInteractions i ON i.TicketInteractionId = h.TicketInteractionId WHERE i.TicketId = {s.StartProven}")[0].Select(r => (DateTime)r[0]!));

        // everyone else is untouched: the correct conversation and every suspect
        Assert.Equal(othersBefore.Select(r => string.Join(',', r)), Run($"SELECT TicketInteractionId, InteractionStartedAtUtc, EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId NOT IN ({provenList}) ORDER BY 1")[0].Select(r => string.Join(',', r)));
        Assert.Equal(otherLinesBefore.Select(r => string.Join(',', r)), Run($"SELECT m.TicketInteractionMessageId, m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId NOT IN ({provenList}) ORDER BY 1")[0].Select(r => string.Join(',', r)));

        // ticket status/owner/closure/SLA/assignments/history/resolutions: byte-for-byte (First Human Response and the SLA flag are reported, NOT recalculated)
        Assert.Equal(protectedBefore, Snapshot(ProtectedTables));
        Assert.Equal(T0.AddHours(4).AddMinutes(1), Scalar<DateTime>($"SELECT FirstHumanResponseAtUtc FROM dbo.Tickets WHERE TicketId = {s.Full}"));

        // audit: existing rows untouched, exactly one new row per corrected ticket, with old and new values and the proof type
        Assert.Equal(auditBefore + 2, Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries"));
        var audit = Run($"SELECT EntityId, BeforeValue, AfterValue FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected' AND EntityId = N'{s.Full}'")[0].Single();
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T12:00:00", (string)audit[1]!);
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T08:00:00", (string)audit[2]!);
        Assert.Contains("ShiftHours=-4", (string)audit[2]!);
        Assert.Contains("Proof=PROVEN_FULL_OFFSET", (string)audit[2]!);
        var startAudit = Run($"SELECT BeforeValue, AfterValue FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected' AND EntityId = N'{s.StartProven}'")[0].Single();
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T11:40:00", (string)startAudit[0]!);
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T07:40:00", (string)startAudit[1]!);
        Assert.Contains("Proof=PROVEN_START_OFFSET", (string)startAudit[1]!);

        // idempotent: a second real run finds nothing left to correct and writes nothing
        var snapshot = Snapshot(Everything);
        Run(Remediate(dryRun: false));
        Assert.Equal(snapshot, Snapshot(Everything));
        var after = Run(Diagnose());
        Assert.Equal(0, Convert.ToInt32(Assert.Single(after[1])[1]));   // nothing is PROVEN any more
    }

    [VisibilitySqlServerFact]
    public void PilotOnOneTicket_TouchesOnlyThatTicket()
    {
        var s = Seed();
        // a second proven conversation that the pilot must NOT touch
        var other = Ticket(T0.AddHours(4).AddMinutes(1));
        var oi = Conversation(other, "conv-other", T0.AddSeconds(5), T0.AddHours(4), T0.AddHours(4).AddMinutes(10));
        EndReceipt(other, "conv-other", T0.AddMinutes(10).AddSeconds(2));
        Line(oi, 1, T0.AddHours(4).AddMinutes(1));

        Run(Remediate(dryRun: false, ticketId: s.Full));

        Assert.Equal(T0, Scalar<DateTime>($"SELECT InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {s.Full}"));
        Assert.Equal(T0.AddHours(4), Scalar<DateTime>($"SELECT InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {other}"));
        Assert.Equal(T0.AddHours(3).AddMinutes(40), Scalar<DateTime>($"SELECT InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {s.StartProven}"));
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
    public void TheRemediation_HasOptimisticChecks_ValidationAndRollbackOnEveryFailurePath()
    {
        var script = File.ReadAllText(RepoFile("Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql"));
        Assert.Contains("THROW 50310", script);
        Assert.Contains("THROW 50311", script);
        Assert.Contains("THROW 50312", script);
        Assert.Contains("THROW 50320", script);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", script);
        Assert.Contains("SET XACT_ABORT ON", script);
        Assert.Contains("DECLARE @DryRun bit = 1;", script);
    }
}
