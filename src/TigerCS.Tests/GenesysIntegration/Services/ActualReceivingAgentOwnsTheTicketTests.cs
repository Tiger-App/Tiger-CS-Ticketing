using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// THE HUMAN AGENT WHO ACTUALLY RECEIVES OR ACCEPTS THE INTERACTION OWNS THE TICKET - on every channel. Besides <c>routing.agentId</c> (connected),
/// a <c>HumanAgent</c> transcript line carrying the agent's Genesys id proves who received the conversation; the ticket then goes to THAT
/// employee. There is no Round Robin: an agent who cannot take it leaves the ticket in the Call Center queue FLAGGED, with the AI Agent still its owner, and
/// nobody else is dealt in their place.
/// </summary>
public class ActualReceivingAgentOwnsTheTicketTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> AllChannels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp,
        GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    private sealed record World(GenesysServiceFixture F, Department Cs, Guid[] Agents);

    private static World Build()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        return new World(f, cs, [f.AddAgent("g-a1", "Agent 1"), f.AddAgent("g-a2", "Agent 2"), f.AddAgent("g-a3", "Agent 3")]);
    }

    private static async Task<Ticket> IngestAsync(World w, string conversationId, GenesysChannel channel, bool? humanFollowUp = null)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, channel, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId, HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    private static Task<GenesysTicketUpdateResult> HumanLineAsync(World w, Ticket t, string conversationId, string sender, string senderId, string id = "m1") =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, t.TicketId,
            new GenesysTicketUpdateDto(conversationId, Transcript: [new GenesysTranscriptMessageDto(sender, DateTime.UtcNow, "Hello, how can I help?", "Agent", senderId, id)]));

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, Ticket t, string conversationId, string agentId) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(conversationId, Routing: new GenesysRoutingUpdateDto(AgentId: agentId, ConnectedAtUtc: DateTime.UtcNow)));

    private static bool RotationUntouched(World w) => w.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    private static void AssertSingleOwner(World w, Ticket t, Guid? expected)
    {
        Assert.Equal(expected, t.CurrentOwnerEmployeeId);
        var current = w.F.TicketAssignments.Added.Where(a => a.TicketId == t.TicketId && a.IsCurrent).ToList();
        if (expected is null) Assert.Empty(current); else Assert.Equal(expected, Assert.Single(current).AssignedEmployeeId);
    }

    private static void AssertQueuedWithTheAiOwner(World w, Ticket t)
    {
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        Assert.Equal(ServiceAccount, t.AiAgentOwnerEmployeeId);
        AssertSingleOwner(w, t, null);
        Assert.True(RotationUntouched(w));
    }

    // ---- 1. a human who wrote to the customer received the interaction ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanAgentTranscriptLine_GivesTheTicketToThatEmployee_NotToRoundRobin(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-line", channel);

        await HumanLineAsync(w, t, "c-line", "HumanAgent", "g-a2");

        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        AssertSingleOwner(w, t, w.Agents[1]);
        Assert.True(RotationUntouched(w));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.AfterValue!.Contains("Source=Transcript"));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanLineFromAnAgentWhoCannotTakeIt_FlagsTheTicketInTheQueue_AndNobodyIsDealtInTheirPlace(GenesysChannel channel)
    {
        var w = Build();
        var employee = w.F.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);        // mapped Call Center member, but not a CS Agent
        var t = await IngestAsync(w, "c-inelig", channel);

        await HumanLineAsync(w, t, "c-inelig", "HumanAgent", "g-emp");

        Assert.NotEqual(employee, t.CurrentOwnerEmployeeId);                              // the non-CS-Agent never owns it
        AssertQueuedWithTheAiOwner(w, t);                                                 // ...and no CS Agent is dealt by Round Robin instead
        Assert.Contains(w.F.WorkflowEvents.All, e => e.TicketId == t.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.EntityId == t.TicketId.ToString());
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == t.TicketId.ToString());
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanLineWithAnUnknownId_StillCountsAsHumanWork_ButNobodyIsAssigned(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-unknown", channel);

        await HumanLineAsync(w, t, "c-unknown", "HumanAgent", "participant-not-an-employee");

        AssertQueuedWithTheAiOwner(w, t);                                                 // a person is involved: queue only, never a Round Robin pick
        Assert.Contains(w.F.WorkflowEvents.All, e => e.TicketId == t.TicketId && e.EventType == WorkflowEventType.TicketTransferred && e.ToDepartmentId == w.F.CallCenter!.DepartmentId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action is "AssignOnAnswer" or "AutoAssign" && e.EntityId == t.TicketId.ToString());
    }

    // ---- 7. AI-only stays AI -----------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task VirtualAgentAndCustomerLines_NeverMoveOrAssign_EvenWithASenderId(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-bot", channel);

        await HumanLineAsync(w, t, "c-bot", "VirtualAgent", "g-a1", "m1");
        await HumanLineAsync(w, t, "c-bot", "Customer", "g-a1", "m2");

        Assert.Equal(w.Cs.DepartmentId, t.CurrentDepartmentId);
        Assert.Equal(ServiceAccount, t.AiAgentOwnerEmployeeId);
        AssertSingleOwner(w, t, null);
        Assert.True(RotationUntouched(w));
    }

    // ---- 2-3. AI transfers to a human: the ticket queues with the AI owner, then the agent who ACCEPTS owns it ---------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AiTransfersToHuman_TheTicketQueuesWithTheAiOwner_TheAcceptingAgentIsAssignedDirectly(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-transfer", channel);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-transfer", Handoff: new GenesysHandoffUpdateDto(Required: true, AgentAvailable: false, Mode: "HumanTakeover")));

        // Nobody has accepted: the department moved to the Call Center queue (the originating one is untouched), the AI Agent is still the owner.
        AssertQueuedWithTheAiOwner(w, t);
        Assert.Equal(w.Cs.DepartmentId, t.OriginatingDepartmentId);
        Assert.DoesNotContain(w.F.AssignmentHistory.Entries, h => h.TicketId == t.TicketId && h.Source == TicketAssignmentSource.GenesysHumanFollowUp);
        await ConnectAsync(w, t, "c-transfer", "g-a2");

        AssertSingleOwner(w, t, w.Agents[1]);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == t.TicketId);                 // no provisional row ever existed
        var accepted = Assert.Single(w.F.AssignmentHistory.Entries,
            h => h.TicketId == t.TicketId && h.NewEmployeeId == w.Agents[1] && h.Source is TicketAssignmentSource.GenesysDirectCall or TicketAssignmentSource.GenesysCallTransfer);
        Assert.Equal(ServiceAccount, accepted.PreviousEmployeeId);                                 // the previous owner is the AI Agent
        Assert.True(RotationUntouched(w));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanFollowUpFlagRaisedLater_QueuesTheTicket_WritingAQueueRoutingRow_AndAssignsNobody(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-flag", channel);
        Assert.Equal(w.Cs.DepartmentId, t.CurrentDepartmentId);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto("c-flag", HumanFollowUpRequired: true));

        AssertQueuedWithTheAiOwner(w, t);
        Assert.Equal(w.Cs.DepartmentId, t.OriginatingDepartmentId);
        var move = Assert.Single(w.F.AssignmentHistory.Entries, h => h.TicketId == t.TicketId && h.Source == TicketAssignmentSource.GenesysQueueRouting);
        Assert.Equal(w.Cs.DepartmentId, move.PreviousDepartmentId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, move.DepartmentId);
        Assert.Equal(ServiceAccount, move.PreviousEmployeeId);
        Assert.Equal(ServiceAccount, move.NewEmployeeId);                                          // ownership unchanged: the AI Agent on both sides
        Assert.DoesNotContain(w.F.AssignmentHistory.Entries, h => h.TicketId == t.TicketId && h.Source == TicketAssignmentSource.GenesysHumanFollowUp);
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanLine_GivesAQueuedTicketToThatAgent_ButNotAfterTheOwnerHasWorked(GenesysChannel channel)
    {
        var w = Build();
        var queued = await IngestAsync(w, "c-repl", channel, humanFollowUp: true);
        var worked = await IngestAsync(w, "c-work", channel, humanFollowUp: true);
        AssertQueuedWithTheAiOwner(w, queued);
        AssertQueuedWithTheAiOwner(w, worked);

        await ConnectAsync(w, worked, "c-work", "g-a1");                                         // agent 1 accepted...
        worked.RecordFirstHumanResponse(DateTime.UtcNow);                                        // ...and already answered the customer

        await HumanLineAsync(w, queued, "c-repl", "HumanAgent", "g-a2");
        await HumanLineAsync(w, worked, "c-work", "HumanAgent", "g-a3");

        AssertSingleOwner(w, queued, w.Agents[1]);
        AssertSingleOwner(w, worked, w.Agents[0]);
        Assert.True(RotationUntouched(w));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConnectedAndFirstReplyInOneUpdate_TheReplyDoesNotMakeAnEarlierAcceptorLookLikeTheWorker(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-combined", channel, humanFollowUp: true);

        // Connected and the first reply from the SAME agent in one update: they take the queued ticket.
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-combined",
            Routing: new GenesysRoutingUpdateDto(AgentId: "g-a2", ConnectedAtUtc: DateTime.UtcNow),
            Transcript: [new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow, "Hi, I am taking this.", "Agent", "g-a2", "m1")]));

        AssertSingleOwner(w, t, w.Agents[1]);

        // ...whereas a reply written by the agent who ALREADY holds the ticket is their work and keeps it, even when Genesys also reports
        // somebody else connected in the same update.
        var t2 = await IngestAsync(w, "c-combined-2", channel, humanFollowUp: true);
        await ConnectAsync(w, t2, "c-combined-2", "g-a1");
        AssertSingleOwner(w, t2, w.Agents[0]);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, t2.TicketId, new GenesysTicketUpdateDto(
            "c-combined-2",
            Routing: new GenesysRoutingUpdateDto(AgentId: "g-a3", ConnectedAtUtc: DateTime.UtcNow),
            Transcript: [new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow, "Already answered by the first agent.", "Agent", "g-a1", "m2")]));

        AssertSingleOwner(w, t2, w.Agents[0]);
        Assert.True(RotationUntouched(w));
    }
}
