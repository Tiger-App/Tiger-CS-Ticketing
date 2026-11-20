using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Regression coverage for ticket TG-LCS-20261021-0002: a customer chatting normally with the AI saw "System — asked for a human agent",
/// "Waiting for agent" and "Awaiting classification: no request type was supplied" although nobody had asked for a human.
///
/// <para>
/// The rules pinned here: a human handoff exists only when Genesys states one explicitly; a missing request type leaves the ticket
/// Unclassified and nothing else; ending a conversation is not a request for a human; the AI may refine its own classification until a
/// person has worked the ticket (history kept); bot lines never satisfy First Response; a shared phone number never names a customer.
/// </para>
/// </summary>
public class GenesysHandoffAndClassificationRegressionTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private sealed record World(
        GenesysServiceFixture F, Department Arrival, Department Leasing, Department Finance, RequestType Handover, RequestType Receipt);

    private static World Build(bool autoRaiseHandoffOnConversationEnd = false)
    {
        var f = new GenesysServiceFixture(autoRaiseHandoffOnConversationEnd: autoRaiseHandoffOnConversationEnd);
        var (arrival, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.Departments.AddDepartment("Leasing", "LCS");
        var finance = f.Departments.AddDepartment("Finance", "FIN");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var handover = f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, "Handover", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var receipt = f.RequestTypes.Add(new RequestType(
            finance.DepartmentId, "Payment Receipt", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        return new World(f, arrival, leasing, finance, handover, receipt);
    }

    private static GenesysInquiryDto Inquiry(World w, string conversationId, GenesysRequestTypeDto? requestType = null) =>
        new(conversationId, GenesysChannel.WebsiteChat,
            CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali",
            DepartmentId: w.Arrival.DepartmentId, RequestType: requestType);

    private static async Task<Ticket> IngestAsync(World w, string conversationId, GenesysRequestTypeDto? requestType = null)
    {
        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, conversationId, requestType));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    private static GenesysTranscriptMessageDto Customer(string body, DateTime at, string? id = null) =>
        new("Customer", at, body, SenderName: "Ahmed Ali", ExternalMessageId: id);

    private static GenesysTranscriptMessageDto Bot(string body, DateTime at, string? id = null) =>
        new("VirtualAgent", at, body, SenderName: "Tiger Bot", SenderId: "bot-1", ExternalMessageId: id);

    private static GenesysTranscriptMessageDto Human(string body, DateTime at, string? id = null) =>
        new("HumanAgent", at, body, SenderName: "Layla", SenderId: "ga-7", ExternalMessageId: id);

    private static void AssertNoHumanWasRequested(World w)
    {
        Assert.Empty(w.F.Handoffs.All);
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffRequested);
    }

    private static async Task<string?> HandlingStateAsync(World w, Ticket ticket)
    {
        var history = await w.F.InteractionQuery.GetForTicketAsync(Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId);
        return Assert.Single(history.Response!.Interactions).HandlingState;
    }

    // ---- AI-only conversation / normal question / no handoff signal ----

    [Fact]
    public async Task AiOnlyConversation_WithACustomerQuestion_RaisesNoHandoff_AndIsReportedAsAiHandled()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-ai-only", new GenesysRequestTypeDto(Name: "Handover"));
        var now = DateTime.UtcNow;

        var update = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-ai-only",
            Ended: new GenesysConversationEndUpdateDto(now, "CustomerDisconnect",
            [
                Customer("How does the handover process work?", now.AddMinutes(-3)),
                Bot("Handover happens after the final payment. Shall I send the checklist?", now.AddMinutes(-2))
            ])));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        Assert.Null(update.HandoffStatus);
        AssertNoHumanWasRequested(w);
        Assert.Equal("Ai", await HandlingStateAsync(w, ticket));
        Assert.Null(ticket.FirstHumanResponseAtUtc);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    [Fact]
    public async Task NoHandoffSignal_AnUpdateCarryingOnlyTranscriptAndRouting_NeverCreatesHumanWork()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-nosignal");

        var update = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-nosignal", Routing: new GenesysRoutingUpdateDto("q-1", "Chatbot Queue"), AwaitingCustomerReply: true));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        AssertNoHumanWasRequested(w);
    }

    [Fact]
    public async Task ConversationEndedWithoutAHandoff_RecordsTheEnd_ButNeverARequestForAHuman_AndDoesNotMoveTheTicket()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-ended");
        var statusBefore = ticket.TicketStatus;
        var endedAt = DateTime.UtcNow;

        var end = await w.F.ConversationEnd.EndAsync(ServiceAccount, new GenesysConversationEndDto(
            "conv-ended", endedAt, "AiCompleted", null, null,
            [Customer("Thanks, that is all", endedAt.AddMinutes(-1)), Bot("You're welcome!", endedAt.AddSeconds(-30))]));

        Assert.Equal(GenesysConversationEndOutcome.Ended, end.Outcome);
        Assert.True(w.F.InteractionFor("conv-ended")!.IsEnded);
        Assert.Equal(statusBefore, ticket.TicketStatus);
        AssertNoHumanWasRequested(w);
    }

    [Fact]
    public async Task TheLegacyEndOfConversationSafetyNet_IsOptIn_AndWhenEnabledStillSaysItWasRaisedByTheSystem()
    {
        var w = Build(autoRaiseHandoffOnConversationEnd: true);
        var ticket = await IngestAsync(w, "conv-lever");
        var endedAt = DateTime.UtcNow;

        await w.F.ConversationEnd.EndAsync(ServiceAccount, new GenesysConversationEndDto(
            "conv-lever", endedAt, "Disconnected", null, null, [Bot("Hello?", endedAt.AddMinutes(-1))]));

        var handoff = Assert.Single(w.F.Handoffs.All);
        Assert.Equal(HandoffTrigger.AiConnectionLost, handoff.Trigger);
        Assert.Equal(ticket.TicketId, handoff.TicketId);
    }

    // ---- Classification ----

    [Fact]
    public async Task AiClassifiesAHandoverInquiry_RoutesToLeasing_WithNoHandoff()
    {
        var w = Build();

        var ticket = await IngestAsync(w, "conv-handover", new GenesysRequestTypeDto(Name: "Handover"));

        Assert.Equal(w.Handover.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Arrival.DepartmentId, ticket.OriginatingDepartmentId);
        AssertNoHumanWasRequested(w);
    }

    [Fact]
    public async Task MissingRequestType_StaysUnclassified_OnTheDefaultPriority_WithNoHandoffAndNoHandoffEvent()
    {
        var w = Build();

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-missing"));
        var ticket = Assert.Single(w.F.Tickets.All);

        Assert.Equal("AwaitingClassification", result.Classification!.Status);
        Assert.Null(ticket.RequestTypeId);
        Assert.False(ticket.IsClassified);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.Equal(w.Arrival.DepartmentId, ticket.CurrentDepartmentId);
        AssertNoHumanWasRequested(w);
        Assert.Equal("Ai", await HandlingStateAsync(w, ticket));
    }

    [Fact]
    public async Task InvalidRequestType_IsRefused_WithNothingWritten()
    {
        var w = Build();
        var before = (w.F.Tickets.All.Count, w.F.Interactions.All.Count, w.F.Audit.Entries.Count);

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-bad", new GenesysRequestTypeDto(Name: "Not A Thing")));

        Assert.Equal(GenesysIngestionOutcome.RequestTypeInvalid, result.Outcome);
        Assert.Equal(before, (w.F.Tickets.All.Count, w.F.Interactions.All.Count, w.F.Audit.Entries.Count));
        AssertNoHumanWasRequested(w);
    }

    [Fact]
    public async Task AnUpdateThatWillBeRefusedForItsHandoffVocabulary_DoesNotLeaveItsClassificationBehind()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-partial");

        var update = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-partial",
            RequestType: new GenesysRequestTypeDto(w.Handover.RequestTypeId),
            Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "TheCustomerSeemedAnnoyed")));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidHandoffTrigger, update.Outcome);
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal(w.Arrival.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Empty(w.F.Handoffs.All);
    }

    [Fact]
    public async Task ClassificationChanges_WhileNoHumanHasWorkedTheTicket_KeepHistory_AndTransferWithOriginatingDepartmentUntouched()
    {
        var w = Build();
        var financeEmployee = Guid.NewGuid();
        w.F.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(financeEmployee, w.Finance.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.Receipt.RequestTypeId, financeEmployee));

        var ticket = await IngestAsync(w, "conv-refine", new GenesysRequestTypeDto(w.Handover.RequestTypeId));
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);

        // The customer's real question turns out to be a payment receipt: the AI refines its own classification.
        var refined = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-refine", RequestType: new GenesysRequestTypeDto(w.Receipt.RequestTypeId)));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, refined.Outcome);
        Assert.Equal(w.Receipt.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);          // transferred
        Assert.Equal(w.Arrival.DepartmentId, ticket.OriginatingDepartmentId);       // write-once, untouched
        Assert.Equal(financeEmployee, ticket.CurrentOwnerEmployeeId);               // assignment re-run for the receiving department

        // History: an append-only event and an audit entry name the previous and the new request type.
        var evt = Assert.Single(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.RequestTypeReclassified);
        Assert.Contains("Payment Receipt", evt.Note);
        Assert.Contains(w.F.Audit.Entries, e =>
            e.Action == "ReclassifyRequestType"
            && e.BeforeValue!.Contains($"RequestTypeId={w.Handover.RequestTypeId}", StringComparison.Ordinal)
            && e.AfterValue!.Contains($"RequestTypeId={w.Receipt.RequestTypeId}", StringComparison.Ordinal));
        AssertNoHumanWasRequested(w);
    }

    [Fact]
    public async Task ClassificationChange_IsRefused_OnceAPersonHasRespondedToTheCustomer_AndNothingMoves()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-locked", new GenesysRequestTypeDto(w.Handover.RequestTypeId));
        ticket.RecordFirstHumanResponse(DateTime.UtcNow);

        var update = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-locked", RequestType: new GenesysRequestTypeDto(w.Receipt.RequestTypeId)));

        Assert.Equal(GenesysTicketUpdateOutcome.RequestTypeConflict, update.Outcome);
        Assert.Equal(w.Handover.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.RequestTypeReclassified);
    }

    [Fact]
    public async Task ClassificationChange_IsRefused_WhenAPersonClassifiedTheTicketByHand()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-manual", new GenesysRequestTypeDto(w.Handover.RequestTypeId));
        ticket.Classify(categoryId: 3, priorityId: (byte)PriorityLevel.Medium);

        var update = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-manual", RequestType: new GenesysRequestTypeDto(w.Receipt.RequestTypeId)));

        Assert.Equal(GenesysTicketUpdateOutcome.RequestTypeConflict, update.Outcome);
        Assert.Equal(w.Handover.RequestTypeId, ticket.RequestTypeId);
    }

    // ---- Human handoff: only on an explicit signal, with its initiator ----

    [Theory]
    [InlineData("CustomerRequestedHuman", "customer asked for a human agent")]
    [InlineData("AiEscalated", "AI escalated")]
    [InlineData("RoutingDecision", "routing rule")]
    public async Task AnExplicitHandoffSignal_IsRecordedWithItsInitiator_AndOnlyThenIsAHumanWaited(string trigger, string _)
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-handoff-" + trigger);

        var update = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-handoff-" + trigger,
            Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: trigger, Reason: "stated by Genesys")));

        Assert.Equal("WaitingForAgent", update.HandoffStatus);
        var handoff = Assert.Single(w.F.Handoffs.All);
        Assert.Equal(Enum.Parse<HandoffTrigger>(trigger), handoff.Trigger);
        Assert.Equal("WaitingForHuman", await HandlingStateAsync(w, ticket));
        var evt = Assert.Single(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffRequested);
        Assert.StartsWith(trigger, evt.Note);
    }

    [Fact]
    public async Task ARealHumanAcceptingTheInteraction_IsShownAsHumanHandled()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-human");
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-human", Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "CustomerRequestedHuman")));

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-human", AgentName: "Layla", Handoff: new GenesysHandoffUpdateDto(AssignedAgentId: "ga-7")));

        Assert.Equal("Human", await HandlingStateAsync(w, ticket));
    }

    // ---- Duplicate events ----

    [Fact]
    public async Task DuplicateGenesysEvents_CreateOneTicket_OneHandoff_AndNoDuplicateMessages()
    {
        var w = Build();
        var first = await IngestAsync(w, "conv-dup");
        var again = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-dup"));
        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, again.Outcome);
        Assert.Single(w.F.Tickets.All);

        var handoffUpdate = new GenesysTicketUpdateDto(
            "conv-dup", Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "CustomerRequestedHuman", WorkItemId: "wi-1"));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, first.TicketId, handoffUpdate);
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, first.TicketId, handoffUpdate);
        Assert.Single(w.F.Handoffs.All);
        Assert.Single(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffRequested);

        var endedAt = DateTime.UtcNow;
        var end = new GenesysTicketUpdateDto("conv-dup", Ended: new GenesysConversationEndUpdateDto(endedAt, "Done",
        [
            Customer("hello", endedAt.AddMinutes(-2), "m-1"),
            Bot("hi", endedAt.AddMinutes(-1), "m-2")
        ]));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, first.TicketId, end);
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, first.TicketId, end);
        Assert.Equal(2, w.F.Conversations.AllMessages.Count(m => m.TicketInteractionId == w.F.InteractionFor("conv-dup")!.TicketInteractionId));
    }

    // ---- SLA: bot lines never stop First Response ----

    [Fact]
    public async Task BotMessages_DoNotRecordFirstResponse_TheFirstHumanMessageDoes()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-sla");
        var t0 = DateTime.UtcNow.AddMinutes(-10);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-sla", Ended: new GenesysConversationEndUpdateDto(t0.AddMinutes(5), "Transferred",
            [Customer("hi", t0), Bot("Hello, how can I help?", t0.AddSeconds(2)), Bot("One moment", t0.AddSeconds(30))])));
        Assert.Null(ticket.FirstHumanResponseAtUtc);                // a machine answering in seconds is not a first response

        var humanAt = t0.AddMinutes(4);
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-sla", Ended: new GenesysConversationEndUpdateDto(t0.AddMinutes(5), "Transferred",
            [Human("Hello, this is Layla", humanAt)])));
        Assert.Equal(humanAt, ticket.FirstHumanResponseAtUtc);
    }

    // ---- CRM ambiguity ----

    [Fact]
    public async Task APhoneNumberSharedByTwoCustomers_NeverNamesTheConversation_NorVerifiesNorLinksAUnit()
    {
        var w = Build();
        w.F.CrmBuyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(new CrmCustomerDto(9001, "Sami Nasser", null, "+971500000001", null),
                [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Nobles Tower", null, 1, "Buyer")]),
            new CrmBuyerMatchDto(new CrmCustomerDto(9002, "Mona Khalil", null, "+971500000001", null),
                [new CrmBuyerUnitDto(2, 5, "Contract", 202, "2203", 1, 2, 22, 11, "Other Tower", null, 1, "Buyer")])
        ]));

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount,
            new GenesysInquiryDto("conv-shared", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: w.Arrival.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Null(ticket.CrmBuyerCustomerId);
        Assert.Null(ticket.CrmBuyerUnitId);
        Assert.Null(w.F.InteractionFor("conv-shared")!.CustomerName);          // neither "Sami Nasser" nor "Mona Khalil"
        Assert.Contains(w.F.Audit.Entries, a => a.Action == "GenesysInquiryIngested" && a.AfterValue!.Contains("Crm=AmbiguousMatch", StringComparison.Ordinal));
    }

    // ---- Legacy rows written by the previous behaviour ----

    [Fact]
    public async Task ALegacyAwaitingClassificationHandoff_IsStoodDown_WhenTheTicketIsLaterClassified_ButACustomerRequestIsNot()
    {
        var w = Build();
        var legacy = await IngestAsync(w, "conv-legacy");
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(
            "conv-legacy", AgentAvailable: false, Reason: "Awaiting classification: no request type was supplied.",
            Trigger: nameof(HandoffTrigger.RoutingDecision)));
        var requested = await IngestAsync(w, "conv-customer");
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(
            "conv-customer", AgentAvailable: false, Reason: "I want to talk to someone", Trigger: nameof(HandoffTrigger.CustomerRequestedHuman)));

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, legacy.TicketId, new GenesysTicketUpdateDto(
            "conv-legacy", RequestType: new GenesysRequestTypeDto(w.Handover.RequestTypeId)));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, requested.TicketId, new GenesysTicketUpdateDto(
            "conv-customer", RequestType: new GenesysRequestTypeDto(w.Handover.RequestTypeId)));

        Assert.Equal(AgentHandoffStatus.Cancelled, w.F.Handoffs.All.Single(h => h.TicketId == legacy.TicketId).Status);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, w.F.Handoffs.All.Single(h => h.TicketId == requested.TicketId).Status);
    }
}
