using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

public sealed class ReviewDomainRulesTests
{
    [Theory]
    [InlineData("971500003001", "+971500003001")]
    [InlineData("+971 50 000 3001", "+971500003001")]
    [InlineData("050-000-3001", "+971500003001")]
    [InlineData("0500003001", "+971500003001")]
    [InlineData("500003001", "+971500003001")]            // leading zero lost in a spreadsheet
    [InlineData("00971500003001", "+971500003001")]
    [InlineData("+9710500003001", "+971500003001")]       // stray trunk zero after the country code
    [InlineData("(050) 000.3001", "+971500003001")]
    [InlineData("04 123 4567", "+97141234567")]           // a landline is a valid number to call
    [InlineData("+44 7911 123456", "+447911123456")]
    [InlineData("0500003001 / 0500003001", "+971500003001")] // the same number written twice is not ambiguous
    public void PhoneIsNormalizedToInternationalFormat(string raw, string expected)
    {
        var result = PhoneNormalizer.Normalize(raw);
        Assert.True(result.IsValid, $"{raw} -> {result.Rejection}");
        Assert.Equal(expected, result.E164);
    }

    [Theory]
    [InlineData("", PhoneRejection.Empty)]
    [InlineData("   ", PhoneRejection.Empty)]
    [InlineData("n/a", PhoneRejection.NotNumeric)]
    [InlineData("12345", PhoneRejection.InvalidLength)]
    [InlineData("0500003001 / 0559999999", PhoneRejection.MultipleNumbers)]  // never guess between two people's numbers
    [InlineData("0100003001", PhoneRejection.InvalidUaeNumber)]
    [InlineData("+971123456789", PhoneRejection.InvalidUaeNumber)]
    public void PhoneRejectionsAreSpecificAndNothingIsInvented(string raw, PhoneRejection reason)
    {
        var result = PhoneNormalizer.Normalize(raw);
        Assert.False(result.IsValid);
        Assert.Equal(reason, result.Rejection);
        Assert.NotEmpty(PhoneNormalizer.Explain(reason));
    }

    [Fact]
    public void ExactTwoDecimalAmountsAreUnchanged()
    {
        var result = MoneyNormalizer.Normalize(1234.50m, sourceIsFloatingPoint: false);
        Assert.Equal(AmountPrecision.Exact, result.Precision);
        Assert.Equal(1234.50m, result.Value);
    }

    [Fact]
    public void DecimalSourceWithExtraPlacesIsUnresolvedNotRounded()
    {
        // 99.999 formats as "100.00" but is a different amount: it must stay a visible precision problem.
        var result = MoneyNormalizer.Normalize(99.999m, sourceIsFloatingPoint: false);
        Assert.Equal(AmountPrecision.Unresolved, result.Precision);
        Assert.Null(result.Value);
        Assert.Equal(99.999m, result.Raw);
    }

    [Fact]
    public void FloatingPointNoiseIsNormalizedOnlyWithinTolerance()
    {
        var noise = MoneyNormalizer.Normalize(250.00000000004m, sourceIsFloatingPoint: true);
        Assert.Equal(AmountPrecision.FloatingPointNoise, noise.Precision);
        Assert.Equal(250.00m, noise.Value);

        var genuine = MoneyNormalizer.Normalize(250.004m, sourceIsFloatingPoint: true);   // 0.4 fils is not float noise
        Assert.Equal(AmountPrecision.Unresolved, genuine.Precision);
    }

