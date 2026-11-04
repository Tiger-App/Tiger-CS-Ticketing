using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Ownership between the Genesys agent who ACTUALLY handles an interaction and the TigerCS ticket.
///
/// Precedence: (1) an authorized manual assignment / supervisor override, (2) the confirmed Genesys handling agent (<c>routing.agentId</c> =
/// connected; a phone inquiry's <c>agentId</c>; a mapped agent's <c>HumanAgent</c> transcript line), (3) the existing eligible CS Agent,
/// (4) the shared sequential round robin. What Genesys does NOT deliver today - a transfer's offered / accepted / failed / cancelled state and
/// whether the sender stays on the call (consult, conference) - is never guessed: a second connected agent is recorded as a handler and a visible
/// call-transfer event, and the owner only changes through a person (Assign / Transfer) until Genesys sends that state.
/// </summary>
public class GenesysOwnershipLifecycleTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

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

    private static async Task<(long TicketId, string ConversationId)> ChatAsync(World w, string conversationId, bool humanFollowUp = false)
    {
        var r = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId,
                HumanFollowUpRequired: humanFollowUp ? true : null));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return (r.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, (long TicketId, string ConversationId) t, string genesysAgentId, DateTime? at = null) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, t.TicketId,
            new GenesysTicketUpdateDto(t.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: at ?? DateTime.UtcNow)));

    private static Task<GenesysTicketUpdateResult> UpdateAsync(World w, (long TicketId, string ConversationId) t, Func<string, GenesysTicketUpdateDto> build) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, build(t.ConversationId));

    private static List<TicketAssignment> Current(World w, long id) => w.F.TicketAssignments.Added.Where(a => a.TicketId == id && a.IsCurrent).ToList();

    private static void AssertOwner(World w, long id, Guid? owner)
    {
        Assert.Equal(owner, w.Ticket(id).CurrentOwnerEmployeeId);
        if (owner is null) Assert.Empty(Current(w, id));
        else Assert.Equal(owner, Assert.Single(Current(w, id)).AssignedEmployeeId);   // exactly one current assignment, and it is the owner
    }

    private TicketAssignmentAppService AssignmentService(World w) =>
        new(w.F.Tickets, w.F.TicketAssignments, w.F.DepartmentAssignments, w.F.Departments, w.F.UnitOfWork, w.F.Audit, TimeProvider.System,
            w.F.DepartmentSettings, w.F.AutoAssignment, w.F.WorkflowEvents, requestTypeRepository: w.F.RequestTypes);

    // ---- 1. Agent A answers and owns the ticket -------------------------------------------------------------------------------------

    [Fact]
    public async Task AgentAAnswers_OwnsTheTicket_NotTheRoundRobinNext()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-a", humanFollowUp: true);                 // human required, nobody accepted: provisional round robin owner
        var provisional = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId;
        var agentA = w.Agents.Last(a => a.Id != provisional);

        var result = await ConnectAsync(w, chat, agentA.GenesysId);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        AssertOwner(w, chat.TicketId, agentA.Id);
        Assert.Equal(agentA.Id, Current(w, chat.TicketId).Single().AssigningActorEmployeeId);          // the agent's own act, i.e. confirmed human ownership
    }

    // ---- 2. Round robin first, then Agent B accepts: the fallback is NOT a confirmed handler ------------------------------------------

    [Fact]
    public async Task TicketAssignedByRoundRobin_ThenAgentBAccepts_OwnershipSynchronizesToB_AndTheFallbackIsNeverRecordedAsAHandler()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-b", humanFollowUp: true);
        var fallback = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId!.Value;
        var agentB = w.Agents.First(a => a.Id != fallback);
        Assert.DoesNotContain(w.F.Handlers.All, h => h.TicketId == chat.TicketId);                      // nobody has accepted yet

        await ConnectAsync(w, chat, agentB.GenesysId);

        AssertOwner(w, chat.TicketId, agentB.Id);
        var handlers = w.F.Handlers.All.Where(h => h.TicketId == chat.TicketId).ToList();
        Assert.Equal(agentB.Id, Assert.Single(handlers).HandledByUserId);                              // only the agent who actually accepted
        Assert.DoesNotContain(handlers, h => h.HandledByUserId == fallback);
        Assert.Contains(w.F.TicketAssignments.Added, a => a.TicketId == chat.TicketId && a.AssignedEmployeeId == fallback && !a.IsCurrent);   // history kept, superseded
    }

    // ---- never infer acceptance from anything but a connected agent ------------------------------------------------------------------

    [Fact]
    public async Task OfferedRingingScreenPopOrAssignedAgent_IsNeverAnAcceptance()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-offer", humanFollowUp: true);
        var fallback = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId!.Value;
        var offered = w.Agents.First(a => a.Id != fallback);

        // Genesys names an agent as ASSIGNED / available (ringing, offered) - on the handoff and as the work item's assignee - but never "connected".
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Handoff: new GenesysHandoffUpdateDto(Required: true, AgentAvailable: true, AssignedAgentId: offered.GenesysId)));
        // A queue change without an agent, and a top-level agent context (display data), are not an acceptance either.
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Routing: new GenesysRoutingUpdateDto(QueueId: "q-1", QueueName: "Support")));
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, AgentId: offered.GenesysId, AgentName: "Offered Agent"));

        AssertOwner(w, chat.TicketId, fallback);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == chat.TicketId.ToString());
    }

    // ---- 3. AI and human handoff -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AiTransfersToAHuman_TheAgentWhoAcceptsOwnsIt()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-handoff");
        Assert.Null(w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);                                   // AI-handled: no agent needed yet

        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover", Trigger: "AiEscalated")));
        var provisional = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId;
        Assert.NotNull(provisional);                                                                   // human work with nobody accepted: round robin
        var acceptor = w.Agents.First(a => a.Id != provisional);

        await ConnectAsync(w, chat, acceptor.GenesysId);

        AssertOwner(w, chat.TicketId, acceptor.Id);
    }

    [Fact]
    public async Task AiEndsWithoutResolvingOrClosing_TheTicketIsRoutedForHumanHandling_NotLeftUnassignedInItsDepartment()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-ai-ended");

        var ended = await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "CustomerDisconnect")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, ended.Outcome);
        var ticket = w.Ticket(chat.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);                        // Call Center first
        Assert.Equal(w.Cs.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.NotNull(ticket.CurrentOwnerEmployeeId);                                                 // provisional shared round robin owner
        Assert.Single(Current(w, chat.TicketId));

        // Redelivery of the same end changes nothing (no second assignment, no second turn of the rotation).
        var rotationBefore = w.F.Rotation.Rows.Values.Select(r => r.LastAssignedEmployeeId).ToList();
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "CustomerDisconnect")));
        Assert.Single(Current(w, chat.TicketId));
        Assert.Equal(rotationBefore, w.F.Rotation.Rows.Values.Select(r => r.LastAssignedEmployeeId).ToList());
    }

    [Fact]
    public async Task AiClosedOrResolvedTickets_NeedNoCsAgent_AndAnEndWhileAwaitingTheCustomerDoesNotRouteEither()
    {
        var w = NewWorld();

        // Closed by the AI in the same update that ends the conversation.
        var closed = await ChatAsync(w, "c-ai-closed");
        var r = await UpdateAsync(w, closed, cid => new GenesysTicketUpdateDto(cid, ClosedByAi: true, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "Completed")));
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, r.Outcome);
        Assert.Equal(TicketStatus.Closed, w.Ticket(closed.TicketId).TicketStatus);
        Assert.Null(w.Ticket(closed.TicketId).CurrentOwnerEmployeeId);
        Assert.Empty(Current(w, closed.TicketId));
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.TicketId == closed.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);

        // The AI is waiting for the customer's reply when the conversation ends: the inactivity close owns that path, so nothing is routed.
        var waiting = await ChatAsync(w, "c-ai-waiting");
        await UpdateAsync(w, waiting, cid => new GenesysTicketUpdateDto(cid, AwaitingCustomerReply: true, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "Timeout")));
        Assert.Null(w.Ticket(waiting.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(waiting.TicketId).CurrentDepartmentId);

        // Neither consumed a round robin turn.
        Assert.All(w.F.Rotation.Rows.Values, row => Assert.Null(row.LastAssignedEmployeeId));
    }

    [Fact]
    public async Task AnAiEventAfterAConfirmedHumanAssignment_NeverOverwritesIt()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-human-then-ai");
        await ConnectAsync(w, chat, w.Agents[2].GenesysId);
        AssertOwner(w, chat.TicketId, w.Agents[2].Id);

        // Late AI events: the AI says it closed it, the conversation ends, the AI asks for a handoff again.
        var closeAttempt = await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, ClosedByAi: true));
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "AgentDisconnect")));
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover")));

        Assert.True(closeAttempt.ClosedByAi == false);
        AssertOwner(w, chat.TicketId, w.Agents[2].Id);                                                 // an interaction ending never reassigns
        Assert.NotEqual(TicketStatus.Closed, w.Ticket(chat.TicketId).TicketStatus);
    }

    // ---- 4. Transfers: only what Genesys actually confirms ---------------------------------------------------------------------------

    [Fact]
    public async Task OfferedOrFailedTransfer_PreservesAgentA()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-xfer-failed");
        var (a, b) = (w.Agents[0], w.Agents[1]);
        await ConnectAsync(w, chat, a.GenesysId);
        AssertOwner(w, chat.TicketId, a.Id);

        // A offers the interaction to B: B is only assigned / ringing (handoff assignee), then the transfer fails - Genesys never reports B connected.
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Handoff: new GenesysHandoffUpdateDto(AgentAvailable: true, AssignedAgentId: b.GenesysId)));
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Routing: new GenesysRoutingUpdateDto(QueueId: "q-transfer", QueueName: "Transfers")));
        await ConnectAsync(w, chat, a.GenesysId);                                                      // A is still the connected agent

        AssertOwner(w, chat.TicketId, a.Id);
        Assert.DoesNotContain(w.F.Handlers.All, h => h.TicketId == chat.TicketId && h.HandledByUserId == b.Id);
    }

    [Fact]
    public async Task AnotherAgentConnecting_IsRecordedAsAHandlerAndATransferEvent_ButTheOwnerIsNotGuessed()
    {
        // Genesys sends only "agent X connected". It does not say whether the sender left (blind / warm transfer) or stayed (consult / conference),
        // so TigerCS cannot know the final owner. It records everything and leaves the owner to a person - see docs for the contract needed.
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-xfer-accepted");
        var (a, b) = (w.Agents[0], w.Agents[1]);
        await ConnectAsync(w, chat, a.GenesysId, T0);

        await ConnectAsync(w, chat, b.GenesysId, T0.AddMinutes(2));

        AssertOwner(w, chat.TicketId, a.Id);
        Assert.Equal(
            new[] { a.Id, b.Id }.Order(),
            w.F.Handlers.All.Where(h => h.TicketId == chat.TicketId).Select(h => h.HandledByUserId).Order());       // both are handlers
        Assert.Contains(w.F.WorkflowEvents.All, e => e.TicketId == chat.TicketId && e.EventType == WorkflowEventType.GenesysCallTransferred);
    }

    [Fact]
    public async Task AnAuthorizedPersonTransferringTheOwnership_IsTheOnlyWayTheOwnerMovesToB()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-xfer-person");
        var (a, b) = (w.Agents[0], w.Agents[1]);
        await ConnectAsync(w, chat, a.GenesysId);
        await ConnectAsync(w, chat, b.GenesysId);
        var ticket = w.Ticket(chat.TicketId);

        // A CS Manager reassigns to B (the handler Genesys reported): explicit, audited, one current owner.
        var result = await AssignmentService(w).AssignAsync(
            Guid.NewGuid(), [Roles.CsManager], chat.TicketId, new AssignTicketRequestDto(b.Id, ticket.RowVersion, ReasonCode: TicketTransferReason.UnableToResolve, Reason: "Agent B took the call over"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        AssertOwner(w, chat.TicketId, b.Id);
    }

    // ---- 5. Supervisor override stays authoritative -----------------------------------------------------------------------------------

    [Fact]
    public async Task SupervisorOverride_IsNotOverwrittenByGenesysEvents_RepeatedOrLate()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-override", humanFollowUp: true);
        var (supervisorsChoice, someoneElse) = (w.Agents[2], w.Agents[0]);
        var ticket = w.Ticket(chat.TicketId);
        var assigned = await AssignmentService(w).AssignAsync(
            Guid.NewGuid(), [Roles.CsManager], chat.TicketId, new AssignTicketRequestDto(supervisorsChoice.Id, ticket.RowVersion, ReasonCode: TicketTransferReason.UnableToResolve, Reason: "Supervisor override"));
        Assert.Equal(TicketMutationOutcome.Success, assigned.Outcome);

        await ConnectAsync(w, chat, someoneElse.GenesysId);                                           // a different agent really answers
        await ConnectAsync(w, chat, someoneElse.GenesysId);                                           // redelivered
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "AgentDisconnect")));

        AssertOwner(w, chat.TicketId, supervisorsChoice.Id);                                           // authoritative until a new authorized reassignment
        Assert.Contains(w.F.Handlers.All, h => h.TicketId == chat.TicketId && h.HandledByUserId == someoneElse.Id);   // but the real handler is on record
    }

    // ---- 6/7. Repeated and out-of-order events -----------------------------------------------------------------------------------------

    [Fact]
    public async Task OutOfOrderConnectEvents_NeverReplaceAConfirmedOwner_AndHandlersAreOrderedByTheirConnectedTime()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-ooo");
        var (a, b) = (w.Agents[0], w.Agents[1]);

        await ConnectAsync(w, chat, b.GenesysId, T0.AddMinutes(5));                                    // the LATER connection is delivered first
        await ConnectAsync(w, chat, a.GenesysId, T0);                                                  // the stale, earlier one arrives afterwards
        await ConnectAsync(w, chat, a.GenesysId, T0);                                                  // and is redelivered

        AssertOwner(w, chat.TicketId, b.Id);                                                           // the stale event did not take the ticket
        var handlers = w.F.Handlers.All.Where(h => h.TicketId == chat.TicketId).ToList();
        Assert.Equal(2, handlers.Count);                                                               // deduplicated: one row per (interaction, agent)
        Assert.Equal(b.Id, handlers.OrderByDescending(h => h.ConnectedAtUtc).First().HandledByUserId);
        Assert.Single(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == chat.TicketId.ToString());
    }

    [Fact]
    public async Task ConnectEventForAnAgentAfterTheInteractionEnded_DoesNotReassign()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-late");
        await ConnectAsync(w, chat, w.Agents[1].GenesysId, T0);
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, Ended: new GenesysConversationEndUpdateDto(T0.AddMinutes(10), "AgentDisconnect")));

        await ConnectAsync(w, chat, w.Agents[2].GenesysId, T0.AddMinutes(1));                         // delayed: it happened before the end but is delivered after

        AssertOwner(w, chat.TicketId, w.Agents[1].Id);
    }

    // ---- 8. Mapping problems ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnmappedAgent_NeverTakesTheTicket_IsFlagged_AndTheExistingCsAgentIsPreserved()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-unmapped", humanFollowUp: true);

        await ConnectAsync(w, chat, "g-not-in-tigercs");

        // The unmapped person never becomes the owner; the ticket is never left without a CS Agent (an eligible CS Agent owns it).
        Assert.Contains(w.Ticket(chat.TicketId).CurrentOwnerEmployeeId, w.Agents.Select(a => (Guid?)a.Id));
        Assert.Contains(w.F.WorkflowEvents.All, e => e.TicketId == chat.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired && e.Note!.Contains("g-not-in-tigercs"));
    }

    [Fact]
    public async Task AGenesysIdMappedToTheServiceAccount_IsNeverAHandlingAgent()
    {
        var w = NewWorld();
        var serviceUser = w.F.MapAgent("g-integration", displayName: "Genesys Integration", roles: [Roles.CsAgent]);
        w.F.ServiceIdentities!.Add(serviceUser);
        w.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(serviceUser, w.F.CallCenter!.DepartmentId, true, DateTime.UtcNow, null));
        var chat = await ChatAsync(w, "c-service", humanFollowUp: true);

        await ConnectAsync(w, chat, "g-integration");

        Assert.NotEqual(serviceUser, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.Handlers.All, h => h.HandledByUserId == serviceUser);
        Assert.Contains(w.F.WorkflowEvents.All, e => e.TicketId == chat.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired && e.Note!.Contains("service account"));
    }

    [Fact]
    public async Task DuplicateMappingForOneGenesysId_IsNeverResolvedToEitherUser()
    {
        // Real repository over EF InMemory (which has no unique index): two Ticketing users carry the same Genesys id.
        await using var db = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var name in new[] { "dup.one", "dup.two" })
        {
            db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = name, NormalizedUserName = name.ToUpperInvariant(), GenesysUserId = "g-dup" });
        }

        await db.SaveChangesAsync();
        var resolver = new GenesysAgentResolutionAppService(new GenesysAgentMappingRepository(db), new FakeUserRoleReader(), new FakeUserDepartmentAssignmentRepository());

        var result = await resolver.ResolveByGenesysUserIdAsync("g-dup");

        Assert.Equal(GenesysAgentResolutionOutcome.Ambiguous, result.Outcome);
        Assert.False(result.IsResolved);
        Assert.Equal(AnsweringAgentIssue.AmbiguousMapping, GenesysAnsweringAgentAssignmentService.ToAnsweringAgent("g-dup", result).Issue);
        Assert.Equal(GenesysAgentResolutionOutcome.NotMapped, (await resolver.ResolveByGenesysUserIdAsync("g-other")).Outcome);
        Assert.Equal(GenesysAgentResolutionOutcome.Invalid, (await resolver.ResolveByGenesysUserIdAsync("  ")).Outcome);
    }

    [Fact]
    public async Task MatchingIsByGenesysUserIdOnly_NeverByDisplayName()
    {
        var w = NewWorld();
        w.F.MapAgent("g-real-id", displayName: "Agent 1");                                              // same display name as a real agent, different id
        var chat = await ChatAsync(w, "c-name", humanFollowUp: true);
        var fallback = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId;

        // The event carries only a name that matches an agent's display name; no id => nothing is resolved.
        await UpdateAsync(w, chat, cid => new GenesysTicketUpdateDto(cid, AgentName: "Agent 1", Routing: new GenesysRoutingUpdateDto(AgentName: "Agent 1")));

        AssertOwner(w, chat.TicketId, fallback);
    }

    // ---- department classification never costs the ticket its agent --------------------------------------------------------------------

    [Fact]
    public async Task RequestTypeClassificationAndDepartmentTransfer_KeepTheConfirmedCsAgent()
    {
        var w = NewWorld();
        var facilities = w.F.Departments.AddDepartment("Facilities Management", "FM");
        var chat = await ChatAsync(w, "c-dept");
        await ConnectAsync(w, chat, w.Agents[2].GenesysId);
        var owner = w.Agents[2].Id;

        // A CS Manager classifies / moves the ticket to Facilities: the department changes, the CS Agent does not.
        var ticket = w.Ticket(chat.TicketId);
        var moved = await AssignmentService(w).TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], chat.TicketId, new TransferTicketRequestDto(facilities.DepartmentId, "Facilities issue", ticket.RowVersion));

        Assert.Equal(TicketMutationOutcome.Success, moved.Outcome);
        Assert.Equal(facilities.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
        AssertOwner(w, chat.TicketId, owner);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == chat.TicketId.ToString() && e.AfterValue!.Contains("Trigger=DepartmentTransfer"));

        // A later Genesys event does not pull it back to the Call Center nor take the agent away.
        await ConnectAsync(w, chat, w.Agents[0].GenesysId);
        Assert.Equal(facilities.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
        AssertOwner(w, chat.TicketId, owner);
    }

    // ---- 9. Concurrency ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SimultaneousAcceptanceAndManualReassignment_LoserIsReported_ExactlyOneOwnerRemains()
    {
        var w = NewWorld();
        var chat = await ChatAsync(w, "c-race", humanFollowUp: true);
        var fallback = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId!.Value;
        var acceptor = w.Agents.First(a => a.Id != fallback);

        // Another writer changed the ticket first: the acceptance's save loses on the row version.
        // Call 3 of a connect update is the first-handler record - the save two concurrent identical deliveries race on.
        w.F.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = w.F.UnitOfWork.SaveChangesCallCount + 3;
        var first = await ConnectAsync(w, chat, acceptor.GenesysId);

        Assert.Equal(GenesysTicketUpdateOutcome.ConcurrencyConflict, first.Outcome);                  // reported, safe to redeliver

        // Redelivery succeeds and the result is the single, consistent one.
        var redelivered = await ConnectAsync(w, chat, acceptor.GenesysId);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, redelivered.Outcome);
        Assert.Equal(acceptor.Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        Assert.Single(Current(w, chat.TicketId), a => a.AssignedEmployeeId == acceptor.Id);
    }
}
