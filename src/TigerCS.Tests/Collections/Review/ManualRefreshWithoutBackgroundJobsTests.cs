using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.BackgroundJobs;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Refreshing the approval data by hand when <c>BackgroundJobs:Enabled</c> is false. The refresh is the same <see cref="ReviewRefreshService"/>; it runs on one bounded in-process worker, one at a time and
/// rate limited, never recurs, and nothing else changes: a dispatch and the suppression sweep still need real background jobs, so with jobs off nothing can be sent.
/// </summary>
public sealed class ManualRefreshWithoutBackgroundJobsTests
{
    private static ReviewHarness Harness(bool inProcess, int cooldownSeconds = 60, bool jobsOff = true)
    {
        var h = new ReviewHarness();
        h.Scheduler.Available = !jobsOff;
        h.Scheduler.InProcessRefresh = inProcess;
        h.Review.InProcessRefreshCooldownSeconds = cooldownSeconds;
        h.Source.Items.AddRange([ReviewHarness.Row(1), ReviewHarness.Row(2)]);
        return h;
    }

    private static async Task<CollectionsResult<ReviewRunDto>> StartAsync(ReviewHarness h, CollectionsCaller? caller = null)
    {
        using var scope = h.NewScope();
        return await scope.Refresh.StartAsync(caller ?? h.Manager, null, CancellationToken.None);
    }

    private static async Task RunAsync(ReviewHarness h, long runId)
    {
        using var scope = h.NewScope();
        await scope.Refresh.RunAsync(runId, CancellationToken.None);
    }

    private static async Task<ReviewRunDto?> CurrentAsync(ReviewHarness h)
    {
        using var scope = h.NewScope();
        return (await scope.Query.GetCurrentRunAsync(h.Manager, CancellationToken.None)).Value;
    }

    // ------------------------------------------------------------ the service

    [Fact]
    public async Task WithJobsOffAndNoInProcessWorker_TheRefreshStillFailsVisibly_AsBefore()
    {
        var h = Harness(inProcess: false);
        var started = await StartAsync(h);
        Assert.Equal("Failed", started.Value!.Status); Assert.Contains("Background jobs are disabled", started.Value.Error);
        Assert.Empty(h.Scheduler.Refreshes);
    }

    [Fact]
    public async Task WithJobsOff_AnAuthorizedUserCanRefresh_ThroughTheSameService_AndTheDataIsPublished()
    {
        var h = Harness(inProcess: true);
        var started = await StartAsync(h);
        Assert.Equal(CollectionsOutcome.Accepted, started.Outcome); Assert.Equal("Queued", started.Value!.Status);
        Assert.Equal([started.Value.RunId], h.Scheduler.Refreshes);                                   // handed to the in-process worker, nothing recurring
        await RunAsync(h, started.Value.RunId);                                                      // what the worker does
        var run = await CurrentAsync(h);
        Assert.Equal("Completed", run!.Status); Assert.True(run.IsCurrent); Assert.True(run.RecordCount > 0); Assert.Equal(100, run.ProgressPercent);
        Assert.Empty(h.Scheduler.Dispatches);                                                          // a refresh never queues a send
    }

    [Fact]
    public async Task TheFinancialReconciliationGate_IsUnchanged_ByRefreshingInProcess()
    {
        var h = Harness(inProcess: true);
        h.Campaign.FinancialSourceValidated = false;                                                  // no sign-off
        var started = await StartAsync(h);
        await RunAsync(h, started.Value!.RunId);
        var run = await CurrentAsync(h);
        Assert.Equal("Completed", run!.Status); Assert.False(run.SourceReconciled);                    // the run says so ...
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth"), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(0, page.Counts.Ready);                                                            // ... and every record is held back for reconciliation
        Assert.All(page.Items, i => Assert.Contains(i.Reasons, r => r.Code == "SourceReconciliationRequired"));
    }

    [Fact]
    public async Task OnlyOneRefreshRunsAtATime_ASecondRequestGetsTheRunInProgress()
    {
        var h = Harness(inProcess: true);
        var first = await StartAsync(h);
        var second = await StartAsync(h);
        var third = await StartAsync(h, h.Agent);                                                      // another user, same moment
        Assert.Equal(CollectionsOutcome.Replayed, second.Outcome); Assert.Equal(first.Value!.RunId, second.Value!.RunId);
        Assert.Equal(first.Value.RunId, third.Value!.RunId);
        Assert.Single(h.Scheduler.Refreshes);                                                          // no second worker, no second read of the source
        using var context = h.CreateContext();
        Assert.Single(context.CollectionsReviewRuns);
    }