    [Fact]
    public void AmountDueIsFormattedInvariantlyWithTwoDecimals()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1234.50", MoneyNormalizer.Format(1234.5m));
            Assert.Equal("5000.00", MoneyNormalizer.Format(5000m));
            Assert.Equal("0.07", MoneyNormalizer.Format(0.07m));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void PaymentStatusComesFromVerifiedSourceFieldsOnly()
    {
        // The deployed procedures report only Paid / Installment, which cannot separate unpaid from partially paid.
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Installment", 500m, planAmount: null));
        // The reviewed V2 report adds the instalment's plan amount.
        Assert.Equal(ReviewPaymentStatus.Unpaid, PaymentStatusRules.Derive("Installment", 500m, 500m));
        Assert.Equal(ReviewPaymentStatus.PartiallyPaid, PaymentStatusRules.Derive("Installment", 200m, 500m));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Installment", 600m, 500m)); // remaining above plan is inconsistent
        Assert.Equal(ReviewPaymentStatus.Paid, PaymentStatusRules.Derive("Paid", 0m, 500m));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Paid", 50m, 500m));        // contradictory
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive(null, 500m, 500m));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Whatever", 500m, 500m));
    }

    [Fact]
    public void PaidAndRemainingFromTheSourceDecideTheStatus_WhenTheyAddUp()
    {
        Assert.Equal(ReviewPaymentStatus.Unpaid, PaymentStatusRules.Derive("Installment", 500m, 500m, null, 0m));
        Assert.Equal(ReviewPaymentStatus.PartiallyPaid, PaymentStatusRules.Derive("Installment", 300m, 500m, null, 200m));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Installment", 300m, 500m, null, 100m));   // 100 + 300 != 500
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Installment", 300m, 500m, null, -200m));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Installment", 0.5m, 0m, null, 0.5m));
        Assert.True(PaymentStatusRules.AmountsAddUp(500.0000m, 200.0000m, 300.00004m));
        Assert.False(PaymentStatusRules.AmountsAddUp(500m, 200m, 300.01m));
    }

    [Fact]
    public void OnlyAConfiguredVerifiedMappingCanGiveAnUnknownSourceValueAStatus()
    {
        var map = new Dictionary<string, string> { ["Open"] = "Unpaid", ["Part"] = "PartiallyPaid", ["Bogus"] = "Paid" };
        Assert.Equal(ReviewPaymentStatus.Unpaid, PaymentStatusRules.Derive("Open", 10m, null, map));
        Assert.Equal(ReviewPaymentStatus.PartiallyPaid, PaymentStatusRules.Derive("part", 10m, null, map));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Bogus", 10m, null, map)); // a mapping can't declare a balance paid
    }

    [Fact]
    public void DueDateAloneNeverDecidesPaymentStatus()
    {
        // The same remaining balance is Unknown whether the date is long overdue or in the future.
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Derive("Installment", 500m, null));
        Assert.DoesNotContain(typeof(PaymentStatusRules).GetMethod(nameof(PaymentStatusRules.Derive))!.GetParameters(), p => p.ParameterType == typeof(DateOnly));
    }

    [Fact]
    public void CombiningInstalmentsNeverHidesAnUnknown()
    {
        Assert.Equal(ReviewPaymentStatus.Unpaid, PaymentStatusRules.Combine([ReviewPaymentStatus.Unpaid, ReviewPaymentStatus.Unpaid]));
        Assert.Equal(ReviewPaymentStatus.PartiallyPaid, PaymentStatusRules.Combine([ReviewPaymentStatus.Unpaid, ReviewPaymentStatus.PartiallyPaid]));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Combine([ReviewPaymentStatus.Unpaid, ReviewPaymentStatus.Unknown]));
        Assert.Equal(ReviewPaymentStatus.Unknown, PaymentStatusRules.Combine([]));
    }

    [Fact]
    public void ReminderTypesMapOneToOneOntoTheApprovedCampaignStages()
    {
        foreach (var stage in Enum.GetValues<CollectionsCampaignStage>())
            Assert.Equal(stage, CampaignReminderTypes.ToStage(CampaignReminderTypes.FromStage(stage)));
        Assert.Equal(CampaignReminderType.LegalCase, CampaignReminderTypes.FromStage(CollectionsCampaignStage.LegalReferral));
        Assert.Equal(5, Enum.GetValues<CampaignReminderType>().Length);
    }

    [Fact]
    public void ReminderTypeLabelsMatchTheSuppliedTemplateNames()
    {
        // Pinned to the five names in the contact-list table. Confirm against the Genesys templates before go-live.
        Assert.Equal(new[] { "Current Month", "Follow Up", "Overdue", "Legal Notice", "Legal Case" },
            new[] { CampaignReminderType.CurrentMonth, CampaignReminderType.FollowUp, CampaignReminderType.Overdue,
                CampaignReminderType.LegalNotice, CampaignReminderType.LegalCase }.Select(CampaignReminderTypes.DefaultLabel));
    }

    [Theory]
    [InlineData(CampaignReminderType.CurrentMonth, "79e5ae74-ea6e-4941-b76d-45ddf487d8d1")]
    [InlineData(CampaignReminderType.FollowUp, "3a91c06e-47ab-4a5e-a720-004bd5cf5bba")]
    [InlineData(CampaignReminderType.LegalCase, "41178d2a-af65-45ae-9a33-b50260dc1b9b")]
    [InlineData(CampaignReminderType.LegalNotice, "372d81d7-6d2d-4b9e-8f09-cf2262aecfdf")]
    [InlineData(CampaignReminderType.Overdue, "d5f4d808-2e12-410e-8fe6-b810cca1ef4c")]
    public void EachReminderTypeRoutesToItsOwnContactList(CampaignReminderType type, string listId)
    {
        Assert.Equal(listId, new GenesysOutboundOptions().ContactListIdFor(type));
    }

    [Fact]
    public void AMissingOrMalformedContactListIdFailsClosed()
    {
        var options = new GenesysOutboundOptions();
        options.ContactListIds[nameof(CampaignReminderType.Overdue)] = "not-a-guid";
        Assert.Throws<InvalidOperationException>(() => options.ContactListIdFor(CampaignReminderType.Overdue));
        options.ContactListIds.Remove(nameof(CampaignReminderType.FollowUp));
        Assert.Throws<InvalidOperationException>(() => options.ContactListIdFor(CampaignReminderType.FollowUp));
    }

    [Fact]
    public void EveryReasonHasAPlainLanguageExplanationAndAKind()
    {
        Assert.All(ReviewReasons.All, r => { Assert.False(string.IsNullOrWhiteSpace(r.Explanation)); Assert.Equal(r.Explanation, ReviewReasons.Explain(r.Code)); });
        Assert.Equal(ReviewReasons.All.Count, ReviewReasons.All.Select(r => r.Code).Distinct().Count());
        Assert.Equal(ReviewValidationStatus.Excluded, ReviewReasons.StatusFor([ReviewReasons.SharedPhoneMultipleUnits], false)); // held back pending a business decision
        Assert.Equal(ReviewValidationStatus.Excluded, ReviewReasons.StatusFor([ReviewReasons.ReminderTypeOverlap], false));
        Assert.Equal(ReviewValidationStatus.Ready, ReviewReasons.StatusFor([], false));
        Assert.Equal(ReviewValidationStatus.NeedsReview, ReviewReasons.StatusFor([ReviewReasons.OutsideSchedule, ReviewReasons.NoValidContact], false));
        Assert.Equal(ReviewValidationStatus.Excluded, ReviewReasons.StatusFor([ReviewReasons.OutsideSchedule], false));
        Assert.Equal(ReviewValidationStatus.AlreadySent, ReviewReasons.StatusFor([], true));
    }
}
