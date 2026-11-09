using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The payment-classification rule, end to end: the verified REMAINING amount decides (0 = Paid, more = Unpaid, missing / invalid / contradicted = Needs Review), identically in the
/// domain rule, the Receivables classification, the customers API, the campaign records (Manual and Auto eligibility and the send) and the Genesys export, which stays separate from
/// eligibility. The harness clock is 14 Oct 2026, a scheduled day for the Current Month reminder.
/// </summary>
public sealed class PaymentClassificationTests
{
    private static readonly IReadOnlyDictionary<string, string> NoMap = new Dictionary<string, string>();

    // ------------------------------------------------------------ the one rule, in every classifier

    [Theory]
    [InlineData(0, "Installment", "Paid")]
    [InlineData(0, "Paid", "Paid")]
    [InlineData(250, "Installment", "Unpaid")]
    [InlineData(250, null, "Unpaid")]
    [InlineData(250, "Paid", "Unknown")]
    [InlineData(-1, "Installment", "Unknown")]
    public void ZeroIsPaid_PositiveIsUnpaid_ContradictedOrNegativeNeedsReview_InEveryClassifier(double remaining, string? status, string expected)
    {
        var amount = (decimal)remaining;
        Assert.Equal(Enum.Parse<ReviewPaymentStatus>(expected), PaymentStatusRules.Derive(status, amount));
        Assert.Equal(expected == "Paid" ? "FullyPaid" : expected, CollectionsPaymentFilters.Classify(amount, null, null, status, false));
        Assert.Equal(expected, PactReceivableCustomersAppService.PaymentStatus(status, amount, NoMap));
    }

    [Fact]
    public void AMissingRemainingAmount_NeedsReview_InEveryClassifier()
    {
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Installment", null));
        Assert.Equal("Unknown", CollectionsPaymentFilters.Classify(null, null, null, "Installment", false));
        Assert.Equal("Unknown", PactReceivableCustomersAppService.PaymentStatus("Installment", null, NoMap));
    }

    [Theory]
    [InlineData(300, 500, 200, "Unpaid")]       // original = paid + remaining
    [InlineData(300, 500, 100, "Unknown")]      // 100 + 300 != 500
    [InlineData(600, 500, 0, "Unknown")]        // remaining above the original
    [InlineData(300, 500, -1, "Unknown")]       // negative paid
    [InlineData(0, 500, 500, "Paid")]
    public void AnInconsistentBreakdown_NeedsReview_AConsistentOneDoesNotChangeTheRule(double remaining, double original, double paid, string expected)
    {
        Assert.Equal(Enum.Parse<ReviewPaymentStatus>(expected), PaymentStatusRules.Derive("Installment", (decimal)remaining, (decimal)original, (decimal)paid));
        var snapshot = CollectionsPaymentFilters.Classify((decimal)remaining, (decimal)original, (decimal)paid, "Installment", true);
        Assert.Equal(expected switch { "Paid" => "FullyPaid", "Unpaid" when paid > 0 => "PartiallyPaid", var e => e }, snapshot);
    }

    // ------------------------------------------------------------ the campaign records: eligibility in Manual and Auto, the send, and the export

    /// <summary>Customer 1: plain unpaid, no plan amount (the deployed report). Customer 2: the source says Paid but owes 400. Customer 3: a remainder above the original. Customer 4: settled.</summary>
    private static PactReceivableInstalment[] Rows() =>
    [
        ReviewHarness.Row(1, 500m) with { PlanAmount = null },
        ReviewHarness.Row(2, 400m, status: "Paid"),
        ReviewHarness.Row(3, 800m, plan: 500m) with { AllocatedAmount = 100m },
        ReviewHarness.Row(4, 0m, status: "Paid")
    ];

    private static async Task<ReviewHarness> SeededAsync()
    {
        var h = new ReviewHarness();
        h.Source.Items.AddRange(Rows());
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Auto() => new(ReminderType: "CurrentMonth", MinRemaining: 0m);
    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);

