using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Reconciliation;

/// <summary>The unit / instalment comparison of the snapshot with a fresh PACT read: what counts as a difference, and what does not.</summary>
public sealed class FinancialReconcilerTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);   // Dubai month = October 2026
    private static readonly DateOnly From = new(2026, 1, 1), Through = new(2026, 10, 31);

    private static ReconLine L(string unit, string voucher, DateOnly due, decimal amount, string tenant = "T1", long unitId = 11) => new(tenant, unitId, unit, voucher, due, amount);
    private static ReconComparison Run(IEnumerable<ReconLine> snapshot, IEnumerable<ReconLine> source, decimal tolerance = 0.01m) => FinancialReconciler.Compare(snapshot, source, From, Through, Today, tolerance);

    // ---------------------------------------------------------------- the AED 2.00 business tolerance (default)

    private static ReconComparison RunDefault(IEnumerable<ReconLine> snapshot, IEnumerable<ReconLine> source) => FinancialReconciler.Compare(snapshot, source, From, Through, Today);

    [Fact]
    public void TheDefaultToleranceIsTwoDirhams()
    {
        Assert.Equal(2.00m, FinancialReconciler.DefaultTolerance);
        Assert.Equal(2.00m, new TigerCS.Application.Modules.Collections.CollectionsCampaignOptions().ReconciliationToleranceAed);
    }

    [Theory]
    [InlineData("1000.00", "1000.00", true)]     // 0.00
    [InlineData("1000.00", "1001.99", true)]     // 1.99
    [InlineData("1000.00", "1002.00", true)]     // 2.00 exactly: inclusive
    [InlineData("1000.00", "1002.01", false)]    // 2.01
    [InlineData("1002.00", "1000.00", true)]     // either side may be the larger one
    [InlineData("1002.01", "1000.00", false)]
    [InlineData("1000.00", "998.00", true)]
    [InlineData("1000.00", "997.99", false)]
    public void ADifferenceOfUpToTwoDirhamsIsEquivalent_AndMoreIsAMismatch_ForTheSameInstalment(string snapshotAmount, string pactAmount, bool equivalent)
    {
        var snapshot = decimal.Parse(snapshotAmount, System.Globalization.CultureInfo.InvariantCulture);
        var pact = decimal.Parse(pactAmount, System.Globalization.CultureInfo.InvariantCulture);
        var r = RunDefault([L("TP140-101", "V1", new(2026, 9, 5), snapshot)], [L("TP140-101", "V1", new(2026, 9, 5), pact)]);
        Assert.Equal(equivalent, r.Matches);
        if (equivalent) { Assert.Empty(r.Diffs); return; }
        var instalment = Assert.Single(Assert.Single(r.Diffs).Instalments);
        Assert.Equal(ReconDifferenceKinds.AmountMismatch, instalment.Kind);
        Assert.Equal((snapshot, pact, snapshot - pact), (instalment.SnapshotAmount, instalment.SourceAmount, instalment.Difference));   // both amounts and the difference are reported
    }

    [Fact]
    public void AMismatchReportsBothAmountsAndTheDifference_InTheUnitSummaryToo()
    {
        var r = RunDefault([L("TP140-101", "V1", new(2026, 9, 5), 1000m)], [L("TP140-101", "V1", new(2026, 9, 5), 1002.01m)]);
        var unit = Assert.Single(r.Diffs);
        Assert.Contains("1000.00", unit.Summary); Assert.Contains("1002.01", unit.Summary); Assert.Contains("-2.01", unit.Summary);
        Assert.Equal(-2.01m, unit.TotalDifference);
    }

    [Theory]
    [InlineData("1000.004", "1002.004", true)]   // 1000.00 vs 1002.00 after rounding: 2.00
    [InlineData("1000.005", "1002.004", true)]   // 1000.01 vs 1002.00: 1.99
    [InlineData("1000.004", "1002.005", false)]  // 1000.00 vs 1002.01: 2.01 (half away from zero, not banker's rounding)
    public void AmountsAreRoundedToTwoDecimalsBeforeTheyAreCompared(string snapshotAmount, string pactAmount, bool equivalent)
    {
        var snapshot = decimal.Parse(snapshotAmount, System.Globalization.CultureInfo.InvariantCulture);
        var pact = decimal.Parse(pactAmount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(equivalent, RunDefault([L("TP140-101", "V1", new(2026, 9, 5), snapshot)], [L("TP140-101", "V1", new(2026, 9, 5), pact)]).Matches);
    }

    [Fact]
    public void TheAllowanceIsNotAddedUpAcrossInstalments_ManySmallDifferencesCannotHideABiggerOne()
    {
        // Each instalment is within AED 2.00 on its own, but together the unit is AED 4.50 away from PACT: that is a larger discrepancy and it is reported (the unit total gets the same single allowance).
        var snapshot = new[] { L("TP140-101", "V1", new(2026, 8, 5), 1000m), L("TP140-101", "V2", new(2026, 9, 5), 1000m), L("TP140-101", "V3", new(2026, 10, 5), 1000m) };
        var source = new[] { L("TP140-101", "V1", new(2026, 8, 5), 1001.5m), L("TP140-101", "V2", new(2026, 9, 5), 1001.5m), L("TP140-101", "V3", new(2026, 10, 5), 1001.5m) };
        var r = RunDefault(snapshot, source);
        Assert.False(r.Matches);
        var unit = Assert.Single(r.Diffs);
        Assert.Equal(-4.5m, unit.TotalDifference); Assert.Empty(unit.Instalments);       // none of them exceeds AED 2.00 alone
        Assert.Contains("not added up", unit.Summary);
        // Within the allowance as a whole: equivalent.
        Assert.True(RunDefault(snapshot, [L("TP140-101", "V1", new(2026, 8, 5), 1000.5m), L("TP140-101", "V2", new(2026, 9, 5), 1000.5m), L("TP140-101", "V3", new(2026, 10, 5), 1000.5m)]).Matches);
    }

    [Fact]
    public void OffsettingDifferencesCannotCancelEachOtherOut()
    {
        // +3.00 on one instalment and -3.00 on another: the unit total agrees, each instalment does not.
        var r = RunDefault([L("TP140-101", "V1", new(2026, 8, 5), 1000m), L("TP140-101", "V2", new(2026, 9, 5), 1000m)],
            [L("TP140-101", "V1", new(2026, 8, 5), 1003m), L("TP140-101", "V2", new(2026, 9, 5), 997m)]);
        Assert.False(r.Matches);
        Assert.Equal(2, Assert.Single(r.Diffs).Instalments.Count(i => i.Kind == ReconDifferenceKinds.AmountMismatch));
    }

    [Fact]
    public void IdentityMismatchesAreNeverTolerated_WhateverTheAmount()
    {
        // The same small amount, but the instalment is missing on one side, or its due date differs, or it belongs to another unit.
        Assert.False(RunDefault([L("TP140-101", "V1", new(2026, 9, 5), 1.50m)], []).Matches);
        Assert.False(RunDefault([], [L("TP140-101", "V1", new(2026, 9, 5), 1.50m)]).Matches);
        var dueDate = RunDefault([L("TP140-101", "V1", new(2026, 9, 5), 1000m)], [L("TP140-101", "V1", new(2026, 10, 5), 1000m)]);
        Assert.False(dueDate.Matches); Assert.Equal(ReconDifferenceKinds.DueDateMismatch, Assert.Single(Assert.Single(dueDate.Diffs).Instalments).Kind);
        Assert.False(RunDefault([L("TP140-101", "V1", new(2026, 9, 5), 1000m)], [L("TP140-102", "V1", new(2026, 9, 5), 1000m, unitId: 12)]).Matches);
    }

    [Fact]
    public void InvalidAmountsStayOutsideTheComparison_NegativeAndZeroAreNotReceivables_AndNeverMatchAPositiveBalance()
    {
        // A negative or zero remaining amount is not a receivable on either side; a positive balance on one side against it is an instalment missing on the other, not an equivalence.
        Assert.False(RunDefault([L("TP140-101", "V1", new(2026, 9, 5), -5m)], [L("TP140-101", "V1", new(2026, 9, 5), 1m)]).Matches);
        Assert.False(RunDefault([L("TP140-101", "V1", new(2026, 9, 5), 0m)], [L("TP140-101", "V1", new(2026, 9, 5), 1m)]).Matches);
    }

    [Fact]
    public void ASmallPositiveBalanceBelowHalfAFilsIsStillAReceivable_NotSettled()
    {
        // 0.004 is a positive remaining balance: it is compared (as 0.00 after rounding) and an instalment that exists on one side only is reported, never read as paid.
        Assert.False(RunDefault([L("TP140-101", "V1", new(2026, 9, 5), 0.004m)], []).Matches);
        Assert.True(RunDefault([L("TP140-101", "V1", new(2026, 9, 5), 0.004m)], [L("TP140-101", "V1", new(2026, 9, 5), 0.004m)]).Matches);
    }

    [Fact]
    public void IdenticalSides_Match_AndCountEveryUnit()
    {
        ReconLine[] lines = [L("TP140-101", "V1", new(2026, 9, 5), 1000m), L("TP140-101", "V2", new(2026, 10, 5), 500m), L("TP140-102", "V3", new(2026, 8, 5), 250.5m, unitId: 12)];
        var r = Run(lines, lines);
        Assert.True(r.Matches);
        Assert.Equal((2, 2, 3, 3), (r.SnapshotUnits, r.SourceUnits, r.SnapshotLines, r.SourceLines));
        Assert.Equal(2, r.MatchedUnits); Assert.Empty(r.Diffs);
    }

    [Fact]
    public void RoundingWithinTheTolerance_IsNotADifference_ButOneFilsBeyondItIs()
    {
        var snapshot = new[] { L("TP140-101", "V1", new(2026, 9, 5), 1000.004m) };
        Assert.True(Run(snapshot, [L("TP140-101", "V1", new(2026, 9, 5), 1000.00m)]).Matches);
        var r = Run(snapshot, [L("TP140-101", "V1", new(2026, 9, 5), 1000.02m)]);
        Assert.False(r.Matches);
        var diff = Assert.Single(r.Diffs);
        Assert.Equal(ReconDifferenceKinds.AmountMismatch, Assert.Single(diff.Instalments).Kind);
    }

    [Fact]
    public void APartialPaymentSinceTheSnapshot_IsAnAmountMismatchOfThatInstalment_NotOfTheWholeUnit()
    {
        var r = Run([L("TP140-101", "V1", new(2026, 9, 5), 1000m), L("TP140-101", "V2", new(2026, 10, 5), 500m)], [L("TP140-101", "V1", new(2026, 9, 5), 400m), L("TP140-101", "V2", new(2026, 10, 5), 500m)]);
        var instalment = Assert.Single(Assert.Single(r.Diffs).Instalments);
        Assert.Equal(("V1", 1000m, 400m), (instalment.Voucher, instalment.SnapshotAmount, instalment.SourceAmount));
    }

    [Fact]
    public void AnInstalmentMissingOnOneSide_IsNamedWithTheSideItIsMissingFrom()
    {
        var r = Run([L("TP140-101", "V1", new(2026, 9, 5), 1000m), L("TP140-101", "V9", new(2026, 9, 6), 77m)], [L("TP140-101", "V1", new(2026, 9, 5), 1000m), L("TP140-101", "V8", new(2026, 9, 7), 66m)]);
        var kinds = Assert.Single(r.Diffs).Instalments.Select(i => i.Kind).OrderBy(k => k).ToArray();
        Assert.Equal([ReconDifferenceKinds.MissingInSnapshot, ReconDifferenceKinds.MissingInSource], kinds);
    }

    [Fact]
    public void ASameVoucherWithAnotherDueDate_IsADueDateMismatch_AndSaysWhenItCrossesTheDueOverdueBoundary()
    {
        // Due 30 September (Overdue today) on one side, 2 October (Due) on the other.
        var r = Run([L("TP140-101", "V1", new(2026, 9, 30), 1000m)], [L("TP140-101", "V1", new(2026, 10, 2), 1000m)]);
        var i = Assert.Single(Assert.Single(r.Diffs).Instalments);
        Assert.Equal(ReconDifferenceKinds.DueDateMismatch, i.Kind);
        Assert.True(i.CrossesDueOverdueBoundary);
        var d = r.Diffs[0];
        Assert.Equal((1000m, 0m, 0m, 1000m), (d.SnapshotOverdue, d.SnapshotDue, d.SourceOverdue, d.SourceDue));
        Assert.Contains("Due on one side and Overdue on the other", d.Summary);
        // The same two dates inside one month do not cross the boundary.
        Assert.False(Run([L("TP140-101", "V1", new(2026, 10, 1), 1m)], [L("TP140-101", "V1", new(2026, 10, 20), 1m)]).Diffs[0].Instalments[0].CrossesDueOverdueBoundary);
    }

    [Fact]
    public void UnitsOnlyOnOneSide_AreCountedSeparately()
    {
        var r = Run([L("TP140-101", "V1", new(2026, 9, 5), 10m), L("TP140-102", "V2", new(2026, 9, 5), 20m, unitId: 12)], [L("TP140-101", "V1", new(2026, 9, 5), 10m), L("TP140-103", "V3", new(2026, 9, 5), 30m, unitId: 13)]);
        Assert.Equal((2, 1, 1), (r.MismatchedUnits, r.UnitsOnlyInSnapshot, r.UnitsOnlyInSource));
        Assert.Contains("none in PACT", r.Diffs.Single(d => d.UnitCode == "TP140-102").Summary);
        Assert.Contains("none in the snapshot", r.Diffs.Single(d => d.UnitCode == "TP140-103").Summary);
    }

    [Fact]
    public void BothSidesAreReducedByTheSnapshotsOwnValidityRules_SoARuleDifferenceIsNeverReported()
    {
        // Settled, UnitId 0, cancelled '*' units, blank tenant and a 1E-12 float residue are not in the snapshot, so PACT returning them is not a difference.
        var noise = new[]
        {
            L("TP140-101", "S1", new(2026, 9, 5), 0m), L("TP140-102", "S2", new(2026, 9, 5), 50m, unitId: 0), L("513*", "S3", new(2026, 9, 5), 50m, unitId: 14),
            L("TP140-104", "S4", new(2026, 9, 5), 50m, tenant: " ", unitId: 15), L("TP140-105", "S5", new(2026, 9, 5), 1E-12m, unitId: 16)
        };
        var real = L("TP140-106", "V1", new(2026, 9, 5), 80m, unitId: 17);
        Assert.True(Run([real], [real, .. noise]).Matches);
    }

    [Fact]
    public void InstalmentsOutsideTheWindow_AreIgnoredOnBothSides()
    {
        var r = Run([L("TP140-101", "V1", new(2026, 9, 5), 10m), L("TP140-101", "OLD", new(2025, 12, 31), 99m)], [L("TP140-101", "V1", new(2026, 9, 5), 10m), L("TP140-101", "FUT", new(2026, 11, 1), 99m)]);
        Assert.True(r.Matches);
    }

    [Fact]
    public void DifferencesAreCappedForDiagnostics_ButTheCountsStayComplete()
    {
        var snapshot = Enumerable.Range(1, 30).Select(i => L($"TP140-{i}", $"V{i}", new(2026, 9, 5), 10m, unitId: i)).ToList();
        var r = FinancialReconciler.Compare(snapshot, [], From, Through, Today, 0.01m, maxDiffs: 5);
        Assert.Equal((30, 5, true), (r.MismatchedUnits, r.Diffs.Count, r.DiffsTruncated));
    }

    [Fact]
    public void TwoEmptySides_DoNotMatch_BecauseNothingWasCompared() => Assert.False(Run([], []).Matches);

    [Fact]
    public void RepeatedInstalments_AreCountedAsEvidence_PerSide_WithoutBeingTreatedAsADifference()
    {
        var twice = new[] { L("TP140-101", "V1", new(2026, 9, 5), 60m), L("TP140-101", "V1", new(2026, 9, 5), 60m), L("TP140-101", "", new(2026, 9, 6), 5m), L("TP140-101", "", new(2026, 9, 6), 5m) };
        var r = Run(twice, twice);
        Assert.True(r.Matches);
        Assert.Equal((1, 1), (r.SnapshotDuplicateInstalments, r.SourceDuplicateInstalments));     // the blank-voucher pair is not identifiable, so it is not counted
        Assert.Equal((0, 1), (Run([L("TP140-101", "V1", new(2026, 9, 5), 120m)], twice.Take(2)).SnapshotDuplicateInstalments, Run([L("TP140-101", "V1", new(2026, 9, 5), 120m)], twice.Take(2)).SourceDuplicateInstalments));
    }

    [Fact]
    public void ADuplicateVoucherOnTheSameDay_IsComparedBySum()
    {
        var snapshot = new[] { L("TP140-101", "V1", new(2026, 9, 5), 60m), L("TP140-101", "V1", new(2026, 9, 5), 40m) };
        Assert.True(Run(snapshot, [L("TP140-101", "V1", new(2026, 9, 5), 100m)]).Matches);
    }
}
