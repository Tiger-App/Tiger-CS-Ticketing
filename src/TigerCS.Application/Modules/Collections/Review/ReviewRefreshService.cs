using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// The explicit "refresh review data" job: one read of the financial source, validation of every record, and storage.
/// Starting one is a normal request (it only queues a run); the work happens in the background job with visible progress.
/// The review screen itself never reads PACT.
/// </summary>
public sealed class ReviewRefreshService(
    CollectionsOptions options, CollectionsCampaignOptions campaignOptions, PactReceivablesOptions sourceOptions,
    CollectionsReviewOptions reviewOptions, CollectionsAuthorizationService authorization, CollectionsClock clock,
    IPactReceivablesSource source, IReviewStore store, IReviewJobScheduler scheduler, ReviewQueryService query,
    ILogger<ReviewRefreshService> logger, CollectionsUnitLinkService? links = null,
    TigerCS.Application.Modules.Collections.Reconciliation.IFinancialReconciliationEvidence? reconciliation = null) : IReviewRefreshRunner
{
    public const string SourceLabel = "PACT receivables (companies 4 and 32)";

    /// <summary>Whether the reconciliation sign-off applies to the procedure that is configured now.</summary>
    public bool SourceReconciled => SourceGate().Reconciled;

    /// <summary>The one gate definition (sign-off, procedure, recent automated reconciliation) shared with the Campaigns preview.</summary>
    public TigerCS.Application.Modules.Collections.Reconciliation.SourceGateDto SourceGate() =>
        TigerCS.Application.Modules.Collections.Reconciliation.SourceGate.Evaluate(campaignOptions, sourceOptions, reconciliation, clock.UtcNow);

    /// <summary>
    /// The per-instalment minimum of every source read of the review pipeline (the refresh and the revalidation before an upload): none. The Campaigns preview reads the same way, so the three
    /// read the same instalments and calculate the same amounts; a minimum is applied to a customer's total, never to single instalments.
    /// </summary>
    public const decimal ReadMinimumAmount = 0m;

    public ReviewBuildContext Context(DateOnly asOf, DateTime readAtUtc, CrmLinkSet? crm = null) => new(asOf, readAtUtc, SourceLabel, sourceOptions.Currency,
        SourceReconciled, campaignOptions.LegalNoticeExportEnabled, campaignOptions.LegalCaseDispatchApproved,
        reviewOptions.FloatNoiseTolerance, sourceOptions.SourceStatusMap, crm, PolicyStart);

    /// <summary>The first due date the stage policy looks at: the configured PACT start date, the Campaigns preview's eligibility scope.</summary>
    public DateOnly PolicyStart => DateOnly.FromDateTime(sourceOptions.StartDate);

    /// <summary>
    /// The first due date the review pipeline READS: the whole history, like the Campaigns preview's default period, so a customer's Due / Overdue figures, Minimum Total and Manual amount
    /// see the same instalments in both modes. Which instalments make a unit a candidate is still decided from <see cref="PolicyStart"/> on.
    /// </summary>
    public static readonly DateOnly ReadStart = CollectionsCampaignAppService.HistoryStart;

    /// <summary>The CRM links of every unit of a source read (ONE bulk read), with the state of the CRM data; empty when no link service is configured.</summary>
    public Task<CrmLinkSet> ReadCrmAsync(IEnumerable<PactReceivableInstalment> items, CancellationToken ct) =>
        links is null ? Task.FromResult(CrmLinkSet.None) : links.GetLinksAsync(items.Select(r => (r.CompanyId, r.UnitCode)), ct);

    public async Task<CollectionsResult<ReviewRunDto>> StartAsync(CollectionsCaller caller, int? companyId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.Forbidden);
        return await StartCoreAsync(caller.EmployeeId, companyId, enqueue: true, ct);
    }

    /// <summary>
    /// The refresh the Auto dispatcher starts on a scheduled date (no person is involved). It only queues a run record; the dispatcher then runs it in its own job (<paramref name="ct"/>), so nothing is
    /// enqueued twice.
    /// </summary>
    public Task<CollectionsResult<ReviewRunDto>> StartScheduledAsync(CancellationToken ct) => StartCoreAsync(DispatchService.AutoActorId, null, enqueue: false, ct);

    private async Task<CollectionsResult<ReviewRunDto>> StartCoreAsync(Guid requestedBy, int? companyId, bool enqueue, CancellationToken ct)
    {
        if (!options.Enabled || !sourceOptions.Enabled) return CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.Disabled);
        if (companyId is not (null or 4 or 32))
            return CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.InvalidRequest, "Company must be 4 or 32.");

        var now = clock.UtcNow;
        var active = await store.GetActiveRunAsync(ct);
        // One refresh at a time: a request while one is queued or running gets that run back (it is also the database's rule, see TryAddRunAsync).
        if (active is not null && !IsDead(active, now)) return CollectionsResult<ReviewRunDto>.Ok(query.ToDto(active), CollectionsOutcome.Replayed);
        if (active is not null) await store.FailRunAsync(active.CollectionsReviewRunId, "The previous refresh did not finish and was replaced.", now, ct);

        var asOf = clock.BusinessDate;
        // With background jobs off the refresh runs inside this process, so it is also rate limited: the data that was just published for today is returned instead of reading the source again.
        if (enqueue && scheduler.RefreshRunsInProcess && reviewOptions.InProcessRefreshCooldownSeconds > 0
            && await store.GetCurrentRunAsync(ct) is { CompletedAtUtc: { } done } recent && recent.AsOfDate == asOf && recent.CompanyId == companyId
            && now - done < TimeSpan.FromSeconds(reviewOptions.InProcessRefreshCooldownSeconds))
            return CollectionsResult<ReviewRunDto>.Ok(query.ToDto(recent), CollectionsOutcome.Replayed);
        var run = new CollectionsReviewRun
        {
            Status = ReviewRunStatus.Queued, AsOfDate = asOf, CompanyId = companyId,
            DueFrom = ReadStart,
            DueTo = new DateOnly(asOf.Year, asOf.Month, DateTime.DaysInMonth(asOf.Year, asOf.Month)),
            Source = SourceLabel, SourceProcedureSuffix = sourceOptions.ProcedureSuffix ?? "", SourceReconciled = SourceReconciled,
            RequestedByEmployeeId = requestedBy, RequestedAtUtc = now, Phase = "Queued"
        };
        if (!await store.TryAddRunAsync(run, ct))
        {
            // Lost the race with a concurrent click: report the run that won.
            var winner = await store.GetActiveRunAsync(ct);
            return winner is null
                ? CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.FinanceUnavailable, "Could not queue the refresh. Retry.")
                : CollectionsResult<ReviewRunDto>.Ok(query.ToDto(winner), CollectionsOutcome.Replayed);
        }
        // Nothing would ever pick the run up (background jobs off) or the queue is unreachable: say so now instead of leaving the page on "Queued".
        string? notQueued = null;
        if (!enqueue) { /* the caller runs the refresh itself */ }
        else if (!scheduler.CanRunRefresh)
            notQueued = "Background jobs are disabled on this server (BackgroundJobs:Enabled is false), so the refresh cannot run. Enable them and retry.";
        else
        {
            try { scheduler.EnqueueRefresh(run.CollectionsReviewRunId); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Review refresh {RunId} could not be queued.", run.CollectionsReviewRunId);
                notQueued = $"The refresh could not be queued ({ex.GetType().Name}: {ex.Message}). Retry.";
            }
        }
        if (notQueued is not null)
        {
            await store.FailRunAsync(run.CollectionsReviewRunId, notQueued, clock.UtcNow, ct);
            run = await store.GetRunAsync(run.CollectionsReviewRunId, ct) ?? run;
            return CollectionsResult<ReviewRunDto>.Ok(query.ToDto(run), CollectionsOutcome.Accepted);
        }
        return CollectionsResult<ReviewRunDto>.Ok(query.ToDto(run), CollectionsOutcome.Accepted);
    }

    /// <summary>
    /// A database failure as the application sees it: the store's own <see cref="ReviewStorageException"/>, a provider <see cref="DbException"/>, or the <see cref="InvalidOperationException"/> that EF Core raises
    /// ("An exception has been raised that is likely due to a transient failure") around a transient SQL error for writes such as ExecuteUpdate / SaveChanges / transactions.
    /// </summary>
    private static bool IsDatabaseFailure(Exception ex) =>
        ex is DbException or ReviewStorageException || (ex is InvalidOperationException && ex.InnerException is not null && IsDatabaseFailure(ex.InnerException));

    /// <summary>Progress is informational: a failed progress write (for example a dropped connection) must not fail a refresh whose real work can still succeed.</summary>
    private async Task ProgressAsync(long runId, string phase, int percent, int? sourceRows, CancellationToken ct)
    {
        try { await store.UpdateRunProgressAsync(runId, phase, percent, sourceRows, ct); }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        { logger.LogWarning("Review refresh {RunId}: progress could not be written ({ExceptionType}); continuing.", runId, ex.GetType().Name); }
    }

    /// <summary>Records the failure; if even that write fails, the run is closed by the stuck-run timeout rather than the job crashing.</summary>
    private async Task FailSafelyAsync(long runId, string message)
    {
        try { await store.FailRunAsync(runId, message, clock.UtcNow, CancellationToken.None); }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        { logger.LogError("Review refresh {RunId} could not record its failure ({ExceptionType}); the stuck-run timeout will close it.", runId, ex.GetType().Name); }
    }

    private bool IsDead(CollectionsReviewRun run, DateTime now) =>
        now - (run.StartedAtUtc ?? run.RequestedAtUtc) > TimeSpan.FromMinutes(Math.Max(1, reviewOptions.StuckRunMinutes));

    /// <summary>The background job body. Idempotent: a second execution of an already-claimed run does nothing.</summary>
    public async Task RunAsync(long runId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        CollectionsReviewRun? run;
        try
        {
            if (!await store.TryStartRunAsync(runId, now, ct)) { logger.LogInformation("Review refresh {RunId} was already claimed or finished.", runId); return; }
            run = await store.GetRunAsync(runId, ct);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex) && !ct.IsCancellationRequested)
        {
            // Without this the run would sit in Queued (or Running) until the timeout. Say what happened now; the person can retry.
            logger.LogError(ex.InnerException ?? ex, "Review refresh {RunId} could not start ({ExceptionType}).", runId, ex.GetType().Name);
            await FailSafelyAsync(runId, ex is ReviewStorageException ? ex.Message : $"The review refresh could not start because of a database error ({ex.GetType().Name}). Retry.");
            return;
        }
        if (run is null) return;
        var phase = "Starting";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, reviewOptions.RefreshTimeoutMinutes)));
        try
        {
            phase = "Reading the financial source"; await ProgressAsync(runId, phase, 5, null, ct);
            // Every instalment of the window is read (no per-instalment minimum), exactly as the Campaigns preview reads it: the minimum is a rule about a customer's TOTAL (Minimum Total, the
            // stage threshold), applied by the calculation. A per-instalment minimum here made the two modes disagree: a customer whose instalments were each below it was missing from the
            // stored data, or stored with a smaller amount, while the preview counted them all.
            var snapshot = await source.ReadAsync(new PactReceivablesRequest(run.DueFrom, run.DueTo, run.CompanyId, MinAmount: ReadMinimumAmount), budget.Token);
            // A local snapshot that is not loaded or does not cover the window would silently omit receivables: refuse. (A merely stale snapshot is flagged on every record through its
            // read time, as before.)
            if (snapshot.Snapshot is { } snapshotStatus && (!snapshotStatus.RangeCovered || snapshotStatus.Companies.Any(c => !c.HasSnapshot)))
                throw new PactReceivablesSourceException($"Receivables data is not ready. {snapshotStatus.ReadyProblem}");
            phase = "Validating records"; await ProgressAsync(runId, phase, 45, snapshot.Items.Count, ct);
            if (snapshot.Items.Any(r => r.CompanyId is not (4 or 32)))
                throw new PactReceivablesSourceException("PACT returned a receivable without a valid company identity.");

            var crm = await ReadCrmAsync(snapshot.Items, budget.Token);
            var built = ReviewRecordBuilder.Build(snapshot.Items, Context(run.AsOfDate, snapshot.ReadAtUtc, crm));
            logger.LogInformation("Review refresh {RunId}: {Rows} source rows, {Records} records, {Noise} float-noise amounts normalized, {Settled} settled rows ignored.",
                runId, snapshot.Items.Count, built.Records.Count, built.FloatNormalizedRows, built.SettledRows);

            phase = "Saving records"; await ProgressAsync(runId, phase, 60, snapshot.Items.Count, ct);
            var chunk = Math.Clamp(reviewOptions.PersistBatchSize, 1, 2000);
            try
            {
                for (var i = 0; i < built.Records.Count; i += chunk)
                {
                    // Each batch is its own idempotent transaction (see ReviewStore.AddRecordsAsync): a retry after a lost connection cannot duplicate or skip records.
                    await store.AddRecordsAsync(runId, built.Records.Skip(i).Take(chunk).ToList(), budget.Token);
                    var percent = 60 + (int)(35.0 * Math.Min(built.Records.Count, i + chunk) / Math.Max(1, built.Records.Count));
                    await ProgressAsync(runId, "Saving records", percent, snapshot.Items.Count, ct);
                }
                await store.PublishRunAsync(runId, built.Records.Count, snapshot.Items.Count, snapshot.ReadAtUtc, clock.UtcNow, reviewOptions.RunsToKeep, ct);
            }
            catch (Exception ex) when (ex is DbException)
            {
                // A database error while SAVING is not a financial-source problem: say what failed.
                throw new ReviewStorageException($"Saving the review records failed ({ex.GetType().Name}). Nothing was published; the previous review data is still in use. Retry.", false, null, ex);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailSafelyAsync(runId, "The refresh was cancelled.");
            throw;
        }
        catch (OperationCanceledException)
        {
            await FailSafelyAsync(runId, "The financial source read exceeded the time budget. Retry, or refresh one company.");
        }
        catch (ReviewStorageException ex)
        {
            logger.LogError(ex.InnerException ?? ex, "Review refresh {RunId} could not save its records (transient={Transient}, sql={SqlNumber}).", runId, ex.Transient, ex.SqlNumber);
            await FailSafelyAsync(runId, ex.Message);
        }
        catch (PactReceivablesSourceException ex)
        {
            await FailSafelyAsync(runId, ex.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("Review refresh {RunId} failed ({ExceptionType}).", runId, ex.GetType().Name);
            await FailSafelyAsync(runId, $"The financial source could not be read (while {phase}).");
        }
        catch (Exception ex)
        {
            // Never leave the run Running: persist what actually went wrong so the page shows Failed with a retry.
            logger.LogError(ex, "Review refresh {RunId} failed unexpectedly while {Phase}.", runId, phase);
            // A transient database error wrapped by EF is reported as such (and with its SQL number when the store knows it), not as a bare InvalidOperationException.
            var message = IsDatabaseFailure(ex) && ex is not ReviewStorageException
                ? $"A database error interrupted the refresh while {phase} ({ex.GetType().Name} / {ex.InnerException?.GetType().Name}). The previous review data is still in use. Retry."
                : $"Failed while {phase}: {ex.GetType().Name}: {ex.Message}";
            await FailSafelyAsync(runId, message);
        }
    }
}
