using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Business decision: a valid positive remaining amount with more than two decimals is rounded to two places (half away from zero) and is no reason to hold the customer. The original PACT value is kept,
/// and Paid / Unpaid is decided before rounding. These tests follow one balance through the Review record, the Export for Genesys file and the Send to Genesys upload (fake client: nothing is sent anywhere).
/// </summary>
public sealed class AmountRoundingTests
{
    private static async Task<ReviewHarness> SeededAsync(params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);   // 15 Oct: no stage is scheduled, Manual sends anyway
        h.Source.Items.AddRange(rows);
        await h.RefreshAsync();
        return h;
    }

    private static SelectionRequest All() => new(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m), ReviewQueryService.ModeAllMatching);

    [Theory]
    [InlineData("500.005", "500.01")]    // the midpoint goes away from zero
    [InlineData("500.004", "500.00")]
    [InlineData("2.675", "2.68")]
    public async Task ExtraDecimals_AreRoundedInTheRecord_TheOriginalIsKept_AndTheCustomerIsReady(string raw, string expected)
    {
        var value = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        using var h = await SeededAsync(ReviewHarness.Row(1, value, plan: value));
        using var context = h.CreateContext();
        var record = Assert.Single(context.CollectionsReviewRecords, r => r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), record.RemainingAmount);
        Assert.Equal(value, record.RawRemainingAmount);                                              // traceability: the amount exactly as PACT sent it
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.DoesNotContain(ReviewReasons.AmountPrecisionNeedsReview, ReviewRecordBuilder.Parse(record.Reasons));
    }

    [Fact]
    public async Task TheSendUploadsTheRoundedAmount_AndThePreviewAndTheExportAgree()
    {
        using var h = await SeededAsync(ReviewHarness.Row(1, 500.005m, plan: 500.005m), ReviewHarness.Row(2, 99.994m, plan: 99.994m));
        var (summary, confirm) = await h.PrepareAsync(All(), "rounding");
        Assert.Equal(2, summary.Count);                                                              // both customers are listed (Preview / approval)
        Assert.Equal(600.00m, summary.TotalsByCurrency["AED"]);                                      // 500.01 + 99.99, rounded per amount
        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        }
        var upload = Assert.Single(h.Client.Calls);
        Assert.Equal(["500.01", "99.99"], upload.Contacts.OrderBy(c => c.CustomerName, StringComparer.Ordinal).Select(c => c.AmountDue).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task TheExportCarriesTheSameRoundedAmount()
    {
        using var h = await SeededAsync(ReviewHarness.Row(1, 500.005m, plan: 500.005m));
        using var scope = h.NewScope();
        var file = (await scope.Export.ExportAsync(h.Manager, All(), CancellationToken.None)).Value!;
        Assert.Equal(1, file.RowCount); Assert.Contains("\"500.01\"", file.Csv); Assert.DoesNotContain("500.005", file.Csv);
        Assert.Equal(500.01m, file.TotalAmount);
    }

    [Fact]
    public async Task ASmallPositiveBalance_IsNeitherPaidNorSent_ItStaysBlockedWithItsOriginalValue()
    {
        using var h = await SeededAsync(ReviewHarness.Row(1, 0.004m, plan: 0.004m), ReviewHarness.Row(2, 300m));
        using var context = h.CreateContext();
        var small = Assert.Single(context.CollectionsReviewRecords, r => r.UnitCode == "TP140-1" && r.ReminderType == CampaignReminderType.CurrentMonth);
        Assert.Contains(ReviewReasons.AmountPrecisionNeedsReview, ReviewRecordBuilder.Parse(small.Reasons));
        Assert.Null(small.RemainingAmount); Assert.Equal(0.004m, small.RawRemainingAmount);
        Assert.NotEqual(ReviewPaymentStatus.Paid, small.PaymentStatus); Assert.NotEqual(ReviewValidationStatus.Ready, small.ValidationStatus);
        var (summary, _) = await h.PrepareAsync(All(), "small");
        Assert.Equal(1, summary.Count);                                                              // only the clean customer can be approved
    }

    [Fact]
    public async Task UnitsSharingAPhoneStayOneRecordEach_WhateverTheirDecimals()
    {
        var phone = ReviewHarness.Row(1).Mobile;
        using var h = await SeededAsync(ReviewHarness.Row(1, 500.005m, plan: 500.005m), ReviewHarness.Row(2, 250.125m, plan: 250.125m, mobile: phone));
        var (summary, _) = await h.PrepareAsync(All(), "shared");
        Assert.Equal(2, summary.Count);
        Assert.Equal([250.13m, 500.01m], summary.Contacts.Select(c => c.Amount).Order());           // never merged, never summed into one record
    }
}
