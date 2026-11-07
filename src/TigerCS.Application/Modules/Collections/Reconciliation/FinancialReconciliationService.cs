using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>
/// Reconciles the local receivables snapshot (what the pages show and what an approval is built from) with a FRESH, direct read of the same configured PACT procedure, per company,
/// unit by unit and instalment by instalment (<see cref="FinancialReconciler"/>). It is started only on request (never by a page view), one at a time, with a cooldown.
/// A run that cannot complete (PACT unreachable, snapshot not fresh, snapshot republished during the run) is an <c>Error</c> - missing evidence, never a "pass" and never a finding.
/// BASIS (<see cref="ReconciliationBasis"/>): both sides are the output of the SAME PACT procedure (the snapshot via the refresh job's linked-server call, the other side via a direct call), so
/// this is a SNAPSHOT-CONSISTENCY check, not an independent ledger reconciliation. It never sets or implies the human sign-off, which remains mandatory in <see cref="SourceGate"/>.
/// </summary>
public sealed class FinancialReconciliationService(
    CollectionsOptions options, CollectionsCampaignOptions campaignOptions, PactReceivablesOptions sourceOptions,
    CollectionsAuthorizationService authorization, CollectionsClock clock, IPactReceivablesSource snapshotSource, IReconciliationSource directSource,
    ReconciliationHistory history, ILogger<FinancialReconciliationService> logger, IReconciliationLauncher? launcher = null, ReceivablesSnapshotOptions? snapshotOptions = null)
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    public SourceGateDto Gate() => SourceGate.Evaluate(campaignOptions, sourceOptions, history, clock.UtcNow);

    public async Task<CollectionsResult<ReconciliationOverviewDto>> OverviewAsync(CollectionsCaller caller, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials) return CollectionsResult<ReconciliationOverviewDto>.Fail(CollectionsOutcome.Forbidden);
        return CollectionsResult<ReconciliationOverviewDto>.Ok(new(Gate(), history.Latest(), history.Recent()));
    }

    public async Task<CollectionsResult<ReconciliationRunDto>> GetRunAsync(CollectionsCaller caller, Guid runId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials) return CollectionsResult<ReconciliationRunDto>.Fail(CollectionsOutcome.Forbidden);
        return history.Get(runId) is { } run ? CollectionsResult<ReconciliationRunDto>.Ok(run) : CollectionsResult<ReconciliationRunDto>.Fail(CollectionsOutcome.NotFound);
    }

    /// <summary>Registers a run and hands it to the background launcher. Returns at once; the result is read with <see cref="GetRunAsync"/>.</summary>
    public async Task<CollectionsResult<ReconciliationRunDto>> StartAsync(CollectionsCaller caller, int? companyId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders) return CollectionsResult<ReconciliationRunDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled || !sourceOptions.Enabled || launcher is null) return CollectionsResult<ReconciliationRunDto>.Fail(CollectionsOutcome.Disabled, "Reconciliation is not available in this environment.");
        if (companyId is not (null or 4 or 32)) return CollectionsResult<ReconciliationRunDto>.Fail(CollectionsOutcome.InvalidRequest, "Company must be 4 or 32.");
        var (from, through) = Window();
        var run = new ReconciliationRunDto(Guid.NewGuid(), ReconciliationStates.Running, clock.UtcNow, null, from, through, companyId, campaignOptions.ReconciliationToleranceAed, caller.EmployeeId.ToString(), []);
        if (!history.TryBegin(run, Cooldown, out var refusal)) return CollectionsResult<ReconciliationRunDto>.Fail(CollectionsOutcome.Conflict, refusal);
        try { launcher.Launch(run.RunId); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reconciliation run {RunId} could not be launched.", run.RunId);
            history.Replace(run with { Status = ReconciliationStates.Error, FinishedUtc = clock.UtcNow });
            return CollectionsResult<ReconciliationRunDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The reconciliation could not be started.");
        }
        return CollectionsResult<ReconciliationRunDto>.Ok(run, CollectionsOutcome.Accepted);
    }

    /// <summary>
    /// The due-date window of a run: from <see cref="CollectionsCampaignOptions.ReconciliationStartDate"/> (never before the snapshot's own coverage, which has nothing earlier to compare) to the
    /// end of the current month. Both sides are read for exactly this window and compared line by line inside it.
    /// </summary>
    public (DateOnly From, DateOnly Through) Window()
    {
        var start = DateOnly.FromDateTime(campaignOptions.ReconciliationStartDate);
        if (snapshotOptions is not null && DateOnly.FromDateTime(snapshotOptions.SourceFromDate) is var coverage && coverage > start) start = coverage;
        return (start, CollectionsDueMonth.MonthEnd(clock.BusinessDate));
    }

    /// <summary>Runs a registered run to the end. Never throws: whatever happens ends the run in a terminal state with a reason.</summary>
    public async Task ExecuteAsync(Guid runId, CancellationToken ct)
    {
        var run = history.Get(runId);
        if (run is null || run.Status != ReconciliationStates.Running) return;
        var only = run.CompanyScope;
        var results = new List<ReconciliationCompanyResultDto>();
        foreach (var company in new[] { 4, 32 }.Where(c => only is null || c == only))
        {
            try { results.Add(await ReconcileCompanyAsync(run, company, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Reconciliation run {RunId} company {CompanyId} ended unexpectedly.", runId, company);
                results.Add(Error(run, company, clock.UtcNow, null, "The comparison stopped unexpectedly.", Stopwatch.GetTimestamp()));
            }
        }
        var status = results.Any(r => r.Status == ReconciliationStates.Failed) ? ReconciliationStates.Failed
            : results.Any(r => r.Status == ReconciliationStates.Error) ? ReconciliationStates.Error : ReconciliationStates.Passed;
        history.Replace(run with { Status = status, FinishedUtc = clock.UtcNow, Companies = results });
        logger.LogInformation("Reconciliation run {RunId} finished {Status}: {Summary}.", runId, status,
            string.Join("; ", results.Select(r => $"company {r.CompanyId} {r.Status} ({r.Comparison?.MismatchedUnits ?? 0} unit(s) differ)")));
    }

    private ReconciliationCompanyResultDto Error(ReconciliationRunDto run, int company, DateTime started, DateTime? snapshotAt, string message, long t0)
    {
        logger.LogWarning("Reconciliation run {RunId} company {CompanyId} could not be completed after {ElapsedMs:F0} ms: {Reason}", run.RunId, company, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, message);
        return new(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), ReconciliationStates.Error, message, started, clock.UtcNow, snapshotAt, directSource.ProcedureLabel(company), null);
    }

    private async Task<ReconciliationCompanyResultDto> ReconcileCompanyAsync(ReconciliationRunDto run, int company, CancellationToken ct)
    {
        var t0 = Stopwatch.GetTimestamp();
        var started = clock.UtcNow;
        var maxAge = Math.Max(1, campaignOptions.ReconciliationMaxSnapshotAgeMinutes);

        // 1. The snapshot side (local). It must be loaded, not being refreshed, and fresh enough that the difference is not just the time since the refresh.
        PactReceivablesSnapshot snapshot;
        try { snapshot = await snapshotSource.ReadAsync(new PactReceivablesRequest(run.From, run.Through, company), ct); }
        catch (PactReceivablesSourceException ex) { return Error(run, company, started, null, $"The snapshot could not be read ({ex.Message})", t0); }
        var state = snapshot.Snapshot?.Companies.FirstOrDefault(c => c.CompanyId == company);
        if (state is null || !state.HasSnapshot) return Error(run, company, started, null, "No snapshot has been loaded for this company yet.", t0);
        if (state.IsLoading) return Error(run, company, started, state.LastSuccessUtc, "A refresh of this company is running; reconcile after it has finished.", t0);
        if (state.AgeMinutes is not { } age || age > maxAge)
            return Error(run, company, started, state.LastSuccessUtc, $"The snapshot is {state.AgeMinutes} minutes old (limit {maxAge}); payments made since would show as differences. Refresh the company, then reconcile.", t0);

        // 2. The independent side: the same procedure, read directly now.
        IReadOnlyList<PactReceivableInstalment> direct;
        try { direct = await directSource.ReadAsync(company, run.From, run.Through, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Error(run, company, started, state.LastSuccessUtc, "PACT did not answer in time.", t0); }
        catch (Exception ex) when (ex is PactReceivablesSourceException or System.Data.Common.DbException)
        { return Error(run, company, started, state.LastSuccessUtc, ex is PactReceivablesSourceException p ? p.Message : "PACT could not be read.", t0); }

        // 3. The snapshot must be the same publish before and after, otherwise the two sides are not comparable.
        if (snapshotSource is ISnapshotStatusSource status)
        {
            try
            {
                var after = (await status.ReadStatusAsync(ct)).Companies.FirstOrDefault(c => c.CompanyId == company);
                if (after is null || after.LastSuccessUtc != state.LastSuccessUtc)
                    return Error(run, company, started, state.LastSuccessUtc, "The snapshot was refreshed while the comparison ran; run it again.", t0);
            }
            catch (PactReceivablesSourceException) { /* the comparison itself is still valid for the data read */ }
        }

        ReconLine Line(PactReceivableInstalment r) => new(r.TenantId, r.UnitId ?? 0, r.UnitCode, r.VoucherNumber, DateOnly.FromDateTime(r.DueDate), r.Amount);
        var comparison = FinancialReconciler.Compare(snapshot.Items.Where(r => r.CompanyId == company).Select(Line), direct.Where(r => r.CompanyId == company).Select(Line),
            run.From, run.Through, clock.BusinessDate, run.Tolerance, Math.Max(1, campaignOptions.ReconciliationMaxDifferences));
        if (comparison.SnapshotLines == 0 && comparison.SourceLines == 0)
            return Error(run, company, started, state.LastSuccessUtc, "Neither the snapshot nor PACT returned any unpaid instalment in the window; there is nothing to compare.", t0);
        var message = comparison.Matches ? null
            : $"{comparison.MismatchedUnits:N0} of {comparison.SnapshotUnits + comparison.UnitsOnlyInSource:N0} units differ ({comparison.UnitsOnlyInSnapshot:N0} only in the snapshot, {comparison.UnitsOnlyInSource:N0} only in PACT); largest unit difference {comparison.MaxUnitDelta:N2}.";
        logger.LogInformation("Reconciliation run {RunId} company {CompanyId} {Status} in {ElapsedMs:F0} ms: snapshot {SnapshotLines} lines / {SnapshotTotal:N2}, PACT {SourceLines} lines / {SourceTotal:N2}, {Mismatched} unit(s) differ.",
            run.RunId, company, comparison.Matches ? "Passed" : "Failed", Stopwatch.GetElapsedTime(t0).TotalMilliseconds, comparison.SnapshotLines, comparison.SnapshotTotal, comparison.SourceLines, comparison.SourceTotal, comparison.MismatchedUnits);
        return new(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), comparison.Matches ? ReconciliationStates.Passed : ReconciliationStates.Failed, message,
            started, clock.UtcNow, state.LastSuccessUtc, directSource.ProcedureLabel(company), comparison, ReconciliationBasis.SnapshotSide(company));
    }
}