    [Fact]
    public async Task ARefreshIsNotRepeatedDuringTheCooldown_ButIsAllowedAfterIt()
    {
        var h = Harness(inProcess: true, cooldownSeconds: 60);
        var first = await StartAsync(h);
        await RunAsync(h, first.Value!.RunId);
        var tooSoon = await StartAsync(h);                                                             // a double click, or a colleague
        Assert.Equal(CollectionsOutcome.Replayed, tooSoon.Outcome); Assert.Equal("Completed", tooSoon.Value!.Status); Assert.Equal(first.Value.RunId, tooSoon.Value.RunId);
        Assert.Single(h.Scheduler.Refreshes);                                                          // the source was not read again
        h.Time.Advance(TimeSpan.FromSeconds(61)); h.Source.ReadAt = h.Time.GetUtcNow().UtcDateTime;
        var later = await StartAsync(h);
        Assert.Equal(CollectionsOutcome.Accepted, later.Outcome); Assert.NotEqual(first.Value.RunId, later.Value!.RunId);
        Assert.Equal(2, h.Scheduler.Refreshes.Count);
    }

    [Fact]
    public async Task TheCooldown_CanBeSwitchedOff_AndNeverAppliesWhenBackgroundJobsRun()
    {
        var noCooldown = Harness(inProcess: true, cooldownSeconds: 0);
        var a = await StartAsync(noCooldown); await RunAsync(noCooldown, a.Value!.RunId);
        Assert.Equal(CollectionsOutcome.Accepted, (await StartAsync(noCooldown)).Outcome);

        var withJobs = Harness(inProcess: false, cooldownSeconds: 600, jobsOff: false);                // Hangfire is on: the established behaviour, untouched
        var b = await StartAsync(withJobs); await RunAsync(withJobs, b.Value!.RunId);
        var again = await StartAsync(withJobs);
        Assert.Equal(CollectionsOutcome.Accepted, again.Outcome); Assert.NotEqual(b.Value.RunId, again.Value!.RunId);
    }

    [Fact]
    public async Task AFailedRefresh_ShowsAClearError_AndTheNextAttemptIsNotBlocked()
    {
        var h = Harness(inProcess: true);
        h.Source.Failure = new PactReceivablesSourceException("The PACT report connection is not configured.");
        var started = await StartAsync(h);
        await RunAsync(h, started.Value!.RunId);
        var failed = await CurrentAsync(h);
        Assert.Equal("Failed", failed!.Status); Assert.Equal("The PACT report connection is not configured.", failed.Error);
        h.Source.Failure = null;
        var retry = await StartAsync(h);                                                               // a failed run starts no cooldown and leaves nothing active
        Assert.Equal(CollectionsOutcome.Accepted, retry.Outcome); Assert.NotEqual(started.Value.RunId, retry.Value!.RunId);
    }

    [Fact]
    public async Task OnlyUsersWhoMayReadFinancials_CanStartIt()
    {
        var h = Harness(inProcess: true);
        Assert.Equal(CollectionsOutcome.Forbidden, (await StartAsync(h, h.Reporting)).Outcome);
        Assert.Empty(h.Scheduler.Refreshes);
    }

