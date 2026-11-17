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
/// <c>QueueingIntegration:EventPayloadRetentionDays</c> (default 90), in batches. It only schedules: <see cref="QueueingEventProcessor.PurgeExpiredPayloadsAsync"/> owns the rule
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
