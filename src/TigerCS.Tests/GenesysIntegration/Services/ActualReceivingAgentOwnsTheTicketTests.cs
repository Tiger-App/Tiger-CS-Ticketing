using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// THE HUMAN AGENT WHO ACTUALLY RECEIVES OR ACCEPTS THE INTERACTION OWNS THE TICKET - on every channel. Besides <c>routing.agentId</c> (connected),
/// a <c>HumanAgent</c> transcript line carrying the agent's Genesys id proves who received the conversation; the ticket then goes to THAT
/// employee (never the next Round Robin name). An agent who cannot take it is flagged, never replaced by somebody else. A Genesys messaging ticket has no provisional
/// owner: until Genesys confirms the agent it is queued with no owner (the AI Agent stays the recorded owner), and no Round Robin turn is spent.
/// </summary>
public class ActualReceivingAgentOwnsTheTicketTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> AllChannels => new()
    {
        GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp,
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

    /// <summary>A legacy provisional owner: the automatic pre-assignment an authorised retry (which bypasses the Genesys-messaging guard) still makes.</summary>
    private static async Task<Guid> GiveProvisionalOwnerAsync(World w, Ticket t)
    {
        await w.F.AutoAssignment.EnsureCsAgentAsync(t, null, AutoAssignmentTrigger.ManualRetry, DateTime.UtcNow, Guid.NewGuid());
        Assert.Contains(t.CurrentOwnerEmployeeId!.Value, w.Agents);
        return t.CurrentOwnerEmployeeId!.Value;
    }

    private static string IdOf(World w, Guid id) => id == w.Agents[0] ? "g-a1" : id == w.Agents[1] ? "g-a2" : "g-a3";

    private static void AssertSingleOwner(World w, Ticket t, Guid? expected)
    {
        Assert.Equal(expected, t.CurrentOwnerEmployeeId);
        var current = w.F.TicketAssignments.Added.Where(a => a.TicketId == t.TicketId && a.IsCurrent).ToList();
        if (expected is null) Assert.Empty(current); else Assert.Equal(expected, Assert.Single(current).AssignedEmployeeId);
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
    public async Task AHumanLineFromAnAgentWhoCannotTakeIt_FlagsTheTicket_AndItStaysUnassigned_NoRotationFallback(GenesysChannel channel)
    {
        var w = Build();
        var employee = w.F.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);        // mapped Call Center member, but not a CS Agent
        var t = await IngestAsync(w, "c-inelig", channel);

        await HumanLineAsync(w, t, "c-inelig", "HumanAgent", "g-emp");

        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        Assert.NotEqual(employee, t.CurrentOwnerEmployeeId);                              // the non-CS-Agent never owns it
        AssertSingleOwner(w, t, null);                                                    // nor does anybody else: flagged and left in the queue
        Assert.True(RotationUntouched(w));
        Assert.Contains(w.F.WorkflowEvents.All, e => e.TicketId == t.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanLineWithAnUnknownId_StillCountsAsHumanWork_ButAssignsNobody_NoRoundRobin(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-unknown", channel);

        await HumanLineAsync(w, t, "c-unknown", "HumanAgent", "participant-not-an-employee");

        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        AssertSingleOwner(w, t, null);
        Assert.True(RotationUntouched(w));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action is "AutoAssign" && e.EntityId == t.TicketId.ToString());
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
        AssertSingleOwner(w, t, null);
    }

    // ---- 2-3. AI transfers to a human: nobody is dealt, then the agent who ACCEPTS owns it directly --------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AiTransfersToHuman_NobodyIsDealt_TheAcceptingAgentOwnsItDirectly(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-transfer", channel);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-transfer", Handoff: new GenesysHandoffUpdateDto(Required: true, AgentAvailable: false, Mode: "HumanTakeover")));
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        AssertSingleOwner(w, t, null);                                                           // the handoff request is not an acceptance: no provisional owner
        Assert.True(RotationUntouched(w));

        var acceptor = w.Agents[1];
        await ConnectAsync(w, t, "c-transfer", IdOf(w, acceptor));

        AssertSingleOwner(w, t, acceptor);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == t.TicketId);                // directly to the acceptor: one row, no intermediate owner
        Assert.True(RotationUntouched(w));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanLine_ReplacesALegacyProvisionalOwner_ButNotAfterTheOwnerHasWorked(GenesysChannel channel)
    {
        var w = Build();
        // A legacy ticket that already HAS a provisional owner (created before Genesys became the source of truth, or by an authorised retry) keeps the replace logic.
        var replaced = await IngestAsync(w, "c-repl", channel, humanFollowUp: true);
        var worked = await IngestAsync(w, "c-work", channel, humanFollowUp: true);
        AssertSingleOwner(w, replaced, null);
        await GiveProvisionalOwnerAsync(w, replaced);
        var workedOwner = await GiveProvisionalOwnerAsync(w, worked);
        worked.RecordFirstHumanResponse(DateTime.UtcNow);                                        // the provisional owner already answered the customer

        var other1 = w.Agents.First(a => a != replaced.CurrentOwnerEmployeeId);
        var other2 = w.Agents.First(a => a != workedOwner);

        await HumanLineAsync(w, replaced, "c-repl", "HumanAgent", IdOf(w, other1));
        await HumanLineAsync(w, worked, "c-work", "HumanAgent", IdOf(w, other2));

        AssertSingleOwner(w, replaced, other1);
        AssertSingleOwner(w, worked, workedOwner);
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConnectedAndFirstReplyInOneUpdate_AssignsTheConnectedAgent_AndAReplyFromALegacyProvisionalOwnerKeepsTheTicketWithThem(GenesysChannel channel)
    {
        var w = Build();
        var t = await IngestAsync(w, "c-combined", channel, humanFollowUp: true);
        AssertSingleOwner(w, t, null);
        var incoming = w.Agents[1];
        var incomingId = IdOf(w, incoming);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-combined",
            Routing: new GenesysRoutingUpdateDto(AgentId: incomingId, ConnectedAtUtc: DateTime.UtcNow),
            Transcript: [new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow, "Hi, I am taking this.", "Agent", incomingId, "m1")]));

        AssertSingleOwner(w, t, incoming);

        // ...whereas, on a legacy ticket that already has a provisional owner, a reply written by SOMEONE ELSE in the same update is the provisional owner's work and keeps the ticket with them.
        var t2 = await IngestAsync(w, "c-combined-2", channel, humanFollowUp: true);
        var provisional2 = await GiveProvisionalOwnerAsync(w, t2);
        var provisionalId = IdOf(w, provisional2);
        var incoming2 = w.Agents.First(a => a != provisional2);
        var incoming2Id = IdOf(w, incoming2);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, t2.TicketId, new GenesysTicketUpdateDto(
            "c-combined-2",
            Routing: new GenesysRoutingUpdateDto(AgentId: incoming2Id, ConnectedAtUtc: DateTime.UtcNow),
            Transcript: [new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow, "Already answered by the first agent.", "Agent", provisionalId, "m2")]));

        AssertSingleOwner(w, t2, provisional2);
    }
}
