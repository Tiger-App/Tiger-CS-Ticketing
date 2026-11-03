using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// "The answering agent owns the ticket" on EVERY Genesys channel, end to end through the real ingestion and update services:
/// Phone, Website (Live) Chat, WhatsApp, Instagram, Facebook Messenger and generic social media. Each channel runs the same scenarios
/// against the same expectations, so a channel can never again behave differently from Phone.
/// </summary>
public class OmnichannelAnsweringAgentMatrixTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string AnsweringGenesysId = "g-answering";

    public static TheoryData<GenesysChannel, byte, string> Channels => new()
    {
        { GenesysChannel.Phone, WellKnownChannels.Phone, "PHONE" },
        { GenesysChannel.WebsiteChat, WellKnownChannels.LiveChat, "LIVE_CHAT" },
        { GenesysChannel.WhatsApp, WellKnownChannels.WhatsApp, "WHATSAPP" },
        { GenesysChannel.Instagram, WellKnownChannels.Instagram, "INSTAGRAM" },
        { GenesysChannel.Facebook, WellKnownChannels.Facebook, "FACEBOOK" },
        { GenesysChannel.SocialMedia, WellKnownChannels.SocialMediaDirectMessage, "SOCIAL_DM" }
    };

    public static TheoryData<GenesysChannel> AllChannels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    public static TheoryData<GenesysChannel> DigitalChannels => new()
    {
        GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required Guid Answering { get; init; }
        public required Guid[] RoundRobinAgents { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
    }

    private static World Build()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var answering = f.AddAgent(AnsweringGenesysId, "Answering Agent");
        var others = new[] { f.AddAgent("g-other-1", "Other 1"), f.AddAgent("g-other-2", "Other 2") };
        return new World { F = f, Cs = cs, Answering = answering, RoundRobinAgents = [answering, .. others] };
    }

    private static async Task<long> IngestAsync(World w, string conversationId, GenesysChannel channel, string? agentId = null, bool? humanFollowUp = null)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, channel, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId,
                AgentId: agentId, HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return result.Ticket!.TicketId;
    }

    private static Task<GenesysTicketUpdateResult> UpdateAsync(World w, long ticketId, GenesysTicketUpdateDto dto) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, dto);

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, long ticketId, string conversationId, string agentId) =>
        UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(conversationId, Routing: new GenesysRoutingUpdateDto(AgentId: agentId, ConnectedAtUtc: DateTime.UtcNow)));

    private static void AssertOneOwner(World w, long ticketId, Guid? expected)
    {
        Assert.Equal(expected, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        var current = w.F.TicketAssignments.Added.Where(a => a.TicketId == ticketId && a.IsCurrent).ToList();
        if (expected is null) Assert.Empty(current); else Assert.Equal(expected, Assert.Single(current).AssignedEmployeeId);
    }

    // ---- 1. channel normalization: the wire name Genesys sends -> the channel row the ticket is filed under ---------------------

    [Theory]
    [InlineData("Phone", GenesysChannel.Phone)]
    [InlineData("phone", GenesysChannel.Phone)]
    [InlineData("LiveChat", GenesysChannel.WebsiteChat)]
    [InlineData("Live Chat", GenesysChannel.WebsiteChat)]
    [InlineData("live_chat", GenesysChannel.WebsiteChat)]
    [InlineData("WebsiteChat", GenesysChannel.WebsiteChat)]
    [InlineData("WebMessaging", GenesysChannel.WebsiteChat)]
    [InlineData("WebChat", GenesysChannel.WebsiteChat)]
    [InlineData("WhatsApp", GenesysChannel.WhatsApp)]
    [InlineData("whatsapp", GenesysChannel.WhatsApp)]
    [InlineData("Instagram", GenesysChannel.Instagram)]
    [InlineData("INSTAGRAM", GenesysChannel.Instagram)]
    [InlineData("Facebook", GenesysChannel.Facebook)]
    [InlineData("FacebookMessenger", GenesysChannel.Facebook)]
    [InlineData("Facebook Messenger", GenesysChannel.Facebook)]
    [InlineData("Messenger", GenesysChannel.Facebook)]
    [InlineData("SocialMedia", GenesysChannel.SocialMedia)]
    [InlineData("Twitter", GenesysChannel.SocialMedia)]
    [InlineData("Telegram", GenesysChannel.SocialMedia)]
    public void TheWireChannelName_IsNormalizedToItsOwnChannel(string wire, GenesysChannel expected)
    {
        Assert.True(GenesysContractMapper.TryParseChannel(wire, out var channel));
        Assert.Equal(expected, channel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Carrier Pigeon")]
    [InlineData("5")]
    [InlineData("Instagram,Facebook")]
    public void AnUnknownWireChannel_IsRejected_NeverGuessed(string? wire) =>
        Assert.False(GenesysContractMapper.TryParseChannel(wire, out _));

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task EveryChannel_IsFiledUnderItsOwnCatalogueChannel_ByCode(GenesysChannel channel, byte expectedChannelId, string expectedCode)
    {
        Assert.Equal(expectedCode, GenesysChannelResolver.CodeFor(channel));

        var w = Build();
        var ticketId = await IngestAsync(w, $"conv-{channel}", channel);

        var interaction = w.F.Interactions.All.Single(i => i.TicketId == ticketId);
        Assert.Equal(expectedChannelId, interaction.ChannelId);
        Assert.True(interaction.IsOriginatingInteraction);   // the flag the Tickets list's Channel filter keys on
    }

    // ---- 2. an agent who ACCEPTS owns the ticket -------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AgentReportedConnected_OwnsTheTicket_DirectlyInTheCallCenter_NoRoundRobin(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-connect", channel);

        var result = await ConnectAsync(w, ticketId, "c-connect", AnsweringGenesysId);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).OriginatingDepartmentId);
        AssertOneOwner(w, ticketId, w.Answering);
        Assert.True(w.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null));
    }

    // ---- 3. merely offered / queued is NOT acceptance --------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task TopLevelAgentIdAtCreation_IsNotAcceptance_OnAnyDigitalChannel(GenesysChannel channel)
    {
        var w = Build();

        // Offered/queued agent, no human requested: stays an AI conversation, untouched.
        var offered = await IngestAsync(w, "c-offered", channel, agentId: AnsweringGenesysId);
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(offered).CurrentDepartmentId);
        AssertOneOwner(w, offered, null);

        // Offered agent AND a human is required: Call Center + Round Robin (the offered agent is not special-cased)...
        var required = await IngestAsync(w, "c-required", channel, agentId: AnsweringGenesysId, humanFollowUp: true);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(required).CurrentDepartmentId);
        Assert.Contains(w.Ticket(required).CurrentOwnerEmployeeId!.Value, w.RoundRobinAgents);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == required.ToString());

        // ...and only an update that reports the agent CONNECTED hands it to them.
        await ConnectAsync(w, required, "c-required", AnsweringGenesysId);
        AssertOneOwner(w, required, w.Answering);
    }

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task AnAgentOnlyAssignedByTheHandoff_IsNotAcceptance_RoundRobinStillApplies(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-handoff", channel);

        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(
            "c-handoff", Handoff: new GenesysHandoffUpdateDto(Required: true, AgentAvailable: true, Mode: "HumanTakeover")));

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        Assert.Contains(w.Ticket(ticketId).CurrentOwnerEmployeeId!.Value, w.RoundRobinAgents);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

    // ---- 4. human required, nobody accepted -> Round Robin ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task HumanRequiredAtCreation_NobodyAccepted_GoesToCallCenter_AndRoundRobinWalksTheAgents(GenesysChannel channel)
    {
        var w = Build();

        var first = await IngestAsync(w, "c-rr-1", channel, humanFollowUp: true);
        var second = await IngestAsync(w, "c-rr-2", channel, humanFollowUp: true);
        var third = await IngestAsync(w, "c-rr-3", channel, humanFollowUp: true);

        var owners = new[] { first, second, third }.Select(id => w.Ticket(id).CurrentOwnerEmployeeId!.Value).ToArray();
        Assert.All(new[] { first, second, third }, id => Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(id).CurrentDepartmentId));
        Assert.Equal(3, owners.Distinct().Count());          // three eligible agents, three consecutive tickets: strictly one each
        Assert.All(owners, o => Assert.Contains(o, w.RoundRobinAgents));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task HumanRequiredReportedLater_RoutesTheTicket_ToCallCenter_WithOneAgent(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-late", channel);
        AssertOneOwner(w, ticketId, null);

        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto("c-late", HumanFollowUpRequired: true));

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        Assert.Contains(w.Ticket(ticketId).CurrentOwnerEmployeeId!.Value, w.RoundRobinAgents);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent);
    }

    // ---- 5. AI-only is preserved ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AiOnlyConversation_IsNeverMovedOrAssigned(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-ai", channel);

        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto("c-ai", Routing: new GenesysRoutingUpdateDto(QueueId: "q", QueueName: "Queue")));
        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto("c-ai", HumanFollowUpRequired: false));
        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto("c-ai", CustomerActive: true));

        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        AssertOneOwner(w, ticketId, null);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action is "Transfer" or "AutoAssign" or "AssignOnAnswer");
    }

    // ---- 6. existing ownership and completed transfers are never undone --------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanOwner_IsNeverOverwritten_ByALaterConnectedAgent(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-owned", channel, humanFollowUp: true);
        var rotationOwner = w.Ticket(ticketId).CurrentOwnerEmployeeId!.Value;
        var other = w.RoundRobinAgents.First(a => a != rotationOwner && a != w.Answering);

        // Another agent takes the ticket themselves (a human act, not an automatic pre-assignment).
        var ticket = w.Ticket(ticketId);
        w.F.TicketAssignments.Added.Single(a => a.TicketId == ticketId && a.IsCurrent).MarkSuperseded();
        ticket.AssignTo(other);
        await w.F.TicketAssignments.AddAsync(
            new TicketAssignment(ticketId, other, w.F.CallCenter!.DepartmentId, DateTime.UtcNow, assigningActorEmployeeId: other));

        await ConnectAsync(w, ticketId, "c-owned", AnsweringGenesysId);

        AssertOneOwner(w, ticketId, other);
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ACompletedDepartmentTransfer_IsNeverReversed_ByALaterConnectedAgentOrFlag(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-transfer", channel);
        await ConnectAsync(w, ticketId, "c-transfer", AnsweringGenesysId);
        AssertOneOwner(w, ticketId, w.Answering);

        // The Call Center agent transfers it onward to the originating department; then Genesys keeps reporting.
        w.Ticket(ticketId).TransferToDepartment(w.Cs.DepartmentId);

        await ConnectAsync(w, ticketId, "c-transfer", AnsweringGenesysId);
        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto("c-transfer", HumanFollowUpRequired: true));

        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task RedeliveredConnectedEvent_IsIdempotent(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-idem", channel);
        await ConnectAsync(w, ticketId, "c-idem", AnsweringGenesysId);
        var rows = w.F.TicketAssignments.Added.Count;

        await ConnectAsync(w, ticketId, "c-idem", AnsweringGenesysId);

        AssertOneOwner(w, ticketId, w.Answering);
        Assert.Equal(rows, w.F.TicketAssignments.Added.Count);
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConnectedAgentWhoCannotTakeIt_LeavesItQueuedInCallCenter_NeverGivenToSomeoneElse(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "c-cannot", channel, humanFollowUp: null);
        // Mapped but not a Call Center member: a real person, so the ticket is human-managed, but they cannot own a Call Center ticket.
        w.F.AddAgent("g-finance-person", "Finance Person", inCallCenter: false, roles: [Roles.DepartmentEmployee]);

        await ConnectAsync(w, ticketId, "c-cannot", "g-finance-person");

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        AssertOneOwner(w, ticketId, null);
        Assert.Contains(w.F.WorkflowEvents.All, e => e.TicketId == ticketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }
}
