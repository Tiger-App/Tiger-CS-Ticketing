using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Re-reads current balances for the companies involved in a dispatch or a suppression sweep and rebuilds the review records, keyed by
/// <c>RecordKey</c>. One read per company, run concurrently (they hit different PACT databases).
///
/// <para><b>Source limitation.</b> The PACT report procedures accept only <c>@StartDate</c>, <c>@EndDate</c> and <c>@MinAmount</c> (V2 adds
/// <c>@IncludeSettled</c> and <c>@StrictIdentity</c>); none selects a customer, unit or tag, and the ledger and allocation work runs in full whatever the
/// window. The window cannot be narrowed safely either: a unit's quoted amount sums every qualifying instalment from the configured start date, and an
/// older instalment that becomes unpaid again (a reversed cheque) would be missed. So the cost of one revalidation is one full company read, which is
/// why it runs in the background job with a lease heartbeat. Targeted reads need a new, separately reviewed procedure parameter; none is invented here.</para>
/// <para><b>Same filters as the preview.</b> The read uses the same minimum outstanding amount (<c>DefaultMinOutstandingAmount</c>, remaining &gt;= value per instalment), the same
/// configured start date and, with the local snapshot, the same rows as the Campaigns preview and export, and requires the snapshot to be fresh and to cover the window.</para>
/// </summary>
public sealed class CurrentBalanceReader(PactReceivablesOptions sourceOptions, IPactReceivablesSource source, ReviewRefreshService refresh,
    ILogger<CurrentBalanceReader> logger)
{
    /// <summary>The rebuilt records per as-of date, and <c>Gate</c>: the source gate as it stands for the snapshots just read (evidence bound to their publish time), per company.</summary>
    public sealed record Result(IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, CollectionsReviewRecord>> ByAsOfDate, long ElapsedMs, int SourceRows,
        TigerCS.Application.Modules.Collections.Reconciliation.SourceGateDto Gate);

    /// <summary>Whether the financial source is reconciled for both companies right now (the one gate definition the refresh uses).</summary>
    public bool SourceReconciled => refresh.SourceReconciled;

    /// <summary>The gate right now, per company (before any balance is read; the read itself binds it to the snapshot publish).</summary>
    public TigerCS.Application.Modules.Collections.Reconciliation.SourceGateDto Gate() => refresh.SourceGate();

    public async Task<Result> ReadAsync(IReadOnlyCollection<int> companies, IReadOnlyCollection<DateOnly> asOfDates, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var latest = asOfDates.Max();
        var from = ReviewRefreshService.ReadStart;   // the same window as the refresh (and the preview), so the amounts can be compared
        var to = new DateOnly(latest.Year, latest.Month, DateTime.DaysInMonth(latest.Year, latest.Month));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(sourceOptions.RequestBudgetSeconds));

        var snapshots = await Task.WhenAll(companies.Distinct().Select(company =>
            source.ReadAsync(new PactReceivablesRequest(from, to, company, MinAmount: ReviewRefreshService.ReadMinimumAmount), budget.Token)));   // the same read as the refresh, so the amounts can be compared

        // A local snapshot that is stale, not loaded or does not cover the window must never be read as "no balance left": the dispatch would then
        // treat every approved record as paid. The revalidation fails (nothing is sent) until the snapshot is fresh and complete.
        foreach (var snapshot in snapshots)
            if (snapshot.Snapshot is { IsReady: false } status)
                throw new PactReceivablesSourceException($"Receivables data is not ready. {status.ReadyProblem}");

        // The contacts are re-linked NOW from the current CRM owner data (one bulk read) - the contact stored at approval time is never trusted. If CRM cannot be used, every
        // record comes back CrmUnavailable (needs review), the comparison below sees a change, and nothing is sent.
        var crm = await refresh.ReadCrmAsync(snapshots.SelectMany(s => s.Items), budget.Token);
        // The gate for exactly these publishes: evidence taken against an older publish of a company does not cover the balances just read, and the units a reconciled company holds
        // back are stamped on the rebuilt records, so a held unit shows up as a changed record and is not sent.
        var published = new Dictionary<int, DateTime?>();
        foreach (var snapshot in snapshots) foreach (var (company, at) in ReviewRefreshService.PublishedUtc(snapshot) ?? new Dictionary<int, DateTime?>()) published[company] = at;
        var gate = refresh.SourceGate(published);
        var byDate = new Dictionary<DateOnly, IReadOnlyDictionary<string, CollectionsReviewRecord>>();
        foreach (var asOf in asOfDates.Distinct())
        {
            var map = new Dictionary<string, CollectionsReviewRecord>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
                foreach (var record in ReviewRecordBuilder.Build(snapshot.Items, refresh.Context(asOf, snapshot.ReadAtUtc, crm, gate)).Records)
                    map[record.RecordKey] = record;
            byDate[asOf] = map;
        }
        var rows = snapshots.Sum(s => s.Items.Count);
        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation("Current balances read for {Companies} company(ies): {Rows} source rows in {ElapsedMs} ms.", companies.Count, rows, elapsed);
        return new Result(byDate, elapsed, rows, gate);
    }
}
