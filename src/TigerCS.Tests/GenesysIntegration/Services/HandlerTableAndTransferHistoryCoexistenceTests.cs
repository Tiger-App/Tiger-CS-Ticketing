using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// <c>TicketInteractionHandlers</c> (who handled the interaction — the CS Agent visibility source of truth) and the ticket's
/// Genesys handling events (the ordered timeline: who answered, who passed the call to whom) are written from the same Genesys
/// updates and must coexist with ONE responsibility each: neither duplicates the other's rows, neither grants visibility through
/// the other, and neither touches ticket ownership or the escalation count.
/// </summary>
public class HandlerTableAndTransferHistoryCoexistenceTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly string[] AgentRole = [Roles.CsAgent];
    private const string AgentAId = "ga-a";
    private const string AgentBId = "ga-b";

    private static async Task<(GenesysServiceFixture F, long TicketId, Guid A, Guid B, int DepartmentId)> TransferredCallAsync()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var a = f.MapAgent(AgentAId, roles: Roles.CsAgent);
        var b = f.MapAgent(AgentBId, roles: Roles.CsAgent);
        var created = await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            "conv-co", GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId, AgentId: AgentAId));
        var ticketId = created.Ticket!.TicketId;

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(
            "conv-co", Routing: new GenesysRoutingUpdateDto(QueueId: "q", AgentId: AgentBId, AgentName: "Omar")));
        return (f, ticketId, a, b, department.DepartmentId);
    }

    private static TicketQueryAppService DetailQuery(GenesysServiceFixture f) => new(
        f.Tickets, f.DepartmentAssignments, f.Resolutions, f.StatusHistory, ReopenPolicy.Default, TimeProvider.System,
        interactionRepository: f.Interactions, workflowEventRepository: f.WorkflowEvents, handlerRepository: f.Tickets.Handlers);

    [Fact]
    public async Task ATransferredCall_WritesOneHandlerRowPerAgent_AndOneTimelineEventPerStep_NeitherDuplicatingTheOther()
    {
        var (f, _, a, b, _) = await TransferredCallAsync();

        // The visibility source of truth: exactly one row per (interaction, user).
        Assert.Equal([a, b], f.Handlers.All.Select(h => h.HandledByUserId).OrderBy(x => x == a ? 0 : 1));
        Assert.Equal(2, f.Handlers.All.Count);

        // The timeline: who answered, then who passed the call to whom.
        var connected = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysAgentConnected);
        var transferred = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.GenesysCallTransferred);
        Assert.Equal(a, connected.ToEmployeeId);
        Assert.Equal(a, transferred.ActorEmployeeId);
        Assert.Equal(b, transferred.ToEmployeeId);
    }

    [Fact]
    public async Task ARedeliveredRoutingEvent_AddsNothingToEitherStore()
    {
        var (f, ticketId, _, _, _) = await TransferredCallAsync();
        var handlers = f.Handlers.All.Count;
        var events = f.WorkflowEvents.All.Count;

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(
            "conv-co", Routing: new GenesysRoutingUpdateDto(QueueId: "q", AgentId: AgentBId, AgentName: "Omar")));

        Assert.Equal(handlers, f.Handlers.All.Count);
        Assert.Equal(events, f.WorkflowEvents.All.Count);
    }

    [Fact]
    public async Task HandlingAndTheTimelineGrantNothing_OnlyTheCurrentAssignmentDoes()
    {
        var (f, ticketId, a, b, _) = await TransferredCallAsync();

        // Both agents are in the handler table and the timeline, but neither handling nor the timeline grants a CS Agent visibility: the
        // ticket is unassigned, so nobody sees it.
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(a, AgentRole, ticketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(b, AgentRole, ticketId)).Outcome);

        // Once the ticket is assigned to the agent the call was transferred to, only that agent sees it - the first handler no longer does.
        f.DepartmentAssignments.RolesByEmployee[a] = [Roles.CsAgent];
        f.DepartmentAssignments.RolesByEmployee[b] = [Roles.CsAgent];
        f.Tickets.All.Single(t => t.TicketId == ticketId).AssignTo(b);
        Assert.Equal(TicketQueryOutcome.Success, (await f.StrictTicketQuery.GetDetailAsync(b, AgentRole, ticketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(a, AgentRole, ticketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(Guid.NewGuid(), AgentRole, ticketId)).Outcome);
    }

    [Fact]
    public async Task GenesysHandlingHistory_NeverChangesOwnershipOrTheTransferAndEscalationCounts()
    {
        var (f, ticketId, a, _, _) = await TransferredCallAsync();

        var detail = (await DetailQuery(f).GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticketId)).Response!;

        Assert.Null(detail.CurrentOwnerEmployeeId);                 // handling is not ownership
        Assert.Equal(a, detail.FirstHandledByEmployeeId);           // the first handler is unchanged by the transfer
        Assert.Equal(0, detail.TransferCount);                      // a call transfer is not a ticket transfer
        Assert.Equal(0, detail.EscalationCount);                    // ...and not an escalation
    }

    [Fact]
    public async Task AnExplicitEscalation_CountsOnce_AndAddsNoHandlerRowAndNoGenesysEvent()
    {
        var (f, ticketId, a, b, departmentId) = await TransferredCallAsync();
        var supervisor = Guid.NewGuid();
        var receiver = Guid.NewGuid();
        foreach (var id in new[] { supervisor, receiver })
        {
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, departmentId, true, DateTime.UtcNow, null));
        }

        var assignment = new TicketAssignmentAppService(
            f.Tickets, f.TicketAssignments, f.DepartmentAssignments, f.Departments, f.UnitOfWork, f.Audit, TimeProvider.System,
            f.DepartmentSettings,
            new TicketAutoAssignmentService(f.AssignmentRules, f.DepartmentSettings, f.DepartmentAssignments, f.TicketAssignments, f.Audit),
            f.WorkflowEvents);
        var ticket = f.Tickets.All.Single(t => t.TicketId == ticketId);
        ticket.AssignTo(a);   // the first agent owns it; a supervisor now passes it up
        var handlers = f.Handlers.All.Count;
        var genesysEvents = f.WorkflowEvents.All.Count(e => e.EventType is WorkflowEventType.GenesysAgentConnected or WorkflowEventType.GenesysCallTransferred);

        var result = await assignment.AssignAsync(
            supervisor, [Roles.CsSupervisor], ticketId, new AssignTicketRequestDto(receiver, [], TicketTransferReason.UnableToResolve));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var detail = (await DetailQuery(f).GetDetailAsync(Guid.NewGuid(), [Roles.CsManager], ticketId)).Response!;
        Assert.Equal(1, detail.TransferCount);
        Assert.Equal(1, detail.EscalationCount);
        Assert.Equal(receiver, detail.CurrentOwnerEmployeeId);
        Assert.Equal(a, detail.FirstHandledByEmployeeId);     // CurrentOwner and FirstHandledBy stay separate
        Assert.Equal(handlers, f.Handlers.All.Count);          // an escalation is not "handling the call"
        Assert.Equal(genesysEvents, f.WorkflowEvents.All.Count(e => e.EventType is WorkflowEventType.GenesysAgentConnected or WorkflowEventType.GenesysCallTransferred));
        _ = b;
    }
}
