using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The AI Agent is the ticket's ACTUAL assigned owner while it handles a conversation (persisted <c>AiAgentOwnerEmployeeId</c>), without changing what
/// "owned by a person" means anywhere else: <c>CurrentOwnerEmployeeId</c> stays the human owner, so Round Robin, Take Ownership, auto-close, the
/// reconciler and every "unassigned" bucket behave exactly as before. A person's assignment — acceptance, direct pickup, Round Robin, a department
/// move — ends the AI's ownership; closing a ticket keeps it.
/// </summary>
public class AiAgentOwnershipTests
{
    private static readonly Guid AiAgent = Guid.NewGuid();   // the Genesys integration account (the caller of every Genesys endpoint)

    private static (GenesysServiceFixture F, Department Cs) World(bool strict = true)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: strict);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        return (f, cs);
    }

    private static async Task<Ticket> IngestAsync(GenesysServiceFixture f, Department cs, string conv, GenesysChannel channel = GenesysChannel.WhatsApp, string? agent = null, bool? human = null)
    {
        var r = await f.Ingestion.IngestAsync(
            AiAgent, new GenesysInquiryDto(conv, channel, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId, AgentId: agent, HumanFollowUpRequired: human));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return f.Tickets.All.Single(t => t.TicketId == r.Ticket!.TicketId);
    }

    [Theory]
    [InlineData(GenesysChannel.WhatsApp)]
    [InlineData(GenesysChannel.WebsiteChat)]
    [InlineData(GenesysChannel.Phone)]
    public async Task AnAiFirstConversation_IsOwnedByTheAiAgent_OnEveryChannel(GenesysChannel channel)
    {
        var (f, cs) = World();

        var ticket = await IngestAsync(f, cs, "conv-ai", channel);

        Assert.Equal(AiAgent, ticket.AiAgentOwnerEmployeeId);
        Assert.Equal(AiAgent, ticket.AssignedOwnerEmployeeId);
        Assert.True(ticket.IsOwnedByAiAgent);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                       // no HUMAN owner: every existing rule sees what it always saw
        Assert.Contains(f.Audit.Entries, e => e.Action == "AssignToAiAgent" && e.EntityId == ticket.TicketId.ToString());
        Assert.Single(f.AssignmentHistory.Entries, h => h.Source == TicketAssignmentSource.GenesysAiAgent);
    }

    [Fact]
    public async Task ADirectHumanPickup_IsOwnedByThePerson_NotTheAi()
    {
        var (f, cs) = World();
        var agent = f.AddAgent("g-direct", "Direct Agent");

        var ticket = await IngestAsync(f, cs, "conv-direct", GenesysChannel.Phone, agent: "g-direct");

        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.AiAgentOwnerEmployeeId);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AssignToAiAgent");
    }

    [Fact]
    public async Task AConversationThatNeedsAPersonFromTheStart_WaitsInTheCallCenterQueue_StillOwnedByTheAi_NoRoundRobin()
    {
        var (f, cs) = World();
        f.AddAgent("g-rr", "RR Agent");

        var ticket = await IngestAsync(f, cs, "conv-human", human: true);

        Assert.Null(ticket.CurrentOwnerEmployeeId);                       // nobody is dealt the ticket
        Assert.Equal(AiAgent, ticket.AiAgentOwnerEmployeeId);             // the AI stays the owner until a person actually accepts
        Assert.Equal(f.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Empty(f.TicketAssignments.Added);
        Assert.All(f.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
    }

    [Fact]
    public async Task WhenAPersonAcceptsTheConversation_TheAiStopsBeingTheOwner_AtOnce()
    {
        var (f, cs) = World();
        var agent = f.AddAgent("g-accept", "Accepting Agent");
        var ticket = await IngestAsync(f, cs, "conv-accept");
        Assert.True(ticket.IsOwnedByAiAgent);

        await f.TicketUpdate.UpdateAsync(AiAgent, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-accept", Routing: new GenesysRoutingUpdateDto(AgentId: "g-accept", ConnectedAtUtc: DateTime.UtcNow)));

        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.AiAgentOwnerEmployeeId);
        Assert.False(ticket.IsOwnedByAiAgent);
        Assert.Equal(agent, ticket.AssignedOwnerEmployeeId);
    }

    [Fact]
    public async Task WhenTheAiOwnedTicketIsAlreadyInTheCallCenter_AcceptanceEndsTheAiOwnership_ThroughTheAssignmentItself()
    {
        // No department move happens here (a move would clear the AI owner on its own), so only the person's assignment can end the AI's ownership.
        var (f, _) = World();
        var agent = f.AddAgent("g-in-cc", "Call Center Agent");
        var ticket = await IngestAsync(f, f.CallCenter!, "conv-in-cc");
        Assert.True(ticket.IsOwnedByAiAgent);
        Assert.Equal(f.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);

        await f.TicketUpdate.UpdateAsync(AiAgent, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-in-cc", Routing: new GenesysRoutingUpdateDto(AgentId: "g-in-cc", ConnectedAtUtc: DateTime.UtcNow)));

        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.AiAgentOwnerEmployeeId);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "Transfer");
    }

    [Fact]
    public async Task AnOfferedAgent_DoesNotEndTheAiOwnership()
    {
        var (f, cs) = World();
        var ticket = await IngestAsync(f, cs, "conv-offer");
        f.AddAgent("g-offered", "Offered Agent");

        // Genesys names an agent on a handoff that nobody has accepted: not an acceptance.
        await f.TicketUpdate.UpdateAsync(AiAgent, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-offer", AgentId: "g-offered", Handoff: new GenesysHandoffUpdateDto(AssignedAgentId: "g-offered")));

        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AssignOnAnswer");
    }

    [Fact]
    public async Task ADelayedAiEvent_NeverTakesOwnershipBackFromAPerson()
    {
        var (f, cs) = World();
        var agent = f.AddAgent("g-keep", "Keeper");
        var ticket = await IngestAsync(f, cs, "conv-keep");
        await f.TicketUpdate.UpdateAsync(AiAgent, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-keep", Routing: new GenesysRoutingUpdateDto(AgentId: "g-keep", ConnectedAtUtc: DateTime.UtcNow)));

        // A late AI-only transcript and a replayed create.
        await f.TicketUpdate.UpdateAsync(AiAgent, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-keep", Transcript: [new GenesysTranscriptMessageDto("VirtualAgent", DateTime.UtcNow.AddMinutes(-9), "late", "Bot", ExternalMessageId: "x1")]));
        await f.Ingestion.IngestAsync(AiAgent, new GenesysInquiryDto("conv-keep", GenesysChannel.WhatsApp, CustomerPhone: "+971500000001"));
        var recorded = await f.AutoAssignment.RecordAiAgentAssignmentAsync(ticket, AiAgent, "conv-keep", null, DateTime.UtcNow);

        Assert.False(recorded);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.AiAgentOwnerEmployeeId);
    }

    [Fact]
    public async Task ClosingAnAiHandledTicket_KeepsTheAiAsItsOwner_AndALateEventChangesNothing()
    {
        var (f, cs) = World();
        f.AddAgent("g-late", "Late Agent");
        var ticket = await IngestAsync(f, cs, "conv-closed-ai");
        var closed = await f.TicketUpdate.UpdateAsync(AiAgent, ticket.TicketId, new GenesysTicketUpdateDto("conv-closed-ai", ClosedByAi: true));
        Assert.True(closed.ClosedByAi, closed.ClosedByAiNote);

        await f.TicketUpdate.UpdateAsync(AiAgent, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-closed-ai", Routing: new GenesysRoutingUpdateDto(AgentId: "g-late", ConnectedAtUtc: DateTime.UtcNow)));

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(AiAgent, ticket.AiAgentOwnerEmployeeId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    // ---- domain rules ----

    private static Ticket NewTicket() => Ticket.CreateUnclassified("TG-T-1", 2, "x", DateTime.UtcNow, defaultPriorityId: 3);

    [Fact]
    public void Domain_AiOwnership_YieldsToAnyAssignment_AndNeverStartsOnAHumanOwnedTicket()
    {
        var ticket = NewTicket();
        Assert.True(ticket.AssignToAiAgent(AiAgent));
        Assert.False(ticket.AssignToAiAgent(AiAgent));            // idempotent

        var person = Guid.NewGuid();
        ticket.AssignTo(person);
        Assert.Null(ticket.AiAgentOwnerEmployeeId);
        Assert.False(ticket.AssignToAiAgent(AiAgent));            // a person owns it: the AI cannot take it back
        Assert.Equal(person, ticket.AssignedOwnerEmployeeId);
    }

    [Fact]
    public void Domain_MovingTheTicketToADepartmentQueue_EndsTheAiOwnership()
    {
        var ticket = NewTicket();
        ticket.AssignToAiAgent(AiAgent);

        ticket.TransferToDepartment(5);

        Assert.Null(ticket.AiAgentOwnerEmployeeId);
        Assert.Null(ticket.AssignedOwnerEmployeeId);
    }
}
