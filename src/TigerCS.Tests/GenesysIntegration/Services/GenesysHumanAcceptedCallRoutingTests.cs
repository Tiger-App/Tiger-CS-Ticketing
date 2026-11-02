using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Regression for TG-ACC-20261102-0021: a Genesys PHONE ticket created at pickup, with no <c>humanFollowUpRequired</c>, stayed Open and
/// unassigned in Accounting. A call a person accepted is now recognised from the actual interaction events (a mapped agent at pickup, an
/// agent CONNECTED, an agent taking pending work, a HumanAgent line) and routed to the Call Center with one Round Robin agent — at creation
/// and on any later update — while an AI-handled conversation, with no such evidence, is never moved.
/// </summary>
public class GenesysHumanAcceptedCallRoutingTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string HumanGenesysId = "genesys-user-human-1";

    private sealed record Scenario(GenesysServiceFixture F, Department Accounting, Guid[] CallCenterAgents);

    private static Scenario Build()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var accounting = f.Departments.AddDepartment("Accounting", "ACC");
        var agents = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToArray();
        foreach (var id in agents)
        {
            f.DepartmentAssignments.Assignments.Add(
                new UserDepartmentAssignment(id, f.CallCenter!.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        f.MapAgent(HumanGenesysId);
        return new Scenario(f, accounting, agents);
    }

    private static async Task<Ticket> IngestAsync(
        Scenario s, string conversationId, GenesysChannel channel, string? agentId = null, bool? humanFollowUp = null)
    {
        var result = await s.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, channel, CustomerPhone: "+971500000001", DepartmentId: s.Accounting.DepartmentId,
                AgentId: agentId, HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return s.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    private static Task<GenesysTicketUpdateResult> UpdateAsync(Scenario s, Ticket ticket, GenesysTicketUpdateDto dto) =>
        s.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, dto);

    private static void AssertRoutedToCallCenterWithOneAgent(Scenario s, Ticket ticket)
    {
        Assert.Equal(s.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(s.Accounting.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Contains(ticket.CurrentOwnerEmployeeId!.Value, s.CallCenterAgents);
        Assert.Single(s.F.TicketAssignments.Added, a => a.TicketId == ticket.TicketId && a.IsCurrent);
    }

    // ---- creation ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PhoneCallCreatedAtPickupByAMappedAgent_EntersCallCenter_WithOneAgent_AndIsFlaggedHuman()
    {
        var s = Build();

        var ticket = await IngestAsync(s, "ph-human", GenesysChannel.Phone, agentId: HumanGenesysId);

        AssertRoutedToCallCenterWithOneAgent(s, ticket);
        var interaction = s.F.Interactions.All.Single(i => i.TicketId == ticket.TicketId);
        Assert.True(interaction.RequiresHumanFollowUp);
        Assert.Contains(s.F.Audit.Entries, e => e.Action == "GenesysInquiryIngested" && e.AfterValue!.Contains("HumanSignal=PhonePickupByMappedAgent"));
    }

    [Fact]
    public async Task PhoneCallWithNoAgent_ReachedByAVirtualAgentOnly_StaysWhereItWas()
    {
        var s = Build();

        var ticket = await IngestAsync(s, "ph-ai", GenesysChannel.Phone, agentId: null);

        Assert.Equal(s.Accounting.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.False(s.F.Interactions.All.Single(i => i.TicketId == ticket.TicketId).RequiresHumanFollowUp);
    }

    [Fact]
    public async Task PhoneCallNamingAnUnmappedAgent_IsNotGuessedToBeHuman()
    {
        var s = Build();

        // An unmapped id may be a bot or virtual agent: nothing is inferred from it, and an active AI call is not rerouted.
        var ticket = await IngestAsync(s, "ph-unmapped", GenesysChannel.Phone, agentId: "some-bot-or-unmapped-id");

        Assert.Equal(s.Accounting.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Theory]
    [InlineData(GenesysChannel.WebsiteChat)]
    [InlineData(GenesysChannel.WhatsApp)]
    [InlineData(GenesysChannel.SocialMedia)]
    public async Task DigitalConversationNamingAMappedAgentAtCreation_IsNotYetHuman_UntilAgentIsReportedConnected(GenesysChannel channel)
    {
        var s = Build();

        // At creation a digital agentId may be only the offered/ringing agent — the connected signal is routing.agentId on an update.
        var ticket = await IngestAsync(s, "dg-offered", channel, agentId: HumanGenesysId);

        Assert.Equal(s.Accounting.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    // ---- late human follow-up (updates) --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(GenesysChannel.Phone)]
    [InlineData(GenesysChannel.WebsiteChat)]
    [InlineData(GenesysChannel.WhatsApp)]
    public async Task MappedAgentReportedConnectedOnAnUpdate_RoutesTheAiCreatedTicket_ToCallCenter_WithOneAgent(GenesysChannel channel)
    {
        var s = Build();
        var ticket = await IngestAsync(s, "late-connect", channel);
        Assert.Equal(s.Accounting.DepartmentId, ticket.CurrentDepartmentId);

        var result = await UpdateAsync(s, ticket, new GenesysTicketUpdateDto(
            "late-connect", Routing: new GenesysRoutingUpdateDto(QueueId: "q-1", QueueName: "Queue", AgentId: HumanGenesysId, AgentName: "Agent")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        AssertRoutedToCallCenterWithOneAgent(s, ticket);
        Assert.True(s.F.Interactions.All.Single(i => i.TicketId == ticket.TicketId).RequiresHumanFollowUp);
        Assert.Contains(s.F.Audit.Entries, e => e.Action == "GenesysHumanFollowUpRequired" && e.AfterValue!.Contains("HumanSignal=MappedAgentConnected"));
    }

    [Fact]
    public async Task HumanAgentTranscriptLine_RoutesTheTicket_ButVirtualAgentLinesDoNot()
    {
        var s = Build();
        var bot = await IngestAsync(s, "tx-bot", GenesysChannel.WebsiteChat);
        var person = await IngestAsync(s, "tx-human", GenesysChannel.WebsiteChat);
        var at = DateTime.UtcNow;

        await UpdateAsync(s, bot, new GenesysTicketUpdateDto(
            "tx-bot", Transcript: [new GenesysTranscriptMessageDto("VirtualAgent", at, "How can I help?", ExternalMessageId: "m1")]));
        await UpdateAsync(s, person, new GenesysTicketUpdateDto(
            "tx-human", Transcript: [new GenesysTranscriptMessageDto("HumanAgent", at, "Hello, this is Sara.", ExternalMessageId: "m2")]));

        Assert.Equal(s.Accounting.DepartmentId, bot.CurrentDepartmentId);
        Assert.Null(bot.CurrentOwnerEmployeeId);
        AssertRoutedToCallCenterWithOneAgent(s, person);
    }

    [Fact]
    public async Task ActiveAiConversation_IsNotRerouted_ByQueueChange_UnmappedAgent_OrCustomerActivity()
    {
        var s = Build();
        var ticket = await IngestAsync(s, "ai-active", GenesysChannel.WebsiteChat);

        await UpdateAsync(s, ticket, new GenesysTicketUpdateDto("ai-active", Routing: new GenesysRoutingUpdateDto(QueueId: "q-2", QueueName: "Another queue")));
        await UpdateAsync(s, ticket, new GenesysTicketUpdateDto("ai-active", Routing: new GenesysRoutingUpdateDto(AgentId: "unmapped-agent")));
        await UpdateAsync(s, ticket, new GenesysTicketUpdateDto("ai-active", CustomerActive: true, AwaitingCustomerReply: true));
        await UpdateAsync(s, ticket, new GenesysTicketUpdateDto("ai-active", Handoff: new GenesysHandoffUpdateDto(Required: false)));

        Assert.Equal(s.Accounting.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.False(s.F.Interactions.All.Single(i => i.TicketId == ticket.TicketId).RequiresHumanFollowUp);
        Assert.DoesNotContain(s.F.Audit.Entries, e => e.Action == "Transfer");
    }

    [Fact]
    public async Task RepeatedAgentConnectedUpdate_IsIdempotent()
    {
        var s = Build();
        var ticket = await IngestAsync(s, "idem", GenesysChannel.Phone);
        var dto = new GenesysTicketUpdateDto("idem", Routing: new GenesysRoutingUpdateDto(AgentId: HumanGenesysId));
        await UpdateAsync(s, ticket, dto);
        var owner = ticket.CurrentOwnerEmployeeId;
        var assignments = s.F.TicketAssignments.Added.Count;

        await UpdateAsync(s, ticket, dto);

        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(assignments, s.F.TicketAssignments.Added.Count);
    }

    [Fact]
    public async Task TicketTransferredOnwardByACallCenterAgent_IsNotPulledBackByALaterUpdate()
    {
        var s = Build();
        var ticket = await IngestAsync(s, "no-pullback", GenesysChannel.Phone, agentId: HumanGenesysId);
        AssertRoutedToCallCenterWithOneAgent(s, ticket);

        // The Call Center agent transfers it onward to Accounting (what the Transfer action does), then Genesys reports the call ending
        // with the agent still connected.
        ticket.TransferToDepartment(s.Accounting.DepartmentId);
        var owner = ticket.CurrentOwnerEmployeeId;

        await UpdateAsync(s, ticket, new GenesysTicketUpdateDto(
            "no-pullback", Routing: new GenesysRoutingUpdateDto(AgentId: HumanGenesysId),
            Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "customer disconnected")));

        Assert.Equal(s.Accounting.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
    }
}
