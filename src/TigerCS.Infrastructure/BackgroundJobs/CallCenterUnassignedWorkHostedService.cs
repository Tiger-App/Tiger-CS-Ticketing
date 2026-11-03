using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs <see cref="CallCenterUnassignedWorkJob"/> on a timer, INDEPENDENTLY of Hangfire: it needs neither <c>BackgroundJobs:Enabled</c> nor
/// any Hangfire storage, so enabling the reconciler never starts the SLA / Collections / outbox machinery. While
/// <c>CallCenterReconciliation:Enabled</c> is false (the default) it does nothing but re-read the setting once a minute.
/// </summary>
public sealed class CallCenterUnassignedWorkHostedService(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<CallCenterReconciliationOptions> options, ILogger<CallCenterUnassignedWorkHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Call Center reconciliation: Enabled={Enabled}, DryRun={DryRun} (off and dry-run unless configured otherwise).",
            options.CurrentValue.Enabled, options.CurrentValue.DryRun);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);   // let the host finish starting before any work

            while (!stoppingToken.IsCancellationRequested)
            {
                var current = options.CurrentValue;
                var wait = TimeSpan.FromMinutes(1);
                if (current.Enabled)
                {
                    wait = TimeSpan.FromMinutes(Math.Clamp(current.IntervalMinutes, 1, 60));
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<CallCenterUnassignedWorkJob>().RunAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Call Center reconciliation run failed; it is retried at the next interval.");
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
