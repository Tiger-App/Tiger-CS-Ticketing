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
/// The contacts behind the Genesys lists (the review records) follow the same rule as the pages: CRM first, PACT only for what CRM lacks, no two people merged, a CRM service problem
/// is its own reason - and they are re-linked from the CURRENT CRM data before anything is sent.
/// </summary>
public sealed class ReviewCrmContactTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static CrmLinkSet Crm(CrmAvailability availability, params ((int Company, string Key), CrmUnitLink Link)[] links) =>
        new(availability, links.ToDictionary(l => l.Item1, l => l.Link));

    private static CrmUnitLink Single(string phone, string name = "Crm Customer", string email = "crm@example.test") =>
        new(CrmLinkStatus.Single, 1, 11, 55, 77, new(name, phone, email));

    private static CollectionsReviewRecord Record(PactReceivableInstalment row, CrmLinkSet? crm)
    {
        var context = new ReviewBuildContext(Today, ReviewHarness.Now, "PACT test", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>(), crm);
        return Assert.Single(ReviewRecordBuilder.Build([row], context).Records, r => r.ReminderType == CampaignReminderType.CurrentMonth);
    }

    private static string[] Reasons(CollectionsReviewRecord r) => ReviewRecordBuilder.Parse(r.Reasons).ToArray();

    private static readonly CrmAvailability Ready = new(CrmFeedState.Ready);

    [Fact]
    public void ASaudiCrmMobile_IsTheContactToCall_WhenPactHoldsNone()
    {
        var row = ReviewHarness.Row(1, mobile: "");
        var record = Record(row, Crm(Ready, ((4, "TP140-1"), Single("+966508650505", "Mohammad Mousa Abdul Hamid", email: ""))));
        Assert.Equal(("+966508650505", "Mohammad Mousa Abdul Hamid", ReviewValidationStatus.Ready), (record.Phone, record.CustomerName, record.ValidationStatus));
        Assert.DoesNotContain(ReviewReasons.NoValidContact, Reasons(record));
    }

    [Fact]
    public void WithoutCrm_TheContactIsPacts_ExactlyAsBefore()
    {
        var plain = Record(ReviewHarness.Row(1), null);
        var off = Record(ReviewHarness.Row(1), CrmLinkSet.None);
        Assert.Equal(("+971500000001", "Customer 1", ReviewValidationStatus.Ready), (plain.Phone, plain.CustomerName, plain.ValidationStatus));
        Assert.Equal((plain.Phone, plain.CustomerName, plain.Reasons), (off.Phone, off.CustomerName, off.Reasons));
        Assert.Equal("+971500000001", Record(ReviewHarness.Row(1), Crm(Ready)).Phone);      // CRM healthy, no sale for this unit: PACT's contact, no flag
    }

    [Fact]
    public void CrmFirst_AValidCrmNumberWinsOverAnInvalidPactOne_AndPactCompletesOnlyTheMissingEmail()
    {
        var row = ReviewHarness.Row(1, mobile: "12345");           // PACT's number cannot be confirmed
        var record = Record(row, Crm(Ready, ((4, "TP140-1"), Single("+966508650505", email: ""))));
        Assert.Equal("+966508650505", record.Phone);
        Assert.Equal("c1@example.test", record.Email);             // CRM lacks the e-mail: PACT completes it
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void ADifferentPhoneInCrm_CrmWins_AndIsNotHeldForReview()
    {
        var record = Record(ReviewHarness.Row(1), Crm(Ready, ((4, "TP140-1"), Single("+966508650505"))));
        Assert.Equal("+966508650505", record.Phone);
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.DoesNotContain(ReviewReasons.ContactSourceConflict, Reasons(record));
    }

    [Fact]
    public void SeveralCrmBuyers_AndACompanyBothClaim_NeedReview_AndNobodyIsChosen()
    {
        var several = Record(ReviewHarness.Row(1), Crm(Ready, ((4, "TP140-1"), new CrmUnitLink(CrmLinkStatus.Ambiguous, 2, 0, 0, 0, SourceContact.Empty))));
        Assert.Contains(ReviewReasons.CrmCustomerAmbiguous, Reasons(several));
        Assert.Equal("+971500000001", several.Phone);               // PACT's own contact; no CRM buyer was picked
        var company = Record(ReviewHarness.Row(1), Crm(Ready, ((4, "TP140-1"), new CrmUnitLink(CrmLinkStatus.CompanyAmbiguous, 1, 0, 0, 0, SourceContact.Empty))));
        Assert.Contains(ReviewReasons.CrmCompanyAmbiguous, Reasons(company));
        Assert.Equal(ReviewValidationStatus.NeedsReview, company.ValidationStatus);
    }

    [Theory]
    [InlineData(CrmFeedState.NotLoaded)]
    [InlineData(CrmFeedState.Stale)]
    [InlineData(CrmFeedState.ReadFailed)]
    public void ACrmServiceProblem_IsItsOwnReason_AndTheRecordIsNotReady(CrmFeedState state)
    {
        var record = Record(ReviewHarness.Row(1), Crm(new CrmAvailability(state)));
        Assert.Contains(ReviewReasons.CrmUnavailable, Reasons(record));
        Assert.Equal(ReviewValidationStatus.NeedsReview, record.ValidationStatus);
        Assert.Equal("+971500000001", record.Phone);                // the PACT number is shown, but never as a confirmed contact
    }

    // ---- re-verified from the CURRENT CRM data before anything is sent ----

    private sealed class CrmRig
    {
        public FakeCrmOwnerStore Store { get; } = new();
        public CollectionsCrmOwnersOptions Options { get; } = new() { Enabled = true };
        public CrmRig() { Store.LastSuccessUtc = ReviewHarness.Now.AddMinutes(-20); }
        public CollectionsUnitLinkService Links => new(Store, Options, new FakeTimeProvider(ReviewHarness.Now), NullLogger<CollectionsUnitLinkService>.Instance);
        public void Owner(int n, string mobile) =>
            Store.ReplaceAsync([.. Store.Rows.Where(r => r.UnitKey != $"TP140-{n}"),
                new CrmOwnerRow($"TP140-{n}", "TP140", n.ToString(), n * 10, 8, n, 5000 + n, 7, "Crm Customer " + n, mobile,
                    $"c{n}@example.test", CollectionsContactNormalizer.NormalizePhone(mobile), $"c{n}@example.test", "Blue Wave")], default, null).GetAwaiter().GetResult();
    }

    private static async Task<ReviewHarness> SeededAsync(CrmRig rig, int count = 2)
    {
        var h = new ReviewHarness { Links = rig.Links };
        for (var n = 1; n <= count; n++) { h.Source.Items.Add(ReviewHarness.Row(n, 100m + n, mobile: "")); rig.Owner(n, "+96650865050" + n); }
        await h.RefreshAsync();
        return h;
    }

    [Fact]
    public async Task TheReviewRecords_CarryTheCrmContact_AndTheGenesysListUsesIt()
    {
        var rig = new CrmRig();
        using var h = await SeededAsync(rig);
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth"), 1, 25, CancellationToken.None)).Value!;
        Assert.Equal(["+966508650501", "+966508650502"], page.Items.Select(i => i.Phone).Order());
        Assert.All(page.Items, i => Assert.Equal("Ready", i.ValidationStatus));
        var (summary, _) = await h.PrepareAsync();
        Assert.Equal(["+966508650501", "+966508650502"], summary.Contacts.Select(c => c.Phone).Order());
    }

    [Fact]
    public async Task ACrmContactThatChangedAfterApproval_StopsTheDispatch_BecauseTheContactIsReReadFromCrm()
    {
        var rig = new CrmRig();
        using var h = await SeededAsync(rig);
        var (_, confirm) = await h.PrepareAsync();
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        rig.Owner(2, "+966559998888");                                    // CRM was corrected after the approval
        using var run = h.NewScope();
        await run.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);                                     // nothing was uploaded
        Assert.Equal(DispatchStatus.ReviewRequired, (await run.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task IfCrmCannotBeUsedAtSendTime_NothingIsSent()
    {
        var rig = new CrmRig();
        using var h = await SeededAsync(rig);
        var (_, confirm) = await h.PrepareAsync();
        using (var scope = h.NewScope()) await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        rig.Store.Throw = true;                                           // the CRM links cannot be read now
        using var run = h.NewScope();
        await run.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        Assert.Equal(DispatchStatus.ReviewRequired, (await run.Store.GetDispatchByIdAsync(h.Scheduler.Dispatches[0], CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task AnUnchangedCrmContact_IsSent()
    {
        var rig = new CrmRig();
        using var h = await SeededAsync(rig);
        var (_, confirm) = await h.PrepareAsync();
        using (var scope = h.NewScope()) await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        using var run = h.NewScope();
        await run.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Equal(["+966508650501", "+966508650502"], h.Client.Calls.SelectMany(c => c.Contacts).Select(c => c.Phone).Order());
    }
}
