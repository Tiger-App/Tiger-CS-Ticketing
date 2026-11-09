using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Collections.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>Unit/customer linking through the Campaign / Receivables in-memory path, the CRM owner refresh and the unit Payment Summary composition.</summary>
public sealed partial class CollectionsCrmLinkingTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public CollectionsCampaignAppServiceTests.Source Source { get; } = new();
        public FakeCrmOwnerStore Store { get; } = new();
        public CollectionsOptions Options { get; } = new() { Enabled = true };
        public PactReceivablesOptions Sql { get; } = new() { Enabled = true, DefaultMinOutstandingAmount = 0m };
        public CollectionsCrmOwnersOptions Crm { get; } = new() { Enabled = true };
        public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);
        public CollectionsCampaignAppService Service { get; }
        public Rig() { Store.LastSuccessUtc = Now.AddMinutes(-30); Service = new(Options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, Sql, new(Options, new FakeDepartmentRepository()),
            new(Options, new FakeTimeProvider(Now)), Source, NullLogger<CollectionsCampaignAppService>.Instance, Links); }
        public CollectionsUnitLinkService Links => new(Store, Crm, new FakeTimeProvider(Now), NullLogger<CollectionsUnitLinkService>.Instance);

        public void Owners(params CrmUnitOwnerDto[] owners)
        {
            var refresh = new CollectionsCrmOwnersRefreshService(Crm, new FakeCrmUnitOwnersGateway { Owners = { } }, Store, NullLogger<CollectionsCrmOwnersRefreshService>.Instance);
            Store.ReplaceAsync(owners.Select(o => refresh.ToRow(o)).OfType<CrmOwnerRow>().ToList(), default, null).GetAwaiter().GetResult();
        }

        public async Task<IReadOnlyList<CollectionsCampaignContactDto>> Units() => (await Service.PreviewAsync(Manager, "CurrentMonthReminder")).Value!.Items;
    }

    private static PactReceivableInstalment Row(string tenant, int unit, string tower = "140", string mobile = "971500003001", string name = "Pact Customer", string email = "pact@example.test", int company = 4, decimal amount = 500m) =>
        new(company, tenant, name, mobile, email, unit, $"TP{tower}-{unit}", $"TP{tower}", "INV-" + tenant + unit, "", new(2026, 10, 10), amount, "Installment");

    private static CrmUnitOwnerDto Owner(int customer, string project, string unit, string? name = "Crm Customer", string? mobile = "0501234567", string? email = "crm@example.test") =>
        FakeCrmUnitOwnersGateway.Owner(customer, project, unit, name, mobile, email);

    [Fact]
    public async Task UnitWithoutPactMobile_IsStillListed_AndTakesTheCrmContact()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101, mobile: ""));
        r.Owners(Owner(1, "TP140", "101"));
        var unit = Assert.Single(await r.Units());
        Assert.Equal("Crm Customer", unit.CustomerName);
        Assert.Equal("+971501234567", unit.Phone);
        Assert.Equal(1, unit.CrmCustomerId);
    }

    [Fact]
    public async Task UnitWithoutPactMobile_AndNoCrm_IsStillListed_ButCannotBeContactedByPhone()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101, mobile: ""));
        var unit = Assert.Single(await r.Units());
        Assert.Equal("", unit.Phone);
        Assert.Equal("pact@example.test", unit.Email);
    }

    [Fact]
    public async Task CrmDataMissingAName_IsCompletedFromPact_AndNeverBlankedByCrm()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101, name: "Pact Name"));
        r.Owners(Owner(1, "TP140", "101", name: null, mobile: "971500003001", email: null));
        var unit = Assert.Single(await r.Units());
        Assert.Equal("Pact Name", unit.CustomerName);
        Assert.Equal("pact@example.test", unit.Email);
        Assert.Equal("+971500003001", unit.Phone);
    }

    [Fact]
    public async Task CancelledFormerOwnerAndNonBuyerCrmRecords_AreNeverUsed()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101));
        r.Owners(FakeCrmUnitOwnersGateway.Owner(9, "TP140", "101", "Former Owner", "0509999999", statusName: "Cancelled"),
            FakeCrmUnitOwnersGateway.Owner(8, "TP140", "101", "Tenant", "0508888888", customerType: 2),
            FakeCrmUnitOwnersGateway.Owner(7, "TP140", "101", "Reserved", "0507777777", leadStatus: 2, statusName: "Reserved"));
        Assert.Empty(r.Store.Rows);
        var unit = Assert.Single(await r.Units());
        Assert.Equal("Pact Customer", unit.CustomerName);
        Assert.Null(unit.CrmCustomerId);
    }

    [Fact]
    public async Task SeveralApartmentsOfOneCustomer_KeepTheirOwnAmounts()
    {
        var r = new Rig();
        r.Source.Items.AddRange([Row("T1", 101, amount: 300m), Row("T1", 102, amount: 700m)]);
        r.Owners(Owner(1, "TP140", "101"), Owner(1, "TP140", "102"));
        var units = (await r.Units()).OrderBy(u => u.UnitCode).ToList();
        Assert.Equal([300m, 700m], units.Select(u => u.DueAmount));   // 10 Oct is inside the current month: Due
        Assert.All(units, u => Assert.Equal(1, u.CrmCustomerId));
    }

    [Fact]
    public async Task SameApartmentNumberInDifferentTowers_LinksOnlyToItsOwnTower()
    {
        var r = new Rig();
        r.Source.Items.AddRange([Row("T1", 101, tower: "140", name: "A"), Row("T2", 101, tower: "124", name: "B")]);
        r.Owners(Owner(1, "TP140", "101", "Owner Of 140"));
        var units = (await r.Units()).ToDictionary(u => u.UnitCode);
        Assert.Equal("Owner Of 140", units["TP140-101"].CustomerName);
        Assert.Equal("B", units["TP124-101"].CustomerName);
        Assert.Null(units["TP124-101"].CrmCustomerId);
    }

    [Fact]
    public async Task SeveralQualifyingCrmCustomers_NeedReview_AndTheFirstIsNotPicked()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101));
        r.Owners(Owner(1, "TP140", "101", "First"), Owner(2, "TP140", "101", "Second", "0502222222"));
        var unit = Assert.Single(await r.Units());
        Assert.Equal("NeedsReview", unit.Status);
        Assert.Contains("CrmCustomerAmbiguous", unit.Reason);
        Assert.Equal("Pact Customer", unit.CustomerName);
        Assert.Null(unit.CrmCustomerId);
    }

    [Fact]
    public async Task CrmAndPactDisagreeingOnThePhone_CrmWins_WithoutReviewOrMerging()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101, mobile: "971500003001"));
        r.Owners(Owner(1, "TP140", "101", mobile: "0551112222"));
        var unit = Assert.Single(await r.Units());
        Assert.DoesNotContain("ContactSourceConflict", unit.Reason);
        Assert.Equal("+971551112222", unit.Phone); // CRM is authoritative
        Assert.Equal("crm@example.test", unit.Email);
    }

    [Fact]
    public async Task ACrmStoreFailure_DegradesToPactOnly_AndTheListStillLoads()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101));
        r.Owners(Owner(1, "TP140", "101")); r.Store.Throw = true;
        var unit = Assert.Single(await r.Units());
        Assert.Equal("Pact Customer", unit.CustomerName);
    }

    [Fact]
    public async Task TheCrmIsReadInBulk_NeverOncePerUnit()
    {
        var r = new Rig();
        for (var n = 1; n <= 40; n++) r.Source.Items.Add(Row("T" + n, 100 + n));
        r.Owners(Owner(1, "TP140", "101"));
        await r.Units();
        Assert.Equal(1, r.Store.LinkReads);
    }

    // ---- refresh ----

    [Fact]
    public async Task Refresh_StoresOnlyEligibleOwners_AndKeepsThePreviousDataWhenTheFeedIsEmptyOrFails()
    {
        var crm = new CollectionsCrmOwnersOptions { Enabled = true };
        var gateway = new FakeCrmUnitOwnersGateway(); var store = new FakeCrmOwnerStore();
        var service = new CollectionsCrmOwnersRefreshService(crm, gateway, store, NullLogger<CollectionsCrmOwnersRefreshService>.Instance);
        gateway.Owners.AddRange([Owner(1, "TP140", "101"), Owner(2, "TP140", "102"),
            FakeCrmUnitOwnersGateway.Owner(3, "TP140", "103", statusName: "Cancelled"),
            FakeCrmUnitOwnersGateway.Owner(4, "TP140", "104", customerType: 2),
            Owner(5, "TP140", "*"), Owner(6, "TP140", "0"), Owner(7, "", "105")]);
        var ok = await service.RefreshAsync(default);
        Assert.True(ok.Succeeded); Assert.Equal(7, ok.Fetched); Assert.Equal(2, ok.Stored);

        gateway.Owners.Clear();
        Assert.False((await service.RefreshAsync(default)).Succeeded);
        Assert.Equal(2, store.Rows.Count);
        Assert.NotNull(store.Failure);

        gateway.Outcome = CrmUnitOwnersOutcome.Unavailable;
        Assert.False((await service.RefreshAsync(default)).Succeeded);
        Assert.Equal(2, store.Rows.Count);
    }

    [Fact]
    public async Task Refresh_DoesNothingWhenDisabled()
    {
        var gateway = new FakeCrmUnitOwnersGateway();
        var service = new CollectionsCrmOwnersRefreshService(new(), gateway, new FakeCrmOwnerStore(), NullLogger<CollectionsCrmOwnersRefreshService>.Instance);
        Assert.False((await service.RefreshAsync(default)).Succeeded);
        Assert.Equal(0, gateway.Requests);
    }

    // ---- unit Payment Summary ----

    private static SnapshotStatusDto Snapshot(params int[] companies) => new(
        companies.Select(c => new SnapshotCompanyStatusDto(c, "Co" + c, true, Now, Now, "Succeeded", null, 0, 10, null, null, 0, 0, 0, 0, 0, 0, "Fresh", 1)).ToList(), [], 120);

    private static PactUnitIdentity Id(string tenant = "T1", int unit = 101, string mobile = "", string name = "Pact Customer", string email = "", int company = 4, int all = 1, int open = 1) =>
        new(company, tenant, unit, "TP140-101", name, mobile, email, "140", "Tower", all, open);

    private static PactUnitInstalment Inst(string tenant = "T1", int unit = 101, decimal amount = 400m, int day = 5, int company = 4) =>
        new(company, tenant, unit, "V" + day, new(2026, 10, day), amount, amount, 0m, "Installment");

    private static CollectionsUnitPaymentSummaryDto Compose(PactUnitReceivables data) =>
        CollectionsUnitPaymentSummaryAppService.Compose("TP140-101", new(2026, 10, 14), data, 120);

    [Fact]
    public void Summary_WithoutPactMobile_UsesCrm_AndShowsPactAmounts()
    {
        var link = new CrmUnitLink(CrmLinkStatus.Single, 1, 11, 55, 77, new("Crm Customer", "+971501234567", "crm@example.test"));
        var s = Compose(new(Snapshot(4), [Id()], [Inst(amount: 400m, day: 5), Inst(amount: 100m, day: 14), Inst(amount: 900m, day: 20)], new Dictionary<int, CrmUnitLink> { [4] = link }, true, Now));
        Assert.Equal("Linked", s.LinkStatus);
        Assert.Equal("Crm Customer", s.Customer.Name); Assert.Equal("+971501234567", s.Customer.Mobile);
        Assert.Equal("Available", s.FinancialStatus);
        Assert.Equal(0m, s.Overdue); Assert.Equal(1400m, s.Due); Assert.Equal(1400m, s.Total); // 5, 14 and the coming 20 Oct are all Due (the current month); nothing is before 1 Oct
    }

    private static PactUnitInstalment On(DateOnly due, decimal remaining, string voucher, decimal? original = null) =>
        new(4, "T1", 101, voucher, due, remaining, original ?? remaining, (original ?? remaining) - remaining, "Installment");

    private static CollectionsUnitPaymentSummaryDto ComposeOn(DateOnly today, params PactUnitInstalment[] instalments) =>
        CollectionsUnitPaymentSummaryAppService.Compose("TP140-101", today, new(Snapshot(4), [Id()], instalments, new Dictionary<int, CrmUnitLink>(), true, Now), 120);

    [Fact]
    public void Summary_UsesTheSameMonthRuleAsReceivablesAndCampaigns()
    {
        // Today 12 Oct 2026: 30 Sep Overdue; 1, 12 and 31 Oct Due; 1 Nov in neither (never listed or totalled).
        var s = ComposeOn(new(2026, 10, 12), On(new(2026, 9, 30), 10m, "S30"), On(new(2026, 10, 1), 20m, "O01"), On(new(2026, 10, 12), 40m, "O12"),
            On(new(2026, 10, 31), 80m, "O31"), On(new(2026, 11, 1), 160m, "N01"));
        Assert.Equal((10m, 140m, 150m), (s.Overdue, s.Due, s.Total));
        Assert.Equal(["S30", "O01", "O12", "O31"], s.Instalments.Select(i => i.Description.Replace("Voucher ", "")));
        Assert.Equal(["Overdue", "Due", "Due", "Due"], s.Instalments.Select(i => i.Status));
        Assert.Null(s.Instalments.Single(i => i.Description.EndsWith("O31")).DaysLate);                    // a coming day is Due and not late
        Assert.Equal(s.Total, s.Instalments.Sum(i => i.RemainingAmount));                                  // details add up to the total
    }

    [Fact]
    public void Summary_DecemberToJanuary_AndPartlyPaid()
    {
        var december = ComposeOn(new(2026, 12, 15), On(new(2026, 11, 30), 5m, "N30"), On(new(2026, 12, 31), 7m, "D31"), On(new(2027, 1, 1), 9m, "J01"));
        Assert.Equal((5m, 7m, 12m), (december.Overdue, december.Due, december.Total));
        var january = ComposeOn(new(2027, 1, 10), On(new(2026, 12, 31), 5m, "D31"), On(new(2027, 1, 31), 7m, "J31"), On(new(2027, 2, 1), 9m, "F01"));
        Assert.Equal((5m, 7m, 12m), (january.Overdue, january.Due, january.Total));
        // Partly paid: only the remainder counts (original 1000, 400 paid -> 600), a fully paid instalment (nothing remaining) never does.
        var partial = ComposeOn(new(2026, 10, 12), On(new(2026, 9, 10), 600m, "P", original: 1000m), On(new(2026, 10, 25), 0m, "PAID", original: 300m));
        Assert.Equal((600m, 0m, 600m), (partial.Overdue, partial.Due, partial.Total));
        Assert.Equal(1000m, partial.Instalments.Single().OriginalAmount);
    }

    [Fact]
    public void Summary_WithoutAPactRecord_UsesAValidCrmSale_AndIsNotZero()
    {
        var link = new CrmUnitLink(CrmLinkStatus.Single, 1, 11, 55, 77, new("Crm Customer", "+971501234567", ""));
        var s = Compose(new(Snapshot(4), [], [], new Dictionary<int, CrmUnitLink> { [4] = link }, true, Now));
        Assert.Equal("Linked", s.LinkStatus);
        Assert.Equal("Crm Customer", s.Customer.Name);
        Assert.Equal("NoFinancialData", s.FinancialStatus);
        Assert.Null(s.Total);
    }

    [Fact]
    public void Summary_UnitNeitherInCrmNorPact_IsNotFound()
    {
        var s = Compose(new(Snapshot(4), [], [], new Dictionary<int, CrmUnitLink>(), true, Now));
        Assert.Equal("NotFound", s.LinkStatus); Assert.Equal("NoFinancialData", s.FinancialStatus);
    }

    [Fact]
    public void Summary_UnitWithNothingOpen_IsConfirmedNoDues_NotUnavailable()
    {
        var s = Compose(new(Snapshot(4), [Id(open: 0)], [], new Dictionary<int, CrmUnitLink>(), true, Now));
        Assert.Equal("NoDues", s.FinancialStatus); Assert.Equal(0m, s.Total);
    }

    [Fact]
    public void Summary_OnlyTheMatchingUnitsInstalmentsAreUsed_NeverAnotherApartmentOfTheCustomer()
    {
        var s = Compose(new(Snapshot(4), [Id(unit: 101)], [Inst(unit: 101, amount: 300m), Inst(unit: 102, amount: 9000m)], new Dictionary<int, CrmUnitLink>(), true, Now));
        Assert.Equal(300m, s.Total);
    }

    private static PactUnitInstalment Dated(DateOnly due, decimal remaining, string voucher, decimal? original = null, decimal? paid = null, string tenant = "T1", int unit = 101) =>
        new(4, tenant, unit, voucher, due, remaining, original, paid, "Installment");

    [Fact]
    public void PaymentFigures_UseTheDubaiMonth_DueIsTheWholeMonth_OverdueIsBeforeIt_RemainingIncludesFuture_TotalAndPaidAreUnavailable()
    {
        // Today is 2026-10-14 (Dubai). September is overdue, every day of October is Due (also the 5th, already past, and the 31st, still ahead), November and later are not yet due.
        var s = Compose(new(Snapshot(4), [Id(all: 6, open: 6)], [
            Dated(new(2026, 9, 30), 700m, "V1", 800m, 100m), Dated(new(2026, 10, 5), 400m, "V2"), Dated(new(2026, 10, 14), 100m, "V3"), Dated(new(2026, 10, 31), 250m, "V4"),
            Dated(new(2026, 11, 1), 900m, "V5"), Dated(new(2026, 12, 20), 50m, "V6")], new Dictionary<int, CrmUnitLink>(), true, Now));

        var p = Assert.IsType<CollectionsUnitPaymentFiguresDto>(s.Payments);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (p.MonthStart, p.MonthEnd));
        Assert.Equal(700m, p.Overdue);
        Assert.Equal(750m, p.DueThisMonth);                           // 400 + 100 + 250: all of October, whatever the day
        Assert.Equal(2400m, p.RemainingBalance);                      // every unpaid instalment, future ones included - not Due + Overdue (1450)
        Assert.NotEqual(p.Overdue + p.DueThisMonth, p.RemainingBalance);
        Assert.Null(p.TotalAmount);                                   // the source holds neither: never inferred, never 0
        Assert.Null(p.PaidAmount);
        Assert.Equal(["Overdue", "Due", "Due", "Due", "NotYetDue", "NotYetDue"], p.Schedule.Select(l => l.Status));
        Assert.Equal(["Voucher V1", "Voucher V2", "Voucher V3", "Voucher V4", "Voucher V5", "Voucher V6"], p.Schedule.Select(l => l.Description));
        Assert.Equal((800m, 100m), (p.Schedule[0].ScheduledAmount, p.Schedule[0].PaidAmount));     // what the source returned is passed through
        Assert.Null(p.Schedule[1].ScheduledAmount); Assert.Null(p.Schedule[1].PaidAmount);          // what it did not return stays null
        Assert.Equal(14, p.Schedule[0].DaysLate);
        Assert.Null(p.Schedule[3].DaysLate);                                                         // 31 Oct is ahead of today
        // The same rule as Receivables / Campaigns (the unit's own Due / Overdue / Total fields): one definition, one answer.
        Assert.Equal((p.Overdue, p.DueThisMonth, p.Overdue + p.DueThisMonth), (s.Overdue, s.Due, s.Total));
    }

    [Fact]
    public void PaymentFigures_CardsAndTableAreTheSameNumbers_ForOneUnitAndScope()
    {
        // The reported case: October holds 2,500 + 1,500, September 1,200 + 800 and three later months 3,500 each.
        var s = Compose(new(Snapshot(4), [Id(all: 7, open: 7)], [
            Dated(new(2026, 9, 11), 1200m, "A"), Dated(new(2026, 9, 28), 800m, "B"), Dated(new(2026, 10, 2), 2500m, "C"), Dated(new(2026, 10, 25), 1500m, "D"),
            Dated(new(2026, 11, 5), 3500m, "E"), Dated(new(2026, 12, 5), 3500m, "F"), Dated(new(2027, 1, 5), 3500m, "G")], new Dictionary<int, CrmUnitLink>(), true, Now));
        var p = Assert.IsType<CollectionsUnitPaymentFiguresDto>(s.Payments);
        Assert.Equal(4000m, p.DueThisMonth);
        Assert.Equal(2000m, p.Overdue);
        Assert.Equal(16500m, p.RemainingBalance);
        Assert.Equal(p.RemainingBalance, p.Schedule.Sum(l => l.RemainingAmount));
        Assert.Equal(p.DueThisMonth, p.Schedule.Where(l => l.Status == "Due").Sum(l => l.RemainingAmount));
        Assert.Equal(p.Overdue, p.Schedule.Where(l => l.Status == "Overdue").Sum(l => l.RemainingAmount));
        Assert.Equal(p.Overdue + p.DueThisMonth + p.Schedule.Where(l => l.Status == "NotYetDue").Sum(l => l.RemainingAmount), p.RemainingBalance);
    }

    [Fact]
    public void PaymentFigures_OnlyFutureInstalments_StillShowTheScheduleAndTheRemainingBalance_WithZeroDueAndOverdue()
    {
        var s = Compose(new(Snapshot(4), [Id()], [Dated(new(2026, 11, 15), 300m, "F1"), Dated(new(2027, 1, 15), 300m, "F2")], new Dictionary<int, CrmUnitLink>(), true, Now));
        Assert.Equal("NoDues", s.FinancialStatus);
        var p = Assert.IsType<CollectionsUnitPaymentFiguresDto>(s.Payments);
        Assert.Equal((0m, 0m, 600m), (p.Overdue, p.DueThisMonth, p.RemainingBalance));
        Assert.All(p.Schedule, l => Assert.Equal("NotYetDue", l.Status));
    }

    [Fact]
    public void PaymentFigures_AreAbsentWhenThereIsNoFinancialData_NotZero()
    {
        Assert.Null(Compose(new(Snapshot(4), [], [], new Dictionary<int, CrmUnitLink>(), true, Now)).Payments);
        Assert.Null(Compose(new(Snapshot(), [Id()], [Dated(new(2026, 10, 1), 10m, "X")], new Dictionary<int, CrmUnitLink>(), true, Now)).Payments);       // company snapshot not loaded
    }

    [Fact]
    public void Summary_SeveralCrmCustomers_NeedsReview_AndUsesPactNameOnly()
    {
        var link = new CrmUnitLink(CrmLinkStatus.Ambiguous, 2, 0, 0, 0, SourceContact.Empty);
        var s = Compose(new(Snapshot(4), [Id(mobile: "971500003001")], [Inst()], new Dictionary<int, CrmUnitLink> { [4] = link }, true, Now));
        Assert.Equal("NeedsReview", s.LinkStatus); Assert.Contains("CrmCustomerAmbiguous", s.ReviewReasons);
        Assert.Equal("Pact Customer", s.Customer.Name); Assert.Null(s.CrmCustomerId);
    }

    [Fact]
    public void Summary_DifferentPactPhone_CrmWins_AndIsNotReviewed()
    {
        var link = new CrmUnitLink(CrmLinkStatus.Single, 1, 11, 55, 77, new("Crm Customer", "+971551112222", "crm@example.test"));
        var s = Compose(new(Snapshot(4), [Id(mobile: "971500003001")], [Inst()], new Dictionary<int, CrmUnitLink> { [4] = link }, true, Now));
        Assert.DoesNotContain("ContactSourceConflict", s.ReviewReasons);
        Assert.Equal("+971551112222", s.Customer.Mobile);
    }

    [Fact]
    public void Summary_UnitInTwoCompanies_IsWithheld_NotGuessed()
    {
        var s = Compose(new(Snapshot(4, 32), [Id(company: 4), Id(tenant: "T9", company: 32)], [Inst(), Inst("T9", company: 32)], new Dictionary<int, CrmUnitLink>(), true, Now));
        Assert.Equal("NeedsReview", s.LinkStatus); Assert.Equal("Withheld", s.FinancialStatus); Assert.Null(s.Total);
    }

    [Fact]
    public async Task Parity_CampaignAndPaymentSummary_ShowTheSameIdentity()
    {
        var r = new Rig(); r.Source.Items.Add(Row("T1", 101, mobile: "", email: ""));
        r.Owners(Owner(1, "TP140", "101"));
        var campaign = Assert.Single(await r.Units());
        var link = (await r.Store.GetLinksAsync([(4, "TP140-101")], default))[(4, "TP140-101")];
        var s = Compose(new(Snapshot(4), [Id(mobile: "", email: "")], [Inst()], new Dictionary<int, CrmUnitLink> { [4] = link }, true, Now));
        Assert.Equal(campaign.CustomerName, s.Customer.Name);
        Assert.Equal(campaign.Phone, s.Customer.Mobile);
        Assert.Equal(campaign.Email, s.Customer.Email);
        Assert.Equal(campaign.CrmCustomerId, s.CrmCustomerId);
    }
}
