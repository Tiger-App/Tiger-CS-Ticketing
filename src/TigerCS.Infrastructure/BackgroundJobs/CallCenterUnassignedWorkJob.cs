using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// The recurring safety net behind "a ticket that needs a person never silently stays without an owner". It only schedules and isolates:
/// <see cref="CallCenterUnassignedWorkReconciler"/> owns every rule. OFF by default (<c>CallCenterReconciliation:Enabled</c>) and dry-run by
/// default (<c>DryRun</c>): a dry run writes nothing and logs exactly which tickets it would give to whom.
///
/// <para>One DI scope (one DbContext) per ticket, so a lost race cannot poison the next one. Only counts and ticket ids are logged.</para>
/// </summary>
public sealed class CallCenterUnassignedWorkJob(
    IServiceScopeFactory scopeFactory, IOptions<CallCenterReconciliationOptions> options, ILogger<CallCenterUnassignedWorkJob> logger)
{
    public const string RecurringJobId = "call-center-unassigned-work";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var byClass = new Dictionary<UnassignedTicketClass, int>();
        var byOutcome = new Dictionary<ReconcileOutcome, int>();
        var wouldAct = new List<long>();
        long after = 0;
        int examined = 0, written = 0, failed = 0;

        while (examined < settings.BatchSize * 20)   // bounded: at most 20 pages per run
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
                byClass[classification.Class] = byClass.GetValueOrDefault(classification.Class) + 1;

                if (classification.Action == UnassignedTicketAction.None || (!settings.DryRun && written >= settings.MaxAssignmentsPerRun))
                {
                    continue;
                }

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<CallCenterUnassignedWorkReconciler>()
                        .TryReconcileAsync(evidence.TicketId, settings, settings.DryRun, cancellationToken);
                    byOutcome[result.Outcome] = byOutcome.GetValueOrDefault(result.Outcome) + 1;
                    if (result.Outcome == ReconcileOutcome.WouldAct) wouldAct.Add(evidence.TicketId);
                    if (result.Outcome is ReconcileOutcome.AssignedToAnsweringAgent or ReconcileOutcome.AssignedRoundRobin) written++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(ex, "Unassigned-work reconciliation failed for ticket {TicketId}; it is retried on the next run.", evidence.TicketId);
                }
            }
        }

        logger.LogInformation(
            "Call Center reconciliation ({Mode}): examined {Examined} unowned Open tickets; classes {Classes}; outcomes {Outcomes}; failed {Failed}.{WouldAct}",
            settings.DryRun ? "DRY RUN - nothing written" : "LIVE", examined,
            string.Join(", ", byClass.Select(p => $"{p.Key}={p.Value}")),
            string.Join(", ", byOutcome.Select(p => $"{p.Key}={p.Value}")),
            failed,
            wouldAct.Count > 0 ? $" Would act on ticket ids: {string.Join(",", wouldAct.Take(200))}" : string.Empty);
    }
}
