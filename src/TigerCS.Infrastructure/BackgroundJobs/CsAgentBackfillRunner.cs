using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

public sealed record CsAgentBackfillRunRequest(bool DryRun = true, int? BatchSize = null, long AfterTicketId = 0, string? BatchId = null);

public enum CsAgentBackfillRunStatus
{
    /// <summary>The batch ran (or, for a dry run, was simulated) to its size limit or to the end of the backlog.</summary>
    Completed,
    /// <summary>A LIVE run was requested but <c>CsAgentBackfill:Enabled</c> is false. Nothing was done.</summary>
    Disabled,
    /// <summary>Another backfill / reconciliation run holds the single-flight lock. Nothing was done.</summary>
    Busy,
    /// <summary>A LIVE run found no eligible CS Agent in the shared pool, before touching any ticket. Nothing was written.</summary>
    NoEligiblePool,
    /// <summary>The rotation ran out of eligible agents mid-run; the run stopped at that ticket (its failure is recorded).</summary>
    StoppedNoEligibleAgent,
}

public sealed record CsAgentBackfillRunResult(
    string BatchId, bool DryRun, CsAgentBackfillRunStatus Status, int Examined, int Eligible, int Assigned, int WouldAssign, int LostRace,
    int NoLongerApplicable, int Failed, IReadOnlyDictionary<string, int> Excluded, long ResumeAfterTicketId, bool BacklogExhausted,
    IReadOnlyList<CsAgentBackfillTicketResult> Tickets);

/// <summary>
/// Throttled, resumable execution of the CS Agent backfill. It only sequences and isolates - <see cref="CsAgentBackfillService"/> owns every
/// rule. Manual only (called by the admin endpoint): nothing schedules it. One DI scope (one DbContext) and one transaction per ticket, the
/// SQL application lock keeps runs from overlapping (including with the reconciler), and the result carries <c>ResumeAfterTicketId</c> so an
/// interrupted run continues where it stopped; re-running from 0 is also safe because assigned tickets are no longer candidates.
/// </summary>
public sealed class CsAgentBackfillRunner(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<CsAgentBackfillOptions> options,
    ICallCenterReconciliationRunLock runLock, ILogger<CsAgentBackfillRunner> logger)
{
    public async Task<CsAgentBackfillRunResult> RunAsync(CsAgentBackfillRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.CurrentValue;
        var batchId = string.IsNullOrWhiteSpace(request.BatchId) ? Guid.NewGuid().ToString("N") : request.BatchId.Trim();
        var batchSize = Math.Clamp(request.BatchSize ?? settings.DefaultBatchSize, 1, Math.Max(1, settings.MaxBatchSize));
        var after = Math.Max(0, request.AfterTicketId);

        CsAgentBackfillRunResult Empty(CsAgentBackfillRunStatus status) =>
            new(batchId, request.DryRun, status, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), after, false, []);

        if (!request.DryRun && !settings.Enabled)
        {
            logger.LogWarning("CS Agent backfill batch {BatchId}: a LIVE run was refused because CsAgentBackfill:Enabled is false.", batchId);
            return Empty(CsAgentBackfillRunStatus.Disabled);
        }

        await using var handle = await runLock.TryAcquireAsync(cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("CS Agent backfill batch {BatchId}: another run holds the lock; nothing done.", batchId);
            return Empty(CsAgentBackfillRunStatus.Busy);
        }

        if (!request.DryRun)
        {
            await using var poolScope = scopeFactory.CreateAsyncScope();
            var (_, pool, _) = await poolScope.ServiceProvider.GetRequiredService<TicketAutoAssignmentService>().GetSharedPoolAsync(cancellationToken);
            if (pool.Count == 0)
            {
                logger.LogError("CS Agent backfill batch {BatchId}: no eligible CS Agent in the shared pool; nothing written.", batchId);
                return Empty(CsAgentBackfillRunStatus.NoEligiblePool);
            }
        }

        logger.LogInformation("CS Agent backfill batch {BatchId} started ({Mode}); batch size {Size}, after ticket {After}.",
            batchId, request.DryRun ? "DRY RUN - nothing written" : "LIVE", batchSize, after);

        IReadOnlyList<UnassignedTicketEvidence> page;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            page = await listScope.ServiceProvider.GetRequiredService<ITicketUnassignedWorkRepository>()
                .ListActiveUnownedAsync(after, batchSize, cancellationToken);
        }

        var excluded = new Dictionary<string, int>();
        var tickets = new List<CsAgentBackfillTicketResult>();
        int eligible = 0, assigned = 0, wouldAssign = 0, lostRace = 0, noLonger = 0, failed = 0;
        var status = CsAgentBackfillRunStatus.Completed;
        var resume = after;

        foreach (var evidence in page)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<CsAgentBackfillService>()
                    .TryBackfillAsync(evidence.TicketId, batchId, settings, request.DryRun, cancellationToken);
                tickets.Add(result);

                switch (result.Outcome)
                {
                    case CsAgentBackfillOutcome.Skipped:
                        excluded[result.Class.ToString()] = excluded.GetValueOrDefault(result.Class.ToString()) + 1;
                        break;
                    case CsAgentBackfillOutcome.WouldAssign:
                        eligible++; wouldAssign++;
                        break;
                    case CsAgentBackfillOutcome.Assigned:
                        eligible++; assigned++;
                        logger.LogInformation("Backfill batch {BatchId}: ticket {TicketNumber} (#{TicketId}) -> employee {EmployeeId}.",
                            batchId, evidence.TicketNumber, evidence.TicketId, result.AssignedEmployeeId);
                        if (settings.DelayBetweenTicketsMs > 0) await Task.Delay(settings.DelayBetweenTicketsMs, cancellationToken);
                        break;
                    case CsAgentBackfillOutcome.LostRace:
                        eligible++; lostRace++;
                        break;
                    case CsAgentBackfillOutcome.NoEligibleAgent:
                        eligible++; failed++;
                        status = CsAgentBackfillRunStatus.StoppedNoEligibleAgent;
                        break;
                    default:
                        noLonger++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Backfill batch {BatchId}: ticket {TicketNumber} (#{TicketId}) FAILED; it stays a candidate for the next run.",
                    batchId, evidence.TicketNumber, evidence.TicketId);
            }

            if (status == CsAgentBackfillRunStatus.StoppedNoEligibleAgent)
            {
                break;   // resume point stays BEFORE this ticket, so the next run retries it
            }

            resume = evidence.TicketId;
        }

        var exhausted = status == CsAgentBackfillRunStatus.Completed && page.Count < batchSize;
        logger.LogInformation(
            "CS Agent backfill batch {BatchId} finished ({Status}, {Mode}): examined {Examined}; eligible {Eligible}; assigned {Assigned}; would-assign {Would}; lost race {Lost}; "
            + "no longer applicable {NoLonger}; failed {Failed}; excluded [{Excluded}]; resume after #{Resume}; backlog exhausted {Exhausted}.",
            batchId, status, request.DryRun ? "dry run" : "live", tickets.Count, eligible, assigned, wouldAssign, lostRace, noLonger, failed,
            string.Join(", ", excluded.Select(p => $"{p.Key}={p.Value}")), resume, exhausted);

        return new CsAgentBackfillRunResult(
            batchId, request.DryRun, status, tickets.Count, eligible, assigned, wouldAssign, lostRace, noLonger, failed, excluded, resume, exhausted, tickets);
    }
}
