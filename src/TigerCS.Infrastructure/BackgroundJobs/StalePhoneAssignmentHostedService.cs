using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs <see cref="StalePhoneAssignmentJob"/> on a timer, independently of Hangfire and of the Call Center reconciler (its own section, switch and interval).
/// While <c>StalePhoneAssignment:Enabled</c> is false (the default) it only re-reads the setting once a minute.
/// </summary>
public sealed class StalePhoneAssignmentHostedService(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<StalePhoneAssignmentOptions> options, ILogger<StalePhoneAssignmentHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Stale Phone assignment: Enabled={Enabled}, DryRun={DryRun}, StaleAfterHours={Hours} (off and dry-run unless configured otherwise).",
            options.CurrentValue.Enabled, options.CurrentValue.DryRun, options.CurrentValue.StaleAfterHours);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);   // let the host finish starting before any work

            while (!stoppingToken.IsCancellationRequested)
            {
                var current = options.CurrentValue;
                var wait = TimeSpan.FromMinutes(1);
                if (current.Enabled)
                {
                    wait = TimeSpan.FromMinutes(Math.Clamp(current.IntervalMinutes, 1, 240));
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<StalePhoneAssignmentJob>().RunAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Stale Phone assignment run failed; it is retried at the next interval.");
                    }
                }

                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
