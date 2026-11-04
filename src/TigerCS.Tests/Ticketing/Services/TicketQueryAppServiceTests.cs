using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketQueryAppServiceTests
{
    private sealed record Fixture(
        TicketQueryAppService Service,
        FakeTicketRepository Tickets,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketResolutionRepository Resolutions,
        FakeTicketStatusHistoryRepository StatusHistory);

    private static Fixture CreateService(TimeProvider? timeProvider = null)
    {
        var tickets = new FakeTicketRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var resolutions = new FakeTicketResolutionRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        return new Fixture(
            new TicketQueryAppService(
                tickets, departmentAssignments, resolutions, statusHistory, ReopenPolicy.Default, timeProvider ?? TimeProvider.System),
            tickets, departmentAssignments, resolutions, statusHistory);
    }

    private static async Task<Ticket> SeedTicketAsync(FakeTicketRepository repo, int departmentId)
    {
        var ticket = Ticket.CreateVerified(
            $"TG-CS-20260821-{departmentId:D4}", departmentId, 10, 20, 5, (byte)PriorityLevel.High, "Issue", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        return ticket;
    }

    [Fact]
    public async Task GetQueueAsync_DepartmentEmployee_OnlySeesTicketsAssignedToThem_InAnyDepartment()
    {
        var f = CreateService();
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 2, true, DateTime.UtcNow, null));
        var own = await SeedTicketAsync(f.Tickets, 2);
        own.AssignTo(employeeId);
        var ownElsewhere = await SeedTicketAsync(f.Tickets, 3);   // assigned to them but in ANOTHER department: still theirs
        ownElsewhere.AssignTo(employeeId);
        var colleagues = await SeedTicketAsync(f.Tickets, 2);     // same department, assigned to a colleague: not theirs
        colleagues.AssignTo(Guid.NewGuid());
        await SeedTicketAsync(f.Tickets, 2);                      // same department, unassigned: not theirs
        await SeedTicketAsync(f.Tickets, 3);                      // other department, unassigned: not theirs

        var result = await f.Service.GetQueueAsync(
            employeeId, [Roles.DepartmentEmployee],
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 50));

        Assert.Equal(
            new[] { own.TicketId, ownElsewhere.TicketId }.Order(),
            result.Items.Select(i => i.TicketId).Order());
    }

    [Fact]
    public async Task GetQueueAsync_CsManager_SeesAllDepartments()
    {
        var f = CreateService();
        await SeedTicketAsync(f.Tickets, 2);
        await SeedTicketAsync(f.Tickets, 3);

        var result = await f.Service.GetQueueAsync(
            Guid.NewGuid(), [Roles.CsManager],
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 50));

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task GetDetailAsync_CallerOutsideDepartmentScope_ReturnsForbidden_DoesNotLeakTicket()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, 2);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.GetDetailAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task GetDetailAsync_UnverifiedTicket_ExposesUnverifiedAndNullReferences_NeverFabricatesVerified()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260821-0099", 2, 5, (byte)PriorityLevel.Critical, "Flooding", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
        Assert.Equal("Unverified", result.Response!.VerificationStatus);
        Assert.Null(result.Response.UnitReferenceId);
        Assert.Null(result.Response.ContactReferenceId);
    }

    [Fact]
    public async Task GetDetailAsync_UnknownTicket_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], 999);

        Assert.Equal(TicketQueryOutcome.NotFound, result.Outcome);
    }

    // ---- Ticket Details CRM Buyer / manual / legacy unit display (root-cause fix:
    // TicketDetailDto never carried the CrmBuyer*/Manual* snapshot fields the Ticket
    // entity already persists, so the read path silently dropped them) ----

    [Fact]
    public async Task GetDetailAsync_CrmBuyerVerifiedTicket_ExposesTheExactSelectedCustomerProjectAndUnit()
    {
        var f = CreateService();
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            "TG-CS-20260827-0001", 2,
            crmBuyerCustomerId: 555, crmBuyerLeadId: 306756, crmBuyerUnitId: 100003691, crmBuyerProjectId: 42,
            crmBuyerCustomerName: "Walid Jalanbo", crmBuyerProjectName: "Nobles Tower", crmBuyerUnitNumber: "2508",
            categoryId: 5, priorityId: (byte)PriorityLevel.Medium, requestSummary: "AC not cooling", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
        var dto = result.Response!;
        Assert.Equal("Verified", dto.VerificationStatus);
        Assert.Equal("Walid Jalanbo", dto.CrmBuyerCustomerName);
        Assert.Equal("Nobles Tower", dto.CrmBuyerProjectName);
        // The exact unit the agent selected — not merely "a" unit or the first match.
        Assert.Equal("2508", dto.CrmBuyerUnitNumber);
        Assert.Equal(100003691, dto.CrmBuyerUnitId);
        Assert.Equal(306756, dto.CrmBuyerLeadId);
        Assert.Null(dto.ManualProjectName);
        Assert.Null(dto.ManualUnitNumber);
        Assert.Null(dto.UnitReferenceId);
        Assert.Null(dto.ContactReferenceId);
    }

    [Fact]
    public async Task GetDetailAsync_ManualNoCrmMatchTicket_ExposesManualProjectAndUnit_NotVerified()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260827-0002", 2, 5, (byte)PriorityLevel.Low, "Water leak",
            DateTime.UtcNow, manualProjectName: "Sapphire Residences", manualUnitNumber: "1204");
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        var dto = result.Response!;
        Assert.Equal("Unverified", dto.VerificationStatus);
        Assert.Equal("Sapphire Residences", dto.ManualProjectName);
        Assert.Equal("1204", dto.ManualUnitNumber);
        Assert.Null(dto.CrmBuyerUnitId);
        Assert.Null(dto.CrmBuyerCustomerName);
    }

    [Fact]
    public async Task GetDetailAsync_LegacyVerifiedTicket_StaysBackwardCompatible_NoCrmOrManualFields()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, 2);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        var dto = result.Response!;
        Assert.Equal("Verified", dto.VerificationStatus);
        Assert.Equal(10, dto.UnitReferenceId);
        Assert.Equal(20, dto.ContactReferenceId);
        Assert.Null(dto.CrmBuyerUnitId);
        Assert.Null(dto.ManualProjectName);
        Assert.Null(dto.ManualUnitNumber);
    }

    [Fact]
    public async Task GetDetailAsync_PlainUnverifiedTicket_NullCrmAndManualFields_DoesNotThrow()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260827-0003", 2, 5, (byte)PriorityLevel.Critical, "General inquiry", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        var dto = result.Response!;
        Assert.Null(dto.CrmBuyerUnitId);
        Assert.Null(dto.CrmBuyerCustomerName);
        Assert.Null(dto.ManualProjectName);
        Assert.Null(dto.ManualUnitNumber);
        Assert.Null(dto.UnitReferenceId);
        Assert.Null(dto.ContactReferenceId);
    }

    // ---------------------------------------------------------------
    // Reopen display eligibility (Customer Workspace phase): the detail
    // read stamps IsReopenEligible from the same ReopenPolicy the Reopen
    // action enforces — lifecycle only, never a permission prediction.
    // ---------------------------------------------------------------

    private async Task<Ticket> SeedResolvedTicketAsync(
        Fixture f,
        DateTime resolvedAtUtc,
        bool close = false,
        DateTime? closedAtUtc = null,
        ResolutionOutcome outcome = ResolutionOutcome.Resolved)
    {
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        long? duplicateOf = outcome == ResolutionOutcome.Duplicate ? ticket.TicketId + 1 : null;
        ticket.Resolve(outcome, duplicateOf);
        await f.Resolutions.AddAsync(new TicketResolution(
            ticket.TicketId, outcome, "Fixed.", null, duplicateOf, Guid.NewGuid(), resolvedAtUtc));
        if (close)
        {
            ticket.Close();
            // The lifecycle row the reopen window is measured from.
            await f.StatusHistory.AddAsync(new TicketStatusHistory(
                ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Resolved, (byte)TicketStatus.Closed,
                Guid.NewGuid(), actorIsSystem: false, note: null, Guid.NewGuid(), closedAtUtc ?? resolvedAtUtc));
        }

        return ticket;
    }

    [Fact]
    public async Task GetDetailAsync_ClosedWithinTheWindow_IsReopenEligible_AndCarriesBothTimestamps()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new Notifications.Fakes.FakeTimeProvider(now));
        var ticket = await SeedResolvedTicketAsync(
            f, resolvedAtUtc: now.AddDays(-3), close: true, closedAtUtc: now.AddDays(-2));

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.True(result.Response!.IsReopenEligible);
        Assert.Equal(now.AddDays(-3), result.Response.ResolvedAtUtc);
        Assert.Equal(now.AddDays(-2), result.Response.ClosedAtUtc);
    }

    [Fact]
    public async Task GetDetailAsync_ClosedOutsideTheWindow_IsNotReopenEligible()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new Notifications.Fakes.FakeTimeProvider(now));
        var ticket = await SeedResolvedTicketAsync(
            f, resolvedAtUtc: now.AddDays(-10), close: true, closedAtUtc: now.AddDays(-8));

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.False(result.Response!.IsReopenEligible);
    }

    [Fact]
    public async Task GetDetailAsync_ResolvedButNotClosed_IsNotReopenEligible_UnderTheApprovedRule()
    {
        // The rule change the UI depends on: a Resolved ticket no longer
        // offers Reopen, however recently it was resolved.
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new Notifications.Fakes.FakeTimeProvider(now));
        var ticket = await SeedResolvedTicketAsync(f, resolvedAtUtc: now.AddHours(-1));

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.False(result.Response!.IsReopenEligible);
        Assert.Null(result.Response.ClosedAtUtc);
    }

    [Theory]
    [InlineData(ResolutionOutcome.Cancelled)]
    [InlineData(ResolutionOutcome.Rejected)]
    [InlineData(ResolutionOutcome.Duplicate)]
    public async Task GetDetailAsync_ClosedOnATerminalOutcome_IsNotReopenEligible(ResolutionOutcome outcome)
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new Notifications.Fakes.FakeTimeProvider(now));
        var ticket = await SeedResolvedTicketAsync(
            f, resolvedAtUtc: now.AddDays(-1), close: true, outcome: outcome);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.False(result.Response!.IsReopenEligible);
    }

    [Fact]
    public async Task GetDetailAsync_ActivelyWorkedTicket_IsNotReopenEligible()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);

        var result = await f.Service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.False(result.Response!.IsReopenEligible);
        Assert.Null(result.Response.ResolvedAtUtc);
        Assert.Null(result.Response.ClosedAtUtc);
    }

    [Fact]
    public async Task GetLifecycleHistoryAsync_ServesTheAppendOnlyHistory_BehindTheSameVisibilityCheckAsTheDetail()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var f = CreateService(new Notifications.Fakes.FakeTimeProvider(now));
        var ticket = await SeedResolvedTicketAsync(f, resolvedAtUtc: now.AddDays(-2), close: true);
        await f.StatusHistory.AddAsync(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Closed, (byte)TicketStatus.InProgress,
            Guid.NewGuid(), actorIsSystem: false, note: "Customer called back.", Guid.NewGuid(), now));

        var visible = await f.Service.GetLifecycleHistoryAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Success, visible.Outcome);
        var reopen = Assert.Single(visible.Response!.Entries, e => e.NewValue == nameof(TicketStatus.InProgress));
        Assert.Equal(nameof(TicketStatus.Closed), reopen.OldValue);
        Assert.Equal(nameof(TicketStatusDimension.TicketStatus), reopen.Dimension);
        Assert.Equal("Customer called back.", reopen.Note);
        Assert.False(reopen.ActorIsSystem);

        // A department-scoped caller with no claim to this ticket sees nothing.
        var hidden = await f.Service.GetLifecycleHistoryAsync(
            Guid.NewGuid(), [Roles.DepartmentEmployee], ticket.TicketId);
        Assert.Equal(TicketQueryOutcome.Forbidden, hidden.Outcome);
    }

    // ---- Channel Management: Ticket Details names the ORIGINATING channel,
    // even after an administrator deactivates it. ----

    [Fact]
    public async Task GetDetailAsync_ShowsTheOriginatingChannelName_EvenWhenTheChannelIsNowInactive()
    {
        var tickets = new FakeTicketRepository();
        var interactions = new FakeTicketInteractionRepository();
        var channels = new FakeChannelRepository().SeedWellKnown();
        var service = new TicketQueryAppService(
            tickets, new FakeUserDepartmentAssignmentRepository(), new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System,
            interactionRepository: interactions, channelRepository: channels);

        var ticket = await SeedTicketAsync(tickets, 2);
        await interactions.AddAsync(TicketInteraction.CreateLocal(ticket.TicketId, WellKnownChannels.Phone, "+971500000001", DateTime.UtcNow, isOriginatingInteraction: true));
        await interactions.AddAsync(TicketInteraction.CreateLocal(ticket.TicketId, WellKnownChannels.WhatsAppOrLiveChat, "+971500000001", DateTime.UtcNow.AddHours(1)));

        var before = await service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);
        Assert.Equal(WellKnownChannels.Phone, before.Response!.OriginatingChannelId);
        Assert.Equal("Phone", before.Response.OriginatingChannelName);

        // Admin retires Phone: the historical ticket still says Phone.
        (await channels.GetByIdAsync(WellKnownChannels.Phone))!.Deactivate();

        var after = await service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);
        Assert.Equal("Phone", after.Response!.OriginatingChannelName);
        Assert.DoesNotContain(await channels.ListAsync(activeOnly: true), c => c.ChannelId == WellKnownChannels.Phone);
    }

    [Fact]
    public async Task GetDetailAsync_TicketPredatingTheInteractionModel_HasNoOriginatingChannel()
    {
        var tickets = new FakeTicketRepository();
        var service = new TicketQueryAppService(
            tickets, new FakeUserDepartmentAssignmentRepository(), new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System,
            interactionRepository: new FakeTicketInteractionRepository(), channelRepository: new FakeChannelRepository().SeedWellKnown());
        var ticket = await SeedTicketAsync(tickets, 2);

        var detail = await service.GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Null(detail.Response!.OriginatingChannelId);
        Assert.Null(detail.Response.OriginatingChannelName);
    }
}
