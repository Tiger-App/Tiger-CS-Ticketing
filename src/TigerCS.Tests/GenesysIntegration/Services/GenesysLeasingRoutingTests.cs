using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;
using TigerCS.Tests.WorkflowConfiguration;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Leasing requests from the Genesys AI Agent: the delivered configuration (what the seed/import provides), the exact UAT payload
/// (<c>conversationId</c>, <c>serviceRequestId</c>, <c>requestType: "Leasing"</c>, <c>humanFollowUpRequired: true</c>) routed through the
/// existing request-type routing and automatic assignment, and the follow-up ticket staying open against every automatic closer.
/// </summary>
public class GenesysLeasingRoutingTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    // ---- what the delivered configuration contains ----

    [Fact]
    public async Task TheImportedCatalogue_ProvidesAnActiveLeasingDepartment_WithFiveActiveRequestTypes_AndNoneNamedLeasing_AndNoAssignmentRules()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        await NewRequestTypesImporter.ImportAsync(db, Now);

        var lcs = await db.Departments.SingleAsync(d => d.Code == "LCS");
        Assert.True(lcs.IsActive);
        Assert.Equal("Leasing Customer Services", lcs.Name);

        var types = await db.RequestTypes.Where(r => r.DepartmentId == lcs.DepartmentId).ToListAsync();
        Assert.Equal(
            ["Booking", "Ejari", "Move-In / Move-Out", "Rent / DEWA / AC Cheque Inquiry", "Tenancy Contract"],
            types.Select(t => t.Name).Order());
        Assert.All(types, t => Assert.True(t.IsActive));

        // Every type is routed to LCS through RequestType.DepartmentId and has a Published workflow, so Genesys can use it by id or exact name...
        foreach (var type in types)
        {
            Assert.NotNull(await db.WorkflowTemplates.FirstOrDefaultAsync(w => w.WorkflowId == type.WorkflowId && w.Status == WorkflowVersionStatus.Published));
        }

        // ...but the import defines no request type named "Leasing" (it is not part of the 35-row workbook) and NO automatic-assignment
        // rule: tickets land in the LCS department queue until an administrator configures a rule per request type.
        Assert.DoesNotContain(await db.RequestTypes.ToListAsync(), t => string.Equals(t.Name, "Leasing", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await db.RequestTypeAssignmentRules.Where(r => types.Select(t => t.RequestTypeId).Contains(r.RequestTypeId)).ToListAsync());
    }

    // ---- the exact UAT request ----

    private sealed record World(GenesysServiceFixture F, Department Bot, Department Leasing, RequestType LeasingType, Guid Employee);

    private static World Build(bool withRule = true, string requestTypeName = "Leasing")
    {
        var f = new GenesysServiceFixture();
        var (bot, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        f.QueueMappings.Map("queue-bot", bot.DepartmentId);
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var type = f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, requestTypeName, template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var employee = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employee, leasing.DepartmentId, true, DateTime.UtcNow, null));
        if (withRule)
        {
            f.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(type.RequestTypeId, employee));
        }

        return new World(f, bot, leasing, type, employee);
    }

    private static GenesysInquiryDto UatRequest() =>
        new("TEST-CONVERSATION-001", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", QueueId: "queue-bot",
            ServiceRequestId: "leasing-001", RequestType: new GenesysRequestTypeDto(Name: "Leasing"), HumanFollowUpRequired: true);

    [Fact]
    public async Task TheUatLeasingRequest_CreatesASeparateTicket_RoutedToLeasing_AutoAssigned_AndOpenForHumanFollowUp()
    {
        var w = Build();
        var documents = await w.F.Ingestion.IngestAsync(ServiceAccount, UatRequest() with
        {
            ServiceRequestId = "documents", RequestType = null, HumanFollowUpRequired = null
        });

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, UatRequest());

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.NotEqual(documents.Ticket!.TicketId, result.Ticket!.TicketId);
        var ticket = w.F.Tickets.All.Single(t => t.TicketId == result.Ticket.TicketId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);       // by the configured request type, no id in code
        Assert.Equal(w.Bot.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(w.LeasingType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Employee, ticket.CurrentOwnerEmployeeId);                 // the existing assignment rule
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.True(w.F.Interactions.All.Single(i => i.TicketId == ticket.TicketId).RequiresHumanFollowUp);
    }

    [Fact]
    public async Task TheDedicatedLeasingInquiryType_RoutesFromGenesysByName_AtNormalPriority_AutoAssigned_AndOpenForHumanFollowUp()
    {
        var w = Build(requestTypeName: "Leasing Inquiry");
        var request = UatRequest() with { ServiceRequestId = "leasing-inquiry-001", RequestType = new GenesysRequestTypeDto(Name: "Leasing Inquiry") };
        var documents = await w.F.Ingestion.IngestAsync(ServiceAccount, UatRequest() with
        {
            ServiceRequestId = "documents", RequestType = null, HumanFollowUpRequired = null
        });

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, request);

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.NotEqual(documents.Ticket!.TicketId, result.Ticket!.TicketId);        // a separate ticket in the same conversation
        var ticket = w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.LeasingType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);             // "Normal" is the Medium tier
        Assert.Equal(w.Employee, ticket.CurrentOwnerEmployeeId);
        Assert.True(w.F.Interactions.All.Single(i => i.TicketId == ticket.TicketId).RequiresHumanFollowUp);

        // a retry of the same (conversation, serviceRequestId) returns the same ticket; the old name no longer resolves
        var retry = await w.F.Ingestion.IngestAsync(ServiceAccount, request);
        Assert.Equal(result.Ticket!.TicketId, retry.Ticket!.TicketId);
        var oldName = await w.F.Ingestion.IngestAsync(ServiceAccount, UatRequest() with { ConversationId = "OTHER-CONVERSATION" });
        Assert.NotEqual(GenesysIngestionOutcome.TicketCreated, oldName.Outcome);
    }

    [Fact]
    public async Task WithNoAssignmentRule_TheLeasingTicketWaitsInTheLeasingDepartmentQueue_NeverElsewhere()
    {
        var w = Build(withRule: false);

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, UatRequest());

        var ticket = w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.StartsWith("DepartmentQueue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLeasingTicket_StaysOpen_AgainstAiCompletion_DocumentDelivery_AndBothInactivityJobs()
    {
        var w = Build();
        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, UatRequest());
        var ticket = w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);

        // AI completion
        var ai = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("TEST-CONVERSATION-001", ClosedByAi: true));
        Assert.False(ai.ClosedByAi);

        // document delivery (even when it names this very service request)
        var deliveries = new TigerCS.Tests.CrmDocuments.FakeCrmDocumentDeliveryRepository();
        var docs = new DocumentDeliveryTicketService(
            new TigerCS.Application.Modules.CrmDocuments.CrmDocumentOptions { Enabled = true }, w.F.Conversations, w.F.Tickets, w.F.Handoffs,
            w.F.Interactions, deliveries, w.F.Lifecycle, w.F.Audit, new TigerCS.Tests.CrmDocuments.CommittingUnitOfWork(deliveries), w.F.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentDeliveryTicketService>.Instance);
        await docs.HandleAsync(
            new DocumentDeliveryTicketContext(ServiceAccount, Guid.NewGuid(), "TEST-CONVERSATION-001", null, TigerCS.Tests.CrmDocuments.DocumentServiceFixture.CustomerId, "+971500000001", "leasing-001"),
            new DocumentDeliveryTicketUpdate(DocumentDeliveryTicketEvent.Sent, "Contract", "REC-1", null, "sent", 1));

        // the 5-minute bot-prompt timer and the 10-minute customer-inactivity clock, long overdue
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("TEST-CONVERSATION-001", AwaitingCustomerReply: true, CustomerActive: true));
        w.F.Clock.Advance(TimeSpan.FromHours(6));
        Assert.Empty(await w.F.InactivityClose.ListDueAsync());
        foreach (var id in await w.F.InactivityClose.ListDueAsync())
        {
            await w.F.InactivityClose.TryCloseAsync(id);
        }

        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Empty(w.F.Resolutions.Added);
    }
}
