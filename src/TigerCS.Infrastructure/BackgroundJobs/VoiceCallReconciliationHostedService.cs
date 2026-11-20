using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.GenesysIntegration.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs <see cref="VoiceCallReconciliationJob"/> on a timer, independently of Hangfire and of the generic Call Center reconciler (its own section, switch and
/// interval): a failed-AI voice call must be dealt to an agent once its reconciliation window passes whether or not <c>BackgroundJobs:Enabled</c> is set.
/// </summary>
public sealed class VoiceCallReconciliationHostedService(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<VoiceCallReconciliationOptions> options, ILogger<VoiceCallReconciliationHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var start = options.CurrentValue;
        logger.LogInformation(
            "Voice call reconciliation: Enabled={Enabled}, DryRun={DryRun}, NoAnswerWindowSeconds={Window}, LookbackHours={Lookback}, IntervalSeconds={Interval}.",
            start.Enabled, start.DryRun, start.NoAnswerWindowSeconds, start.LookbackHours, start.IntervalSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);   // let the host finish starting before any work

            while (!stoppingToken.IsCancellationRequested)
            {
                var current = options.CurrentValue;
                var wait = TimeSpan.FromMinutes(1);
                if (current.Enabled)
                {
                    wait = TimeSpan.FromSeconds(Math.Clamp(current.IntervalSeconds, 15, 3600));
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<VoiceCallReconciliationJob>().RunAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Voice call reconciliation run failed; it is retried at the next interval.");
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
