using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>
/// CRM first, PACT completing only what CRM lacks; a CRM SERVICE problem is reported and never read as "CRM has no mobile"; international (+966) numbers; the company is never guessed;
/// tower order over the whole result. All through the shared link service (CollectionsUnitLinkService) and the Campaign in-memory path.
/// </summary>
public sealed partial class CollectionsCrmLinkingTests
{
    private static CrmUnitOwnerDto Tp115(string? mobile = "+966508650505", string? email = "mohammad.m.abdulhamid@gmail.com", string? name = "Mohammad Mousa Abdul Hamid") =>
        new(256146, 4, "Contract", 1, 777, name, null, mobile, email, 1921, "1921", 115, "TP115", "Blue Wave");

    private static PactReceivableInstalment Tp115Row(string mobile = "", string email = "", string name = "Pact Name", int company = 4) =>
        new(company, "T115", name, mobile, email, 1921, "TP115-1921", "TP115", "INV-115", "", new(2026, 10, 10), 500m, "Installment", "115", null, "Blue Wave");

    private static async Task<CollectionsCampaignPreviewDto> Preview(Rig r, string stage = "CurrentMonthReminder") => (await r.Service.PreviewAsync(r.Manager, stage)).Value!;

    // ---- the reported case: TP115-1921, Blue Wave, lead 256146 (Contract), CRM mobile +966508650505 ----

