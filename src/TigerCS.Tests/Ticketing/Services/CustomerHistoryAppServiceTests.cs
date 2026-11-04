using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Customer -> Previous Ticket History (this increment). Covers the CRM-
/// verified identity path (CrmBuyerCustomerId), the phone-snapshot fallback
/// for tickets with no CrmBuyerCustomerId, department-visibility
/// authorization reuse, sort order, limits, and null-safety.
/// </summary>
public class CustomerHistoryAppServiceTests
{
    private sealed record Fixture(
        CustomerHistoryAppService Service,
        FakeTicketRepository Tickets,
        FakeIntakeRecordRepository IntakeRecords,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketResolutionRepository Resolutions,
        FakeTicketStatusHistoryRepository StatusHistory);

    private static Fixture CreateService(TimeProvider? timeProvider = null)
    {
        var tickets = new FakeTicketRepository();
        var intakeRecords = new FakeIntakeRecordRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var resolutions = new FakeTicketResolutionRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var clock = timeProvider ?? TimeProvider.System;
        var queryService = new TicketQueryAppService(
            tickets, departmentAssignments, resolutions, statusHistory, ReopenPolicy.Default, clock);
        return new Fixture(
            new CustomerHistoryAppService(
                tickets, intakeRecords, resolutions, statusHistory, queryService, ReopenPolicy.Default, clock),
            tickets, intakeRecords, departmentAssignments, resolutions, statusHistory);
    }

