using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>
/// Reconciles the local receivables snapshot (what the pages show and what an approval is built from) with a FRESH, direct read of the same configured PACT procedure, per company,
/// unit by unit and instalment by instalment (<see cref="FinancialReconciler"/>). PACT is the authoritative source and the snapshot a synchronized copy of it, so a difference is first
/// treated as a synchronization to repair, not as a reason to stop everything: the company's snapshot is refreshed ONCE from PACT (the existing per-company refresh: staging, validation,
/// atomic publish) and compared again. A difference that was only the time between the refresh and the check is gone after that; one that persists is reported as a synchronization or
/// mapping defect for investigation, and only the units that differ are held back (<see cref="SourceGate"/>). Nothing is retried beyond that one refresh per company per run.
/// <para>A run starts on request (the Run button), or by itself right after a successful snapshot refresh of a company (<see cref="IPostRefreshReconciliation"/>), never by a page view;
/// runs of the same company never overlap. A run that cannot complete (PACT unreachable, snapshot republished during the run) is an <c>Error</c> - missing evidence, never a "pass" and
/// never a finding.</para>
/// BASIS (<see cref="ReconciliationBasis"/>): both sides are the output of the SAME PACT procedure (the snapshot via the refresh job's linked-server call, the other side via a direct call), so
/// this is a SNAPSHOT-CONSISTENCY check, not an independent ledger reconciliation. It never sets or implies the human sign-off, which remains mandatory in <see cref="SourceGate"/>.
/// </summary>
public sealed class FinancialReconciliationService(
    CollectionsOptions options, CollectionsCampaignOptions campaignOptions, PactReceivablesOptions sourceOptions,
    CollectionsAuthorizationService authorization, CollectionsClock clock, IPactReceivablesSource snapshotSource, IReconciliationSource directSource,
    ReconciliationHistory history, ILogger<FinancialReconciliationService> logger, IReconciliationLauncher? launcher = null, ReceivablesSnapshotOptions? snapshotOptions = null,
    IReceivablesRefresher? refresher = null) : IPostRefreshReconciliation
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);
    /// <summary>The trigger source recorded on a refresh the reconciliation starts itself (so a refresh job never starts another check from it).</summary>
    public const string RecoveryRefreshTrigger = "Reconciliation";
    public const string SystemActor = "system";

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
        var run = NewRun(companyId, caller.EmployeeId.ToString(), ReconciliationTriggers.Manual);
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
    /// The check that follows a successful snapshot refresh of <paramref name="companyId"/> (scheduled, start-up or Retry): the new publish is noted (older evidence no longer covers it) and
    /// compared at once, in the caller's job. Off with <c>ReconcileAfterRefresh</c> or when the automated evidence is not required. Never throws; a run of the same company already in progress
    /// is reported and skipped (the next refresh checks again).
    /// </summary>
    public async Task<ReconciliationRunDto?> RunAfterRefreshAsync(int companyId, CancellationToken ct)
    {
        if (companyId is not (4 or 32)) return null;
        history.NoteSnapshotPublished(companyId, clock.UtcNow);
        if (!options.Enabled || !sourceOptions.Enabled || !campaignOptions.ReconcileAfterRefresh || !campaignOptions.RequireAutomatedReconciliation) return null;
        var run = NewRun(companyId, SystemActor, ReconciliationTriggers.AfterRefresh);
        if (!history.TryBegin(run, TimeSpan.Zero, out var refusal))
        {
            logger.LogInformation("Snapshot-consistency check of company {CompanyId} after its refresh was skipped: {Reason}", companyId, refusal);
            return null;
        }
        try { await ExecuteAsync(run.RunId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Snapshot-consistency check {RunId} of company {CompanyId} after its refresh failed outside its own error handling.", run.RunId, companyId);
            history.Replace(run with { Status = ReconciliationStates.Error, FinishedUtc = clock.UtcNow });
        }
        return history.Get(run.RunId);
    }

    private ReconciliationRunDto NewRun(int? companyId, string requestedBy, string trigger)
    {
        var (from, through) = Window();
        return new(Guid.NewGuid(), ReconciliationStates.Running, clock.UtcNow, null, from, through, companyId, campaignOptions.ReconciliationToleranceAed, requestedBy, [], trigger);
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
        logger.LogInformation("Reconciliation run {RunId} ({Trigger}) finished {Status}: {Summary}.", runId, run.Trigger, status,
            string.Join("; ", results.Select(r => $"company {r.CompanyId} {r.Status} ({r.Comparison?.MismatchedUnits ?? 0} unit(s) differ{(r.Recovery is { } rec ? $", recovery refresh {rec.RefreshStatus}, {rec.MismatchedUnitsBefore} before" : "")})")));
    }

    private ReconciliationCompanyResultDto Error(ReconciliationRunDto run, int company, DateTime started, DateTime? snapshotAt, string message, long t0)
    {
        logger.LogWarning("Reconciliation run {RunId} company {CompanyId} could not be completed after {ElapsedMs:F0} ms: {Reason}", run.RunId, company, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, message);
        return new(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), ReconciliationStates.Error, message, started, clock.UtcNow, snapshotAt, directSource.ProcedureLabel(company), null);
    }

    /// <summary>One comparison, and when it is not conclusive why not: the reason a controlled refresh of the company could make it so.</summary>
    private sealed record Attempt(ReconciliationCompanyResultDto Result, string? RecoveryReason);

    /// <summary>
    /// Compare; if the snapshot is too old to compare or the comparison finds differences, refresh THIS company from PACT once (the normal refresh: staging, validation, atomic publish; a
    /// failed or partial refresh keeps the previous snapshot) and compare again. One refresh at most: the second result is final, and a difference that survives it is a defect to investigate.
    /// </summary>
    private async Task<ReconciliationCompanyResultDto> ReconcileCompanyAsync(ReconciliationRunDto run, int company, CancellationToken ct)
    {
        var first = await CompareAsync(run, company, ct);
        if (first.RecoveryReason is null || !campaignOptions.ReconciliationRecoveryRefresh || refresher is null) return first.Result;

        var before = first.Result.Comparison;
        if (before is not null)
            logger.LogWarning("Reconciliation run {RunId} company {CompanyId}: {Mismatched} unit(s) differ between the snapshot (published {SnapshotAt:u}) and PACT; refreshing the company's snapshot once and comparing again. First differences: {Differences}",
                run.RunId, company, before.MismatchedUnits, first.Result.SnapshotSuccessUtc,
                string.Join(" | ", before.Diffs.Take(20).Select(d => $"{d.UnitCode} snapshot {d.SnapshotTotal:N2} vs PACT {d.SourceTotal:N2}: {d.Summary}")) + (before.Diffs.Count > 20 ? " | ..." : ""));
        else logger.LogInformation("Reconciliation run {RunId} company {CompanyId}: {Reason}; refreshing the company's snapshot before comparing.", run.RunId, company, first.Result.Message);

        string refreshStatus; string? refreshMessage = null;
        try
        {
            var refreshed = await refresher.RefreshAsync(RecoveryRefreshTrigger, company, ct);
            var own = refreshed.Companies.FirstOrDefault(c => c.CompanyId == company);
            refreshStatus = own?.Status ?? refreshed.Status;
            refreshMessage = own?.ErrorMessage ?? refreshed.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Reconciliation run {RunId} company {CompanyId}: the recovery refresh failed.", run.RunId, company);
            refreshStatus = "Failed"; refreshMessage = $"The refresh stopped unexpectedly ({ex.GetType().Name}).";
        }
        var mismatchedBefore = before?.MismatchedUnits ?? 0;
        if (refreshStatus != "Succeeded")
        {
            // The previous snapshot is kept by the refresh, so the first comparison still describes the data in use; the differences stay unresolved and the company stays closed.
            var recovery = new ReconciliationRecoveryDto(first.RecoveryReason, refreshStatus, refreshMessage, false, mismatchedBefore, false, first.Result.SnapshotSuccessUtc, null, before);
            logger.LogWarning("Reconciliation run {RunId} company {CompanyId}: the recovery refresh ended {Status} ({Message}); the previous snapshot is still in use.", run.RunId, company, refreshStatus, refreshMessage);
            return first.Result with { Recovery = recovery, Message = $"{first.Result.Message} The snapshot could not be refreshed to resolve this ({refreshStatus}: {refreshMessage}); the previous snapshot is still in use." };
        }

        var second = await CompareAsync(run, company, ct);
        var resolved = second.Result.Status == ReconciliationStates.Passed;
        var outcome = new ReconciliationRecoveryDto(first.RecoveryReason, refreshStatus, null, true, mismatchedBefore, resolved, first.Result.SnapshotSuccessUtc, second.Result.SnapshotSuccessUtc, before);
        var message = second.Result.Status switch
        {
            ReconciliationStates.Passed when before is not null =>
                $"Recovered: {mismatchedBefore:N0} unit(s) differed before the snapshot was refreshed (changes in PACT since the previous refresh); after the refresh both sides agree.",
            ReconciliationStates.Passed => "The snapshot was refreshed first (it was too old to compare); after the refresh both sides agree.",
            ReconciliationStates.Failed =>
                $"{second.Result.Message} The differences persist after a successful refresh of the snapshot ({mismatchedBefore:N0} unit(s) differed before it): a synchronization or mapping defect that needs investigation. Only the listed units are held back.",
            _ => $"{second.Result.Message} (The snapshot was refreshed first.)"
        };
        if (second.Result.Status == ReconciliationStates.Failed)
            logger.LogError("Reconciliation run {RunId} company {CompanyId}: {Mismatched} unit(s) still differ after a successful refresh of the snapshot (published {SnapshotAt:u}): synchronization or mapping defect, investigate. {Differences}",
                run.RunId, company, second.Result.Comparison?.MismatchedUnits, second.Result.SnapshotSuccessUtc,
                string.Join(" | ", (second.Result.Comparison?.Diffs ?? []).Take(20).Select(d => $"{d.UnitCode} ({d.TenantId}/{d.UnitId}) snapshot {d.SnapshotTotal:N2} vs PACT {d.SourceTotal:N2}: {d.Summary}")));
        return second.Result with { Recovery = outcome, Message = message };
    }

    private async Task<Attempt> CompareAsync(ReconciliationRunDto run, int company, CancellationToken ct)
    {
        var t0 = Stopwatch.GetTimestamp();
        var started = clock.UtcNow;
        var maxAge = Math.Max(1, campaignOptions.ReconciliationMaxSnapshotAgeMinutes);

        // 1. The snapshot side (local). It must be loaded, not being refreshed, and fresh enough that the difference is not just the time since the refresh.
        PactReceivablesSnapshot snapshot;
        try { snapshot = await snapshotSource.ReadAsync(new PactReceivablesRequest(run.From, run.Through, company), ct); }
        catch (PactReceivablesSourceException ex) { return new(Error(run, company, started, null, $"The snapshot could not be read ({ex.Message})", t0), null); }
        var state = snapshot.Snapshot?.Companies.FirstOrDefault(c => c.CompanyId == company);
        if (state is null || !state.HasSnapshot) return new(Error(run, company, started, null, "No snapshot has been loaded for this company yet.", t0), null);
        if (state.IsLoading) return new(Error(run, company, started, state.LastSuccessUtc, "A refresh of this company is running; reconcile after it has finished.", t0), null);
        if (state.AgeMinutes is not { } age || age > maxAge)
            return new(Error(run, company, started, state.LastSuccessUtc, $"The snapshot is {state.AgeMinutes} minutes old (limit {maxAge}); payments made since would show as differences. Refresh the company, then reconcile.", t0),
                ReconciliationRecoveryReasons.StaleSnapshot);

        // 2. The independent side: the same procedure, read directly now.
        IReadOnlyList<PactReceivableInstalment> direct;
        try { direct = await directSource.ReadAsync(company, run.From, run.Through, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error(run, company, started, state.LastSuccessUtc, "PACT did not answer in time.", t0), null); }
        catch (Exception ex) when (ex is PactReceivablesSourceException or System.Data.Common.DbException)
        { return new(Error(run, company, started, state.LastSuccessUtc, ex is PactReceivablesSourceException p ? p.Message : "PACT could not be read.", t0), null); }

        // 3. The snapshot must be the same publish before and after, otherwise the two sides are not comparable.
        if (snapshotSource is ISnapshotStatusSource status)
        {
            try
            {
                var after = (await status.ReadStatusAsync(ct)).Companies.FirstOrDefault(c => c.CompanyId == company);
                if (after is null || after.LastSuccessUtc != state.LastSuccessUtc)
                    return new(Error(run, company, started, state.LastSuccessUtc, "The snapshot was refreshed while the comparison ran; run it again.", t0), null);
            }
            catch (PactReceivablesSourceException) { /* the comparison itself is still valid for the data read */ }
        }

        ReconLine Line(PactReceivableInstalment r) => new(r.TenantId, r.UnitId ?? 0, r.UnitCode, r.VoucherNumber, DateOnly.FromDateTime(r.DueDate), r.Amount);
        var comparison = FinancialReconciler.Compare(snapshot.Items.Where(r => r.CompanyId == company).Select(Line), direct.Where(r => r.CompanyId == company).Select(Line),
            run.From, run.Through, clock.BusinessDate, run.Tolerance, Math.Max(1, campaignOptions.ReconciliationMaxDifferences));
        if (comparison.SnapshotLines == 0 && comparison.SourceLines == 0)
            return new(Error(run, company, started, state.LastSuccessUtc, "Neither the snapshot nor PACT returned any unpaid instalment in the window; there is nothing to compare.", t0), null);
        var message = comparison.Matches ? null
            : $"{comparison.MismatchedUnits:N0} of {comparison.SnapshotUnits + comparison.UnitsOnlyInSource:N0} units differ ({comparison.UnitsOnlyInSnapshot:N0} only in the snapshot, {comparison.UnitsOnlyInSource:N0} only in PACT); largest unit difference {comparison.MaxUnitDelta:N2}.";
        logger.LogInformation("Reconciliation run {RunId} company {CompanyId} {Status} in {ElapsedMs:F0} ms: snapshot {SnapshotLines} lines / {SnapshotTotal:N2}, PACT {SourceLines} lines / {SourceTotal:N2}, {Mismatched} unit(s) differ.",
            run.RunId, company, comparison.Matches ? "Passed" : "Failed", Stopwatch.GetElapsedTime(t0).TotalMilliseconds, comparison.SnapshotLines, comparison.SnapshotTotal, comparison.SourceLines, comparison.SourceTotal, comparison.MismatchedUnits);
        var result = new ReconciliationCompanyResultDto(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), comparison.Matches ? ReconciliationStates.Passed : ReconciliationStates.Failed, message,
            started, clock.UtcNow, state.LastSuccessUtc, directSource.ProcedureLabel(company), comparison, ReconciliationBasis.SnapshotSide(company));
        return new(result, comparison.Matches ? null : ReconciliationRecoveryReasons.Differences);
    }
}
