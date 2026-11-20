using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// <b>Digital-channel regression (PR #292 second pass).</b> The new AI-ownership / answering-agent rules are Phone-only. WhatsApp, web chat, Instagram, Facebook and social media keep
/// the assignment behaviour they had before the PR - Call Center first with ONE Round Robin agent at creation / handoff / follow-up, the end-of-conversation promotion, the
/// next-agent fallback and reassignment on transfer, no first response from a connect event, the Call-Center guard on AI closure - until their own requirements are final.
/// Each scenario is the same one <see cref="VoiceOwnershipRulesTests"/> asserts for a call.
/// </summary>
public class DigitalChannelsKeepTheirPreviousBehaviourTests
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

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task HumanFollowUpAtCreation_EntersTheCallCenter_WithOneRoundRobinAgent_AsOwner(GenesysChannel channel)
    {
        var w = NewWorld();

        var chat = await ChatAsync(w, channel, "d-follow-up", humanFollowUp: true);

        var t = w.Ticket(chat.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        Assert.Equal(w.Agents[0].Id, t.CurrentOwnerEmployeeId);                                // Round Robin, exactly as before the PR
        Assert.Null(t.AiAgentOwnerEmployeeId);                                                 // a person owns it; the AI Agent no longer does
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task AHandoffRequest_DealsOneAgentByRoundRobin_ImmediatelyAtHandoffTime(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-handoff");

        var handoff = await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(chat.ConversationId, AgentAvailable: false, Mode: "HumanTakeover"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        Assert.Equal(w.Agents[0].Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
    }

    [Theory]
    [MemberData(nameof(Digital))]
    public async Task AnAiConversationThatEndsUnresolved_IsPromotedToHumanWork_QueuedAndDealtByRoundRobin(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-ended");

        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, chat.TicketId,
            new GenesysTicketUpdateDto(chat.ConversationId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow.AddSeconds(-1), "CustomerDisconnect")));

        var t = w.Ticket(chat.TicketId);
        Assert.True(w.F.InteractionFor(chat.ConversationId)!.RequiresHumanFollowUp);           // the previous behaviour: an unfinished AI end is human work
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        Assert.Equal(w.Agents[0].Id, t.CurrentOwnerEmployeeId);
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
    public async Task AnAnsweringAgentWhoCannotTakeIt_IsFlagged_AndTheSharedRotationStillGivesItACsAgent(GenesysChannel channel)
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, channel, "d-unmapped", humanFollowUp: true);
        var provisional = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId;

        await ConnectAsync(w, chat, "g-not-mapped-anywhere");

        Assert.Single(w.F.WorkflowEvents.All, e => e.TicketId == chat.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        Assert.NotNull(w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);                         // never left without a CS Agent (previous rule)
        Assert.Equal(provisional, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);              // the existing eligible CS Agent is preserved
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
