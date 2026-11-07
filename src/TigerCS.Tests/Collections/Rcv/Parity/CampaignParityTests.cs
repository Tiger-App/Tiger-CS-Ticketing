using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Rcv.Parity;

/// <summary>
/// PARITY of the Campaigns evaluation over the rebuilt pipeline, against an INDEPENDENT reference of the same rules. The production engine is the application evaluation
/// (CollectionsCampaignAppService.BuildFactsInMemory) over the payment rows <c>usp_CollectionsRcv_GetLines</c> returns (<see cref="RcvReceivablesSource"/>). The reference
/// (<see cref="PublishedLinesOracle"/>) runs the SAME application evaluation over the published lines read with a plain SELECT and filtered in C#, so any difference is a difference in
/// what the read procedure returns (window, exclusions, representative contact row). Both therefore apply the CURRENT rule: Due / Overdue against the current Dubai calendar month
/// (CollectionsDueMonth), stage eligibility up to today, read scope through the end of the month. The previous day-based SQL engine is no longer a reference (it is retired).
/// The fake PACT is loaded with generated customers + the hand-written edge cases (ambiguous same-day payments, contradictory status, conflicting contacts, invalid phones / e-mails,
/// threshold boundaries, former owners, CRM single / ambiguous / conflicting / completing); 90 scenarios (5 stages x 18 filters) plus off-schedule dates and the legal release gates compare
/// every item with every field, the counts Total / Ready / Review and BOTH CSV exports.
/// Runs only when TIGERCS_PERF_SQL holds a connection string to the throw-away database (never in CI). Without it each test reports SKIPPED (see <see cref="SqlFactAttribute"/>), never passed.
/// </summary>
[Collection("RcvRealSql")]
public sealed class CampaignParityTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL");
    private static readonly CollectionsCaller Manager = new(Guid.NewGuid(), [Roles.CsManager], []);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool loaded;

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "collections-receivables"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "database", "collections-receivables");
    }

    private static async Task RunFile(string path, string? database = null)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        if (database is not null) builder.InitialCatalog = database;
        await using var connection = new SqlConnection(builder.ConnectionString); await connection.OpenAsync();
        foreach (var batch in Regex.Split(File.ReadAllText(path), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 300 }; await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task Exec(string sql, string? database = null)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        if (database is not null) builder.InitialCatalog = database;
        await using var connection = new SqlConnection(builder.ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 }; await command.ExecuteNonQueryAsync();
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();

    /// <summary>Loads the fake PACT (generated + edge data) and the rebuilt snapshot, once.</summary>
    private static async Task EnsureLoaded()
    {
        await Gate.WaitAsync();
        try
        {
            if (loaded) return;
            var legacy = Path.Combine(Root(), "legacy", "tests", "local-sqlserver");
            await Exec("UPDATE dbo.FakeConfig SET Tenants = CASE CompanyId WHEN 4 THEN 150 ELSE 100 END, Instalments = 12, DelaySeconds = 0, Shape = CASE WHEN CompanyId = 32 THEN 3 ELSE 1 END;", "PACTRPT");
            await Exec("CREATE OR ALTER PROCEDURE dbo.p32AccountReceivables @StartDate datetime, @EndDate datetime, @MinAmount int AS EXEC dbo.FakeReceivables 32, @StartDate, @EndDate, @MinAmount;", "PACTRPT");
            await RunFile(Path.Combine(legacy, "03_edge_cases.sql"));
            // new pipeline
            await Exec("DELETE FROM dbo.CollectionsRcvLine; DELETE FROM dbo.CollectionsRcvRun; UPDATE dbo.CollectionsRcvCompany SET CurrentRunId = NULL, LastSuccessUtc = NULL, PublishedRows = 0, LastRunId = NULL, LastAttemptUtc = NULL, LastFinishedUtc = NULL, LastStatus = 'Never', LastErrorNumber = NULL, LastError = NULL, ConsecutiveFailures = 0;");
            var result = await new RcvReceivablesRefresher(Config(), new ReceivablesSnapshotOptions(), NullLogger<RcvReceivablesRefresher>.Instance).RefreshAsync("Parity", null, default);
            Assert.Equal("Succeeded", result.Status);
            // CRM owners of the edge units (single / ambiguous / conflicting / completing): the reference and the production source read the same V010 tables.
            await RunFile(Path.Combine(legacy, "05_crm_owner_cases.sql"));
            loaded = true;
        }
        finally { Gate.Release(); }
    }

    private static CollectionsCampaignAppService Service(IPactReceivablesSource source, DateTime nowUtc, bool legalNoticeRelease)
    {
        var options = new CollectionsOptions { Enabled = true };
        var crm = new SqlCollectionsCrmOwnerStore(Config(), new ReceivablesSnapshotOptions());
        return new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true, LegalNoticeExportEnabled = legalNoticeRelease }, new PactReceivablesOptions { Enabled = true },
            new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(nowUtc)), source, NullLogger<CollectionsCampaignAppService>.Instance,
            new CollectionsUnitLinkService(crm, new CollectionsCrmOwnersOptions { Enabled = true, MaxAgeMinutes = 1_000_000 }, new FakeTimeProvider(nowUtc), NullLogger<CollectionsUnitLinkService>.Instance));
    }

    private static RcvReceivablesSource NewSource() =>
        new(Config(), new ReceivablesSnapshotOptions { MaxAgeMinutes = 1_000_000 }, new PactReceivablesOptions { Enabled = true, MaxSourceRows = 1_000_000 }, TimeProvider.System, NullLogger<RcvReceivablesSource>.Instance);

    /// <summary>
    /// The tower id of a tower NUMBER. The same number can exist in both companies (127 = "Tower 127" in Dubai, "Faradis" in Sharjah), so the lookup is deterministic: "127" is the Sharjah tower
    /// (company 32, the one holding the generated Tower-127 payments), "127@4" names the company explicitly.
    /// </summary>
    private static async Task<int?> TowerIdAsync(string number)
    {
        var parts = number.Split('@');
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TOP (1) TowerId FROM dbo.CollectionsTowers WHERE LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) = @n AND IsActive = 1 AND (@c IS NULL OR CompanyId = @c) ORDER BY CompanyId DESC, TowerId", connection);
        command.Parameters.AddWithValue("@n", parts[0]);
        command.Parameters.AddWithValue("@c", parts.Length > 1 ? int.Parse(parts[1]) : DBNull.Value);
        return await command.ExecuteScalarAsync() is int id ? id : null;
    }

    private static (DateOnly Date, DateTime NowUtc) Scheduled(string stage) => stage switch
    {
        "OverdueReminder" => (new(2026, 10, 1), new(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc)),
        "CurrentMonthReminder" => (new(2026, 10, 14), new(2026, 10, 14, 5, 0, 0, DateTimeKind.Utc)),
        "FollowUpReminder" => (new(2026, 10, 28), new(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc)),
        "LegalNotice" => (new(2026, 10, 12), new(2026, 10, 12, 5, 0, 0, DateTimeKind.Utc)),
        _ => (new(2026, 10, 30), new(2026, 10, 30, 5, 0, 0, DateTimeKind.Utc))
    };

    private static readonly string[] Stages = ["OverdueReminder", "CurrentMonthReminder", "FollowUpReminder", "LegalNotice", "LegalReferral"];

    public static IEnumerable<object?[]> Scenarios()
    {
        // stage, tower number, company, from, to, min total, search, release of client-facing legal notices.  A null From is made explicit (2026-01-01, the configured start) for both
        // pipelines so that the READ scope equals the ELIGIBILITY scope; the null-From (All months) behaviour has its own test below.
        foreach (var stage in Stages)
        {
            yield return [stage, null, null, null, null, 100m, null, false];
            yield return [stage, null, null, null, null, 0m, null, true];
            yield return [stage, null, null, null, null, 1000m, null, false];
            yield return [stage, null, 4, null, null, 100m, null, false];
            yield return [stage, null, 32, null, null, 100m, null, false];
            yield return [stage, "103", null, null, null, 100m, null, true];
            yield return [stage, "127", null, null, null, 0m, null, false];
            yield return [stage, "127@4", null, null, null, 0m, null, false];
            yield return [stage, null, null, "2026-03-01", "2026-03-31", 100m, null, false];
            yield return [stage, null, null, "2025-04-09", "2026-10-31", 99.99m, null, false];
            yield return [stage, null, null, null, null, 100m, "EDGE", false];
            yield return [stage, null, null, "2026-01-01", "2026-12-31", 0m, "Customer 12", false];
            yield return [stage, null, null, null, null, 100m, "+9715011100", false];
            yield return [stage, null, null, null, null, 100m, "o_brien", false];
            yield return [stage, null, null, null, null, 100m, "100%", false];
            yield return [stage, null, null, null, null, 100m, "[Test]", false];
            yield return [stage, null, null, null, null, 100m, "عميل", false];
            yield return [stage, null, null, null, null, 100m, "jane roe", false];
            yield return [stage, null, null, null, null, 100m, "TP106-", false];
        }
    }

    private static async Task<(int Total, int Ready, int Review, Dictionary<string, CollectionsCampaignContactDto> Items, List<string> Order)> AllPages(
        CollectionsCampaignAppService service, string stage, DateOnly date, int? company, string? search, DateOnly? from, DateOnly? to, int? towerId, decimal min)
    {
        var items = new Dictionary<string, CollectionsCampaignContactDto>(); var order = new List<string>();
        var first = await service.PreviewAsync(Manager, stage, date, company, search, 1, 100, false, default, from, to, towerId, min);
        Assert.True(first.IsSuccess, first.Detail);
        var pages = Math.Max(1, (first.Value!.TotalCount + 99) / 100);
        for (var page = 1; page <= pages; page++)
        {
            var report = page == 1 ? first : await service.PreviewAsync(Manager, stage, date, company, search, page, 100, false, default, from, to, towerId, min);
            foreach (var item in report.Value!.Items) { Assert.True(items.TryAdd(item.RecordId, item), "a unit appears on two pages: " + item.RecordId); order.Add(item.RecordId); }
        }
        Assert.Equal(first.Value.TotalCount, items.Count);
        return (first.Value.TotalCount, first.Value.ReadyCount, first.Value.ReviewCount, items, order);
    }

    private static string Diff(CollectionsCampaignContactDto a, CollectionsCampaignContactDto b) =>
        string.Join("; ", typeof(CollectionsCampaignContactDto).GetProperties().Where(p => !Equals(p.GetValue(a), p.GetValue(b))).Select(p => $"{p.Name}: reference=[{p.GetValue(a)}] production=[{p.GetValue(b)}]"));

    /// <summary>The export as a set of rows, without the ReadAtUtc column (it is the time of each snapshot's load).</summary>
    private static List<string> Rows(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        var rows = new List<string>();
        while (!parser.EndOfData) { var cells = parser.ReadFields()!.ToList(); cells.RemoveAt(15); rows.Add(string.Join("|", cells)); }
        return [.. rows.Skip(1).Order(StringComparer.Ordinal)];
    }

    [SqlTheory]
    [MemberData(nameof(Scenarios))]
    public async Task ReadProcedure_AndIndependentReference_GiveTheSameUnitsAmountsStatusesReasonsCountsAndExports(
        string stage, string? towerNumber, int? company, string? from, string? to, decimal min, string? search, bool release)
    {
        await EnsureLoaded();
        var (date, nowUtc) = Scheduled(stage);
        var referenceService = Service(new PublishedLinesOracle(ConnectionString!, NewSource()), nowUtc, release);
        var productionService = Service(NewSource(), nowUtc, release);
        int? towerId = towerNumber is null ? null : await TowerIdAsync(towerNumber);
        var f = from is null ? new DateOnly(2026, 1, 1) : DateOnly.Parse(from);
        DateOnly? t = to is null ? null : DateOnly.Parse(to);
        var label = string.Join(" | ", new object?[] { stage, towerNumber, company, from, to, min, search, release }.Select(x => x?.ToString() ?? "-"));

        var a = await AllPages(referenceService, stage, date, company, search, f, t, towerId, min);
        var b = await AllPages(productionService, stage, date, company, search, f, t, towerId, min);
        Assert.True(a.Items.Keys.Order().SequenceEqual(b.Items.Keys.Order()), $"{label}: units only in reference: [{string.Join(",", a.Items.Where(x => !b.Items.ContainsKey(x.Key)).Select(x => x.Value.UnitCode))}] only in production: [{string.Join(",", b.Items.Where(x => !a.Items.ContainsKey(x.Key)).Select(x => x.Value.UnitCode))}]");   // the same units
        Assert.Equal((a.Total, a.Ready, a.Review), (b.Total, b.Ready, b.Review));                    // the counts
        foreach (var (id, expected) in a.Items)
            Assert.True(expected == b.Items[id], $"{label}: unit {expected.CompanyId}:{expected.UnitCode} differs in {Diff(expected, b.Items[id])}");   // every field: stage amount, earliest due, status, review reasons, contact, Due / Overdue
        // The only intended difference: the order. New = newest payment first, then company / customer / unit code / unit id.
        var due = b.Items.Values.ToDictionary(i => i.RecordId, i => i.DueAmount + i.OverdueAmount);
        Assert.Equal(b.Order.Count, b.Order.Distinct().Count());

        foreach (var mode in new[] { "review", "genesys" })
        {
            var x = await referenceService.ExportAsync(Manager, stage, mode, date, company, search, default, f, t, towerId, min);
            var y = await productionService.ExportAsync(Manager, stage, mode, date, company, search, default, f, t, towerId, min);
            Assert.Equal((x.IsSuccess, x.Outcome, x.Detail), (y.IsSuccess, y.Outcome, y.Detail));    // refused identically (row limit / not every row ready) ...
            if (x.IsSuccess)
            {
                Assert.Equal(x.Value!.RowCount, y.Value!.RowCount);
                Assert.Equal(x.Value.FileName, y.Value.FileName);
                Assert.Equal(Rows(x.Value.Csv), Rows(y.Value.Csv));                                       // ... or the same rows
            }
        }
    }

    [SqlFact]
    public async Task OffScheduleDatesAndTheLegalReleaseGates_AlsoGiveTheSameResults()
    {
        await EnsureLoaded();
        var reasons = new HashSet<string>(); var statuses = new HashSet<string>();
        foreach (var stage in Stages)
            foreach (var release in new[] { false, true })
            {
                var (scheduled, nowUtc) = Scheduled(stage);
                var preview = scheduled.AddDays(1);                                                       // not a scheduled day of the stage: PreviewOnly / OutsideSchedule
                var a = await AllPages(Service(new PublishedLinesOracle(ConnectionString!, NewSource()), nowUtc, release), stage, preview, null, null, new DateOnly(2026, 1, 1), null, null, 0m);
                var b = await AllPages(Service(NewSource(), nowUtc, release), stage, preview, null, null, new DateOnly(2026, 1, 1), null, null, 0m);
                Assert.Equal((a.Total, a.Ready, a.Review), (b.Total, b.Ready, b.Review));
                Assert.True(a.Items.Count == b.Items.Count && a.Items.All(x => b.Items.TryGetValue(x.Key, out var y) && x.Value == y), $"{stage} release={release}");
                foreach (var item in b.Items.Values) { statuses.Add(item.Status); foreach (var r in item.Reason.Split(';')) reasons.Add(r); }
            }
        Assert.Contains("OutsideSchedule", reasons); Assert.Contains("PreviewOnly", statuses);
    }

    [SqlFact]
    public async Task TheParityScenarios_ReallyExerciseTheRules_StagesStatusesAndReviewReasons()
    {
        await EnsureLoaded();
        var reasons = new HashSet<string>(); var statuses = new HashSet<string>(); var stagesWithUnits = 0; var withAmount = 0; var withoutAmount = 0; var withDue = 0;
        foreach (var (stage, release) in Stages.SelectMany(st => new[] { (st, true), (st, false) }))
        {
            var (date, nowUtc) = Scheduled(stage);
            var r = await AllPages(Service(NewSource(), nowUtc, release), stage, date, null, null, new DateOnly(2026, 1, 1), null, null, 0m);
            if (r.Items.Count > 0) stagesWithUnits++;
            foreach (var item in r.Items.Values)
            {
                statuses.Add(item.Status);
                foreach (var reason in item.Reason.Split(';')) reasons.Add(reason);
                if (item.Amount is null) withoutAmount++; else withAmount++;
                if (item.DueAmount > 0) withDue++;
            }
        }
        Assert.Equal(10, stagesWithUnits);                                                              // every stage has candidates, with and without the legal-notice release
        Assert.Contains("LegalNoticeReleaseRequired", reasons); Assert.Contains("InternalLegalReferralOnly", reasons); Assert.Contains("InternalReview", statuses);
        foreach (var expected in new[] { "Qualifies", "AmbiguousInstalments", "ConflictingContactDetails", "UnitAllocationNeedsReview", "ContradictoryPaymentStatus", "NoValidContact",
                                         "CrmCustomerAmbiguous" })
            Assert.Contains(expected, reasons);                                                         // the review reasons the edge data is built to trigger
        Assert.Contains("Ready", statuses); Assert.Contains("NeedsReview", statuses);
        Assert.True(withAmount > 20 && withoutAmount > 0 && withDue > 0, $"amounts {withAmount}/{withoutAmount}, due {withDue}");   // priced units, ambiguous (no amount) units, units due today
    }

    [SqlFact]
    public async Task TheOrder_IsNewestTowerFirst_ThenTheUnit_ThenCompanyCustomer_AndPagesNeverOverlap()
    {
        await EnsureLoaded();
        var (date, nowUtc) = Scheduled("OverdueReminder");
        var service = Service(NewSource(), nowUtc, false);
        var result = await AllPages(service, "OverdueReminder", date, null, null, new DateOnly(2026, 1, 1), null, null, 0m);
        var sequence = result.Order.Select(id => result.Items[id]).ToList();
        Assert.True(sequence.Count > 100, "needs more than one page");
        // Newest TOWER first (the tower number as an integer, descending), then the unit inside the tower, then company / customer / unit id: a strict total order, so no ties and pages never overlap or skip.
        for (var n = 1; n < sequence.Count; n++)
        {
            var (p, q) = (sequence[n - 1], sequence[n]);
            Assert.True(CollectionsTowerOrder.Compare(CollectionsTowerNumber.Parse(p.UnitCode), p.UnitCode, p.CompanyId, p.TenantId, p.UnitId ?? 0,
                CollectionsTowerNumber.Parse(q.UnitCode), q.UnitCode, q.CompanyId, q.TenantId, q.UnitId ?? 0) < 0, $"order broken at {n}: {p.UnitCode} (company {p.CompanyId}) then {q.UnitCode} (company {q.CompanyId})");
        }
        Assert.Equal(sequence.Count, sequence.Select(i => i.RecordId).Distinct().Count());
    }

    [SqlTheory]
    [InlineData("OverdueReminder")]
    [InlineData("CurrentMonthReminder")]
    [InlineData("FollowUpReminder")]
    [InlineData("LegalNotice")]
    [InlineData("LegalReferral")]
    public async Task AllMonths_ChangesOnlyTheShownTotals_NeverTheStagePolicy(string stage)
    {
        await EnsureLoaded();
        var (date, nowUtc) = Scheduled(stage);
        var referenceService = Service(new PublishedLinesOracle(ConnectionString!, NewSource()), nowUtc, false);
        var productionService = Service(NewSource(), nowUtc, false);
        var start = new DateOnly(2026, 1, 1);
        var a = await AllPages(referenceService, stage, date, null, null, start, null, null, 0m);               // the old behaviour: read scope = configured start
        var b = await AllPages(productionService, stage, date, null, null, null, null, null, 0m);                // the new "All months": read scope = whole history, eligibility = configured start
        Assert.Equal(a.Items.Keys.Order(), b.Items.Keys.Order());                                          // the same units are eligible
        Assert.Equal((a.Total, a.Ready, a.Review), (b.Total, b.Ready, b.Review));
        // Only Due / Overdue (and so Total) may differ, and exactly by the unit's payments before the configured start date.
        var before = new Dictionary<(int, string, int, string), decimal>();
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT l.CompanyId, l.TenantId, l.UnitId, l.UnitCode, SUM(l.Amount) FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId AND c.CurrentRunId = l.RunId " +
            "WHERE l.DueDate < '20260101' GROUP BY l.CompanyId, l.TenantId, l.UnitId, l.UnitCode", connection);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) before[(reader.GetInt32(0), reader.GetString(1).Trim(), (int)reader.GetInt64(2), reader.GetString(3).Trim())] = reader.GetDecimal(4);
        var widened = 0;
        foreach (var (id, expected) in a.Items)
        {
            var actual = b.Items[id];
            Assert.Equal(expected with { DueAmount = 0, OverdueAmount = 0 }, actual with { DueAmount = 0, OverdueAmount = 0 });   // amount, earliest due, status, reasons, contact: identical
            var extra = before.GetValueOrDefault((expected.CompanyId, expected.TenantId.Trim(), expected.UnitId ?? 0, expected.UnitCode.Trim()));
            Assert.Equal(expected.DueAmount, actual.DueAmount);
            Assert.Equal(expected.OverdueAmount + extra, actual.OverdueAmount);
            if (extra > 0) widened++;
        }
        Assert.True(widened > 0 || stage is "LegalNotice" or "LegalReferral" or "CurrentMonthReminder" or "FollowUpReminder", "the generated 2025 instalments should widen at least some Overdue totals in the overdue stage");
    }
}
