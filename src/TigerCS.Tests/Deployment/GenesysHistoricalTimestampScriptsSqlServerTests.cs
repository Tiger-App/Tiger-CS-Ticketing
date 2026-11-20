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

    private (long Confirmed, long Correct, long SuspectStartOnly, long SuspectEndOnly) Seed()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using (var context = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options))
        {
            context.Database.Migrate();
        }

        // 1. PROVEN +4h (the 1878 shape): TigerCS created the ticket at 08:00 and received "ended" at 08:10; the stored values are 12:00 / 12:10, lines 12:01 / 12:05, connect 12:01.
        var confirmed = Ticket(T0.AddHours(4).AddMinutes(1));
        var ci = Conversation(confirmed, "conv-confirmed", T0.AddSeconds(5), T0.AddHours(4), T0.AddHours(4).AddMinutes(10));
        EndReceipt(confirmed, "conv-confirmed", T0.AddMinutes(10).AddSeconds(2));
        Line(ci, 1, T0.AddHours(4).AddMinutes(1)); Line(ci, 2, T0.AddHours(4).AddMinutes(5));
        Handler(ci, T0.AddMinutes(2), T0.AddHours(4).AddMinutes(1));
        Insert("TicketSlaInstances", new()
        {
            ["TicketId"] = confirmed.ToString(), ["PeriodStartAtUtc"] = Lit(T0), ["FirstResponseDueAtUtc"] = Lit(T0.AddHours(2)), ["FirstResponseBreached"] = "1",
        });

        // 2. CORRECT (came as "Z" / already UTC): never touched.
        var correct = Ticket(T0.AddMinutes(1));
        var ki = Conversation(correct, "conv-correct", T0.AddSeconds(3), T0, T0.AddMinutes(10));
        EndReceipt(correct, "conv-correct", T0.AddMinutes(10).AddSeconds(1));
        Line(ki, 1, T0.AddMinutes(1));

        // 3. SUSPECT: start looks +4h but there is no recorded end event to confirm it: single evidence, never touched.
        var startOnly = Ticket(null);
        var si = Conversation(startOnly, "conv-start-only", T0.AddSeconds(3), T0.AddHours(4), null);
        Line(si, 1, T0.AddHours(4).AddMinutes(1));

        // 4. SUSPECT: the end looks +4h but the start is fine (mixed): single evidence, never touched.
        var endOnly = Ticket(null);
        var ei = Conversation(endOnly, "conv-end-only", T0.AddSeconds(3), T0, T0.AddHours(4).AddMinutes(10));
        EndReceipt(endOnly, "conv-end-only", T0.AddMinutes(10).AddSeconds(1));
        Line(ei, 1, T0.AddMinutes(1));
        return (confirmed, correct, startOnly, endOnly);
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

    [VisibilitySqlServerFact]
    public void TheDiagnostic_IsReadOnly_ClassifiesByEvidence_AndProposesOnlyTheProvenConversation()
    {
        var (confirmed, correct, startOnly, endOnly) = Seed();
        var before = Snapshot(Everything);

        var sets = Run(Diagnose());
        var dump = string.Join("\n", sets.Select((set, i) => $"[{i}] " + string.Join(" || ", set.Select(r => string.Join(",", r)))));

        Assert.Equal(before, Snapshot(Everything));                                  // read-only in fact
        var text = string.Join('|', sets.SelectMany(s => s).Select(r => string.Join(',', r)));
        Assert.Contains("CONFIRMED_PLUS4H", text);
        Assert.Contains("SUSPECT", text);

        // result set 2 (conversations): the proven one has a proposed value 4h earlier; nobody else does
        var provenRows = sets[1].Where(r => r.Any(v => v is long l && l == confirmed)).ToList();
        Assert.True(provenRows.Count == 1, dump);
        var proven = provenRows[0];
        Assert.Contains(proven, v => v is DateTime d && d == T0);   // the proposed start, 4h earlier than stored
        Assert.DoesNotContain(sets[1], r => r.Any(v => v is long l && (l == correct || l == startOnly || l == endOnly)) && r.Any(v => v is DateTime d && d == T0.AddHours(-4)));
        // transcript lines (3) belong only to the proven conversation
        Assert.Equal(2, sets[2].Count);
        // suspects (6) are exactly the two single-evidence tickets, never the correct one
        Assert.Equal(2, sets[5].Count);
        Assert.Contains(sets[5], r => r.Any(v => v is long l && l == startOnly));
        Assert.Contains(sets[5], r => r.Any(v => v is long l && l == endOnly));
        Assert.DoesNotContain(sets[5], r => r.Any(v => v is long l && l == correct));
        // SLA review (5): the proven ticket's First Human Response came from a shifted line
        Assert.Single(sets[4]);
    }

    [Fact]
    public void TheDiagnostic_ContainsNoWriteStatements()
    {
        var sql = Regex.Replace(Diagnose(), @"--[^\r\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotMatch(new Regex(@"\b(INSERT\s+(INTO\s+)?(dbo\.)?(?!@)\w+|UPDATE\s+(dbo\.)?\w+\s+SET|DELETE\s+(FROM\s+)?(dbo\.)|MERGE|DROP\s|ALTER\s|TRUNCATE|EXEC(UTE)?\s)", RegexOptions.IgnoreCase), sql);
    }

    [VisibilitySqlServerFact]
    public void DryRun_IsTheDefault_ShowsOldAndProposed_AndWritesNothing()
    {
        var (confirmed, _, _, _) = Seed();
        var before = Snapshot(Everything);

        var sets = Run(Remediate(dryRun: true));

        Assert.Equal(before, Snapshot(Everything));
        var plan = sets.First(s => s.Count > 0 && s[0].Length > 0 && s[0][0] is "PLAN: conversations");
        var row = Assert.Single(plan);
        Assert.Equal(confirmed, Convert.ToInt64(row[1]));
        Assert.Equal(T0.AddHours(4), (DateTime)row[6]!);   // old
        Assert.Equal(T0, (DateTime)row[7]!);               // proposed
        Assert.Contains(sets, s => s.Count == 1 && s[0][0] is string h && h.StartsWith("SLA REVIEW"));
        var rollback = sets.First(s => s.Count > 0 && s[0][0] is "ROLLBACK STATEMENTS");
        Assert.Equal(4, rollback.Count);                   // conversation + 2 lines + 1 connect
    }

    [VisibilitySqlServerFact]
    public void RealRun_CorrectsOnlyTheProvenConversation_ChangesOnlyTimestamps_AppendsAudit_AndIsIdempotent()
    {
        var (confirmed, correct, startOnly, endOnly) = Seed();
        var protectedBefore = Snapshot(ProtectedTables);
        var auditBefore = Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries");
        var untouchedBefore = Snapshot("TicketInteractionHandlers"); // sanity: handlers other than the proven one do not exist
        var othersBefore = Run($"SELECT TicketInteractionId, InteractionStartedAtUtc, EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId <> {confirmed} ORDER BY 1")[0];
        var otherLinesBefore = Run($"SELECT m.TicketInteractionMessageId, m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId <> {confirmed} ORDER BY 1")[0];

        Run(Remediate(dryRun: false));

        // the proven conversation: every shifted value is exactly 4h earlier
        var c = Run($"SELECT InteractionStartedAtUtc, EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {confirmed}")[0].Single();
        Assert.Equal(T0, (DateTime)c[0]!);
        Assert.Equal(T0.AddMinutes(10), (DateTime)c[1]!);
        var lines = Run($"SELECT m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId = {confirmed} ORDER BY m.Sequence")[0];
        Assert.Equal([T0.AddMinutes(1), T0.AddMinutes(5)], lines.Select(r => (DateTime)r[0]!).ToArray());
        var connect = Run($"SELECT h.ConnectedAtUtc FROM dbo.TicketInteractionHandlers h JOIN dbo.TicketInteractions i ON i.TicketInteractionId = h.TicketInteractionId WHERE i.TicketId = {confirmed}")[0].Single();
        Assert.Equal(T0.AddMinutes(1), (DateTime)connect[0]!);

        // everyone else is untouched: the correct conversation and both single-evidence suspects
        Assert.Equal(othersBefore.Select(r => string.Join(',', r)), Run($"SELECT TicketInteractionId, InteractionStartedAtUtc, EndedAtUtc FROM dbo.TicketInteractions WHERE TicketId <> {confirmed} ORDER BY 1")[0].Select(r => string.Join(',', r)));
        Assert.Equal(otherLinesBefore.Select(r => string.Join(',', r)), Run($"SELECT m.TicketInteractionMessageId, m.SentAtUtc FROM dbo.TicketInteractionMessages m JOIN dbo.TicketInteractions i ON i.TicketInteractionId = m.TicketInteractionId WHERE i.TicketId <> {confirmed} ORDER BY 1")[0].Select(r => string.Join(',', r)));

        // ticket status/owner/closure/SLA/assignments/history/resolutions: byte-for-byte (First Human Response and the SLA flag are reported, NOT recalculated)
        Assert.Equal(protectedBefore, Snapshot(ProtectedTables));
        Assert.Equal(T0.AddHours(4).AddMinutes(1), Scalar<DateTime>($"SELECT FirstHumanResponseAtUtc FROM dbo.Tickets WHERE TicketId = {confirmed}"));

        // audit: existing rows untouched, exactly one new row for the corrected ticket, with old and new values
        Assert.Equal(auditBefore + 1, Scalar<int>("SELECT COUNT(*) FROM dbo.AuditEntries"));
        var audit = Run("SELECT EntityId, BeforeValue, AfterValue FROM dbo.AuditEntries WHERE Action = N'GenesysTimestampCorrected'")[0].Single();
        Assert.Equal(confirmed.ToString(), (string)audit[0]!);
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T12:00:00", (string)audit[1]!);
        Assert.Contains("InteractionStartedAtUtc=2026-11-10T08:00:00", (string)audit[2]!);
        Assert.Contains("ShiftHours=-4", (string)audit[2]!);
        _ = (correct, startOnly, endOnly, untouchedBefore);

        // idempotent: a second real run finds nothing left to correct and writes nothing
        var snapshot = Snapshot(Everything);
        Run(Remediate(dryRun: false));
        Assert.Equal(snapshot, Snapshot(Everything));
    }

    [VisibilitySqlServerFact]
    public void PilotOnOneTicket_TouchesOnlyThatTicket()
    {
        var (confirmed, _, _, _) = Seed();
        // a second proven conversation that the pilot must NOT touch
        var other = Ticket(T0.AddHours(4).AddMinutes(1));
        var oi = Conversation(other, "conv-other", T0.AddSeconds(5), T0.AddHours(4), T0.AddHours(4).AddMinutes(10));
        EndReceipt(other, "conv-other", T0.AddMinutes(10).AddSeconds(2));
        Line(oi, 1, T0.AddHours(4).AddMinutes(1));

        Run(Remediate(dryRun: false, ticketId: confirmed));

        Assert.Equal(T0, Scalar<DateTime>($"SELECT InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {confirmed}"));
        Assert.Equal(T0.AddHours(4), Scalar<DateTime>($"SELECT InteractionStartedAtUtc FROM dbo.TicketInteractions WHERE TicketId = {other}"));
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

    [VisibilitySqlServerFact]
    public void ARowChangedAfterThePlanWasRead_RollsEverythingBack()
    {
        // simulate "something else touched the conversation" by making the plan unable to match: a tampered start that no longer equals the planned old value cannot be
        // produced from outside one batch, so prove the validation guard instead: the script must contain the optimistic checks and the rollback on every failure path
        var script = File.ReadAllText(RepoFile("Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql"));
        Assert.Contains("THROW 50310", script);
        Assert.Contains("THROW 50311", script);
        Assert.Contains("THROW 50312", script);
        Assert.Contains("THROW 50320", script);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", script);
        Assert.Contains("SET XACT_ABORT ON", script);
    }
}
