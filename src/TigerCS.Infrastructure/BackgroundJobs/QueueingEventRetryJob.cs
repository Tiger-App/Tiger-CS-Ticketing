using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.QueueingIntegration.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// The controlled retry of unresolved Queueing System events (ticket not created yet, a mapping or an employee not set up yet). It only schedules:
/// <see cref="QueueingEventProcessor.RetryDueAsync"/> owns every rule. One scope (one DbContext) per run, so a lost race never poisons the next batch.
/// </summary>
public sealed class QueueingEventRetryJob(IServiceScopeFactory scopeFactory, ILogger<QueueingEventRetryJob> logger)
{
    public const string RecurringJobId = "queueing-event-retry";

    private const int BatchSize = 50;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settled = await scope.ServiceProvider.GetRequiredService<QueueingEventProcessor>().RetryDueAsync(BatchSize, cancellationToken);
            if (settled > 0)
            {
                logger.LogInformation("Queueing retry sweep settled {Count} event(s).", settled);
            }

            // The alert for blocked queue tickets: the oldest unresolved event of a ticket (a missing mapping or employee, or a Failed event waiting for an administrator)
            // holds every later event of that ticket. Counts, ages and outcome codes only - never event content - so a log-based alert can fire on it.
            var options = scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.QueueingIntegrationOptions>();
            var snapshot = await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.Abstractions.IQueueingStore>()
                .GetMonitoringSnapshotAsync(
                    scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, 5, TimeSpan.FromMinutes(Math.Max(1, options.BlockedAlertAfterMinutes)), cancellationToken);
            if (snapshot.AlertCount > 0)
            {
                var worst = snapshot.BlockedTickets.FirstOrDefault(b => b.Alert);
                logger.LogWarning(
                    "Queueing integration ALERT: {AlertCount} queue ticket(s) blocked (of {Blocked}); the oldest has waited {AgeSeconds}s on {Outcome} ({FailureClass}, {Status}). Fix the mapping / employee, or reprocess a Failed event.",
                    snapshot.AlertCount, snapshot.BlockedTicketCount, snapshot.OldestBlockedAgeSeconds, worst?.HeadOutcomeCode, worst?.FailureClass, worst?.HeadStatus);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Counts and the exception type only - never event content.
            logger.LogError("Queueing retry sweep failed ({ExceptionType}); it runs again next minute.", ex.GetType().Name);
        }
    }
}

/// <summary>
/// The daily retention sweep for the Queueing System event inbox: erases the payload of successfully processed events older than
/// <c>QueueingIntegration:PayloadRetentionDays</c> (default 90), in batches. It only schedules: <see cref="QueueingEventProcessor.PurgeExpiredPayloadsAsync"/> owns the rule
/// (and never touches an unresolved event, an identifier or the ticket history).
/// </summary>
public sealed class QueueingPayloadRetentionJob(IServiceScopeFactory scopeFactory, ILogger<QueueingPayloadRetentionJob> logger)
{
    public const string RecurringJobId = "queueing-payload-retention";

    private const int BatchSize = 500;
    private const int MaxBatchesPerRun = 200;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var total = 0;
            for (var batch = 0; batch < MaxBatchesPerRun; batch++)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var purged = await scope.ServiceProvider.GetRequiredService<QueueingEventProcessor>().PurgeExpiredPayloadsAsync(BatchSize, cancellationToken);
                total += purged;
                if (purged < BatchSize)
                {
                    break;
                }
            }

            if (total > 0)
            {
                logger.LogInformation("Queueing payload retention erased {Count} expired payload(s).", total);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Queueing payload retention sweep failed ({ExceptionType}); it runs again tomorrow.", ex.GetType().Name);
        }
    }
}
