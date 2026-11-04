using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Services;

public sealed class CollectionsCampaignAppServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    internal sealed class Source : IPactReceivablesSource
    {
        public List<PactReceivableInstalment> Items { get; } = [];
        private int _reads;
        public int Reads => _reads;
        public DateTime ReadAt { get; set; } = Now;
        public Exception? Failure { get; set; }
        public PactReceivablesRequest? LastRequest { get; private set; }
        /// <summary>Every request, in order.</summary>
        public List<PactReceivablesRequest> Requests { get; } = [];
        /// <summary>When set, the source behaves like the local snapshot: it honours MinAmount and reports this status (freshness / coverage).</summary>
        public TigerCS.Application.Modules.Collections.Dto.SnapshotStatusDto? Snapshot { get; set; }
        public bool HonourMinAmount { get; set; }
        public TimeSpan Delay { get; set; }
        /// <summary>Runs at the start of every read (before the delay), e.g. to advance a clock or touch the database while a read is in flight.</summary>
        public Func<CancellationToken, Task>? OnRead { get; set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
            ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);

        public async Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads); LastRequest = request; lock (Requests) Requests.Add(request);
            if (OnRead is not null) await OnRead(cancellationToken);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            if (Failure is not null) throw Failure;
            // Mimic the SQL source: only the requested window and company are returned.
            var rows = Items.Where(r => DateOnly.FromDateTime(r.DueDate) >= (request.FromDate ?? DateOnly.MinValue)
                && DateOnly.FromDateTime(r.DueDate) <= request.ThroughDate
                && (request.CompanyId is null || r.CompanyId == request.CompanyId)
                && (request.TowerId is null || r.TowerId == request.TowerId)
                && (!HonourMinAmount || (r.Amount > 0 && r.Amount >= request.MinAmount))).ToList();
            return new PactReceivablesSnapshot(rows, ReadAt, false, Snapshot);
        }
    }

    internal sealed class Harness
    {
        public Source Source { get; } = new();
        public CollectionsOptions Options { get; } = new() { Enabled = true };
        public PactReceivablesOptions Sql { get; } = new() { Enabled = true, DefaultMinOutstandingAmount = 0m };
        public CollectionsCampaignOptions Campaign { get; } = new();
        public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);
        public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);
        public CollectionsCampaignAppService Service { get; }
        /// <summary>The clock is today (Dubai): nothing due after it is ever listed. <paramref name="now"/> moves it (e.g. to the preview date of a later cycle).</summary>
        public Harness(DateTime? now = null) => Service = new(Options, Campaign, Sql,
            new(Options, new FakeDepartmentRepository()), new(Options, new FakeTimeProvider(now ?? Now)),
            Source, NullLogger<CollectionsCampaignAppService>.Instance);
    }

    internal static PactReceivableInstalment Row(int day = 10, decimal amount = 500m, string tenant = "3001",
        int company = 4, int unit = 101) => new(company, tenant, "Example Customer", "971500003001", "example@example.test",
            unit, $"TP140-{unit}", "TP140", "INV-1", "", new(2026, 10, day), amount, "Installment");

    [Fact]
    public async Task SeveralTowers_FilterCustomersCountsAndTotals_AndEmptyOrSingleKeepTheEstablishedPaths()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([
            Row(amount: 100m, tenant: "1", unit: 101) with { TowerId = 1, TowerNumber = "140" },
            Row(amount: 200m, tenant: "2", unit: 102) with { TowerId = 2, TowerNumber = "139" },
            Row(amount: 400m, tenant: "3", unit: 103) with { TowerId = 3, TowerNumber = "138" }]);

        var two = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerIds: [2, 1])).Value!;
        Assert.Equal(2, two.TotalCount);
        Assert.Equal(new[] { "1", "2" }, two.Items.Select(c => c.TenantId).Order().ToArray());
        Assert.Equal(300m, two.Items.Sum(c => c.TotalAmount));
        Assert.Equal(new[] { 1, 2 }, two.TowerIds!.ToArray());
        Assert.Equal(new[] { 1, 2 }, h.Source.Requests.Select(r => r.TowerId!.Value).Order().ToArray());   // one read per tower: the same evaluation as a single tower

        var all = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerIds: [])).Value!;   // empty selection = All projects
        Assert.Equal(3, all.TotalCount); Assert.Equal(700m, all.Items.Sum(c => c.TotalAmount)); Assert.Null(all.TowerIds);

        var one = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerIds: [3])).Value!;
        Assert.Equal(new[] { "3" }, one.Items.Select(c => c.TenantId).ToArray()); Assert.Equal(3, one.TowerId);

        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", towerIds: [1, 0])).Outcome);
    }

    [Fact]
    public async Task DefaultIsReviewOnly_GenesysExportRequiresReconciledSource()
    {
        var h = new Harness(); h.Source.Items.Add(Row());
        var report = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!;
        var row = Assert.Single(report.Items);
        Assert.Equal("NeedsReview", row.Status);
        Assert.Contains("SourceReconciliationRequired", row.Reason);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);
        var review = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Value!;
        Assert.Contains("InternalReviewOnly", review.Csv);
        Assert.Contains("SourceReconciliationRequired", review.Csv);
    }

    [Fact]
    public async Task ExportReReadsAllPages_AndAlreadyPaidUnitDisappears()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        for (var n = 1; n <= 30; n++) h.Source.Items.Add(Row(tenant: n.ToString()));
        var preview = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!;
        Assert.Equal(25, preview.Items.Count); Assert.Equal(30, preview.TotalCount);
        var stableId = preview.Items.First(c => c.TenantId == "10").RecordId;
        h.Source.Items.RemoveAll(r => r.TenantId == "1");
        var file = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Value!;
        Assert.Equal(29, file.RowCount); Assert.Equal(2, h.Source.Reads);
        Assert.Contains(stableId, file.Csv);
        using var parser = new TextFieldParser(new StringReader(file.Csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        Assert.Equal(22, parser.ReadFields()!.Length);
        var tenants = new List<string>();
        while (!parser.EndOfData) { var cells = parser.ReadFields()!; tenants.Add(cells[3]); Assert.Equal("+971500003001", cells[5]); Assert.Equal(new[] { "true", "true", "true" }, cells[19..]); }
        Assert.Equal(29, tenants.Count); Assert.DoesNotContain("1", tenants);
    }

    [Fact]
    public async Task UnitsAndCompaniesAreIndependent_IdsAreStableWithinTheMonth()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([Row(amount: 100m), Row(day: 12, amount: 200m, unit: 202), Row(amount: 300m, company: 32)]);
        var items = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items;
        Assert.Equal(3, items.Count); Assert.Equal(3, items.Select(c => c.RecordId).Distinct().Count());
        Assert.Equal(new decimal?[] { 100m, 300m, 200m }, items.Select(c => c.Amount));   // one tower: its units by unit number (101 in company 4, 101 in company 32, 202), never by payment date
        var again = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new(2026, 10, 15))).Value!.Items;
        Assert.Equal(items.Select(c => c.RecordId), again.Select(c => c.RecordId));
        Assert.All(again, c => Assert.Equal("PreviewOnly", c.Status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidUnitCannotEnterAGenesysCampaign(int unit)
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true; h.Source.Items.Add(Row(unit: unit));
        // A unit without a real number is not listed at all (excluded before grouping), so it can never reach any export.
        Assert.Empty((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);   // nothing to send
    }

    [Fact]
    public async Task RepeatedDueDatesAcrossUnitsAreFlaggedEvenAfterSourceValidation()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([Row(), Row(unit: 202)]);
        Assert.All((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items,
            c => Assert.Contains("UnitAllocationNeedsReview", c.Reason));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);
    }

    [Fact]
    public async Task StaleSourceFailsCampaignExport_AndSourceFailureDoesNotLookLikeAnEmptyList()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true; h.Source.Items.Add(Row());
        h.Source.ReadAt = Now.AddHours(-2);
        Assert.Contains("StaleSource", Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items).Reason);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);
        h.Source.Failure = new PactReceivablesSourceException("Unavailable");
        var failed = await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder");
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, failed.Outcome); Assert.Null(failed.Value);
    }

    [Fact]
    public async Task LegalNoticeIsSeparateAndReferralNeverBecomesACustomerCampaign()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.Add(Row(amount: 2000m) with { DueDate = new(2026, 9, 20) });
        Assert.Contains("LegalNoticeReleaseRequired", Assert.Single((await h.Service.PreviewAsync(h.Manager, "LegalNotice")).Value!.Items).Reason);
        h.Campaign.LegalNoticeExportEnabled = true;
        Assert.Equal(CollectionsOutcome.Success, (await h.Service.ExportAsync(h.Manager, "LegalNotice", "genesys")).Outcome);
        h.Source.Items.Clear(); h.Source.Items.Add(Row(amount: 25000m) with { DueDate = new(2026, 6, 1) });
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "LegalReferral", "genesys", new(2026, 10, 30))).Outcome);
        Assert.Equal(CollectionsOutcome.Success, (await h.Service.ExportAsync(h.Manager, "LegalReferral", "review")).Outcome);
    }

    [Fact]
    public async Task ExportCapRejectsRatherThanTruncating()
    {
        var h = new Harness(); h.Campaign.MaxExportRows = 1;
        h.Source.Items.AddRange([Row(), Row(tenant: "3002")]);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Outcome);
    }

    [Fact]
    public async Task AgentsCanPreviewButCannotExport_AndInvalidRequestsNeverReadSource()
    {
        var h = new Harness(); h.Source.Items.Add(Row());
        Assert.Equal(CollectionsOutcome.Success, (await h.Service.PreviewAsync(h.Agent, "CurrentMonthReminder")).Outcome);
        var reads = h.Source.Reads;
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Service.ExportAsync(h.Agent, "CurrentMonthReminder", "review")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, "1")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", companyId: 25)).Outcome);
        Assert.Equal(reads, h.Source.Reads);
    }

    [Fact]
    public async Task ReviewCsvEscapesCommasQuotesNewlinesAndFormulaPrefixes()
    {
        var h = new Harness(); h.Source.Items.Add(Row() with { FullName = "=\"Example,\"\nCustomer" });
        var file = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Value!;
        using var parser = new TextFieldParser(new StringReader(file.Csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        parser.ReadFields(); var fields = parser.ReadFields()!;
        Assert.Equal("'=\"Example,\"\nCustomer", fields[4]); Assert.Equal("'+971500003001", fields[5]);
        Assert.Equal("500.00", fields[10]); Assert.Equal(24, fields.Length); Assert.Equal(new[] { "false", "false", "false" }, fields[19..22]);   // the review file adds TowerNumber, TowerName
    }

    [Theory]
    [InlineData("OverdueReminder", 2026, 10, 8, 2026, 10, 31)]
    [InlineData("LegalNotice", 2026, 10, 8, 2026, 10, 31)]
    [InlineData("LegalReferral", 2026, 10, 8, 2026, 10, 31)]
    [InlineData("CurrentMonthReminder", 2026, 10, 8, 2026, 10, 31)]
    [InlineData("FollowUpReminder", 2026, 10, 8, 2026, 10, 31)]
    [InlineData("CurrentMonthReminder", 2026, 2, 14, 2026, 2, 28)]   // non-leap February
    [InlineData("FollowUpReminder", 2028, 2, 3, 2028, 2, 29)]        // leap February
    [InlineData("CurrentMonthReminder", 2026, 12, 31, 2026, 12, 31)] // year end
    [InlineData("OverdueReminder", 2027, 1, 1, 2027, 1, 31)]         // From is the configured StartDate, NOT the preview year: Dec 2026 arrears must stay reachable in 2027
    public async Task DefaultWindowDependsOnTheStage_TheReadStartsAtTheHistory_TheEligibilityAtTheConfiguredStartDate(
        string stage, int y, int m, int d, int toY, int toM, int toD)
    {
        var h = new Harness();
        var report = (await h.Service.PreviewAsync(h.Manager, stage, new(y, m, d))).Value!;
        // The default end is the last day of the preview month for EVERY stage, and it never goes past the end of the CURRENT month (October 2026, Dubai): later months are neither Due nor Overdue.
        var expectedTo = new DateOnly(toY, toM, toD) > new DateOnly(2026, 10, 31) ? new DateOnly(2026, 10, 31) : new DateOnly(toY, toM, toD);
        // READ scope: the whole history (All months). The stage ELIGIBILITY still starts at the configured StartDate (2026-01-01): see CampaignReadScopeTests and the Verification tests.
        Assert.Equal(new PactReceivablesRequest(new(2000, 1, 1), expectedTo, null), h.Source.LastRequest);
        Assert.Equal(new DateOnly(2000, 1, 1), report.DateFrom); Assert.Equal(expectedTo, report.DateTo);
        Assert.Equal(new DateOnly(y, m, d), report.BusinessDate);
    }

    [Fact]
    public async Task CurrentMonthDefault_ReadsTheRestOfTheMonth_NeverNextMonth_AndTheOverdueStageKeepsItsPolicy()
    {
        var h = new Harness(); h.Source.Items.Add(Row(tenant: "3009") with { DueDate = new(2026, 11, 5) });   // next month: never read, never listed
        var current = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new(2026, 10, 8))).Value!;
        Assert.Empty(current.Items);                        // next month only: not listed at all
        h.Source.Items.Add(Row(tenant: "3002", day: 25));   // ONLY a coming day of the current month (today is 14 Oct): Due, and a candidate of the whole-month stage (the same rule Review applies)
        var coming = Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new(2026, 10, 8))).Value!.Items);
        Assert.NotEqual("NotEligible", coming.Status); Assert.Equal(500m, coming.Amount);
        h.Source.Items.Add(Row(tenant: "3002", day: 10));
        var item = Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new(2026, 10, 8))).Value!.Items);
        Assert.Equal(("3002", 1000m, 0m, 1000m), (item.TenantId, item.DueAmount, item.OverdueAmount, item.Amount));
        var overdue = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2026, 10, 8))).Value!;
        Assert.All(overdue.Items, i => Assert.Equal("NotEligible", i.Status));   // listed (it is Due), never an Overdue candidate
    }

    [Fact]
    public async Task EditedDatesOverrideTheStageDefaults_WithoutChangingEligibility()
    {
        var h = new Harness(); h.Source.Items.AddRange([Row(day: 12), Row(tenant: "3002", day: 5)]);
        var report = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new(2026, 10, 8),
            dateFrom: new(2026, 10, 1), dateTo: new(2026, 10, 8))).Value!;
        Assert.Equal("3002", Assert.Single(report.Items).TenantId);
        Assert.Contains(report.RangeNotes!, n => n.Contains("preview month"));
    }

    [Fact]
    public async Task ExplicitRangeAndCompanyAreAppliedAtTheSource_PreviewDateStaysSeparate()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(), Row(tenant: "3002", company: 32), Row(tenant: "3003") with { DueDate = new(2026, 3, 1) }]);
        var report = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new(2026, 10, 8), 4,
            dateFrom: new(2026, 10, 1), dateTo: new(2026, 10, 31))).Value!;
        Assert.Equal(new PactReceivablesRequest(new(2026, 10, 1), new(2026, 10, 31), 4), h.Source.LastRequest);   // To is the end of the current month
        Assert.Equal("3001", Assert.Single(report.Items).TenantId);
        Assert.Equal(new DateOnly(2026, 10, 8), report.BusinessDate);
    }

    [Fact]
    public async Task InvertedRangeIsRejectedWithoutReadingTheSource()
    {
        var h = new Harness();
        var result = await h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2026, 10, 8),
            dateFrom: new(2026, 10, 9), dateTo: new(2026, 10, 8));
        Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome); Assert.Equal(0, h.Source.Reads);
    }

    [Fact]
    public async Task OverdueStageKeepsItsPolicyCutoff_AndFlagsTheRangeInsteadOfChangingEligibility()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(tenant: "A") with { DueDate = new(2026, 2, 5) },   // overdue > 1 month: qualifies
                                 Row(tenant: "B") with { DueDate = new(2026, 9, 20) }]); // overdue < 1 month: does not
        var report = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2026, 10, 8))).Value!;
        Assert.Equal("A", Assert.Single(report.Items, i => i.Status != "NotEligible").TenantId);
        Assert.Equal("NotEligible", report.Items.Single(i => i.TenantId == "B").Status);   // B stays listed with its balance
        Assert.Contains(report.RangeNotes!, n => n.Contains("08 Sep 2026"));
    }

    [Fact]
    public async Task SourceTimeoutIsUnavailable_AndCallerCancellationPropagates()
    {
        var h = new Harness { }; h.Source.Delay = TimeSpan.FromSeconds(30);
        h.Sql.CommandTimeoutSeconds = 1; // budget = 31 s: the caller token fires first below
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2026, 10, 8), cancellationToken: cts.Token));
        h.Source.Failure = new TaskCanceledException(); h.Source.Delay = TimeSpan.Zero;
        var failed = await h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2026, 10, 8));
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, failed.Outcome);
    }
}
