using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.DefaultClassification;

/// <summary>
/// The approved default classification: every ticket has a valid classification, and one with none that can be trusted is classified
/// Customer Services / General / General - never left "Unclassified". Valid classifications, ownership, Collections and the fallback's
/// own configuration are protected.
/// </summary>
public class DefaultClassificationTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000001";

    public static TheoryData<GenesysChannel> AllGenesysChannels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.SocialMedia,
        GenesysChannel.Instagram, GenesysChannel.Facebook
    };

    /// <summary>Customer Services with its General category / request type, a Finance department whose request type pairs with a category, and Leasing as an arrival department.</summary>
    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required Category General { get; init; }
        public required RequestType GeneralType { get; init; }
        public required Department Finance { get; init; }
        public required Category FinanceCategory { get; init; }
        public required RequestType FinanceType { get; init; }
        public required Department Leasing { get; init; }
        public Guid[] Agents { get; init; } = [];
    }

    private static World Build(bool callCenterFirst = false, bool seedDefault = true, int agents = 0)
    {
        var f = new GenesysServiceFixture(callCenterFirst: callCenterFirst);
        var cs = f.Departments.AddDepartment("Customer Services", "CS");
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var finance = f.Departments.AddDepartment("Finance", "FIN");
        var financeCategory = f.Categories.Seed(finance.DepartmentId, "Payment Receipt");
        var financeTemplate = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var financeType = f.RequestTypes.Add(new RequestType(
            finance.DepartmentId, "Payment Receipt", financeTemplate.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));

        Category? general = null;
        RequestType? generalType = null;
        if (seedDefault)
        {
            (_, general, generalType) = f.SeedDefaultClassification(cs);
        }

        var agentIds = Enumerable.Range(0, agents).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToArray();
        foreach (var id in agentIds)
        {
            f.DepartmentAssignments.Assignments.Add(
                new UserDepartmentAssignment(id, f.CallCenter!.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        return new World
        {
            F = f, Cs = cs, General = general!, GeneralType = generalType!, Finance = finance, FinanceCategory = financeCategory,
            FinanceType = financeType, Leasing = leasing, Agents = agentIds
        };
    }

    private static async Task<(Ticket Ticket, GenesysIngestionResult Result)> IngestAsync(
        World w, string conversationId, GenesysChannel channel = GenesysChannel.WebsiteChat, GenesysRequestTypeDto? requestType = null,
        bool? humanFollowUp = null, int? departmentId = null)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, channel, CustomerPhone: Phone, DepartmentId: departmentId ?? w.Leasing.DepartmentId,
                RequestType: requestType, HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId), result);
    }

    private static void AssertIsDefault(World w, Ticket ticket)
    {
        Assert.Equal(w.General.CategoryId, ticket.CategoryId);
        Assert.Equal(w.GeneralType.RequestTypeId, ticket.RequestTypeId);
        Assert.NotNull(ticket.WorkflowTemplateId);
        Assert.True(ticket.IsClassified);
    }

    // ---- Missing classification, on every creation channel ----

    [Theory]
    [MemberData(nameof(AllGenesysChannels))]
    public async Task MissingClassification_OnEveryGenesysChannel_BecomesCustomerServicesGeneral(GenesysChannel channel)
    {
        var w = Build();

        var (ticket, result) = await IngestAsync(w, "conv-missing", channel);

        AssertIsDefault(w, ticket);
        // A valid department is never changed because its General is missing: Leasing keeps the ticket, Customer Services' General classifies it,
        // and the configuration gap is reported on the integration audit entry.
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Contains("DefaultClassificationConfigurationIssue=LCS:", w.F.Audit.Entries.Single(e => e.Action == "GenesysInquiryIngested").AfterValue!, StringComparison.Ordinal);
        Assert.Equal("Classified", result.Classification!.Status);
        Assert.Equal("General", result.Classification.RequestTypeName);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification" && e.EntityId == ticket.TicketId.ToString());
        // Priority, status and SLA behave as for every Genesys ticket: the configured default (Normal), Open, clock running.
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(SlaState.Running, ticket.SlaState);
    }

    [Fact]
    public async Task ManualCreation_WithNoCategoryAndNoDepartment_IsGeneral_NotRefused()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", null, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        var created = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, null, null, "Walk-in"));

        Assert.Equal(TicketCreationOutcome.Success, created.Outcome);
        AssertIsDefault(w, Assert.Single(w.F.Tickets.All));
    }

    [Fact]
    public async Task ManualCreation_WithOnlyADepartment_IsGeneral_AndKeepsTheSuppliedPriority()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Leasing.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        var created = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, null, (byte)PriorityLevel.High, "Walk-in", DepartmentId: w.Leasing.DepartmentId));

        Assert.Equal(TicketCreationOutcome.Success, created.Outcome);
        var ticket = Assert.Single(w.F.Tickets.All);
        AssertIsDefault(w, ticket);
        Assert.Equal((byte)PriorityLevel.High, ticket.PriorityId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);   // the department named is kept
    }

    // ---- Invalid, ambiguous and partial classification ----

    [Fact]
    public async Task ManualCreation_WithAnInvalidCategory_IsGeneral()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", null, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        var created = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, 987654, (byte)PriorityLevel.Medium, "Bogus category"));

        Assert.Equal(TicketCreationOutcome.Success, created.Outcome);
        AssertIsDefault(w, Assert.Single(w.F.Tickets.All));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification" && e.AfterValue!.Contains("unknown or inactive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualCreation_WithAValidCategoryButAnInvalidRequestType_IsGeneral_NotAHalfClassifiedTicket()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Finance.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        var created = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, w.FinanceCategory.CategoryId, (byte)PriorityLevel.Medium, "Mismatch", RequestTypeId: 424242));

        Assert.Equal(TicketCreationOutcome.Success, created.Outcome);
        AssertIsDefault(w, Assert.Single(w.F.Tickets.All));
    }

    [Fact]
    public async Task ManualCreation_WithARequestTypeFromAnotherDepartment_IsAmbiguous_AndIsGeneral()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Leasing.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);
        var leasingCategory = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Enquiry");

        var created = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, leasingCategory.CategoryId, (byte)PriorityLevel.Medium, "Category and type disagree",
                RequestTypeId: w.FinanceType.RequestTypeId));

        Assert.Equal(TicketCreationOutcome.Success, created.Outcome);
        AssertIsDefault(w, Assert.Single(w.F.Tickets.All));
    }

    [Fact]
    public async Task Genesys_AnUnknownRequestType_IsGeneral_InsteadOfARefusal_AndTheCustomerIsNotLost()
    {
        var w = Build();

        var (ticket, result) = await IngestAsync(w, "conv-unknown", requestType: new GenesysRequestTypeDto(RequestTypeId: 999999));

        AssertIsDefault(w, ticket);
        Assert.Equal("Classified", result.Classification!.Status);
        // The reason travels on the integration audit entry, so a ticket that landed on the fallback can be traced to the exact input.
        Assert.Contains("999999", w.F.Audit.Entries.Single(e => e.Action == "GenesysInquiryIngested").AfterValue!, StringComparison.Ordinal);
        Assert.Contains("DefaultClassification=Customer Services / General because", w.F.Audit.Entries.Single(e => e.Action == "GenesysInquiryIngested").AfterValue!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Genesys_AnAmbiguousRequestTypeName_IsGeneral_NotAGuess()
    {
        var w = Build();
        var lcsTemplate = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 301));
        w.F.RequestTypes.Add(new RequestType(
            w.Leasing.DepartmentId, "Payment Receipt", lcsTemplate.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));

        var (ticket, _) = await IngestAsync(w, "conv-ambiguous", requestType: new GenesysRequestTypeDto(Name: "Payment Receipt"));

        AssertIsDefault(w, ticket);
    }

    [Fact]
    public async Task Genesys_AValidRequestTypeWithNoMatchingCategory_IsPartial_AndIsGeneral()
    {
        var w = Build();
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 302));
        var orphan = w.F.RequestTypes.Add(new RequestType(
            w.Finance.DepartmentId, "Orphan Type", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));

        var (ticket, _) = await IngestAsync(w, "conv-orphan", requestType: new GenesysRequestTypeDto(orphan.RequestTypeId));

        AssertIsDefault(w, ticket);
    }

    // ---- Correctly classified tickets stay exactly as they are ----

    [Fact]
    public async Task Genesys_AValidRequestType_IsClassifiedAsRequested_NeverGeneral()
    {
        var w = Build();

        var (ticket, result) = await IngestAsync(w, "conv-valid", requestType: new GenesysRequestTypeDto(w.FinanceType.RequestTypeId));

        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
        Assert.Equal(w.FinanceType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.NotEqual(w.General.CategoryId, ticket.CategoryId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
        Assert.Equal("Classified", result.Classification!.Status);
    }

    [Fact]
    public async Task ManualCreation_WithAValidCategoryAndRequestType_IsUnchanged()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Finance.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        var created = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, w.FinanceCategory.CategoryId, (byte)PriorityLevel.High, "Receipt", RequestTypeId: w.FinanceType.RequestTypeId));

        Assert.Equal(TicketCreationOutcome.Success, created.Outcome);
        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
        Assert.Equal(w.FinanceType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal((byte)PriorityLevel.High, ticket.PriorityId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
    }

    [Fact]
    public async Task ManualCreation_WithAValidCategoryAndNoRequestType_KeepsTheAgentsCategory()
    {
        var w = Build();
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Finance.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, w.FinanceCategory.CategoryId, (byte)PriorityLevel.Medium, "Receipt"));

        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
        Assert.NotEqual(w.General.CategoryId, ticket.CategoryId);
    }

    // ---- Configuration: reuse, no duplicates, safe when missing ----

    [Fact]
    public async Task WithoutTheConfiguration_PreviousBehaviourIsKept_AndNothingIsCreatedAtRuntime()
    {
        var w = Build(seedDefault: false);
        var categories = (await w.F.Categories.ListAsync(false, null)).Count;
        var types = (await w.F.RequestTypes.ListAsync(null, true)).Count;

        var (ticket, result) = await IngestAsync(w, "conv-no-config");

        Assert.Null(ticket.CategoryId);
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal("AwaitingClassification", result.Classification!.Status);
        Assert.Equal(categories, (await w.F.Categories.ListAsync(false, null)).Count);
        Assert.Equal(types, (await w.F.RequestTypes.ListAsync(null, true)).Count);
        var resolution = await w.F.DefaultClassification.ResolveAsync();
        Assert.False(resolution.IsAvailable);
        Assert.Equal(DefaultClassificationGap.CategoryMissing, (await new DefaultClassificationService(
            w.F.Departments, w.F.Categories, w.F.RequestTypes, w.F.WorkflowTemplates).ResolveAsync()).Gap);
    }

    [Fact]
    public async Task DuplicateGeneralRecords_AreReported_NeverGuessedBetween_AndNothingIsCreated()
    {
        var w = Build();
        w.F.Categories.Seed(w.Cs.DepartmentId, "General");   // a second active General category
        var categories = (await w.F.Categories.ListAsync(false, null)).Count;

        var resolution = await w.F.DefaultClassification.ResolveAsync();
        var (ticket, _) = await IngestAsync(w, "conv-duplicate");

        Assert.False(resolution.IsAvailable);
        Assert.Equal(DefaultClassificationGap.CategoryAmbiguous, resolution.Gap);
        Assert.Null(ticket.CategoryId);
        Assert.Equal(categories, (await w.F.Categories.ListAsync(false, null)).Count);
    }

    [Fact]
    public async Task AGeneralRequestTypeWithoutAPublishedWorkflow_IsNotUsable()
    {
        var f = new GenesysServiceFixture();
        var cs = f.Departments.AddDepartment("Customer Services", "CS");
        f.Categories.Seed(cs.DepartmentId, "General");
        f.RequestTypes.Add(new RequestType(cs.DepartmentId, "General", workflowId: 777, (byte)PriorityLevel.Medium, false, true, true, true));

        var resolution = await f.DefaultClassification.ResolveAsync();

        Assert.Equal(DefaultClassificationGap.WorkflowNotPublished, resolution.Gap);
    }

    [Fact]
    public async Task TheDepartment_IsFoundByCode_ElseByTheUniqueCustomerServiceName()
    {
        var f = new GenesysServiceFixture();
        var cs = f.Departments.AddDepartment("Customer Service", "XYZ");   // production's own name, a different code
        f.SeedDefaultClassification(cs);

        var resolution = await f.DefaultClassification.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal(cs.DepartmentId, resolution.Value!.Department.DepartmentId);
    }

    // ---- Collections is out of scope ----

    [Fact]
    public async Task CollectionsTickets_AreNeverGivenTheFallback()
    {
        var w = Build();
        var collections = w.F.Departments.AddDepartment("Collections", "COL");

        var (ticket, _) = await IngestAsync(w, "conv-collections", departmentId: collections.DepartmentId);

        Assert.Null(ticket.CategoryId);
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal(collections.DepartmentId, ticket.CurrentDepartmentId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
    }

    // ---- Genesys updates and reclassification ----

    private static GenesysTicketUpdateDto UpdateWithType(string conversationId, GenesysRequestTypeDto type) => new(conversationId, RequestType: type);

    [Fact]
    public async Task GenesysUpdate_WithAValidRequestType_ReplacesTheGeneralFallback_AndKeepsTheCsAgent()
    {
        var w = Build(callCenterFirst: true, agents: 2);
        var (ticket, _) = await IngestAsync(w, "conv-refine", humanFollowUp: true);
        AssertIsDefault(w, ticket);
        var owner = ticket.CurrentOwnerEmployeeId;
        Assert.Equal(w.Agents[0], owner);

        var update = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, UpdateWithType("conv-refine", new GenesysRequestTypeDto(w.FinanceType.RequestTypeId)));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
        Assert.Equal(w.FinanceType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);                        // classification never clears the CS Agent
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);    // and the Call Center keeps routing
    }

    [Fact]
    public async Task GenesysUpdate_WithAnInvalidRequestType_DoesNotOverwriteAValidClassification()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-keep", requestType: new GenesysRequestTypeDto(w.FinanceType.RequestTypeId));

        var update = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, UpdateWithType("conv-keep", new GenesysRequestTypeDto(RequestTypeId: 999999)));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidRequestType, update.Outcome);
        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
        Assert.Equal(w.FinanceType.RequestTypeId, ticket.RequestTypeId);
    }

    [Fact]
    public async Task GenesysUpdate_WithoutARequestType_NeverMovesAValidClassificationToGeneral()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-plain", requestType: new GenesysRequestTypeDto(w.FinanceType.RequestTypeId));

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-plain", HumanFollowUpRequired: true));

        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
        Assert.Equal(w.FinanceType.RequestTypeId, ticket.RequestTypeId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
    }

    [Fact]
    public async Task GenesysUpdate_WithAnInvalidRequestType_LeavesTheGeneralFallbackInPlace()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-still-general");

        var update = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, UpdateWithType("conv-still-general", new GenesysRequestTypeDto(RequestTypeId: 999999)));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidRequestType, update.Outcome);
        AssertIsDefault(w, ticket);
    }

    [Fact]
    public async Task GenesysUpdate_CannotReplaceTheFallbackOnceAPersonHasRespondedToTheCustomer()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-person");
        ticket.RecordFirstHumanResponse(DateTime.UtcNow);

        var update = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, UpdateWithType("conv-person", new GenesysRequestTypeDto(w.FinanceType.RequestTypeId)));

        Assert.Equal(GenesysTicketUpdateOutcome.RequestTypeConflict, update.Outcome);
        AssertIsDefault(w, ticket);   // from then on only an authorized person reclassifies
    }

    [Fact]
    public async Task ARedeliveredInquiry_DoesNotCreateASecondTicketOrReapplyTheFallback()
    {
        var w = Build();
        var (first, _) = await IngestAsync(w, "conv-dup");
        var audits = w.F.Audit.Entries.Count(e => e.Action == "ApplyDefaultClassification");

        var again = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-dup", GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: w.Leasing.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, again.Outcome);
        Assert.Single(w.F.Tickets.All);
        Assert.Equal(first.TicketId, again.Ticket!.TicketId);
        Assert.Equal(audits, w.F.Audit.Entries.Count(e => e.Action == "ApplyDefaultClassification"));
    }

    // ---- Manual reclassification by an authorized user ----

    private static ClassifyTicketRequestDto Reclassify(int categoryId, byte priority, int? requestTypeId) =>
        new(categoryId, priority, requestTypeId, [1, 2, 3, 4]);

    [Fact]
    public async Task AnAuthorizedUser_CanReclassifyAGeneralTicket_AndItKeepsItsOwner_Priority_And_Sla()
    {
        var w = Build(callCenterFirst: true, agents: 1);
        var (ticket, _) = await IngestAsync(w, "conv-manual", humanFollowUp: true);
        var owner = ticket.CurrentOwnerEmployeeId;
        var priority = ticket.PriorityId;
        var period = Assert.Single(w.F.Sla.SlaInstances.All);

        var result = await w.F.Classification.ClassifyAsync(
            w.Agents[0], [Roles.CsAgent], ticket.TicketId,
            Reclassify(w.FinanceCategory.CategoryId, priority!.Value, w.FinanceType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
        Assert.Equal(w.FinanceType.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(priority, ticket.PriorityId);
        Assert.Same(period, Assert.Single(w.F.Sla.SlaInstances.All));      // the running clock is neither restarted nor duplicated
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "ReclassifyTicket" && e.BeforeValue!.Contains($"CategoryId={w.General.CategoryId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReclassifyingAGeneralTicketToAnotherDepartment_TransfersIt_AndKeepsTheCsAgent()
    {
        var w = Build(callCenterFirst: false);
        var (ticket, _) = await IngestAsync(w, "conv-transfer");
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);
        Assert.Equal(w.Leasing.DepartmentId, ticket.CurrentDepartmentId);

        var result = await w.F.Classification.ClassifyAsync(
            agent, [Roles.CsAgent], ticket.TicketId,
            Reclassify(w.FinanceCategory.CategoryId, ticket.PriorityId!.Value, w.FinanceType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Leasing.DepartmentId, ticket.OriginatingDepartmentId); // history of where it started is untouched
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);                    // a department transfer through classification keeps the CS Agent
    }

    [Fact]
    public async Task ReclassifyingAGeneralTicket_BySelectingGeneralAgain_ChangesNothing()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-same");
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);

        var result = await w.F.Classification.ClassifyAsync(
            agent, [Roles.CsAgent], ticket.TicketId, Reclassify(w.General.CategoryId, ticket.PriorityId!.Value, w.GeneralType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.AlreadyClassified, result.Outcome);
        AssertIsDefault(w, ticket);
    }

    [Fact]
    public async Task AValidlyClassifiedTicket_StillCannotBeClassifiedAgain()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-already", requestType: new GenesysRequestTypeDto(w.FinanceType.RequestTypeId));
        ticket.AssignTo(Guid.NewGuid());

        var result = await w.F.Classification.ClassifyAsync(   // not the assigned agent
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId,
            Reclassify(w.General.CategoryId, ticket.PriorityId!.Value, w.GeneralType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.AlreadyClassified, result.Outcome);
        Assert.Equal(w.FinanceCategory.CategoryId, ticket.CategoryId);
    }

    [Fact]
    public async Task AReadOnlyRole_CannotReclassifyAGeneralTicket()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-readonly");

        var result = await w.F.Classification.ClassifyAsync(
            Guid.NewGuid(), [Roles.ReportingUser], ticket.TicketId,
            Reclassify(w.FinanceCategory.CategoryId, ticket.PriorityId!.Value, w.FinanceType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        AssertIsDefault(w, ticket);
    }

    // ---- CS Agent ownership ----

    [Fact]
    public async Task HumanNeededGeneralTickets_GetTheNextCsAgentInSequence_NotADepartmentEmployee()
    {
        var w = Build(callCenterFirst: true, agents: 3);
        // A Customer Services employee with an assignment rule configured for General must still not receive the ticket automatically.
        var csEmployee = Guid.NewGuid();
        w.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(csEmployee, w.Cs.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.GeneralType.RequestTypeId, csEmployee));

        var owners = new List<Guid?>();
        foreach (var channel in new[] { GenesysChannel.Phone, GenesysChannel.WhatsApp, GenesysChannel.WebsiteChat, GenesysChannel.Phone })
        {
            var (ticket, _) = await IngestAsync(w, $"conv-{owners.Count}", channel, humanFollowUp: true);
            AssertIsDefault(w, ticket);
            Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
            owners.Add(ticket.CurrentOwnerEmployeeId);
        }

        Assert.Equal([w.Agents[0], w.Agents[1], w.Agents[2], w.Agents[0]], owners);
        Assert.DoesNotContain(csEmployee, owners.Cast<Guid>());
    }

    [Fact]
    public async Task AnAiHandledGeneralTicket_IsNotGivenAnAgent_AndAnAiClosedOneNeverNeedsOne()
    {
        var w = Build(callCenterFirst: true, agents: 2);
        var (ticket, _) = await IngestAsync(w, "conv-ai", humanFollowUp: false);
        AssertIsDefault(w, ticket);
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        var closed = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-ai", ClosedByAi: true));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, closed.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        AssertIsDefault(w, ticket);                                              // closing keeps the classification it had
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.TicketId == ticket.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        Assert.Equal(w.Agents[0], (await IngestAsync(w, "conv-next", humanFollowUp: true)).Ticket.CurrentOwnerEmployeeId);  // the rotation did not advance
    }

    [Fact]
    public async Task ConfirmedGenesysAgent_StillTakesAGeneralTicket_AndClassificationNeverClearsThem()
    {
        var w = Build(callCenterFirst: true, agents: 2);
        var (ticket, _) = await IngestAsync(w, "conv-confirmed", humanFollowUp: true);
        var automaticOwner = ticket.CurrentOwnerEmployeeId;
        Assert.Equal(w.Agents[0], automaticOwner);

        // Reclassify by an authorized person: the owner is not cleared or replaced.
        await w.F.Classification.ClassifyAsync(
            w.Agents[0], [Roles.CsAgent], ticket.TicketId,
            Reclassify(w.FinanceCategory.CategoryId, ticket.PriorityId!.Value, w.FinanceType.RequestTypeId));

        Assert.Equal(automaticOwner, ticket.CurrentOwnerEmployeeId);
    }

    // ---- Department transfers keep the classification ----

    [Fact]
    public async Task ATransferOfAGeneralTicket_DoesNotChangeItsClassification()
    {
        var w = Build();
        var (ticket, _) = await IngestAsync(w, "conv-dept-transfer");

        ticket.TransferToDepartment(w.Finance.DepartmentId, preserveOwner: true);

        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        AssertIsDefault(w, ticket);
        Assert.Equal(w.Leasing.DepartmentId, ticket.OriginatingDepartmentId);
    }

    // ---- Detail flag for the UI ----

    [Fact]
    public async Task TicketDetail_ReportsTheFallback_FromStoredIds_NotFromNames()
    {
        var w = Build();
        var (general, _) = await IngestAsync(w, "conv-detail-general");
        var (real, _) = await IngestAsync(w, "conv-detail-real", requestType: new GenesysRequestTypeDto(w.FinanceType.RequestTypeId));
        var query = new TicketQueryAppService(
            w.F.Tickets, w.F.DepartmentAssignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(),
            ReopenPolicy.Default, TimeProvider.System, requestTypeRepository: w.F.RequestTypes, workflowTemplateRepository: w.F.WorkflowTemplates,
            handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(), defaultClassification: w.F.DefaultClassification);

        var defaultDetail = await query.GetDetailAsync(Guid.NewGuid(), [Roles.SystemAdministrator], general.TicketId);
        var realDetail = await query.GetDetailAsync(Guid.NewGuid(), [Roles.SystemAdministrator], real.TicketId);

        Assert.True(defaultDetail.Response!.IsDefaultClassification);
        Assert.False(realDetail.Response!.IsDefaultClassification);
    }
}