    private static async Task<Ticket> SeedCrmBuyerTicketAsync(
        FakeTicketRepository repo, int departmentId, int crmBuyerCustomerId, string unitNumber, DateTime createdAtUtc, string? customerName = "Walid Jalanbo")
    {
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-CS-{createdAtUtc:yyyyMMdd}-{Guid.NewGuid():N}"[..24], departmentId,
            crmBuyerCustomerId: crmBuyerCustomerId, crmBuyerLeadId: 1, crmBuyerUnitId: Random.Shared.Next(1, 999999), crmBuyerProjectId: 1,
            crmBuyerCustomerName: customerName, crmBuyerProjectName: "Nobles Tower", crmBuyerUnitNumber: unitNumber,
            categoryId: 5, priorityId: (byte)PriorityLevel.Medium, requestSummary: "Issue", createdAtUtc);
        await repo.AddAsync(ticket);
        return ticket;
    }

    // ---------------------------------------------------------------
    // 1-3: CRM-verified identity
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_NoPreviousTickets_ReturnsEmptyHistory()
    {
        var f = CreateService();

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], crmBuyerCustomerId: 493575);

        Assert.Equal("Verified", result.VerificationType);
        Assert.Equal(0, result.TotalTickets);
        Assert.Equal(0, result.OpenTickets);
        Assert.Equal(0, result.ClosedTickets);
        Assert.Empty(result.Tickets);
    }

    [Fact]
    public async Task GetByCrmCustomerIdAsync_OnePreviousTicket_ReturnsIt()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow);

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575);

        Assert.Equal(1, result.TotalTickets);
        var row = Assert.Single(result.Tickets);
        Assert.Equal(ticket.TicketId, row.TicketId);
        Assert.Equal("2508", row.UnitNumber);
    }

    [Fact]
    public async Task GetByCrmCustomerIdAsync_MultipleTicketsAcrossMultipleUnits_ReturnsAllOfThem()
    {
        var f = CreateService();
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow.AddDays(-3));
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2608", DateTime.UtcNow.AddDays(-2));
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2810", DateTime.UtcNow.AddDays(-1));

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575);

        Assert.Equal(3, result.TotalTickets);
        Assert.Equal(["2810", "2608", "2508"], result.Tickets.Select(t => t.UnitNumber));
    }

    // ---------------------------------------------------------------
    // 4: exact CrmBuyerCustomerId isolation
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_SamePhoneTwoDifferentCrmCustomerIds_NeverSharesHistory()
    {
        var f = CreateService();
        // Both tickets carry the same real-world phone number (captured on
        // their IntakeRecords) but were matched to two different CRM
        // customers — a phone number is not a trusted unique identity.
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, crmBuyerCustomerId: 111, "2508", DateTime.UtcNow);
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, crmBuyerCustomerId: 222, "9001", DateTime.UtcNow);

        var historyFor111 = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 111);
        var historyFor222 = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 222);

        Assert.Equal(1, historyFor111.TotalTickets);
        Assert.Equal(1, historyFor222.TotalTickets);
        Assert.DoesNotContain(historyFor111.Tickets, t => t.UnitNumber == "9001");
        Assert.DoesNotContain(historyFor222.Tickets, t => t.UnitNumber == "2508");
    }

    // ---------------------------------------------------------------
    // 5: current ticket excluded from Ticket Details previous history
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetForTicketAsync_ExcludesTheCurrentTicketFromItsOwnHistory()
    {
        var f = CreateService();
        var older = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow.AddDays(-1));
        var current = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2608", DateTime.UtcNow);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], current.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Success, result.Outcome);
        Assert.Equal(1, result.Response!.TotalTickets);
        var row = Assert.Single(result.Response.Tickets);
        Assert.Equal(older.TicketId, row.TicketId);
        Assert.DoesNotContain(result.Response.Tickets, t => t.TicketId == current.TicketId);
    }

    // ---------------------------------------------------------------
    // 6: newest first
    // ---------------------------------------------------------------

    [Fact]
    public async Task History_IsSortedNewestFirst()
    {
        var f = CreateService();
        var oldest = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 777, "A", DateTime.UtcNow.AddDays(-5));
        var middle = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 777, "B", DateTime.UtcNow.AddDays(-3));
        var newest = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 777, "C", DateTime.UtcNow.AddDays(-1));

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 777);

        Assert.Equal([newest.TicketId, middle.TicketId, oldest.TicketId], result.Tickets.Select(t => t.TicketId));
    }

    // ---------------------------------------------------------------
    // 7-8: manual/unverified customer history by phone snapshot, clearly marked
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetForTicketAsync_NoCrmBuyerCustomerId_FallsBackToPhoneSnapshot_MarkedUnverified()
    {
        var f = CreateService();
        const string phone = "+971501234567";

        var olderTicket = Ticket.CreateUnverified(
            "TG-CS-20260101-0001", 2, 5, (byte)PriorityLevel.Low, "Older issue", DateTime.UtcNow.AddDays(-1),
            manualProjectName: "Sapphire Residences", manualUnitNumber: "1204");
        await f.Tickets.AddAsync(olderTicket);
        var olderIntake = new IntakeRecord(WellKnownChannels.Phone, phone, 2, false, null, null, Guid.NewGuid(), DateTime.UtcNow.AddDays(-1));
        await f.IntakeRecords.AddAsync(olderIntake);
        olderIntake.LinkToTicket(olderTicket.TicketId, olderTicket.VerificationStatus, hasSelectedUnit: false);

        var currentTicket = Ticket.CreateUnverified(
            "TG-CS-20260102-0001", 2, 5, (byte)PriorityLevel.Low, "Current issue", DateTime.UtcNow,
            manualProjectName: "Sapphire Residences", manualUnitNumber: "1204");
        await f.Tickets.AddAsync(currentTicket);
        var currentIntake = new IntakeRecord(WellKnownChannels.Phone, phone, 2, false, null, null, Guid.NewGuid(), DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(currentIntake);
        currentIntake.LinkToTicket(currentTicket.TicketId, currentTicket.VerificationStatus, hasSelectedUnit: false);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], currentTicket.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationType);
        Assert.Equal(phone, result.Response.PhoneNumberSnapshot);
        Assert.Null(result.Response.CrmBuyerCustomerId);
        var row = Assert.Single(result.Response.Tickets);
        Assert.Equal(olderTicket.TicketId, row.TicketId);
    }

    // ---------------------------------------------------------------
    // 9: authorization — never wider than the ticket queue's own visibility
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_DepartmentEmployee_OnlySeesTicketsAssignedToThem()
    {
        var f = CreateService();
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 2, true, DateTime.UtcNow, null));
        var mine = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow);
        mine.AssignTo(employeeId);
        var colleagues = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2509", DateTime.UtcNow);   // same department, a colleague's
        colleagues.AssignTo(Guid.NewGuid());
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2510", DateTime.UtcNow);                    // same department, unassigned
        await SeedCrmBuyerTicketAsync(f.Tickets, 3, 493575, "2608", DateTime.UtcNow);                    // another department

        var result = await f.Service.GetByCrmCustomerIdAsync(employeeId, [Roles.DepartmentEmployee], 493575);

        Assert.Equal(1, result.TotalTickets);
        Assert.Equal("2508", Assert.Single(result.Tickets).UnitNumber);
    }

    [Fact]
    public async Task GetForTicketAsync_CallerOutsideDepartmentScope_ReturnsForbidden_DoesNotLeakHistory()
    {
        var f = CreateService();
        var ticket = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.GetForTicketAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Forbidden, result.Outcome);
        Assert.Null(result.Response);
    }

    // ---------------------------------------------------------------
    // 11: preview limit
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByCrmCustomerIdAsync_LimitsTheReturnedTickets_ButNotTheTotalCount()
    {
        var f = CreateService();
        for (var i = 0; i < 8; i++)
        {
            await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, $"{2500 + i}", DateTime.UtcNow.AddDays(-i));
        }

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575, limit: 5);

        Assert.Equal(8, result.TotalTickets);
        Assert.Equal(5, result.Tickets.Count);
    }

    // ---------------------------------------------------------------
    // 12: TicketId is preserved for detail links
    // ---------------------------------------------------------------

    [Fact]
    public async Task History_RowsCarryTheirOwnRealTicketId()
    {
        var f = CreateService();
        var a = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", DateTime.UtcNow.AddDays(-1));
        var b = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2608", DateTime.UtcNow);

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575);

        Assert.Equal([b.TicketId, a.TicketId], result.Tickets.Select(t => t.TicketId));
        Assert.All(result.Tickets, t => Assert.True(t.TicketId > 0));
    }

    // ---------------------------------------------------------------
    // 14: null CRM/customer data never throws
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetForTicketAsync_NoCrmBuyerCustomerIdAndNoLinkedIntakeRecord_ReturnsEmptyUnverifiedHistory_DoesNotThrow()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified("TG-CS-20260101-0002", 2, 5, (byte)PriorityLevel.Low, "No intake link", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationType);
        Assert.Equal(0, result.Response.TotalTickets);
        Assert.Empty(result.Response.Tickets);
    }

    [Fact]
    public async Task GetForTicketAsync_UnknownTicket_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], 999);

        Assert.Equal(CustomerHistoryOutcome.NotFound, result.Outcome);
    }

    // ---------------------------------------------------------------
    // External-identity history (Customer Workspace phase): PACT/Tasleeh
    // customers are keyed by the persisted CustomerVerificationSource +
    // ExternalCustomerId pair — never by display name or phone.
    // ---------------------------------------------------------------

    private static async Task<Ticket> SeedExternalTicketAsync(
        FakeTicketRepository repo, string source, string externalCustomerId, string unitNumber,
        DateTime createdAtUtc, int departmentId = 2, string summary = "AC issue")
    {
        var ticket = Ticket.CreateFromExternalLookup(
            $"TG-CS-{createdAtUtc:yyyyMMdd}-{Guid.NewGuid():N}"[..24], departmentId,
            customerVerificationSource: source, externalCustomerId: externalCustomerId, externalUnitId: $"U-{unitNumber}",
            manualProjectName: "Nobles Tower", manualUnitNumber: unitNumber,
            categoryId: 5, priorityId: (byte)PriorityLevel.Medium, requestSummary: summary, createdAtUtc);
        await repo.AddAsync(ticket);
        return ticket;
    }

    [Fact]
    public async Task GetByExternalIdentityAsync_ReturnsOnlyTicketsCarryingThatExactSourceAndExternalId()
    {
        var f = CreateService();
        var mine = await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1506", DateTime.UtcNow.AddDays(-2));
        // Same source, different customer id — a different real customer.
        await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-2", "1506", DateTime.UtcNow.AddDays(-1));
        // Same external id under a different source — never merged.
        await SeedExternalTicketAsync(f.Tickets, "Tasleeh", "PACT-CUST-1", "1506", DateTime.UtcNow);
        // A plain manual ticket must never attach to an externally-verified customer.
        var manual = Ticket.CreateUnverified(
            "TG-CS-20260901-0001", 2, 5, (byte)PriorityLevel.Low, "Manual entry", DateTime.UtcNow,
            manualProjectName: "Nobles Tower", manualUnitNumber: "1506");
        await f.Tickets.AddAsync(manual);

        var result = await f.Service.GetByExternalIdentityAsync(Guid.NewGuid(), [Roles.CsManager], "Pact", "PACT-CUST-1");

        Assert.Equal("ExternalVerified", result.VerificationType);
        Assert.Equal("Pact", result.ExternalSource);
        Assert.Equal("PACT-CUST-1", result.ExternalCustomerId);
        var row = Assert.Single(result.Tickets);
        Assert.Equal(mine.TicketId, row.TicketId);
    }

    [Fact]
    public async Task GetByExternalIdentityAsync_ForADepartmentEmployee_IsScopedToTheirAssignedTickets()
    {
        var f = CreateService();
        var caller = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 2, true, DateTime.UtcNow, null));
        var mine = await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1506", DateTime.UtcNow.AddDays(-2), departmentId: 2);
        mine.AssignTo(caller);
        await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1204", DateTime.UtcNow.AddDays(-1), departmentId: 9);
        await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1302", DateTime.UtcNow.AddDays(-1), departmentId: 2);   // own department, not assigned to them

        var result = await f.Service.GetByExternalIdentityAsync(caller, [Roles.DepartmentEmployee], "Pact", "PACT-CUST-1");

        var row = Assert.Single(result.Tickets);
        Assert.Equal("1506", row.UnitNumber);
    }

    [Fact]
    public async Task GetForTicketAsync_ExternallyVerifiedAnchor_UsesTheExternalIdentity_NeverThePhoneFallback()
    {
        var f = CreateService();
        var anchor = await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1506", DateTime.UtcNow.AddDays(-3));
        var sibling = await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1204", DateTime.UtcNow.AddDays(-2));
        await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-2", "1802", DateTime.UtcNow.AddDays(-1));

        var result = await f.Service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], anchor.TicketId);

        Assert.Equal(CustomerHistoryOutcome.Success, result.Outcome);
        Assert.Equal("ExternalVerified", result.Response!.VerificationType);
        var row = Assert.Single(result.Response.Tickets);
        Assert.Equal(sibling.TicketId, row.TicketId);
    }

    // ---------------------------------------------------------------
    // Reopen-eligibility stamping (Customer Workspace phase): the same
    // ReopenPolicy that gates the Reopen action computes each row's flag.
    // ---------------------------------------------------------------

    /// <summary>Drives a seeded ticket to Closed and records the lifecycle row the reopen window is measured from.</summary>
    private static async Task CloseAsync(Fixture f, Ticket ticket, DateTime resolvedAtUtc, DateTime closedAtUtc, string note)
    {
        MoveToResolved(ticket);
        await f.Resolutions.AddAsync(new TicketResolution(
            ticket.TicketId, ResolutionOutcome.Resolved, note, null, null, Guid.NewGuid(), resolvedAtUtc));
        ticket.Close();
        await f.StatusHistory.AddAsync(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
            Guid.NewGuid(), actorIsSystem: false, note: null, Guid.NewGuid(), closedAtUtc));
    }

    [Fact]
    public async Task History_StampsReopenEligibility_FromTheClosureMomentAndTheWindow()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new Notifications.Fakes.FakeTimeProvider(now));

        var recentlyClosed = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2508", now.AddDays(-10));
        await CloseAsync(f, recentlyClosed, now.AddDays(-3), now.AddDays(-2), "Fixed.");

        var longClosed = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2608", now.AddDays(-40));
        await CloseAsync(f, longClosed, now.AddDays(-31), now.AddDays(-30), "Fixed long ago.");

        // Resolved but never closed: not reopenable under the approved rule,
        // however recent the resolution.
        var resolvedOnly = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2709", now.AddDays(-5));
        MoveToResolved(resolvedOnly);
        await f.Resolutions.AddAsync(new TicketResolution(
            resolvedOnly.TicketId, ResolutionOutcome.Resolved, "Done.", null, null, Guid.NewGuid(), now.AddDays(-1)));

        var stillOpen = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "2810", now.AddDays(-1));

        var result = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575, limit: 10);

        Assert.True(result.Tickets.Single(t => t.TicketId == recentlyClosed.TicketId).IsReopenEligible);
        Assert.False(result.Tickets.Single(t => t.TicketId == longClosed.TicketId).IsReopenEligible);
        Assert.False(result.Tickets.Single(t => t.TicketId == resolvedOnly.TicketId).IsReopenEligible);
        Assert.False(result.Tickets.Single(t => t.TicketId == stillOpen.TicketId).IsReopenEligible);
        Assert.Equal(now.AddDays(-3), result.Tickets.Single(t => t.TicketId == recentlyClosed.TicketId).ResolvedAtUtc);
    }

    [Fact]
    public async Task History_CarriesTheRequestSummary_ForListScanning()
    {
        var f = CreateService();
        await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1506", DateTime.UtcNow, summary: "Water leak in kitchen");

        var result = await f.Service.GetByExternalIdentityAsync(Guid.NewGuid(), [Roles.CsManager], "Pact", "PACT-CUST-1");

        Assert.Equal("Water leak in kitchen", Assert.Single(result.Tickets).RequestSummary);
    }

    private static void MoveToResolved(Ticket ticket)
    {
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
    }

    // ---------------------------------------------------------------
    // Phase E — duplicate-ticket awareness: the same history query,
    // narrowed to one unit and ordered active-first. Deterministic
    // identity + unit matching only; one repository query per call.
    // ---------------------------------------------------------------

    [Fact]
    public async Task RelatedTickets_SameCrmCustomerAndUnit_AreReturned_OtherUnitsAndCustomersAreNot()
    {
        var f = CreateService();
        var sameUnit = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "1506", DateTime.UtcNow.AddDays(-2));
        // Same customer, different unit — must not surface as a same-unit match.
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "1204", DateTime.UtcNow.AddDays(-1));
        // Different customer, same unit number — a different real customer.
        await SeedCrmBuyerTicketAsync(f.Tickets, 2, 999999, "1506", DateTime.UtcNow);

        var result = await f.Service.GetByCrmCustomerIdAsync(
            Guid.NewGuid(), [Roles.CsManager], 493575, unitNumber: "1506", orderActiveFirst: true);

        var row = Assert.Single(result.Tickets);
        Assert.Equal(sameUnit.TicketId, row.TicketId);
        Assert.Equal(1, f.Tickets.SearchCustomerHistoryCallCount);
    }

    [Fact]
    public async Task RelatedTickets_PactIdentityPlusUnit_MatchesOnlyThatCustomersUnitTickets()
    {
        var f = CreateService();
        var mine = await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1506", DateTime.UtcNow.AddDays(-3));
        await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1204", DateTime.UtcNow.AddDays(-2));
        await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-2", "1506", DateTime.UtcNow.AddDays(-1));

        var result = await f.Service.GetByExternalIdentityAsync(
            Guid.NewGuid(), [Roles.CsManager], "Pact", "PACT-CUST-1", unitNumber: "1506", orderActiveFirst: true);

        var row = Assert.Single(result.Tickets);
        Assert.Equal(mine.TicketId, row.TicketId);
    }

    [Fact]
    public async Task RelatedTickets_TasleehIdentity_WorksThroughTheSamePersistedIdentityPair()
    {
        var f = CreateService();
        var mine = await SeedExternalTicketAsync(f.Tickets, "Tasleeh", "TAS-CUST-9", "1802", DateTime.UtcNow.AddDays(-1));
        await SeedExternalTicketAsync(f.Tickets, "Pact", "TAS-CUST-9", "1802", DateTime.UtcNow);

        var result = await f.Service.GetByExternalIdentityAsync(
            Guid.NewGuid(), [Roles.CsManager], "Tasleeh", "TAS-CUST-9", unitNumber: "1802", orderActiveFirst: true);

        var row = Assert.Single(result.Tickets);
        Assert.Equal(mine.TicketId, row.TicketId);
    }

    [Fact]
    public async Task RelatedTickets_ActiveTicketsComeBeforeNewerResolvedOnes()
    {
        var f = CreateService();
        var resolvedNewer = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "1506", DateTime.UtcNow.AddDays(-1));
        MoveToResolved(resolvedNewer);
        var activeOlder = await SeedCrmBuyerTicketAsync(f.Tickets, 2, 493575, "1506", DateTime.UtcNow.AddDays(-5));

        var result = await f.Service.GetByCrmCustomerIdAsync(
            Guid.NewGuid(), [Roles.CsManager], 493575, unitNumber: "1506", orderActiveFirst: true);

        Assert.Equal([activeOlder.TicketId, resolvedNewer.TicketId], result.Tickets.Select(t => t.TicketId));
        // The default (non-related) ordering stays newest-first, unchanged.
        var plain = await f.Service.GetByCrmCustomerIdAsync(Guid.NewGuid(), [Roles.CsManager], 493575);
        Assert.Equal([resolvedNewer.TicketId, activeOlder.TicketId], plain.Tickets.Select(t => t.TicketId));
    }

    [Fact]
    public async Task RelatedTickets_UnitFilter_MatchesTheManualUnitSnapshotToo()
    {
        // An external-lookup ticket persists its unit as the manual snapshot
        // — the same-unit rule must still find it (exact match, no fuzz).
        var f = CreateService();
        var external = await SeedExternalTicketAsync(f.Tickets, "Pact", "PACT-CUST-1", "1506", DateTime.UtcNow);

        var result = await f.Service.GetByExternalIdentityAsync(
            Guid.NewGuid(), [Roles.CsManager], "Pact", "PACT-CUST-1", unitNumber: "1506");

        Assert.Equal(external.TicketId, Assert.Single(result.Tickets).TicketId);

        var noMatch = await f.Service.GetByExternalIdentityAsync(
            Guid.NewGuid(), [Roles.CsManager], "Pact", "PACT-CUST-1", unitNumber: "15");
        Assert.Empty(noMatch.Tickets);
    }
}
