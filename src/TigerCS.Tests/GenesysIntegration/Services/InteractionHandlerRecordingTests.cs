using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Which Genesys events make an agent a recorded <b>handler</b> of an interaction (which by itself grants no ticket visibility). The integration used to
/// keep one handler per interaction (the first, never overwritten), so a transferred call only ever named the first agent. It now records
/// every agent Genesys reports as connected, and nothing that is merely an assignment, a ring or a screen pop.
/// </summary>
public class InteractionHandlerRecordingTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly string[] Agent = [Roles.CsAgent];
    private const string AgentAId = "genesys-agent-a";
    private const string AgentBId = "genesys-agent-b";

    private static async Task<(GenesysServiceFixture F, long TicketId, int DepartmentId)> ConversationAsync(
        string conversationId = "conv-h", string? agentId = null)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001",
                DepartmentId: department.DepartmentId, AgentId: agentId));
        return (f, created.Ticket!.TicketId, department.DepartmentId);
    }

    private static Task<GenesysTicketUpdateResult> RoutingAsync(
        GenesysServiceFixture f, long ticketId, string conversationId, string agentId, DateTime? connectedAtUtc = null) =>
        f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                conversationId, Routing: new GenesysRoutingUpdateDto(QueueId: "q-1", AgentId: agentId, AgentName: "Agent", ConnectedAtUtc: connectedAtUtc)));

    // =====================================================================
    // What is recorded
    // =====================================================================

    [Fact]
    public async Task AnInquiryNamingAMappedAgent_RecordsThemAsAHandler()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agent = f.MapAgent(AgentAId, roles: Roles.CsAgent);

        var created = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                "conv-in", GenesysChannel.Phone, CustomerPhone: "+971500000001",
                DepartmentId: department.DepartmentId, AgentId: AgentAId));

        var handler = Assert.Single(f.Handlers.All);
        Assert.Equal(agent, handler.HandledByUserId);
        Assert.Equal(AgentAId, handler.GenesysAgentUserId);
        Assert.Equal(created.Ticket!.TicketId, handler.TicketId);
        Assert.Equal(InteractionHandlerSource.Inquiry, handler.Source);
        Assert.Equal(f.InteractionFor("conv-in")!.TicketInteractionId, handler.TicketInteractionId);
    }

    [Fact]
    public async Task AnInquiryNamingNoAgent_RecordsNoHandler_AndSoGrantsNobodyTheTicket()
    {
        var (f, ticketId, _) = await ConversationAsync();

        Assert.Empty(f.Handlers.All);
        var anyAgent = Guid.NewGuid();
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(anyAgent, Agent, ticketId)).Outcome);
    }

    [Fact]
    public async Task ARoutingUpdateNamingTheConnectedAgent_RecordsThem_WithTheConnectedTime()
    {
        var (f, ticketId, _) = await ConversationAsync();
        var agent = f.MapAgent(AgentAId, roles: Roles.CsAgent);
        var connectedAt = new DateTime(2026, 10, 30, 8, 15, 0, DateTimeKind.Utc);

        var result = await RoutingAsync(f, ticketId, "conv-h", AgentAId, connectedAt);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        var handler = Assert.Single(f.Handlers.All);
        Assert.Equal(agent, handler.HandledByUserId);
        Assert.Equal(InteractionHandlerSource.Routing, handler.Source);
        Assert.Equal(connectedAt, handler.ConnectedAtUtc);

        // Being recorded as the handler grants nothing by itself; the ticket is the agent's once it is assigned to them.
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, ticketId)).Outcome);
        f.Tickets.All.Single(t => t.TicketId == ticketId).AssignTo(agent);
        Assert.Equal(TicketQueryOutcome.Success, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, ticketId)).Outcome);
    }

    [Fact]
    public async Task AnAgentNamedOnAPlainUpdateOrOnTheConversationEnd_IsRecordedToo()
    {
        var (f, ticketId, _) = await ConversationAsync();
        var onUpdate = f.MapAgent(AgentAId, roles: Roles.CsAgent);
        var onEnd = f.MapAgent(AgentBId, roles: Roles.CsAgent);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-h", AgentId: AgentAId));
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                "conv-h", AgentId: AgentBId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "AgentDisconnect")));

        Assert.Equal(
            [InteractionHandlerSource.Update, InteractionHandlerSource.ConversationEnd],
            f.Handlers.All.Select(h => h.Source).ToArray());
        Assert.Equal([onUpdate, onEnd], f.Handlers.All.Select(h => h.HandledByUserId).ToArray());
    }

    // =====================================================================
    // Transfers
    // =====================================================================

    [Fact]
    public async Task ATransfer_RecordsBothAgents_WhileTheFirstStaysTheInteractionsLegacySingleHandler()
    {
        var (f, ticketId, _) = await ConversationAsync();
        var first = f.MapAgent(AgentAId, roles: Roles.CsAgent);
        var second = f.MapAgent(AgentBId, roles: Roles.CsAgent);

        await RoutingAsync(f, ticketId, "conv-h", AgentAId);
        await RoutingAsync(f, ticketId, "conv-h", AgentBId); // transferred to B

        Assert.Equal([first, second], f.Handlers.All.Select(h => h.HandledByUserId).ToArray());
        var interaction = f.InteractionFor("conv-h")!;
        Assert.Equal(first, interaction.HandledByUserId);   // unchanged: "who took it first"
        Assert.Equal(AgentBId, interaction.GenesysAgentId); // the current routing

        // Handling grants nothing: until the ticket is assigned, neither the agent who transferred away nor the one who received it can open it.
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(first, Agent, ticketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(second, Agent, ticketId)).Outcome);

        // Once it is assigned to the agent the call went to, only that agent can open it - not the one who handled it first, nor a third.
        f.DepartmentAssignments.RolesByEmployee[first] = [Roles.CsAgent];
        f.DepartmentAssignments.RolesByEmployee[second] = [Roles.CsAgent];
        f.Tickets.All.Single(t => t.TicketId == ticketId).AssignTo(second);
        Assert.Equal(TicketQueryOutcome.Success, (await f.StrictTicketQuery.GetDetailAsync(second, Agent, ticketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(first, Agent, ticketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(Guid.NewGuid(), Agent, ticketId)).Outcome);
    }

    [Fact]
    public async Task ABackAndForthTransfer_RecordsEachAgentOnce()
    {
        var (f, ticketId, _) = await ConversationAsync();
        f.MapAgent(AgentAId, roles: Roles.CsAgent);
        f.MapAgent(AgentBId, roles: Roles.CsAgent);

        await RoutingAsync(f, ticketId, "conv-h", AgentAId);
        await RoutingAsync(f, ticketId, "conv-h", AgentBId);
        await RoutingAsync(f, ticketId, "conv-h", AgentAId);

        Assert.Equal(2, f.Handlers.All.Count);
    }

    [Fact]
    public async Task ARedeliveredEvent_RecordsNothingTwice()
    {
        var (f, ticketId, _) = await ConversationAsync();
        f.MapAgent(AgentAId, roles: Roles.CsAgent);

        await RoutingAsync(f, ticketId, "conv-h", AgentAId);
        await RoutingAsync(f, ticketId, "conv-h", AgentAId);
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-h", AgentId: AgentAId));

        Assert.Single(f.Handlers.All);
        Assert.Single(f.Audit.Entries, e => e.Action == GenesysAuditActionsFor.HandlerAdded);
    }

    // =====================================================================
    // What is NOT proof of handling
    // =====================================================================

    [Fact]
    public async Task AnAgentIdThatIsNotMappedToAUser_ABotOrAnUnknownAgent_IsNeverRecorded()
    {
        var (f, ticketId, _) = await ConversationAsync();

        await RoutingAsync(f, ticketId, "conv-h", "the-virtual-agent-id");
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-h", AgentId: "another-unknown-id"));

        Assert.Empty(f.Handlers.All);
    }

    [Fact]
    public async Task ADeactivatedAgent_IsNeverRecorded()
    {
        var (f, ticketId, _) = await ConversationAsync();
        f.MapAgent(AgentAId, isActive: false, roles: Roles.CsAgent);

        await RoutingAsync(f, ticketId, "conv-h", AgentAId);

        Assert.Empty(f.Handlers.All);
    }

    [Fact]
    public async Task AQueueMovementWithNoAgent_ARingingStateNeverSent_RecordsNobody()
    {
        var (f, ticketId, _) = await ConversationAsync();
        f.MapAgent(AgentAId, roles: Roles.CsAgent);

        // The conversation entered a queue; an agent may be about to be offered it. TigerCS is told nothing about the agent until they connect.
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-h", Routing: new GenesysRoutingUpdateDto(QueueId: "q-1", QueueName: "Customer Service")));

        Assert.Empty(f.Handlers.All);
    }

    [Fact]
    public async Task AHandoffAssignment_AnAgentOfferedTheWork_IsNotProofTheyHandledTheInteraction()
    {
        var (f, ticketId, _) = await ConversationAsync();
        f.MapAgent(AgentAId, roles: Roles.CsAgent);
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-h", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-h", Handoff: new GenesysHandoffUpdateDto(AssignedAgentId: AgentAId)));

        Assert.Empty(f.Handlers.All);
    }

    [Fact]
    public async Task OpeningTheScreenPopFromGenesys_DoesNotRecordAHandler_BecauseItMayFireWhileTheCallIsStillRinging()
    {
        var (f, _, _) = await ConversationAsync();
        var agent = f.MapAgent(AgentAId, roles: Roles.CsAgent);

        var result = await f.AgentContext.ResolveAsync(
            ServiceAccount, new GenesysAgentContextDto(AgentAId, ConversationId: "conv-h"));

        Assert.Equal(GenesysAgentContextOutcome.Resolved, result.Outcome);
        Assert.Equal(agent, result.HandledByUserId);          // the legacy single-handler record is unchanged
        Assert.Empty(f.Handlers.All);                         // ...but a screen pop is not proof anyone answered
    }

    // =====================================================================
    // Several tickets from one conversation
    // =====================================================================

    [Fact]
    public async Task EachTicketOfAConversation_HasItsOwnHandlers()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agent = f.MapAgent(AgentAId, roles: Roles.CsAgent);
        var first = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-multi", GenesysChannel.Phone, CustomerPhone: "+971500000001",
                DepartmentId: department.DepartmentId, ServiceRequestId: "documents"));
        var second = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-multi", GenesysChannel.Phone, CustomerPhone: "+971500000001",
                DepartmentId: department.DepartmentId, ServiceRequestId: "leasing"));

        // The agent connects and the update is addressed to ONE of the two tickets.
        await RoutingAsync(f, second.Ticket!.TicketId, "conv-multi", AgentAId);

        var handler = Assert.Single(f.Handlers.All);
        Assert.Equal(second.Ticket.TicketId, handler.TicketId);

        // Handling grants no visibility on either ticket; the agent sees the one that is assigned to them, and still not its sibling.
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, second.Ticket.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, first.Ticket!.TicketId)).Outcome);
        f.Tickets.All.Single(t => t.TicketId == second.Ticket.TicketId).AssignTo(agent);
        Assert.Equal(TicketQueryOutcome.Success, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, second.Ticket.TicketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, first.Ticket.TicketId)).Outcome);
    }

    /// <summary>The audit vocabulary, spelled once for the assertions above.</summary>
    private static class GenesysAuditActionsFor
    {
        public const string HandlerAdded = "GenesysInteractionHandlerAdded";
    }
}
