using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Campaign preview/export over the local snapshot: tower filter, freshness gate, export == preview.</summary>
public sealed class CampaignSnapshotTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);   // 14 Oct 2026: current-month day

    private sealed class Harness
    {
        public FakeSnapshotSource Source { get; } = new(Now);
        public CollectionsCampaignAppService Service { get; }
        public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);

        public Harness()
        {
            var options = new CollectionsOptions { Enabled = true };
            Service = new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m },
                new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(Now)), Source, NullLogger<CollectionsCampaignAppService>.Instance, TigerCS.Tests.Collections.Review.MirrorCrmBuild.Links(() => Source.Rows, Now));
            Source.Towers.AddRange([new(1, "124", "Tower 124", 4, true), new(3, "127", "Faradis", 32, true)]);
        }
    }

    private static PactReceivableInstalment Row(string tenant, string unit, int unitId, int company = 4, int day = 10, decimal amount = 500m) =>
        new(company, tenant, "Example Customer " + tenant, "971500003001", "x@example.test", unitId, unit, "", "INV-" + tenant, "", new DateTime(2026, 10, day), amount, "Installment");

    private static void Seed(Harness h)
    {
        for (var n = 1; n <= 30; n++)
            h.Source.Rows.Add(Row("d" + n, "TP124-" + (1000 + n), n));
        for (var n = 1; n <= 10; n++)
            h.Source.Rows.Add(Row("s" + n, "TP127-" + (2000 + n), 100 + n, company: 32));
    }

    [Fact]
    public async Task TowerAndWindowAreForwarded_AndTheCompanyComesFromTheTower()
    {
        var h = new Harness(); Seed(h);
        var report = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: 3, dateFrom: new DateOnly(2026, 10, 1), dateTo: new DateOnly(2026, 10, 31))).Value!;
        Assert.Equal(10, report.TotalCount);
        Assert.All(report.Items, c => { Assert.Equal(32, c.CompanyId); Assert.Equal("127", c.TowerNumber); Assert.Equal("Faradis", c.TowerName); });
        Assert.Equal(3, h.Source.LastRequest!.TowerId);
        Assert.Equal(3, report.TowerId);
    }

    [Fact]
    public async Task EachUnitShowsDueOverdueAndTotal_OfItsOwnInstalments_ThroughTheEndOfTheCurrentMonth()
    {
        var h = new Harness();
        // Same customer, two apartments, today = 14 Oct. Apartment 1: 5, 14 and 20 Oct (all inside the current month: Due, even the coming 20th) and 15 Dec (a later month: ignored).
        // Apartment 2: 1 Oct (the first day of the month: Due) only. (Overdue = before the first of the month is covered by DueMonthRuleTests.)
        h.Source.Rows.AddRange([Row("c1", "TP124-1", 1, day: 14, amount: 500m), Row("c1", "TP124-1", 1, day: 5, amount: 300m), Row("c1", "TP124-1", 1, day: 20, amount: 700m),
            new(4, "c1", "Example Customer c1", "971500003001", "x@example.test", 1, "TP124-1", "", "INV-late", "", new DateTime(2026, 12, 15), 900m, "Installment"),
            Row("c1", "TP124-2", 2, day: 1, amount: 250m)]);
        var items = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14), minTotal: null)).Value!.Items;
        var one = items.Single(i => i.UnitCode == "TP124-1");
        Assert.Equal(1500m, one.Amount);                                                                 // the stage amount: 5, 14 and 20 Oct (the stage window is the whole month); the December 900 is in neither
        Assert.Equal((1500m, 0m, 1500m), (one.DueAmount, one.OverdueAmount, one.TotalAmount));      // Total = Due + Overdue = 500 + 300 + 700; the December 900 is in neither
        var two = items.Single(i => i.UnitCode == "TP124-2");
        Assert.Equal((250m, 0m, 250m), (two.DueAmount, two.OverdueAmount, two.TotalAmount));        // another apartment of the same customer is never added in
    }

    [Fact]
    public async Task UnknownTowerIsRejected_NotTreatedAsNoMatches()
    {
        var h = new Harness(); Seed(h);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: 77)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: -1)).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ExportContainsExactlyThePreviewRows_ForTheSameFilters(int? towerId)
    {
        var h = new Harness(); Seed(h);
        var from = new DateOnly(2026, 1, 1); var to = new DateOnly(2026, 10, 31);
        var seen = new List<string>();
        var total = 0;
        for (var page = 1; ; page++)
        {
            var preview = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", null, null, null, page, 25, towerId: towerId, dateFrom: from, dateTo: to)).Value!;
            total = preview.TotalCount; seen.AddRange(preview.Items.Select(c => c.RecordId));
            if (page * 25 >= total) break;
        }
        var file = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys", towerId: towerId, dateFrom: from, dateTo: to)).Value!;
        Assert.Equal(total, file.RowCount);
        using var parser = new TextFieldParser(new StringReader(file.Csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        parser.ReadFields();
        var exported = new List<string>();
        while (!parser.EndOfData) exported.Add(parser.ReadFields()![0]);
        Assert.Equal(seen.Order(), exported.Order());
        Assert.Equal(towerId switch { 1 => 30, 3 => 10, _ => 40 }, total);
    }

    [Fact]
    public async Task StaleSnapshot_MarksRowsForReview_AndBlocksBothExportModes()
    {
        var h = new Harness(); Seed(h);
        h.Source.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-91));
        var report = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: 1)).Value!;
        Assert.All(report.Items, c => { Assert.Equal("NeedsReview", c.Status); Assert.Contains("StaleSource", c.Reason); });
        Assert.False(report.Snapshot!.IsFresh);
        foreach (var mode in new[] { "review", "genesys" })
        {
            var export = await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", mode, towerId: 1);
            Assert.Equal(CollectionsOutcome.InvalidRequest, export.Outcome);
            Assert.Contains("not ready to export", export.Detail);
        }
    }

    [Fact]
    public async Task StaleSnapshot_KeepsTheCustomersFinanciallyEligible_ButNoneReadyToSend()
    {
        // "Eligible" (the stage rule found an amount) and "Ready" (also passes every send check) are different counts by design: stale data keeps the first and zeroes the second.
        var h = new Harness(); Seed(h);
        h.Source.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-91));
        var report = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: 1)).Value!;
        var eligible = report.TotalCount - report.NotEligibleCount;
        Assert.True(eligible > 0);
        Assert.Equal(0, report.ReadyCount);
        Assert.Equal(eligible, report.ReviewCount);                    // every eligible customer is "Needs review / blocked", none silently dropped
        Assert.All(report.Items, c => Assert.Contains("StaleSource", c.Reason));
    }

    [Fact]
    public async Task ExportIsAllowedExactlyAtTheAgeLimit_AndRefusedOneMinuteLater()
    {
        var h = new Harness(); Seed(h);
        h.Source.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-90));
        Assert.True((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys", towerId: 1)).IsSuccess);
        h.Source.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-91));
        Assert.False((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys", towerId: 1)).IsSuccess);
    }

    [Fact]
    public async Task PartialRefreshFailure_BlocksAllTowersExport_ButNotTheHealthyCompanysTower()
    {
        var h = new Harness(); Seed(h);
        h.Source.Companies[32] = FakeSnapshotSource.Failed(32, Now.AddHours(-5), Now.AddMinutes(-2));
        var all = await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review");
        Assert.Equal(CollectionsOutcome.InvalidRequest, all.Outcome);
        Assert.Contains("Sharjah", all.Detail);
        Assert.True((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys", towerId: 1)).IsSuccess);
        Assert.False((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys", towerId: 3)).IsSuccess);
    }

    [Fact]
    public async Task ACompanyThatWasNeverLoaded_BlocksTheAllTowersExport_AndNeverLooksLikeNoReceivables()
    {
        var h = new Harness(); Seed(h);
        h.Source.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        var preview = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!;
        Assert.Equal("Missing", preview.Snapshot!.Companies.Single(c => c.CompanyId == 32).Freshness);
        var export = await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review");
        Assert.Contains("has not been loaded", export.Detail);
        // Selecting the tower of the missing company: nothing was ever loaded for it -> an explicit not-loaded state (so the page can offer "Load data"),
        // never a fresh-looking empty list, and never exportable.
        var missing = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: 3)).Value!;
        Assert.Equal(0, missing.TotalCount);
        Assert.True(missing.Snapshot!.NothingLoaded);
        Assert.False(missing.Snapshot.IsReady);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review", towerId: 3)).Outcome);
    }

    [Fact]
    public async Task StageRulesAreUnchanged_AFutureInstalmentIsNeverOverdue_AndTheWindowOnlyNarrows()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Row("old", "TP124-1", 1, day: 20) with { DueDate = new DateTime(2026, 8, 1) }, Row("future", "TP124-2", 2, day: 20)]);
        var overdue = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", dateTo: new DateOnly(2026, 12, 31))).Value!;
        Assert.Equal("old", Assert.Single(overdue.Items, i => i.Status != "NotEligible").TenantId);          // due 20 Oct is not overdue on 14 Oct
        var narrowed = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", dateFrom: new DateOnly(2026, 9, 1))).Value!;
        Assert.Equal(0, narrowed.ReadyCount + narrowed.ReviewCount);       // From only removes instalments, it never adds eligibility
        Assert.All(narrowed.Items, i => Assert.Equal("NotEligible", i.Status));
    }
}
