using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The approved VOICE business rules (PR #292, second pass), through the real ingestion / update / handoff services:
/// AI answers -> the AI Agent owns it; AI resolves -> closed under the AI Agent; a human answers directly -> that employee; AI transfers -> the first human who actually
/// answered; a later agent never replaces the original; call termination alone never closes, flags or assigns; Round Robin never runs at handoff time; first response is the
/// real connect time. The same scenarios on a digital channel keep their previous behaviour (see <see cref="DigitalChannelsKeepTheirPreviousBehaviourTests"/>).
/// </summary>
public class VoiceOwnershipRulesTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
        public List<TicketAssignment> Current(long id) => F.TicketAssignments.Added.Where(a => a.TicketId == id && a.IsCurrent).ToList();
    }

    private sealed record Call(long TicketId, string ConversationId);

    private static World NewWorld(int agents = 3)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var list = Enumerable.Range(1, agents)
            .Select(i => (Id: f.AddAgent($"g-agent-{i}", $"Agent {i}"), GenesysId: $"g-agent-{i}"))
            .OrderBy(a => a.Id).ToArray();
        return new World { F = f, Cs = cs, Agents = list };
    }

    /// <summary>A phone call the AI is handling (no agent named).</summary>
    private static async Task<Call> AiCallAsync(World w, string conversationId, bool humanFollowUp = false)
    {
        var r = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId,
                HumanFollowUpRequired: humanFollowUp ? true : null));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return new Call(r.Ticket!.TicketId, conversationId);
    }

    /// <summary>A phone call picked up by a human (the inquiry names the agent who answered).</summary>
    private static async Task<Call> DirectCallAsync(World w, string conversationId, string answeringGenesysId, DateTime? connectedAtUtc = null)
    {
        var r = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId,
                AgentId: answeringGenesysId, ConnectedAtUtc: connectedAtUtc));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return new Call(r.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, Call c, string genesysAgentId, DateTime? connectedAtUtc = null) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, c.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: connectedAtUtc ?? DateTime.UtcNow.AddSeconds(-5))));

    private static Task<GenesysTicketUpdateResult> EndAsync(World w, Call c, string reason = "CustomerDisconnect") =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, c.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow.AddSeconds(-1), reason)));

    private static Task<GenesysTicketUpdateResult> CloseByAiAsync(World w, Call c) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, c.TicketId, new GenesysTicketUpdateDto(c.ConversationId, ClosedByAi: true));

    private static void AssertAiOwnedAndUnassigned(World w, long id)
    {
        var t = w.Ticket(id);
        Assert.Null(t.CurrentOwnerEmployeeId);
        Assert.NotNull(t.AiAgentOwnerEmployeeId);
        Assert.Empty(w.Current(id));
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));   // no Round Robin turn was taken
    }

    // ---- 1. AI answers: the AI Agent owns the ticket ------------------------------------------------------------------------------

    [Fact]
    public async Task AiAnswersTheCall_TheAiAgentOwnsTheTicket_NobodyIsAssigned()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-ai");

        AssertAiOwnedAndUnassigned(w, call.TicketId);
        var first = w.F.AssignmentHistory.Entries.Where(h => h.TicketId == call.TicketId).OrderBy(h => h.ChangedAtUtc).First();
        Assert.Equal(TicketAssignmentSource.GenesysAiAgent, first.Source);
    }

    // ---- 2. AI resolves: closed under the AI Agent, ownership kept, attribution correct ---------------------------------------------

    [Fact]
    public async Task AiResolves_ClosedByAi_IsClosedUnderTheAiAgent_AndTheOwnershipAndAttributionAreTheAiAgents()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-resolve");
        var aiAgent = w.Ticket(call.TicketId).AiAgentOwnerEmployeeId;

        var result = await CloseByAiAsync(w, call);

        Assert.True(result.ClosedByAi);
        var t = w.Ticket(call.TicketId);
        Assert.Equal(TicketStatus.Closed, t.TicketStatus);
        Assert.Null(t.CurrentOwnerEmployeeId);
        Assert.Equal(aiAgent, t.AiAgentOwnerEmployeeId);                                     // the closed ticket is still the AI Agent's
        var resolution = Assert.Single(w.F.Resolutions.Added, r => r.TicketId == call.TicketId);
        Assert.Equal(aiAgent, resolution.ResolvingEmployeeId);                              // ClosedBy = the AI Agent, never a person
        Assert.All(w.F.StatusHistory.Added.Where(h => h.TicketId == call.TicketId && h.NewValue == (byte)TicketStatus.Closed), h => Assert.True(h.ActorIsSystem));
    }

    [Fact]
    public async Task EndedArrivesBeforeClosedByAi_TheLaterAiClosureStillCloses()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-ended-first");

        var ended = await EndAsync(w, call);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, ended.Outcome);
        Assert.False(ended.ClosedByAi);
        Assert.NotEqual(TicketStatus.Closed, w.Ticket(call.TicketId).TicketStatus);          // the end alone closes nothing

        var closed = await CloseByAiAsync(w, call);                                           // ... and a later explicit closure is honoured

        Assert.True(closed.ClosedByAi);
        Assert.Equal(TicketStatus.Closed, w.Ticket(call.TicketId).TicketStatus);
    }

    [Fact]
    public async Task ClosedByAiAndEndedInTheSameUpdate_Closes()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-same-update");

        var result = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, call.TicketId,
            new GenesysTicketUpdateDto(call.ConversationId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow.AddSeconds(-1), "AiCompleted"), ClosedByAi: true));

        Assert.True(result.ClosedByAi);
        Assert.Equal(TicketStatus.Closed, w.Ticket(call.TicketId).TicketStatus);
    }

    [Fact]
    public async Task CallTerminationAlone_NeverClosesFlagsRaisesAHandoffOrAssignsAnyone()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-end-only");

        var ended = await EndAsync(w, call);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, ended.Outcome);
        var t = w.Ticket(call.TicketId);
        Assert.Equal(TicketStatus.Open, t.TicketStatus);
        AssertAiOwnedAndUnassigned(w, call.TicketId);
        var interaction = w.F.InteractionFor(call.ConversationId)!;
        Assert.True(interaction.IsEnded);
        Assert.NotNull(interaction.EndReceivedAtUtc);                                          // the anchor of the reconciliation window
        Assert.False(interaction.RequiresHumanFollowUp);                                       // no permanent human-follow-up flag from the end alone
        Assert.Empty(w.F.Handoffs.All);                                                        // no handoff work item from the end alone
        Assert.Equal(w.Cs.DepartmentId, t.CurrentDepartmentId);                                // not moved
    }

    [Fact]
    public async Task AiClosure_IsNotRejectedBecauseTheTicketSitsInTheCallCenter()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-cc");
        var ticket = w.Ticket(call.TicketId);
        ticket.TransferToDepartment(w.F.CallCenter!.DepartmentId, keepAiAgentOwner: true);    // a voice ticket queued in the Call Center, still the AI's

        var result = await CloseByAiAsync(w, call);

        Assert.True(result.ClosedByAi);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    // ---- 3. Handoff: no Round Robin while a human may still answer -----------------------------------------------------------------

    [Fact]
    public async Task AiAsksForAHuman_VoiceTicketWaitsInTheCallCenterQueue_WithTheAiAgentAsOwner_AndNoRoundRobin()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-handoff");

        var handoff = await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(call.ConversationId, AgentAvailable: false, Mode: "HumanTakeover"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        var t = w.Ticket(call.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        AssertAiOwnedAndUnassigned(w, call.TicketId);
    }

    [Fact]
    public async Task ExplicitHumanFollowUp_OnAVoiceCall_QueuesWithoutRoundRobin()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-follow-up", humanFollowUp: true);

        AssertAiOwnedAndUnassigned(w, call.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);
    }

    [Fact]
    public async Task AgentOnlyOfferedOrAssignedToTheHandoff_IsNotAnAnswer()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-offered");

        await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(call.ConversationId, AgentAvailable: true, Mode: "HumanTakeover", AgentId: w.Agents[1].GenesysId));

        AssertAiOwnedAndUnassigned(w, call.TicketId);                                          // offered / assigned is not answered
    }

    // ---- 4. Direct human call / AI->human transfer -------------------------------------------------------------------------------

    [Fact]
    public async Task DirectHumanCall_IsAssignedToTheActualAnsweringEmployee_NotTheRoundRobinNext()
    {
        var w = NewWorld();
        var answering = w.Agents.Last();

        var call = await DirectCallAsync(w, "v-direct", answering.GenesysId);

        var t = w.Ticket(call.TicketId);
        Assert.Equal(answering.Id, t.CurrentOwnerEmployeeId);
        Assert.Null(t.AiAgentOwnerEmployeeId);                                                // the AI Agent is never recorded on a call a human picked up
        Assert.Equal(answering.Id, t.OriginalHumanAgentId);                                    // persisted, write-once
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
    }

    [Fact]
    public async Task AiTransfersToAHuman_TheFirstHumanWhoActuallyAnsweredOwnsTheExistingTicket()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-transfer");
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(call.ConversationId, AgentAvailable: false, Mode: "HumanTakeover"));
        var answering = w.Agents.Last();

        var result = await ConnectAsync(w, call, answering.GenesysId);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        var t = w.Ticket(call.TicketId);
        Assert.Equal(answering.Id, t.CurrentOwnerEmployeeId);
        Assert.Null(t.AiAgentOwnerEmployeeId);
        Assert.Equal(answering.Id, t.OriginalHumanAgentId);
        Assert.Single(w.F.Tickets.All, x => x.TicketId == call.TicketId);                      // the SAME ticket, no duplicate
        var history = w.F.AssignmentHistory.Entries.Where(h => h.TicketId == call.TicketId).ToList();
        var accepted = Assert.Single(history, h => h.NewEmployeeId == answering.Id);           // exactly one row hands the ticket to the answering employee
        Assert.NotNull(accepted.PreviousEmployeeId);                                           // AI Agent -> employee
        Assert.Equal(TicketAssignmentSource.GenesysCallTransfer, accepted.Source);
    }

    // ---- 5. A later agent never replaces the original; every agent is in the interaction history ------------------------------------

    [Fact]
    public async Task LaterAgentOnTheSameCall_DoesNotReplaceTheOriginalOwner_AndIsRecordedAsAHandler()
    {
        var w = NewWorld();
        var call = await DirectCallAsync(w, "v-two-agents", w.Agents[0].GenesysId);
        var original = w.Agents[0].Id;
        var second = w.Agents[1];

        await ConnectAsync(w, call, second.GenesysId, DateTime.UtcNow);

        var t = w.Ticket(call.TicketId);
        Assert.Equal(original, t.CurrentOwnerEmployeeId);                                      // retained
        Assert.Equal(original, t.OriginalHumanAgentId);
        var assignment = Assert.Single(w.Current(call.TicketId));
        Assert.Equal(original, assignment.AssignedEmployeeId);
        Assert.Contains(w.F.Handlers.All, h => h.TicketId == call.TicketId && h.HandledByUserId == second.Id);   // recorded in the interaction history
    }

    [Fact]
    public async Task ManyAgentsInTurn_OnlyTheFirstOwns_AndTheSameEventTwiceChangesNothing()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-many");
        await ConnectAsync(w, call, w.Agents[2].GenesysId);

        await ConnectAsync(w, call, w.Agents[0].GenesysId);
        await ConnectAsync(w, call, w.Agents[1].GenesysId);
        await ConnectAsync(w, call, w.Agents[2].GenesysId);                                    // duplicate of the first agent's event

        Assert.Equal(w.Agents[2].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Single(w.Current(call.TicketId));
        Assert.Equal(1, w.F.TicketAssignments.Added.Count(a => a.TicketId == call.TicketId && a.AssignedEmployeeId == w.Agents[2].Id));
    }

    // ---- 6. A person's manual / authorised transfer is preserved ---------------------------------------------------------------------

    [Fact]
    public async Task AnAuthorisedManualAssignment_IsNeverMovedByALaterConnectEvent()
    {
        var w = NewWorld();
        var call = await DirectCallAsync(w, "v-manual", w.Agents[0].GenesysId);
        var supervisorChoice = w.Agents[1].Id;
        var ticket = w.Ticket(call.TicketId);
        w.Current(call.TicketId).Single().MarkSuperseded();
        ticket.AssignTo(supervisorChoice);
        await w.F.TicketAssignments.AddAsync(new TicketAssignment(call.TicketId, supervisorChoice, ticket.CurrentDepartmentId, DateTime.UtcNow, assigningActorEmployeeId: Guid.NewGuid()));

        await ConnectAsync(w, call, w.Agents[2].GenesysId);

        Assert.Equal(supervisorChoice, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    // ---- 7. Delayed and out-of-order events -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RoutingAgentArrivingAfterEnded_StillGetsTheTicket_AndNoRoundRobinRan()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-late-routing");
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(call.ConversationId, AgentAvailable: false, Mode: "HumanTakeover"));
        await EndAsync(w, call);
        AssertAiOwnedAndUnassigned(w, call.TicketId);                                          // ended, nobody answered yet: still waiting

        await ConnectAsync(w, call, w.Agents[1].GenesysId);                                    // the answer event arrives AFTER ended

        Assert.Equal(w.Agents[1].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
    }

    [Fact]
    public async Task DuplicateEndedEvent_IsIdempotent_AndKeepsTheFirstReceiptTime()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-dup-end");
        await EndAsync(w, call);
        var received = w.F.InteractionFor(call.ConversationId)!.EndReceivedAtUtc;

        var again = await EndAsync(w, call);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, again.Outcome);
        Assert.Equal(received, w.F.InteractionFor(call.ConversationId)!.EndReceivedAtUtc);    // write-once: a redelivery never restarts the window
    }

    // ---- 8. SLA: first response is the real connect time ----------------------------------------------------------------------------

    [Fact]
    public async Task ConnectEvent_RecordsFirstHumanResponseAtTheRealConnectTime_WithoutWaitingForATranscript()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-fr");
        var connectedAt = DateTime.UtcNow.AddMinutes(-3);

        await ConnectAsync(w, call, w.Agents[1].GenesysId, connectedAt);

        var t = w.Ticket(call.TicketId);
        Assert.Equal(connectedAt, t.FirstHumanResponseAtUtc);                                  // not the receipt time
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "RecordFirstResponse" && e.EntityId == call.TicketId.ToString() && e.AfterValue!.Contains("GenesysCallAnswer"));
    }

    [Fact]
    public async Task DirectCall_RecordsFirstHumanResponseAtTheInquiryConnectTime()
    {
        var w = NewWorld();
        var connectedAt = DateTime.UtcNow.AddMinutes(-2);

        var call = await DirectCallAsync(w, "v-fr-direct", w.Agents[0].GenesysId, connectedAt);

        Assert.Equal(connectedAt, w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task ConnectWithoutAConnectTime_UsesReceiptTimeAsTheAuditedLastResort()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-fr-missing");

        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, call.TicketId,
            new GenesysTicketUpdateDto(call.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: w.Agents[0].GenesysId)));

        Assert.NotNull(w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == GenesysAuditActions.VoiceFirstResponseConnectTimeMissing);
    }

    [Fact]
    public async Task ALaterAgentOrATransfer_NeverMovesTheFirstResponse()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-fr-once");
        var first = DateTime.UtcNow.AddMinutes(-4);
        await ConnectAsync(w, call, w.Agents[0].GenesysId, first);

        await ConnectAsync(w, call, w.Agents[1].GenesysId, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(first, w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task QueueRoutingAndTheAiConversation_NeverRecordAFirstHumanResponse()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-fr-none");
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(call.ConversationId, AgentAvailable: false, Mode: "HumanTakeover"));
        await EndAsync(w, call);

        Assert.Null(w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task AnUnmappedAnsweringAgent_RecordsNoFirstResponse_NoRoundRobin_AndFlagsTheMappingFailure()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "v-unmapped");

        await ConnectAsync(w, call, "g-not-mapped-anywhere");

        var t = w.Ticket(call.TicketId);
        Assert.Null(t.CurrentOwnerEmployeeId);                                                  // not Round Robin, not guessed
        Assert.Null(t.FirstHumanResponseAtUtc);                                                 // an unmapped id has no employee to credit
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
        var flag = Assert.Single(w.F.WorkflowEvents.All, e => e.TicketId == call.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        Assert.Contains("GenesysUserId=g-not-mapped-anywhere", flag.Note);
        Assert.Contains("ConnectedAtUtc=", flag.Note);                                          // lets the reconciler date the assignment at the real connect time
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);                      // visible in the Call Center queue
    }
}
