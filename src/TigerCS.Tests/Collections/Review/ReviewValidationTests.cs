using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

public sealed class ReviewValidationTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);   // Current Month and Legal Notice are scheduled today

    private static ReviewBuildContext Context(bool reconciled = true, bool legalNotice = true, bool legalCase = false, DateOnly? asOf = null) =>
        new(asOf ?? Today, ReviewHarness.Now, "PACT test", "AED", reconciled, legalNotice, legalCase,
            TigerCS.Domain.Modules.Collections.Review.MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>());

    private static CollectionsReviewRecord Single(IEnumerable<PactReceivableInstalment> rows, CampaignReminderType type = CampaignReminderType.CurrentMonth,
        ReviewBuildContext? context = null) =>
        Assert.Single(ReviewRecordBuilder.Build(rows, context ?? Context()).Records, r => r.ReminderType == type);

    private static string[] Reasons(CollectionsReviewRecord r) => ReviewRecordBuilder.Parse(r.Reasons).ToArray();

    [Fact]
    public void FullyVerifiedUnitIsReady()
    {
        var record = Single([ReviewHarness.Row(1)]);
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.Equal(ReviewPaymentStatus.Unpaid, record.PaymentStatus);
        Assert.Equal(500m, record.RemainingAmount);
        Assert.Equal("+971500000001", record.Phone);
        Assert.Empty(Reasons(record));
    }

    [Fact]
    public void AnUnreconciledSource_IsNotACustomerReason_TheRecordKeepsItsOwnStatus()
    {
        // Reconciliation is a property of the source: the run records it and the send refuses on it (ReconciliationGateTests). A sound customer stays Ready, never "Needs review" for it.
        var record = Single([ReviewHarness.Row(1)], context: Context(reconciled: false));
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.DoesNotContain(ReviewReasons.SourceReconciliationRequired, Reasons(record));
    }

    [Fact]
    public void ASignOffForAnotherProcedureDoesNotCount()
    {
        var h = new ReviewHarness();
        h.Campaign.FinancialSourceValidated = true;
        h.Campaign.ValidatedProcedureSuffix = "";     // reconciled the originals...
        h.Sql.ProcedureSuffix = "V2";                 // ...but the deployment now reads V2
        using var scope = h.NewScope();
        Assert.False(scope.Refresh.SourceReconciled);
        h.Campaign.ValidatedProcedureSuffix = "V2";
        Assert.True(scope.Refresh.SourceReconciled);
    }

    [Fact]
    public void AmountPrecision_ExtraDecimalsAreRoundedHalfAwayFromZero_AndTheCustomerStaysReady_WithTheOriginalKept()
    {
        var record = Single([ReviewHarness.Row(1, amount: 500.005m, plan: 500.005m)]);
        Assert.Empty(Reasons(record));   // decimals alone are no longer a blocker
        Assert.Equal(500.01m, record.RemainingAmount);                                        // midpoint: away from zero
        Assert.Equal(500.005m, record.RawRemainingAmount);                                    // the original PACT value is kept for traceability
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void AmountPrecision_ASmallPositiveBalanceIsNeverSilentlyPaid_ItStaysBlockedAndUnrounded()
    {
        // 0.004 rounds to 0.00, which would look settled. The decision is taken on the actual amount: it is a positive balance, so it is neither dropped as paid nor sent as 0.00.
        var record = Single([ReviewHarness.Row(1, amount: 0.004m, plan: 0.004m)]);
        Assert.Contains(ReviewReasons.AmountPrecisionNeedsReview, Reasons(record));
        Assert.Null(record.RemainingAmount);
        Assert.Equal(0.004m, record.RawRemainingAmount);
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.NotEqual(ReviewPaymentStatus.Paid, record.PaymentStatus);
    }

    [Fact]
    public void AmountPrecision_ASettledZeroBalanceIsStillSettled()
    {
        Assert.Empty(ReviewRecordBuilder.Build([ReviewHarness.Row(1, amount: 0m, plan: 500m)], Context()).Records);
    }

    [Fact]
    public void AmountPrecision_FloatingPointResidueFromTheOriginalProcedureIsNormalizedAndStaysReady()
    {
        // Cause of most flags: the deployed procedures compute in float. 750.0000000000001 is the same money as 750.00.
        var row = ReviewHarness.Row(1, amount: 750.00000000000m) with { Amount = 750.0000000000001m, PlanAmount = 750m, AmountIsFloatingPoint = true };
        var record = Single([row]);
        Assert.DoesNotContain(ReviewReasons.AmountPrecisionNeedsReview, Reasons(record));
        Assert.Equal(750.00m, record.RemainingAmount);
    }

    [Fact]
    public void AmountPrecision_AFloatRemainderOfZeroIsASettledInstalment_NotAReceivable()
    {
        var settled = ReviewHarness.Row(1) with { Amount = 0.000000000001m, AmountIsFloatingPoint = true };
        var result = ReviewRecordBuilder.Build([settled], Context());
        Assert.Empty(result.Records);
        Assert.Equal(1, result.SettledRows);
    }

    [Fact]
    public void AmountPrecision_AGenuineSubFilsAmountOnAFloatSourceIsRoundedLikeAnyOther()
    {
        var row = ReviewHarness.Row(1) with { Amount = 500.004m, PlanAmount = 500.004m, AmountIsFloatingPoint = true };
        var record = Single([row]);
        Assert.DoesNotContain(ReviewReasons.AmountPrecisionNeedsReview, Reasons(record));
        Assert.Equal(500.00m, record.RemainingAmount); Assert.Equal(500.004m, record.RawRemainingAmount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    [InlineData("12345")]
    [InlineData("0500003001 / 0559999999")]
    public void NoValidContact_IsRaisedForEveryUnusablePhone(string mobile)
    {
        var record = Single([ReviewHarness.Row(1, mobile: mobile)]);
        Assert.Contains(ReviewReasons.NoValidContact, Reasons(record));
        Assert.Equal("", record.Phone);
    }

    [Theory]
    [InlineData("0500003001")]
    [InlineData("500003001")]
    [InlineData("+971 50 000 3001")]
    [InlineData("00971-50-000-3001")]
    public void NoValidContact_IsNotRaisedForRepairableUaeFormats(string mobile)
    {
        var record = Single([ReviewHarness.Row(1, mobile: mobile)]);
        Assert.DoesNotContain(ReviewReasons.NoValidContact, Reasons(record));
        Assert.Equal("+971500003001", record.Phone);
    }

    [Fact]
    public void AVoiceCampaignNeedsAPhone_AnEmailAloneIsNotEnough()
    {
        var record = Single([ReviewHarness.Row(1, mobile: "")]);   // has a valid email
        Assert.NotEqual("", record.Email);
        Assert.Contains(ReviewReasons.NoValidContact, Reasons(record));
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void APositiveRemainderWithoutAPlanAmount_IsUnpaidAndReady()
    {
        // The deployed report returns the remaining amount and "Installment" only: the remainder is the source of truth, so the record is Unpaid and sendable.
        var row = ReviewHarness.Row(1) with { PlanAmount = null };
        var record = Single([row]);
        Assert.Equal(ReviewPaymentStatus.Unpaid, record.PaymentStatus);
        Assert.DoesNotContain(ReviewReasons.PaymentStatusUnknown, Reasons(record));
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void AContradictedRemainder_IsUnknown_VisibleButNotReady()
    {
        // The source says Paid but a remainder is owed: contradictory, needs review, never sent.
        var record = Single([ReviewHarness.Row(1, status: "Paid")]);
        Assert.Equal(ReviewPaymentStatus.Unknown, record.PaymentStatus);
        Assert.Contains(ReviewReasons.ContradictoryPaymentStatus, Reasons(record));
        Assert.Equal(ReviewValidationStatus.NeedsReview, record.ValidationStatus);
    }

    [Fact]
    public void APartlyPaidRemainder_IsUnpaid_WithTheRemainingAmount()
    {
        var record = Single([ReviewHarness.Row(1, amount: 200m, plan: 500m)]);
        Assert.Equal(ReviewPaymentStatus.Unpaid, record.PaymentStatus);
        Assert.Equal(200m, record.RemainingAmount);
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void PaidStatusWithARemainingBalanceIsContradictory()
    {
        var record = Single([ReviewHarness.Row(1, status: "Paid")]);
        Assert.Contains(ReviewReasons.ContradictoryPaymentStatus, Reasons(record));
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void IdenticalDuplicateSourceRowsAreFlaggedAsDuplicates_NotMerged()
    {
        var row = ReviewHarness.Row(1);
        var record = Single([row, row]);
        Assert.Contains(ReviewReasons.DuplicateSourceRecord, Reasons(record));
        Assert.Null(record.RemainingAmount);        // not summed to 1,000 and not collapsed to 500
        Assert.NotEqual(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void DifferentInstalmentsOnOneDateAreAmbiguous_NotDuplicates()
    {
        var a = ReviewHarness.Row(1);
        var b = a with { VoucherNumber = "INV-OTHER", Amount = 300m, PlanAmount = 300m };
        var reasons = Reasons(Single([a, b]));
        Assert.Contains(ReviewReasons.AmbiguousInstalments, reasons);
        Assert.DoesNotContain(ReviewReasons.DuplicateSourceRecord, reasons);
    }

    [Fact]
    public void ACustomersUnitsStaySeparateRecords_BalancesAreNeverMerged()
    {
        var first = ReviewHarness.Row(1, amount: 300m, day: 16);
        var second = ReviewHarness.Row(1, amount: 450m, day: 18) with { UnitId = 2001, UnitCode = "TP140-2001" };
        var records = ReviewRecordBuilder.Build([first, second], Context()).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(new decimal?[] { 300m, 450m }, records.OrderBy(r => r.DueDate).Select(r => r.RemainingAmount));
        // One customer, two units, one phone: two Ready records, each its own call (the confirmed per-unit decision); not merged and not resolved by choosing a unit.
        Assert.All(records, r => { Assert.Equal(ReviewValidationStatus.Ready, r.ValidationStatus); Assert.DoesNotContain(ReviewReasons.SharedPhoneMultipleUnits, Reasons(r)); });
    }

    [Fact]
    public void ThePhoneSharedByTwoUnits_HoldsNeither_NothingIsMergedOrChosen()
    {
        var a = ReviewHarness.Row(1, mobile: "0500003001", amount: 300m);
        var b = ReviewHarness.Row(2, mobile: "0500003001", amount: 450m);
        var records = ReviewRecordBuilder.Build([a, b], Context()).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
        Assert.Equal(2, records.Count);                                         // one record per unit; no merged 750
        Assert.Equal(new decimal?[] { 300m, 450m }, records.OrderBy(r => r.RemainingAmount).Select(r => r.RemainingAmount));
        Assert.All(records, r =>
        {
            Assert.DoesNotContain(ReviewReasons.SharedPhoneMultipleUnits, Reasons(r));
            Assert.Equal(ReviewValidationStatus.Ready, r.ValidationStatus);     // a repeated number alone never produces a hold
        });
    }

    [Fact]
    public void ADayFourteenCurrentMonthAndLegalNoticeOnOneUnitAreBothEligible_CampaignsAreIndependent()
    {
        // The same unit: a September balance above AED 1,500 (Legal Notice) and an October instalment (Current Month); both are scheduled on day 14.
        var september = ReviewHarness.Row(1, amount: 1600m) with { DueDate = new DateTime(2026, 9, 10), VoucherNumber = "INV-SEP" };
        var october = ReviewHarness.Row(1, amount: 400m, day: 20);
        var records = ReviewRecordBuilder.Build([september, october], Context()).Records;
        var today = records.Where(r => r.ReminderType is CampaignReminderType.CurrentMonth or CampaignReminderType.LegalNotice).ToList();
        Assert.Equal(2, today.Count);
        Assert.All(today, r =>
        {
            Assert.DoesNotContain(ReviewReasons.ReminderTypeOverlap, Reasons(r));
            Assert.DoesNotContain(ReviewReasons.ManualReminderTypeOverlap, Reasons(r));
            Assert.Equal(ReviewValidationStatus.Ready, r.ValidationStatus);
        });
        // No record carries a cross-campaign marker, whatever its type.
        Assert.DoesNotContain(records, r => Reasons(r).Contains(ReviewReasons.ReminderTypeOverlap) || Reasons(r).Contains(ReviewReasons.ManualReminderTypeOverlap));
    }

    [Fact]
    public void AUnitWithOneReminderTypeTodayIsNotAnOverlap()
    {
        var record = Single([ReviewHarness.Row(1)]);
        Assert.DoesNotContain(ReviewReasons.ReminderTypeOverlap, Reasons(record));
        Assert.DoesNotContain(ReviewReasons.SharedPhoneMultipleUnits, Reasons(record));
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void OriginalPaidAndRemainingAmountsMustAddUp()
    {
        // V2: original (PlanAmount) 500 = paid (AllocatedAmount) 200 + remaining 300.
        var consistent = ReviewHarness.Row(1, amount: 300m, plan: 500m) with { AllocatedAmount = 200m };
        var ok = Single([consistent]);
        Assert.Equal(ReviewPaymentStatus.Unpaid, ok.PaymentStatus);
        Assert.Equal(ReviewValidationStatus.Ready, ok.ValidationStatus);

        // Nothing paid: Unpaid, derived from AllocatedAmount = 0, not from the due date.
        var unpaid = Single([ReviewHarness.Row(2, amount: 500m, plan: 500m) with { AllocatedAmount = 0m }]);
        Assert.Equal(ReviewPaymentStatus.Unpaid, unpaid.PaymentStatus);

        // 500 != 100 + 300: the source disagrees with itself, so the payment status is unconfirmed and the record is not sendable.
        var broken = Single([ReviewHarness.Row(3, amount: 300m, plan: 500m) with { AllocatedAmount = 100m }]);
        Assert.Equal(ReviewPaymentStatus.Unknown, broken.PaymentStatus);
        Assert.Contains(ReviewReasons.SourceAmountsInconsistent, Reasons(broken));
        Assert.Contains(ReviewReasons.PaymentStatusUnknown, Reasons(broken));
        Assert.Equal(ReviewValidationStatus.NeedsReview, broken.ValidationStatus);
    }

    [Fact]
    public void InstalmentRepeatedUnderTwoUnitsOfOneCustomerNeedsAllocationReview()
    {
        var a = ReviewHarness.Row(1);
        var b = a with { UnitId = 2001, UnitCode = "TP140-2001" };
        Assert.All(ReviewRecordBuilder.Build([a, b], Context()).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth),
            r => Assert.Contains(ReviewReasons.UnitAllocationNeedsReview, Reasons(r)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MissingUnitIdentityNeedsReview(int unitId)
    {
        var row = ReviewHarness.Row(1) with { UnitId = unitId };
        Assert.Contains(ReviewReasons.MissingUnitIdentity, Reasons(Single([row])));
    }

    [Fact]
    public void MissingCustomerIdentityNeedsReview()
    {
        Assert.Contains(ReviewReasons.MissingCustomerIdentity, Reasons(Single([ReviewHarness.Row(1, name: " ")])));
        Assert.Contains(ReviewReasons.MissingCustomerIdentity, Reasons(Single([ReviewHarness.Row(1) with { TenantId = " " }])));
    }

    [Fact]
    public void ConflictingContactDetailsOnOneUnitNeedReview()
    {
        var a = ReviewHarness.Row(1, day: 16);
        var b = ReviewHarness.Row(1, day: 18, mobile: "0559999999");
        Assert.Contains(ReviewReasons.ConflictingContactDetails, Reasons(Single([a, b])));
    }

    [Fact]
    public void NonAedCurrencyNeedsReview()
    {
        var record = Single([ReviewHarness.Row(1)], context: Context() with { Currency = "USD" });
        Assert.Contains(ReviewReasons.CurrencyNeedsReview, Reasons(record));
    }

    [Fact]
    public void ReminderTypeIsClassifiedByTheApprovedPolicy_NotByAgeAlone()
    {
        // A balance 4 months overdue and above AED 20,000 is a Legal Case candidate by the approved policy...
        var old = ReviewHarness.Row(1, amount: 25000m, day: 1) with { DueDate = new DateTime(2026, 6, 1) };
        var types = ReviewRecordBuilder.Build([old], Context()).Records.Select(r => r.ReminderType).ToHashSet();
        Assert.Contains(CampaignReminderType.LegalCase, types);
        Assert.Contains(CampaignReminderType.Overdue, types);

        // ...but the same age with AED 5,000 is not: age alone never creates a Legal Case.
        var smaller = old with { Amount = 5000m, PlanAmount = 5000m };
        types = ReviewRecordBuilder.Build([smaller], Context()).Records.Select(r => r.ReminderType).ToHashSet();
        Assert.DoesNotContain(CampaignReminderType.LegalCase, types);
        Assert.DoesNotContain(CampaignReminderType.LegalNotice, types);
        Assert.Contains(CampaignReminderType.Overdue, types);
    }

    [Fact]
    public void LegalNoticeNeedsPreviousMonthBalanceAboveThresholdAndRelease()
    {
        var september = ReviewHarness.Row(1, amount: 1500m, day: 10) with { DueDate = new DateTime(2026, 9, 10) };
        Assert.DoesNotContain(ReviewRecordBuilder.Build([september], Context()).Records, r => r.ReminderType == CampaignReminderType.LegalNotice); // strictly more than 1,500
        var above = september with { Amount = 1500.01m, PlanAmount = 1500.01m };
        var notice = Single([above], CampaignReminderType.LegalNotice);
        Assert.Equal(ReviewValidationStatus.Ready, notice.ValidationStatus);
        var unreleased = Single([above], CampaignReminderType.LegalNotice, Context(legalNotice: false));
        Assert.Equal(ReviewValidationStatus.Excluded, unreleased.ValidationStatus);
        Assert.Contains(ReviewReasons.LegalNoticeReleaseRequired, Reasons(unreleased));
    }

    [Fact]
    public void LegalCaseIsExcludedUntilTheBusinessApprovesCustomerContact()
    {
        var old = ReviewHarness.Row(1, amount: 25000m) with { DueDate = new DateTime(2026, 6, 1) };
        var day30 = new DateOnly(2026, 10, 30);
        var blocked = Single([old], CampaignReminderType.LegalCase, Context(asOf: day30));
        Assert.Equal(ReviewValidationStatus.Excluded, blocked.ValidationStatus);
        Assert.Contains(ReviewReasons.LegalCaseNotApproved, Reasons(blocked));
        var allowed = Single([old], CampaignReminderType.LegalCase, Context(legalCase: true, asOf: day30));
        Assert.Equal(ReviewValidationStatus.Ready, allowed.ValidationStatus);
    }

    [Fact]
    public void OutsideTheScheduledDayIsExcluded()
    {
        var record = Single([ReviewHarness.Row(1, day: 20)], context: Context(asOf: new DateOnly(2026, 10, 15)));
        Assert.Equal(ReviewValidationStatus.Excluded, record.ValidationStatus);
        Assert.Contains(ReviewReasons.OutsideSchedule, Reasons(record));
    }

    [Fact]
    public void RecordKeyIsStableForTheSameUnitAndCycleAndDiffersByStage()
    {
        var first = ReviewRecordBuilder.Build([ReviewHarness.Row(1)], Context()).Records.ToList();
        var again = ReviewRecordBuilder.Build([ReviewHarness.Row(1, amount: 900m)], Context()).Records.ToList();
        Assert.Equal(first.Select(r => r.RecordKey).Order(), again.Select(r => r.RecordKey).Order());
        Assert.Equal(first.Count, first.Select(r => r.RecordKey).Distinct().Count());
    }
}
