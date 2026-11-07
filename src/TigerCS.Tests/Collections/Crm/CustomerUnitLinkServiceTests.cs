using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>
/// The customer / unit linking behind the Payment tab and New Ticket: CRM first, PACT by company + tower + apartment, Leasing, review and failure states.
/// These tests use in-memory CRM / PACT / mapping fakes: they prove the linking RULES, not that the real CRM / PACT sources return what the fakes return.
/// </summary>
public sealed partial class CustomerUnitLinkServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);   // 12:00 in Dubai
    private const string Phone = "0501234567";
    private const string PhoneE164 = "+971501234567";

    // ---- fakes ----

    private sealed class FakeCrm : ICrmBuyerLookupGateway
    {
        public CrmBuyerLookupResult Result { get; set; } = CrmBuyerLookupResult.NotFound();
        public List<string> Calls { get; } = [];
        public Task<CrmBuyerLookupResult> GetBuyerByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default) { Calls.Add(phoneNumber); return Task.FromResult(Result); }
    }

    private sealed class FakePact : IPactCustomerLookupGateway
    {
        public PactCustomerLookupResult Result { get; set; } = PactCustomerLookupResult.NotFound();
        public List<string> Calls { get; } = [];
        public Task<PactCustomerLookupResult> SearchByMobileAsync(string mobileNumber, CancellationToken cancellationToken = default) { Calls.Add(mobileNumber); return Task.FromResult(Result); }
    }

    private sealed class FakeMap : ICollectionsCrmProjectMap
    {
        public List<CrmProjectTowerMapping> Rows { get; } = [];
        public Task<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>> GetAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>>(Rows.Where(r => ids.Contains(r.CrmProjectId)).GroupBy(r => r.CrmProjectId).ToDictionary(g => g.Key, g => (IReadOnlyList<CrmProjectTowerMapping>)g.ToList()));
    }

    private sealed class FakeTowers : ICollectionsTowerCatalog
    {
        public List<CollectionsTowerDto> Towers { get; } = [new(1, "140", "Al Ghaf Tower", 4, true), new(2, "124", "Other Tower", 4, true), new(3, "127", "Third Tower", 32, true)];
        public Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CollectionsTowerDto>>(Towers);
    }

    private sealed class FakeUnits : IPactReceivablesSource, IUnitReceivablesSource
    {
        public List<PactUnitIdentity> Identities { get; } = [];
        public List<PactUnitInstalment> Instalments { get; } = [];
        public Dictionary<(string Key, int Company), CrmUnitLink> CrmLinks { get; } = [];
        public List<(string Key, int? Company)> Reads { get; } = [];
        public bool Throw { get; set; }
        /// <summary>Directory-only units: PACT knows them (fully paid, nothing open) but they have no open instalments.</summary>
        public List<PactDirectoryUnit> PaidUnits { get; } = [];
        public bool DirectoryComplete { get; set; }
        public int[] LoadedCompanies { get; set; } = [4, 32];

        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => Task.FromResult(new PactReceivablesSnapshot([], Now, false));

        public Task<PactAccountDirectory> ReadAccountDirectoryAsync(string unitKey, int? companyId, CancellationToken cancellationToken)
        {
            var all = Identities.Select(i => new PactDirectoryUnit(i.CompanyId, i.TenantId, i.UnitId, i.UnitCode, Math.Max(i.OpenRows, 1), 0, CollectionsUnitKey.Normalize(i.UnitCode) is { } k && !CollectionsUnitKey.IsListable(k))).Concat(PaidUnits).ToList();
            var matches = all.Where(u => !u.IsCancelled && CollectionsUnitKey.Normalize(u.UnitCode) == unitKey && (companyId is null || u.CompanyId == companyId)).ToList();
            var account = all.Where(u => matches.Any(m => m.CompanyId == u.CompanyId && m.TenantId == u.TenantId)).ToList();
            return Task.FromResult(new PactAccountDirectory(matches, account, DirectoryComplete, "Test directory", Now));
        }

        public Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken)
        {
            Reads.Add((unitKey, companyId));
            if (Throw) throw new PactReceivablesSourceException("PACT receivables are unavailable.");
            var ids = Identities.Where(i => CollectionsUnitKey.Normalize(i.UnitCode) == unitKey && (companyId is null || i.CompanyId == companyId)).ToList();
            var instalments = Instalments.Where(n => ids.Any(i => i.CompanyId == n.CompanyId && i.TenantId == n.TenantId && i.UnitId == n.UnitId) && n.RemainingAmount > 0).ToList();   // like the real read: future instalments too
            var links = CrmLinks.Where(l => l.Key.Key == unitKey && (companyId is null || l.Key.Company == companyId)).ToDictionary(l => l.Key.Company, l => l.Value);
            var snapshot = new SnapshotStatusDto(LoadedCompanies.Select(c => new SnapshotCompanyStatusDto(c, "Co" + c, true, Now, Now, "Succeeded", null, 0, 10, null, null, 0, 0, 0, 0, 0, 0, "Fresh", 1)).ToList(), [], 120);
            return Task.FromResult(new PactUnitReceivables(snapshot, ids, instalments, links, true, Now));
        }
    }

    private sealed class Rig
    {
        public FakeCrm Crm { get; } = new();
        public FakePact Pact { get; } = new();
        public FakeMap Map { get; } = new();
        public FakeTowers Towers { get; } = new();
        public FakeUnits Units { get; } = new();
        public CollectionsOptions Options { get; } = new() { Enabled = true };
        public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);
        public CustomerUnitLinkService Service { get; }

        public Rig() => Service = new(Options, new PactReceivablesOptions { Enabled = true }, new ReceivablesSnapshotOptions(), new CollectionsCrmOwnersOptions { Enabled = true },
            new(Options, new FakeDepartmentRepository()), new(Options, new FakeTimeProvider(Now)), Crm, Pact, Map, Towers, Units, NullLogger<CustomerUnitLinkService>.Instance);

        public async Task<CustomerUnitLinkResultDto> Find(string? selection = null) => (await Service.FindByPhoneAsync(Manager, Phone, selection)).Value!;
        public async Task<CustomerUnitLinkResultDto> ForCrm(int id, params string[] phones) => (await Service.FindForCrmCustomerAsync(Manager, id, phones.Length == 0 ? [Phone] : phones, null)).Value!;

        /// <summary>One PACT account for tower/unit with the given open instalments (one overdue, optionally one due today).</summary>
        public void Pact4(string tower, int apartment, string tenant, decimal overdue, decimal dueToday = 0m, int company = 4, string name = "Pact Name", string mobile = "", string email = "", int unitId = 0)
        {
            var id = unitId != 0 ? unitId : int.Parse(tower) * 10000 + apartment;
            Units.Identities.Add(new(company, tenant, id, $"TP{tower}-{apartment}", name, mobile, email, tower, "Tower " + tower, 3, overdue + dueToday > 0 ? 2 : 0));
            if (overdue > 0) Units.Instalments.Add(new(company, tenant, id, $"V{tower}{apartment}a", new(2026, 9, 1), overdue, overdue, 0m, "Installment"));
            if (dueToday > 0) Units.Instalments.Add(new(company, tenant, id, $"V{tower}{apartment}b", new(2026, 10, 14), dueToday, dueToday, 0m, "Installment"));
        }
    }

    private static CrmBuyerUnitDto CrmUnit(int unitId, string number, int project = 79, string projectName = "Al Ghaf Tower", string status = "Sold", int leadStatus = 8, int customerType = 1) =>
        new(1000 + unitId, leadStatus, status, unitId, number, 3, 2, 9, project, projectName, null, customerType, "Buyer");

    private static CrmBuyerMatchDto Buyer(int customerId, string? name, string? mobile, string? email, params CrmBuyerUnitDto[] units) =>
        new(new CrmCustomerDto(customerId, name, null, mobile, email), units);

    private static void CrmReturns(Rig r, params CrmBuyerMatchDto[] buyers) => r.Crm.Result = CrmBuyerLookupResult.Success(buyers);

    // ---- CRM first, unit-based financials ----

    [Fact]
    public async Task ReportedCase_CrmCustomerWhosePhoneIsAbsentFromPact_GetsThePactFiguresOfTheirUnit()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(498397, "Sreesaran Maru Sudhakar", PhoneE164, "s@example.test", CrmUnit(9090, "909")));
        r.Pact4("140", 909, "T-909", overdue: 700m, dueToday: 300m, name: "Someone Else At Pact", mobile: "");   // PACT's own phone is blank / different

        var result = await r.ForCrm(498397);

        Assert.Equal("Found", result.CrmStatus);
        Assert.Equal("NotSearched", result.PactStatus);
        Assert.Empty(r.Pact.Calls);                                        // PACT was never searched by phone
        var unit = Assert.Single(result.Candidates);
        Assert.Equal(("TP140-909", 4), (unit.UnitKey, unit.CompanyId));
        Assert.Equal("Linked", unit.LinkStatus);
        Assert.Equal("Available", unit.FinancialStatus);
        Assert.Equal((700m, 300m, 1000m), (unit.Overdue, unit.Due, unit.Total));   // Total = Due + Overdue
        Assert.Equal("Sreesaran Maru Sudhakar", unit.Customer.Name);       // CRM name wins over PACT's
        Assert.Equal(PhoneE164, unit.Customer.Mobile);
        Assert.Equal("Crm", unit.Customer.MobileSource);
        Assert.Equal(498397, unit.CrmCustomerId);
        Assert.Equal("T-909", unit.PactTenantId);
        Assert.Equal(result.Candidates[0].SelectionId, result.SelectedId);  // one candidate: nothing to choose
        Assert.False(result.SelectionRequired);
    }

    [Fact]
    public async Task UnmappedCrmProject_IsAMatchFailure_WithNoAmounts_NotAGuess_AndNoPactRead()
    {
        var r = new Rig();                                                  // no mapping row for CRM project 79, although a tower named "Al Ghaf Tower" exists in CollectionsTowers
        CrmReturns(r, Buyer(498397, "Sreesaran", PhoneE164, null, CrmUnit(9090, "909")));
        r.Pact4("140", 909, "T-909", overdue: 700m);

        var unit = Assert.Single((await r.ForCrm(498397)).Candidates);

        Assert.Equal("MatchFailed", unit.FinancialStatus);
        Assert.Equal("ProjectMappingMissing", unit.FinancialReason);
        Assert.Null(unit.Total);
        Assert.Empty(r.Units.Reads);
        Assert.Equal("Sreesaran", unit.Customer.Name);                      // the contact is still usable for the ticket
        Assert.Equal("Al Ghaf Tower", unit.ProjectName);
    }

    [Fact]
    public async Task CrmContactIsPreferred_AndMissingFieldsAreCompletedFromPact_WithoutMergingDifferentPeople()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Crm Name", "", "", CrmUnit(11, "101")));   // CRM has a name only
        r.Pact4("140", 101, "T1", 500m, name: "Pact Name", mobile: "971509998888", email: "pact@example.test");

        var unit = Assert.Single((await r.ForCrm(1)).Candidates);

        Assert.Equal(("Crm Name", "Crm"), (unit.Customer.Name, unit.Customer.NameSource));
        Assert.Equal(("+971509998888", "Pact"), (unit.Customer.Mobile, unit.Customer.MobileSource));
        Assert.Equal(("pact@example.test", "Pact"), (unit.Customer.Email, unit.Customer.EmailSource));
        Assert.Equal("Linked", unit.LinkStatus);

        // Different people: CRM has a phone, PACT a different one -> CRM alone, flagged for review.
        var c = new Rig();
        c.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(c, Buyer(1, "Crm Name", PhoneE164, "crm@example.test", CrmUnit(11, "101")));
        c.Pact4("140", 101, "T1", 500m, name: "Pact Name", mobile: "971559998888", email: "pact@example.test");
        var conflict = Assert.Single((await c.ForCrm(1)).Candidates);
        Assert.DoesNotContain("ContactSourceConflict", conflict.ReviewReasons);
        Assert.Equal(PhoneE164, conflict.Customer.Mobile);
        Assert.Equal("crm@example.test", conflict.Customer.Email);
        Assert.Equal("Crm Name", conflict.Customer.Name);
    }

    [Fact]
    public async Task ACustomerWithSeveralUnits_GetsEachUnitsOwnAmounts_AndMustChoose()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        r.Map.Rows.Add(new(80, "TP124", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "909"), CrmUnit(12, "101", project: 80, projectName: "Other Tower"), CrmUnit(13, "102", project: 80, projectName: "Other Tower")));
        r.Pact4("140", 909, "T1", overdue: 1000m);
        r.Pact4("124", 101, "T1", overdue: 200m, dueToday: 50m);
        r.Pact4("124", 102, "T1", overdue: 0m, dueToday: 0m);               // PACT lists it, nothing open

        var result = await r.ForCrm(1);

        Assert.Equal(3, result.Candidates.Count);
        Assert.True(result.SelectionRequired);                              // several units: the first is NOT taken
        Assert.Null(result.SelectedId);
        var byUnit = result.Candidates.ToDictionary(c => c.UnitKey!);
        Assert.Equal(1000m, byUnit["TP140-909"].Total);
        Assert.Equal(250m, byUnit["TP124-101"].Total);
        Assert.Equal(("NoDues", 0m), (byUnit["TP124-102"].FinancialStatus, byUnit["TP124-102"].Total));   // confirmed zero
        Assert.Equal(2, byUnit["TP124-101"].Instalments.Count + 0);
        Assert.Single(byUnit["TP140-909"].Instalments);
    }

    [Fact]
    public async Task TheSameApartmentNumber_InDifferentTowersAndCompanies_NeverShareAmounts()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));                    // company 4
        r.Map.Rows.Add(new(81, "TP127", null, "Manual"));                    // company 32
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "101"), CrmUnit(21, "101", project: 81, projectName: "Third Tower")));
        r.Pact4("140", 101, "TA", overdue: 111m, company: 4);
        r.Pact4("127", 101, "TB", overdue: 222m, company: 32);

        var result = await r.ForCrm(1);

        var a = result.Candidates.Single(c => c.UnitKey == "TP140-101"); var b = result.Candidates.Single(c => c.UnitKey == "TP127-101");
        Assert.Equal((4, 111m, "TA"), (a.CompanyId, a.Total, a.PactTenantId));
        Assert.Equal((32, 222m, "TB"), (b.CompanyId, b.Total, b.PactTenantId));
        Assert.Contains(("TP140-101", (int?)4), r.Units.Reads);
        Assert.Contains(("TP127-101", (int?)32), r.Units.Reads);
    }

    [Fact]
    public async Task AnApartmentNumberAlone_IsNeverEnough_AUnitOfAnotherTowerIsNotUsed()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "101")));
        r.Pact4("124", 101, "OTHER", overdue: 9999m);                       // same apartment number, different tower
        var unit = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal("NoFinancialData", unit.FinancialStatus);
        Assert.Equal("PactHoldsNoRecord", unit.FinancialReason);
        Assert.Null(unit.Total);
    }

    [Fact]
    public async Task TowerInBothCompanies_IsResolvedByTheCompanyThatHoldsTheExactUnit_AndStaysAmbiguousWhenBothDo()
    {
        var r = new Rig();
        r.Towers.Towers.Add(new(9, "140", "Al Ghaf Tower (Co 32)", 32, true));   // tower 140 now exists in both companies
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "909")));
        r.Pact4("140", 909, "T1", 500m);                                        // only company 4's PACT account holds TP140-909

        // The tower number alone cannot decide, the EXACT unit can: company 4 holds it, company 32 does not.
        var unit = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal((4, "Available", "Linked"), (unit.CompanyId, unit.FinancialStatus, unit.LinkStatus));
        Assert.Equal([("TP140-909", (int?)null), ("TP140-909", (int?)4)], r.Units.Reads);   // one probe of both companies, then the read of the company that holds it

        // Both companies hold the very same unit code: nobody is chosen.
        r.Units.Reads.Clear();
        r.Pact4("140", 909, "T2", 800m, company: 32);
        var ambiguous = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal(("MatchFailed", "CompanyMappingAmbiguous"), (ambiguous.FinancialStatus, ambiguous.FinancialReason));
        Assert.Null(ambiguous.CompanyId); Assert.Null(ambiguous.Total);
        Assert.Equal([("TP140-909", (int?)null)], r.Units.Reads);

        // The mapping itself naming the company resolves it - and only that company is read.
        r.Units.Reads.Clear();
        r.Map.Rows.Clear(); r.Map.Rows.Add(new(79, "TP140", 4, "Manual"));
        var resolved = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal((4, "Available"), (resolved.CompanyId, resolved.FinancialStatus));
        Assert.Equal([("TP140-909", (int?)4)], r.Units.Reads);
    }

    [Fact]
    public async Task TowerInBothCompanies_HeldByNeither_IsStillAReviewState()
    {
        var r = new Rig();
        r.Towers.Towers.Add(new(9, "140", "Al Ghaf Tower (Co 32)", 32, true));
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "909")));
        var unit = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal(("MatchFailed", "CompanyMappingAmbiguous"), (unit.FinancialStatus, unit.FinancialReason));
        Assert.Contains("neither company holds", unit.FinancialDetail);
    }

    [Fact]
    public async Task AmbiguousProjectMapping_TwoCodesForOneCrmProject_IsAReviewState()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual")); r.Map.Rows.Add(new(79, "TP124", null, "CrmFeed"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "909")));
        var unit = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal("ProjectMappingAmbiguous", unit.FinancialReason);
        Assert.Empty(r.Units.Reads);
    }

    [Fact]
    public async Task SeveralCrmOwnersOfTheUnit_AreFlaggedForReview_NotResolved()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner One", PhoneE164, null, CrmUnit(11, "909")));
        r.Pact4("140", 909, "T1", 500m);
        r.Units.CrmLinks[("TP140-909", 4)] = new(CrmLinkStatus.Ambiguous, 2, 0, 0, 0, SourceContact.Empty);
        var ambiguous = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal("NeedsReview", ambiguous.LinkStatus);
        Assert.Contains("CrmCustomerAmbiguous", ambiguous.ReviewReasons);

        r.Units.CrmLinks[("TP140-909", 4)] = new(CrmLinkStatus.Single, 1, 777, 5, 6, new("Other Owner", "+971550000000", ""));
        var conflict = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Contains("CrmOwnershipConflict", conflict.ReviewReasons);
        Assert.Equal(1, conflict.CrmCustomerId);                              // never silently swapped for the other owner
    }

    [Fact]
    public async Task OnlyEligibleCrmSales_Count_HotAndCancelledLeadsAndNonBuyersAreIgnored()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null,
            CrmUnit(11, "101", status: "Sold"), CrmUnit(12, "102", status: "Contract", leadStatus: 9), CrmUnit(13, "103", status: "Hot", leadStatus: 2),
            CrmUnit(14, "104", status: "Cancelled", leadStatus: 9), CrmUnit(15, "105", customerType: 2)));
        foreach (var apt in new[] { 101, 102, 103, 104, 105 }) r.Pact4("140", apt, "T" + apt, 100m);

        var result = await r.ForCrm(1);

        Assert.Equal(["TP140-101", "TP140-102"], result.Candidates.Select(c => c.UnitKey!).Order());
        Assert.Contains(result.Notes, n => n.Contains("3 CRM unit(s) were ignored"));
    }

    [Theory]
    [InlineData("Sold", 8, true)]
    [InlineData("Contract", 9, true)]
    [InlineData("contract", 99, true)]
    [InlineData("Approved", 4, false)]
    [InlineData("Cancelled", 8, false)]
    [InlineData("Sold - cancelled", 8, false)]
    [InlineData("Hot", 8, false)]
    [InlineData(null, 8, true)]
    [InlineData(null, 9, true)]
    [InlineData(null, 4, false)]
    [InlineData(null, 6, false)]
    public void SaleEligibility_IsDecidedByTheCrmStatusName_NumbersOnlyWhenNoNameWasSent(string? name, int number, bool expected) =>
        Assert.Equal(expected, CustomerUnitLinkService.IsEligibleSale(new(1, number, name, 5, "1", 3, 2, null, 79, "P", null, 1, "Buyer"), new CollectionsCrmOwnersOptions()));

    // ---- states ----

    [Fact]
    public async Task TheFourStates_AreToldApart()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "101"), CrmUnit(12, "102"), CrmUnit(13, "103")));
        r.Pact4("140", 101, "T1", overdue: 0m);                              // PACT holds the unit, nothing open: confirmed zero
        // 102: PACT holds nothing: no data

        var states = (await r.ForCrm(1)).Candidates.ToDictionary(c => c.UnitKey!, c => c.FinancialStatus);
        Assert.Equal("NoDues", states["TP140-101"]);
        Assert.Equal("NoFinancialData", states["TP140-102"]);

        r.Units.Throw = true;                                                // a source loading error
        var failed = (await r.ForCrm(1)).Candidates;
        Assert.All(failed, c => { Assert.Equal("SourceError", c.FinancialStatus); Assert.Equal("PactUnavailable", c.FinancialReason); Assert.Null(c.Total); });
    }

    [Fact]
    public async Task AnUnloadedCompanySnapshot_IsNoFinancialData_NotZero()
    {
        var r = new Rig { };
        r.Units.LoadedCompanies = [32];
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "101")));
        r.Pact4("140", 101, "T1", 500m);
        var unit = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal(("NoFinancialData", "CompanySnapshotNotLoaded"), (unit.FinancialStatus, unit.FinancialReason));
    }

    [Fact]
    public async Task NextMonthsInstalments_AreNotDueOrOverdue_ButAreInTheRemainingBalance_AndTheRestOfTheMonthIsDue()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "101")));
        r.Pact4("140", 101, "T1", overdue: 100m);
        r.Units.Instalments.Add(new(4, "T1", 1400101, "TOMORROW", new(2026, 10, 15), 40m, 40m, 0m, "Installment"));   // later this month: Due (the whole current Dubai month is Due)
        r.Units.Instalments.Add(new(4, "T1", 1400101, "NEXTMONTH", new(2026, 11, 1), 5000m, 5000m, 0m, "Installment")); // next month: neither Due nor Overdue
        var unit = Assert.Single((await r.ForCrm(1)).Candidates);
        Assert.Equal((100m, 40m, 140m), (unit.Overdue, unit.Due, unit.Total));
        Assert.Equal((100m, 40m, 5140m), (unit.Payments!.Overdue, unit.Payments.DueThisMonth, unit.Payments.RemainingBalance));
    }

    // ---- phone lookup ----

    [Fact]
    public async Task PhoneLookup_FindsTheCrmCustomerFirst_AndTheNumberIsNormalisedForPact()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "Owner", PhoneE164, null, CrmUnit(11, "101")));
        r.Pact4("140", 101, "T1", 500m);
        var result = await r.Find();
        Assert.Equal(PhoneE164, result.PhoneNormalized);
        Assert.Equal("Found", result.CrmStatus);
        Assert.Empty(r.Pact.Calls);
        Assert.Equal(500m, Assert.Single(result.Candidates).Total);
    }

    [Fact]
    public async Task PhoneLookup_RejectsAnUnusableNumber()
    {
        var r = new Rig();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await r.Service.FindByPhoneAsync(r.Manager, "abc", null)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await r.Service.FindByPhoneAsync(r.Manager, "", null)).Outcome);
    }

    [Fact]
    public async Task SeveralCrmCustomers_ArePresentedAsChoices_NeverCollapsedToTheFirst()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(1, "First", PhoneE164, null, CrmUnit(11, "101")), Buyer(2, "Second", PhoneE164, null, CrmUnit(12, "102")));
        r.Pact4("140", 101, "T1", 500m); r.Pact4("140", 102, "T2", 800m);
        var result = await r.Find();
        Assert.Equal(2, result.Candidates.Count);
        Assert.True(result.SelectionRequired);
        Assert.Null(result.SelectedId);
        Assert.Equal(["First", "Second"], result.Candidates.Select(c => c.Customer.Name).Order());
        // choosing one resolves it
        var chosen = await r.Find(result.Candidates.Single(c => c.Customer.Name == "Second").SelectionId);
        Assert.False(chosen.SelectionRequired);
        Assert.Equal("Second", chosen.Candidates.Single(c => c.SelectionId == chosen.SelectedId).Customer.Name);
    }

    [Fact]
    public async Task CrmCustomerWithNoEligibleUnit_FallsBackToPactByPhone()
    {
        var r = new Rig();
        CrmReturns(r, Buyer(1, "Hot Lead", PhoneE164, null, CrmUnit(11, "101", status: "Hot", leadStatus: 2)));
        r.Pact.Result = PactCustomerLookupResult.Success([new("T9", "Pact Owner", "971501234567", null, "1", [new("1400909", "C9", "909", "Al Ghaf Tower", "Res", 4, null, "TP140-909")])]);
        r.Pact4("140", 909, "T9", 400m, unitId: 1400909);
        var result = await r.Find();
        Assert.Equal("NoEligibleUnits", result.CrmStatus);
        Assert.Equal("Found", result.PactStatus);
        var unit = Assert.Single(result.Candidates);
        Assert.Equal(("Pact", 4, 400m), (unit.Source, unit.CompanyId, unit.Total));
    }

    // ---- Leasing (company 7) ----

    [Fact]
    public async Task LeasingCustomerFoundOnlyInPact_IsListedPerContract_WithTheExactMissingSource_AndNoZero()
    {
        var r = new Rig();                                                   // CRM knows nothing
        r.Pact.Result = PactCustomerLookupResult.Success([new("L1", "Leasing Tenant", "971501234567", "t@example.test", "1",
        [
            new("7001", "LC-1", "12A", "Leasing Tower", "Res", 7, null, "TP900-12A"),
            new("7002", "LC-2", "12B", "Leasing Tower", "Res", 7, new DateOnly(2027, 1, 1), "TP900-12B")
        ])]);

        var result = await r.Find();

        Assert.Equal(("NotFound", "Found"), (result.CrmStatus, result.PactStatus));
        Assert.Equal(2, result.Candidates.Count);
        Assert.True(result.SelectionRequired);
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(("Leasing", 7, "NoFinancialData", "LeasingReceivablesSourceMissing"), (c.Source, c.CompanyId, c.FinancialStatus, c.FinancialReason));
            Assert.Null(c.Total); Assert.Null(c.Due); Assert.Null(c.Overdue);
            Assert.Contains("p4AccountReceivablesV2", c.FinancialDetail);
            Assert.Equal("Leasing Tenant", c.Customer.Name); Assert.Equal("L1", c.PactTenantId);
        });
        Assert.Equal(["LC-1", "LC-2"], result.Candidates.Select(c => c.ContractNumber!).Order());
        Assert.Empty(r.Units.Reads);                                         // no company-7 source is invented or queried
    }

    [Fact]
    public async Task FormerTenantsAndCancelledContracts_AreExcluded_AndSaid()
    {
        var r = new Rig();
        r.Pact.Result = PactCustomerLookupResult.Success([new("L1", "Tenant", "971501234567", null, "1",
        [
            new("7001", "LC-OLD", "12A", "P", "Res", 7, new DateOnly(2026, 10, 13), "TP900-12A"),          // ended yesterday: former tenant
            new("7002", "LC-CANCELLED", "12*", "P", "Res", 7, null, "TP900-12*"),                          // cancelled apartment code
            new("7003", "LC-ENDS-TODAY", "12C", "P", "Res", 7, new DateOnly(2026, 10, 14), "TP900-12C"),   // ends today: still current
            new("7004", "LC-OPEN", "12D", "P", "Res", 7, null, "TP900-12D")
        ])]);
        var result = await r.Find();
        Assert.Equal(["LC-ENDS-TODAY", "LC-OPEN"], result.Candidates.Select(c => c.ContractNumber!).Order());
        Assert.Contains(result.Notes, n => n.Contains("ended before today"));
        Assert.Contains(result.Notes, n => n.Contains("cancelled apartment"));
    }

    [Fact]
    public async Task ACompany4PactContract_UsesOnlyItsOwnTenant_AFormerTenantOfTheSameUnitIsNotMixedIn()
    {
        var r = new Rig();
        r.Pact.Result = PactCustomerLookupResult.Success([new("CURRENT", "Current Tenant", "971501234567", null, "1", [new("1400101", "C1", "101", "P", "Res", 4, null, "TP140-101")])]);
        r.Pact4("140", 101, "CURRENT", overdue: 300m, unitId: 1400101);
        r.Pact4("140", 101, "FORMER", overdue: 9000m, unitId: 1400101);
        var unit = Assert.Single((await r.Find()).Candidates);
        Assert.Equal((300m, "CURRENT", "Linked"), (unit.Total, unit.PactTenantId, unit.LinkStatus));
    }

    [Fact]
    public async Task APactContractWithoutACompany_OrAnUnsupportedOne_IsNeverGuessed()
    {
        var r = new Rig();
        r.Pact.Result = PactCustomerLookupResult.Success([new("T", "Tenant", null, null, null,
        [
            new("1", "C1", "1", "P", "Res", null, null, "TP140-1"),
            new("2", "C2", "2", "P", "Res", 25, null, "TP140-2"),
            new("3", "C3", "3", "P", "Res", 4, null, null)
        ])]);
        var byContract = (await r.Find()).Candidates.ToDictionary(c => c.ContractNumber!);
        Assert.Equal("CompanyMissing", byContract["C1"].FinancialReason);
        Assert.Equal("CompanyNotSupported", byContract["C2"].FinancialReason);
        Assert.Equal("UnitCodeMissing", byContract["C3"].FinancialReason);
        Assert.All(byContract.Values, c => Assert.Null(c.Total));
    }

    [Fact]
    public async Task WhenCrmAndPactAreBothDown_BothSourcesAreReported_NotZero()
    {
        var r = new Rig();
        r.Crm.Result = CrmBuyerLookupResult.Unavailable("down");
        r.Pact.Result = PactCustomerLookupResult.Unavailable("down");
        var result = await r.Find();
        Assert.Equal(("Unavailable", "Unavailable"), (result.CrmStatus, result.PactStatus));
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task APactSearchIsNotRunWhenCrmFoundAnEligibleCustomer_AndACustomerWithoutAPhoneIsReported()
    {
        var r = new Rig();
        var none = (await r.Service.FindForCrmCustomerAsync(r.Manager, 1, ["", "abc"], null)).Value!;
        Assert.Equal("NotSearched", none.CrmStatus);
        Assert.Empty(r.Crm.Calls);
    }

    [Fact]
    public async Task ForACrmCustomerId_AnotherCustomerReturnedForThePhone_IsNeverShown()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", null, "Manual"));
        CrmReturns(r, Buyer(2, "Somebody Else", PhoneE164, null, CrmUnit(11, "101")));
        r.Pact4("140", 101, "T1", 500m);
        var result = await r.ForCrm(1);
        Assert.Empty(result.Candidates);
        Assert.Equal("NotFound", result.CrmStatus);
    }

    [Fact]
    public async Task AUserWithoutFinancialPermission_OrWithCollectionsOff_GetsNothing()
    {
        var r = new Rig();
        var reporter = new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []);
        Assert.Equal(CollectionsOutcome.Forbidden, (await r.Service.FindByPhoneAsync(reporter, Phone, null)).Outcome);
        r.Options.Enabled = false;
        Assert.Equal(CollectionsOutcome.Disabled, (await r.Service.FindByPhoneAsync(r.Manager, Phone, null)).Outcome);
    }
}
