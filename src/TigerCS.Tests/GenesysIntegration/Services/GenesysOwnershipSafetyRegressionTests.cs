using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Regression tests for two ownership guarantees of the Genesys transfer flow:
/// (1) the Original Human Agent is set once, on the first eligible AI-to-human answer, survives A to B to C and the return to the Call Center,
///     and is never invented for a legacy ticket;
/// (2) an agent-connected event reassigns only within the SAME live Genesys conversation - a new call never changes an existing ticket's owner,
///     a person's assignment stands, and duplicate or out-of-order events cannot overwrite a newer assignment.
/// </summary>
public class GenesysOwnershipSafetyRegressionTests
{
    private static readonly Guid AiAgent = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
        public List<TicketAssignmentHistoryEntry> History(long id) => [.. F.AssignmentHistory.Entries.Where(e => e.TicketId == id)];
        public int OriginalAgentAudits(long id) => F.Audit.Entries.Count(e => e.Action == "SetOriginalHumanAgent" && e.EntityId == id.ToString());
    }

    private static World NewWorld()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = Enumerable.Range(1, 3)
            .Select(i => (Id: f.AddAgent($"g-agent-{i}", $"Agent {i}", roles: Roles.CsAgent), GenesysId: $"g-agent-{i}"))
            .OrderBy(a => a.Id).ToArray();
        return new World { F = f, Cs = cs, Agents = agents };
    }

    private static async Task<(long TicketId, string ConversationId)> AiCallAsync(World w, string conversationId)
    {
        var r = await w.F.Ingestion.IngestAsync(
            AiAgent,
            new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId, StartedAtUtc: T0));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return (r.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, (long TicketId, string ConversationId) t, string genesysAgentId, DateTime at) =>
        w.F.TicketUpdate.UpdateAsync(
            AiAgent, t.TicketId,
            new GenesysTicketUpdateDto(t.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: at)));

    private static TicketAssignmentAppService AssignmentService(World w) =>
        new(w.F.Tickets, w.F.TicketAssignments, w.F.DepartmentAssignments, w.F.Departments, w.F.UnitOfWork, w.F.Audit, TimeProvider.System,
            w.F.DepartmentSettings, w.F.AutoAssignment, w.F.WorkflowEvents, requestTypeRepository: w.F.RequestTypes);

    // ---- 1. Original Human Agent --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstEligibleAiToHumanTransfer_SetsTheOriginalHumanAgent_OnANewWorkflowTicket()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-oha-1");
        Assert.True(w.Ticket(call.TicketId).OriginalAgentWorkflowEnabled);
        Assert.Null(w.Ticket(call.TicketId).OriginalHumanAgentId);                          // the AI Agent is never the Original Human Agent

        await ConnectAsync(w, call, w.Agents[0].GenesysId, T0.AddMinutes(1));

        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).OriginalHumanAgentId);
        Assert.Equal(1, w.OriginalAgentAudits(call.TicketId));
    }

    [Fact]
    public async Task AnUnmappedFirstAgent_NeverBecomesTheOriginalHumanAgent_AndTheOriginalIsNotRewrittenLater()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-oha-unmapped");

        // An id that maps to nobody is flagged and the ticket is dealt to an eligible CS Agent by the shared rotation (existing rule);
        // that eligible agent - never the unmapped id - is the first Original Human Agent.
        await ConnectAsync(w, call, "g-not-in-tigercs", T0.AddMinutes(1));
        var original = w.Ticket(call.TicketId).OriginalHumanAgentId;
        Assert.Contains(original, w.Agents.Select(a => (Guid?)a.Id));

        var next = w.Agents.First(a => a.Id != original);
        await ConnectAsync(w, call, next.GenesysId, T0.AddMinutes(2));

        Assert.Equal(next.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(original, w.Ticket(call.TicketId).OriginalHumanAgentId);               // write-once
    }

    [Fact]
    public async Task AToBToC_PreservesTheOriginalHumanAgent_WhileTheOwnerFollowsTheAnsweringAgent()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-oha-chain");
        var (a, b, c) = (w.Agents[0], w.Agents[1], w.Agents[2]);

        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));
        await ConnectAsync(w, call, b.GenesysId, T0.AddMinutes(2));
        await ConnectAsync(w, call, c.GenesysId, T0.AddMinutes(3));

        Assert.Equal(c.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(a.Id, w.Ticket(call.TicketId).OriginalHumanAgentId);                   // write-once: still A
        Assert.Equal(1, w.OriginalAgentAudits(call.TicketId));
    }

    [Fact]
    public async Task ReturningToTheCallCenter_GoesBackToTheOriginalHumanAgent_NotTheLatestOwner()
    {
        var w = NewWorld();
        var facilities = w.F.Departments.AddDepartment("Facilities Management", "FM");
        var call = await AiCallAsync(w, "conv-oha-return");
        var (a, b) = (w.Agents[0], w.Agents[1]);
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));
        await ConnectAsync(w, call, b.GenesysId, T0.AddMinutes(2));                         // B owns it now; A is the Original Human Agent

        var service = AssignmentService(w);
        var out1 = await service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], call.TicketId,
            new TransferTicketRequestDto(facilities.DepartmentId, "Facilities issue", w.Ticket(call.TicketId).RowVersion));
        Assert.Equal(TicketMutationOutcome.Success, out1.Outcome);

        var back = await service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], call.TicketId,
            new TransferTicketRequestDto(w.F.CallCenter!.DepartmentId, "Back to the Call Center", w.Ticket(call.TicketId).RowVersion));
        Assert.Equal(TicketMutationOutcome.Success, back.Outcome);

        Assert.Equal(a.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                 // the established return-to-original behaviour
        Assert.Equal(a.Id, w.Ticket(call.TicketId).OriginalHumanAgentId);
    }

    [Fact]
    public async Task LegacyTicket_IsNeverBackfilled_ByGenesysTransfers()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-oha-legacy");
        // Model a ticket that existed before the deployment cutoff: it does not follow the Original Human Agent workflow.
        typeof(Ticket).GetProperty(nameof(Ticket.OriginalAgentWorkflowEnabled))!.SetValue(w.Ticket(call.TicketId), false);

        await ConnectAsync(w, call, w.Agents[0].GenesysId, T0.AddMinutes(1));
        await ConnectAsync(w, call, w.Agents[1].GenesysId, T0.AddMinutes(2));

        Assert.Null(w.Ticket(call.TicketId).OriginalHumanAgentId);
        Assert.Null(w.Ticket(call.TicketId).ActiveOriginalHumanAgentId);
        Assert.Equal(0, w.OriginalAgentAudits(call.TicketId));
        Assert.Equal(w.Agents[1].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);       // ownership itself still follows the answering agent
    }

    // ---- 2. Same active conversation only -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewCustomerCall_CannotChangeTheOwnerOfAnExistingTicket()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-first-call");
        var (a, b) = (w.Agents[0], w.Agents[1]);
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));
        var historyBefore = w.History(call.TicketId).Count;

        // The same customer calls again: a NEW conversation id naming the existing ticket is refused outright...
        var viaApi = await ConnectAsync(w, (call.TicketId, "conv-second-call"), b.GenesysId, T0.AddHours(5));
        Assert.NotEqual(GenesysTicketUpdateOutcome.Applied, viaApi.Outcome);

        // ...and even if a connected event of another conversation reached the assignment rule directly, it changes nothing.
        var ticket = w.Ticket(call.TicketId);
        var otherCall = TicketInteraction.CreateFromGenesys(
            ticket.TicketId, WellKnownChannels.Phone, "+971500000001", "conv-second-call", null, null, null, null, null, T0.AddHours(5), null, DateTime.UtcNow);
        var direct = await w.F.AnsweringAgents.ApplyConnectedAgentAsync(
            AiAgent, ticket, otherCall, b.GenesysId, "Routing", connectedAtUtc: T0.AddHours(5), allowOwnerTransfer: true);

        Assert.Equal(AnswerAssignmentOutcome.NotApplicable, direct.Outcome);
        Assert.Equal(a.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(historyBefore, w.History(call.TicketId).Count);
    }

    [Fact]
    public async Task ANewCall_CannotGiveAnAiHandledTicketAnOwnerEither()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ai-first");
        var ticket = w.Ticket(call.TicketId);
        var otherCall = TicketInteraction.CreateFromGenesys(
            ticket.TicketId, WellKnownChannels.Phone, "+971500000001", "conv-other", null, null, null, null, null, T0.AddHours(2), null, DateTime.UtcNow);

        var result = await w.F.AnsweringAgents.ApplyConnectedAgentAsync(
            AiAgent, ticket, otherCall, w.Agents[0].GenesysId, "Routing", connectedAtUtc: T0.AddHours(2), allowOwnerTransfer: true);

        Assert.Equal(AnswerAssignmentOutcome.NotApplicable, result.Outcome);
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AConnectAfterTheConversationEnded_DoesNotReassign()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ended");
        await ConnectAsync(w, call, w.Agents[0].GenesysId, T0.AddMinutes(1));
        await w.F.TicketUpdate.UpdateAsync(
            AiAgent, call.TicketId, new GenesysTicketUpdateDto(call.ConversationId, Ended: new GenesysConversationEndUpdateDto(T0.AddMinutes(10), "AgentDisconnect")));

        await ConnectAsync(w, call, w.Agents[1].GenesysId, T0.AddMinutes(11));

        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    // ---- Manual and supervisor overrides ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASupervisorReassigningToTheSamePersonGenesysChose_StillBlocksALaterTransfer()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-supervisor-same");
        var (a, b) = (w.Agents[0], w.Agents[1]);
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));

        // A supervisor "confirms" A: the assignment now carries the supervisor as actor, so it is no longer Genesys-set.
        // (Re-assigning the current owner is refused by the service, so move to B and back to A as a person.)
        var service = AssignmentService(w);
        var toB = await service.AssignAsync(Guid.NewGuid(), [Roles.CsManager], call.TicketId,
            new AssignTicketRequestDto(b.Id, w.Ticket(call.TicketId).RowVersion, ReasonCode: TicketTransferReason.UnableToResolve, Reason: "Supervisor"));
        Assert.Equal(TicketMutationOutcome.Success, toB.Outcome);
        var toA = await service.AssignAsync(Guid.NewGuid(), [Roles.CsManager], call.TicketId,
            new AssignTicketRequestDto(a.Id, w.Ticket(call.TicketId).RowVersion, ReasonCode: TicketTransferReason.UnableToResolve, Reason: "Supervisor"));
        Assert.Equal(TicketMutationOutcome.Success, toA.Outcome);

        await ConnectAsync(w, call, w.Agents[2].GenesysId, DateTime.UtcNow.AddMinutes(30));

        Assert.Equal(a.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.History(call.TicketId), h => h.NewEmployeeId == w.Agents[2].Id);
    }

    [Fact]
    public async Task ASupervisorOverrideOfAnAiTicketBeforeAnyAnswer_IsNotReplacedByGenesys()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-supervisor-ai");
        var (chosen, other) = (w.Agents[2], w.Agents[0]);
        w.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(chosen.Id, w.Cs.DepartmentId, false, DateTime.UtcNow, null));
        var assigned = await AssignmentService(w).AssignAsync(Guid.NewGuid(), [Roles.CsManager], call.TicketId,
            new AssignTicketRequestDto(chosen.Id, w.Ticket(call.TicketId).RowVersion, ReasonCode: TicketTransferReason.UnableToResolve, Reason: "Supervisor override"));
        Assert.Equal(TicketMutationOutcome.Success, assigned.Outcome);

        await ConnectAsync(w, call, other.GenesysId, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(chosen.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    // ---- Duplicate and out-of-order events ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LateOlderEventAfterATransferChain_AndDuplicates_NeverOverwriteTheNewestAssignment()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "conv-ooo-chain");
        var (a, b, c) = (w.Agents[0], w.Agents[1], w.Agents[2]);

        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));
        await ConnectAsync(w, call, c.GenesysId, T0.AddMinutes(5));
        await ConnectAsync(w, call, b.GenesysId, T0.AddMinutes(3));                          // older than C's: arrives late
        await ConnectAsync(w, call, c.GenesysId, T0.AddMinutes(5));                          // duplicate of C's
        await ConnectAsync(w, call, a.GenesysId, T0.AddMinutes(1));                          // duplicate of A's, long superseded

        Assert.Equal(c.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(
            new (Guid?, Guid)[] { (null, AiAgent), (AiAgent, a.Id), (a.Id, c.Id) },
            w.History(call.TicketId).Select(h => (h.PreviousEmployeeId, h.NewEmployeeId)));
        Assert.Single(w.F.TicketAssignments.Added, x => x.TicketId == call.TicketId && x.IsCurrent);
        Assert.Equal(a.Id, w.Ticket(call.TicketId).OriginalHumanAgentId);
    }
}
