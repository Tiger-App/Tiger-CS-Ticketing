using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The Genesys ticket ownership and assignment flow:
/// (1) the AI Agent answers first and finishes - the AI Agent is the initial assignee IN THE HISTORY (the ticket keeps no owner);
/// (2) the AI Agent transfers and a human agent answers - the SAME ticket moves to that agent, history AI Agent -> human, source "Genesys call transfer";
/// (3) a human agent answers directly - the ticket is created owned by that agent, history records the initial assignment;
/// plus multiple transfers, duplicate and out-of-order events, person-owned tickets, and the rollback switch.
/// </summary>
public class GenesysAssignmentFlowTests
{
    /// <summary>The Genesys integration account = the AI Agent. It creates every Genesys ticket and is the AI's identity in the assignment history.</summary>
    private static readonly Guid AiAgent = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
        public List<TicketAssignmentHistoryEntry> History(long id) => [.. F.AssignmentHistory.Entries.Where(e => e.TicketId == id)];
    }

    private static World NewWorld(int agents = 3)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var list = Enumerable.Range(1, agents)
            .Select(i => (Id: f.AddAgent($"g-agent-{i}", $"Agent {i}"), GenesysId: $"g-agent-{i}"))
            .OrderBy(a => a.Id).ToArray();
        return new World { F = f, Cs = cs, Agents = list };
    }

    private static async Task<(long TicketId, string ConversationId)> AiCallAsync(World w, string conversationId)
    {
        var r = await w.F.Ingestion.IngestAsync(
            AiAgent,
            new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId, StartedAtUtc: T0));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return (r.Ticket!.TicketId, conversationId);
    }

    private static async Task<(long TicketId, string ConversationId)> DirectCallAsync(World w, string conversationId, string genesysAgentId)
    {
        var r = await w.F.Ingestion.IngestAsync(
            AiAgent,
            new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId, AgentId: genesysAgentId, StartedAtUtc: T0));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return (r.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, (long TicketId, string ConversationId) t, string genesysAgentId, DateTime at) =>
        w.F.TicketUpdate.UpdateAsync(
            AiAgent, t.TicketId,
            new GenesysTicketUpdateDto(t.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: at)));

    // ---- Scenario 1: the AI Agent answers first and finishes -----------------------------------------------------------------------------

    [Fact]
    public async Task AiAgentHandlesTheCall_AiIsTheInitialAssigneeInTheHistory_AndTheTicketKeepsNoOwner()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ai-only");

        var entry = Assert.Single(w.History(call.TicketId));
        Assert.Equal(TicketAssignmentSource.GenesysAiAgent, entry.Source);
        Assert.Equal(AiAgent, entry.NewEmployeeId);
        Assert.Null(entry.PreviousEmployeeId);
        Assert.Equal("conv-ai-only", entry.GenesysConversationId);
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                       // ownerless = AI-handled; round robin, auto-close and dashboards rely on it
        Assert.Equal(AiAgent, w.Ticket(call.TicketId).CreatedByEmployeeId);                // the creator is the integration account, unchanged
    }

    [Fact]
    public async Task AiAgentFinishesWithoutAHuman_StaysAiAssigned_AndRedeliveryDoesNotDuplicateTheHistory()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ai-ends");

        // The AI resolves the customer's request itself and the call ends: no human was involved, so the AI Agent stays the only assignee.
        await w.F.TicketUpdate.UpdateAsync(
            AiAgent, call.TicketId,
            new GenesysTicketUpdateDto(call.ConversationId, ClosedByAi: true, Ended: new GenesysConversationEndUpdateDto(T0.AddMinutes(3), "CustomerDisconnect")));
        var again = await w.F.Ingestion.IngestAsync(
            AiAgent, new GenesysInquiryDto("conv-ai-ends", GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, again.Outcome);
        Assert.Equal(TicketAssignmentSource.GenesysAiAgent, Assert.Single(w.History(call.TicketId)).Source);
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    // ---- Scenario 2: the AI Agent transfers to a human who answers ------------------------------------------------------------------------

    [Fact]
    public async Task AiTransfersToAHuman_TheSameTicketMovesToTheAgentWhoAnswered_AndTheTransferIsRecorded()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ai-to-human");
        var ticketsBefore = w.F.Tickets.All.Count;
        var a = w.Agents[0];

        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));

        Assert.Equal(a.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(ticketsBefore, w.F.Tickets.All.Count);                                // no second ticket for the conversation
        Assert.Equal(AiAgent, w.Ticket(call.TicketId).CreatedByEmployeeId);                // creator unchanged
        var history = w.History(call.TicketId);
        Assert.Equal(2, history.Count);
        var transfer = history[1];
        Assert.Equal(TicketAssignmentSource.GenesysCallTransfer, transfer.Source);
        Assert.Equal(AiAgent, transfer.PreviousEmployeeId);                                // previous assignee: the AI Agent
        Assert.Equal(a.Id, transfer.NewEmployeeId);                                        // new assignee: the human who answered
        Assert.Equal(T0.AddMinutes(1), transfer.SourceEventAtUtc);
        Assert.NotEqual(default, transfer.ChangedAtUtc);                                   // assignment date/time
        Assert.Contains("Genesys call transfer", transfer.Reason);
    }

    [Fact]
    public async Task RingingOnly_NoHumanAnswered_DoesNotReassign()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ringing");

        // The next agent is only assigned/ringing (handoff assignee): not connected, so nothing is reassigned.
        await w.F.TicketUpdate.UpdateAsync(
            AiAgent, call.TicketId,
            new GenesysTicketUpdateDto(call.ConversationId, Handoff: new GenesysHandoffUpdateDto(AgentAvailable: true, AssignedAgentId: w.Agents[0].GenesysId)));

        Assert.NotEqual(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.History(call.TicketId), h => h.NewEmployeeId == w.Agents[0].Id);
    }

    // ---- Scenario 3: a human answers directly ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task HumanAnswersDirectly_TicketIsCreatedOwnedByThem_AndTheInitialAssignmentIsInTheHistory()
    {
        var w = NewWorld();
        var a = w.Agents[1];

        var call = await DirectCallAsync(w, "conv-direct", a.GenesysId);

        Assert.Equal(a.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        var entry = Assert.Single(w.History(call.TicketId));                               // no AI entry: the AI never handled it
        Assert.Equal(TicketAssignmentSource.GenesysDirectCall, entry.Source);
        Assert.Null(entry.PreviousEmployeeId);
        Assert.Equal(a.Id, entry.NewEmployeeId);
        Assert.Equal(AiAgent, w.Ticket(call.TicketId).CreatedByEmployeeId);
    }

    // ---- Multiple transfers, duplicates, ordering -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AiToAToB_EachAgentWhoActuallyAnswersOwnsIt_AndTheWholeHistoryIsPreserved()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-multi");
        var (a, b, c) = (w.Agents[0], w.Agents[1], w.Agents[2]);

        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));
        await ConnectAsync(w, call, b.GenesysId, T0.AddMinutes(2));
        await ConnectAsync(w, call, c.GenesysId, T0.AddMinutes(3));

        Assert.Equal(c.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        var current = Assert.Single(w.F.TicketAssignments.Added, x => x.TicketId == call.TicketId && x.IsCurrent);
        Assert.Equal(c.Id, current.AssignedEmployeeId);                                    // exactly one current assignment, the current assignee
        Assert.Equal(
            new (Guid?, Guid)[] { (null, AiAgent), (AiAgent, a.Id), (a.Id, b.Id), (b.Id, c.Id) },
            w.History(call.TicketId).Select(h => (h.PreviousEmployeeId, h.NewEmployeeId)));
        Assert.Equal(2, w.F.TicketAssignments.Added.Count(x => x.TicketId == call.TicketId && !x.IsCurrent));   // A and B superseded, never deleted
    }

    [Fact]
    public async Task DuplicateConnectEvent_WritesNothingTwice()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-dup");
        var a = w.Agents[0];

        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));

        Assert.Equal(2, w.History(call.TicketId).Count);
        Assert.Single(w.F.TicketAssignments.Added, x => x.TicketId == call.TicketId && x.AssignedEmployeeId == a.Id);
    }

    [Fact]
    public async Task OutOfOrderEvents_AnOlderConnectNeverOverwritesANewerAssignment()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ooo");
        var (a, b) = (w.Agents[0], w.Agents[1]);

        await ConnectAsync(w, call, b.GenesysId, T0.AddMinutes(5));                        // the later connection arrives first
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));                        // the older one arrives afterwards

        Assert.Equal(b.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.History(call.TicketId), h => h.NewEmployeeId == a.Id);
        Assert.Contains(w.F.Handlers.All, h => h.TicketId == call.TicketId && h.HandledByUserId == a.Id);   // but A is still recorded as a handler
    }

    // ---- Person-owned tickets, ineligible agents, rollback ---------------------------------------------------------------------------------

    [Fact]
    public async Task APersonsAssignmentAfterTheAnswer_IsNotMovedByALaterGenesysConnect()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-person");
        var (a, b, c) = (w.Agents[0], w.Agents[1], w.Agents[2]);
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));

        var ticket = w.Ticket(call.TicketId);
        var service = new TicketAssignmentAppService(
            w.F.Tickets, w.F.TicketAssignments, w.F.DepartmentAssignments, w.F.Departments, w.F.UnitOfWork, w.F.Audit, TimeProvider.System,
            w.F.DepartmentSettings, w.F.AutoAssignment, w.F.WorkflowEvents, requestTypeRepository: w.F.RequestTypes);
        var assigned = await service.AssignAsync(
            Guid.NewGuid(), [Roles.CsManager], call.TicketId,
            new AssignTicketRequestDto(c.Id, ticket.RowVersion, ReasonCode: TicketTransferReason.UnableToResolve, Reason: "Supervisor decision"));
        Assert.Equal(TicketMutationOutcome.Success, assigned.Outcome);

        await ConnectAsync(w, call, b.GenesysId, DateTime.UtcNow.AddMinutes(10));

        Assert.Equal(c.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.History(call.TicketId), h => h.NewEmployeeId == b.Id);
    }

    [Fact]
    public async Task AnUnmappedNextAgent_NeverDisplacesTheCurrentOwner()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-unmapped");
        var a = w.Agents[0];
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));

        await ConnectAsync(w, call, "g-not-in-tigercs", T0.AddMinutes(2));

        Assert.Equal(a.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(2, w.History(call.TicketId).Count);
    }

    [Fact]
    public async Task ReassignOnTransferOff_RestoresTheEarlierBehaviour_TheSecondAgentIsOnlyAHandler()
    {
        var w = NewWorld();
        w.F.Options.ReassignOnTransfer = false;
        var call = await AiCallAsync(w, "conv-switch-off");
        var (a, b) = (w.Agents[0], w.Agents[1]);

        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));
        await ConnectAsync(w, call, b.GenesysId, T0.AddMinutes(2));

        Assert.Equal(a.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Contains(w.F.Handlers.All, h => h.TicketId == call.TicketId && h.HandledByUserId == b.Id);
    }
}
