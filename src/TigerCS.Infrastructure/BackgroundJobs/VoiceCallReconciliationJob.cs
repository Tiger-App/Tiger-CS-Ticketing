using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;

namespace TigerCS.Infrastructure.BackgroundJobs;

public sealed record VoiceCallReconciliationRunResult(
    bool Ran, bool DryRun, int Examined, int Eligible, int AssignedAnsweringAgent, int FlaggedAndQueued, int QueuedNoEligibleAgent, int MappingPending,
    int WouldAct, int LostRace, int NoLongerApplicable, int Failed, bool LimitReached);

/// <summary>
/// One voice-call reconciliation pass. It only sequences and isolates: <see cref="VoiceCallReconciler"/> owns every rule (the end-of-call window, who answered,
/// flag-and-queue; never Round Robin). One DI scope (one DbContext) per ticket, so a lost race cannot poison the next one. Shares the single-flight lock with the other assignment jobs
/// so runs never overlap. Logs ticket ids and numbers only - no customer data.
/// </summary>
public sealed class VoiceCallReconciliationJob(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<VoiceCallReconciliationOptions> options, ICallCenterReconciliationRunLock runLock,
    ILogger<VoiceCallReconciliationJob> logger)
{
    private const int MaxPagesPerRun = 10;

    public async Task<VoiceCallReconciliationRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var none = new VoiceCallReconciliationRunResult(false, settings.DryRun, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);
        if (!settings.Enabled)
        {
            return none;
        }

        await using var handle = await runLock.TryAcquireAsync(cancellationToken);
        if (handle is null)
        {
            logger.LogInformation("Voice call reconciliation: another assignment run is in progress; this run is skipped (runs never overlap).");
            return none;
        }

        var limit = Math.Max(0, settings.MaxActionsPerRun);
        var pageSize = Math.Clamp(settings.BatchSize, 1, 1000);
        long after = 0;
        int examined = 0, eligible = 0, answering = 0, flaggedQueued = 0, queued = 0, mapping = 0, would = 0, lost = 0, noLonger = 0, failed = 0, acted = 0;
        var limitReached = false;

        for (var pageNumber = 0; pageNumber < MaxPagesPerRun && !limitReached; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<(TigerCS.Application.Modules.Ticketing.Services.UnassignedTicketEvidence Evidence, VoiceReconcileClassification Classification)> page;
            await using (var listScope = scopeFactory.CreateAsyncScope())
            {
                page = await listScope.ServiceProvider.GetRequiredService<VoiceCallReconciler>().ListAsync(after, settings, cancellationToken);
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (var (evidence, classification) in page)
            {
                after = Math.Max(after, evidence.TicketId);
                examined++;

                if (classification.Class == VoiceReconcileClass.AnsweringAgentMappingPending)
                {
                    mapping++;
                    logger.LogWarning(
                        "Voice call: ticket {TicketNumber} (#{TicketId}) was answered by Genesys agent {GenesysUserId} who is not mapped to (or cannot take it as) a Ticketing user; "
                        + "it stays in the queue, flagged for manual assignment, and is reconciled automatically once the mapping is corrected. No Round Robin.",
                        evidence.TicketNumber, evidence.TicketId, evidence.FlaggedAnsweringGenesysUserId ?? evidence.UnmappedGenesysAgentId);
                    continue;
                }

                if (classification.Action == VoiceReconcileAction.None)
                {
                    continue;
                }

                if (acted >= limit)
                {
                    limitReached = true;
                    break;
                }

                eligible++;
                acted++;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<VoiceCallReconciler>()
                        .TryReconcileAsync(evidence.TicketId, settings, settings.DryRun, cancellationToken);

                    switch (result.Outcome)
                    {
                        case VoiceReconcileOutcome.WouldAct:
                            would++;
                            logger.LogInformation("DRY RUN: would reconcile voice ticket {TicketNumber} (#{TicketId}) as {Class}.", evidence.TicketNumber, evidence.TicketId, result.Class);
                            break;
                        case VoiceReconcileOutcome.AssignedToAnsweringAgent:
                            answering++;
                            logger.LogInformation("Voice call: ticket {TicketNumber} (#{TicketId}) given to the agent who answered it, employee {EmployeeId}.", evidence.TicketNumber, evidence.TicketId, result.AssignedEmployeeId);
                            break;
                        case VoiceReconcileOutcome.FlaggedAndQueued:
                            flaggedQueued++;
                            logger.LogInformation("Voice call: ticket {TicketNumber} (#{TicketId}) ended unanswered: flagged as waiting for a person and queued in the Call Center (AI Agent remains the owner; nobody assigned).", evidence.TicketNumber, evidence.TicketId);
                            break;
                        case VoiceReconcileOutcome.QueuedNoEligibleAgent:
                            queued++;
                            logger.LogWarning("Voice call: ticket {TicketNumber} (#{TicketId}) ended unanswered but no active Call Center department exists; nothing could be queued and it is retried next run.", evidence.TicketNumber, evidence.TicketId);
                            break;
                        case VoiceReconcileOutcome.FlaggedMappingPending:
                            mapping++;
                            break;
                        case VoiceReconcileOutcome.LostRace:
                            lost++;
                            logger.LogInformation("Voice call: ticket {TicketNumber} (#{TicketId}) changed concurrently; the other writer's result stands.", evidence.TicketNumber, evidence.TicketId);
                            break;
                        default:
                            noLonger++;
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(ex, "Voice call reconciliation FAILED for ticket {TicketNumber} (#{TicketId}); it is retried on the next run.", evidence.TicketNumber, evidence.TicketId);
                }
            }

            if (page.Count < pageSize)
            {
                break;
            }
        }

        if (examined > 0)
        {
            logger.LogInformation(
                "Voice call reconciliation finished ({Mode}): examined {Examined}; acted {Eligible}; to answering agent {Answering}; flagged and queued {FlaggedQueued}; no Call Center department {Queued}; "
                + "mapping pending {Mapping}; would act {Would}; lost race {Lost}; no longer applicable {NoLonger}; failed {Failed}; limit reached {Limit}.",
                settings.DryRun ? "DRY RUN" : "LIVE", examined, eligible, answering, flaggedQueued, queued, mapping, would, lost, noLonger, failed, limitReached);
        }

        return new VoiceCallReconciliationRunResult(true, settings.DryRun, examined, eligible, answering, flaggedQueued, queued, mapping, would, lost, noLonger, failed, limitReached);
    }
}
