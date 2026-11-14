using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.Collections.Crm;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// CRM is the ONLY source of the customer's mobile for every campaign: the buyer of the unit (existing CRM owner snapshot, project mapping, Sold / Contract only, one buyer) supplies it,
/// it is normalised and validated, and the PACT phone is never used - neither to find the CRM customer nor as a fallback. PACT stays the financial source.
/// </summary>
public sealed class CrmMobileSourceTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly CrmAvailability Ready = new(CrmFeedState.Ready);

    private static CrmLinkSet Crm(params ((int Company, string Key), CrmUnitLink Link)[] links) => new(Ready, links.ToDictionary(l => l.Item1, l => l.Link));
    private static CrmUnitLink Buyer(string phone) => new(CrmLinkStatus.Single, 1, 11, 55, 77, new("Crm Buyer", phone, "buyer@example.test"));

    private static CollectionsReviewRecord Record(PactReceivableInstalment row, CrmLinkSet crm)
    {
        var context = new ReviewBuildContext(Today, ReviewHarness.Now, "PACT test", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>(), crm);
        return Assert.Single(ReviewRecordBuilder.Build([row], context).Records, r => r.ReminderType == CampaignReminderType.CurrentMonth);
    }

    private static string[] Reasons(CollectionsReviewRecord r) => ReviewRecordBuilder.Parse(r.Reasons).ToArray();

    // ---- the rule itself ----

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("not a phone")]
    public void AMissingOrInvalidPactPhone_DoesNotHoldBack_AUnitWhoseCrmBuyerHasAValidMobile(string pactPhone)
    {
        var record = Record(ReviewHarness.Row(1, mobile: pactPhone), Crm(((4, "TP140-1"), Buyer("0501234567"))));
        Assert.Equal("+971501234567", record.Phone);                       // CRM's mobile, normalised to E.164
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.DoesNotContain(ReviewReasons.NoValidContact, Reasons(record));
    }

    [Fact]
    public void ADifferentValidPactPhone_IsNeverUsed_TheCrmMobileIsTheOneToCall()
    {
        var record = Record(ReviewHarness.Row(1, mobile: "971500000001"), Crm(((4, "TP140-1"), Buyer("+966508650505"))));
        Assert.Equal("+966508650505", record.Phone);
    }

    [Theory]
    [InlineData("")]                      // CRM holds no mobile
    [InlineData("12345")]                 // wrong length
    [InlineData("abc")]                   // not a number
    [InlineData("0501234567/0559876543")] // two different numbers: nobody is guessed
    public void AMissingOrInvalidCrmMobile_NeedsReview_EvenWhenPactHoldsAValidPhone(string crmMobile)
    {
        var record = Record(ReviewHarness.Row(1, mobile: "971500000001"), Crm(((4, "TP140-1"), Buyer(crmMobile))));
        Assert.Equal("", record.Phone);                                    // the PACT phone is not a fallback
        Assert.Contains(ReviewReasons.NoValidContact, Reasons(record));
        Assert.Equal(ReviewValidationStatus.NeedsReview, record.ValidationStatus);
    }

    [Fact]
    public void ACrmMobileThatWasStoredUnnormalised_IsValidatedAgainBeforeItIsUsed()
    {
        // Defence in depth: whatever the snapshot holds, only a valid E.164 number can reach Genesys.
        Assert.Equal("+971501234567", Record(ReviewHarness.Row(1), Crm(((4, "TP140-1"), Buyer("00971 50 123 4567")))).Phone);
        Assert.Equal("", Record(ReviewHarness.Row(1), Crm(((4, "TP140-1"), Buyer("971 5 1234")))).Phone);
    }

    [Fact]
    public void WithoutAnEligibleCrmBuyer_ThereIsNoMobile_AndPactDoesNotFillIn()
    {
        var noSale = Record(ReviewHarness.Row(1), Crm());                                    // CRM healthy, no Sold / Contract buyer for the unit
        var off = Record(ReviewHarness.Row(1), CrmLinkSet.None);                             // CRM switched off
        Assert.All(new[] { noSale, off }, r => { Assert.Equal("", r.Phone); Assert.Contains(ReviewReasons.NoValidContact, Reasons(r)); Assert.Equal(ReviewValidationStatus.NeedsReview, r.ValidationStatus); });
    }

    [Fact]
    public void SeveralCrmBuyers_AreNeverPicked_NoMobileAndNeedsReview()
    {
        var several = new CrmUnitLink(CrmLinkStatus.Ambiguous, 2, 0, 0, 0, SourceContact.Empty);
        var record = Record(ReviewHarness.Row(1), Crm(((4, "TP140-1"), several)));
        Assert.Equal("", record.Phone);
        Assert.Contains(ReviewReasons.CrmCustomerAmbiguous, Reasons(record));
        Assert.Equal(ReviewValidationStatus.NeedsReview, record.ValidationStatus);
    }

    [Fact]
    public void TheCrmBuyerIsFoundByTheUnit_NeverByThePactPhone()
    {
        // Two PACT units share a phone; each CRM buyer is tied to ITS unit by the project mapping, so each call goes to its own buyer.
        var rows = new[] { ReviewHarness.Row(1, mobile: "971500000009"), ReviewHarness.Row(2, mobile: "971500000009") };
        var crm = Crm(((4, "TP140-1"), Buyer("+971501111111")), ((4, "TP140-2"), Buyer("+971502222222")));
        var context = new ReviewBuildContext(Today, ReviewHarness.Now, "PACT test", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>(), crm);
        var phones = ReviewRecordBuilder.Build(rows, context).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).OrderBy(r => r.UnitCode).Select(r => r.Phone);
        Assert.Equal(["+971501111111", "+971502222222"], phones);
    }

    [Fact]
    public void TheDomainLinker_NeverReturnsAPactPhoneForACampaign()
    {
        var pact = new SourceContact("Pact Name", "+971500000001", "pact@example.test");
        var crm = new SourceContact("Crm Name", "", "");
        foreach (var status in Enum.GetValues<CrmLinkStatus>())
            Assert.Equal("", CollectionsContactLinker.LinkForCampaign(pact, crm, status).Phone);
        var linked = CollectionsContactLinker.LinkForCampaign(pact, new("Crm Name", "+971502222222", ""), CrmLinkStatus.Single);
        Assert.Equal(("+971502222222", "Crm"), (linked.Phone, linked.PhoneSource));
        Assert.Equal("pact@example.test", linked.Email);                                     // only the e-mail may still complete from PACT
    }

    [Fact]
    public void TheMobileRule_IsTheSame_ForEveryCampaignType()
    {
        foreach (var asOf in new[] { new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30) })
        {
            var context = new ReviewBuildContext(asOf, ReviewHarness.Now, "PACT test", "AED", true, true, true, MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>(), Crm(((4, "TP140-1"), Buyer("+971501234567"))));
            var older = ReviewHarness.Row(1, 2000m) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-AUG" };
            var records = ReviewRecordBuilder.Build([older, ReviewHarness.Row(1, 400m, day: 20)], context).Records;
            Assert.NotEmpty(records);
            Assert.All(records, r => Assert.Equal("+971501234567", r.Phone));
        }
    }

    // ---- preview, export, send: one resolved mobile, from the CRM owner snapshot (one bulk read, no per-row CRM call) ----

    private sealed class CrmRig
    {
        public FakeCrmOwnerStore Store { get; } = new();
        public CollectionsCrmOwnersOptions Options { get; } = new() { Enabled = true };
        public CrmRig() { Store.LastSuccessUtc = ReviewHarness.Now.AddMinutes(-20); }
        public CollectionsUnitLinkService Links => new(Store, Options, new FakeTimeProvider(ReviewHarness.Now), NullLogger<CollectionsUnitLinkService>.Instance);
        public void Owner(int n, string mobile) =>
            Store.ReplaceAsync([.. Store.Rows.Where(r => r.UnitKey != $"TP140-{n}"),
                new CrmOwnerRow($"TP140-{n}", "TP140", n.ToString(), n * 10, 8, n, 5000 + n, 7, "Crm Customer " + n, mobile, $"c{n}@example.test",
                    CollectionsContactNormalizer.NormalizePhone(mobile), $"c{n}@example.test", "Blue Wave")], default, null).GetAwaiter().GetResult();
    }

    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);

    [Fact]
    public async Task Search_Export_ReviewFile_AndSend_AllUseTheCrmMobile_NeverThePactPhone()
    {
        var rig = new CrmRig();
        var h = new ReviewHarness { Links = rig.Links };
        using var _ = h;
        // PACT has a DIFFERENT valid phone for unit 1 and none for unit 2; CRM holds the real mobiles.
        h.Source.Items.AddRange([ReviewHarness.Row(1, 100m, mobile: "971500000001"), ReviewHarness.Row(2, 200m, mobile: "")]);
        rig.Owner(1, "+966508650501"); rig.Owner(2, "+966508650502");
        await h.RefreshAsync();
        using var scope = h.NewScope();

        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 25, CancellationToken.None)).Value!;
        Assert.Equal(["+966508650501", "+966508650502"], page.Items.Select(i => i.Phone).Order());
        Assert.All(page.Items, i => Assert.Equal("Ready", i.ValidationStatus));

        // Customer search finds the CRM mobile, and not the PACT one.
        var byCrm = (await scope.Query.QueryAsync(h.Manager, Manual() with { Customer = "966508650501" }, 1, 25, CancellationToken.None)).Value!;
        Assert.Single(byCrm.Items);
        var byPact = (await scope.Query.QueryAsync(h.Manager, Manual() with { Customer = "500000001" }, 1, 25, CancellationToken.None)).Value!;
        Assert.Empty(byPact.Items);

        var genesys = (await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
        Assert.Contains("+966508650501", genesys.Csv); Assert.Contains("+966508650502", genesys.Csv); Assert.DoesNotContain("971500000001", genesys.Csv);

        var review = (await scope.ReviewExport.ExportAsync(h.Manager, Manual(), CancellationToken.None)).Value!;
        Assert.Contains("966508650501", review.Csv); Assert.DoesNotContain("971500000001", review.Csv);

        var (_, confirm) = await h.PrepareAsync(new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), "crm-mobile");
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        Assert.Equal(["+966508650501", "+966508650502"], h.Client.Calls.SelectMany(c => c.Contacts).Select(c => c.Phone).Order());
    }

    [Fact]
    public async Task ACrmBuyerWithoutAMobile_IsNeedsReview_InThePageAndNeverSent_EvenWithAValidPactPhone()
    {
        var rig = new CrmRig();
        var h = new ReviewHarness { Links = rig.Links };
        using var _ = h;
        h.Source.Items.AddRange([ReviewHarness.Row(1, 100m, mobile: "971500000001"), ReviewHarness.Row(2, 200m, mobile: "971500000002")]);
        rig.Owner(1, ""); rig.Owner(2, "+966508650502");
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 25, CancellationToken.None)).Value!;
        var held = Assert.Single(page.Items, i => i.UnitCode == "TP140-1");
        Assert.Equal(("NeedsReview", ""), (held.ValidationStatus, held.Phone));
        Assert.Contains(held.Reasons, r => r.Code == ReviewReasons.NoValidContact);
        var (summary, _) = await h.PrepareAsync(new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), "needs-review");
        Assert.Equal(1, summary.Count);                                                      // only the unit with a CRM mobile can be sent
    }

    [Fact]
    public void OnlySoldAndContractBuyersSupplyAMobile_TheExistingEligibleStatuses_8And9()
    {
        var refresh = new CollectionsCrmOwnersRefreshService(new() { Enabled = true }, new FakeCrmUnitOwnersGateway(), new FakeCrmOwnerStore(), NullLogger<CollectionsCrmOwnersRefreshService>.Instance);
        Assert.Null(refresh.ToRow(FakeCrmUnitOwnersGateway.Owner(1, "TP140", "1") with { LeadStatus = 2, LeadStatusName = "Reserved" }));
        Assert.NotNull(refresh.ToRow(FakeCrmUnitOwnersGateway.Owner(1, "TP140", "1") with { LeadStatus = 9, LeadStatusName = "Contract" }));
        Assert.NotNull(refresh.ToRow(FakeCrmUnitOwnersGateway.Owner(1, "TP140", "1") with { LeadStatus = 8, LeadStatusName = "Sold" }));
    }
}
