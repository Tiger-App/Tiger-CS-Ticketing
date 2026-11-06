using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.GenesysIntegration.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// The recurring job behind "close AI-only tickets after 18 hours". It only schedules and isolates:
/// <see cref="AiOnlyTicketAutoCloseAppService"/> owns every rule. Runs every 15 minutes, in small keyset-paged batches
/// (<see cref="AiOnlyAutoCloseOptions.BatchSize"/> × <see cref="AiOnlyAutoCloseOptions.MaxBatchesPerRun"/> at most per run), so existing backlog
/// drains over successive runs without a CPU or database spike. One DI scope (one DbContext) per ticket: a lost race or failure on one ticket
/// cannot poison the next, and is simply re-evaluated by the next run. Logs counts and ticket ids only.
/// </summary>
public sealed class AiOnlyAutoCloseJob(IServiceScopeFactory scopeFactory, ILogger<AiOnlyAutoCloseJob> logger)
{
    /// <summary>The Hangfire recurring-job identifier.</summary>
    public const string RecurringJobId = "ai-only-auto-close";

    /// <summary>Every 15 minutes.</summary>
    public const string Cron = "*/15 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int closed = 0, notEligible = 0, lostRace = 0, failed = 0, examined = 0;
        long cursor = 0;

        int maxBatches;
        await using (var optionsScope = scopeFactory.CreateAsyncScope())
        {
            maxBatches = optionsScope.ServiceProvider.GetRequiredService<AiOnlyAutoCloseOptions>().EffectiveMaxBatches;
        }

        for (var batch = 0; batch < maxBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<long> candidates;
            await using (var listScope = scopeFactory.CreateAsyncScope())
            {
                candidates = await listScope.ServiceProvider
                    .GetRequiredService<AiOnlyTicketAutoCloseAppService>()
                    .ListCandidatesAsync(cursor, cancellationToken);
            }

            if (candidates.Count == 0)
            {
                break;
            }

            foreach (var ticketId in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = ticketId;
                examined++;

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var outcome = await scope.ServiceProvider
                        .GetRequiredService<AiOnlyTicketAutoCloseAppService>()
                        .TryCloseAsync(ticketId, cancellationToken);

                    switch (outcome)
                    {
                        case AiOnlyAutoCloseOutcome.Closed: closed++; break;
                        case AiOnlyAutoCloseOutcome.LostRace: lostRace++; break;
                        default: notEligible++; break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(ex, "AI-only auto-close failed for ticket {TicketId}; it will be re-evaluated on the next run.", ticketId);
                }
            }
        }

        if (examined > 0)
        {
            logger.LogInformation(
                "AI-only auto-close pass: {Examined} examined, {Closed} closed, {NotEligible} not eligible, {LostRace} lost to a concurrent writer, {Failed} failed.",
                examined, closed, notEligible, lostRace, failed);
        }
    }
}
