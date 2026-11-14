extern alias TigerCsWeb;

using TigerCS.Application.Modules.Collections.Dto;
using CampaignsModel = TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel;
using CollectionsDisplay = TigerCsWeb::TigerCS.Web.Pages.Collections.CollectionsDisplay;
using ReceivablesModel = TigerCsWeb::TigerCS.Web.Pages.Collections.ReceivablesModel;

namespace TigerCS.Tests.Web;

/// <summary>What the Receivables / Campaigns tables and the tower picker show for a tower name and for the CRM state (no HTTP host needed).</summary>
public sealed class CollectionsTowerAndCrmDisplayTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static PactInstalmentRowDto Row(string unit, decimal remaining = 500m) =>
        new(4, "Tiger Group Dubai", "119", null, 1, unit, "T1", "Pact Name", "", "", "V1", "", new DateOnly(2026, 10, 5), null, null, remaining, "Unknown", "Due", "Installment");

    private static PactInstalmentsPageDto Report(PactInstalmentUnitDto unit, CrmContactStatusDto? crm = null) =>
        new(Today, new(2026, 10, 1), new(2026, 10, 31), new(2000, 1, 1), new(2026, 10, 31), null, "outstanding", 0m, false, "AED",
            new PactInstalmentTotalsDto(1, 500m, 0, 0m, 1, 500m, 0, 0m, 0, 1), 1, 25, [], null!, new PaymentViewAvailabilityDto(true, false, false, false, false, 0), [], DateTime.UtcNow,
            View: "units", Units: [unit], Crm: crm);

    private static PactInstalmentUnitDto Unit(string? towerName, string tower = "119", int? crmCustomers = null, string? crmPhone = null, string? crmName = null, string? crmEmail = null, bool companyAmbiguous = false, string pactPhone = "") =>
        new(4, "Tiger Group Dubai", tower, towerName, 1, $"TP{tower}-1", "T1", "Pact Name", 1, 500m, new DateOnly(2026, 10, 5),
            [Row($"TP{tower}-1") with { TowerNumber = tower, TowerName = towerName, Mobile = pactPhone }], crmCustomers, crmCustomers == 1 ? 9 : null, crmName, crmPhone, crmEmail, companyAmbiguous);

    [Fact]
    public void ATowerWithTheRealNameShowsTheName_AndOneWithAPlaceholderShowsItsNumber()
    {
        Assert.Equal("Blue Wave", ReceivablesModel.Lines(Report(Unit("Blue Wave", "115"))).Single().Tower);
        Assert.Equal("119", ReceivablesModel.Lines(Report(Unit(null))).Single().Tower);
        Assert.Equal("119", ReceivablesModel.Lines(Report(Unit("119"))).Single().Tower);           // a "name" that only repeats the number is not a name
        Assert.Equal("Tower 119", ReceivablesModel.Lines(Report(Unit("Tower 119"))).Single().Tower);   // a real stored name is kept as it is
    }

    [Fact]
    public void ThePicker_LabelsATowerByNumberAndName_AndANamelessTowerByNumberAlone()
    {
        Assert.Equal("115 - Blue Wave", CollectionsDisplay.TowerLabel(new(1, "115", "Blue Wave", 4, true)));
        Assert.Equal("119", CollectionsDisplay.TowerLabel(new(2, "119", "119", 4, true)));
        Assert.Equal("119", CollectionsDisplay.TowerLabel(new(2, "119", "", 4, true)));
        Assert.Equal("Blue Wave", CollectionsDisplay.TowerCell("Blue Wave", "115"));
        Assert.Equal("TP115", CollectionsDisplay.TowerCell(null, null, "TP115"));
        Assert.Equal("—", CollectionsDisplay.TowerCell(null, null));
    }

    [Fact]
    public void ReceivablesTakesTheCrmMobileFirst_AndPactOnlyCompletes()
    {
        var crmOnly = ReceivablesModel.Lines(Report(Unit("Blue Wave", "115", crmCustomers: 1, crmPhone: "+966508650505", crmName: "Mohammad Mousa Abdul Hamid", crmEmail: "m@example.test"))).Single();
        Assert.Equal(("Mohammad Mousa Abdul Hamid", "+966508650505", "m@example.test"), (crmOnly.Customer, crmOnly.Phone, crmOnly.Email));
        Assert.Empty(crmOnly.Notes);
        // CRM has the mobile and no e-mail; PACT has a different mobile: CRM wins and no "contacts differ" review note is raised.
        var conflict = ReceivablesModel.Lines(Report(Unit("Blue Wave", "115", crmCustomers: 1, crmPhone: "+966508650505", crmName: "Crm", crmEmail: "", pactPhone: "971500003001"))).Single();
        Assert.Equal("+966508650505", conflict.Phone);
        Assert.DoesNotContain(conflict.Notes, n => n.Contains("differ"));
    }

    [Fact]
    public void ACrmServiceProblem_IsShownAsSuch_NotAsNoMobile()
    {
        var down = new CrmContactStatusDto("ReadFailed", true, "CRM contact data could not be read.", null);
        var line = ReceivablesModel.Lines(Report(Unit("Blue Wave", "115"), down)).Single();
        Assert.Contains(line.Notes, n => n.StartsWith("CRM unavailable") && n.Contains("review"));
        Assert.DoesNotContain(line.Notes, n => n.Contains("No mobile"));
        // Healthy CRM that simply holds nothing: the plain "no mobile" statement.
        var healthy = ReceivablesModel.Lines(Report(Unit("Blue Wave", "115"), new CrmContactStatusDto("Ready", false, null, DateTime.UtcNow))).Single();
        Assert.Contains(healthy.Notes, n => n.Contains("No mobile"));
        Assert.DoesNotContain(healthy.Notes, n => n.StartsWith("CRM unavailable"));
    }

    [Fact]
    public void ASaleBothCompaniesClaim_IsAReviewNote_AndTheCrmContactIsNotUsed()
    {
        var line = ReceivablesModel.Lines(Report(Unit("Blue Wave", "115", crmCustomers: 1, crmPhone: null, companyAmbiguous: true, pactPhone: "971500003001"))).Single();
        Assert.Contains(line.Notes, n => n.Contains("company") && n.Contains("review"));
        Assert.Equal("+971500003001", line.Phone);
    }

    [Theory]
    [InlineData("OverdueReminder", "No mobile: voice / SMS not possible", "NoValidContact")]
    [InlineData("OverdueReminder", "Mobile unconfirmed: CRM unavailable", "CrmUnavailable;NoValidContact")]
    public void TheCampaignsNoMobileNote_SaysWhetherCrmWasDown(string stage, string expected, string reason)
    {
        _ = stage;
        var row = new CollectionsCampaignContactDto("r", "k", 4, "T1", "N", "", "", 1, "TP115-1921", "TP115", 1m, "AED", null, "S", "C", "NeedsReview", reason);
        Assert.Equal(expected, CampaignsModel.NoMobileNote(row));
    }

    [Fact]
    public void ACampaignRowWhoseCrmWasDown_DoesNotAlsoClaimThatCrmHoldsNothing()
    {
        var row = new CollectionsCampaignContactDto("r", "k", 4, "T1", "N", "", "", 1, "TP115-1921", "TP115", 1m, "AED", null, "S", "C", "NeedsReview", "CrmUnavailable;NoValidContact");
        var notes = CampaignsModel.ReviewNotes(row).ToList();
        Assert.Contains(notes, n => n.StartsWith("CRM unavailable"));
        Assert.DoesNotContain(notes, n => n.Contains("No valid mobile in CRM"));
        var plain = row with { Reason = "NoValidContact" };
        Assert.Contains(CampaignsModel.ReviewNotes(plain), n => n.Contains("No valid mobile in CRM"));
    }

    // ---- the four different reasons nobody can be reached (switched off / owner not linked / CRM linked but empty / CRM service down) ----

    [Fact]
    public void WhenCrmLinkingIsSwitchedOff_TheNoContactNoteSaysCrmWasNotChecked_NotThatCrmHoldsNothing()
    {
        var off = new CrmContactStatusDto("Disabled", false, null, null);
        var line = ReceivablesModel.Lines(Report(Unit("Blue Wave", "140"), off)).Single();
        var note = Assert.Single(line.Notes, n => n.StartsWith("No mobile or email"));
        Assert.Contains("switched off", note);
        Assert.DoesNotContain("CRM or PACT", note);
    }

    [Fact]
    public void WhenCrmIsLoaded_ButNoOwnerIsLinkedToTheUnit_TheNoteSaysSo()
    {
        var ready = new CrmContactStatusDto("Ready", false, null, DateTime.UtcNow);
        var line = ReceivablesModel.Lines(Report(Unit("Blue Wave", "140"), ready)).Single();
        var note = Assert.Single(line.Notes, n => n.StartsWith("No mobile or email"));
        Assert.Contains("no CRM owner is linked", note);
    }

    [Fact]
    public void OnlyWhenTheCrmOwnerIsLinkedAndEmpty_DoesTheNoteSayCrmOrPactHoldNothing()
    {
        var ready = new CrmContactStatusDto("Ready", false, null, DateTime.UtcNow);
        var line = ReceivablesModel.Lines(Report(Unit("Blue Wave", "140", crmCustomers: 1, crmPhone: "", crmName: "Owner", crmEmail: ""), ready)).Single();
        Assert.Contains(line.Notes, n => n.Contains("No mobile or email in CRM or PACT"));
        var ambiguous = ReceivablesModel.Lines(Report(Unit("Blue Wave", "140", crmCustomers: 2), ready)).Single();
        Assert.Contains(ambiguous.Notes, n => n.Contains("Several CRM customers"));
        Assert.DoesNotContain(ambiguous.Notes, n => n.Contains("no CRM owner is linked"));
    }

    [Fact]
    public void ACrmOwnerWithAMobileAndEmail_IsShown_ForTheReportedUnit_AndPactIsNotNeeded()
    {
        // TP140-407 / lead 307092: CRM holds +971529586320 and the e-mail; PACT holds neither.
        var line = ReceivablesModel.Lines(Report(Unit("Tower 140", "140", crmCustomers: 1, crmPhone: "+971529586320", crmName: "AFTAB ALAM KHAN MOHAMMAD MAZAHARUDDIN KHAN", crmEmail: "kaftab.alam@yahoo.co.in"),
            new CrmContactStatusDto("Ready", false, null, DateTime.UtcNow))).Single();
        Assert.Equal(("+971529586320", "kaftab.alam@yahoo.co.in"), (line.Phone, line.Email));
        Assert.DoesNotContain(line.Notes, n => n.StartsWith("No mobile"));
    }

    [Fact]
    public void TheCampaignNoContactNote_FollowsTheSameFourStates()
    {
        var row = new CollectionsCampaignContactDto("r", "k", 4, "T1", "N", "", "", 1, "TP140-407", "TP140", 1m, "AED", null, "S", "C", "NeedsReview", "NoValidContact");
        Assert.Contains("switched off", CampaignsModel.ReviewNotes(row, "Disabled").Single());
        Assert.Contains("no CRM owner is linked", CampaignsModel.ReviewNotes(row, "Ready").Single());
        Assert.Contains("No valid mobile in CRM", CampaignsModel.ReviewNotes(row with { CrmCustomerId = 9 }, "Ready").Single());
        Assert.Contains("CRM unavailable", CampaignsModel.ReviewNotes(row with { Reason = "CrmUnavailable;NoValidContact" }, "ReadFailed").Single());
        Assert.Contains("No valid mobile in CRM", CampaignsModel.ReviewNotes(row).Single());   // state unknown: no claim about a missing CRM owner
    }
}
