using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>
/// The Campaigns service on top of a source that evaluates campaigns in the data store (IPactCampaignSource): what it asks for, how it turns unit flags and
/// the global gates into statuses / reasons / counts, and its fallback. (The production source reads payment rows and the application evaluates the rules in memory; this
/// engine contract stays for a source that evaluates campaigns itself.)
/// </summary>
public sealed class CampaignSqlEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 5, 0, 0, DateTimeKind.Utc);   // 09:00 in Dubai, the 14th: scheduled day of the current-month stage
    private static readonly CollectionsCaller Manager = new(Guid.NewGuid(), [Roles.CsManager], []);

    private sealed class EngineSource(PactCampaignPage page) : IPactReceivablesSource, IPactCampaignSource
    {
        public PactCampaignRequest? Last { get; private set; }
        public int RowReads { get; private set; }
        public Exception? Throw { get; set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
        {
            RowReads++;
            return Task.FromResult(new PactReceivablesSnapshot([], Now.AddMinutes(-5), false, Status(Now.AddMinutes(-5))));
        }
        public Task<PactCampaignPage> ReadCampaignAsync(PactCampaignRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            if (Throw is not null) throw Throw;
            return Task.FromResult(page);
        }
    }

    private static TigerCS.Application.Modules.Collections.Dto.SnapshotStatusDto Status(DateTime successUtc, DateTime? now = null) =>
        ReceivablesSnapshotComposer.BuildStatus(new(2026, 1, 1), new(2026, 10, 31),
            [FakeSnapshotSource.Healthy(4, successUtc), FakeSnapshotSource.Healthy(32, successUtc)], [], now ?? Now, 90);

    private static CampaignUnitFacts Unit(string tenant, CollectionsCampaignFlags flags = CollectionsCampaignFlags.None, decimal? amount = 250m) =>
        new(4, tenant, "Customer " + tenant, "+971500000001", "c@example.test", 11, "TP124-1001", "", amount, new DateOnly(2026, 10, 20), (int)flags, "124", "Tower 124");

    private static PactCampaignPage Page(int clean, int review, DateTime? successUtc = null, params CampaignUnitFacts[] units) => PageAt(clean, review, successUtc, null, units);

    private static PactCampaignPage PageAt(int clean, int review, DateTime? successUtc, DateTime? now, params CampaignUnitFacts[] units) =>
        new(true, 0, clean + review, clean, review, units, (successUtc ?? Now.AddMinutes(-5)), Status(successUtc ?? Now.AddMinutes(-5), now), 12);

    private static CollectionsCampaignAppService Service(IPactReceivablesSource source, bool validated = true, bool legalRelease = false, int maxExport = 5000, string currency = "AED", DateTime? nowUtc = null)
    {
        var options = new CollectionsOptions { Enabled = true };
        return new(options, new CollectionsCampaignOptions { FinancialSourceValidated = validated, LegalNoticeExportEnabled = legalRelease, MaxExportRows = maxExport },
            new PactReceivablesOptions { Enabled = true, Currency = currency }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(nowUtc ?? Now)), source,
            NullLogger<CollectionsCampaignAppService>.Instance);
    }

    [Theory]
    [InlineData("OverdueReminder", null, "2026-09-14", 0, true)]                  // due before one month ago
    [InlineData("CurrentMonthReminder", "2026-10-01", "2026-11-01", 0, true)]
    [InlineData("FollowUpReminder", "2026-10-01", "2026-11-01", 0, true)]
    [InlineData("LegalNotice", "2026-09-01", "2026-10-01", 1500, true)]
    [InlineData("LegalReferral", null, "2026-07-14", 20000, false)]               // internal: no contact needed
    public async Task TheStageRuleThresholdAndContactRequirement_AreHandedToTheEngine(string stage, string? stageFrom, string stageToExclusive, int threshold, bool contactRequired)
    {
        var source = new EngineSource(Page(0, 0));
        await Service(source).PreviewAsync(Manager, stage, new DateOnly(2026, 10, 14));
        var r = source.Last!;
        Assert.Equal(stageFrom is null ? null : DateOnly.Parse(stageFrom), r.StageFrom);
        Assert.Equal(DateOnly.Parse(stageToExclusive), r.StageToExclusive);
        Assert.Equal((decimal)threshold, r.Threshold);
        Assert.Equal(contactRequired, r.ContactRequired);
        Assert.Equal(0, source.RowReads);                                          // a supporting source is never asked for the window's instalments
    }

    [Fact]
    public async Task FiltersWindowPagingAndSearchTermsReachTheEngine_AsOneRequest()
    {
        var source = new EngineSource(Page(0, 0));
        await Service(source).PreviewAsync(Manager, "OverdueReminder", new DateOnly(2026, 10, 14), 32, "  +97150000  ", 3, 10, false, default,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 7, 250.5m);
        var r = source.Last!;
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 250.5m, 32, 7), (r.From, r.To, r.MinTotal, r.CompanyId, r.TowerId));
        Assert.Equal((20, 10), (r.Offset, r.Take));                                // page 3 of 10: paging is decided by the engine, after eligibility
        Assert.Equal(("+97150000", "97150000"), (r.Search, r.PhoneDigits));       // phone-like term: digits are searched in the normalised phone too
        foreach (var (term, digits) in new (string, string?)[] { ("Customer 12", null), ("abc123456", null), ("124", "124"), ("TP124-1001", null) })
        {
            await Service(source).PreviewAsync(Manager, "OverdueReminder", new DateOnly(2026, 10, 14), search: term);
            Assert.Equal(digits, source.Last!.PhoneDigits);
        }
        await Service(source).PreviewAsync(Manager, "OverdueReminder", new DateOnly(2026, 10, 14), search: "   ");
        Assert.Null(source.Last!.Search);
    }

    [Fact]
    public async Task Counts_FollowTheWholeResult_NotThePage_AndEveryUnitLevelReasonMakesAUnitNeedReview()
    {
        // 3 clean + 2 with unit-level reasons over the whole set; only one page of 2 units is returned.
        var page = Page(3, 2, null, Unit("A"), Unit("B", CollectionsCampaignFlags.ConflictingContactDetails));
        var report = (await Service(new EngineSource(page)).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14), pageSize: 2)).Value!;
        Assert.Equal((5, 3, 2), (report.TotalCount, report.ReadyCount, report.ReviewCount));
        Assert.Equal(["Ready", "NeedsReview"], report.Items.Select(i => i.Status));
        Assert.Equal(["Qualifies", "ConflictingContactDetails"], report.Items.Select(i => i.Reason));
    }

    [Fact]
    public async Task GlobalGates_ApplyToEveryUnit_ExactlyAsInTheInMemoryEvaluation()
    {
        var clean = new[] { Unit("A") };
        // Stale snapshot: every unit needs review, Ready is 0 whatever the page holds.
        var stale = (await Service(new EngineSource(Page(3, 2, Now.AddHours(-10), clean))).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14))).Value!;
        Assert.Equal((5, 0, 5), (stale.TotalCount, stale.ReadyCount, stale.ReviewCount));
        Assert.Equal("StaleSource", stale.Items[0].Reason);
        // Not the live scheduled date: preview only (neither Ready nor NeedsReview), units with their own reasons still need review.
        var preview = (await Service(new EngineSource(Page(3, 2, null, clean))).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 13))).Value!;
        Assert.Equal((5, 0, 2), (preview.TotalCount, preview.ReadyCount, preview.ReviewCount));
        Assert.Equal(("PreviewOnly", "OutsideSchedule"), (preview.Items[0].Status, preview.Items[0].Reason));
        // Legal notices need an explicit release; internal legal referrals are never customer-ready.
        var notice = (await Service(new EngineSource(Page(3, 2, null, clean))).PreviewAsync(Manager, "LegalNotice", new DateOnly(2026, 10, 14))).Value!;
        Assert.Equal(("NeedsReview", "LegalNoticeReleaseRequired"), (notice.Items[0].Status, notice.Items[0].Reason));
        Assert.Equal((0, 5), (notice.ReadyCount, notice.ReviewCount));
        var released = (await Service(new EngineSource(Page(3, 2, null, clean)), legalRelease: true).PreviewAsync(Manager, "LegalNotice", new DateOnly(2026, 10, 14))).Value!;
        Assert.Equal((3, 2), (released.ReadyCount, released.ReviewCount));
        var referralDay = new DateTime(2026, 10, 30, 5, 0, 0, DateTimeKind.Utc);            // the 30th is the referral stage's scheduled day
        var referral = (await Service(new EngineSource(PageAt(3, 2, referralDay.AddMinutes(-5), referralDay, clean)), nowUtc: referralDay).PreviewAsync(Manager, "LegalReferral", new DateOnly(2026, 10, 30))).Value!;
        Assert.Equal(("InternalReview", "InternalLegalReferralOnly"), (referral.Items[0].Status, referral.Items[0].Reason));
        Assert.Equal((0, 2), (referral.ReadyCount, referral.ReviewCount));
        // Unvalidated financial source: the report says so once and sending is blocked; the units keep their own status. Non-AED currency: everything needs review.
        var unvalidated = (await Service(new EngineSource(Page(3, 2, null, clean)), validated: false).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14))).Value!;
        Assert.False(unvalidated.FinancialSourceValidated); Assert.Equal((3, 2), (unvalidated.ReadyCount, unvalidated.ReviewCount));
        Assert.DoesNotContain("SourceReconciliationRequired", unvalidated.Items[0].Reason);
        var usd = (await Service(new EngineSource(Page(3, 2, null, clean)), currency: "USD").PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14))).Value!;
        Assert.Equal("CurrencyNeedsReview", usd.Items[0].Reason);
    }

    [Fact]
    public async Task ReasonOrder_IsTheDocumentedOne_WithGlobalReasonsBetweenUnitAndContactReasons()
    {
        var all = CollectionsCampaignFlags.AmbiguousInstalments | CollectionsCampaignFlags.AmountPrecisionNeedsReview | CollectionsCampaignFlags.MissingUnitIdentity
            | CollectionsCampaignFlags.ConflictingContactDetails | CollectionsCampaignFlags.UnitAllocationNeedsReview | CollectionsCampaignFlags.ContradictoryPaymentStatus
            | CollectionsCampaignFlags.NoValidContact;
        var report = (await Service(new EngineSource(Page(0, 1, Now.AddHours(-10), Unit("A", all, amount: null))), validated: false)
            .PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14))).Value!;
        Assert.Equal("AmbiguousInstalments;AmountPrecisionNeedsReview;MissingUnitIdentity;ConflictingContactDetails;UnitAllocationNeedsReview;ContradictoryPaymentStatus;StaleSource;NoValidContact",
            report.Items[0].Reason);
        Assert.Null(report.Items[0].Amount);                                       // an ambiguous unit has no amount
    }

    [Fact]
    public async Task Exports_AskForTheBoundedFullResult_AndRefuseOverTheLimitBeforeBuildingAnyFile()
    {
        var units = Enumerable.Range(1, 3).Select(i => Unit("T" + i)).ToArray();
        var source = new EngineSource(Page(3, 0, null, units));
        foreach (var mode in new[] { "review", "genesys" })
        {
            var file = await Service(source, maxExport: 3).ExportAsync(Manager, "CurrentMonthReminder", mode, new DateOnly(2026, 10, 14));
            Assert.True(file.IsSuccess, file.Detail);
            Assert.Equal((0, 4), (source.Last!.Offset, source.Last.Take));        // limit + 1: one extra row proves "over the limit" without paging
            Assert.Equal(3, file.Value!.RowCount);
            Assert.Equal(4, file.Value.Csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);   // header + 3 rows
        }
        var refused = await Service(new EngineSource(Page(3, 0, null, units)), maxExport: 2).ExportAsync(Manager, "CurrentMonthReminder", "review", new DateOnly(2026, 10, 14));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome);
        Assert.Contains("row limit", refused.Detail);
    }

    [Fact]
    public async Task GenesysExport_NeedsEveryRowReady_OnTheEnginePath()
    {
        var withReview = new EngineSource(Page(1, 1, null, Unit("A"), Unit("B", CollectionsCampaignFlags.NoValidContact)));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Service(withReview).ExportAsync(Manager, "CurrentMonthReminder", "genesys", new DateOnly(2026, 10, 14))).Outcome);
        Assert.True((await Service(withReview).ExportAsync(Manager, "CurrentMonthReminder", "review", new DateOnly(2026, 10, 14))).IsSuccess);
        var stale = new EngineSource(Page(2, 0, Now.AddHours(-10), Unit("A"), Unit("B")));
        var blocked = await Service(stale).ExportAsync(Manager, "CurrentMonthReminder", "review", new DateOnly(2026, 10, 14));
        Assert.Equal(CollectionsOutcome.InvalidRequest, blocked.Outcome);         // the freshness / coverage gate is unchanged
    }

    [Fact]
    public async Task AnEngineThatCannotAnswer_FallsBackToTheInMemoryEvaluation()
    {
        var source = new EngineSource(new PactCampaignPage(false, 0, 0, 0, 0, [], Now, Status(Now.AddMinutes(-5)), 3));
        var report = await Service(source).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14));
        Assert.True(report.IsSuccess);
        Assert.Equal(1, source.RowReads);                                          // the window's instalments are read and evaluated in memory
    }

    [Fact]
    public async Task EngineErrors_AreReportedLikeAnyOtherSourceError()
    {
        var unavailable = new EngineSource(Page(0, 0)) { Throw = new PactReceivablesSourceException("The local receivables snapshot could not be read.") };
        var failed = await Service(unavailable).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14));
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, failed.Outcome);
        var scope = new EngineSource(Page(0, 0)) { Throw = new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.") };
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Service(scope).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14), towerId: 999)).Outcome);
        var identity = new PactCampaignPage(true, 2, 0, 0, 0, [], Now, Status(Now.AddMinutes(-5)), 1);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, (await Service(new EngineSource(identity)).PreviewAsync(Manager, "CurrentMonthReminder", new DateOnly(2026, 10, 14))).Outcome);
    }

    [Fact]
    public void Preview_DoesNotDependOnTheRefreshOrTheRangeLoader()
    {
        // A preview only reads the local snapshot: the service has no way to start or wait for a PACT refresh.
        var dependencies = typeof(CollectionsCampaignAppService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IReceivablesRefresher), dependencies);
        Assert.DoesNotContain(typeof(IReceivablesRangeLoader), dependencies);
        Assert.Contains(typeof(IPactReceivablesSource), dependencies);
    }

    [Fact]
    public void StageRangeAndThreshold_DescribeExactlyWhatEvaluateQualifies()
    {
        // Evaluate (the in-memory rule) and StageRange/Threshold (what the SQL engine receives) must agree on every boundary day.
        foreach (var stage in Enum.GetValues<CollectionsCampaignStage>())
            foreach (var date in new[] { new DateOnly(2026, 10, 14), new DateOnly(2026, 3, 31), new DateOnly(2024, 2, 29), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31) })
            {
                var (from, toExclusive) = CollectionsCampaignPolicy.StageRange(stage, date);
                var threshold = CollectionsCampaignPolicy.Threshold(stage);
                for (var offset = -460; offset <= 70; offset++)
                {
                    var due = date.AddDays(offset);
                    var inRange = (from is null || due >= from) && due < toExclusive;
                    foreach (var amount in new[] { threshold, threshold + 0.01m, threshold + 1m })
                    {
                        if (amount <= 0) continue;
                        var result = CollectionsCampaignPolicy.Evaluate([new CampaignInstalment(due, amount)], stage, date);
                        Assert.Equal(inRange && amount > threshold, result.Reason == "Qualifies");
                    }
                }
            }
    }

    [Theory]
    [InlineData("0501234567", "+971501234567")]
    [InlineData("+971501234567", "+971501234567")]
    [InlineData("971501234567", "+971501234567")]
    [InlineData(" 050 123 4567 ", "+971501234567")]
    [InlineData("00971501234567", "+971501234567")]
    [InlineData("123", "")]
    [InlineData("", "")]
    public void PhoneNormalisation_IsTheReviewWorkflowsRule_NotASecondDefinition(string raw, string expected)
    {
        Assert.Equal(expected, CollectionsContactNormalizer.NormalizePhone(raw));
        Assert.Equal(TigerCS.Domain.Modules.Collections.Review.PhoneNormalizer.Normalize(raw).E164, CollectionsContactNormalizer.NormalizePhone(raw));
    }

    [Theory]
    [InlineData("a@example.test", "a@example.test")]
    [InlineData("  a@example.test ", "a@example.test")]
    [InlineData("Name <a@example.test>", "")]
    [InlineData("not-an-email", "")]
    [InlineData("", "")]
    [InlineData("a@b", "a@b")]
    public void EmailNormalisation_IsTheSingleDefinition(string raw, string expected) => Assert.Equal(expected, CollectionsContactNormalizer.NormalizeEmail(raw));
}
