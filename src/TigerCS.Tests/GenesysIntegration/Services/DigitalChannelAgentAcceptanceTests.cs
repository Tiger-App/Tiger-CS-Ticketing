using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// "Whoever accepts owns it" holds on EVERY Genesys channel, not just Phone: Website Chat, WhatsApp and Social DM (Genesys normalizes Instagram
/// and Facebook messaging to <see cref="GenesysChannel.SocialMedia"/>). A mapped CS Agent reported as CONNECTED (routing.agentId) gets the ticket
/// directly - no Round Robin turn is spent. An accepting agent who is not a CS Agent (or otherwise cannot take it) leaves the ticket in the Call
/// Center queue, flagged. Nobody accepted but a person is required: Round Robin among CS Agents. AI-only chats and existing owners are untouched.
/// </summary>
public class DigitalChannelAgentAcceptanceTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> DigitalChannels => new()
    {
        GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.SocialMedia
    };

    private sealed record World(GenesysServiceFixture F, Department Cs, (Guid Id, string GenesysId)[] Agents)
    {
        /// <summary>The agent Round Robin would pick next.</summary>
        public (Guid Id, string GenesysId) RoundRobinNext => Agents[0];

        /// <summary>Never the next in the rotation, so a direct assignment cannot be mistaken for Round Robin.</summary>
        public (Guid Id, string GenesysId) Accepting => Agents[2];

        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
    }

    private static World NewWorld()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = Enumerable.Range(1, 3)
            .Select(i => (GenesysId: $"g-digital-{i}", Id: f.AddAgent($"g-digital-{i}", $"Agent {i}")))
            .OrderBy(a => a.Id)
            .Select(a => (a.Id, a.GenesysId))
            .ToArray();
        return new World(f, cs, agents);
    }

    private static async Task<long> IngestAsync(World w, GenesysChannel channel, string conversationId, string? agentId = null, bool? humanFollowUp = null)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, channel, CustomerPhone: "+971500000002", DepartmentId: w.Cs.DepartmentId,
                AgentId: agentId, HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return result.Ticket!.TicketId;
    }

    private static Task<GenesysTicketUpdateResult> AcceptAsync(World w, long ticketId, string conversationId, string genesysAgentId) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(conversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: DateTime.UtcNow)));

    private static bool CursorUntouched(World w) => w.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    private static void AssertSingleOwner(World w, long ticketId, Guid? expectedOwner)
    {
        Assert.Equal(expectedOwner, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        var current = w.F.TicketAssignments.Added.Where(a => a.TicketId == ticketId && a.IsCurrent).ToList();
        if (expectedOwner is null)
        {
            Assert.Empty(current);
        }
        else
        {
            Assert.Equal(expectedOwner, Assert.Single(current).AssignedEmployeeId);
        }
    }

    private static int FlagCount(World w, long ticketId) =>
        w.F.WorkflowEvents.All.Count(e => e.TicketId == ticketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task AiChat_ThenMappedCsAgentAccepts_IsAssignedDirectly_InTheCallCenter_WithoutARoundRobinTurn(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-accept");
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);   // AI-only so far: untouched, unowned
        AssertSingleOwner(w, ticketId, null);

        var result = await AcceptAsync(w, ticketId, $"conv-{channel}-accept", w.Accepting.GenesysId);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).OriginatingDepartmentId);
        AssertSingleOwner(w, ticketId, w.Accepting.Id);
        Assert.NotEqual(w.RoundRobinNext.Id, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.True(CursorUntouched(w));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString());
    }

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task ChatCreatedWithAHumanAlreadyAccepted_IsAssignedDirectlyAtCreation(GenesysChannel channel)
    {
        var w = NewWorld();

        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-created", w.Accepting.GenesysId, humanFollowUp: true);

        AssertSingleOwner(w, ticketId, w.Accepting.Id);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        Assert.True(CursorUntouched(w));
    }

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task AgentNamedButNotConnected_AiChatStaysProtected_NoOwner_NoRoundRobin(GenesysChannel channel)
    {
        var w = NewWorld();

        // agentId on a digital inquiry without humanFollowUpRequired is not an acceptance: the AI still owns the conversation.
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-named", w.Accepting.GenesysId);

        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        AssertSingleOwner(w, ticketId, null);
        Assert.True(CursorUntouched(w));
        Assert.Equal(0, FlagCount(w, ticketId));
    }

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task AcceptingAgentWhoIsNotACsAgent_LeavesTheTicketInTheCallCenterQueue_Flagged_NoRoundRobin(GenesysChannel channel)
    {
        var w = NewWorld();
        // A Call Center member with an operational role - but not CS Agent. Call Center tickets belong to CS Agents only.
        w.F.AddAgent("g-dept-employee", "Dept Employee", roles: [Roles.DepartmentEmployee]);
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-ineligible");

        await AcceptAsync(w, ticketId, $"conv-{channel}-ineligible", "g-dept-employee");

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        AssertSingleOwner(w, ticketId, null);
        Assert.Equal(1, FlagCount(w, ticketId));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.AfterValue!.Contains("Issue=NotCsAgent"));
        Assert.True(CursorUntouched(w));
        Assert.DoesNotContain(w.F.Tickets.All, t => w.Agents.Any(a => a.Id == t.CurrentOwnerEmployeeId));
    }

    [Fact]
    public async Task PhoneCallAnsweredByANonCsAgent_IsFlagged_NotAssigned()
    {
        var w = NewWorld();
        w.F.AddAgent("g-dept-head", "Dept Head", roles: [Roles.DepartmentHead]);

        var ticketId = await IngestAsync(w, GenesysChannel.Phone, "conv-phone-head", "g-dept-head");

        AssertSingleOwner(w, ticketId, null);
        Assert.Equal(1, FlagCount(w, ticketId));
        Assert.True(CursorUntouched(w));
    }

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task HumanRequiredButNobodyAccepted_UsesRoundRobin_AmongCsAgentsOnly(GenesysChannel channel)
    {
        var w = NewWorld();
        w.F.AddAgent("g-cc-dept-employee", "CC Dept Employee", roles: [Roles.DepartmentEmployee]);   // in the Call Center, never dealt a ticket
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-rr");

        var handoff = await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto($"conv-{channel}-rr", AgentAvailable: false, Mode: "HumanTakeover"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        AssertSingleOwner(w, ticketId, w.RoundRobinNext.Id);
        Assert.Contains(w.Agents, a => a.Id == w.Ticket(ticketId).CurrentOwnerEmployeeId);
    }

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task ExistingHumanOwner_IsNeverOverwritten_WhenAnotherAgentAccepts(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-owned", w.Agents[1].GenesysId, humanFollowUp: true);
        AssertSingleOwner(w, ticketId, w.Agents[1].Id);

        await AcceptAsync(w, ticketId, $"conv-{channel}-owned", w.Accepting.GenesysId);

        AssertSingleOwner(w, ticketId, w.Agents[1].Id);
    }

    [Theory]
    [MemberData(nameof(DigitalChannels))]
    public async Task RedeliveredAcceptance_ChangesNothing(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-dup");

        await AcceptAsync(w, ticketId, $"conv-{channel}-dup", w.Accepting.GenesysId);
        await AcceptAsync(w, ticketId, $"conv-{channel}-dup", w.Accepting.GenesysId);

        AssertSingleOwner(w, ticketId, w.Accepting.Id);
        Assert.Single(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

    [Fact]
    public async Task UnmappedIdOnADigitalChat_IsTreatedAsABot_NothingMoves()
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, GenesysChannel.WhatsApp, "conv-bot");

        await AcceptAsync(w, ticketId, "conv-bot", "virtual-agent-bot");

        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        AssertSingleOwner(w, ticketId, null);
        Assert.Equal(0, FlagCount(w, ticketId));
    }
}
