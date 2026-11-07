using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.BackgroundJobs;

namespace TigerCS.Tests.Collections.Snapshot;

public sealed class ReceivablesSnapshotStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "Fresh")]
    [InlineData(90, "Fresh")]   // exactly at the limit is still fresh
    [InlineData(91, "Stale")]
    [InlineData(600, "Stale")]
    public void FreshnessFollowsTheDocumentedMaximumAge(int ageMinutes, string expected) =>
        Assert.Equal(expected, ReceivablesSnapshotStatusBuilder.Company(FakeSnapshotSource.Healthy(4, Now.AddMinutes(-ageMinutes)), Now, 90).Freshness);

    [Fact]
    public void NeverLoadedIsMissing_NotFreshAndNotEmpty()
    {
        var company = ReceivablesSnapshotStatusBuilder.Company(FakeSnapshotSource.NeverLoaded(32), Now, 90);
        Assert.Equal(("Missing", false), (company.Freshness, company.HasSnapshot));
    }

    [Fact]
    public void ASuccessTimestampInTheFutureIsNotTrustedAsFresh() =>
        Assert.Equal("Stale", ReceivablesSnapshotStatusBuilder.Company(FakeSnapshotSource.Healthy(4, Now.AddHours(3)), Now, 90).Freshness);

    [Fact]
    public void FailureAndCoverageAreTrackedPerCompany()
    {
        var status = ReceivablesSnapshotStatusBuilder.Build(
            [FakeSnapshotSource.Healthy(4, Now.AddMinutes(-5)), FakeSnapshotSource.Failed(32, Now.AddMinutes(-200), Now.AddMinutes(-1))], [], Now, 90);
        var dubai = status.Companies.Single(c => c.CompanyId == 4);
        var sharjah = status.Companies.Single(c => c.CompanyId == 32);
        Assert.False(dubai.LastRefreshFailed);
        Assert.Equal("Fresh", dubai.Freshness);
        Assert.True(sharjah.LastRefreshFailed);
        Assert.Equal(3, sharjah.ConsecutiveFailures);
        Assert.Equal("Stale", sharjah.Freshness);
        Assert.True(sharjah.HasSnapshot);          // the previous snapshot is still served
        Assert.True(status.HasFailures);
        Assert.False(status.IsFresh);
        Assert.Contains("Sharjah", status.FreshnessProblem);
        Assert.Equal(Now.AddMinutes(-200), status.OldestSuccessUtc);
    }

    [Fact]
    public void AFailedLatestAttemptWithinTheAgeLimitIsStillFlaggedButDataIsFresh()
    {
        var status = ReceivablesSnapshotStatusBuilder.Build([FakeSnapshotSource.Failed(4, Now.AddMinutes(-20), Now.AddMinutes(-1))], [], Now, 90);
        Assert.True(status.HasFailures);
        Assert.True(status.IsFresh);
    }

    [Fact]
    public void ACompanyThatWasNeverLoadedMakesTheScopeNotFresh_AndHasNoOldestSuccess()
    {
        var status = ReceivablesSnapshotStatusBuilder.Build([FakeSnapshotSource.Healthy(4, Now), FakeSnapshotSource.NeverLoaded(32)], [], Now, 90);
        Assert.False(status.IsComplete);
        Assert.False(status.IsFresh);
        Assert.Null(status.OldestSuccessUtc);
        Assert.Contains("not been loaded", status.FreshnessProblem);
    }

    private static readonly DateOnly From = new(2026, 1, 1), Through = new(2026, 10, 31);

    [Fact]
    public void ComposeNeverTurnsNothingLoadedIntoAnEmptyResult_ItReportsTheWholeRangeAsMissing()
    {
        var snapshot = ReceivablesSnapshotComposer.Compose(From, Through, [FakeSnapshotSource.NeverLoaded(4), FakeSnapshotSource.NeverLoaded(32)], [], [], Now, 90);
        Assert.True(snapshot.Snapshot!.NothingLoaded);
        Assert.Equal(2, snapshot.Snapshot.Gaps.Count);
        Assert.All(snapshot.Snapshot.Gaps, g => Assert.Equal((From, Through), (g.From, g.Through)));
        Assert.False(snapshot.Snapshot.IsReady);
        Assert.Equal(DateTime.UnixEpoch, snapshot.ReadAtUtc);          // "never" - callers display it as not loaded
    }

    [Fact]
    public void ComposeKeepsServingThePreviousSnapshotWhenTheLatestRefreshFailed()
    {
        var snapshot = ReceivablesSnapshotComposer.Compose(From, Through,
            [FakeSnapshotSource.Healthy(4, Now.AddMinutes(-5)), FakeSnapshotSource.Failed(32, Now.AddHours(-5), Now.AddMinutes(-2))], [], [], Now, 90);
        Assert.Equal(Now.AddHours(-5), snapshot.ReadAtUtc);      // the OLDEST success governs
        Assert.False(snapshot.Snapshot!.IsFresh);
        Assert.True(snapshot.Snapshot.Companies.Single(c => c.CompanyId == 32).LastRefreshFailed);
    }

    [Fact]
    public void ComposeFlagsAWindowOutsideTheLoadedCoverage_InsteadOfTruncatingItOrFailing()
    {
        var narrow = FakeSnapshotSource.Healthy(4, Now) with { CoverageFrom = new DateOnly(2026, 6, 1) };
        Assert.False(ReceivablesSnapshotComposer.Compose(From, Through, [narrow], [], [], Now, 90).Snapshot!.RangeCovered);
        var early = FakeSnapshotSource.Healthy(4, Now) with { CoverageThrough = new DateOnly(2026, 9, 30) };
        Assert.False(ReceivablesSnapshotComposer.Compose(From, Through, [early], [], [], Now, 90).Snapshot!.RangeCovered);
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Abandoned", true)]      // a timeout / cancellation / restart is a failed attempt: the page offers Retry
    [InlineData("Running", false)]       // loading, not failed
    [InlineData("Succeeded", false)]
    [InlineData("Never", false)]
    public void AnAbandonedAttemptCountsAsAFailedRefresh_ARunningOneDoesNot(string lastStatus, bool failed) =>
        Assert.Equal(failed, ReceivablesSnapshotStatusBuilder.Company(FakeSnapshotSource.Healthy(4, Now.AddMinutes(-5)) with { LastAttemptStatus = lastStatus }, Now, 180).LastRefreshFailed);

    private sealed class Refresher(ReceivablesRefreshResult result) : IReceivablesRefresher
    {
        public Task<ReceivablesRefreshResult> RefreshAsync(string triggerSource, int? companyId, CancellationToken cancellationToken, DateOnly? fromDate = null, DateOnly? throughDate = null) => Task.FromResult(result);
    }

    private sealed class PostRefresh : TigerCS.Application.Modules.Collections.Reconciliation.IPostRefreshReconciliation
    {
        public List<int> Companies { get; } = [];
        public Task<TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationRunDto?> RunAfterRefreshAsync(int companyId, CancellationToken cancellationToken)
        { Companies.Add(companyId); return Task.FromResult<TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationRunDto?>(null); }
    }

    private static Task Run(string status, params ReceivablesRefreshCompanyResult[] companies) => Run(status, null, companies);

    private static Task Run(string status, PostRefresh? reconciliation, params ReceivablesRefreshCompanyResult[] companies) =>
        new CollectionsReceivablesRefreshJob(new Refresher(new ReceivablesRefreshResult(Guid.NewGuid(), status, "m", companies)), NullLogger<CollectionsReceivablesRefreshJob>.Instance, reconciliation)
            .RunAsync(4, CancellationToken.None);

    [Fact]
    public async Task Job_RunsTheSnapshotConsistencyCheckOfTheCompany_OnlyAfterItsRefreshSucceeded()
    {
        var after = new PostRefresh();
        await Run("Succeeded", after, new ReceivablesRefreshCompanyResult(4, "Succeeded", 10, 5, 4, 1, 0, null, null));
        Assert.Equal([4], after.Companies);                                                  // the refreshed company, in the same job, after the publish
        await Run("AlreadyRunning", after);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("Failed", after, new ReceivablesRefreshCompanyResult(4, "Failed", null, null, null, null, null, 7416, "x")));
        Assert.Equal([4], after.Companies);                                                  // a skipped or failed refresh publishes nothing, so nothing is checked
        var retry = new CollectionsReceivablesCompanyRetryJob(new Refresher(new ReceivablesRefreshResult(Guid.NewGuid(), "Succeeded", null, [new(32, "Succeeded", 10, 5, 4, 1, 0, null, null)])), NullLogger<CollectionsReceivablesCompanyRetryJob>.Instance, after);
        await retry.RunAsync(32, CancellationToken.None);
        Assert.Equal([4, 32], after.Companies);                                              // the Retry button's refresh is followed by the check too
    }

    [Fact]
    public async Task Job_SucceedsSilently_AndTreatsAnOverlapAsASkipNotAnError()
    {
        await Run("Succeeded", new ReceivablesRefreshCompanyResult(4, "Succeeded", 10, 5, 4, 1, 0, null, null));
        await Run("AlreadyRunning");
    }

    [Theory]
    [InlineData("PartialFailure")]
    [InlineData("Failed")]
    public async Task Job_ReportsPartialAndTotalFailureToHangfire(string status) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(status,
            new ReceivablesRefreshCompanyResult(4, "Succeeded", 10, 5, 4, 1, 0, null, null),
            new ReceivablesRefreshCompanyResult(32, "Failed", null, null, null, null, null, 7416, "x")));

    [Fact]
    public void ServicesDefaultToATwoHourRefresh_InDubai_AndAFreshnessLimitThatOutlivesOneRefresh()
    {
        var options = new TigerCS.Application.Modules.Collections.ReceivablesSnapshotOptions();
        Assert.Equal("0 */2 * * *", options.RefreshCron);
        Assert.Equal("Asia/Dubai", options.RefreshTimeZoneId);
        // 120 minutes between refreshes + the 30 minute command timeout of a slow one + slack: a snapshot is never "stale" before its next normal refresh.
        Assert.Equal(180, options.MaxAgeMinutes);
        Assert.True(options.MaxAgeMinutes >= 120 + options.RefreshCommandTimeoutSeconds / 60);
        // StaleAfterMinutes (60) judges LIVE reads. Figures read from the 2-hourly snapshot (the Review workflow) use the snapshot's own limit instead - never stricter than it.
        Assert.Equal(60, new TigerCS.Application.Modules.Collections.CollectionsOptions().StaleAfterMinutes);
        var clock = new TigerCS.Application.Modules.Collections.Services.CollectionsClock(new TigerCS.Application.Modules.Collections.CollectionsOptions(), new TigerCS.Tests.Notifications.Fakes.FakeTimeProvider(Now));
        Assert.True(clock.IsStale(Now.AddMinutes(-90)));                         // a live read of 90 minutes ago is stale ...
        Assert.False(clock.IsStale(Now.AddMinutes(-170), options.MaxAgeMinutes)); // ... a snapshot copy of 170 minutes ago (due for refresh within the hour) is not
        Assert.True(clock.IsStale(Now.AddMinutes(-181), options.MaxAgeMinutes));
        Assert.Equal(0, options.SourceMinAmount);          // never the diagnostic 100: the Minimum Total of the pages is not sent to PACT
        Assert.Equal(new DateTime(2000, 1, 1), options.SourceFromDate);
        Assert.Equal(new DateTime(2099, 12, 31), options.SourceThroughDate);   // the permanent history window, not a test window
        Assert.Equal(100m, new PactReceivablesOptions().DefaultMinOutstandingAmount);
    }
}
