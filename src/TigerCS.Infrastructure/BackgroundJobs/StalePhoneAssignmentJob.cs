using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

public enum StalePhoneAssignmentRunStatus
{
    /// <summary>The pass ran (or, for a dry run, was simulated).</summary>
    Completed,
    /// <summary><c>StalePhoneAssignment:Enabled</c> is false, or the threshold is null / non-positive. Nothing was done.</summary>
    Disabled,
    /// <summary>Another backfill / reconciliation / stale-phone run holds the single-flight lock. Nothing was done.</summary>
    Busy,
    /// <summary>A LIVE pass found no eligible CS Agent before touching any ticket. Nothing was written.</summary>
    NoEligiblePool,
    /// <summary>The rotation ran out of eligible agents mid-run; the pass stopped at that ticket.</summary>
    StoppedNoEligibleAgent,
}

public sealed record StalePhoneAssignmentRunResult(
    StalePhoneAssignmentRunStatus Status, bool DryRun, int Examined, int StalePhoneTickets, int Assigned, int WouldAssign, int LostRace,
    int NoLongerApplicable, int Failed, bool LimitReached);

/// <summary>
/// One pass of the recurring stale-Phone assignment. It only sequences and isolates: <see cref="CsAgentBackfillClassifier.IsStalePhoneAi"/> owns the rule and
/// <see cref="CsAgentBackfillService.TryBackfillAsync"/> re-checks it under the ticket's transaction and assigns through the shared CS Agent rotation, so a
/// customer message, a Genesys event, a human pick-up or an approval that arrives meanwhile protects the ticket again. It acts ONLY on tickets the
/// stale-Phone rule releases - never on any other unowned ticket - and never replaces an owner, writes an end timestamp, closes or moves a ticket.
/// Off and dry-run by default; shares the run lock with the backfill and the reconciler so runs never overlap. Logs ticket ids / numbers only.
/// </summary>
public sealed class StalePhoneAssignmentJob(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<StalePhoneAssignmentOptions> options, ICallCenterReconciliationRunLock runLock,
    TimeProvider timeProvider, ILogger<StalePhoneAssignmentJob> logger)
{
    private const int MaxPagesPerRun = 20;

    public async Task<StalePhoneAssignmentRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var backfillOptions = settings.ToBackfillOptions();

        StalePhoneAssignmentRunResult Empty(StalePhoneAssignmentRunStatus status) => new(status, settings.DryRun, 0, 0, 0, 0, 0, 0, 0, false);

        if (!settings.Enabled || backfillOptions.StalePhoneAiThreshold is null)
        {
            return Empty(StalePhoneAssignmentRunStatus.Disabled);
        }

        await using var handle = await runLock.TryAcquireAsync(cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Stale Phone assignment: another run is in progress; this run is skipped (runs never overlap).");
            return Empty(StalePhoneAssignmentRunStatus.Busy);
        }

        if (!settings.DryRun)
        {
            await using var poolScope = scopeFactory.CreateAsyncScope();
            var (_, pool, _) = await poolScope.ServiceProvider.GetRequiredService<TicketAutoAssignmentService>().GetSharedPoolAsync(cancellationToken);
            if (pool.Count == 0)
            {
                logger.LogError("Stale Phone assignment: no eligible CS Agent in the shared pool; nothing written.");
                return Empty(StalePhoneAssignmentRunStatus.NoEligiblePool);
            }
        }

        var batchId = Guid.NewGuid().ToString("N");
        var mode = settings.DryRun ? "DRY RUN - nothing written" : "LIVE";
        var pageSize = Math.Clamp(settings.BatchSize, 1, 1000);
        var limit = Math.Max(0, settings.MaxAssignmentsPerRun);
        long after = 0;
        int examined = 0, stale = 0, assigned = 0, wouldAssign = 0, lostRace = 0, noLonger = 0, failed = 0;
        var limitReached = false;
        var status = StalePhoneAssignmentRunStatus.Completed;

        logger.LogInformation("Stale Phone assignment {BatchId} started ({Mode}): threshold {Hours}h, page {Page}, max assignments per run {Max}.",
            batchId, mode, settings.StaleAfterHours, pageSize, limit);

        for (var pageNumber = 0; pageNumber < MaxPagesPerRun && !limitReached && status == StalePhoneAssignmentRunStatus.Completed; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<UnassignedTicketEvidence> page;
            await using (var listScope = scopeFactory.CreateAsyncScope())
            {
                page = await listScope.ServiceProvider.GetRequiredService<ITicketUnassignedWorkRepository>()
                    .ListActiveUnownedAsync(after, pageSize, cancellationToken);
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (var evidence in page)
            {
                after = Math.Max(after, evidence.TicketId);
                examined++;

                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (CsAgentBackfillClassifier.Classify(evidence, now, backfillOptions) != CsAgentBackfillClass.Eligible
                    || !CsAgentBackfillClassifier.IsStalePhoneAi(evidence, now, backfillOptions))
                {
                    continue;   // not a stale Phone ticket: protected, owned, or somebody else's job - never touched here
                }

                if (assigned + wouldAssign >= limit)
                {
                    limitReached = true;
                    break;
                }

                stale++;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<CsAgentBackfillService>()
                        .TryBackfillAsync(evidence.TicketId, batchId, backfillOptions, settings.DryRun, cancellationToken);

                    switch (result.Outcome)
                    {
                        case CsAgentBackfillOutcome.WouldAssign:
                            wouldAssign++;
                            logger.LogInformation("DRY RUN: would assign stale Phone ticket {TicketNumber} (#{TicketId}), last activity {LastActivityUtc:O}.",
                                evidence.TicketNumber, evidence.TicketId, evidence.LastActivityUtc);
                            break;
                        case CsAgentBackfillOutcome.Assigned:
                            assigned++;
                            logger.LogInformation("Stale Phone assignment {BatchId}: ticket {TicketNumber} (#{TicketId}) -> employee {EmployeeId} (last activity {LastActivityUtc:O}).",
                                batchId, evidence.TicketNumber, evidence.TicketId, result.AssignedEmployeeId, evidence.LastActivityUtc);
                            if (settings.DelayBetweenTicketsMs > 0) await Task.Delay(settings.DelayBetweenTicketsMs, cancellationToken);
                            break;
                        case CsAgentBackfillOutcome.LostRace:
                            lostRace++;
                            break;
                        case CsAgentBackfillOutcome.NoEligibleAgent:
                            failed++;
                            status = StalePhoneAssignmentRunStatus.StoppedNoEligibleAgent;
                            logger.LogWarning("Stale Phone assignment {BatchId}: no eligible CS Agent for ticket {TicketNumber} (#{TicketId}); stopped, retried next run.",
                                batchId, evidence.TicketNumber, evidence.TicketId);
                            break;
                        default:
                            noLonger++;   // re-check under the transaction protected it again (new activity / owner / approval)
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(ex, "Stale Phone assignment {BatchId}: ticket {TicketNumber} (#{TicketId}) FAILED; it is retried on the next run.",
                        batchId, evidence.TicketNumber, evidence.TicketId);
                }

                if (status != StalePhoneAssignmentRunStatus.Completed)
                {
                    break;
                }
            }

            if (page.Count < pageSize)
            {
                break;
            }
        }

        logger.LogInformation(
            "Stale Phone assignment {BatchId} finished ({Status}, {Mode}): examined {Examined}; stale Phone tickets {Stale}; assigned {Assigned}; would-assign {Would}; lost race {Lost}; "
            + "no longer applicable {NoLonger}; failed {Failed}; max-per-run reached {Limit}.",
            batchId, status, mode, examined, stale, assigned, wouldAssign, lostRace, noLonger, failed, limitReached);

        return new StalePhoneAssignmentRunResult(status, settings.DryRun, examined, stale, assigned, wouldAssign, lostRace, noLonger, failed, limitReached);
    }
}
