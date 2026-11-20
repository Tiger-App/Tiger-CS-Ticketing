using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Release acceptance for the business rule "when the Genesys AI cannot resolve a request, the ticket goes to a human immediately": the
/// whole <c>POST /api/genesys/tickets</c> → <c>PATCH /api/genesys/tickets/{id}</c> journey, through the same services the endpoints call.
///
/// <para>
/// Pins, end to end: an AI-handled ticket is never given to a person (not by a request-type rule, not by Round Robin); an unresolved one waits in
/// the Call Center QUEUE (or keeps the department an explicit workflow chose) still owned by the AI Agent, with its hand-off outstanding, until a
/// person actually accepts it; and none of it is repeated by a retry or a redelivery — no second ticket, no second hand-off, no second transfer,
/// no assignment at all. Request type validation, the Normal default priority and the SLA clock are unchanged by the hand-off.
/// </para>
/// </summary>
public class GenesysAiUnresolvedHandoffAcceptanceTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private sealed record World(
        GenesysServiceFixture F, Department Cs, Department Leasing, RequestType Leasing_Type, Guid[] Agents)
    {
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
    }

    private static World Build(int callCenterAgents = 2)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.Departments.AddDepartment("Leasing", "LCS");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var type = f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, "Handover", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));

        // A configured rule would hand an AI-classified ticket to this leasing employee (the automatic pre-assignment a hand-off releases).
        var leasingEmployee = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(leasingEmployee, leasing.DepartmentId, true, DateTime.UtcNow, null));
        f.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(type.RequestTypeId, leasingEmployee));

        var agents = Enumerable.Range(0, callCenterAgents).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToArray();
        foreach (var id in agents)
        {
            f.DepartmentAssignments.Assignments.Add(
                new UserDepartmentAssignment(id, f.CallCenter!.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        return new World(f, cs, leasing, type, agents);
    }

    private static GenesysInquiryDto Inquiry(World w, string conversationId, GenesysRequestTypeDto? requestType = null) =>
        new(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId, RequestType: requestType);

    private static async Task<long> IngestAsync(World w, string conversationId, GenesysRequestTypeDto? requestType = null)
    {
        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, conversationId, requestType));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return result.Ticket!.TicketId;
    }

    private static Task<GenesysTicketUpdateResult> EscalateAsync(World w, long ticketId, string conversationId) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(
                conversationId, Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "AiEscalated", Reason: "The AI could not resolve the request.")));

    private static int TransfersToCallCenter(World w, long ticketId) =>
        w.F.WorkflowEvents.All.Count(e =>
            e.TicketId == ticketId && e.EventType == WorkflowEventType.TicketTransferred && e.ToDepartmentId == w.F.CallCenter!.DepartmentId);

    private static int CurrentAssignments(World w, long ticketId) =>
        w.F.TicketAssignments.Added.Count(a => a.TicketId == ticketId && a.IsCurrent);

    /// <summary>The AI Agent still owns the ticket and no person was dealt it: no human owner, no assignment row, no Round Robin turn.</summary>
    private static void AssertStillTheAis(World w, Ticket ticket)
    {
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(ServiceAccount, ticket.AiAgentOwnerEmployeeId);
        Assert.Equal(0, CurrentAssignments(w, ticket.TicketId));
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticket.TicketId);
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
    }

    // ---- 1. the AI resolves it ----

    [Fact]
    public async Task AiResolved_TicketIsClosedByTheAi_AndNeverTouchesTheCallCenterOrAPerson()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-resolved", new GenesysRequestTypeDto(Name: "Handover"));

        var result = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-resolved", ClosedByAi: true));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.True(result.ClosedByAi);
        var ticket = w.Ticket(ticketId);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Empty(w.F.Handoffs.All);
        Assert.Equal(0, TransfersToCallCenter(w, ticketId));
        Assert.NotEqual(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Trigger=CallCenterIntake"));
        Assert.All(w.Agents, agent => Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.AssignedEmployeeId == agent));
    }

    // ---- 2. the AI cannot resolve it: hand-off ----

    [Fact]
    public async Task AiCannotResolve_Handoff_OnATicketWhoseDepartmentWasChosenByItsRequestType_KeepsThatDepartment_FlagsTheHandoff_AndAssignsNobody()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-fail", new GenesysRequestTypeDto(Name: "Handover"));
        var beforeEscalation = w.Ticket(ticketId);
        Assert.Equal(w.Leasing.DepartmentId, beforeEscalation.CurrentDepartmentId);       // AI-classified: routed by request type, AI still owns the chat
        AssertStillTheAis(w, beforeEscalation);                                           // the configured rule did NOT pre-assign the leasing employee
        var priorityBefore = beforeEscalation.PriorityId;

        var result = await EscalateAsync(w, ticketId, "conv-fail");

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal("WaitingForAgent", result.HandoffStatus);
        var ticket = w.Ticket(ticketId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);                  // an explicit workflow chose it: not pulled into the Call Center
        Assert.Equal(0, TransfersToCallCenter(w, ticketId));
        Assert.Equal(w.Cs.DepartmentId, ticket.OriginatingDepartmentId);                   // the originating department is never rewritten
        AssertStillTheAis(w, ticket);                                                      // flagged only: still the AI's, nobody dealt
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(priorityBefore, ticket.PriorityId);                                   // Normal, untouched by the hand-off
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        var handoff = Assert.Single(w.F.Handoffs.All);
        Assert.Equal(HandoffTrigger.AiEscalated, handoff.Trigger);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);
        Assert.Equal(ticketId, handoff.TicketId);
    }

    [Fact]
    public async Task AiCannotResolve_Handoff_OnADefaultIntakeTicket_WaitsInTheCallCenterQueue_StillOwnedByTheAi_AndAssignsNobody()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-fail-queue");
        AssertStillTheAis(w, w.Ticket(ticketId));

        var result = await EscalateAsync(w, ticketId, "conv-fail-queue");

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal("WaitingForAgent", result.HandoffStatus);
        var ticket = w.Ticket(ticketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);            // the human queue
        Assert.Equal(w.Cs.DepartmentId, ticket.OriginatingDepartmentId);                   // never rewritten
        Assert.Equal(1, TransfersToCallCenter(w, ticketId));
        AssertStillTheAis(w, ticket);                                                      // a QUEUE move, not an assignment: no Round Robin, no owner
        // Assignment History never shows a person for it: every row it has names the AI Agent as the owner.
        Assert.All(w.F.AssignmentHistory.Entries.Where(h => h.TicketId == ticketId), h => Assert.Equal(ServiceAccount, h.NewEmployeeId));
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, Assert.Single(w.F.Handoffs.All).Status);
    }

    [Fact]
    public async Task AiCannotResolve_RedeliveredHandoff_ChangesNothing_NoSecondTransfer_NoAssignment_NoSecondWorkItem()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-retry");
        await EscalateAsync(w, ticketId, "conv-retry");
        var owner = w.Ticket(ticketId).CurrentOwnerEmployeeId;
        var audit = w.F.Audit.Entries.Count;
        var events = w.F.WorkflowEvents.All.Count;

        for (var i = 0; i < 3; i++)
        {
            var again = await EscalateAsync(w, ticketId, "conv-retry");
            Assert.Equal(GenesysTicketUpdateOutcome.Applied, again.Outcome);
        }

        Assert.Equal(owner, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        AssertStillTheAis(w, w.Ticket(ticketId));
        Assert.Single(w.F.Handoffs.All);
        Assert.Equal(1, TransfersToCallCenter(w, ticketId));
        Assert.Single(w.F.Tickets.All);
        Assert.Equal(audit, w.F.Audit.Entries.Count);
        Assert.Equal(events, w.F.WorkflowEvents.All.Count);
    }

    [Fact]
    public async Task AiCannotResolve_ASecondEscalationAfterTheFirstWasStoodDown_DoesNotMoveTheTicketOrItsOwnerAgain()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-resume");
        await EscalateAsync(w, ticketId, "conv-resume");
        var owner = w.Ticket(ticketId).CurrentOwnerEmployeeId;
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-resume", Handoff: new GenesysHandoffUpdateDto(Required: false, Reason: "AI resumed")));

        await EscalateAsync(w, ticketId, "conv-resume");

        Assert.Equal(owner, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.Equal(1, TransfersToCallCenter(w, ticketId));
        AssertStillTheAis(w, w.Ticket(ticketId));
    }

    // ---- 3. missing request type ----

    [Fact]
    public async Task MissingRequestType_TicketIsStillCreated_OnNormalPriority_AndAHandoffStillReachesTheHumanQueue()
    {
        var w = Build();

        var created = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-no-type"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
        var ticket = w.Ticket(created.Ticket!.TicketId);
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                                        // AI-handled: nobody is given it
        Assert.Empty(w.F.Handoffs.All);

        await EscalateAsync(w, ticket.TicketId, "conv-no-type");

        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        AssertStillTheAis(w, ticket);                                                      // reaches the human queue, nobody is dealt it
        Assert.Null(ticket.RequestTypeId);                                                 // still unclassified: a person classifies it
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
    }

    [Fact]
    public async Task InvalidRequestType_IsRefused_NothingIsCreated_AndNothingIsAssigned()
    {
        var w = Build();

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-bad-type", new GenesysRequestTypeDto(Name: "No Such Type")));

        Assert.Equal(GenesysIngestionOutcome.RequestTypeInvalid, result.Outcome);
        Assert.Empty(w.F.Tickets.All);
        Assert.Empty(w.F.Interactions.All);
        Assert.Empty(w.F.Handoffs.All);
        Assert.Empty(w.F.TicketAssignments.Added);
    }

    // ---- 4. no agent available ----

    [Fact]
    public async Task NoEligibleAgent_TicketWaitsInTheCallCenterQueue_WithItsHandoffOutstanding_AndNoAssignmentFailure_AndRetriesDoNotLoseOrDuplicateIt()
    {
        var w = Build(callCenterAgents: 0);
        var ticketId = await IngestAsync(w, "conv-nobody");

        var result = await EscalateAsync(w, ticketId, "conv-nobody");
        await EscalateAsync(w, ticketId, "conv-nobody");

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        var ticket = w.Ticket(ticketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);            // in the human queue, not lost
        AssertStillTheAis(w, ticket);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        var handoff = Assert.Single(w.F.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);
        Assert.Equal(1, TransfersToCallCenter(w, ticketId));
        // No assignment is attempted, so there is nothing to fail: no "could not assign" audit and no manual-assignment work item.
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString());
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.TicketId == ticketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    // ---- 5. duplicate requests ----

    [Fact]
    public async Task DuplicateCreate_ReturnsTheSameTicket_AndCreatesNothingElse()
    {
        var w = Build();
        var first = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-dup", new GenesysRequestTypeDto(Name: "Handover")));
        var tickets = w.F.Tickets.All.Count;
        var interactions = w.F.Interactions.All.Count;
        var audit = w.F.Audit.Entries.Count;

        var second = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-dup", new GenesysRequestTypeDto(Name: "Handover")));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, second.Outcome);
        Assert.Equal(first.Ticket!.TicketId, second.Ticket!.TicketId);
        Assert.Equal(tickets, w.F.Tickets.All.Count);
        Assert.Equal(interactions, w.F.Interactions.All.Count);
        Assert.Equal(audit, w.F.Audit.Entries.Count);
    }

    [Fact]
    public async Task DuplicateCreateAfterTheHandoff_DoesNotReassignOrReopenAnything()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-dup-late");
        await EscalateAsync(w, ticketId, "conv-dup-late");
        var owner = w.Ticket(ticketId).CurrentOwnerEmployeeId;

        var again = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-dup-late"));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, again.Outcome);
        Assert.Equal(owner, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        AssertStillTheAis(w, w.Ticket(ticketId));
        Assert.Single(w.F.Handoffs.All);
    }

    // ---- 6. an AI-resolved ticket is not left on a person just because a rule pre-assigned it ----

    [Fact]
    public async Task AiResolved_AConfiguredRuleNeverPreAssignsTheAiTicket_SoTheAiCanCloseIt_AndStaysItsOwner()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-pre-assigned", new GenesysRequestTypeDto(Name: "Handover"));
        AssertStillTheAis(w, w.Ticket(ticketId));                                          // the request-type rule would have picked the leasing employee: it is not applied

        var result = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-pre-assigned", ClosedByAi: true));

        Assert.True(result.ClosedByAi, result.ClosedByAiNote);
        Assert.Equal(TicketStatus.Closed, w.Ticket(ticketId).TicketStatus);
        Assert.Null(w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.Equal(ServiceAccount, w.Ticket(ticketId).AiAgentOwnerEmployeeId);           // closing keeps the AI as the owner of record
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticketId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoCloseByAi" && e.AfterValue!.Contains("AutomaticPreAssignmentReleased"));   // nothing was pre-assigned
        Assert.Empty(w.F.Handoffs.All);
        Assert.Equal(0, TransfersToCallCenter(w, ticketId));
    }

    [Fact]
    public async Task AiResolved_ButAPersonHasTakenTheTicket_IsNotClosedByTheAi()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-person-owned", new GenesysRequestTypeDto(Name: "Handover"));
        var ticket = w.Ticket(ticketId);
        var person = Guid.NewGuid();
        // A person (not the system) took it: the current assignment names them as the assigning actor.
        w.F.TicketAssignments.Added.Add(new TicketAssignment(ticketId, person, ticket.CurrentDepartmentId, DateTime.UtcNow, assigningActorEmployeeId: person));
        ticket.AssignTo(person);

        var result = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-person-owned", ClosedByAi: true));

        Assert.False(result.ClosedByAi);
        Assert.Contains("person owns", result.ClosedByAiNote);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(person, ticket.CurrentOwnerEmployeeId);
    }

    // ---- 7. a hand-off must never be recorded against a ticket nobody can work ----

    [Fact]
    public async Task HandoffOnAnAlreadyClosedTicket_IsRefusedUpFront_NothingIsQueuedOrMoved()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-closed");
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-closed", ClosedByAi: true));
        Assert.Equal(TicketStatus.Closed, w.Ticket(ticketId).TicketStatus);
        var audit = w.F.Audit.Entries.Count;

        var result = await EscalateAsync(w, ticketId, "conv-closed");

        Assert.Equal(GenesysTicketUpdateOutcome.TicketClosed, result.Outcome);
        Assert.Contains("new ticket", result.Detail);
        Assert.Empty(w.F.Handoffs.All);
        Assert.Equal(0, TransfersToCallCenter(w, ticketId));
        // Nothing is queued or moved. The refusal leaves exactly ONE audit row - the evidence that a late event reached a Closed ticket.
        Assert.Equal(audit + 1, w.F.Audit.Entries.Count);
        Assert.Equal(GenesysAuditActions.EventIgnoredOnClosedTicket, w.F.Audit.Entries[^1].Action);
        Assert.Contains("TicketChanged=false", w.F.Audit.Entries[^1].AfterValue);
        Assert.Equal(TicketStatus.Closed, w.Ticket(ticketId).TicketStatus);
    }

    [Fact]
    public async Task HandoffAndClosedByAiInTheSameUpdate_TheHumanRequestWins_AndTheTicketStaysOpenForThem()
    {
        var w = Build();
        var ticketId = await IngestAsync(w, "conv-both");

        var result = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto("conv-both", ClosedByAi: true, Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "AiEscalated")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.False(result.ClosedByAi);
        Assert.Equal(TicketStatus.Open, w.Ticket(ticketId).TicketStatus);
        Assert.Single(w.F.Handoffs.All);
        AssertStillTheAis(w, w.Ticket(ticketId));                                          // waits for a person, dealt to nobody
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
    }
}
