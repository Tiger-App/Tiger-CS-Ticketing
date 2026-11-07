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
/// CRM is the authoritative source for the customer's contact (phone, e-mail), PACT for the money. Different contacts in the two systems are NOT a conflict and hold nothing back; only
/// an unclear CRM identity does. Customers and units stay separate records whatever they share.
/// </summary>
public sealed class CrmContactPriorityTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly CrmAvailability Ready = new(CrmFeedState.Ready);

    private static CrmUnitLink Single(string phone, string name = "Crm Customer", string email = "crm@example.test") =>
        new(CrmLinkStatus.Single, 1, 11, 55, 77, new(name, phone, email));

    private static CrmLinkSet Crm(params ((int Company, string Key), CrmUnitLink Link)[] links) => new(Ready, links.ToDictionary(l => l.Item1, l => l.Link));

    private static List<CollectionsReviewRecord> Records(CrmLinkSet crm, params PactReceivableInstalment[] rows)
    {
        var context = new ReviewBuildContext(Today, ReviewHarness.Now, "PACT test", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>(), crm);
        return ReviewRecordBuilder.Build(rows, context).Records.Where(r => r.ReminderType == CampaignReminderType.CurrentMonth).ToList();
    }

    private static string[] Reasons(CollectionsReviewRecord r) => ReviewRecordBuilder.Parse(r.Reasons).ToArray();

    [Fact]
    public void DifferentPhones_CrmWins_AndNothingIsHeld()
    {
        var record = Assert.Single(Records(Crm(((4, "TP140-1"), Single("+966508650505"))), ReviewHarness.Row(1, mobile: "971500000001")));
        Assert.Equal("+966508650505", record.Phone);
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.DoesNotContain(ReviewReasons.ContactSourceConflict, Reasons(record));
    }

    [Fact]
    public void DifferentEmails_CrmWins_AndNothingIsHeld()
    {
        var record = Assert.Single(Records(Crm(((4, "TP140-1"), Single("+971500000001", email: "crm.owner@example.test"))), ReviewHarness.Row(1)));
        Assert.Equal("crm.owner@example.test", record.Email);          // PACT holds c1@example.test
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
        Assert.DoesNotContain(ReviewReasons.ContactSourceConflict, Reasons(record));
    }

    [Fact]
    public void MissingCrmContact_FallsBackToPact_ForAReliablyIdentifiedCustomer()
    {
        var record = Assert.Single(Records(Crm(((4, "TP140-1"), Single("", email: ""))), ReviewHarness.Row(1)));
        Assert.Equal(("+971500000001", "c1@example.test"), (record.Phone, record.Email));
        Assert.Equal(ReviewValidationStatus.Ready, record.ValidationStatus);
    }

    [Fact]
    public void TwoCustomersSharingAPhone_StayTwoSeparateRecords()
    {
        var records = Records(Crm(((4, "TP140-1"), Single("+966508650505", "Crm One", "one@example.test")), ((4, "TP140-2"), Single("+966508650505", "Crm Two", "two@example.test"))),
            ReviewHarness.Row(1), ReviewHarness.Row(2));
        Assert.Equal(2, records.Count);
        Assert.Equal(["Crm One", "Crm Two"], records.Select(r => r.CustomerName).Order());
        Assert.Equal(["one@example.test", "two@example.test"], records.Select(r => r.Email).Order());
        Assert.All(records, r => Assert.Equal("+966508650505", r.Phone));
        Assert.Equal(2, records.Select(r => r.RecordKey).Distinct().Count());
    }

    [Fact]
    public void SeveralUnitsOfOneCustomer_AreKeptIndependently_EachWithItsOwnCrmContact()
    {
        var unit2 = ReviewHarness.Row(1, 700m, day: 25) with { UnitId = 2001, UnitCode = "TP140-9", VoucherNumber = "INV-1-9" };
        var records = Records(Crm(((4, "TP140-1"), Single("+966508650501")), ((4, "TP140-9"), Single("+966508650509"))), ReviewHarness.Row(1, 5000m), unit2);
        Assert.Equal(2, records.Count);
        Assert.Equal(["+966508650501", "+966508650509"], records.Select(r => r.Phone).Order());
        Assert.All(records, r => Assert.DoesNotContain(ReviewReasons.ContactSourceConflict, Reasons(r)));
    }

    [Fact]
    public void AmbiguousCrmIdentity_StaysNeedsReview_EvenWhenPactDiffers()
    {
        var record = Assert.Single(Records(Crm(((4, "TP140-1"), new CrmUnitLink(CrmLinkStatus.Ambiguous, 2, 0, 0, 0, SourceContact.Empty))), ReviewHarness.Row(1)));
        Assert.Equal(ReviewValidationStatus.NeedsReview, record.ValidationStatus);
        Assert.Contains(ReviewReasons.CrmCustomerAmbiguous, Reasons(record));
    }

    [Fact]
    public void PactRowsDisagreeingOnTheContact_DoNotHoldAUnitWhoseCrmCustomerIsKnown()
    {
        var other = ReviewHarness.Row(1, 300m, day: 22) with { Mobile = "971509999999", VoucherNumber = "INV-1-b" };
        var rows = new[] { ReviewHarness.Row(1), other };
        var withCrm = Assert.Single(Records(Crm(((4, "TP140-1"), Single("+966508650505"))), rows));
        Assert.DoesNotContain(ReviewReasons.ConflictingContactDetails, Reasons(withCrm));
        var withoutCrm = Assert.Single(Records(CrmLinkSet.None, rows));           // without CRM the PACT disagreement is still a genuine conflict
        Assert.Contains(ReviewReasons.ConflictingContactDetails, Reasons(withoutCrm));
    }

    // ---- Preview, Export and Send read the one resolved contact ----

    private sealed class CrmRig
    {
        public FakeCrmOwnerStore Store { get; } = new();
        public CollectionsCrmOwnersOptions Options { get; } = new() { Enabled = true };
        public CrmRig() { Store.LastSuccessUtc = ReviewHarness.Now.AddMinutes(-20); }
        public CollectionsUnitLinkService Links => new(Store, Options, new FakeTimeProvider(ReviewHarness.Now), NullLogger<CollectionsUnitLinkService>.Instance);
        public void Owner(int n, string mobile, string email) =>
            Store.ReplaceAsync([.. Store.Rows.Where(r => r.UnitKey != $"TP140-{n}"),
                new CrmOwnerRow($"TP140-{n}", "TP140", n.ToString(), n * 10, 8, n, 5000 + n, 7, "Crm Customer " + n, mobile, email,
                    CollectionsContactNormalizer.NormalizePhone(mobile), email, "Blue Wave")], default, null).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Preview_Export_AndSend_UseTheSameCrmContact_WhenPactDiffers()
    {
        var rig = new CrmRig();
        using var h = new ReviewHarness { Links = rig.Links };
        for (var n = 1; n <= 2; n++) { h.Source.Items.Add(ReviewHarness.Row(n, 100m + n, mobile: "97150000000" + n)); rig.Owner(n, "+96650865050" + n, $"crm{n}@example.test"); }
        await h.RefreshAsync();
        string[] expected = ["+966508650501", "+966508650502"];

        using (var scope = h.NewScope())
        {
            var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth"), 1, 25, CancellationToken.None)).Value!;
            Assert.Equal(expected, page.Items.Select(i => i.Phone).Order());                       // Preview
            Assert.All(page.Items, i => Assert.Equal("Ready", i.ValidationStatus));
        }
        using (var scope = h.NewScope())
        {
            var dto = (await scope.Export.ExportAsync(h.Manager, new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
            var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
            Assert.Equal(6, rows[0].Count);                                                        // the six-column template is unchanged
            Assert.Equal(expected, rows.Skip(1).Select(r => r[0]).Order());                        // Export
        }
        var (_, confirm) = await h.PrepareAsync();
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        using var run = h.NewScope();
        await run.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        Assert.Equal(expected, h.Client.Calls.SelectMany(c => c.Contacts).Select(c => c.Phone).Order());   // Send
    }
}
