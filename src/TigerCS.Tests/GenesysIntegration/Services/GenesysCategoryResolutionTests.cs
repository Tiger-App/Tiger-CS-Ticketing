using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// A request type the bot supplies in the structured <c>requestType</c> field is saved together with the Category that pairs with it
/// (same department, same name — the only Category/RequestType relationship the data model has), so Ticket Details shows both. The subject
/// is display text and is never parsed. A missing, unrecognised or unpaired request type never invents a classification.
/// </summary>
public class GenesysCategoryResolutionTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private sealed record World(GenesysServiceFixture F, Department Arrival, Department Registration, RequestType RequestType);

    private static World Build(bool seedPairedCategory = true)
    {
        var f = new GenesysServiceFixture();
        var (arrival, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var registration = f.Departments.AddDepartment("Registration", "REG");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var requestType = f.RequestTypes.Add(new RequestType(
            registration.DepartmentId, "Ownership Transfer / Title Deed Inquiry", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));
        if (seedPairedCategory)
        {
            f.Categories.Seed(registration.DepartmentId, requestType.Name);
        }

        return new World(f, arrival, registration, requestType);
    }

    private static GenesysInquiryDto Inquiry(World w, string conversationId, GenesysRequestTypeDto? requestType, string? subject = null) =>
        new(conversationId, GenesysChannel.WhatsApp, CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali",
            DepartmentId: w.Arrival.DepartmentId, Subject: subject, RequestType: requestType);

    [Fact]
    public async Task ValidRequestTypeId_SavesTheRequestTypeAndItsPairedCategory()
    {
        var w = Build();
        var category = (await w.F.Categories.ListAsync(true, w.Registration.DepartmentId)).Single();

        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(w, "conv-ok", new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.RequestType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(category.CategoryId, ticket.CategoryId);
        Assert.True(ticket.IsClassified);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "ClassifyRequestType" && e.AfterValue!.Contains($"CategoryId={category.CategoryId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidRequestTypeName_ResolvesTheSameRequestTypeAndCategory()
    {
        var w = Build();

        await w.F.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(w, "conv-name", new GenesysRequestTypeDto(Name: " ownership transfer / title deed inquiry ")));

        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.RequestType.RequestTypeId, ticket.RequestTypeId);
        Assert.NotNull(ticket.CategoryId);
    }

    [Fact]
    public async Task RequestTypeWithoutAPairedCategory_StillClassifiesTheRequestType_AndInventsNoCategory()
    {
        var w = Build(seedPairedCategory: false);

        await w.F.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(w, "conv-nocat", new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));

        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.RequestType.RequestTypeId, ticket.RequestTypeId);
        Assert.Null(ticket.CategoryId);
        Assert.Empty(await w.F.Categories.ListAsync(false, w.Registration.DepartmentId)); // nothing created
    }

    [Fact]
    public async Task AmbiguousPairedCategory_IsNotGuessed()
    {
        var w = Build();
        w.F.Categories.Seed(w.Registration.DepartmentId, w.RequestType.Name); // a second active category of the same name

        await w.F.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(w, "conv-amb", new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));

        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.RequestType.RequestTypeId, ticket.RequestTypeId);
        Assert.Null(ticket.CategoryId);
    }

    [Fact]
    public async Task SubjectNamingARequest_WithoutAStructuredRequestType_IsNotParsed_AndStaysUnclassifiedOnNormalPriority()
    {
        var w = Build();

        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(w, "conv-subject", requestType: null,
                subject: "Chat received via Genesys - WhatsApp - CS - " + w.RequestType.Name));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Null(ticket.RequestTypeId);
        Assert.Null(ticket.CategoryId);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.Contains(w.RequestType.Name, ticket.RequestSummary, StringComparison.Ordinal);
        Assert.Empty(w.F.Handoffs.All);
    }

    [Fact]
    public async Task BlankRequestType_StaysUnclassifiedOnNormalPriority()
    {
        var w = Build();

        await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-blank", new GenesysRequestTypeDto(Name: "  ")));

        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Null(ticket.RequestTypeId);
        Assert.Null(ticket.CategoryId);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
    }

    [Theory]
    [InlineData("unknown-name")]
    [InlineData("unknown-id")]
    public async Task UnrecognisedRequestType_IsRefused_NoTicketAndNoCategory(string kind)
    {
        var w = Build();
        var invalid = kind == "unknown-id"
            ? new GenesysRequestTypeDto(RequestTypeId: 99999)
            : new GenesysRequestTypeDto(Name: "Ownership Transfer and NOC");

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-bad", invalid));

        Assert.Equal(GenesysIngestionOutcome.RequestTypeInvalid, result.Outcome);
        Assert.Empty(w.F.Tickets.All);
    }

    [Fact]
    public async Task LaterClassification_ByTheBot_SavesTheCategory_AndARefinementFollowsTheNewPairing()
    {
        var w = Build();
        var other = w.F.RequestTypes.Add(new RequestType(
            w.Registration.DepartmentId, "Title Deed Collection", w.RequestType.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var otherCategory = w.F.Categories.Seed(w.Registration.DepartmentId, other.Name);

        var created = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-late", requestType: null));
        var ticket = w.F.Tickets.All.Single(t => t.TicketId == created.Ticket!.TicketId);
        Assert.Null(ticket.CategoryId);

        GenesysTicketUpdateDto Update(int id) => new("conv-late", RequestType: new GenesysRequestTypeDto(id));

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Update(w.RequestType.RequestTypeId));
        Assert.NotNull(ticket.CategoryId);
        var firstCategory = ticket.CategoryId;

        var refined = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Update(other.RequestTypeId));
        Assert.Equal(GenesysClassificationOutcome.Classified, refined.Classification!.Outcome);
        Assert.Equal(otherCategory.CategoryId, ticket.CategoryId);
        Assert.NotEqual(firstCategory, ticket.CategoryId);
    }

    [Fact]
    public async Task ACategoryAPersonChose_IsNeverReplacedByTheBot()
    {
        var w = Build(seedPairedCategory: false);
        var other = w.F.RequestTypes.Add(new RequestType(
            w.Registration.DepartmentId, "Title Deed Collection", w.RequestType.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var personsCategory = w.F.Categories.Seed(w.Arrival.DepartmentId, "Customer Service Enquiry 2");
        w.F.Categories.Seed(w.Registration.DepartmentId, other.Name); // the bot's type has a pairing, which must not displace the person's choice

        var created = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-person", requestType: null));
        var ticket = w.F.Tickets.All.Single(t => t.TicketId == created.Ticket!.TicketId);
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent); // a CS Agent works only a ticket assigned to them
        var manual = await w.F.Classification.ClassifyAsync(
            agent, [Roles.CsAgent], ticket.TicketId,
            new ClassifyTicketRequestDto(personsCategory.CategoryId, (byte)PriorityLevel.Medium, null, ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.Success, manual.Outcome);

        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, new("conv-person", RequestType: new GenesysRequestTypeDto(other.RequestTypeId)));

        Assert.Equal(personsCategory.CategoryId, ticket.CategoryId);

        // A later, different request type from the bot is refused outright once a person has chosen the category.
        var second = w.F.RequestTypes.Add(new RequestType(
            w.Registration.DepartmentId, "Refund", w.RequestType.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var again = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, new("conv-person", RequestType: new GenesysRequestTypeDto(second.RequestTypeId)));
        Assert.Equal(GenesysTicketUpdateOutcome.RequestTypeConflict, again.Outcome);
        Assert.Equal(personsCategory.CategoryId, ticket.CategoryId);
    }
}