    [Fact]
    public async Task AKnownPositiveRemainder_IsReady_InManualAndAuto_WhateverTheSourceStatusText_AndAPaidInstalmentIsNoReceivable()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var auto = (await scope.Query.QueryAsync(h.Manager, Auto() with { PaymentStatus = "All" }, 1, 50, CancellationToken.None)).Value!;
        var manual = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        foreach (var page in new[] { auto, manual })
        {
            Assert.Equal(3, page.TotalCount);                                                                    // the settled instalment of customer 4 is no receivable
            Assert.DoesNotContain(page.Items, i => i.CustomerName == "Customer 4");
            var one = page.Items.Single(i => i.CustomerName == "Customer 1");
            Assert.Equal(("Unpaid", "Ready"), (one.PaymentStatus, one.ValidationStatus));
            Assert.DoesNotContain(one.Reasons, r => r.Code == ReviewReasons.PaymentStatusUnknown);
            Assert.Equal(1, page.Counts.Ready);
        }
        // Auto's send population and Manual's are the same one Ready customer.
        Assert.Equal(["Customer 1"], (await scope.Query.ResolveAsync(new SelectionRequest(Auto(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Records.Select(r => r.CustomerName));
        Assert.Equal(["Customer 1"], (await scope.Query.ResolveAsync(new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Records.Select(r => r.CustomerName));
    }

    [Fact]
    public async Task AContradictedOrInconsistentRemainder_IsListedAsNeedsReview_AndNeverSent()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        var contradicted = page.Items.Single(i => i.CustomerName == "Customer 2");
        Assert.Equal(("Unknown", "NeedsReview"), (contradicted.PaymentStatus, contradicted.ValidationStatus));
        Assert.Contains(contradicted.Reasons, r => r.Code == ReviewReasons.ContradictoryPaymentStatus);
        var inconsistent = page.Items.Single(i => i.CustomerName == "Customer 3");
        Assert.Equal(("Unknown", "NeedsReview"), (inconsistent.PaymentStatus, inconsistent.ValidationStatus));
        Assert.Contains(inconsistent.Reasons, r => r.Code == ReviewReasons.SourceAmountsInconsistent);
        Assert.Contains(inconsistent.Reasons, r => r.Code == ReviewReasons.PaymentStatusUnknown);
        // A send of the whole list carries only the Ready customer; the two in doubt are left out and said to be.
        var summary = (await scope.Query.SummarizeAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
        Assert.Equal(1, summary.Count); Assert.Equal("Customer 1", summary.Contacts.Single().CustomerName);
    }

    [Fact]
    public async Task TheGenesysExport_StaysSeparateFromEligibility_ItHoldsTheCustomersInDoubtAndLabelsThem()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var file = (await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
        Assert.Equal(3, file.RowCount); Assert.DoesNotContain("Customer 4", file.Csv);
        Assert.Equal(new Dictionary<string, int> { ["Ready"] = 1, ["NeedsReview"] = 2 }, file.IncludedByStatus);
        Assert.False(file.DataVerified); Assert.Contains(file.UnverifiedReasons!, r => r.StartsWith("PaymentStatusUnknown:", StringComparison.Ordinal));
        // The amounts written are the source remainders, as they are.
        var rows = ManualGenesysFile.Records(file.Csv).ToDictionary(r => r["CustomerName"], r => r["AmountDue"]);
        Assert.Equal(("500.00", "400.00", "800.00"), (rows["Customer 1"], rows["Customer 2"], rows["Customer 3"]));
    }

    [Fact]
    public void APaidInstalment_IsExcludedFromTheUnpaidAmounts_AndTheDueOverdueClassStillFollowsTheDueDate()
    {
        var today = new DateOnly(2026, 10, 14);
        var instalments = new List<FilterInstalment> { new(new DateOnly(2026, 8, 10), 300m), new(new DateOnly(2026, 10, 20), 500m) };
        var both = FilteredAmounts.Calculate(instalments, new AmountCriteria(CampaignPaymentFilter.Both, null, null, null), today);
        Assert.Equal((800m, 500m, 300m), (both.Amount, both.DueAmount, both.OverdueAmount));
        // A settled instalment never enters the stored instalments (the record builder drops it), and the calculation ignores a zero if one ever did.
        var withPaid = FilteredAmounts.Calculate([.. instalments, new(new DateOnly(2026, 9, 1), 0m)], new AmountCriteria(CampaignPaymentFilter.Overdue, null, null, null), today);
        Assert.Equal((300m, 0m, 300m), (withPaid.Amount, withPaid.DueAmount, withPaid.OverdueAmount));
        var built = ReviewRecordBuilder.Build(Rows(), new ReviewBuildContext(today, DateTime.UtcNow, "test", "AED", true, true, false, 0.0001m, NoMap));
        Assert.DoesNotContain(built.Records, r => r.TenantId == "T0004");
        Assert.Equal(1, built.SettledRows);
    }
}
