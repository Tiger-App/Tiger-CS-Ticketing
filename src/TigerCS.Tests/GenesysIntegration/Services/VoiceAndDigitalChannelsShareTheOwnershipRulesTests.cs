using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// <b>Voice and WhatsApp (and every other channel) follow the SAME ownership rules</b> (PR #292, reconciled): the AI Agent owns what it handles first; asking for a person,
/// a handoff or an unresolved ending never triggers Round Robin; the ticket waits in the Call Center queue with the AI Agent still the owner; a person gets it only by
/// actually accepting / answering. Each scenario is run on every channel and the observable outcome must be identical.
/// </summary>
public class VoiceAndDigitalChannelsShareTheOwnershipRulesTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> Channels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WhatsApp, GenesysChannel.WebsiteChat, GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    private sealed record Outcome(bool InCallCenter, bool NoPersonOwner, bool AiStillOwner, bool NoPersonAssignment, bool RotationUntouched, bool Flagged);

    private static async Task<(GenesysServiceFixture F, Ticket T, (Guid Id, string GenesysId)[] Agents)> StartAsync(GenesysChannel channel, string conversation, bool humanFollowUp = false)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = Enumerable.Range(1, 3).Select(i => (Id: f.AddAgent($"g-agent-{i}", $"Agent {i}"), GenesysId: $"g-agent-{i}")).OrderBy(a => a.Id).ToArray();
        var r = await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            conversation, channel, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId, HumanFollowUpRequired: humanFollowUp ? true : null));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return (f, f.Tickets.All.Single(t => t.TicketId == r.Ticket!.TicketId), agents);
    }

    private static Outcome Observe(GenesysServiceFixture f, Ticket t, (Guid Id, string GenesysId)[] agents, string conversation) => new(
        InCallCenter: t.CurrentDepartmentId == f.CallCenter!.DepartmentId,
        NoPersonOwner: t.CurrentOwnerEmployeeId is null,
        AiStillOwner: t.AiAgentOwnerEmployeeId == ServiceAccount,
        NoPersonAssignment: !f.TicketAssignments.Added.Any(a => a.TicketId == t.TicketId && agents.Any(x => x.Id == a.AssignedEmployeeId)),
        RotationUntouched: f.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null),
        Flagged: f.InteractionFor(conversation)!.RequiresHumanFollowUp);

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task HumanFollowUpAtCreation_QueuesWithTheAiOwner_NoRoundRobin_OnEveryChannel(GenesysChannel channel)
    {
        var (f, t, agents) = await StartAsync(channel, "c-follow-up", humanFollowUp: true);

        Assert.Equal(new Outcome(true, true, true, true, true, true), Observe(f, t, agents, "c-follow-up"));
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task AHandoffRequest_QueuesWithTheAiOwner_NoRoundRobin_OnEveryChannel(GenesysChannel channel)
    {
        var (f, t, agents) = await StartAsync(channel, "c-handoff");

        await f.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-handoff", Handoff: new GenesysHandoffUpdateDto(Required: true, AgentAvailable: false, Mode: "HumanTakeover")));

        var o = Observe(f, t, agents, "c-handoff");
        Assert.True(o.InCallCenter && o.NoPersonOwner && o.AiStillOwner && o.NoPersonAssignment && o.RotationUntouched);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task TheAgentWhoActuallyAccepted_OwnsIt_OnEveryChannel_AndNobodyElseIsDealt(GenesysChannel channel)
    {
        var (f, t, agents) = await StartAsync(channel, "c-accept", humanFollowUp: true);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-accept", Routing: new GenesysRoutingUpdateDto(AgentId: agents[1].GenesysId, ConnectedAtUtc: DateTime.UtcNow.AddSeconds(-5))));

        Assert.Equal(agents[1].Id, t.CurrentOwnerEmployeeId);
        Assert.Single(f.TicketAssignments.Added, a => a.TicketId == t.TicketId && a.IsCurrent);
        Assert.True(f.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null));
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task ARealAgentWhoCannotTakeIt_FlagsTheTicket_AndNoFallbackAgentIsDealt_OnEveryChannel(GenesysChannel channel)
    {
        var (f, t, agents) = await StartAsync(channel, "c-unusable", humanFollowUp: true);
        var employee = f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);   // mapped, but not a CS Agent

        await f.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-unusable", Routing: new GenesysRoutingUpdateDto(AgentId: "g-emp", ConnectedAtUtc: DateTime.UtcNow.AddSeconds(-5))));

        Assert.NotEqual(employee, t.CurrentOwnerEmployeeId);
        Assert.Null(t.CurrentOwnerEmployeeId);                                                   // never another employee just because this one cannot take it
        Assert.True(f.TicketAssignments.Added.All(a => a.TicketId != t.TicketId || agents.All(x => x.Id != a.AssignedEmployeeId)));
        Assert.Contains(f.WorkflowEvents.All, e => e.TicketId == t.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task ALateEventOnAClosedTicket_ChangesNothing_OnEveryChannel(GenesysChannel channel)
    {
        var (f, t, agents) = await StartAsync(channel, "c-closed");
        var closed = await f.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto("c-closed", ClosedByAi: true));
        Assert.True(closed.ClosedByAi);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto("c-closed", HumanFollowUpRequired: true));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-closed", Routing: new GenesysRoutingUpdateDto(AgentId: agents[0].GenesysId, ConnectedAtUtc: DateTime.UtcNow.AddSeconds(-5))));

        Assert.Equal(TicketStatus.Closed, t.TicketStatus);
        Assert.Null(t.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(f.TicketAssignments.Added, a => a.TicketId == t.TicketId);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task AnAiConversationThatEndedUnresolved_IsNeverRoundRobinned_OnEveryChannel(GenesysChannel channel)
    {
        var (f, t, agents) = await StartAsync(channel, "c-ended");

        await f.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, new GenesysTicketUpdateDto(
            "c-ended", Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "CustomerDisconnect")));

        // Digital channels flag and queue at once; a CALL is concluded by the voice reconciler after its window (the answering agent can still arrive after "ended").
        // On every channel nobody is chosen and the AI Agent stays the owner.
        var o = Observe(f, t, agents, "c-ended");
        Assert.True(o.NoPersonOwner && o.AiStillOwner && o.NoPersonAssignment && o.RotationUntouched);
    }
}
