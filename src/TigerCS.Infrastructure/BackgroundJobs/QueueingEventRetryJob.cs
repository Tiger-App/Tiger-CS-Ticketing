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
