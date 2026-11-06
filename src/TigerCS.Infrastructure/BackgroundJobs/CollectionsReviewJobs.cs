using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>The explicit "refresh review data" job. No automatic retry: a failed run is reported and a person starts a new one.</summary>
public sealed class CollectionsReviewRefreshJob(ReviewRefreshService refresh)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(long runId, CancellationToken cancellationToken) => refresh.RunAsync(runId, cancellationToken);
}

/// <summary>
/// Revalidates and uploads one approved dispatch. Safe to execute twice: the dispatch lease admits one worker, batches are claimed
/// atomically, and a batch whose outcome is unknown is never resent.
/// </summary>
public sealed class CollectionsDispatchJob(DispatchService dispatch, DispatchAdminReportService adminReport)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900])]
    public async Task RunAsync(long dispatchId, CancellationToken cancellationToken)
    {
        await dispatch.ExecuteAsync(dispatchId, cancellationToken);
        // After the Genesys work is stored. The report never throws and never touches Genesys, so a mail problem cannot trigger another upload through a job retry.
        await adminReport.SendAfterDispatchAsync(dispatchId, cancellationToken);
    }
}

/// <summary>
/// Marks uploaded contacts not callable when their balance was paid or changed. Safe to overlap or repeat: each update is an idempotent PUT and
/// an in-process guard allows one sweep at a time.
/// </summary>
public sealed class CollectionsSuppressionJob(SuppressionService suppression, ILogger<CollectionsSuppressionJob> logger)
{
    public const string RecurringJobId = "collections-genesys-suppression";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await suppression.SweepAsync(cancellationToken);
        if (!result.Ran) logger.LogInformation("Suppression sweep skipped: {Detail}", result.Detail);
    }
}

/// <summary>
/// Auto sending mode: on a scheduled date prepares and dispatches the eligible campaigns. Does nothing unless <c>AutoSendingEnabled</c> is true (the recurring job is not even registered then).
/// No automatic retry: a dispatch is idempotent per campaign and day, and the job runs again within the hour.
/// </summary>
public sealed class CollectionsAutoDispatchJob(AutoDispatchService autoDispatch, ILogger<CollectionsAutoDispatchJob> logger)
{
    public const string RecurringJobId = "collections-auto-dispatch";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await autoDispatch.RunAsync(cancellationToken);
        if (!result.Ran) logger.LogInformation("Automatic dispatch skipped: {Detail}", result.Detail);
    }
}

public sealed class HangfireReviewJobScheduler(IBackgroundJobClient client) : IReviewJobScheduler
{
    public void EnqueueRefresh(long runId) => client.Enqueue<CollectionsReviewRefreshJob>(j => j.RunAsync(runId, CancellationToken.None));

    public void EnqueueDispatch(long dispatchId) => client.Enqueue<CollectionsDispatchJob>(j => j.RunAsync(dispatchId, CancellationToken.None));

    public void EnqueueSuppressionSweep() => client.Enqueue<CollectionsSuppressionJob>(j => j.RunAsync(CancellationToken.None));
}

/// <summary>
/// Used when Hangfire is switched off (<c>BackgroundJobs:Enabled</c> false). A person can still refresh the approval data: that one explicit, authorized request runs on a single worker inside
/// this process, one refresh at a time, bounded by the refresh's own time budget and cancelled when the host stops. Nothing recurs, and nothing else changes: a dispatch (the upload to Genesys) and
/// the paid-after-upload sweep still need real background jobs, so with jobs off nothing can be sent. The refresh is the same <see cref="ReviewRefreshService"/> as with Hangfire, so the financial
/// reconciliation gate, the sign-off and every validation are unchanged.
/// </summary>
public sealed class InProcessReviewJobScheduler(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<InProcessReviewJobScheduler> logger) : IReviewJobScheduler
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _sync = new();
    private Task _latest = Task.CompletedTask;

    /// <summary>False: a dispatch (and the suppression sweep) need real background jobs, so an approved send cannot be queued while they are off.</summary>
    public bool IsAvailable => false;
    public bool CanRunRefresh => true;
    public bool RefreshRunsInProcess => true;

    public void EnqueueRefresh(long runId)
    {
        var stopping = lifetime.ApplicationStopping;
        lock (_sync) _latest = Task.Run(() => RunAsync(runId, stopping), CancellationToken.None);
    }

    private async Task RunAsync(long runId, CancellationToken stopping)
    {
        try
        {
            await _oneAtATime.WaitAsync(stopping);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IReviewRefreshRunner>().RunAsync(runId, stopping);
            }
            finally { _oneAtATime.Release(); }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            logger.LogWarning("Review refresh {RunId} was cancelled because the server is stopping; the stuck-run timeout will close it.", runId);
        }
        catch (Exception ex)
        {
            // The refresh records its own failures on the run. This only guards the worker: a run that could not even start is closed by the stuck-run timeout.
            logger.LogError(ex, "Review refresh {RunId} failed in the in-process worker ({ExceptionType}).", runId, ex.GetType().Name);
        }
    }

    /// <summary>Completes when the most recently started refresh has finished (graceful shutdown and tests).</summary>
    public Task WhenIdleAsync() { lock (_sync) return _latest; }

    public void EnqueueDispatch(long dispatchId) => logger.LogWarning("Background jobs are disabled; dispatch {DispatchId} stays queued.", dispatchId);

    public void EnqueueSuppressionSweep() => logger.LogWarning("Background jobs are disabled; the suppression sweep did not run.");
}

/// <summary>Used when Hangfire is switched off: work stays Queued and is reported as such. Nothing runs silently.</summary>
public sealed class NoOpReviewJobScheduler(ILogger<NoOpReviewJobScheduler> logger) : IReviewJobScheduler
{
    public bool IsAvailable => false;

    public void EnqueueRefresh(long runId) => logger.LogWarning("Background jobs are disabled; review refresh {RunId} stays queued.", runId);

    public void EnqueueDispatch(long dispatchId) => logger.LogWarning("Background jobs are disabled; dispatch {DispatchId} stays queued.", dispatchId);

    public void EnqueueSuppressionSweep() => logger.LogWarning("Background jobs are disabled; the suppression sweep did not run.");
}
