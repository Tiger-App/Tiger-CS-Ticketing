namespace TigerCS.Domain.Modules.Collections;

/// <summary>One unpaid instalment as one side of a reconciliation sees it (remaining amount, due day).</summary>
public sealed record ReconLine(string TenantId, long UnitId, string UnitCode, string Voucher, DateOnly DueDate, decimal Amount);

/// <summary>The kinds of difference a reconciliation reports. Each is a different question for the person who investigates it.</summary>
public static class ReconDifferenceKinds
{
    /// <summary>The source holds an unpaid instalment the snapshot does not: the snapshot is missing money (or is older than the source).</summary>
    public const string MissingInSnapshot = "MissingInSnapshot";
    /// <summary>The snapshot holds an instalment the source no longer has: it was paid / cancelled after the snapshot, or the snapshot is wrong.</summary>
    public const string MissingInSource = "MissingInSource";
    /// <summary>Same instalment on both sides with different remaining amounts (a partial payment since the snapshot, rounding beyond the tolerance, or a calculation difference).</summary>
    public const string AmountMismatch = "AmountMismatch";
    /// <summary>Same voucher on both sides with different due dates; when the two dates fall in different months the instalment is Due on one side and Overdue on the other.</summary>
    public const string DueDateMismatch = "DueDateMismatch";
}

public sealed record ReconInstalmentDiff(string Kind, string Voucher, DateOnly? SnapshotDue, DateOnly? SourceDue, decimal? SnapshotAmount, decimal? SourceAmount, bool CrossesDueOverdueBoundary)
{
    /// <summary>Snapshot minus PACT (both rounded to two decimals); null when the instalment exists on one side only.</summary>
    public decimal? Difference => SnapshotAmount is { } s && SourceAmount is { } p ? s - p : null;
}

public sealed record ReconUnitDiff(
    string TenantId, long UnitId, string UnitCode, decimal SnapshotTotal, decimal SourceTotal, int SnapshotCount, int SourceCount,
    decimal SnapshotDue, decimal SourceDue, decimal SnapshotOverdue, decimal SourceOverdue, IReadOnlyList<ReconInstalmentDiff> Instalments, string Summary)
{
    /// <summary>Snapshot total minus PACT total for the unit.</summary>
    public decimal TotalDifference => SnapshotTotal - SourceTotal;
}

/// <summary>Outcome of comparing the two sides for ONE company. <c>Matches</c> is true only when every unit agrees within the tolerance.</summary>
public sealed record ReconComparison(
    int SnapshotUnits, int SourceUnits, int SnapshotLines, int SourceLines, decimal SnapshotTotal, decimal SourceTotal, int MatchedUnits, int MismatchedUnits,
    int UnitsOnlyInSnapshot, int UnitsOnlyInSource, decimal MaxUnitDelta, IReadOnlyList<ReconUnitDiff> Diffs, bool DiffsTruncated,
    int SnapshotDuplicateInstalments = 0, int SourceDuplicateInstalments = 0)
{
    public bool Matches => MismatchedUnits == 0 && (SnapshotLines > 0 || SourceLines > 0);
}

/// <summary>
/// Compares the unpaid instalments the local snapshot holds with a fresh, independent read of the same PACT procedure, unit by unit and instalment by instalment.
/// Both sides are first reduced by the SAME validity rules the snapshot applies when it publishes (remaining amount &gt; 0 at 4 decimals, a real unit id and code,
/// no '*' cancelled units, a customer id), restricted to the same due-date window, so a difference is a real difference and not a rule mismatch.
/// Due / Overdue are measured against the Dubai month of <c>today</c>, exactly as the pages do (Overdue = due before its first day, Due = inside it).
/// <para>
/// Amounts are first rounded to two decimals (decimal arithmetic, <see cref="MidpointRounding.AwayFromZero"/>); PACT is the authoritative side. Business rule: for the same instalment (unit, voucher and
/// due day) a difference of up to <see cref="DefaultTolerance"/> (AED 2.00, inclusive) counts as financially equivalent; anything larger is a mismatch, reported with both amounts and the difference.
/// The tolerance is a single allowance, never accumulated: it applies to each instalment on its own, and the unit's total difference must ALSO stay within the same single allowance, so many small
/// differences cannot add up to a larger discrepancy that goes unseen. A missing or extra instalment, a different due date or a different unit identity is never tolerated.
/// </para>
/// </summary>
public static class FinancialReconciler
{
    public const decimal DefaultTolerance = 2.00m;

