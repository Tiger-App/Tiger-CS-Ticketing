using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Genesys AI → Call Center, finalized: (1) a customer's request for a human reaches Call Center first even when automation pre-assigned
/// an employee — but never over genuine human ownership or an explicit AgentTransfer; (2) the Call Center agent who accepts / handles the
/// interaction owns the ticket or is authorized through that interaction, without any global CS Agent permission.
/// </summary>
public class CallCenterFirstAndInteractionOwnershipTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly IReadOnlyCollection<string> CsAgent = [Roles.CsAgent];

    private sealed record Setup(
        GenesysServiceFixture F, GenesysAgentHandoffAppService Handoff, long TicketId, int OriginDepartmentId, int CallCenterDepartmentId);

    private static async Task<Setup> NewAsync(string conversationId = "conv-cc")
    {
        var f = new GenesysServiceFixture();
        var (origin, _) = f.SeedGenesysDepartment("Collections", "COLL");
        var callCenter = f.Departments.AddDepartment("Call Center", "CC");
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, GenesysChannel.WebsiteChat,
                CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali", DepartmentId: origin.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);

        var handoff = new GenesysAgentHandoffAppService(
            f.Options, f.Conversations, f.Handoffs, f.WorkflowEvents, f.Tickets, f.UnitOfWork, f.Audit, TimeProvider.System,
            f.Departments, f.TicketAssignments);
        return new Setup(f, handoff, created.Ticket!.TicketId, origin.DepartmentId, callCenter.DepartmentId);
    }

    private static async Task<Guid> PreAssignAsync(Setup s, Guid? assigningActor)
    {
        var owner = Guid.NewGuid();
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        ticket.AssignTo(owner);
        await s.F.TicketAssignments.AddAsync(new TicketAssignment(s.TicketId, owner, s.OriginDepartmentId, DateTime.UtcNow, assigningActor));
        return owner;
    }

    private static Task<GenesysHandoffResult> RequestHumanAsync(Setup s, string trigger = "CustomerRequestedHuman") =>
        s.Handoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-cc", Reason: "Customer asked for a person", Trigger: trigger, TicketId: s.TicketId));

    // ===================== Requirement 1 =====================

    [Fact]
    public async Task AutomaticPreAssignment_DoesNotStopTheTicketReachingCallCenterFirst_AndIsReleasedAndAudited()
    {
        var s = await NewAsync();
        var autoOwner = await PreAssignAsync(s, assigningActor: null);   // a Request Type rule / Round Robin put them there

        var result = await RequestHumanAsync(s);

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, result.Outcome);
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(s.CallCenterDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(s.F.TicketAssignments.Added, a => a.IsCurrent && a.AssignedEmployeeId == autoOwner);   // superseded, not deleted
        Assert.Contains(s.F.TicketAssignments.Added, a => a.AssignedEmployeeId == autoOwner);
        var transfer = Assert.Single(s.F.Audit.Entries, e => e.Action == "Transfer");
        Assert.Contains(autoOwner.ToString(), transfer.BeforeValue);
        Assert.Contains("AutomaticPreAssignmentReleased=true", transfer.AfterValue);
        Assert.Contains(s.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffRequested);
        Assert.Equal(s.CallCenterDepartmentId, Assert.Single(s.F.Handoffs.All).DepartmentId);
    }

    [Fact]
    public async Task HumanOwnedTicket_IsNeverTakenFromItsOwner()
    {
        var s = await NewAsync();
        var human = await PreAssignAsync(s, assigningActor: Guid.NewGuid());   // a person assigned / accepted it

        await RequestHumanAsync(s);

        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(s.OriginDepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(human, ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(s.F.Audit.Entries, e => e.Action == "Transfer");
    }

    [Fact]
    public async Task OwnerWithNoAssignmentRecord_IsTreatedAsHumanOwned()
    {
        var s = await NewAsync();
        var owner = Guid.NewGuid();
        (await s.F.Tickets.GetByIdAsync(s.TicketId))!.AssignTo(owner);   // legacy data: cannot prove it was automatic

        await RequestHumanAsync(s);

        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(s.OriginDepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task AutoOwnedTicketWhoseWorkAlreadyStarted_IsTreatedAsHumanOwned()
    {
        var s = await NewAsync();
        var owner = await PreAssignAsync(s, assigningActor: null);
        (await s.F.Tickets.GetByIdAsync(s.TicketId))!.ChangeStatus(TicketStatus.InProgress);   // someone began working it

        await RequestHumanAsync(s);

        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(s.OriginDepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task ExplicitAgentTransfer_KeepsItsAutomaticOwnerAndDepartment()
    {
        var s = await NewAsync();
        var owner = await PreAssignAsync(s, assigningActor: null);

        await RequestHumanAsync(s, trigger: "AgentTransfer");

        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(s.OriginDepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task UnownedTicket_StillRoutesToCallCenter()
    {
        var s = await NewAsync();

        await RequestHumanAsync(s);

        Assert.Equal(s.CallCenterDepartmentId, (await s.F.Tickets.GetByIdAsync(s.TicketId))!.CurrentDepartmentId);
    }

    [Fact]
    public async Task ARedeliveredRequest_DoesNotRouteTwiceOrWriteASecondTransfer()
    {
        var s = await NewAsync();
        await PreAssignAsync(s, assigningActor: null);

        await RequestHumanAsync(s);
        var again = await RequestHumanAsync(s);

        Assert.Equal(GenesysHandoffOutcome.AlreadyRequested, again.Outcome);
        Assert.Single(s.F.Audit.Entries, e => e.Action == "Transfer");
    }

    // ===================== Requirement 2 =====================

    [Fact]
    public async Task CallCenterAgentWhoAccepts_OwnsTheTicket_AndCanResolveIt_ButAnotherCsAgentCannot()
    {
        var s = await NewAsync();
        await PreAssignAsync(s, assigningActor: null);
        await RequestHumanAsync(s);

        var agent = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        foreach (var id in new[] { agent, colleague })
        {
            s.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, s.CallCenterDepartmentId, true, DateTime.UtcNow, null));
        }

        var handoffId = Assert.Single(s.F.Handoffs.All).TicketAgentHandoffId;
        var accepted = await s.F.PendingWork.AcceptAsync(agent, CsAgent, handoffId);

        Assert.Equal(AgentHandoffOutcome.Success, accepted.Outcome);
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        var assignment = s.F.TicketAssignments.Added.Last(a => a.IsCurrent);
        Assert.Equal((agent, agent), (assignment.AssignedEmployeeId, assignment.AssigningActorEmployeeId));   // a human acceptance, not automation

        var row = Convert.ToBase64String(ticket.RowVersion);
        var other = await s.F.Lifecycle.ResolveAsync(
            colleague, CsAgent, s.TicketId, new ResolveTicketRequestDto("Resolved", "x", null, null, ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.Forbidden, other.Outcome);

        var own = await s.F.Lifecycle.ResolveAsync(
            agent, CsAgent, s.TicketId, new ResolveTicketRequestDto("Resolved", "done", null, null, ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.Success, own.Outcome);
        Assert.NotNull(row);
    }

    private sealed record Handled(
        FakeTicketInteractionHandlerRepository Handlers, FakeUserDepartmentAssignmentRepository Members, Ticket Ticket, Guid Agent);

    private static async Task<Handled> HandledTicketAsync(bool isMember = true, bool recorded = true, Guid? owner = null)
    {
        var tickets = new FakeTicketRepository();
        var ticket = Ticket.CreateUnverified("TG-CS-20261101-0001", departmentId: 5, categoryId: 1, (byte)PriorityLevel.Medium, "x", DateTime.UtcNow);
        await tickets.AddAsync(ticket);
        if (owner is { } o)
        {
            ticket.AssignTo(o);
        }

        var agent = Guid.NewGuid();
        var members = new FakeUserDepartmentAssignmentRepository();
        if (isMember)
        {
            members.Assignments.Add(new UserDepartmentAssignment(agent, 5, true, DateTime.UtcNow, null));
        }

        var handlers = new FakeTicketInteractionHandlerRepository();
        if (recorded)
        {
            await handlers.AddIfAbsentAsync(TicketInteractionHandler.Create(
                ticketInteractionId: 1, ticketId: ticket.TicketId, handledByUserId: agent, genesysAgentUserId: "ga-1", InteractionHandlerSource.Routing, DateTime.UtcNow, DateTime.UtcNow));
        }

        return new Handled(handlers, members, ticket, agent);
    }

    private static Task<bool> ActsAsync(Handled h, params string[] roles) =>
        TicketOwnershipRules.IsActingInteractionHandlerAsync(h.Handlers, h.Members, h.Agent, roles, h.Ticket);

    [Fact]
    public async Task InteractionHandler_InTheTicketsDepartment_WhileNobodyElseOwnsIt_IsAuthorized() =>
        Assert.True(await ActsAsync(await HandledTicketAsync(), Roles.CsAgent));

    [Fact]
    public async Task InteractionHandler_WhoAlreadyOwnsTheTicket_IsAuthorized() =>
        Assert.True(await ActsAsync(await HandledTicketAsync(owner: null), Roles.CsAgent));

    [Fact]
    public async Task InteractionHandler_IsNotAuthorized_OnATicketAnotherPersonOwns() =>
        Assert.False(await ActsAsync(await HandledTicketAsync(owner: Guid.NewGuid()), Roles.CsAgent));

    [Fact]
    public async Task InteractionHandler_IsNotAuthorized_OutsideTheTicketsDepartment() =>
        Assert.False(await ActsAsync(await HandledTicketAsync(isMember: false), Roles.CsAgent));

    [Fact]
    public async Task ACsAgentWhoNeverHandledTheInteraction_IsNotAuthorized() =>
        Assert.False(await ActsAsync(await HandledTicketAsync(recorded: false), Roles.CsAgent));

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task OnlyACsAgentCanActThroughInteractionOwnership(string role) =>
        Assert.False(await ActsAsync(await HandledTicketAsync(), role));

    [Fact]
    public async Task InteractionHandler_CanTransferTheTicket_ButAHandlerlessAgentCannot()
    {
        var h = await HandledTicketAsync();
        var tickets = new FakeTicketRepository();
        await tickets.AddAsync(h.Ticket);
        var departments = new FakeDepartmentRepository();
        var target = departments.AddDepartment("Collections", "COLL");
        var assignments = new FakeTicketAssignmentRepository();
        var audit = new FakeAuditEntryWriter();
        var settings = new FakeDepartmentWorkflowSettingsRepository();
        var service = new TicketAssignmentAppService(
            tickets, assignments, h.Members, departments, new FakeTicketingUnitOfWork(), audit, TimeProvider.System, settings,
            new TicketAutoAssignmentService(new FakeRequestTypeAssignmentRuleRepository(), settings, h.Members, assignments, audit),
            handlerRepository: h.Handlers);
        var request = new TransferTicketRequestDto(target.DepartmentId, "Needs Collections", []);

        var stranger = Guid.NewGuid();
        h.Members.Assignments.Add(new UserDepartmentAssignment(stranger, 5, true, DateTime.UtcNow, null));
        Assert.Equal(TicketMutationOutcome.Forbidden, (await service.TransferAsync(stranger, CsAgent, h.Ticket.TicketId, request)).Outcome);

        var result = await service.TransferAsync(h.Agent, CsAgent, h.Ticket.TicketId, request);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(target.DepartmentId, h.Ticket.CurrentDepartmentId);
        Assert.Contains(audit.Entries, e => e.Action == "Transfer");
    }
}
