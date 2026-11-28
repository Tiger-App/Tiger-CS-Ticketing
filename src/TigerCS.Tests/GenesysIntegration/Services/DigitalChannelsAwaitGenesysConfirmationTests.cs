using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// <b>Genesys messaging channels wait for Genesys.</b> WhatsApp, web chat, Instagram, Facebook and social media never get a provisional human owner from TigerCS: the ticket is created and queued
/// (Call Center first, the AI Agent as recorded owner) and then WAITS - no Round Robin at creation / handoff / follow-up / conversation end, no turn spent, no AutoAssign audit. A person owns it only
/// when Genesys confirms who handles the conversation (<c>routing.agentId</c> connected). An answering agent who cannot take it is flagged and the ticket stays unassigned. Each scenario is the same
/// one <see cref="VoiceOwnershipRulesTests"/> asserts for a call; the unchanged digital rules (no first response from a connect event, handling-state display, the Call-Center guard on AI closure) stay pinned too.
/// </summary>
public class DigitalChannelsAwaitGenesysConfirmationTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> Digital => new()
    {
        GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
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

    private static async Task<(long TicketId, string ConversationId)> ChatAsync(World w, GenesysChannel channel, string conversationId, bool humanFollowUp = false, string? agentId = null)
    {
        var r = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, channel, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId, AgentId: agentId,
                HumanFollowUpRequired: humanFollowUp ? true : null));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return (r.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, (long TicketId, string ConversationId) t, string genesysAgentId, DateTime? at = null) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, t.TicketId,
            new GenesysTicketUpdateDto(t.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: at ?? DateTime.UtcNow.AddSeconds(-5))));

    private static bool CursorUntouched(World w) => w.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    private static void AssertAwaitingGenesys(World w, long ticketId)
    {
        Assert.Null(w.Ticket(ticketId).CurrentOwnerEmployeeId);                                 // nobody is dealt a provisional owner
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticketId);        // no assignment row
        Assert.True(CursorUntouched(w));                                                        // no Round Robin turn was taken
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString());
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task HumanFollowUpAtCreation_EntersTheCallCenter_WithNoOwner_AndTheAiAgentStaysRecordedOwner(GenesysChannel channel)
    {
        var w = NewWorld();

        var chat = await ChatAsync(w, channel, "d-follow-up", humanFollowUp: true);

        var t = w.Ticket(chat.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        Assert.NotNull(t.AiAgentOwnerEmployeeId);                                              // the AI Agent stays the recorded owner until Genesys names a person
        AssertAwaitingGenesys(w, chat.TicketId);
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task AHandoffRequest_QueuesTheTicketInTheCallCenter_WithoutDealingAnyone(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-handoff");

        var handoff = await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(chat.ConversationId, AgentAvailable: false, Mode: "HumanTakeover"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
        Assert.NotNull(w.Ticket(chat.TicketId).AiAgentOwnerEmployeeId);
        AssertAwaitingGenesys(w, chat.TicketId);
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task AnAiConversationThatEndsUnresolved_IsPromotedToHumanWork_QueuedButNotDealt(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-ended");

        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, chat.TicketId,
            new GenesysTicketUpdateDto(chat.ConversationId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow.AddSeconds(-1), "CustomerDisconnect")));

        var t = w.Ticket(chat.TicketId);
        Assert.True(w.F.InteractionFor(chat.ConversationId)!.RequiresHumanFollowUp);           // the previous behaviour: an unfinished AI end is human work
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        AssertAwaitingGenesys(w, chat.TicketId);                                                // queued for a person, but Genesys - not a rotation - names who
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task ASecondConnectedAgent_StillTakesOverFromAnOwnerGenesysSet_OnTheLiveConversation(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-transfer", humanFollowUp: true);
        await ConnectAsync(w, chat, w.Agents[2].GenesysId, DateTime.UtcNow.AddSeconds(-30));
        Assert.Equal(w.Agents[2].Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);

        await ConnectAsync(w, chat, w.Agents[1].GenesysId, DateTime.UtcNow.AddSeconds(-5));

        Assert.Equal(w.Agents[1].Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);          // reassignment on transfer is unchanged for digital channels
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task AnAnsweringAgentWhoCannotTakeIt_IsFlagged_AndTheTicketStaysUnassigned_NoRoundRobin(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-unmapped", humanFollowUp: true);
        AssertAwaitingGenesys(w, chat.TicketId);

        await ConnectAsync(w, chat, "g-not-mapped-anywhere");

        Assert.Single(w.F.WorkflowEvents.All, e => e.TicketId == chat.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        AssertAwaitingGenesys(w, chat.TicketId);                                                // flagged and unassigned: the next CS Agent in the rotation is NOT dealt the ticket
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task AConnectEvent_NeverRecordsAFirstHumanResponse_OnADigitalChannel(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-no-fr", humanFollowUp: true);

        await ConnectAsync(w, chat, w.Agents[1].GenesysId);

        Assert.Null(w.Ticket(chat.TicketId).FirstHumanResponseAtUtc);                           // only a real human reply does (unchanged)
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task ADigitalHandlingState_StaysAi_WhateverThePhoneStatesAre(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-state");
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, chat.TicketId,
            new GenesysTicketUpdateDto(chat.ConversationId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow.AddSeconds(-1), "CustomerDisconnect")));

        var interaction = w.F.InteractionFor(chat.ConversationId)!;
        var history = await w.F.InteractionQuery.GetForTicketAsync(Guid.NewGuid(), [Roles.SystemAdministrator], chat.TicketId);

        Assert.True(interaction.IsEnded);
        Assert.Equal("Ai", Assert.Single(history.Response!.Interactions).HandlingState);       // digital display is untouched; "AiEnded"/"HumanFollowUp" are Phone-only
    }

    [Fact]
    public async Task ADigitalTicketInTheCallCenter_IsStillNotClosedByTheAi_ButAVoiceOneIs()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, GenesysChannel.WebsiteChat, "d-cc-chat");
        w.Ticket(chat.TicketId).TransferToDepartment(w.F.CallCenter!.DepartmentId);            // an AI chat sitting in the Call Center queue

        var refused = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, chat.TicketId, new GenesysTicketUpdateDto(chat.ConversationId, ClosedByAi: true));

        Assert.False(refused.ClosedByAi);                                                       // previous rule for digital: Call Center tickets are never closed by the AI
        Assert.Contains("Call Center", refused.ClosedByAiNote);
        Assert.NotEqual(TicketStatus.Closed, w.Ticket(chat.TicketId).TicketStatus);
    }
}