    public static bool IsValid(ReconLine l) =>
        decimal.Round(l.Amount, 4) > 0 && l.UnitId > 0 && !string.IsNullOrWhiteSpace(l.TenantId)
        && !string.IsNullOrWhiteSpace(l.UnitCode) && l.UnitCode.Trim() != "0" && !l.UnitCode.Contains('*');

    private static decimal Fils(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static (string Tenant, long Unit, string Code) Key(ReconLine l) => (l.TenantId.Trim(), l.UnitId, l.UnitCode.Trim());

    public static ReconComparison Compare(IEnumerable<ReconLine> snapshot, IEnumerable<ReconLine> source, DateOnly from, DateOnly through, DateOnly today,
        decimal tolerance = DefaultTolerance, int maxDiffs = 500)
    {
        tolerance = Math.Max(0m, tolerance);
        bool InWindow(ReconLine l) => l.DueDate >= from && l.DueDate <= through;
        // Validity is judged on the value as read (a positive balance is a receivable even below 0.005); the compared amount is then rounded to fils like every other monetary value.
        var a = snapshot.Where(l => IsValid(l) && InWindow(l)).Select(l => l with { Amount = Fils(l.Amount) }).ToList();
        var b = source.Where(l => IsValid(l) && InWindow(l)).Select(l => l with { Amount = Fils(l.Amount) }).ToList();
        var ga = a.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var gb = b.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        decimal Overdue(IEnumerable<ReconLine> l) => l.Where(x => x.DueDate < monthStart).Sum(x => x.Amount);
        decimal Due(IEnumerable<ReconLine> l) => l.Where(x => x.DueDate >= monthStart).Sum(x => x.Amount);
        bool Crosses(DateOnly x, DateOnly y) => (x < monthStart) != (y < monthStart);

        var diffs = new List<ReconUnitDiff>();
        int matched = 0, mismatched = 0, onlySnapshot = 0, onlySource = 0; decimal maxDelta = 0m;
        foreach (var key in ga.Keys.Union(gb.Keys).OrderBy(k => k.Code, StringComparer.Ordinal).ThenBy(k => k.Tenant, StringComparer.Ordinal).ThenBy(k => k.Unit))
        {
            ga.TryGetValue(key, out var la); gb.TryGetValue(key, out var lb);
            la ??= []; lb ??= [];
            var instalments = Instalments(la, lb, tolerance, Crosses);
            var totalDelta = Math.Abs(la.Sum(x => x.Amount) - lb.Sum(x => x.Amount));
            if (instalments.Count == 0 && totalDelta <= tolerance) { matched++; continue; }
            mismatched++; maxDelta = Math.Max(maxDelta, totalDelta);
            if (la.Count == 0) onlySource++; else if (lb.Count == 0) onlySnapshot++;
            if (diffs.Count < maxDiffs)
                diffs.Add(new ReconUnitDiff(key.Tenant, key.Unit, key.Code, la.Sum(x => x.Amount), lb.Sum(x => x.Amount), la.Count, lb.Count,
                    Due(la), Due(lb), Overdue(la), Overdue(lb), instalments, Summary(la, lb, instalments)));
        }
        // Informational: the same voucher repeated for one unit and due day (the snapshot does not remove such repeats; whether one is legitimate is a business question).
        static int Repeats(List<ReconLine> l) => l.Where(x => x.Voucher.Trim().Length > 0).GroupBy(x => (Key(x), x.Voucher.Trim(), x.DueDate)).Count(g => g.Count() > 1);
        return new ReconComparison(ga.Count, gb.Count, a.Count, b.Count, a.Sum(x => x.Amount), b.Sum(x => x.Amount), matched, mismatched, onlySnapshot, onlySource, maxDelta, diffs, mismatched > diffs.Count, Repeats(a), Repeats(b));
    }

    private static List<ReconInstalmentDiff> Instalments(List<ReconLine> snapshot, List<ReconLine> source, decimal tolerance, Func<DateOnly, DateOnly, bool> crosses)
    {
        var result = new List<ReconInstalmentDiff>();
        // Same voucher and same due day on both sides: compare the amounts (summed when a voucher repeats on one day).
        var sa = snapshot.GroupBy(l => (l.Voucher.Trim(), l.DueDate)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var sb = source.GroupBy(l => (l.Voucher.Trim(), l.DueDate)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var onlyA = new List<((string Voucher, DateOnly Due) Key, decimal Amount)>();
        var onlyB = new List<((string Voucher, DateOnly Due) Key, decimal Amount)>();
        foreach (var (k, amount) in sa)
        {
            if (!sb.TryGetValue(k, out var other)) onlyA.Add((k, amount));
            else if (Math.Abs(amount - other) > tolerance) result.Add(new(ReconDifferenceKinds.AmountMismatch, k.Item1, k.DueDate, k.DueDate, amount, other, false));
        }
        foreach (var (k, amount) in sb) if (!sa.ContainsKey(k)) onlyB.Add((k, amount));
        // Leftovers with the same (non-empty) voucher are one instalment whose due date differs; the rest are missing on one side.
        foreach (var left in onlyA.ToList())
        {
            var pair = left.Key.Voucher.Length == 0 ? default : onlyB.FirstOrDefault(r => r.Key.Voucher == left.Key.Voucher);
            if (pair.Key.Voucher is null || pair.Key.Voucher.Length == 0) continue;
            result.Add(new(ReconDifferenceKinds.DueDateMismatch, left.Key.Voucher, left.Key.Due, pair.Key.Due, left.Amount, pair.Amount, crosses(left.Key.Due, pair.Key.Due)));
            onlyA.Remove(left); onlyB.Remove(pair);
        }
        result.AddRange(onlyA.Select(x => new ReconInstalmentDiff(ReconDifferenceKinds.MissingInSource, x.Key.Voucher, x.Key.Due, null, x.Amount, null, false)));
        result.AddRange(onlyB.Select(x => new ReconInstalmentDiff(ReconDifferenceKinds.MissingInSnapshot, x.Key.Voucher, null, x.Key.Due, null, x.Amount, false)));
        return result.OrderBy(d => d.SnapshotDue ?? d.SourceDue).ThenBy(d => d.Voucher, StringComparer.Ordinal).ToList();
    }

    private static string Summary(List<ReconLine> snapshot, List<ReconLine> source, List<ReconInstalmentDiff> instalments)
    {
        if (snapshot.Count == 0) return "The unit has unpaid instalments in PACT but none in the snapshot.";
        if (source.Count == 0) return "The unit has unpaid instalments in the snapshot but none in PACT (paid, cancelled or no longer listed).";
        var kinds = instalments.GroupBy(i => i.Kind).Select(g => $"{g.Count()} {g.Key}");
        var boundary = instalments.Any(i => i.CrossesDueOverdueBoundary) ? " An instalment is Due on one side and Overdue on the other." : "";
        var totals = $" Snapshot total {snapshot.Sum(x => x.Amount):0.00}, PACT total {source.Sum(x => x.Amount):0.00}, difference {snapshot.Sum(x => x.Amount) - source.Sum(x => x.Amount):0.00}.";
        return instalments.Count == 0 ? "No single instalment differs beyond the tolerance, but together they do (the tolerance is not added up across instalments)." + totals : string.Join(", ", kinds) + "." + boundary + totals;
    }
}
