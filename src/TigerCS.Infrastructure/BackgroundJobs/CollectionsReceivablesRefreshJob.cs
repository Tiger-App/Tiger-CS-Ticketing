using Hangfire;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Reconciliation;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Recurring refresh of ONE company of the local PACT receivables snapshot (SQL Server Agent is not used). Dubai (4) and Sharjah (32) are separate recurring jobs, so one that is slow,
/// hung or failing never delays the other. The job is a thin caller: overlap protection, the PACT fetch, validation, the atomic publish and "keep the previous snapshot on failure" live in
/// dbo.usp_CollectionsRcv_Refresh. Hangfire retries are disabled because the next cron tick (or the Retry button) is the retry. A SUCCESSFUL refresh is followed, in the same job, by the
/// snapshot-consistency check of that company (<see cref="IPostRefreshReconciliation"/>): the evidence then always describes the publish in use.
/// </summary>
public sealed class CollectionsReceivablesRefreshJob(IReceivablesRefresher refresher, ILogger<CollectionsReceivablesRefreshJob> logger, IPostRefreshReconciliation? reconciliation = null)
{
    /// <summary>The single recurring job of the previous pipeline; removed at start-up so the old schedule stops.</summary>
    public const string LegacyRecurringJobId = "collections-receivables-refresh";

    public static string RecurringJobId(int companyId) => $"collections-receivables-refresh-{companyId}";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(int companyId, CancellationToken cancellationToken)
    {
        var result = await refresher.RefreshAsync("Hangfire", companyId, cancellationToken);
        foreach (var company in result.Companies)
        {
            if (company.Status == "Succeeded")
                logger.LogInformation("Receivables refresh company {CompanyId}: {Published} unpaid payments published ({Raw} read, {Settled} settled, {Excluded} invalid or cancelled units); fetch from PACT {FetchMs} ms, publish {PublishMs} ms.",
                    company.CompanyId, company.PublishedRows, company.RawRows, company.ExcludedZeroRows, company.ExcludedInvalidUnitRows, company.FetchMs, company.PublishMs);
            else if (company.Status == "AlreadyRunning")
                logger.LogInformation("Receivables refresh company {CompanyId} skipped: {Message}", company.CompanyId, company.ErrorMessage);
            else
                logger.LogWarning("Receivables refresh company {CompanyId} FAILED (error {ErrorNumber}): {ErrorMessage}. The previous snapshot (if any) is still served.", company.CompanyId, company.ErrorNumber, company.ErrorMessage);
        }
        if (result.Status is "Failed" or "PartialFailure")
            throw new InvalidOperationException($"Receivables refresh of company {companyId} failed: {result.Message}");
        await ReconcileAfterAsync(reconciliation, result, companyId, logger, cancellationToken);
    }

    /// <summary>The check that follows a successful refresh of <paramref name="companyId"/>; its own failures never fail the refresh (the snapshot is published either way).</summary>
    internal static async Task ReconcileAfterAsync(IPostRefreshReconciliation? reconciliation, ReceivablesRefreshResult result, int companyId, ILogger logger, CancellationToken cancellationToken)
    {
        if (reconciliation is null || !result.Companies.Any(c => c.CompanyId == companyId && c.Status == "Succeeded")) return;
        var run = await reconciliation.RunAfterRefreshAsync(companyId, cancellationToken);
        if (run is null) return;
        var company = run.Companies.FirstOrDefault(c => c.CompanyId == companyId);
        logger.LogInformation("Snapshot-consistency check of company {CompanyId} after its refresh: {Status}{Detail}", companyId, company?.Status ?? run.Status,
            company?.Message is { } m ? $" ({m})" : "");
    }
}