    [Fact]
    public async Task WithJobsOff_NothingCanBeSent_TheDispatchStillNeedsRealBackgroundJobs()
    {
        var h = Harness(inProcess: true);
        var started = await StartAsync(h);
        await RunAsync(h, started.Value!.RunId);
        var (_, confirm) = await h.PrepareAsync(new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m), ReviewQueryService.ModeAllMatching));
        using var scope = h.NewScope();
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.NotEqual(CollectionsOutcome.Accepted, refused.Outcome);
        Assert.Contains("Background jobs are switched off", refused.Detail);
        Assert.Empty(h.Scheduler.Dispatches); Assert.Empty(h.Client.Calls);
        var readiness = (await scope.Dispatch.ReadinessAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.Contains(readiness.Blockers, b => b.Contains("Background jobs are switched off"));
    }

    // ------------------------------------------------------------ the in-process worker

    private sealed class Runner : IReviewRefreshRunner
    {
        public List<long> Ran { get; } = [];
        public int Concurrent, MaxConcurrent;
        public Exception? Failure;
        public TaskCompletionSource? Hold;
        public async Task RunAsync(long runId, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Concurrent);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                if (Hold is not null) await Hold.Task.WaitAsync(ct);
                lock (this) Ran.Add(runId);
                if (Failure is not null) throw Failure;
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    private static (InProcessReviewJobScheduler Scheduler, Runner Runner, Lifetime Lifetime) Worker()
    {
        var runner = new Runner();
        var services = new ServiceCollection().AddScoped<IReviewRefreshRunner>(_ => runner).BuildServiceProvider();
        var lifetime = new Lifetime();
        return (new InProcessReviewJobScheduler(services.GetRequiredService<IServiceScopeFactory>(), lifetime, NullLogger<InProcessReviewJobScheduler>.Instance), runner, lifetime);
    }

    [Fact]
    public async Task TheWorker_RunsTheRefresh_AndOnlyTheRefresh()
    {
        var (scheduler, runner, lifetime) = Worker(); using var _ = lifetime;
        Assert.False(scheduler.IsAvailable); Assert.True(scheduler.CanRunRefresh); Assert.True(scheduler.RefreshRunsInProcess);
        scheduler.EnqueueDispatch(5); scheduler.EnqueueSuppressionSweep();                           // these stay off with the jobs
        scheduler.EnqueueRefresh(42);
        await scheduler.WhenIdleAsync();
        Assert.Equal([42L], runner.Ran);
    }

    [Fact]
    public async Task TheWorker_RunsRefreshesOneAtATime_NeverTogether()
    {
        var (scheduler, runner, lifetime) = Worker(); using var _ = lifetime;
        runner.Hold = new TaskCompletionSource();
        scheduler.EnqueueRefresh(1); scheduler.EnqueueRefresh(2); scheduler.EnqueueRefresh(3);
        await Task.Delay(100);
        Assert.Equal(1, runner.Concurrent);                                                            // only one is inside the refresh; the others wait their turn
        runner.Hold.SetResult();
        await scheduler.WhenIdleAsync();
        await Task.Delay(100);
        Assert.Equal(1, runner.MaxConcurrent); Assert.Equal(3, runner.Ran.Count);
    }

    [Fact]
    public async Task AFailingRefresh_NeverCrashesTheWorker_AndTheNextOneStillRuns()
    {
        var (scheduler, runner, lifetime) = Worker(); using var _ = lifetime;
        runner.Failure = new InvalidOperationException("boom");
        scheduler.EnqueueRefresh(1); await scheduler.WhenIdleAsync();
        runner.Failure = null;
        scheduler.EnqueueRefresh(2); await scheduler.WhenIdleAsync();
        Assert.Equal([1L, 2L], runner.Ran.Order());
    }

    [Fact]
    public async Task WhenTheServerStops_ARefreshInFlightIsCancelled_AndWaitingOnesNeverStart()
    {
        var (scheduler, runner, lifetime) = Worker(); using var _ = lifetime;
        runner.Hold = new TaskCompletionSource();
        scheduler.EnqueueRefresh(1); scheduler.EnqueueRefresh(2);
        await Task.Delay(100);
        lifetime.StopApplication();
        await scheduler.WhenIdleAsync();
        await Task.Delay(100);
        Assert.Empty(runner.Ran);                                                                      // the run is left for the stuck-run timeout to close
    }

    [Fact]
    public void WithBackgroundJobsOff_TheApiUsesTheInProcessWorker_NotAnImpossibleQueue()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundJobs:Enabled"] = "false" }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IHostApplicationLifetime>(new Lifetime());
        services.AddTigerCsBackgroundJobs(config);
        using var provider = services.BuildServiceProvider();
        var scheduler = provider.GetRequiredService<IReviewJobScheduler>();
        Assert.IsType<InProcessReviewJobScheduler>(scheduler);
        Assert.False(scheduler.IsAvailable); Assert.True(scheduler.CanRunRefresh);
    }
}