    [Fact]
    public async Task ReportedCase_Tp115_1921_TakesTheCrmMobileEmailAndName_WhenPactHoldsNone()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row());
        r.Owners(Tp115());
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.Equal(("Mohammad Mousa Abdul Hamid", "+966508650505", "mohammad.m.abdulhamid@gmail.com"), (unit.CustomerName, unit.Phone, unit.Email));   // +966 is kept, not turned into +971
        Assert.Equal(777, unit.CrmCustomerId);
        Assert.DoesNotContain("NoValidContact", unit.Reason);
        Assert.DoesNotContain("CrmUnavailable", unit.Reason);
        Assert.Equal(("115", "Blue Wave"), (unit.TowerNumber, unit.TowerName));
    }

    [Fact]
    public async Task ReportedCase_Tp140_407_Lead307092_TakesTheCrmMobileAndEmail_WhenPactHoldsNone_AndTheDisabledFeedIsSaidSo()
    {
        var r = new Rig(); r.Store.CompanyOfTower["140"] = 4;
        r.Source.Items.Add(new PactReceivableInstalment(4, "T407", "Pact Holder", "", "", 407, "TP140-407", "TP140", "INV-407", "", new(2026, 10, 10), 500m, "Installment", "140", null, "Tower"));
        r.Owners(FakeCrmUnitOwnersGateway.Owner(4070, "TP140", "407", "AFTAB ALAM KHAN MOHAMMAD MAZAHARUDDIN KHAN", "+971529586320", "kaftab.alam@yahoo.co.in", leadId: 307092));
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.Equal(("+971529586320", "kaftab.alam@yahoo.co.in"), (unit.Phone, unit.Email));
        Assert.Equal("AFTAB ALAM KHAN MOHAMMAD MAZAHARUDDIN KHAN", unit.CustomerName);
        Assert.DoesNotContain("NoValidContact", unit.Reason);

        // The same unit while CRM linking is switched off: PACT's (empty) contact, and the page is told CRM was not consulted.
        var off = new Rig(); off.Crm.Enabled = false;
        off.Source.Items.Add(new PactReceivableInstalment(4, "T407", "Pact Holder", "", "", 407, "TP140-407", "TP140", "INV-407", "", new(2026, 10, 10), 500m, "Installment", "140", null, "Tower"));
        var report = await Preview(off);
        Assert.Equal(("Disabled", false), (report.Crm!.State, report.Crm.Blocks));
        Assert.Contains("switched off", report.Crm.Message);
    }

    [Fact]
    public void TheFeedRow_OfTheReportedCase_IsEligible_AndKeepsTheSaudiNumberAndTheProjectName()
    {
        var service = new CollectionsCrmOwnersRefreshService(new() { Enabled = true }, new FakeCrmUnitOwnersGateway(), new FakeCrmOwnerStore(), NullLogger<CollectionsCrmOwnersRefreshService>.Instance);
        var row = Assert.IsType<CrmOwnerRow>(service.ToRow(Tp115()));
        Assert.Equal(("TP115-1921", "TP115", "+966508650505", "Blue Wave", 256146), (row.UnitKey, row.ProjectCode, row.PhoneNorm, row.ProjectName, row.LeadId));
        // The same number as CRM may store it.
        foreach (var raw in new[] { "966508650505", "00966508650505", "+966 50 865 0505" })
            Assert.Equal("+966508650505", service.ToRow(Tp115(mobile: raw))!.PhoneNorm);
    }

    // ---- CRM first, PACT only for the missing field ----

    [Fact]
    public async Task WhenCrmHoldsEverything_NothingComesFromPact()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001", email: "pact@example.test", name: "Pact Name"));
        r.Owners(Tp115(mobile: "+971500003001"));   // same phone: the same person; CRM's e-mail and name win
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.Equal(("Mohammad Mousa Abdul Hamid", "+971500003001", "mohammad.m.abdulhamid@gmail.com"), (unit.CustomerName, unit.Phone, unit.Email));
        Assert.DoesNotContain("ContactSourceConflict", unit.Reason);
    }

    [Fact]
    public async Task PactCompletesOnlyTheFieldCrmLacks_AndNeverOverridesCrm()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001", email: "mohammad.m.abdulhamid@gmail.com"));
        r.Owners(Tp115(mobile: null));    // CRM has a name and an e-mail (equal to PACT's), no mobile
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.Equal(("Mohammad Mousa Abdul Hamid", "+971500003001", "mohammad.m.abdulhamid@gmail.com"), (unit.CustomerName, unit.Phone, unit.Email));   // the mobile alone comes from PACT
        Assert.DoesNotContain("ContactSourceConflict", unit.Reason);

        // CRM lacks the mobile and PACT's e-mail is a DIFFERENT one: CRM's e-mail wins, PACT's mobile fills the gap, and nothing is held for review.
        var r2 = new Rig(); r2.Store.CompanyOfTower["115"] = 4;
        r2.Source.Items.Add(Tp115Row(mobile: "971500003001", email: "someone.else@example.test"));
        r2.Owners(Tp115(mobile: null));
        var conflict = Assert.Single((await Preview(r2)).Items);
        Assert.DoesNotContain("ContactSourceConflict", conflict.Reason);
        Assert.Equal("+971500003001", conflict.Phone);
        Assert.Equal("mohammad.m.abdulhamid@gmail.com", conflict.Email);
        Assert.DoesNotContain("NeedsReview", conflict.Status);
    }

    [Fact]
    public async Task ACrmValue_IsNeverReplacedByAPactValue_EvenWhenPactHasMoreFields()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001", email: "pact@example.test", name: "Pact Name"));
        r.Owners(Tp115(mobile: "0500003001", email: null, name: "Crm Name"));   // the same number written as UAE national: the same person
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.Equal(("Crm Name", "+971500003001", "pact@example.test"), (unit.CustomerName, unit.Phone, unit.Email));   // only the e-mail CRM lacks comes from PACT
    }

    [Fact]
    public async Task WithoutAnyCrmOwner_TheContactIsPacts_AndNothingIsFlagged()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001", email: "pact@example.test", name: "Pact Name"));
        r.Owners(FakeCrmUnitOwnersGateway.Owner(5, "TP140", "999"));   // CRM is loaded and healthy; it just has no sale for this unit
        var report = await Preview(r);
        var unit = Assert.Single(report.Items);
        Assert.Equal(("Pact Name", "+971500003001"), (unit.CustomerName, unit.Phone));
        Assert.DoesNotContain("CrmUnavailable", unit.Reason);
        Assert.Equal(("Ready", false), (report.Crm!.State, report.Crm.Blocks));
        Assert.Null(report.Crm.Message);
    }

    // ---- a CRM SERVICE problem is not "CRM has no mobile" ----

    [Fact]
    public async Task WhenTheCrmLinksCannotBeRead_EveryUnitSaysCrmUnavailable_AndNoGenesysListIsExported()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001"));
        r.Owners(Tp115());
        r.Store.Throw = true;     // the links read fails
        var report = await Preview(r);
        var unit = Assert.Single(report.Items);
        Assert.Contains("CrmUnavailable", unit.Reason);
        Assert.Equal("NeedsReview", unit.Status);
        Assert.Equal("+971500003001", unit.Phone);                       // PACT's own value is still shown, but as unconfirmed
        Assert.Equal(("ReadFailed", true), (report.Crm!.State, report.Crm.Blocks));
        Assert.Contains("could not be read", report.Crm.Message);
        Assert.Equal(0, report.ReadyCount); Assert.Equal(1, report.ReviewCount);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await r.Service.ExportAsync(r.Manager, "CurrentMonthReminder", "genesys")).Outcome);
        Assert.Equal(CollectionsOutcome.Success, (await r.Service.ExportAsync(r.Manager, "CurrentMonthReminder", "review")).Outcome);   // the internal review file is still available
    }

    [Fact]
    public async Task AnUnreadableCrmState_NeverLooksLikeAMissingMobile()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row());           // no PACT mobile either
        r.Owners(Tp115()); r.Store.ThrowOnState = true;
        var report = await Preview(r);
        var unit = Assert.Single(report.Items);
        Assert.Equal("", unit.Phone);
        Assert.Contains("CrmUnavailable", unit.Reason);    // the reason says the service failed ...
        Assert.Equal("ReadFailed", report.Crm!.State);      // ... and the report carries that state for the page banner
        Assert.Equal(0, r.Store.LinkReads);                 // nothing was claimed from a store that could not be asked
    }

    [Fact]
    public async Task CrmEnabledButNeverLoaded_IsReportedAsNotLoaded_NotAsNoCrmData()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101));      // the store holds no run at all
        var report = await Preview(r);
        Assert.Equal(("NotLoaded", true), (report.Crm!.State, report.Crm.Blocks));
        Assert.Contains("CrmUnavailable", Assert.Single(report.Items).Reason);
    }

    [Fact]
    public async Task CrmDataOlderThanTheLimit_IsStale_AndTheUnitsNeedReview()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101));
        r.Owners(Owner(1, "TP140", "101"));
        r.Store.LastSuccessUtc = Now.AddMinutes(-(r.Crm.MaxAgeMinutes + 1));
        var report = await Preview(r);
        Assert.Equal(("Stale", true), (report.Crm!.State, report.Crm.Blocks));
        Assert.Contains("CrmUnavailable", Assert.Single(report.Items).Reason);
        r.Store.LastSuccessUtc = Now.AddMinutes(-(r.Crm.MaxAgeMinutes - 1));
        Assert.Equal("Ready", (await Preview(r)).Crm!.State);
    }

    [Fact]
    public async Task AFailedLatestRefresh_WithRecentData_IsAWarning_NotABlock()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101));
        r.Owners(Owner(1, "TP140", "101")); r.Store.LastAttemptFailed = true;
        var report = await Preview(r);
        Assert.Equal(("Ready", false, true), (report.Crm!.State, report.Crm.Blocks, report.Crm.LastRefreshFailed));
        Assert.NotNull(report.Crm.Message);
        Assert.DoesNotContain("CrmUnavailable", Assert.Single(report.Items).Reason);
    }

    [Fact]
    public async Task WhenCrmLinkingIsSwitchedOff_PactIsUsedExactlyAsBefore_AndNothingIsFlagged()
    {
        var r = new Rig(); r.Crm.Enabled = false;
        r.Source.Items.Add(Row("T1", 101));
        var report = await Preview(r);
        Assert.Equal(("Disabled", false), (report.Crm!.State, report.Crm.Blocks));
        Assert.DoesNotContain("CrmUnavailable", Assert.Single(report.Items).Reason);
        Assert.Equal(0, r.Store.LinkReads);
    }

    // ---- identity, ambiguity, companies ----

    [Fact]
    public async Task DifferentCrmAndPactPhones_CrmWins_AndTheUnitIsNotHeldForIt()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001"));
        r.Owners(Tp115(mobile: "+966508650505"));      // CRM and PACT hold different phones
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.DoesNotContain("ContactSourceConflict", unit.Reason);
        Assert.Equal("+966508650505", unit.Phone);     // CRM is the authoritative contact source
        Assert.DoesNotContain("NeedsReview", unit.Status);
    }

    [Fact]
    public async Task TwoCrmBuyersOfOneUnit_NeedReview_AndNeitherMobileIsUsed()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001"));
        r.Owners(Tp115(), new CrmUnitOwnerDto(256147, 8, "Sold", 1, 778, "Second Buyer", null, "+966555555555", "second@example.test", 1921, "1921", 115, "TP115", "Blue Wave"));
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.Contains("CrmCustomerAmbiguous", unit.Reason);
        Assert.Equal("+971500003001", unit.Phone);      // PACT's own contact stays; no CRM buyer was chosen
        Assert.Null(unit.CrmCustomerId);
    }

    [Fact]
    public async Task ASaleThatBothCompaniesClaim_IsNeedsReview_AndNoCrmContactIsUsed()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Source.Items.Add(Tp115Row(mobile: "971500003001"));
        r.Owners(Tp115());
        r.Store.AmbiguousUnits.Add((4, "TP115-1921"));
        var unit = Assert.Single((await Preview(r)).Items);
        Assert.Contains("CrmCompanyAmbiguous", unit.Reason);
        Assert.Equal("NeedsReview", unit.Status);
        Assert.Equal("+971500003001", unit.Phone);
        Assert.Null(unit.CrmCustomerId);
    }

    [Fact]
    public async Task ATowerNumberThatExistsInTwoCompanies_LinksOnlyTheCompanyTheOwnerIsTiedTo()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 32;     // CRM's sale was tied to Sharjah
        r.Source.Items.AddRange([Tp115Row(mobile: "971500003001", name: "Dubai Tenant", company: 4), Tp115Row(name: "Sharjah Pact", company: 32)]);
        r.Owners(Tp115());
        var units = (await Preview(r)).Items.ToDictionary(u => u.CompanyId);
        Assert.Equal(("Mohammad Mousa Abdul Hamid", "+966508650505"), (units[32].CustomerName, units[32].Phone));
        Assert.Equal(("Dubai Tenant", "+971500003001"), (units[4].CustomerName, units[4].Phone));     // the other company's unit is untouched
        Assert.Null(units[4].CrmCustomerId);
    }

    // ---- the refresh: nothing is dropped silently ----

    [Fact]
    public async Task Refresh_FillsAMissingProjectCodeFromTheVerifiedMap_AndCountsEveryRejectionByReason()
    {
        var gateway = new FakeCrmUnitOwnersGateway(); var store = new FakeCrmOwnerStore();
        var map = new FakeProjectMap(); map.Rows[115] = [new CrmProjectTowerMapping(115, "TP115", 4, "Manual")];
        gateway.Owners.AddRange([
            Tp115() with { ProjectCode = "" },                                                           // the feed sent no PACT code: the verified map supplies TP115
            Tp115() with { LeadId = 2, CustomerId = 8, ProjectId = 999, ProjectCode = null, UnitNumber = "5" },   // no code and no map row: counted, not stored
            Tp115() with { LeadId = 3, CustomerType = 2 },                                                // a tenant
            Tp115() with { LeadId = 4, LeadStatus = 2, LeadStatusName = "Reserved" },
            Tp115() with { LeadId = 5, LeadStatusName = "Cancelled" },
            Tp115() with { LeadId = 6, UnitNumber = "0" }]);
        var service = new CollectionsCrmOwnersRefreshService(new() { Enabled = true }, gateway, store, NullLogger<CollectionsCrmOwnersRefreshService>.Instance, map);
        var result = await service.RefreshAsync(default);
        Assert.True(result.Succeeded);
        var stored = Assert.Single(store.Rows);
        Assert.Equal(("TP115-1921", "TP115"), (stored.UnitKey, stored.ProjectCode));
        Assert.Equal(new Dictionary<string, int>
        {
            [CrmOwnerRejection.NoProjectCode] = 1, [CrmOwnerRejection.NotBuyer] = 1, [CrmOwnerRejection.NotSaleStatus] = 2, [CrmOwnerRejection.UnitNotListable] = 1
        }, result.RejectedByReason!.ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(5, result.Rejected);
        Assert.Contains("NoProjectCode 1", store.LastNote);
        Assert.Equal(1, map.Calls);       // one bulk map read, never one per owner
    }

    [Fact]
    public async Task Refresh_AProjectWithTwoMappedCodes_IsNotGuessed()
    {
        var gateway = new FakeCrmUnitOwnersGateway(); var store = new FakeCrmOwnerStore();
        var map = new FakeProjectMap(); map.Rows[115] = [new(115, "TP115", 4, "Manual"), new(115, "TP116", 4, "Manual")];
        gateway.Owners.AddRange([Tp115() with { ProjectCode = "" }, Owner(1, "TP140", "101")]);
        var result = await new CollectionsCrmOwnersRefreshService(new() { Enabled = true }, gateway, store, NullLogger<CollectionsCrmOwnersRefreshService>.Instance, map).RefreshAsync(default);
        Assert.Equal(["TP140-101"], store.Rows.Select(r => r.UnitKey));
        Assert.Equal(1, result.RejectedByReason![CrmOwnerRejection.NoProjectCode]);
    }

    // ---- the shared service, directly ----

    [Fact]
    public async Task TheLinkService_LinksAnyNumberOfUnits_WithOneRead_AndReportsTheState()
    {
        var r = new Rig(); r.Store.CompanyOfTower["115"] = 4;
        r.Owners(Tp115());
        var units = Enumerable.Range(0, 200).Select(n => new UnitToLink(4, $"TP140-{n + 1}", "Pact", "971500003001", "")).Append(new UnitToLink(4, "TP115-1921", "", "", "")).ToList();
        var batch = await r.Links.LinkAsync(units, default);
        Assert.Equal(1, r.Store.LinkReads);
        Assert.Equal(CrmFeedState.Ready, batch.Availability.State);
        var linked = batch.Contacts[(4, "TP115-1921")];
        Assert.Equal(("Mohammad Mousa Abdul Hamid", "+966508650505", "Crm", "Crm"), (linked.Name, linked.Phone, linked.NameSource, linked.PhoneSource));
        Assert.Equal("Pact", batch.Contacts[(4, "TP140-7")].PhoneSource);
    }

    // ---- Payment Summary ----

    [Fact]
    public void PaymentSummary_FlagsCrmUnavailable_ForAUnitWithoutAKnownCrmCustomer()
    {
        var data = new PactUnitReceivables(Snapshot(4), [Id(mobile: "971500003001")], [Inst()], new Dictionary<int, CrmUnitLink>(), true, Now);
        var blocked = CollectionsUnitPaymentSummaryAppService.Compose("TP140-101", new(2026, 10, 14), data, 120, availability: new CrmAvailability(CrmFeedState.ReadFailed));
        Assert.Contains("CrmUnavailable", blocked.ReviewReasons);
        Assert.Equal("NeedsReview", blocked.LinkStatus);
        Assert.Equal("Unavailable", blocked.CrmStatus);
        var healthy = CollectionsUnitPaymentSummaryAppService.Compose("TP140-101", new(2026, 10, 14), data, 120, availability: new CrmAvailability(CrmFeedState.Ready));
        Assert.DoesNotContain("CrmUnavailable", healthy.ReviewReasons);
        Assert.Equal("None", healthy.CrmStatus);
        // A caller that already identified the CRM customer (phone lookup) does not depend on the owner feed.
        var known = CollectionsUnitPaymentSummaryAppService.Compose("TP140-101", new(2026, 10, 14), data, 120,
            new KnownCrmCustomer(11, 55, new("Crm Customer", "+966508650505", "")), availability: new CrmAvailability(CrmFeedState.ReadFailed));
        Assert.DoesNotContain("CrmUnavailable", known.ReviewReasons);
        Assert.Equal("+966508650505", known.Customer.Mobile);
    }

    [Fact]
    public void PaymentSummary_ASaleBothCompaniesClaim_IsAReviewState()
    {
        var link = new CrmUnitLink(CrmLinkStatus.CompanyAmbiguous, 1, 0, 0, 0, SourceContact.Empty);
        var s = Compose(new(Snapshot(4), [Id(mobile: "971500003001")], [Inst()], new Dictionary<int, CrmUnitLink> { [4] = link }, true, Now));
        Assert.Contains("CrmCompanyAmbiguous", s.ReviewReasons);
        Assert.Equal("CompanyAmbiguous", s.CrmStatus);
        Assert.Equal("+971500003001", s.Customer.Mobile);      // PACT's own contact; the CRM sale is tied to no company
    }

    // ---- order over the whole result ----

    [Fact]
    public async Task Campaigns_AreListedNewestTowerFirst_OverTheWholeResult_AndEveryPageFollowsIt()
    {
        var r = new Rig();
        int[] towers = [119, 131, 140, 127, 132, 122, 128];
        var unitNumber = 100;
        foreach (var t in towers) foreach (var n in new[] { 3, 1, 2 })
            r.Source.Items.Add(Row($"T{t}{n}", ++unitNumber, tower: t.ToString(), mobile: "971500003001", name: $"Customer {t}-{n}") with { UnitCode = $"TP{t}-{n * 101}" });
        var expected = towers.OrderByDescending(t => t).SelectMany(t => new[] { 1, 2, 3 }.Select(n => $"TP{t}-{n * 101}")).ToList();

        var pages = new List<List<string>>();
        for (var page = 1; page <= 4; page++)
            pages.Add((await r.Service.PreviewAsync(r.Manager, "CurrentMonthReminder", page: page, pageSize: 6)).Value!.Items.Select(i => i.UnitCode).ToList());
        Assert.Equal(expected, pages.SelectMany(p => p));                       // the pages are consecutive slices of ONE order: nothing repeated, nothing lost
        Assert.Equal(expected.Count, pages.Sum(p => p.Count));
        Assert.Equal("TP140-101", pages[0][0]);                                 // the newest tower leads page 1
        Assert.Equal("TP119-303", pages[^1][^1]);                               // the oldest ends the last page

        // The same order in the export, and the totals are those of the whole result.
        var full = (await r.Service.PreviewAsync(r.Manager, "CurrentMonthReminder", page: 1, pageSize: 100)).Value!;
        Assert.Equal(expected, full.Items.Select(i => i.UnitCode));
        Assert.Equal(expected.Count, full.TotalCount);
        var csv = (await r.Service.ExportAsync(r.Manager, "CurrentMonthReminder", "review")).Value!.Csv;
        Assert.Equal(expected, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[8].Trim('"')));
    }

    // ---- what must NOT change: the month rule, the totals, the eligibility ----

    [Fact]
    public async Task TheOrderAndTheCrmLink_DoNotChangeTheFigures_TheTotals_OrTheStageEligibility()
    {
        PactReceivableInstalment Pay(string tenant, int unit, string tower, DateTime due, decimal amount, int company = 4, string? code = null) =>
            new(company, tenant, "Pact " + tenant, "971500003001", "pact@example.test", unit, code ?? $"TP{tower}-{unit}", $"TP{tower}", "INV-" + tenant + unit + due.Day, "", due, amount, "Installment", tower, null, null);

        void Fill(Rig r) => r.Source.Items.AddRange([
            Pay("A", 1, "140", new(2026, 9, 12), 400m), Pay("A", 1, "140", new(2026, 10, 5), 100m),                 // overdue + due: one unit
            Pay("B", 2, "119", new(2026, 10, 25), 900m),                                                            // due later this month (counts as Due, not eligible before its day)
            Pay("C", 3, "127", new(2026, 11, 3), 5000m),                                                            // next month: neither Due nor Overdue, never listed or totalled
            Pay("D", 4, "131", new(2026, 9, 1), 300m, code: "TP131-513*"),                                          // cancelled apartment
            Pay("E", 0, "132", new(2026, 9, 1), 300m),                                                              // unit 0
            Pay("F", 6, "132", new(2026, 8, 1), 250m)]);

        var off = new Rig(); off.Crm.Enabled = false; Fill(off);
        var on = new Rig(); on.Store.CompanyOfTower["140"] = 4; on.Store.CompanyOfTower["119"] = 4; on.Store.CompanyOfTower["132"] = 4; Fill(on);
        on.Owners(Owner(1, "TP140", "1", "Crm A", "+971500003001", "pact@example.test"));   // the same person as PACT: CRM only supplies the name
        foreach (var stage in new[] { "OverdueReminder", "CurrentMonthReminder", "FollowUpReminder" })
        {
            var a = (await off.Service.PreviewAsync(off.Manager, stage, dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!;
            var b = (await on.Service.PreviewAsync(on.Manager, stage, dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!;
            Assert.Equal(a.Items.Select(i => (i.UnitCode, i.Amount, i.DueAmount, i.OverdueAmount, i.TotalAmount, i.DueDate)), b.Items.Select(i => (i.UnitCode, i.Amount, i.DueAmount, i.OverdueAmount, i.TotalAmount, i.DueDate)));
            Assert.Equal((a.TotalCount, a.ReadyCount, a.ReviewCount), (b.TotalCount, b.ReadyCount, b.ReviewCount));          // CRM does not change who qualifies or what is Ready
            Assert.All(a.Items.Concat(b.Items), i => Assert.NotEqual("TP127-3", i.UnitCode));                                  // next month: never listed
            Assert.DoesNotContain(b.Items, i => i.UnitCode.Contains('*') || i.UnitCode == "TP132-0");
        }
        var overdue = (await on.Service.PreviewAsync(on.Manager, "OverdueReminder", dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!;
        var unitA = overdue.Items.Single(i => i.UnitCode == "TP140-1");
        Assert.Equal((100m, 400m, 500m), (unitA.DueAmount, unitA.OverdueAmount, unitA.TotalAmount));                           // Due + Overdue = Total, the month rule as before
        Assert.Equal(("Crm A", "+971500003001"), (unitA.CustomerName, unitA.Phone));                                           // ... and only the name came from CRM
        Assert.Equal(["TP140-1", "TP132-6"], overdue.Items.Select(i => i.UnitCode).Take(2));                                   // newest tower first
    }

    private sealed class FakeProjectMap : ICollectionsCrmProjectMap
    {
        public Dictionary<int, IReadOnlyList<CrmProjectTowerMapping>> Rows { get; } = [];
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>> GetAsync(IReadOnlyCollection<int> crmProjectIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>>(Rows.Where(r => crmProjectIds.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value));
        }
    }
}
