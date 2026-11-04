using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// "Who answered owns the ticket." A Genesys call answered by a mapped, eligible Call Center agent is assigned DIRECTLY to that agent in the
/// Call Center - never to the next agent in the rotation. Round Robin remains only for a human handoff nobody has accepted. An answering agent
/// who cannot take the ticket (unmapped / deactivated / locked out / service account / not in the Call Center) leaves it unowned in the Call
/// Center queue, flagged for manual assignment. A person's ownership - and a person's transfer - are never overwritten by a later Genesys event.
/// </summary>
public class GenesysAnsweringAgentRoutingTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    /// <summary>Three Call Center agents, ordered the way Round Robin would walk them: <c>Agents[0]</c> is "next in the rotation".</summary>
    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }

        /// <summary>The agent Round Robin would hand the next ticket to.</summary>
        public (Guid Id, string GenesysId) RoundRobinNext => Agents[0];

        /// <summary>An agent who is definitely NOT the next in the rotation - so a direct assignment cannot be mistaken for Round Robin.</summary>
        public (Guid Id, string GenesysId) Answering => Agents[2];

        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);

        public long Ticket_Id(Ingested i) => i.TicketId;
    }

    private sealed record Ingested(long TicketId, string ConversationId);

    private static World NewWorld(bool strict = true, bool assignOnAnswer = true)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: strict, assignOnAnswer: assignOnAnswer);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = Enumerable.Range(1, 3)
            .Select(i => (GenesysId: $"g-agent-{i}", Id: f.AddAgent($"g-agent-{i}", $"Agent {i}")))
            .OrderBy(a => a.Id)
            .Select(a => (a.Id, a.GenesysId))
            .ToArray();
        return new World { F = f, Cs = cs, Agents = agents };
    }

    private static async Task<Ingested> IngestPhoneCallAsync(World w, string conversationId, string? answeringGenesysId)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001",
                DepartmentId: w.Cs.DepartmentId, AgentId: answeringGenesysId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return new Ingested(result.Ticket!.TicketId, conversationId);
    }

    private static async Task<Ingested> IngestAiChatAsync(World w, string conversationId)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return new Ingested(result.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, Ingested t, string genesysAgentId) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, t.TicketId,
            new GenesysTicketUpdateDto(t.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: DateTime.UtcNow)));

    private static Task<GenesysHandoffResult> RequestHandoffAsync(World w, Ingested t, string? assignedButNotConnectedAgent = null) =>
        w.F.AgentHandoff.RequestAsync(
            ServiceAccount,
            new GenesysHandoffRequestDto(t.ConversationId, AgentAvailable: assignedButNotConnectedAgent is not null, Mode: "HumanTakeover",
                AgentId: assignedButNotConnectedAgent));

    private static List<TicketAssignment> CurrentRows(World w, long ticketId) =>
        w.F.TicketAssignments.Added.Where(a => a.TicketId == ticketId && a.IsCurrent).ToList();

    /// <summary>Exactly one owner, exactly one current assignment, and they are the same person.</summary>
    private static void AssertSingleOwner(World w, long ticketId, Guid? expectedOwner)
    {
        var ticket = w.Ticket(ticketId);
        Assert.Equal(expectedOwner, ticket.CurrentOwnerEmployeeId);
        var current = CurrentRows(w, ticketId);
        if (expectedOwner is null)
        {
            Assert.Empty(current);
        }
        else
        {
            var only = Assert.Single(current);
            Assert.Equal(expectedOwner, only.AssignedEmployeeId);
        }
    }

    private static int FlagCount(World w, long ticketId) =>
        w.F.WorkflowEvents.All.Count(e => e.TicketId == ticketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);

    private static bool RoundRobinCursorUntouched(World w) =>
        w.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    // ---------------------------------------------------------------------------------------------------------------------------
    // 1-2. Human pickup: assigned directly to the answering agent, in the Call Center
    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CallAnsweredBeforeTheTicketExists_IsAssignedDirectlyToTheAnsweringAgent_NotToTheNextInTheRotation()
    {
        var w = NewWorld();
        Assert.NotEqual(w.RoundRobinNext.Id, w.Answering.Id);

        var call = await IngestPhoneCallAsync(w, "conv-answered", w.Answering.GenesysId);

        var ticket = w.Ticket(call.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);          // routed to CC
        Assert.Equal(w.Cs.DepartmentId, ticket.OriginatingDepartmentId);                 // originating department preserved
        AssertSingleOwner(w, call.TicketId, w.Answering.Id);                             // THE answering agent, not Agents[0]

        // It is the agent's own act (like a self-assignment), so it is human ownership - not an automatic pre-assignment.
        Assert.Equal(w.Answering.Id, CurrentRows(w, call.TicketId).Single().AssigningActorEmployeeId);
        Assert.Equal(w.F.CallCenter.DepartmentId, CurrentRows(w, call.TicketId).Single().AssignedDepartmentId);

        // Round Robin was not consulted: its cursor never moved, and no AutoAssign (rotation) audit entry exists.
        Assert.True(RoundRobinCursorUntouched(w));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == call.TicketId.ToString());
        var audit = Assert.Single(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == call.TicketId.ToString());
        Assert.Equal(w.Answering.Id, audit.ActorEmployeeId);
        Assert.Contains($"GenesysUserId={w.Answering.GenesysId}", audit.AfterValue);

        // Call-Center-first routing is recorded as a system move in the typed event log, like every other Call Center entry.
        Assert.Contains(w.F.WorkflowEvents.All, e =>
            e.TicketId == call.TicketId && e.EventType == WorkflowEventType.TicketTransferred
            && e.TransferReasonCode == (byte)TicketTransferReason.SystemRouting);
    }

    [Fact]
    public async Task AgentConnectsToAnAiTicketAfterWards_RoutesToCallCenter_AndAssignsDirectly()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-chat");
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);    // AI-handled: untouched so far

        var result = await ConnectAsync(w, chat, w.Answering.GenesysId);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
        AssertSingleOwner(w, chat.TicketId, w.Answering.Id);
        Assert.True(RoundRobinCursorUntouched(w));
    }

    [Fact]
    public async Task HandoffAndConnectedAgentInTheSameUpdate_AssignsTheConnectedAgent_WithoutSpendingARoundRobinTurn()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-same-update");

        var result = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, chat.TicketId,
            new GenesysTicketUpdateDto(
                chat.ConversationId,
                Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover"),
                Routing: new GenesysRoutingUpdateDto(AgentId: w.Answering.GenesysId, ConnectedAtUtc: DateTime.UtcNow)));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        AssertSingleOwner(w, chat.TicketId, w.Answering.Id);
        Assert.True(RoundRobinCursorUntouched(w));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == chat.TicketId.ToString());
    }

    [Theory]
    [InlineData("unmapped")]
    [InlineData("locked")]
    public async Task HandoffWithAnUnusableConnectedAgentInTheSameUpdate_IsFlagged_AndTheSharedRotationGivesItACsAgent(string kind)
    {
        var w = NewWorld();
        var gid = kind == "locked" ? "g-locked-agent" : "g-nobody";
        if (kind == "locked")
        {
            w.F.AddAgent(gid, "Locked Agent", lockedOut: true);
        }

        var chat = await IngestAiChatAsync(w, $"conv-unusable-{kind}");

        var result = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, chat.TicketId,
            new GenesysTicketUpdateDto(
                chat.ConversationId,
                Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover"),
                Routing: new GenesysRoutingUpdateDto(AgentId: gid, ConnectedAtUtc: DateTime.UtcNow)));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);   // in the Call Center queue
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);   // flagged AND dealt to the next shared-rotation CS Agent - never unassigned
        Assert.Equal(1, FlagCount(w, chat.TicketId));
    }

    [Fact]
    public async Task RollbackLever_AssignOnAnswerOff_RestoresRoundRobin_ForEveryEntryPoint()
    {
        var w = NewWorld(assignOnAnswer: false);

        // A call answered at creation, exactly as before the rule existed: a mapped agent at pickup is human evidence, so the ticket enters
        // the Call Center and Round Robin picks the owner - the answering agent is only a recorded handler.
        var call = await IngestPhoneCallAsync(w, "conv-lever-call", w.Answering.GenesysId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);
        AssertSingleOwner(w, call.TicketId, w.RoundRobinNext.Id);
        Assert.Contains(w.F.Handlers.All, h => h.TicketId == call.TicketId && h.HandledByUserId == w.Answering.Id);

        // An agent connecting later and a handoff with a connected agent: no direct assignment, no flag.
        var chat = await IngestAiChatAsync(w, "conv-lever-chat");
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, chat.TicketId,
            new GenesysTicketUpdateDto(
                chat.ConversationId,
                Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover"),
                Routing: new GenesysRoutingUpdateDto(AgentId: w.Answering.GenesysId, ConnectedAtUtc: DateTime.UtcNow)));
        Assert.NotEqual(w.Answering.Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        Assert.NotNull(w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);                 // Round Robin, as before the rule existed
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action is "AssignOnAnswer" or "AnswerAssignmentSkipped");

        // An unmapped id at pickup is no evidence of a person: before the rule it moved nothing, and with the lever off it still does not.
        var unmapped = await IngestPhoneCallAsync(w, "conv-lever-unmapped", "ghost-agent");
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(unmapped.TicketId).CurrentDepartmentId);
        Assert.Equal(0, FlagCount(w, unmapped.TicketId));
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // 3. Round Robin only when a handoff is requested and nobody has accepted it
    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandoffRequestedBeforeAnyAgentAccepted_UsesRoundRobin()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-rr");

        var handoff = await RequestHandoffAsync(w, chat);

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Trigger=CallCenterIntake"));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer");
    }

    [Fact]
    public async Task AgentOnlyAssignedOrRinging_IsNotAnAnswer_RoundRobinStillApplies()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-ringing");

        // Genesys names an agent as assigned (the work item is Assigned) - but nobody has connected. Not an answered call.
        var handoff = await RequestHandoffAsync(w, chat, assignedButNotConnectedAgent: w.Answering.GenesysId);

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);                         // Round Robin, not the named agent
        Assert.NotEqual(w.Answering.Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer");
    }

    [Fact]
    public async Task HandoffAssignedAgentAndTopLevelAgentContext_WithoutARoutingConnection_DoNotAssignOwnership()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-assigned-only");
        await RequestHandoffAsync(w, chat);
        var ownerBefore = w.Ticket(chat.TicketId).CurrentOwnerEmployeeId;

        // 'assignedAgentId' = Genesys says who is to take the work; a top-level agentId is conversation context. Neither says "connected".
        var result = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, chat.TicketId,
            new GenesysTicketUpdateDto(
                chat.ConversationId, AgentId: w.Answering.GenesysId, AgentName: "Someone",
                Handoff: new GenesysHandoffUpdateDto(AssignedAgentId: w.Answering.GenesysId)));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(ownerBefore, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        AssertSingleOwner(w, chat.TicketId, ownerBefore);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer");
    }

    [Fact]
    public async Task AnswerAfterRoundRobinPreAssignment_ReplacesTheAutomaticOwner_AndKeepsTheHistory()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-rr-then-answer");
        await RequestHandoffAsync(w, chat);
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);

        await ConnectAsync(w, chat, w.Answering.GenesysId);

        AssertSingleOwner(w, chat.TicketId, w.Answering.Id);
        // The automatic pre-assignment is superseded, never deleted: both rows exist, only the answering agent's is current.
        var rows = w.F.TicketAssignments.Added.Where(a => a.TicketId == chat.TicketId).ToList();
        Assert.Equal(2, rows.Count);
        Assert.False(rows.Single(a => a.AssignedEmployeeId == w.RoundRobinNext.Id).IsCurrent);
        Assert.True(rows.Single(a => a.AssignedEmployeeId == w.Answering.Id).IsCurrent);
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // 4. Duplicate / later events never move or double the owner
    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DuplicateConnectedEvents_ChangeNothing_OneOwnerOneAssignment()
    {
        var w = NewWorld();
        var call = await IngestPhoneCallAsync(w, "conv-dup", w.Answering.GenesysId);
        var assignmentsBefore = w.F.TicketAssignments.Added.Count;
        var auditBefore = w.F.Audit.Entries.Count(e => e.EntityId == call.TicketId.ToString() && e.EntityType == "Ticket");

        for (var i = 0; i < 3; i++)
        {
            var again = await ConnectAsync(w, call, w.Answering.GenesysId);
            Assert.Equal(GenesysTicketUpdateOutcome.Applied, again.Outcome);
        }

        AssertSingleOwner(w, call.TicketId, w.Answering.Id);
        Assert.Equal(assignmentsBefore, w.F.TicketAssignments.Added.Count);               // no second assignment row
        Assert.Equal(auditBefore, w.F.Audit.Entries.Count(e => e.EntityId == call.TicketId.ToString() && e.EntityType == "Ticket"));
    }

    [Fact]
    public async Task DuplicateIngestOfTheSameCall_ReturnsTheSameTicket_AndTheSameSingleOwner()
    {
        var w = NewWorld();
        var first = await IngestPhoneCallAsync(w, "conv-redelivered", w.Answering.GenesysId);

        var second = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-redelivered", GenesysChannel.Phone, CustomerPhone: "+971500000001",
                DepartmentId: w.Cs.DepartmentId, AgentId: w.Answering.GenesysId));

        Assert.Equal(first.TicketId, second.Ticket!.TicketId);
        Assert.Single(w.F.Tickets.All);
        AssertSingleOwner(w, first.TicketId, w.Answering.Id);
    }

    [Fact]
    public async Task AnotherAgentConnectingLater_NeverTakesTheTicketFromTheHumanOwner()
    {
        var w = NewWorld();
        var call = await IngestPhoneCallAsync(w, "conv-two-agents", w.Answering.GenesysId);

        // Genesys moves the live call to a second agent: they become a HANDLER (they can see the ticket) but ownership stays put.
        var other = w.Agents[1];
        await ConnectAsync(w, call, other.GenesysId);

        AssertSingleOwner(w, call.TicketId, w.Answering.Id);
        Assert.Contains(w.F.Handlers.All, h => h.TicketId == call.TicketId && h.HandledByUserId == other.Id);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.ActorEmployeeId == other.Id);
    }

    [Fact]
    public async Task ManualOwnerAlreadyOnTheTicket_IsNeverOverwrittenByAnAnsweringAgent()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-manual-owner");
        await RequestHandoffAsync(w, chat);

        // A person takes the ticket by hand: the assignment row's actor is the person, not the system.
        var manualOwner = w.Agents[1];
        var ticket = w.Ticket(chat.TicketId);
        w.F.TicketAssignments.Added.Single(a => a.TicketId == chat.TicketId && a.IsCurrent).MarkSuperseded();
        ticket.AssignTo(manualOwner.Id);
        await w.F.TicketAssignments.AddAsync(new TicketAssignment(chat.TicketId, manualOwner.Id, ticket.CurrentDepartmentId, DateTime.UtcNow, manualOwner.Id));

        await ConnectAsync(w, chat, w.Answering.GenesysId);

        AssertSingleOwner(w, chat.TicketId, manualOwner.Id);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer");
        Assert.Equal(0, FlagCount(w, chat.TicketId));
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // 5. Unusable answering agent: stays in the Call Center queue, flagged - never given to somebody else
    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnmappedAnsweringAgent_FlagsTheTicket_AndTheSharedRotationGivesItACsAgent()
    {
        var w = NewWorld();

        var call = await IngestPhoneCallAsync(w, "conv-unmapped", "genesys-user-nobody-mapped");

        var ticket = w.Ticket(call.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        AssertSingleOwner(w, call.TicketId, w.RoundRobinNext.Id);   // flagged AND dealt to the next shared-rotation CS Agent - never unassigned
        Assert.Equal(1, FlagCount(w, call.TicketId));
        var flag = w.F.WorkflowEvents.All.Single(e => e.TicketId == call.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        Assert.Contains("genesys-user-nobody-mapped", flag.Note);
        Assert.Contains("not mapped", flag.Note);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.AfterValue!.Contains("ManualAssignmentRequired=true"));
    }

    [Theory]
    [InlineData("deactivated", AnsweringAgentIssue.Inactive)]
    [InlineData("locked", AnsweringAgentIssue.LockedOut)]
    [InlineData("service", AnsweringAgentIssue.ServiceAccount)]
    [InlineData("outsider", AnsweringAgentIssue.NotCallCenterMember)]
    [InlineData("readonly", AnsweringAgentIssue.NoOperationalRole)]
    public async Task AnsweringAgentWhoCannotTakeTheTicket_IsFlagged_AndNeverOwnsIt_TheSharedRotationDoes(string kind, AnsweringAgentIssue expected)
    {
        var w = NewWorld();
        var gid = $"g-{kind}";
        var id = kind switch
        {
            "deactivated" => w.F.AddAgent(gid, "Gone Agent", isActive: false),
            "locked" => w.F.AddAgent(gid, "Locked Agent", lockedOut: true),
            "service" => w.F.AddAgent(gid, "Integration"),
            "outsider" => w.F.AddAgent(gid, "Finance Person", inCallCenter: false),
            _ => w.F.AddAgent(gid, "Reporting Person", roles: [Roles.ReportingUser])
        };
        if (kind == "service")
        {
            w.F.ServiceIdentities!.Add(id);
        }

        var call = await IngestPhoneCallAsync(w, $"conv-{kind}", gid);

        // Never the unusable person; some CS Agent owns it, exactly one current row. (The fake directory keeps a "deactivated" Genesys mapping
        // eligible for rotation - production excludes deactivated employees - so the exact agent is only asserted for the other kinds.)
        var owner = w.Ticket(call.TicketId).CurrentOwnerEmployeeId;
        Assert.NotNull(owner);
        Assert.Single(CurrentRows(w, call.TicketId));
        if (kind != "deactivated") Assert.Equal(w.RoundRobinNext.Id, owner);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);
        Assert.Equal(1, FlagCount(w, call.TicketId));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.AfterValue!.Contains($"Issue={expected}"));
    }

    [Fact]
    public async Task UnmappedAgentConnectsAfterRoundRobin_KeepsTheExistingEligibleCsAgent_WithoutAnotherRotationTurn()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-rr-then-unmapped");
        await RequestHandoffAsync(w, chat);
        Assert.Equal(w.RoundRobinNext.Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        var cursorAfterHandoff = w.F.Rotation.Rows.Values.Single().LastAssignedEmployeeId;

        await ConnectAsync(w, chat, "unknown-genesys-id");

        // The person who really answered is unknown, but the ticket already has an eligible CS Agent: that agent is preserved (final rule, priority 3),
        // not released and not re-dealt.
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);
        Assert.Equal(cursorAfterHandoff, w.F.Rotation.Rows.Values.Single().LastAssignedEmployeeId);           // no new rotation turn
        var rows = w.F.TicketAssignments.Added.Where(a => a.TicketId == chat.TicketId).ToList();
        Assert.Single(rows);
        Assert.True(rows[0].IsCurrent);
    }

    [Fact]
    public async Task RepeatedUnmappedEvents_WriteTheFlagOnce()
    {
        var w = NewWorld();
        var call = await IngestPhoneCallAsync(w, "conv-unmapped-dup", "ghost-agent");
        var eventsBefore = w.F.WorkflowEvents.All.Count;

        await ConnectAsync(w, call, "ghost-agent");
        await ConnectAsync(w, call, "ghost-agent");

        Assert.Equal(1, FlagCount(w, call.TicketId));
        Assert.Equal(eventsBefore, w.F.WorkflowEvents.All.Count);
        AssertSingleOwner(w, call.TicketId, w.RoundRobinNext.Id);   // flagged AND dealt to the next shared-rotation CS Agent - never unassigned
    }

    [Fact]
    public async Task UnmappedAgentConnectsToAnEarlierPhoneTicket_RoutesItToTheCallCenter_FlagsIt_AndAssignsACsAgent()
    {
        var w = NewWorld();
        var call = await IngestPhoneCallAsync(w, "conv-phone-no-agent-yet", answeringGenesysId: null);
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);    // created before anybody answered: stays put

        await ConnectAsync(w, call, "ghost-agent");

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);
        AssertSingleOwner(w, call.TicketId, w.RoundRobinNext.Id);   // flagged AND dealt to the next shared-rotation CS Agent - never unassigned
        Assert.Equal(1, FlagCount(w, call.TicketId));
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // 5b. Final rule: a human-needed ticket that is ALREADY in the Call Center (so no routing happens) must still get a CS Agent
    // ---------------------------------------------------------------------------------------------------------------------------

    private static async Task<Ingested> IngestAiChatInCallCenterAsync(World w, string conversationId)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: w.F.CallCenter!.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return new Ingested(result.Ticket!.TicketId, conversationId);
    }

    [Fact]
    public async Task HandoffOnAnAiTicketAlreadyInTheCallCenter_StillGetsACsAgent()
    {
        var w = NewWorld();
        var chat = await IngestAiChatInCallCenterAsync(w, "conv-ai-in-cc");
        Assert.Null(w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);                     // AI-owned: no agent yet

        await RequestHandoffAsync(w, chat);

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);                        // previously: stayed unassigned (no routing => no assignment)
    }

    [Fact]
    public async Task HumanFollowUpUpdateOnAnAiTicketAlreadyInTheCallCenter_StillGetsACsAgent_AndARedeliveryAssignsNobodyElse()
    {
        var w = NewWorld();
        var chat = await IngestAiChatInCallCenterAsync(w, "conv-ai-in-cc-update");
        var update = new GenesysTicketUpdateDto(chat.ConversationId, Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover"));

        var first = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, chat.TicketId, update);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, first.Outcome);
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, chat.TicketId, update);       // redelivery
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);                        // idempotent: same owner, one current row
    }

    [Fact]
    public async Task UnmappedIdOnAnAiHandledChat_DoesNotDragTheTicketIntoTheCallCenter()
    {
        var w = NewWorld();

        // A chat created with an agent id but no sign a person is involved (a bot, or a not-yet-connected agent) stays where it is.
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-bot-id", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001",
                DepartmentId: w.Cs.DepartmentId, AgentId: "virtual-agent-17"));

        var ticket = w.Ticket(result.Ticket!.TicketId);
        Assert.Equal(w.Cs.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(0, FlagCount(w, ticket.TicketId));
    }

    [Fact]
    public async Task MappedButIneligiblePersonConnectsToAnAiChat_IsFlagged_NotTreatedAsABot_AndTheRotationAssignsACsAgent()
    {
        var w = NewWorld();
        // A real Ticketing user (mapped) who is not a Call Center agent: a person, never a bot - so the chat is human-answered, but this
        // person cannot own a Call Center ticket. It waits in the Call Center queue, flagged; Round Robin must not hand it to someone else.
        w.F.AddAgent("g-finance-person", "Finance Person", inCallCenter: false);
        var chat = await IngestAiChatAsync(w, "conv-mapped-ineligible");

        await ConnectAsync(w, chat, "g-finance-person");

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(chat.TicketId).CurrentDepartmentId);
        AssertSingleOwner(w, chat.TicketId, w.RoundRobinNext.Id);   // flagged AND dealt to the next shared-rotation CS Agent - never unassigned
        Assert.Equal(1, FlagCount(w, chat.TicketId));
        Assert.Contains(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired && e.Note!.Contains("not a Call Center member"));
    }

    [Fact]
    public async Task ChatCreatedWithATopLevelAgentIdAndHumanFollowUp_IsNotAnAnswer_ItGoesToRoundRobin_UntilTheAgentIsReportedConnected()
    {
        var w = NewWorld();

        // A top-level agentId on a digital channel is only who is offered/assigned/queued: not proof of acceptance, even with
        // humanFollowUpRequired. The ticket enters the Call Center and Round Robin gives it to ONE eligible agent.
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-chat-answered", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001",
                DepartmentId: w.Cs.DepartmentId, AgentId: w.Answering.GenesysId, HumanFollowUpRequired: true));

        var ticket = w.Ticket(result.Ticket!.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.NotNull(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticket.TicketId.ToString());
        Assert.False(RoundRobinCursorUntouched(w));

        // The agent actually connects: the automatic pick is replaced by the agent who accepted.
        await ConnectAsync(w, new Ingested(ticket.TicketId, "conv-chat-answered"), w.Answering.GenesysId);

        AssertSingleOwner(w, ticket.TicketId, w.Answering.Id);
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // 6. Post-transfer: the transfer and its new owner stand
    // ---------------------------------------------------------------------------------------------------------------------------

    private static TicketAssignmentAppService TransferService(World w) =>
        new(w.F.Tickets, w.F.TicketAssignments, w.F.DepartmentAssignments, w.F.Departments, w.F.UnitOfWork, w.F.Audit, TimeProvider.System,
            w.F.DepartmentSettings, w.F.AutoAssignment, w.F.WorkflowEvents, requestTypeRepository: w.F.RequestTypes);

    [Fact]
    public async Task AfterTheAnsweringAgentTransfersTheTicket_LaterGenesysUpdatesPreserveTheTransferAndItsNewOwner()
    {
        var w = NewWorld();
        var finance = w.F.Departments.AddDepartment("Finance", "FIN");
        var financeOwner = w.F.AddAgent("g-fin-1", "Finance Employee", inCallCenter: false, roles: [Roles.DepartmentEmployee]);
        w.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(financeOwner, finance.DepartmentId, true, DateTime.UtcNow, null));

        var call = await IngestPhoneCallAsync(w, "conv-transfer", w.Answering.GenesysId);
        AssertSingleOwner(w, call.TicketId, w.Answering.Id);
        var statusBefore = w.Ticket(call.TicketId).TicketStatus;

        // The answering agent - the owner - transfers the ticket to Finance and explicitly reassigns it to Finance's employee.
        var transfer = await TransferService(w).TransferAsync(
            w.Answering.Id, [Roles.CsAgent], call.TicketId,
            new TransferTicketRequestDto(finance.DepartmentId, "Finance matter", [], AssignToEmployeeId: financeOwner, ReasonCode: TicketTransferReason.WrongDepartment));
        Assert.Equal(TicketMutationOutcome.Success, transfer.Outcome);
        AssertSingleOwner(w, call.TicketId, financeOwner);
        Assert.Equal(finance.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);

        var auditAfterTransfer = w.F.Audit.Entries.Count;
        var eventsAfterTransfer = w.F.WorkflowEvents.All.Count;
        var assignmentsAfterTransfer = w.F.TicketAssignments.Added.Count;

        // Late Genesys traffic: the same agent reported connected again, another agent connected, an unmapped id, and the call ending.
        await ConnectAsync(w, call, w.Answering.GenesysId);
        await ConnectAsync(w, call, w.Agents[1].GenesysId);
        await ConnectAsync(w, call, "ghost-agent");
        var ended = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, call.TicketId,
            new GenesysTicketUpdateDto(call.ConversationId, AgentId: w.Answering.GenesysId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "customer-hangup")));
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, ended.Outcome);

        // The transfer and its new owner are untouched: still in Finance, still Finance's employee, one current assignment.
        Assert.Equal(finance.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);
        AssertSingleOwner(w, call.TicketId, financeOwner);
        Assert.Equal(assignmentsAfterTransfer, w.F.TicketAssignments.Added.Count);
        Assert.DoesNotContain(w.F.Audit.Entries.Skip(auditAfterTransfer), e => e.Action is "AssignOnAnswer" or "AnswerAssignmentSkipped" or "Transfer" or "Unassign");
        Assert.Equal(0, FlagCount(w, call.TicketId));
        Assert.DoesNotContain(w.F.WorkflowEvents.All.Skip(eventsAfterTransfer), e => e.EventType is WorkflowEventType.TicketTransferred or WorkflowEventType.ManualAssignmentRequired);
        Assert.Equal(statusBefore, w.Ticket(call.TicketId).TicketStatus);

        // The person's transfer is on record, with its own reason (not system routing).
        Assert.Contains(w.F.WorkflowEvents.All, e =>
            e.TicketId == call.TicketId && e.EventType == WorkflowEventType.TicketTransferred
            && e.TransferReasonCode == (byte)TicketTransferReason.WrongDepartment && e.ToDepartmentId == finance.DepartmentId);
    }

    [Fact]
    public async Task TransferredIntoAnUnstaffedDepartment_KeepsItsCsAgent_AndIsNotPulledBackToTheCallCenterByALateGenesysUpdate()
    {
        var w = NewWorld();
        var legal = w.F.Departments.AddDepartment("Legal", "LEG");     // nobody staffs Legal: the CS Agent stays on the ticket regardless

        var call = await IngestPhoneCallAsync(w, "conv-transfer-queue", w.Answering.GenesysId);
        var transfer = await TransferService(w).TransferAsync(
            w.Answering.Id, [Roles.CsAgent], call.TicketId,
            new TransferTicketRequestDto(legal.DepartmentId, "Legal matter", [], ReasonCode: TicketTransferReason.WrongDepartment));
        Assert.Equal(TicketMutationOutcome.Success, transfer.Outcome);
        AssertSingleOwner(w, call.TicketId, w.Answering.Id);                                 // the CS Agent is preserved, never "Legal Queue (Unassigned)"

        await ConnectAsync(w, call, w.Answering.GenesysId);
        await ConnectAsync(w, call, w.Agents[0].GenesysId);

        Assert.Equal(legal.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);       // not routed back to CC
        AssertSingleOwner(w, call.TicketId, w.Answering.Id);                                 // not re-assigned to anyone
        Assert.Equal(0, FlagCount(w, call.TicketId));
    }

    [Fact]
    public async Task TheAnsweringAgentOwnsTheTicketLikeAnyOwner_CanTransferItWithTheExistingCsAgentPermission()
    {
        var w = NewWorld();
        var finance = w.F.Departments.AddDepartment("Finance", "FIN");
        var call = await IngestPhoneCallAsync(w, "conv-owner-rights", w.Answering.GenesysId);

        // A different Call Center agent is refused (not the owner); the answering agent is allowed: ownership is what grants the right.
        var other = await TransferService(w).TransferAsync(
            w.Agents[0].Id, [Roles.CsAgent], call.TicketId,
            new TransferTicketRequestDto(finance.DepartmentId, "x", [], ReasonCode: TicketTransferReason.WrongDepartment));
        Assert.Equal(TicketMutationOutcome.Forbidden, other.Outcome);

        var owner = await TransferService(w).TransferAsync(
            w.Answering.Id, [Roles.CsAgent], call.TicketId,
            new TransferTicketRequestDto(finance.DepartmentId, "x", [], ReasonCode: TicketTransferReason.WrongDepartment));
        Assert.Equal(TicketMutationOutcome.Success, owner.Outcome);
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Preservation: status, SLA, history, closed tickets
    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AssigningTheAnsweringAgent_PreservesStatusSlaPriorityAndExistingHistory()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-preserve");
        var before = w.Ticket(chat.TicketId);
        var (status, sla, priority, escalation, originating, created) =
            (before.TicketStatus, before.SlaState, before.PriorityId, before.EscalationLevel, before.OriginatingDepartmentId, before.CreatedAtUtc);
        var auditBefore = w.F.Audit.Entries.ToList();
        var eventsBefore = w.F.WorkflowEvents.All.ToList();
        var historyBefore = w.F.StatusHistory.Added.ToList();

        await ConnectAsync(w, chat, w.Answering.GenesysId);

        var after = w.Ticket(chat.TicketId);
        Assert.Equal((status, sla, priority, escalation, originating, created),
            (after.TicketStatus, after.SlaState, after.PriorityId, after.EscalationLevel, after.OriginatingDepartmentId, after.CreatedAtUtc));
        Assert.Equal(historyBefore, w.F.StatusHistory.Added.ToList());                         // no status-history row written or changed
        Assert.True(auditBefore.SequenceEqual(w.F.Audit.Entries.Take(auditBefore.Count)));    // audit is append-only
        Assert.True(eventsBefore.SequenceEqual(w.F.WorkflowEvents.All.Take(eventsBefore.Count)));
    }

    [Fact]
    public async Task ClosedTicket_IsLeftExactlyAsItIs_ByALateGenesysUpdate()
    {
        var w = NewWorld();
        var chat = await IngestAiChatAsync(w, "conv-closed");
        await RequestHandoffAsync(w, chat);
        var owner = w.RoundRobinNext.Id;

        // The owner works it and it is closed (InProgress -> Resolved -> Closed, as the lifecycle does).
        var ticket = w.Ticket(chat.TicketId);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        var assignmentsBefore = w.F.TicketAssignments.Added.Count;
        var auditBefore = w.F.Audit.Entries.Count;

        var late = await ConnectAsync(w, chat, w.Answering.GenesysId);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, late.Outcome);

        Assert.Equal(TicketStatus.Closed, w.Ticket(chat.TicketId).TicketStatus);
        AssertSingleOwner(w, chat.TicketId, owner);
        Assert.Equal(assignmentsBefore, w.F.TicketAssignments.Added.Count);
        Assert.DoesNotContain(w.F.Audit.Entries.Skip(auditBefore), e => e.Action is "AssignOnAnswer" or "Transfer" or "Unassign" or "AnswerAssignmentSkipped");
        Assert.Equal(0, FlagCount(w, chat.TicketId));
    }

    [Fact]
    public void ReleaseOwner_ClearsTheOwnerOnly_AndRefusesAClosedTicket()
    {
        var ticket = Ticket.CreateUnverified("T-1", departmentId: 1, categoryId: 1, priorityId: 3, "x", DateTime.UtcNow);
        ticket.AssignTo(Guid.NewGuid());
        var status = ticket.TicketStatus;

        ticket.ReleaseOwner();

        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(status, ticket.TicketStatus);
        Assert.Equal(1, ticket.CurrentDepartmentId);
    }
}
