using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Manual amount is calculated from the customer's unpaid instalments with the person's Payment status, From / To dates and Minimum Total; it is never the whole balance.
/// The worked example: Due = AED 5,000 (20 Oct) and Overdue = AED 8,000 (10 Aug), today 15 Oct 2026. Payment status Due sends AED 5,000.
/// </summary>
public sealed class FilteredAmountTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly IReadOnlyList<FilterInstalment> Example = [new(new DateOnly(2026, 10, 20), 5000m), new(new DateOnly(2026, 8, 10), 8000m)];

    private static DateOnly? D(string? text) => text is null ? null : DateOnly.Parse(text);

    // Payment status x date range: every combination, with the amount that must be sent (null = the customer is excluded).
    [Theory]
    [InlineData("Both", null, null, 13000)]
    [InlineData("Due", null, null, 5000)]                       // the worked example
    [InlineData("Overdue", null, null, 8000)]
    [InlineData("Both", "2026-10-01", "2026-10-31", 5000)]      // range covers only the due instalment
    [InlineData("Both", "2026-08-01", "2026-08-31", 8000)]      // range covers only the overdue instalment
    [InlineData("Both", "2026-08-10", "2026-10-20", 13000)]     // both ends are inclusive
    [InlineData("Both", "2026-08-11", "2026-10-19", null)]      // the range falls between the two instalments
    [InlineData("Both", "2026-08-10", "2026-10-19", 8000)]
    [InlineData("Both", "2026-08-11", "2026-10-20", 5000)]
    [InlineData("Due", "2026-10-01", "2026-10-31", 5000)]
    [InlineData("Due", "2026-10-20", "2026-10-20", 5000)]
    [InlineData("Due", "2026-10-21", null, null)]               // after the due instalment
    [InlineData("Due", "2026-08-01", "2026-08-31", null)]       // Due, but the range only holds the overdue instalment
    [InlineData("Due", null, "2026-10-19", null)]
    [InlineData("Overdue", "2026-08-01", "2026-08-31", 8000)]
    [InlineData("Overdue", "2026-10-01", "2026-10-31", null)]   // Overdue, but the range only holds the due instalment
    [InlineData("Overdue", "2026-08-11", null, null)]
    [InlineData("Overdue", null, "2026-08-09", null)]
    [InlineData("Overdue", "2026-08-10", "2026-08-10", 8000)]
    [InlineData("Both", "2026-11-01", "2026-12-31", null)]      // next month on is neither Due nor Overdue
    [InlineData("Due", "2026-11-01", null, null)]
    public void TheAmount_IsStrictlyWhatThePaymentStatusAndDatesSelect(string status, string? from, string? to, int? expected)
    {
        CampaignPaymentFilters.TryParse(status, out var filter);
        var result = FilteredAmounts.Calculate(Example, new AmountCriteria(filter, D(from), D(to), null), Today);
        if (expected is null) { Assert.False(result.Qualifies); Assert.Equal(0m, result.Amount); return; }
        Assert.True(result.Qualifies); Assert.Equal((decimal)expected, result.Amount);
        Assert.Equal(result.Amount, result.DueAmount + result.OverdueAmount);
    }

    [Fact]
    public void TheDueAndOverdueParts_FollowTheStatus_AndTheEarliestDueDateIsOfTheSelectedInstalments()
    {
        var both = FilteredAmounts.Calculate(Example, new(CampaignPaymentFilter.Both, null, null, null), Today);
        Assert.Equal((5000m, 8000m, new DateOnly(2026, 8, 10)), (both.DueAmount, both.OverdueAmount, both.EarliestDue!.Value));
        var due = FilteredAmounts.Calculate(Example, new(CampaignPaymentFilter.Due, null, null, null), Today);
        Assert.Equal((5000m, 0m, new DateOnly(2026, 10, 20)), (due.DueAmount, due.OverdueAmount, due.EarliestDue!.Value));   // the Genesys DueDate is the Due instalment, not the August one
        var overdue = FilteredAmounts.Calculate(Example, new(CampaignPaymentFilter.Overdue, null, null, null), Today);
        Assert.Equal((0m, 8000m, new DateOnly(2026, 8, 10)), (overdue.DueAmount, overdue.OverdueAmount, overdue.EarliestDue!.Value));
    }

    [Theory]
    [InlineData("Due", 4999, true)]
    [InlineData("Due", 5000, false)]        // strictly greater than the Minimum Total, like the Campaigns page
    [InlineData("Due", 6000, false)]        // the whole balance (13,000) is above 6,000, the filtered amount (5,000) is not
    [InlineData("Both", 12999, true)]
    [InlineData("Both", 13000, false)]
    [InlineData("Overdue", 0, true)]        // zero / blank means no minimum
    public void TheMinimumTotal_IsAppliedToTheFilteredAmount_NotTheWholeBalance(string status, int minimum, bool qualifies)
    {
        CampaignPaymentFilters.TryParse(status, out var filter);
        var result = FilteredAmounts.Calculate(Example, new(filter, null, null, minimum), Today);
        Assert.Equal(qualifies, result.Qualifies);
        if (!qualifies) Assert.Equal(FilteredAmount.BelowMinimum, result.Reason);
    }

    [Fact]
    public void Nothing_IsDoubleCounted_OrGuessed()
    {
        // Several instalments on one due date cannot be told apart from a duplicated allocation: refused, never summed.
        var twice = FilteredAmounts.Calculate([new(new DateOnly(2026, 10, 20), 5000m), new(new DateOnly(2026, 10, 20), 5000m)], new(CampaignPaymentFilter.Both, null, null, null), Today);
        Assert.Equal(FilteredAmount.Ambiguous, twice.Reason); Assert.Equal(0m, twice.Amount);
        // ... but only when the repeated date is among the SELECTED instalments.
        var outside = FilteredAmounts.Calculate([new(new DateOnly(2026, 10, 20), 5000m), new(new DateOnly(2026, 8, 10), 1m), new(new DateOnly(2026, 8, 10), 1m)], new(CampaignPaymentFilter.Due, null, null, null), Today);
        Assert.True(outside.Qualifies); Assert.Equal(5000m, outside.Amount);
        // Settled / non-positive instalments are not receivables; nothing from next month on is in scope.
        var scope = FilteredAmounts.Calculate([new(new DateOnly(2026, 10, 20), 5000m), new(new DateOnly(2026, 11, 5), 700m), new(new DateOnly(2026, 9, 1), 0m)], new(CampaignPaymentFilter.Both, null, null, null), Today);
        Assert.Equal(5000m, scope.Amount);
        // Unreadable stored instalments never produce an amount.
        Assert.Equal(FilteredAmount.Unavailable, FilteredAmounts.Calculate(null, new(CampaignPaymentFilter.Both, null, null, null), Today).Reason);
    }

    [Fact]
    public void TheStoredInstalments_RoundTrip_AndMalformedTextIsRefusedWhole()
    {
        var text = FilteredAmounts.Serialize(Example);
        Assert.Equal("20260810=8000;20261020=5000", text);
        Assert.Equal(Example.OrderBy(i => i.Due), FilteredAmounts.TryParse(text));
        Assert.Equal(new FilterInstalment(new DateOnly(2026, 10, 1), 1234.5678m), FilteredAmounts.TryParse(FilteredAmounts.Serialize([new(new DateOnly(2026, 10, 1), 1234.5678m)]))![0]);
        foreach (var bad in new string?[] { null, "", "x", "20261020", "20261020=abc", "20261320=5", "20261020=-5", "20261020=5;oops" })
            Assert.Null(FilteredAmounts.TryParse(bad));
    }

    // ------------------------------------------------------------ end to end: list, summary, payload, CSV

    private static PactReceivableInstalment[] ExampleRows(int n = 1) =>
    [
        ReviewHarness.Row(n, 5000m, day: 20),
        ReviewHarness.Row(n, 8000m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = $"INV-{n}B" }
    ];

    private static async Task<ReviewHarness> SeededAsync(params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);          // 15 Oct 2026: no stage is scheduled
        h.Source.Items.AddRange(rows.Length > 0 ? rows : ExampleRows());
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Filter(string status, string? from = null, string? to = null, decimal min = 0m, string type = "CurrentMonth") =>
        new(ReminderType: type, SendingMode: "Manual", DueStatus: status == "Both" ? null : status, DueFrom: D(from), DueTo: D(to), MinRemaining: min);

    private static SelectionRequest All(ReviewFilter filter) => new(filter, ReviewQueryService.ModeAllMatching);

    [Theory]
    [InlineData("Both", null, null, 13000)]
    [InlineData("Due", null, null, 5000)]
    [InlineData("Overdue", null, null, 8000)]
    [InlineData("Both", "2026-10-01", "2026-10-31", 5000)]
    [InlineData("Both", "2026-08-01", "2026-08-31", 8000)]
    [InlineData("Due", "2026-08-01", "2026-08-31", null)]
    [InlineData("Overdue", "2026-10-01", "2026-10-31", null)]
    [InlineData("Both", "2026-11-01", "2026-12-31", null)]
    public async Task TheExactFilteredAmount_IsShownTotalledQuotedAndExported(string status, string? from, string? to, int? expected)
    {
        using var h = await SeededAsync();
        var filter = Filter(status, from, to);
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, filter, 1, 50, CancellationToken.None)).Value!;
        var summary = (await scope.Query.SummarizeAsync(h.Manager, All(filter), CancellationToken.None)).Value!;
        if (expected is null)
        {
            // Zero qualifying amount: the customer is excluded everywhere.
            Assert.Empty(page.Items); Assert.Equal(0, page.Counts.Total); Assert.Equal(0m, page.ReadyTotalAmount); Assert.Equal(0, summary.Count);
            return;
        }
        var amount = (decimal)expected;
        var row = Assert.Single(page.Items);
        Assert.Equal(amount, row.RemainingAmount);                                              // customer table
        Assert.Equal(amount, page.ReadyTotalAmount);                                            // KPI total
        Assert.Equal(amount, summary.TotalsByCurrency["AED"]);                                  // confirmation dialog
        Assert.Equal(amount, summary.Contacts.Single().Amount);
        var (_, confirm) = await h.PrepareAsync(All(filter), "amount-" + status + from);
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        Assert.Equal(amount, accepted.Value!.ApprovedTotalsByCurrency["AED"]);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var payload = Assert.Single(Assert.Single(h.Client.Calls).Contacts);
        Assert.Equal(MoneyNormalizer.Format(amount), payload.AmountDue);                        // Genesys payload
        var csv = (await scope.Dispatch.ExportContactsAsync(h.Manager, accepted.Value.DispatchId, CancellationToken.None)).Value!;
        Assert.Contains($"\"{MoneyNormalizer.Format(amount)}\"", csv);                          // CSV
        if (status == "Due") Assert.Equal("2026-10-20", payload.DueDate);                       // the due date is of the instalments that make the amount
    }

    [Fact]
    public async Task TheWorkedExample_DueAed5000_OverdueAed8000_PaymentStatusDue_SendsAed5000()
    {
        using var h = await SeededAsync();
        var (summary, confirm) = await h.PrepareAsync(All(Filter("Due", "2026-10-01", "2026-10-31")), "example");
        Assert.Equal(5000m, summary.TotalsByCurrency["AED"]);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal("5000.00", Assert.Single(Assert.Single(h.Client.Calls).Contacts).AmountDue);
    }

    [Fact]
    public async Task MinimumTotal_ExcludesCustomersWhoseFilteredAmountIsNotAbove_AndGroupsPerCustomer()
    {
        // Three customers; Payment status Due: 5,000 / 150 / 900. Minimum Total 200 keeps two, and each is ONE row for the whole customer.
        var rows = ExampleRows(1).Concat([ReviewHarness.Row(2, 150m, day: 20), ReviewHarness.Row(3, 900m, day: 22)]).ToArray();
        using var h = await SeededAsync(rows);
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Filter("Due", min: 200m), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(["TP140-1", "TP140-3"], page.Items.Select(i => i.UnitCode));
        Assert.Equal(5900m, page.ReadyTotalAmount);
        // The same customer under Both with the same minimum is still one row (5,000 + 8,000 together).
        var both = (await scope.Query.QueryAsync(h.Manager, Filter("Both", min: 200m), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(13000m, both.Items.Single(i => i.UnitCode == "TP140-1").RemainingAmount); Assert.Equal(2, both.Items.Count);
    }

    [Fact]
    public async Task SharedUnitsAndDuplicatedInstalments_AreNeverDoubleCounted()
    {
        // The same instalment of one tenant under two units: the allocation is unclear, so neither is sent and nothing is summed.
        var shared = new[] { ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(1, 5000m, day: 20) with { UnitId = 2001, UnitCode = "TP140-1B" } };
        using var h = await SeededAsync(shared);
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Filter("Both"), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(0, page.Counts.Ready); Assert.Equal(0m, page.ReadyTotalAmount);
        Assert.All(page.Items, i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.UnitAllocationNeedsReview));
        Assert.Equal(0, (await scope.Query.SummarizeAsync(h.Manager, All(Filter("Both")), CancellationToken.None)).Value!.Count);

        // The identical instalment twice on one unit: held for review, never counted twice.
        using var twice = await SeededAsync(ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(1, 5000m, day: 20));
        using var scope2 = twice.NewScope();
        var held = (await scope2.Query.QueryAsync(twice.Manager, Filter("Both"), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(0, held.Counts.Ready); Assert.DoesNotContain(held.Items, i => i.RemainingAmount == 10000m);
    }

    // ------------------------------------------------------------ revalidation immediately before dispatch

    [Fact]
    public async Task Revalidation_UsesTheFilteredAmount_NotTheWholeBalance()
    {
        // Payment status Due: a change in the OVERDUE part does not change what is sent ...
        using var unaffected = await SeededAsync();
        var (_, confirm) = await unaffected.PrepareAsync(All(Filter("Due")), "reval-1");
        using (var scope = unaffected.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(unaffected.Manager, confirm, CancellationToken.None)).Outcome);
        unaffected.Source.Items.RemoveAll(r => r.DueDate.Month == 8);                           // the 8,000 overdue instalment is paid
        using (var scope = unaffected.NewScope()) await scope.Dispatch.ExecuteAsync(unaffected.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal("5000.00", Assert.Single(Assert.Single(unaffected.Client.Calls).Contacts).AmountDue);

        // ... but a change in the DUE part stops the send for review: the approved amount is no longer true.
        using var changed = await SeededAsync();
        var (_, second) = await changed.PrepareAsync(All(Filter("Due")), "reval-2");
        using (var scope = changed.NewScope()) await scope.Dispatch.ConfirmAsync(changed.Manager, second, CancellationToken.None);
        var idx = changed.Source.Items.FindIndex(r => r.DueDate.Month == 10);
        changed.Source.Items[idx] = changed.Source.Items[idx] with { Amount = 4000m, PlanAmount = 4000m };
        using (var scope = changed.NewScope())
        {
            await scope.Dispatch.ExecuteAsync(changed.Scheduler.Dispatches.Single(), CancellationToken.None);
            var dispatch = (await scope.Dispatch.ListAsync(changed.Manager, CancellationToken.None)).Value!.Single();
            Assert.Equal("ReviewRequired", dispatch.Status);
        }
        Assert.Empty(changed.Client.Calls);

        // Everything the filters selected is paid: the customer is excluded and nothing is uploaded.
        using var paid = await SeededAsync();
        var (_, third) = await paid.PrepareAsync(All(Filter("Due")), "reval-3");
        using (var scope = paid.NewScope()) await scope.Dispatch.ConfirmAsync(paid.Manager, third, CancellationToken.None);
        paid.Source.Items.RemoveAll(r => r.DueDate.Month == 10);
        using (var scope = paid.NewScope())
        {
            await scope.Dispatch.ExecuteAsync(paid.Scheduler.Dispatches.Single(), CancellationToken.None);
            var dispatch = (await scope.Dispatch.ListAsync(paid.Manager, CancellationToken.None)).Value!.Single();
            Assert.Equal("Completed", dispatch.Status); Assert.Equal(0, dispatch.UploadedCount); Assert.Equal(1, dispatch.ExcludedCount);
        }
        Assert.Empty(paid.Client.Calls);
    }

    [Fact]
    public async Task TheDateRangeOfTheApproval_IsKeptForTheRevalidation()
    {
        // The Overdue campaign, Both, but only August: 8,000. Paying the (unselected) October instalment changes nothing.
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(All(Filter("Both", "2026-08-01", "2026-08-31", type: "Overdue")), "range");
        using var scope = h.NewScope();
        await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        h.Source.Items.RemoveAll(r => r.DueDate.Month == 10);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal("8000.00", Assert.Single(Assert.Single(h.Client.Calls).Contacts).AmountDue);
    }

    // ------------------------------------------------------------ the paid-after-upload check uses the same amount

    [Fact]
    public async Task Suppression_ComparesTheFilteredAmount_SoAnUnchangedManualSendIsNotSuppressed()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(All(Filter("Due")), "supp");
        using var scope = h.NewScope();
        await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Single(h.Client.Calls);

        // Nothing paid in the part that was sent (the whole balance is 13,000 but 5,000 was quoted): the contact stays callable.
        var unchanged = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal((1, 0, 1), (unchanged.Examined, unchanged.Suppressed, unchanged.Unchanged));
        Assert.Empty(h.Client.Suppressions);
        // Paying the unselected overdue part still changes nothing for this send.
        h.Source.Items.RemoveAll(r => r.DueDate.Month == 8);
        Assert.Equal(0, (await scope.Suppression.SweepAsync(CancellationToken.None)).Suppressed);
        // Paying the part that was sent makes the contact not callable.
        h.Source.Items.RemoveAll(r => r.DueDate.Month == 10);
        var paid = await scope.Suppression.SweepAsync(CancellationToken.None);
        Assert.Equal(1, paid.Suppressed); Assert.Single(h.Client.Suppressions);
    }
}
