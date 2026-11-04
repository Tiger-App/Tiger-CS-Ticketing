using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;
using Microsoft.EntityFrameworkCore;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Tests.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.DefaultClassification;

/// <summary>
/// AI-created and AI-handled tickets: a valid department without a category / request type is General / General WITHIN that department; no
/// valid department is Customer Services / General / General; valid compatible AI classifications are kept; AI-resolved and AI-closed tickets
/// are classified too but get no CS Agent; human-handled tickets follow the existing ownership rules.
/// </summary>
public class DefaultClassificationAiScenarioTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000001";

    public static TheoryData<GenesysChannel> Channels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.SocialMedia,
        GenesysChannel.Instagram, GenesysChannel.Facebook
    };

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required Category CsGeneral { get; init; }
        public required RequestType CsGeneralType { get; init; }
        public required Department Leasing { get; init; }
        public required Category LeasingGeneral { get; init; }
        public required RequestType LeasingGeneralType { get; init; }
        public required Department Handover { get; init; }   // active, but has NO General of its own
        public Guid[] Agents { get; init; } = [];
    }

    private static (Category, RequestType) SeedGeneral(GenesysServiceFixture f, Department department, int workflowId)
    {
        var category = f.Categories.Seed(department.DepartmentId, "General");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: workflowId));
        var type = f.RequestTypes.Add(new RequestType(
            department.DepartmentId, "General", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        return (category, type);
    }

    private static World Build(bool callCenterFirst = false, int agents = 0)
    {
        var f = new GenesysServiceFixture(callCenterFirst: callCenterFirst);
        var (cs, csGeneral, csType) = f.SeedDefaultClassification();
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var (leasingGeneral, leasingType) = SeedGeneral(f, leasing, 500);
        var handover = f.Departments.AddDepartment("Handover", "HO");
        var ids = Enumerable.Range(0, agents).Select(_ => Guid.NewGuid()).OrderBy(i => i).ToArray();
        foreach (var id in ids)
        {
            f.DepartmentAssignments.Assignments.Add(
                new UserDepartmentAssignment(id, f.CallCenter!.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        return new World
        {
            F = f, Cs = cs, CsGeneral = csGeneral, CsGeneralType = csType, Leasing = leasing, LeasingGeneral = leasingGeneral,
            LeasingGeneralType = leasingType, Handover = handover, Agents = ids
        };
    }

    private static async Task<Ticket> IngestAsync(
        World w, string conv, int? departmentId, GenesysChannel channel = GenesysChannel.WebsiteChat, bool? humanFollowUp = null,
        GenesysRequestTypeDto? requestType = null)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conv, channel, CustomerPhone: Phone, DepartmentId: departmentId, RequestType: requestType, HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    // ---- 1. valid department, nothing else: General / General within it ----

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task AValidDepartmentWithNoCategoryOrRequestType_IsGeneralWithinThatDepartment_OnEveryChannel(GenesysChannel channel)
    {
        var w = Build();

        var ticket = await IngestAsync(w, "conv-dept", w.Leasing.DepartmentId, channel);

        Assert.Equal(w.LeasingGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(w.LeasingGeneralType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                     // AI-handled: no needless agent
        Assert.NotNull(ticket.WorkflowTemplateId);
    }

    [Fact]
    public async Task AValidDepartmentWithoutItsOwnGeneral_UsesCustomerServicesGeneral_NothingIsCreated()
    {
        var w = Build();
        var categories = (await w.F.Categories.ListAsync(false, null)).Count;

        var ticket = await IngestAsync(w, "conv-no-own-general", w.Handover.DepartmentId);

        Assert.Equal(w.CsGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(w.CsGeneralType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Cs.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(categories, (await w.F.Categories.ListAsync(false, null)).Count);
    }

    // ---- 2. no valid department: Customer Services ----

    [Fact]
    public async Task NoDepartmentAtAll_IsCustomerServicesGeneral_InsteadOfARefusal()
    {
        var w = Build();

        var ticket = await IngestAsync(w, "conv-no-dept", departmentId: null);

        Assert.Equal(w.CsGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(w.CsGeneralType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Cs.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task AnInvalidOrInactiveDepartment_IsCustomerServicesGeneral()
    {
        var w = Build();
        var inactive = w.F.Departments.AddDepartment("Retired", "OLD", isActive: false);

        var unknown = await IngestAsync(w, "conv-unknown-dept", 424242);
        var retired = await IngestAsync(w, "conv-retired-dept", inactive.DepartmentId);

        Assert.All(new[] { unknown, retired }, t =>
        {
            Assert.Equal(w.CsGeneral.CategoryId, t.CategoryId);
            Assert.Equal(w.Cs.DepartmentId, t.CurrentDepartmentId);
        });
    }

    [Fact]
    public async Task AnInvalidRequestTypeWithAValidDepartment_IsGeneralWithinThatDepartment()
    {
        var w = Build();

        var ticket = await IngestAsync(w, "conv-bad-type", w.Leasing.DepartmentId, requestType: new GenesysRequestTypeDto(RequestTypeId: 999999));

        Assert.Equal(w.LeasingGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(w.LeasingGeneralType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
    }

    // ---- 3. valid, compatible AI classification is preserved ----

    [Fact]
    public async Task AValidCompatibleAiClassification_IsPreserved_NotReplacedByGeneral()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Inquiry");
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 501));
        var type = w.F.RequestTypes.Add(new RequestType(
            w.Leasing.DepartmentId, "Leasing Inquiry", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));

        var ticket = await IngestAsync(w, "conv-valid-ai", w.Leasing.DepartmentId, requestType: new GenesysRequestTypeDto(type.RequestTypeId));

        Assert.Equal(category.CategoryId, ticket.CategoryId);
        Assert.Equal(type.RequestTypeId, ticket.RequestTypeId);
        Assert.NotEqual(w.LeasingGeneral.CategoryId, ticket.CategoryId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
    }

    // ---- 4. manual creation: same rules ----

    [Fact]
    public async Task ManualCreation_WithADepartmentOnly_IsGeneralWithinThatDepartment()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Leasing.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        var created = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, null, (byte)PriorityLevel.Medium, "Walk-in", DepartmentId: w.Leasing.DepartmentId));

        Assert.Equal(TicketCreationOutcome.Success, created.Outcome);
        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.LeasingGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task ManualCreation_WithNoDepartmentAndAnInvalidCategory_IsCustomerServicesGeneral()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", null, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, 987654, (byte)PriorityLevel.Medium, "Bogus"));

        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.CsGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(w.Cs.DepartmentId, ticket.CurrentDepartmentId);
    }

    // ---- 5. AI-resolved and AI-closed: classified, no needless CS Agent ----

    [Fact]
    public async Task AnAiClosedTicket_IsClassified_KeepsItsDepartmentGeneral_AndNeverGetsAnAgent()
    {
        var w = Build(callCenterFirst: true, agents: 2);
        var ticket = await IngestAsync(w, "conv-ai-close", w.Leasing.DepartmentId, humanFollowUp: false);
        Assert.Equal(w.LeasingGeneral.CategoryId, ticket.CategoryId);

        var closed = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-ai-close", ClosedByAi: true));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, closed.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(w.LeasingGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(w.LeasingGeneralType.RequestTypeId, ticket.RequestTypeId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticket.TicketId);
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.TicketId == ticket.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    [Fact]
    public async Task AnAiClosedTicketThatWasCreatedUnclassified_GetsTheDefaultAtClosure_WithoutAnAgent()
    {
        // Created while the default was not configured (or before it existed): legacy "Unclassified".
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var agent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(agent, f.CallCenter!.DepartmentId, true, DateTime.UtcNow, null));
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-legacy", GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: leasing.DepartmentId, HumanFollowUpRequired: false));
        var ticket = f.Tickets.All.Single(t => t.TicketId == created.Ticket!.TicketId);
        Assert.Null(ticket.CategoryId);

        // The default is configured later; the AI then finishes the conversation.
        var (cs, general, type) = f.SeedDefaultClassification();
        var closed = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-legacy", ClosedByAi: true));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, closed.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(general.CategoryId, ticket.CategoryId);
        Assert.Equal(type.RequestTypeId, ticket.RequestTypeId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(leasing.DepartmentId, ticket.CurrentDepartmentId);   // classification only: the department is not moved
    }

    [Fact]
    public async Task AnAiClosedTicketWithAValidClassification_IsNotTouched()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Inquiry");
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 502));
        var type = w.F.RequestTypes.Add(new RequestType(
            w.Leasing.DepartmentId, "Leasing Inquiry", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var ticket = await IngestAsync(w, "conv-keep-valid", w.Leasing.DepartmentId, requestType: new GenesysRequestTypeDto(type.RequestTypeId));

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-keep-valid", ClosedByAi: true));

        Assert.Equal(category.CategoryId, ticket.CategoryId);
        Assert.Equal(type.RequestTypeId, ticket.RequestTypeId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
    }

    [Fact]
    public async Task AiHandledTickets_DoNotAdvanceTheRotation()
    {
        var w = Build(callCenterFirst: true, agents: 2);
        await IngestAsync(w, "conv-ai-1", w.Leasing.DepartmentId, humanFollowUp: false);
        await IngestAsync(w, "conv-ai-2", departmentId: null, humanFollowUp: false);

        var human = await IngestAsync(w, "conv-human", w.Leasing.DepartmentId, humanFollowUp: true);

        Assert.Equal(w.Agents[0], human.CurrentOwnerEmployeeId);
    }

    // ---- 6. human-handled: existing CS Agent ownership rules ----

    [Fact]
    public async Task HumanHandledGeneralTickets_GoCallCenterFirst_GetTheNextCsAgent_NeverADepartmentEmployee()
    {
        var w = Build(callCenterFirst: true, agents: 2);
        var leasingEmployee = Guid.NewGuid();
        w.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(leasingEmployee, w.Leasing.DepartmentId, true, DateTime.UtcNow, null));
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.LeasingGeneralType.RequestTypeId, leasingEmployee));

        var first = await IngestAsync(w, "conv-h1", w.Leasing.DepartmentId, GenesysChannel.Phone, humanFollowUp: true);
        var second = await IngestAsync(w, "conv-h2", departmentId: null, GenesysChannel.WhatsApp, humanFollowUp: true);

        Assert.Equal(w.F.CallCenter!.DepartmentId, first.CurrentDepartmentId);
        Assert.Equal(w.Leasing.DepartmentId, first.OriginatingDepartmentId);
        Assert.Equal(w.LeasingGeneral.CategoryId, first.CategoryId);
        Assert.Equal(w.Agents[0], first.CurrentOwnerEmployeeId);
        Assert.Equal(w.CsGeneral.CategoryId, second.CategoryId);
        Assert.Equal(w.Agents[1], second.CurrentOwnerEmployeeId);
        Assert.NotEqual(leasingEmployee, first.CurrentOwnerEmployeeId);
    }

    // ---- 7. no duplicates, no overwriting ----

    [Fact]
    public async Task ADepartmentWithDuplicateGeneralRecords_FallsBackToCustomerServices_AndCreatesNothing()
    {
        var w = Build();
        w.F.Categories.Seed(w.Leasing.DepartmentId, "General");   // a second General in Leasing
        var categories = (await w.F.Categories.ListAsync(false, null)).Count;
        var types = (await w.F.RequestTypes.ListAsync(null, true)).Count;

        var ticket = await IngestAsync(w, "conv-dup-dept", w.Leasing.DepartmentId);

        Assert.Equal(w.CsGeneral.CategoryId, ticket.CategoryId);
        Assert.Equal(categories, (await w.F.Categories.ListAsync(false, null)).Count);
        Assert.Equal(types, (await w.F.RequestTypes.ListAsync(null, true)).Count);
    }

    [Fact]
    public async Task CollectionsIsStillNeverClassified()
    {
        var w = Build();
        var collections = w.F.Departments.AddDepartment("Collections", "COL");
        SeedGeneral(w.F, collections, 503);   // even if someone configured one

        var ticket = await IngestAsync(w, "conv-col", collections.DepartmentId);

        Assert.Null(ticket.CategoryId);
        Assert.Equal(collections.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task AValidAiClassification_IsNeverOverwrittenByALaterUpdateWithoutAType()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Inquiry");
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 504));
        var type = w.F.RequestTypes.Add(new RequestType(
            w.Leasing.DepartmentId, "Leasing Inquiry", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var ticket = await IngestAsync(w, "conv-stable", w.Leasing.DepartmentId, requestType: new GenesysRequestTypeDto(type.RequestTypeId));

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-stable", HumanFollowUpRequired: true));

        Assert.Equal(category.CategoryId, ticket.CategoryId);
        Assert.Equal(type.RequestTypeId, ticket.RequestTypeId);
    }

    // ---- department General is recognised as the default (reclassify + later AI refinement) ----

    [Fact]
    public async Task ADepartmentsOwnGeneral_CanBeReplacedByALaterAiRequestType()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Inquiry");
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 505));
        var type = w.F.RequestTypes.Add(new RequestType(
            w.Leasing.DepartmentId, "Leasing Inquiry", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var ticket = await IngestAsync(w, "conv-refine", w.Leasing.DepartmentId);
        Assert.Equal(w.LeasingGeneral.CategoryId, ticket.CategoryId);

        var update = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-refine", RequestType: new GenesysRequestTypeDto(type.RequestTypeId)));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        Assert.Equal(category.CategoryId, ticket.CategoryId);
        Assert.Equal(type.RequestTypeId, ticket.RequestTypeId);
    }

    [Fact]
    public async Task ADepartmentsOwnGeneral_IsReportedAsTheDefaultInTicketDetail_AndCanBeReclassified()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Inquiry");
        var ticket = await IngestAsync(w, "conv-reclass", w.Leasing.DepartmentId);
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);

        Assert.True(await w.F.DefaultClassification.IsDefaultAsync(ticket.CategoryId, ticket.RequestTypeId));
        var result = await w.F.Classification.ClassifyAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new ClassifyTicketRequestDto(category.CategoryId, ticket.PriorityId!.Value, null, [1, 2, 3, 4]));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(category.CategoryId, ticket.CategoryId);
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
    }

    // ---- configuration: General for every department but Collections, idempotent ----

    [Fact]
    public async Task TheImporter_ConfiguresGeneralInEveryDepartmentExceptCollections_AndASecondRunCreatesNothing()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        await db.SaveChangesAsync();
        var now = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

        var first = await GeneralFallbackImporter.ImportAllAsync(db, now);
        var categories = await db.Categories.CountAsync();
        var types = await db.RequestTypes.CountAsync();
        var second = await GeneralFallbackImporter.ImportAllAsync(db, now);

        Assert.Contains("CS", first.Select(r => r.DepartmentCode));
        Assert.DoesNotContain("COL", first.Select(r => r.DepartmentCode));
        Assert.True(first.Count >= 2);
        Assert.All(first, r => Assert.Equal(GeneralFallbackImportOutcome.Created, r.Result.Outcome));
        Assert.All(second, r => Assert.Equal(GeneralFallbackImportOutcome.AlreadyConfigured, r.Result.Outcome));
        Assert.Equal(categories, await db.Categories.CountAsync());
        Assert.Equal(types, await db.RequestTypes.CountAsync());
        var col = await db.Departments.SingleAsync(d => d.Code == "COL");
        Assert.Empty(await db.Categories.Where(c => c.DepartmentId == col.DepartmentId).ToListAsync());
        foreach (var department in await db.Departments.Where(d => d.Code != "COL").ToListAsync())
        {
            Assert.Equal(1, await db.RequestTypes.CountAsync(r => r.Name == "General" && r.DepartmentId == department.DepartmentId));
            Assert.Equal(1, await db.Categories.CountAsync(c => c.Name == "General" && c.DepartmentId == department.DepartmentId));
        }
    }
}
