using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CrmDocuments;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The UAT scenario: in ONE Genesys conversation the customer asks for CRM documents (emailed, ticket closed) and then for apartment
/// leasing. Each distinct service request is its own ticket with its own lifecycle; retries are idempotent per (conversation, service
/// request); a conversation whose caller sends no service request id keeps the original "one conversation, one ticket" rule; the leasing
/// request routes through the configured request type; human follow-up tickets are never closed by a chatbot clock, the AI, document
/// delivery or the conversation ending; and the conversation's transcript is visible on every ticket of it.
/// </summary>
public class GenesysMultipleTicketsPerConversationTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime T0 = DateTime.UtcNow.AddMinutes(-30);
    private const string Conversation = "conv-uat-1";
    private const string Phone = "+971500000001";
    private const string DocumentsRequest = "documents";
    private const string LeasingRequest = "leasing";

    /// <summary>Customer Service (the bot's queue → department), Leasing (responsible for the Apartment Leasing request type) and a Leasing employee with an assignment rule.</summary>
    private sealed record World(
        GenesysServiceFixture F, Department CustomerService, Department Leasing, RequestType ApartmentLeasing, Guid LeasingEmployee);

    private static World Build(int? inactivityMinutes = null)
    {
        var f = new GenesysServiceFixture(inactivityTimeoutMinutes: inactivityMinutes);
        var (customerService, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.Departments.AddDepartment("Leasing", "LS");
        f.QueueMappings.Map("queue-bot", customerService.DepartmentId);
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var apartmentLeasing = f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, "Apartment Leasing", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));

        var employee = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(employee, leasing.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        f.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(apartmentLeasing.RequestTypeId, employee));
        return new World(f, customerService, leasing, apartmentLeasing, employee);
    }

    private static GenesysInquiryDto DocumentsInquiry(string? serviceRequestId = DocumentsRequest, string conversation = Conversation) =>
        new(conversation, GenesysChannel.WebsiteChat, CustomerPhone: Phone, CustomerName: "Ahmed Ali",
            QueueId: "queue-bot", Subject: "CRM documents", ServiceRequestId: serviceRequestId);

    private static GenesysInquiryDto LeasingInquiry(
        World w, string? serviceRequestId = LeasingRequest, string conversation = Conversation, bool humanFollowUp = true) =>
        new(conversation, GenesysChannel.WebsiteChat, CustomerPhone: Phone, CustomerName: "Ahmed Ali",
            QueueId: "queue-bot", Subject: "Apartment leasing", ServiceRequestId: serviceRequestId,
            HumanFollowUpRequired: humanFollowUp, RequestType: new GenesysRequestTypeDto(w.ApartmentLeasing.RequestTypeId));

    private static async Task<Ticket> IngestAsync(World w, GenesysInquiryDto inquiry)
    {
        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, inquiry);
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    private static GenesysTranscriptMessageDto Customer(string body, int minute, string? id = null) =>
        new("Customer", T0.AddMinutes(minute), body, SenderName: "Ahmed Ali", ExternalMessageId: id);

    private static GenesysTranscriptMessageDto Bot(string body, int minute, string? id = null) =>
        new("VirtualAgent", T0.AddMinutes(minute), body, SenderName: "Tiger Bot", SenderId: "bot-1", ExternalMessageId: id);

    private static DocumentDeliveryTicketService DocumentsFor(GenesysServiceFixture f)
    {
        var deliveries = new FakeCrmDocumentDeliveryRepository();
        return new DocumentDeliveryTicketService(
            new CrmDocumentOptions { Enabled = true }, f.Conversations, f.Tickets, f.Handoffs, f.Interactions, deliveries, f.Lifecycle,
            f.Audit, new CommittingUnitOfWork(deliveries), f.Clock, NullLogger<DocumentDeliveryTicketService>.Instance);
    }

    private static DocumentDeliveryTicketContext DocContext(string? serviceRequestId) =>
        new(ServiceAccount, Guid.NewGuid(), Conversation, null, DocumentServiceFixture.CustomerId, Phone, serviceRequestId);

    private static DocumentDeliveryTicketUpdate Sent() =>
        new(DocumentDeliveryTicketEvent.Sent, "Contract", "REC-1", null, "The document was sent to the customer's email on record.", 1);

    private static async Task<ChatbotInactivityCloseOutcome> RunJobAsync(GenesysServiceFixture f)
    {
        var outcome = ChatbotInactivityCloseOutcome.NotDue;
        foreach (var id in await f.InactivityClose.ListDueAsync())
        {
            outcome = await f.InactivityClose.TryCloseAsync(id);
        }

        return outcome;
    }

    // ---- 1 & 2. The UAT scenario: documents completed, then leasing, in the same conversation ----

    [Fact]
    public async Task DocumentsCompleted_ThenLeasingRequested_InTheSameConversation_CreatesASeparateOpenLeasingTicket()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());

        // Step 2 of the scenario: the document is emailed and its ticket is closed (existing, valid behaviour).
        await DocumentsFor(w.F).HandleAsync(DocContext(DocumentsRequest), Sent());
        Assert.Equal(TicketStatus.Closed, documents.TicketStatus);

        // Step 3: the same conversation asks for apartment leasing.
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        Assert.NotEqual(documents.TicketId, leasing.TicketId);
        Assert.NotEqual(documents.TicketNumber, leasing.TicketNumber);
        Assert.Equal(TicketStatus.Closed, documents.TicketStatus);          // untouched by the new request
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);               // the human follow-up is open
        Assert.Equal(w.Leasing.DepartmentId, leasing.CurrentDepartmentId);   // routed by the configured request type
        Assert.Equal(w.CustomerService.DepartmentId, leasing.OriginatingDepartmentId);
        Assert.Equal(w.ApartmentLeasing.RequestTypeId, leasing.RequestTypeId);
    }

    [Fact]
    public async Task TwoServiceRequests_AreTwoTickets_LinkedToOneConversation_EachWithItsOwnInteraction()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        var interactions = w.F.Interactions.All.Where(i => i.GenesysConversationId == Conversation).OrderBy(i => i.TicketInteractionId).ToList();
        Assert.Equal(2, interactions.Count);
        Assert.Equal([documents.TicketId, leasing.TicketId], interactions.Select(i => i.TicketId));
        Assert.Equal([DocumentsRequest, LeasingRequest], interactions.Select(i => i.GenesysServiceRequestId));
        Assert.All(interactions, i => Assert.True(i.IsOriginatingInteraction)); // each ticket's own originating interaction
        Assert.False(interactions[0].RequiresHumanFollowUp);
        Assert.True(interactions[1].RequiresHumanFollowUp);
        Assert.Equal(2, w.F.Tickets.All.Count);
    }

    // ---- 3. Idempotency ----

    [Fact]
    public async Task RepeatedRequest_ForTheSameServiceRequest_ReturnsTheOriginalTicket_AndCreatesNoDuplicate()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        var documentsRetry = await w.F.Ingestion.IngestAsync(ServiceAccount, DocumentsInquiry());
        var leasingRetry = await w.F.Ingestion.IngestAsync(ServiceAccount, LeasingInquiry(w));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, documentsRetry.Outcome);
        Assert.Equal(documents.TicketId, documentsRetry.Ticket!.TicketId);
        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, leasingRetry.Outcome);
        Assert.Equal(leasing.TicketId, leasingRetry.Ticket!.TicketId);
        Assert.Equal(2, w.F.Tickets.All.Count);
        Assert.Equal(2, w.F.Interactions.All.Count);
    }

    [Fact]
    public async Task ServiceRequestId_IsTrimmed_SoWhitespaceNeverMakesARetryLookNew()
    {
        var w = Build();
        var first = await IngestAsync(w, DocumentsInquiry(" documents "));

        var retry = await w.F.Ingestion.IngestAsync(ServiceAccount, DocumentsInquiry("documents"));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, retry.Outcome);
        Assert.Equal(first.TicketId, retry.Ticket!.TicketId);
    }

    [Fact]
    public async Task ServiceRequestIds_AreScopedToTheirConversation()
    {
        var w = Build();
        await IngestAsync(w, DocumentsInquiry(conversation: "conv-a"));

        var other = await w.F.Ingestion.IngestAsync(ServiceAccount, DocumentsInquiry(conversation: "conv-b"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, other.Outcome);
        Assert.Equal(2, w.F.Tickets.All.Count);
    }

    // ---- Backward compatibility ----

    [Fact]
    public async Task WithoutAServiceRequestId_TheConversationStaysOneTicket_AsBeforeEveryExistingIntegration()
    {
        var w = Build();
        var first = await IngestAsync(w, DocumentsInquiry(serviceRequestId: null));

        // An integration that never sends a service request id: a second, different request in the same conversation is still answered
        // with the conversation's one ticket (the original contract) — nothing is created.
        var second = await w.F.Ingestion.IngestAsync(ServiceAccount, LeasingInquiry(w, serviceRequestId: null));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, second.Outcome);
        Assert.Equal(first.TicketId, second.Ticket!.TicketId);
        Assert.Single(w.F.Tickets.All);
        Assert.Null(Assert.Single(w.F.Interactions.All).GenesysServiceRequestId);
    }

    [Fact]
    public async Task ALegacyTicket_WithNoServiceRequestId_IsStillFoundByItsConversation_AndStillUpdatable()
    {
        var w = Build();
        var legacy = await IngestAsync(w, DocumentsInquiry(serviceRequestId: null));

        var update = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, legacy.TicketId, new GenesysTicketUpdateDto(Conversation, Transcript: [Customer("hi", 0, "m-1")]));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        Assert.Equal(1, update.TranscriptMessageCount);
    }

    // ---- 4. Request type routing ----

    [Fact]
    public async Task LeasingRequest_RoutesToTheDepartmentTheRequestTypeIsConfiguredFor_AndAutoAssigns()
    {
        var w = Build();

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, LeasingInquiry(w));

        var ticket = w.F.Tickets.All.Single();
        Assert.Equal("Classified", result.Classification!.Status);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.LeasingEmployee, ticket.CurrentOwnerEmployeeId);          // the normal department auto-assignment ran
        Assert.Equal(w.Leasing.DepartmentId, result.Ticket!.CurrentDepartmentId);
    }

    [Fact]
    public async Task LeasingRequest_ByRequestTypeName_RoutesTheSameWay_NoDepartmentIdIsHardCoded()
    {
        var w = Build();
        var inquiry = LeasingInquiry(w) with { RequestType = new GenesysRequestTypeDto(Name: "apartment leasing") };

        var ticket = await IngestAsync(w, inquiry);

        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task ARequestTypeAlone_WithNoDepartmentOrQueue_RoutesToTheRequestTypesOwnDepartment()
    {
        var w = Build();
        var inquiry = new GenesysInquiryDto(
            Conversation, GenesysChannel.WebsiteChat, CustomerPhone: Phone, ServiceRequestId: LeasingRequest,
            RequestType: new GenesysRequestTypeDto(w.ApartmentLeasing.RequestTypeId));

        var ticket = await IngestAsync(w, inquiry);

        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Contains(w.F.Audit.Entries, e =>
            e.Action == "GenesysInquiryIngested" && e.AfterValue!.Contains("DepartmentSource=RequestTypeDepartment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoRoutingConfiguration_AndNoClassificationQueue_RefusesRatherThanRoutingOnAGuess()
    {
        var w = Build();
        var inquiry = new GenesysInquiryDto(Conversation, GenesysChannel.WebsiteChat, CustomerPhone: Phone, ServiceRequestId: LeasingRequest);

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, inquiry);

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
        Assert.Empty(w.F.Tickets.All);
    }

    [Fact]
    public async Task NoRoutingConfiguration_WithAClassificationQueue_CreatesAnUnclassifiedTicketThere()
    {
        var w = Build();
        w.F.Options.ClassificationDepartmentCode = "CLASSIFY";
        var triage = w.F.Departments.AddDepartment("Classification", "CLASSIFY");
        var inquiry = new GenesysInquiryDto(Conversation, GenesysChannel.WebsiteChat, CustomerPhone: Phone, ServiceRequestId: LeasingRequest);

        var ticket = await IngestAsync(w, inquiry);

        Assert.Equal(triage.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.RequestTypeId);
        Assert.False(ticket.IsClassified);
        Assert.Empty(w.F.Handoffs.All); // a missing classification is still not a request for a human
    }

    [Fact]
    public async Task AnUnusableRequestType_WithAClassificationQueue_IsNotLost_ItIsQueuedForClassification_NotRoutedOnAGuess()
    {
        var w = Build();
        w.F.Options.ClassificationDepartmentCode = "CLASSIFY";
        var triage = w.F.Departments.AddDepartment("Classification", "CLASSIFY");
        var inquiry = LeasingInquiry(w) with { RequestType = new GenesysRequestTypeDto(Name: "Teleportation") };

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, inquiry);

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = w.F.Tickets.All.Single();
        Assert.Equal(triage.DepartmentId, ticket.CurrentDepartmentId);   // not the bot queue's Customer Service
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal("AwaitingClassification", result.Classification!.Status);
        Assert.Contains("Teleportation", result.Classification.Detail);
        Assert.Contains(w.F.Audit.Entries, e =>
            e.Action == "GenesysInquiryIngested" && e.AfterValue!.Contains("DepartmentSource=ClassificationQueue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnusableRequestType_WithoutAClassificationQueue_IsStillRefused_AsBefore()
    {
        var w = Build();
        var inquiry = LeasingInquiry(w) with { RequestType = new GenesysRequestTypeDto(Name: "Teleportation") };

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, inquiry);

        Assert.Equal(GenesysIngestionOutcome.RequestTypeInvalid, result.Outcome);
        Assert.Empty(w.F.Tickets.All);
    }

    [Fact]
    public async Task AnExplicitDepartment_IsRespected_EvenWhenTheRequestTypeIsUnusableAndAClassificationQueueExists()
    {
        var w = Build();
        w.F.Options.ClassificationDepartmentCode = "CLASSIFY";
        w.F.Departments.AddDepartment("Classification", "CLASSIFY");
        var inquiry = LeasingInquiry(w) with
        {
            DepartmentId = w.Leasing.DepartmentId, QueueId = null, RequestType = new GenesysRequestTypeDto(Name: "Teleportation")
        };

        var ticket = await IngestAsync(w, inquiry);

        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task AClassificationQueueThatIsNotConfiguredInTheDepartmentsTable_IsTreatedAsNoQueue()
    {
        var w = Build();
        w.F.Options.ClassificationDepartmentCode = "DOES-NOT-EXIST";
        var inquiry = new GenesysInquiryDto(Conversation, GenesysChannel.WebsiteChat, CustomerPhone: Phone, ServiceRequestId: LeasingRequest);

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, inquiry);

        Assert.Equal(GenesysIngestionOutcome.DepartmentNotResolved, result.Outcome);
    }

    // ---- 5. Closure rules ----

    [Fact]
    public async Task EndingTheConversation_NeverClosesAHumanFollowUpTicket_NorItsSibling()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        var ended = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, leasing.TicketId,
            new GenesysTicketUpdateDto(Conversation, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "CustomerDisconnect")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, ended.Outcome);
        Assert.Equal("Open", ended.TicketStatus);
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);
        // Ending is per interaction: the sibling's interaction is still live, its ticket untouched.
        Assert.True(w.F.Interactions.All.Single(i => i.TicketId == leasing.TicketId).IsEnded);
        Assert.False(w.F.Interactions.All.Single(i => i.TicketId == documents.TicketId).IsEnded);
        Assert.Equal(TicketStatus.Open, documents.TicketStatus);
    }

    [Fact]
    public async Task TheChatbotInactivityRules_NeverCloseAHumanFollowUpTicket_ButStillCloseACompletedChatbotRequest()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));
        // The bot keeps asking questions on BOTH requests (awaitingCustomerReply) — the 5-minute rule — and the customer goes silent.
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, documents.TicketId, new GenesysTicketUpdateDto(Conversation, AwaitingCustomerReply: true));
        var timer = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, leasing.TicketId, new GenesysTicketUpdateDto(Conversation, AwaitingCustomerReply: true));
        Assert.False(timer.AwaitingCustomerReply);
        Assert.Contains("human follow-up", timer.AwaitingCustomerReplyNote, StringComparison.OrdinalIgnoreCase);

        w.F.Clock.Advance(TimeSpan.FromMinutes(45));
        while (await RunJobAsync(w.F) == ChatbotInactivityCloseOutcome.Closed)
        {
        }

        Assert.Equal(TicketStatus.Closed, documents.TicketStatus);   // existing, valid chatbot closure is preserved
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);       // human follow-up stays open
        Assert.Null(w.F.Interactions.All.Single(i => i.TicketId == leasing.TicketId).InactivityClosedAtUtc);
    }

    [Fact]
    public async Task ATicketCreatedAsHumanFollowUp_IsNeverTrackedForCustomerInactivity()
    {
        var w = Build();
        var leasing = await IngestAsync(w, LeasingInquiry(w));
        var interaction = w.F.Interactions.All.Single();

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, leasing.TicketId, new GenesysTicketUpdateDto(Conversation, CustomerActive: true));

        Assert.Null(interaction.LastCustomerInteractionAtUtc);
        w.F.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Empty(await w.F.InactivityClose.ListDueAsync());
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);
    }

    [Fact]
    public async Task HumanFollowUpRequired_OnAPatch_StopsTheClocks_ForGood_AndIsIdempotent()
    {
        var w = Build();
        var ticket = await IngestAsync(w, LeasingInquiry(w, humanFollowUp: false));
        var interaction = w.F.Interactions.All.Single();
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(Conversation, CustomerActive: true));
        Assert.NotNull(interaction.LastCustomerInteractionAtUtc); // tracked until the AI says a person is needed

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(Conversation, HumanFollowUpRequired: true));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(Conversation, HumanFollowUpRequired: true));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(Conversation, CustomerActive: true));

        Assert.True(interaction.RequiresHumanFollowUp);
        Assert.Null(interaction.LastCustomerInteractionAtUtc);
        Assert.Single(w.F.Audit.Entries, e => e.Action == GenesysAuditActions.HumanFollowUpRequired);
        w.F.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty(await w.F.InactivityClose.ListDueAsync());
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    [Fact]
    public async Task ClosedByAi_IsRefusedOnAHumanFollowUpTicket_ButHonouredOnTheCompletedChatbotRequest()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        var refused = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, leasing.TicketId, new GenesysTicketUpdateDto(Conversation, ClosedByAi: true));
        var honoured = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, documents.TicketId, new GenesysTicketUpdateDto(Conversation, ClosedByAi: true));

        Assert.False(refused.ClosedByAi);
        Assert.Contains("human follow-up", refused.ClosedByAiNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);
        Assert.True(honoured.ClosedByAi);
        Assert.Equal(TicketStatus.Closed, documents.TicketStatus);
    }

    [Fact]
    public async Task DocumentDelivery_ClosesOnlyTheDocumentsTicket_NeverTheLeasingTicketOfTheSameConversation()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        await DocumentsFor(w.F).HandleAsync(DocContext(DocumentsRequest), Sent());

        Assert.Equal(TicketStatus.Closed, documents.TicketStatus);
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);
    }

    [Fact]
    public async Task DocumentDelivery_NamingTheLeasingServiceRequest_StillCannotCloseIt()
    {
        var w = Build();
        await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        await DocumentsFor(w.F).HandleAsync(DocContext(LeasingRequest), Sent());

        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);
    }

    [Fact]
    public async Task DocumentDelivery_WithoutAServiceRequestId_ResolvesTheConversationsDefaultTicket_AsBefore()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        await DocumentsFor(w.F).HandleAsync(DocContext(null), Sent());

        // The default is the first (documents) ticket; the later, flagged leasing ticket is not touched.
        Assert.Equal(TicketStatus.Closed, documents.TicketStatus);
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);
    }

    [Fact]
    public async Task DocumentDelivery_NeverClosesALegacySingleTicket_OnceHumanFollowUpIsRequestedOnIt()
    {
        // An integration that cannot create a second ticket: the leasing request arrives as a handoff on the documents ticket.
        var w = Build();
        var only = await IngestAsync(w, DocumentsInquiry(serviceRequestId: null));
        var handoff = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, only.TicketId,
            new GenesysTicketUpdateDto(Conversation, Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "CustomerRequestedHuman", Reason: "Wants apartment leasing")));
        Assert.Equal("WaitingForAgent", handoff.HandoffStatus);

        await DocumentsFor(w.F).HandleAsync(DocContext(null), Sent());

        Assert.Equal(TicketStatus.Open, only.TicketStatus);
    }

    [Fact]
    public async Task DocumentDelivery_NeverClosesATicketFlaggedAsHumanFollowUp_EvenOnTheLegacyPath()
    {
        var w = Build();
        var only = await IngestAsync(w, DocumentsInquiry(serviceRequestId: null));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, only.TicketId, new GenesysTicketUpdateDto(Conversation, HumanFollowUpRequired: true));

        await DocumentsFor(w.F).HandleAsync(DocContext(null), Sent());

        Assert.Equal(TicketStatus.Open, only.TicketStatus);
    }

    [Fact]
    public async Task CompletedDocumentRequests_KeepTheirLifecycle_SentResolvesAndFailedCancels()
    {
        var sent = Build();
        var sentTicket = await IngestAsync(sent, DocumentsInquiry());
        await DocumentsFor(sent.F).HandleAsync(DocContext(DocumentsRequest), Sent());
        Assert.Equal(TicketStatus.Closed, sentTicket.TicketStatus);
        var resolution = Assert.Single(sent.F.Resolutions.Added);
        Assert.Equal(DocumentDeliveryTicketService.SentReason, resolution.ResolutionNote);
        Assert.Equal(ResolutionOutcome.Resolved, resolution.ResolutionOutcome);

        var failed = Build();
        var failedTicket = await IngestAsync(failed, DocumentsInquiry());
        await DocumentsFor(failed.F).HandleAsync(
            DocContext(DocumentsRequest),
            new DocumentDeliveryTicketUpdate(DocumentDeliveryTicketEvent.Failed, "Contract", "REC-1", "DELIVERY_FAILED", "The channel rejected it.", 1));
        Assert.Equal(TicketStatus.Closed, failedTicket.TicketStatus);
        Assert.Equal(ResolutionOutcome.Cancelled, Assert.Single(failed.F.Resolutions.Added).ResolutionOutcome);
    }

    [Fact]
    public async Task ATicketThatIsClosedByAPerson_IsNotTheConversationsBusiness_AndSiblingsKeepIndependentStatuses()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, documents.TicketId, new GenesysTicketUpdateDto(Conversation, ClosedByAi: true));
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, leasing.TicketId,
            new GenesysTicketUpdateDto(Conversation, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "AgentDisconnect")));

        Assert.Equal(TicketStatus.Closed, documents.TicketStatus);
        Assert.Equal(TicketStatus.Open, leasing.TicketStatus);
    }

    // ---- Updates are addressed by (conversation, ticket) ----

    [Fact]
    public async Task AnUpdate_LandsOnTheInteractionOfTheTicketItIsAddressedTo_NotASibling()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));

        var update = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, leasing.TicketId,
            new GenesysTicketUpdateDto(
                Conversation, Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "CustomerRequestedHuman", Reason: "Wants a viewing")));

        Assert.Equal("WaitingForAgent", update.HandoffStatus);
        var handoff = Assert.Single(w.F.Handoffs.All);
        Assert.Equal(leasing.TicketId, handoff.TicketId);
        Assert.NotEqual(documents.TicketId, handoff.TicketId);
    }

    [Fact]
    public async Task AnUpdate_ForATicketThatIsNotInTheConversation_IsAMismatch_AndAnUnknownConversationIsNotFound()
    {
        var w = Build();
        await IngestAsync(w, DocumentsInquiry());
        var stranger = await IngestAsync(w, DocumentsInquiry(conversation: "conv-other"));

        var mismatch = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, stranger.TicketId, new GenesysTicketUpdateDto(Conversation, Transcript: [Customer("hi", 0, "m-1")]));
        var unknown = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, stranger.TicketId, new GenesysTicketUpdateDto("conv-never", Transcript: [Customer("hi", 0, "m-1")]));

        Assert.Equal(GenesysTicketUpdateOutcome.ConversationTicketMismatch, mismatch.Outcome);
        Assert.Equal(GenesysTicketUpdateOutcome.ConversationNotFound, unknown.Outcome);
        Assert.Empty(w.F.Conversations.AllMessages);
    }

    // ---- 6. Transcript ----

    [Fact]
    public async Task TheConversationsTranscript_IsVisibleOnEveryTicketOfIt_EvenWhenGenesysSentItAgainstOnlyOne()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, documents.TicketId,
            new GenesysTicketUpdateDto(Conversation, Transcript: [Customer("I need my SPA", 0, "m-1"), Bot("Sent to your email", 1, "m-2")]));
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, leasing.TicketId,
            new GenesysTicketUpdateDto(Conversation, Transcript: [Customer("I also want to lease an apartment", 2, "m-3")]));

        var viewer = Guid.NewGuid();
        foreach (var ticket in new[] { documents, leasing })
        {
            ticket.AssignTo(viewer); // a CS Agent sees only a ticket assigned to them
            var view = await w.F.InteractionQuery.GetForTicketAsync(viewer, [Roles.CsAgent], ticket.TicketId);

            Assert.Equal(TicketQueryOutcome.Success, view.Outcome);
            var interaction = Assert.Single(view.Response!.Interactions);
            Assert.Equal(
                ["I need my SPA", "Sent to your email", "I also want to lease an apartment"],
                interaction.Messages.Select(m => m.Body));
            Assert.Equal([1, 2, 3], interaction.Messages.Select(m => m.Sequence));
            Assert.Equal(["Customer", "VirtualAgent", "Customer"], interaction.Messages.Select(m => m.Sender));
        }
    }

    [Fact]
    public async Task ATranscriptMessageDeliveredAgainstBothTickets_IsShownOnce()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));
        var message = Customer("Same line, sent twice", 0, "m-1");
        var noId = Bot("A line without an id", 1);
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, documents.TicketId, new GenesysTicketUpdateDto(Conversation, Transcript: [message, noId]));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, leasing.TicketId, new GenesysTicketUpdateDto(Conversation, Transcript: [message, noId]));

        var viewer = Guid.NewGuid();
        leasing.AssignTo(viewer); // a CS Agent sees only a ticket assigned to them
        var view = await w.F.InteractionQuery.GetForTicketAsync(viewer, [Roles.CsAgent], leasing.TicketId);

        Assert.Equal(["Same line, sent twice", "A line without an id"], Assert.Single(view.Response!.Interactions).Messages.Select(m => m.Body));
    }

    [Fact]
    public async Task TranscriptSynchronisation_IsIdempotent_RetriesAndTheFinalTranscriptNeverDuplicate()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var live = new[] { Customer("hello", 0, "m-1"), Bot("hi", 1, "m-2") };

        var first = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, documents.TicketId, new GenesysTicketUpdateDto(Conversation, Transcript: live));
        var retry = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, documents.TicketId, new GenesysTicketUpdateDto(Conversation, Transcript: live));
        var ended = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, documents.TicketId,
            new GenesysTicketUpdateDto(
                Conversation,
                Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow, "CustomerDisconnect", [.. live, Customer("bye", 2, "m-3")])));

        Assert.Equal(2, first.TranscriptMessageCount);
        Assert.Equal(2, retry.TranscriptMessageCount);
        Assert.Equal(3, ended.TranscriptMessageCount);
        Assert.Equal(3, w.F.Conversations.AllMessages.Count);
        Assert.Equal(["m-1", "m-2", "m-3"], w.F.Conversations.AllMessages.OrderBy(m => m.Sequence).Select(m => m.ExternalMessageId));
    }

    [Fact]
    public async Task AMalformedTranscript_IsRefused_WithNothingStoredOnEitherTicket()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        await IngestAsync(w, LeasingInquiry(w));

        var bad = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, documents.TicketId,
            new GenesysTicketUpdateDto(Conversation, Transcript: [Customer("ok", 0, "m-1"), new GenesysTranscriptMessageDto("Robot", T0, "x")]));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidTranscript, bad.Outcome);
        Assert.Empty(w.F.Conversations.AllMessages);
    }

    [Fact]
    public async Task HandlingState_IsJudgedPerTicket_AHumanLineUnderASiblingDoesNotMakeThisTicketHumanHandled()
    {
        var w = Build();
        var documents = await IngestAsync(w, DocumentsInquiry());
        var leasing = await IngestAsync(w, LeasingInquiry(w));
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, documents.TicketId,
            new GenesysTicketUpdateDto(
                Conversation, Transcript: [new GenesysTranscriptMessageDto("HumanAgent", T0, "I can help", SenderName: "Layla", ExternalMessageId: "h-1")]));

        var viewer = Guid.NewGuid();
        documents.AssignTo(viewer); // a CS Agent sees only a ticket assigned to them
        leasing.AssignTo(viewer);
        var documentsView = await w.F.InteractionQuery.GetForTicketAsync(viewer, [Roles.CsAgent], documents.TicketId);
        var leasingView = await w.F.InteractionQuery.GetForTicketAsync(viewer, [Roles.CsAgent], leasing.TicketId);

        Assert.Equal("Human", Assert.Single(documentsView.Response!.Interactions).HandlingState);
        Assert.Equal("Ai", Assert.Single(leasingView.Response!.Interactions).HandlingState);
        // ...yet the conversation's words are still all readable on the leasing ticket.
        Assert.Equal(["I can help"], Assert.Single(leasingView.Response.Interactions).Messages.Select(m => m.Body));
    }
}
