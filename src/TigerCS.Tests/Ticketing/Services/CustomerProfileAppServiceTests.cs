using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Customer Details/Profile (Overview/Contact Info/Units): ticket-anchored
/// identity and authorization, live CRM data via the reused
/// CrmBuyerLookupAppService, and graceful degradation for every non-Found
/// outcome (never a live CRM call away from Customer History's own
/// contract, which this test file does not touch).
/// </summary>
public class CustomerProfileAppServiceTests
{
    private sealed record Fixture(
        CustomerProfileAppService Service,
        FakeTicketRepository Tickets,
        FakeIntakeRecordRepository IntakeRecords,
        FakeTicketInteractionRepository Interactions,
        FakeCrmBuyerLookupGateway CrmGateway,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments);

    /// <summary>
    /// The real <see cref="CrmBuyerLookupAppService"/> sits between the
    /// profile service and the (fake) gateway — exactly the production
    /// wiring, so the ambiguity/consolidation rules the New Ticket wizard
    /// relies on are exercised here unchanged rather than re-implemented.
    /// </summary>
    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var intakeRecords = new FakeIntakeRecordRepository();
        var interactions = new FakeTicketInteractionRepository();
        var crmGateway = new FakeCrmBuyerLookupGateway();
        var crmBuyerLookupAppService = new CrmBuyerLookupAppService(crmGateway, NullLogger<CrmBuyerLookupAppService>.Instance);
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var queryService = new TicketQueryAppService(
            tickets, departmentAssignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System);
        return new Fixture(
            new CustomerProfileAppService(
                tickets, intakeRecords, interactions, crmBuyerLookupAppService, queryService,
                NullLogger<CustomerProfileAppService>.Instance),
            tickets, intakeRecords, interactions, crmGateway, departmentAssignments);
    }

    private static async Task<Ticket> SeedCrmBuyerTicketAsync(FakeTicketRepository repo, FakeIntakeRecordRepository intakeRecords, int departmentId, int crmBuyerCustomerId, string phoneNumber)
    {
        var ticket = await SeedCrmBuyerTicketWithoutIntakeAsync(repo, departmentId, crmBuyerCustomerId);

        var intake = new IntakeRecord(WellKnownChannels.Phone, phoneNumber, departmentId, false, null, null, Guid.NewGuid(), DateTime.UtcNow);
        await intakeRecords.AddAsync(intake);
        intake.LinkToTicket(ticket.TicketId, ticket.VerificationStatus, hasSelectedUnit: true);

        return ticket;
    }

    /// <summary>A CRM Buyer verified ticket with no linked IntakeRecord — the historical/edge shape the phone fallback exists for.</summary>
    private static async Task<Ticket> SeedCrmBuyerTicketWithoutIntakeAsync(FakeTicketRepository repo, int departmentId, int crmBuyerCustomerId)
    {
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-CS-{Guid.NewGuid():N}"[..20], departmentId,
            crmBuyerCustomerId: crmBuyerCustomerId, crmBuyerLeadId: 1, crmBuyerUnitId: 101, crmBuyerProjectId: 10,
            crmBuyerCustomerName: "Walid Jalanbo", crmBuyerProjectName: "Nobles Tower", crmBuyerUnitNumber: "2508",
            categoryId: 5, priorityId: (byte)PriorityLevel.Medium, requestSummary: "Issue", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        return ticket;
    }

    private static CrmBuyerMatchDto Buyer(int customerId, params CrmBuyerUnitDto[] units) => new(
        new CrmCustomerDto(customerId, "Walid Jalanbo", "وليد جلنبو", "+971501234567", "walid@example.test"), units);

    private static CrmBuyerUnitDto Unit(int unitId, string unitNumber, string projectName, int leadStatus = 8, string? leadStatusName = "Sold", int unitType = 2, int? floor = 12) => new(
        LeadId: 1, LeadStatus: leadStatus, LeadStatusName: leadStatusName, UnitId: unitId, UnitNumber: unitNumber,
        UnitStatus: 3, UnitType: unitType, FloorNumber: floor, ProjectId: 10, ProjectName: projectName,
        ProjectArabicName: null, CustomerType: 1, CustomerTypeName: "Buyer");

    [Fact]
    public async Task GetForTicketAsync_CrmFindsTheCustomer_ReturnsFoundWithAllEligibleUnits()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success(
        [
            Buyer(493575, Unit(101, "2508", "Nobles Tower"), Unit(202, "2608", "Nobles Tower"), Unit(303, "9001", "Sky Tower"))
        ]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Success, result.Outcome);
        var profile = result.Response!;
        Assert.Equal("Found", profile.Status);
        Assert.Equal(493575, profile.CrmBuyerCustomerId);
        Assert.Equal("Walid Jalanbo", profile.FullNameEnglish);
        Assert.Equal("وليد جلنبو", profile.FullNameArabic);
        Assert.Equal("+971501234567", profile.MobileNumber);
        Assert.Equal("walid@example.test", profile.Email);
        // All eligible units for the customer, not just the current ticket's own unit (101).
        Assert.Equal(3, profile.Units.Count);
        Assert.Contains(profile.Units, u => u.UnitNumber == "2608");
        Assert.Contains(profile.Units, u => u.UnitNumber == "9001" && u.ProjectName == "Sky Tower");
        // CRM was queried with the ticket's own intake phone — never a caller-supplied value.
        Assert.Equal(1, f.CrmGateway.CallCount);
        Assert.Equal("+971501234567", f.CrmGateway.LastSearchedPhoneNumber);
    }

    /// <summary>
    /// A phone number can be reassigned in CRM after the ticket was created.
    /// The Buyer CRM returns is accepted only when it IS the ticket's
    /// persisted customer — another customer's name, contact details and
    /// units are never shown just because the phone now resolves to them.
    /// </summary>
    [Fact]
    public async Task GetForTicketAsync_CrmResolvesThePhoneToADifferentCustomer_ReturnsNotFoundInCrm_ShowsNothingOfTheOtherCustomer()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(777777, "Someone Else", null, "+971501234567", "else@example.test"),
                [Unit(909, "1201", "Other Tower")])
        ]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Success, result.Outcome);
        var profile = result.Response!;
        Assert.Equal("NotFoundInCrm", profile.Status);
        Assert.Equal(493575, profile.CrmBuyerCustomerId);
        Assert.Null(profile.FullNameEnglish);
        Assert.Null(profile.MobileNumber);
        Assert.Null(profile.Email);
        Assert.Empty(profile.Units);
    }

    /// <summary>
    /// The ambiguous-customer rule is CrmBuyerLookupAppService's, applied
    /// unchanged: CRM naming two distinct CustomerIds for one phone is a
    /// data-integrity conflict, and the profile shows neither candidate —
    /// not even the one whose id happens to equal the ticket's.
    /// </summary>
    [Fact]
    public async Task GetForTicketAsync_CrmReturnsTwoDistinctCustomersForThePhone_ReturnsAmbiguousCustomerMatch_EvenWhenOneIsTheTicketsCustomer()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success(
        [
            Buyer(493575, Unit(101, "2508", "Nobles Tower")),
            new CrmBuyerMatchDto(
                new CrmCustomerDto(888888, "Another Buyer", null, "+971501234567", null),
                [Unit(505, "0703", "Marina Tower")])
        ]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("AmbiguousCustomerMatch", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
        Assert.Null(result.Response.FullNameEnglish);
        Assert.Empty(result.Response.Units);
    }

    /// <summary>
    /// CRM fragmenting one customer's Leads across several entries is not
    /// ambiguity — CrmBuyerLookupAppService merges them, and the profile
    /// keeps every unit (deduplicated by UnitId), never just one.
    /// </summary>
    [Fact]
    public async Task GetForTicketAsync_CrmSplitsOneCustomerAcrossEntries_ReturnsFoundWithEveryUnitMerged()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success(
        [
            Buyer(493575, Unit(101, "2508", "Nobles Tower")),
            Buyer(493575, Unit(202, "2608", "Nobles Tower"), Unit(101, "2508", "Nobles Tower"))
        ]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("Found", result.Response!.Status);
        Assert.Equal(2, result.Response.Units.Count);
        Assert.Contains(result.Response.Units, u => u.UnitId == 101);
        Assert.Contains(result.Response.Units, u => u.UnitId == 202);
    }

    /// <summary>Non-Buyer entries are CrmBuyerLookupAppService's own scoping rule (Buyer only) — a customer left with no Buyer unit is NotFoundInCrm, same as New Ticket.</summary>
    [Fact]
    public async Task GetForTicketAsync_CrmReturnsOnlyNonBuyerUnits_ReturnsNotFoundInCrm()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(493575, "Walid Jalanbo", null, "+971501234567", null),
                [Unit(101, "2508", "Nobles Tower") with { CustomerType = 2, CustomerTypeName = "Tenant" }])
        ]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("NotFoundInCrm", result.Response!.Status);
        Assert.Empty(result.Response.Units);
    }

    /// <summary>
    /// A ticket whose IntakeRecord link is missing still has its intake
    /// phone persisted on the originating interaction — CRM is re-queried
    /// with that, instead of reporting CRM unavailable without ever asking.
    /// </summary>
    [Fact]
    public async Task GetForTicketAsync_NoLinkedIntakeRecord_UsesTheOriginatingInteractionsPhone()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketWithoutIntakeAsync(f.Tickets, 2, 493575);
        await f.Interactions.AddAsync(TicketInteraction.CreateLocal(
            ticket.TicketId, WellKnownChannels.Phone, "+971509999999", DateTime.UtcNow, isOriginatingInteraction: true));
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success([Buyer(493575, Unit(101, "2508", "Nobles Tower"))]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("Found", result.Response!.Status);
        Assert.Equal("+971509999999", f.CrmGateway.LastSearchedPhoneNumber);
    }

    /// <summary>The linked IntakeRecord stays authoritative when both exist.</summary>
    [Fact]
    public async Task GetForTicketAsync_IntakeRecordAndInteractionBothExist_PrefersTheIntakeRecordsPhone()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        await f.Interactions.AddAsync(TicketInteraction.CreateLocal(
            ticket.TicketId, WellKnownChannels.Phone, "+971509999999", DateTime.UtcNow, isOriginatingInteraction: true));
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success([Buyer(493575, Unit(101, "2508", "Nobles Tower"))]));

        await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("+971501234567", f.CrmGateway.LastSearchedPhoneNumber);
    }

    /// <summary>No intake phone anywhere: say so, rather than claiming CRM is unavailable when it was never asked.</summary>
    [Fact]
    public async Task GetForTicketAsync_NoPhoneOnRecordAtAll_ReturnsNoPhoneOnRecord_NoCrmCall()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketWithoutIntakeAsync(f.Tickets, 2, 493575);
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success([Buyer(493575, Unit(101, "2508", "Nobles Tower"))]));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("NoPhoneOnRecord", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
        Assert.Empty(result.Response.Units);
        Assert.Equal(0, f.CrmGateway.CallCount);
    }

    /// <summary>Every gateway failure mode — not only a transport failure — surfaces as CrmUnavailable; none of them is ever mistaken for "customer not found".</summary>
    [Theory]
    [InlineData(CrmBuyerLookupOutcome.Unavailable)]
    [InlineData(CrmBuyerLookupOutcome.Unauthorized)]
    [InlineData(CrmBuyerLookupOutcome.InvalidResponse)]
    public async Task GetForTicketAsync_CrmGatewayFailure_ReturnsCrmUnavailable(CrmBuyerLookupOutcome outcome)
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(new CrmBuyerLookupResult(outcome, Message: "diagnostic"));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("CrmUnavailable", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
        Assert.Empty(result.Response.Units);
    }

    [Fact]
    public async Task GetForTicketAsync_TicketNotCrmVerified_ReturnsNotCrmVerifiedStatus_NoCrmCall()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified("TG-CS-20260101-0001", 2, 5, (byte)PriorityLevel.Low, "No CRM match", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Success, result.Outcome);
        Assert.Equal("NotCrmVerified", result.Response!.Status);
        Assert.Null(result.Response.CrmBuyerCustomerId);
        Assert.Empty(result.Response.Units);
        Assert.Equal(0, f.CrmGateway.CallCount);
    }

    [Fact]
    public async Task GetForTicketAsync_CrmNoLongerFindsAMatch_ReturnsNotFoundInCrm_KeepsTheTicketsOwnCustomerId()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.NotFound());

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("NotFoundInCrm", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
        Assert.Null(result.Response.FullNameEnglish);
        Assert.Empty(result.Response.Units);
    }

    [Fact]
    public async Task GetForTicketAsync_CrmUnavailable_ReturnsCrmUnavailableStatus_KeepsTheTicketsOwnCustomerId()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.Unavailable());

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("CrmUnavailable", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
    }

    [Fact]
    public async Task GetForTicketAsync_CrmDataIntegrityConflict_ReturnsAmbiguousCustomerMatchStatus_NoUnitsOrNames()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        f.CrmGateway.Returns(CrmBuyerLookupResult.AmbiguousCustomerMatch());

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("AmbiguousCustomerMatch", result.Response!.Status);
        Assert.Equal(493575, result.Response.CrmBuyerCustomerId);
        Assert.Empty(result.Response.Units);
        Assert.Null(result.Response.FullNameEnglish);
    }

    [Fact]
    public async Task GetForTicketAsync_UnknownTicket_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], 999);

        Assert.Equal(CustomerProfileOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetForTicketAsync_CallerOutsideDepartmentScope_ReturnsForbidden_DoesNotCallCrm()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Forbidden, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal(0, f.CrmGateway.CallCount);
    }

    [Fact]
    public async Task GetForTicketAsync_DepartmentEmployeeAssignedTheTicket_Returns200()
    {
        var f = CreateService();
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 2, true, DateTime.UtcNow, null));
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        ticket.AssignTo(employeeId);
        f.CrmGateway.Returns(CrmBuyerLookupResult.Success([Buyer(493575, Unit(101, "2508", "Nobles Tower"))]));

        var result = await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(CustomerProfileOutcome.Success, result.Outcome);
        Assert.Equal("Found", result.Response!.Status);
    }

    [Fact]
    public async Task GetForTicketAsync_DepartmentEmployee_CannotReadTheCustomerOfAColleaguesTicket_OrAnUnassignedOne_DoesNotCallCrm()
    {
        var f = CreateService();
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 2, true, DateTime.UtcNow, null));
        var colleagues = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");
        colleagues.AssignTo(Guid.NewGuid());
        var unassigned = await SeedCrmBuyerTicketAsync(f.Tickets, f.IntakeRecords, 2, 493575, "+971501234567");

        Assert.Equal(CustomerProfileOutcome.Forbidden, (await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], colleagues.TicketId)).Outcome);
        Assert.Equal(CustomerProfileOutcome.Forbidden, (await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], unassigned.TicketId)).Outcome);
        Assert.Equal(0, f.CrmGateway.CallCount);
    }
}
