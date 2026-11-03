using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// One reconciliation pass. It only schedules and isolates: <see cref="CallCenterUnassignedWorkReconciler"/> owns every rule.
/// Disabled and dry-run by default. One DI scope (one DbContext) per ticket, so a lost race cannot poison the next one. Logs ticket ids
/// and numbers only - no customer data.
/// </summary>
public sealed class CallCenterUnassignedWorkJob(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<CallCenterReconciliationOptions> options,
    ICallCenterReconciliationRunLock runLock, ILogger<CallCenterUnassignedWorkJob> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled)
        {
            return;
        }

        await using var handle = await runLock.TryAcquireAsync(cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Call Center reconciliation: another run is in progress; this run is skipped (runs never overlap).");
            return;
        }

        var mode = settings.DryRun ? "DRY RUN - nothing written" : "LIVE";
        var limit = Math.Max(0, settings.MaxAssignmentsPerRun);
        var bySkipClass = new Dictionary<UnassignedTicketClass, int>();
        long after = 0;
        int examined = 0, eligible = 0, assigned = 0, wouldAssign = 0, noAgent = 0, lostRace = 0, noLonger = 0, failed = 0, acted = 0;
        var limitReached = false;

        logger.LogInformation("Call Center reconciliation started ({Mode}); batch {Batch}, max assignments per run {Max}, CS Agents only {CsOnly}.",
            mode, settings.BatchSize, limit, settings.CsAgentRoleOnly);

        while (examined < Math.Max(1, settings.BatchSize) * 20 && !limitReached)   // bounded: at most 20 pages per run
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<(UnassignedTicketEvidence Evidence, UnassignedTicketClassification Classification)> page;
            await using (var listScope = scopeFactory.CreateAsyncScope())
            {
                page = await listScope.ServiceProvider.GetRequiredService<CallCenterUnassignedWorkReconciler>()
                    .ListAsync(after, settings, cancellationToken);
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (var (evidence, classification) in page)
            {
                after = Math.Max(after, evidence.TicketId);
                examined++;

                if (classification.Action == UnassignedTicketAction.None)
                {
                    bySkipClass[classification.Class] = bySkipClass.GetValueOrDefault(classification.Class) + 1;
                    logger.LogDebug("Reconciliation skipped ticket {TicketNumber} (#{TicketId}): {Class}.", evidence.TicketNumber, evidence.TicketId, classification.Class);
                    continue;
                }

                if (acted >= limit)
                {
                    limitReached = true;
                    break;
                }

                eligible++;
                acted++;
                logger.LogInformation("Reconciliation eligible: ticket {TicketNumber} (#{TicketId}) is {Class} -> {Action}.",
                    evidence.TicketNumber, evidence.TicketId, classification.Class, classification.Action);

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<CallCenterUnassignedWorkReconciler>()
                        .TryReconcileAsync(evidence.TicketId, settings, settings.DryRun, cancellationToken);

                    switch (result.Outcome)
                    {
                        case ReconcileOutcome.WouldAct:
                            wouldAssign++;
                            logger.LogInformation("DRY RUN: would reconcile ticket {TicketNumber} (#{TicketId}) as {Class}.", evidence.TicketNumber, evidence.TicketId, result.Class);
                            break;
                        case ReconcileOutcome.AssignedToAnsweringAgent:
                        case ReconcileOutcome.AssignedRoundRobin:
                            assigned++;
                            logger.LogInformation("Reconciliation ASSIGNED ticket {TicketNumber} (#{TicketId}) [{Class}] to employee {EmployeeId} via {Outcome}.",
                                evidence.TicketNumber, evidence.TicketId, result.Class, result.AssignedEmployeeId, result.Outcome);
                            break;
                        case ReconcileOutcome.NoEligibleAgent:
                            noAgent++;
                            logger.LogWarning("Reconciliation: ticket {TicketNumber} (#{TicketId}) [{Class}] needs a person but no eligible Call Center CS Agent exists; left queued, retried next run.",
                                evidence.TicketNumber, evidence.TicketId, result.Class);
                            break;
                        case ReconcileOutcome.LostRace:
                            lostRace++;
                            logger.LogWarning("Reconciliation: ticket {TicketNumber} (#{TicketId}) changed concurrently; the other writer's result stands.", evidence.TicketNumber, evidence.TicketId);
                            break;
                        default:
                            noLonger++;
                            logger.LogInformation("Reconciliation skipped ticket {TicketNumber} (#{TicketId}): no longer applicable ({Outcome}).", evidence.TicketNumber, evidence.TicketId, result.Outcome);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(ex, "Reconciliation FAILED for ticket {TicketNumber} (#{TicketId}); it is retried on the next run.", evidence.TicketNumber, evidence.TicketId);
                }
            }
        }

        logger.LogInformation(
            "Call Center reconciliation finished ({Mode}): examined {Examined}; eligible {Eligible}; assigned {Assigned}; would-assign {WouldAssign}; no eligible agent {NoAgent}; "
            + "lost race {LostRace}; no longer applicable {NoLonger}; failed {Failed}; skipped by class [{Skipped}]; MaxAssignmentsPerRun reached {LimitReached}.",
            mode, examined, eligible, assigned, wouldAssign, noAgent, lostRace, noLonger, failed,
            string.Join(", ", bySkipClass.Select(p => $"{p.Key}={p.Value}")), limitReached);
    }
}
