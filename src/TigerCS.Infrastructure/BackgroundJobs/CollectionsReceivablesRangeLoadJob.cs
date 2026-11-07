using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>Retry of ONE company (4 or 32) over the standard window, e.g. after its scheduled refresh failed.</summary>
public sealed class CollectionsReceivablesCompanyRetryJob(IReceivablesRefresher refresher, ILogger<CollectionsReceivablesCompanyRetryJob> logger, IPostRefreshReconciliation? reconciliation = null)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(int companyId, CancellationToken cancellationToken)
    {
        var result = await refresher.RefreshAsync("Retry", companyId, cancellationToken);
        logger.LogInformation("Receivables retry for company {CompanyId}: {Status}.", companyId, result.Status);
        foreach (var company in result.Companies.Where(c => c.Status == "Failed"))
            logger.LogWarning("Receivables retry company {CompanyId} FAILED (error {ErrorNumber}): {ErrorMessage}", company.CompanyId, company.ErrorNumber, company.ErrorMessage);
        if (result.Status is "PartialFailure" or "Failed")
            throw new InvalidOperationException($"Receivables retry for company {companyId} {result.Status}; the previous snapshot is unchanged.");
        await CollectionsReceivablesRefreshJob.ReconcileAfterAsync(reconciliation, result, companyId, logger, cancellationToken);
    }
}

/// <summary>Runs a manual refresh as a Hangfire background job (BackgroundJobs:Enabled).</summary>
public sealed class HangfireReceivablesRangeLoader(IBackgroundJobClient client) : IReceivablesRangeLoader
{
    // The snapshot always holds the whole configured window, so "load a range" simply means "refresh both companies".
    public Task<bool> EnqueueAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken)
    {
        client.Enqueue<CollectionsReceivablesCompanyRetryJob>(job => job.RunAsync(4, CancellationToken.None));
        client.Enqueue<CollectionsReceivablesCompanyRetryJob>(job => job.RunAsync(32, CancellationToken.None));
        return Task.FromResult(true);
    }

    public Task<bool> EnqueueCompanyRefreshAsync(int companyId, CancellationToken cancellationToken)
    {
        client.Enqueue<CollectionsReceivablesCompanyRetryJob>(job => job.RunAsync(companyId, CancellationToken.None));
        return Task.FromResult(true);
    }
}

/// <summary>
/// Fallback when Hangfire is disabled: runs the same refresh on a background task of this process (not tied to the request). The company's application lock still prevents overlap;
/// a restart abandons a running refresh (the next one supersedes it). Note that nothing is SCHEDULED without Hangfire - see docs/Collections/Receivables-Snapshot.md.
/// </summary>
public sealed class InProcessReceivablesRangeLoader(IServiceScopeFactory scopes, ILogger<InProcessReceivablesRangeLoader> logger) : IReceivablesRangeLoader
{
    public async Task<bool> EnqueueAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken)
    {
        await EnqueueCompanyRefreshAsync(4, cancellationToken);
        return await EnqueueCompanyRefreshAsync(32, cancellationToken);
    }

    public Task<bool> EnqueueCompanyRefreshAsync(int companyId, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IReceivablesRefresher>().RefreshAsync("Retry", companyId, CancellationToken.None);
                logger.LogInformation("Receivables retry for company {CompanyId}: {Status}.", companyId, result.Status);
                foreach (var company in result.Companies.Where(c => c.Status == "Failed"))
                    logger.LogWarning("Receivables retry company {CompanyId} FAILED (error {ErrorNumber}): {ErrorMessage}", company.CompanyId, company.ErrorNumber, company.ErrorMessage);
                await CollectionsReceivablesRefreshJob.ReconcileAfterAsync(scope.ServiceProvider.GetService<IPostRefreshReconciliation>(), result, companyId, logger, CancellationToken.None);
            }
            catch (Exception ex) { logger.LogError(ex, "Receivables retry for company {CompanyId} failed.", companyId); }
        }, CancellationToken.None);
        return Task.FromResult(true);
    }
}

/// <summary>Recurring reload of the bulk CRM owner feed (who holds an eligible sale of each unit). A failure keeps the previous data; the next tick retries.</summary>
public sealed class CollectionsCrmOwnersRefreshJob(CollectionsCrmOwnersRefreshService service, ILogger<CollectionsCrmOwnersRefreshJob> logger)
{
    public const string RecurringJobId = "collections-crm-owners-refresh";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await service.RefreshAsync(cancellationToken);
        if (result.Succeeded) logger.LogInformation("CRM owners refreshed: {Stored} stored of {Fetched} fetched.", result.Stored, result.Fetched);
        else throw new InvalidOperationException($"CRM owners refresh failed: {result.Message}");
    }
}
