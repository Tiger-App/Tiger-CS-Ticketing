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
/// Human-handled tickets: an eligible CS Agent and a valid Department / Category / Request Type. A known department and request type are kept;
/// a missing category or request type becomes General within the same department; an unknown department becomes Customer Services; a department
/// is never changed because its General is missing (the gap is reported); the assigned CS Agent can review and correct the classification, and a
/// reclassification keeps the agent, SLA, status and history.
/// </summary>
public class DefaultClassificationHumanHandledTests
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
        public required Department Leasing { get; init; }       // has its own General
        public required Category LeasingGeneral { get; init; }
        public required RequestType LeasingGeneralType { get; init; }
        public required Department Handover { get; init; }      // active, NO General of its own
        public required Category HandoverCategory { get; init; }
        public required RequestType HandoverType { get; init; }
        public Guid[] Agents { get; init; } = [];
    }

    private static World Build(int agents = 2)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var (cs, csGeneral, _) = f.SeedDefaultClassification();
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var leasingGeneral = f.Categories.Seed(leasing.DepartmentId, "General");
        var t1 = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 600));
        var leasingType = f.RequestTypes.Add(new RequestType(leasing.DepartmentId, "General", t1.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var handover = f.Departments.AddDepartment("Handover", "HO");
        var handoverCategory = f.Categories.Seed(handover.DepartmentId, "Handover Request");
        var t2 = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 601));
        var handoverType = f.RequestTypes.Add(new RequestType(handover.DepartmentId, "Handover Request", t2.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var ids = Enumerable.Range(0, agents).Select(_ => Guid.NewGuid()).OrderBy(i => i).ToArray();
        foreach (var id in ids)
        {
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, f.CallCenter!.DepartmentId, true, DateTime.UtcNow, null));
        }

        return new World
        {
            F = f, Cs = cs, CsGeneral = csGeneral, Leasing = leasing, LeasingGeneral = leasingGeneral, LeasingGeneralType = leasingType,
            Handover = handover, HandoverCategory = handoverCategory, HandoverType = handoverType, Agents = ids
        };
    }

    private static async Task<Ticket> IngestAsync(
        World w, string conv, int? departmentId, GenesysChannel channel = GenesysChannel.Phone, bool? human = true, GenesysRequestTypeDto? type = null)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conv, channel, CustomerPhone: Phone, DepartmentId: departmentId, RequestType: type, HumanFollowUpRequired: human));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    private static async Task AssertValidAsync(World w, Ticket t) =>
        Assert.Equal(TicketClassificationState.Valid, await w.F.DefaultClassification.AssessAsync(t.CategoryId, t.RequestTypeId));

    // ---- every human-handled ticket: an eligible CS Agent + a valid classification ----

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task HumanHandled_OnEveryChannel_HasACsAgentAndAValidClassification_InTheSameDepartment(GenesysChannel channel)
    {
        var w = Build();

        var t = await IngestAsync(w, "conv-h", w.Leasing.DepartmentId, channel);

        Assert.Contains(t.CurrentOwnerEmployeeId, w.Agents.Cast<Guid?>());
        Assert.Equal(w.Leasing.DepartmentId, t.OriginatingDepartmentId);
        Assert.Equal(w.LeasingGeneral.CategoryId, t.CategoryId);
        Assert.Equal(w.LeasingGeneralType.RequestTypeId, t.RequestTypeId);
        await AssertValidAsync(w, t);
    }

    [Fact]
    public async Task ADepartmentAndRequestTypeAlreadyKnown_ArePreserved()
    {
        var w = Build();

        var t = await IngestAsync(w, "conv-known", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));

        Assert.Equal(w.HandoverCategory.CategoryId, t.CategoryId);
        Assert.Equal(w.HandoverType.RequestTypeId, t.RequestTypeId);
        Assert.Equal(w.Handover.DepartmentId, t.OriginatingDepartmentId);
        Assert.NotNull(t.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "ApplyDefaultClassification");
    }

    [Fact]
    public async Task AnUnknownDepartment_IsCustomerServicesGeneral_WithACsAgent()
    {
        var w = Build();

        var t = await IngestAsync(w, "conv-unknown", departmentId: null);

        Assert.Equal(w.Cs.DepartmentId, t.OriginatingDepartmentId);
        Assert.Equal(w.CsGeneral.CategoryId, t.CategoryId);
        Assert.NotNull(t.CurrentOwnerEmployeeId);
        await AssertValidAsync(w, t);
    }

    [Fact]
    public async Task AHumanHandledInquiryInADepartmentWithoutGeneral_IsRefusedWithAControlledError_NothingIsCreatedOrMoved()
    {
        var w = Build();
        var tickets = w.F.Tickets.All.Count;
        var intakes = w.F.IntakeRecords.All.Count;

        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-no-general", GenesysChannel.Phone, CustomerPhone: Phone, DepartmentId: w.Handover.DepartmentId, HumanFollowUpRequired: true));

        Assert.Equal(GenesysIngestionOutcome.DefaultClassificationNotConfigured, result.Outcome);
        Assert.Contains("HO", result.Detail, StringComparison.Ordinal);
        Assert.Equal(tickets, w.F.Tickets.All.Count);                               // no half-classified ticket persisted
        Assert.Equal(intakes, w.F.IntakeRecords.All.Count);
        var audit = Assert.Single(w.F.Audit.Entries, e => e.Action == "DefaultClassificationConfigurationError");
        Assert.Contains("TicketCreated=false", audit.AfterValue, StringComparison.Ordinal);
        Assert.Contains("EnsureDefaultGeneralClassification.sql", audit.AfterValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnceTheDepartmentsGeneralIsConfigured_TheSameInquiryIsAccepted_InItsOwnDepartment()
    {
        var w = Build();
        var refused = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-retry", GenesysChannel.Phone, CustomerPhone: Phone, DepartmentId: w.Handover.DepartmentId, HumanFollowUpRequired: true));
        Assert.Equal(GenesysIngestionOutcome.DefaultClassificationNotConfigured, refused.Outcome);

        var (general, type) = w.F.SeedGeneral(w.Handover);   // EnsureDefaultGeneralClassification.sql
        var t = await IngestAsync(w, "conv-retry", w.Handover.DepartmentId);

        Assert.Equal(general.CategoryId, t.CategoryId);
        Assert.Equal(type.RequestTypeId, t.RequestTypeId);
        Assert.Equal(w.Handover.DepartmentId, t.OriginatingDepartmentId);
        Assert.NotNull(t.CurrentOwnerEmployeeId);
        await AssertValidAsync(w, t);
    }

    [Fact]
    public async Task ManualCreation_InADepartmentWithoutGeneral_IsRefused_WhenNoRequestTypeCanBeSet()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Handover.DepartmentId, "Handover Question");   // a valid category, but the department has no General request type
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Handover.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        var noRequestType = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "Walk-in"));
        var noCategory = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, null, (byte)PriorityLevel.Medium, "Walk-in", DepartmentId: w.Handover.DepartmentId));
        var withType = await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, w.HandoverCategory.CategoryId, (byte)PriorityLevel.Medium, "Walk-in", RequestTypeId: w.HandoverType.RequestTypeId));

        Assert.Equal(TicketCreationOutcome.DefaultClassificationNotConfigured, noRequestType.Outcome);
        Assert.Equal(TicketCreationOutcome.DefaultClassificationNotConfigured, noCategory.Outcome);
        Assert.Equal(TicketCreationOutcome.Success, withType.Outcome);          // an explicit, valid category + request type needs no General
        var t = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.HandoverType.RequestTypeId, t.RequestTypeId);
        Assert.Equal(2, w.F.Audit.Entries.Count(e => e.Action == "DefaultClassificationConfigurationError"));
    }

    [Fact]
    public async Task ManualCreation_AValidCategoryWithoutARequestType_GetsTheDepartmentsGeneralRequestType_AndKeepsTheCategory()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Inquiry");
        var employee = Guid.NewGuid();
        var intake = new IntakeRecord(WellKnownChannels.FaceToFaceKiosk, "+971500000077", w.Leasing.DepartmentId, false, null, null, employee, DateTime.UtcNow);
        await w.F.IntakeRecords.AddAsync(intake);

        await w.F.ManualTicketCreation.CreateAsync(
            employee, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.Medium, "Walk-in"));

        var t = Assert.Single(w.F.Tickets.All);
        Assert.Equal(category.CategoryId, t.CategoryId);
        Assert.Equal(w.LeasingGeneralType.RequestTypeId, t.RequestTypeId);
        Assert.NotNull(t.CurrentOwnerEmployeeId);
        await AssertValidAsync(w, t);
    }

    private static async Task<(GenesysServiceFixture F, Department Leasing, Ticket Ticket, Guid Agent)> LegacyTicketAsync()
    {
        // Created while nothing was configured: legacy Unclassified.
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var agent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(agent, f.CallCenter!.DepartmentId, true, DateTime.UtcNow, null));
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-legacy-h", GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: leasing.DepartmentId, HumanFollowUpRequired: false));
        var ticket = f.Tickets.All.Single(t => t.TicketId == created.Ticket!.TicketId);
        Assert.Null(ticket.CategoryId);
        f.SeedDefaultClassification();
        return (f, leasing, ticket, agent);
    }

    [Fact]
    public async Task ALegacyTicketWhoseHumanFollowUpIsRequested_GetsItsDepartmentsGeneral_WithoutLosingItsDepartmentOrAgent()
    {
        var (f, leasing, ticket, agent) = await LegacyTicketAsync();
        var (general, type) = f.SeedGeneral(leasing);

        var update = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-legacy-h", HumanFollowUpRequired: true));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        Assert.Equal(general.CategoryId, ticket.CategoryId);
        Assert.Equal(type.RequestTypeId, ticket.RequestTypeId);
        Assert.Equal(leasing.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketClassificationState.Valid, await f.DefaultClassification.AssessAsync(ticket.CategoryId, ticket.RequestTypeId));
    }

    [Fact]
    public async Task ALegacyTicketWhoseHumanFollowUpIsRequested_InADepartmentWithoutGeneral_IsNotHandedToAPerson_AndTheErrorIsAudited()
    {
        var (f, leasing, ticket, _) = await LegacyTicketAsync();

        var update = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-legacy-h", HumanFollowUpRequired: true));

        Assert.Equal(GenesysTicketUpdateOutcome.DefaultClassificationNotConfigured, update.Outcome);
        Assert.Null(ticket.CategoryId);                                   // not pretended valid
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal(leasing.DepartmentId, ticket.CurrentDepartmentId);   // not moved
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(f.Audit.Entries, e => e.Action == "DefaultClassificationConfigurationError" && e.AfterValue!.Contains("Handling=Human", StringComparison.Ordinal));
    }

    // ---- the assigned CS Agent reviews and corrects ----

    private static ClassifyTicketRequestDto Correct(int categoryId, byte priority, int? requestTypeId = null) => new(categoryId, priority, requestTypeId, [1, 2, 3, 4]);

    [Fact]
    public async Task TheAssignedCsAgent_CanCorrectCategoryAndRequestType_KeepingTheAgent_Sla_Status_AndHistory()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-correct", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));
        var otherCategory = w.F.Categories.Seed(w.Handover.DepartmentId, "Other Handover");
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 602));
        var otherType = w.F.RequestTypes.Add(new RequestType(w.Handover.DepartmentId, "Other Handover", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var agent = t.CurrentOwnerEmployeeId!.Value;
        var period = Assert.Single(w.F.Sla.SlaInstances.All);
        var (status, sla, priority, started) = (t.TicketStatus, t.SlaState, t.PriorityId, period.PeriodStartAtUtc);
        var auditBefore = w.F.Audit.Entries.Count;

        var result = await w.F.Classification.ClassifyAsync(agent, [Roles.CsAgent], t.TicketId, Correct(otherCategory.CategoryId, priority!.Value, otherType.RequestTypeId));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(otherCategory.CategoryId, t.CategoryId);
        Assert.Equal(otherType.RequestTypeId, t.RequestTypeId);
        Assert.Equal(template.WorkflowTemplateId, t.WorkflowTemplateId);
        Assert.Equal(agent, t.CurrentOwnerEmployeeId);
        Assert.Equal((status, sla, priority), (t.TicketStatus, t.SlaState, t.PriorityId));
        Assert.Same(period, Assert.Single(w.F.Sla.SlaInstances.All));
        Assert.Equal(started, period.PeriodStartAtUtc);
        Assert.Equal(w.Handover.DepartmentId, t.OriginatingDepartmentId);
        // History is appended to, never rewritten.
        Assert.True(w.F.Audit.Entries.Count > auditBefore);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "ReclassifyTicket" && e.AfterValue!.Contains("CategoryId=" + otherCategory.CategoryId, StringComparison.Ordinal)
            && e.BeforeValue!.Contains("CategoryId=" + w.HandoverCategory.CategoryId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAssignedCsAgent_CanCorrectTheDepartment_AndStaysTheOwner()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-dept-correct", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));
        var agent = t.CurrentOwnerEmployeeId!.Value;
        var leasingCategory = w.F.Categories.Seed(w.Leasing.DepartmentId, "Leasing Inquiry");

        var result = await w.F.Classification.ClassifyAsync(agent, [Roles.CsAgent], t.TicketId, Correct(leasingCategory.CategoryId, t.PriorityId!.Value));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(leasingCategory.CategoryId, t.CategoryId);
        Assert.Equal(w.LeasingGeneralType.RequestTypeId, t.RequestTypeId);       // no type named: Leasing's General request type, never a gap
        Assert.Equal(agent, t.CurrentOwnerEmployeeId);
        Assert.Equal(w.Handover.DepartmentId, t.OriginatingDepartmentId);        // where it started is history
    }

    [Fact]
    public async Task ACorrectionThatNamesNoRequestType_KeepsTheCurrentOneWhenItStillFits()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-keep-type", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));
        var sameDepartmentCategory = w.F.Categories.Seed(w.Handover.DepartmentId, "Another Handover Category");

        await w.F.Classification.ClassifyAsync(t.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], t.TicketId, Correct(sameDepartmentCategory.CategoryId, t.PriorityId!.Value));

        Assert.Equal(sameDepartmentCategory.CategoryId, t.CategoryId);
        Assert.Equal(w.HandoverType.RequestTypeId, t.RequestTypeId);
    }

    [Fact]
    public async Task AReclassification_PreservesTheExistingSlaDeadlines_EvenWhenAMoreUrgentPriorityIsSent_AndNeverRestartsTheClock()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-sla", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));
        var other = w.F.Categories.Seed(w.Handover.DepartmentId, "Other");
        var period = Assert.Single(w.F.Sla.SlaInstances.All);
        var (start, firstResponseDue, resolutionDue, priority) = (period.PeriodStartAtUtc, period.FirstResponseDueAtUtc, period.ResolutionDueAtUtc, t.PriorityId);

        var result = await w.F.Classification.ClassifyAsync(
            t.CurrentOwnerEmployeeId!.Value, [Roles.CsAgent], t.TicketId, Correct(other.CategoryId, (byte)PriorityLevel.Critical));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Same(period, Assert.Single(w.F.Sla.SlaInstances.All));
        Assert.Equal((start, firstResponseDue, resolutionDue), (period.PeriodStartAtUtc, period.FirstResponseDueAtUtc, period.ResolutionDueAtUtc));
        Assert.Null(period.PeriodEndAtUtc);
        Assert.Equal(priority, t.PriorityId);                                 // priority has its own flow
        Assert.Equal(SlaState.Running, t.SlaState);
    }

    [Fact]
    public async Task ACorrectionToADepartmentWithoutAGeneralRequestType_AndNoRequestTypeNamed_IsRefused_NothingChanges()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-correct-no-config", w.Leasing.DepartmentId, type: null);   // Leasing General
        var agent = t.CurrentOwnerEmployeeId!.Value;
        var targetCategory = w.F.Categories.Seed(w.Handover.DepartmentId, "Handover Question");     // Handover has no General request type
        var before = (t.CategoryId, t.RequestTypeId, t.CurrentDepartmentId, t.CurrentOwnerEmployeeId);

        var result = await w.F.Classification.ClassifyAsync(agent, [Roles.CsAgent], t.TicketId, Correct(targetCategory.CategoryId, t.PriorityId!.Value));

        Assert.Equal(TicketMutationOutcome.DefaultClassificationNotConfigured, result.Outcome);
        Assert.Equal(before, (t.CategoryId, t.RequestTypeId, t.CurrentDepartmentId, t.CurrentOwnerEmployeeId));
    }

    [Fact]
    public async Task APartialClassification_RequestTypeButNoCategory_IsRepairedWithTheDepartmentsGeneral_NotLeftHalfClassified()
    {
        var w = Build();
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 603));
        var orphan = w.F.RequestTypes.Add(new RequestType(w.Leasing.DepartmentId, "Orphan Type", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));

        var t = await IngestAsync(w, "conv-partial", w.Leasing.DepartmentId, type: new GenesysRequestTypeDto(orphan.RequestTypeId));

        Assert.Equal(w.LeasingGeneral.CategoryId, t.CategoryId);                // same department's General, not the orphan type
        Assert.Equal(w.LeasingGeneralType.RequestTypeId, t.RequestTypeId);
        await AssertValidAsync(w, t);
    }

    [Fact]
    public async Task OnlyTheAssignedAgentMayCorrectAValidClassification()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-not-yours", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));
        var category = w.F.Categories.Seed(w.Handover.DepartmentId, "Elsewhere");

        var other = await w.F.Classification.ClassifyAsync(Guid.NewGuid(), [Roles.SystemAdministrator], t.TicketId, Correct(category.CategoryId, t.PriorityId!.Value));
        var readOnly = await w.F.Classification.ClassifyAsync(t.CurrentOwnerEmployeeId!.Value, [Roles.ReportingUser], t.TicketId, Correct(category.CategoryId, t.PriorityId!.Value));

        Assert.Equal(TicketMutationOutcome.AlreadyClassified, other.Outcome);
        Assert.Equal(TicketMutationOutcome.Forbidden, readOnly.Outcome);
        Assert.Equal(w.HandoverCategory.CategoryId, t.CategoryId);
    }

    [Fact]
    public async Task ACorrectionIsRefusedOnAClosedTicket()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-closed", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));
        var agent = t.CurrentOwnerEmployeeId!.Value;
        t.ChangeStatus(TicketStatus.InProgress);
        t.Resolve(ResolutionOutcome.Resolved, null);
        t.Close();
        var category = w.F.Categories.Seed(w.Handover.DepartmentId, "Late");

        var result = await w.F.Classification.ClassifyAsync(agent, [Roles.CsAgent], t.TicketId, Correct(category.CategoryId, t.PriorityId!.Value));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(w.HandoverCategory.CategoryId, t.CategoryId);
    }

    [Fact]
    public async Task TicketDetail_OffersCorrectionOnlyToTheAssignedCsAgent()
    {
        var w = Build();
        var t = await IngestAsync(w, "conv-flag", w.Handover.DepartmentId, type: new GenesysRequestTypeDto(w.HandoverType.RequestTypeId));
        var agent = t.CurrentOwnerEmployeeId!.Value;
        var query = new TicketQueryAppService(
            w.F.Tickets, w.F.DepartmentAssignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(),
            ReopenPolicy.Default, TimeProvider.System, requestTypeRepository: w.F.RequestTypes, workflowTemplateRepository: w.F.WorkflowTemplates,
            handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(), defaultClassification: w.F.DefaultClassification);

        var owner = await query.GetDetailAsync(agent, [Roles.CsAgent], t.TicketId);
        var admin = await query.GetDetailAsync(Guid.NewGuid(), [Roles.SystemAdministrator], t.TicketId);

        Assert.True(owner.Response!.CanCorrectClassification);
        Assert.False(admin.Response!.CanCorrectClassification);
    }

    // ---- AI-closed: valid classification, no CS Agent ----

    [Fact]
    public async Task AnAiClosedTicket_GetsItsDepartmentsGeneral_WithoutMovingOrAssigning()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var handover = f.Departments.AddDepartment("Handover", "HO");
        var agent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(agent, f.CallCenter!.DepartmentId, true, DateTime.UtcNow, null));
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-ai-ho", GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: handover.DepartmentId, HumanFollowUpRequired: false));
        var ticket = f.Tickets.All.Single(t => t.TicketId == created.Ticket!.TicketId);
        f.SeedDefaultClassification();
        var (general, _) = f.SeedGeneral(handover);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-ai-ho", ClosedByAi: true));

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(handover.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(general.CategoryId, ticket.CategoryId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketClassificationState.Valid, await f.DefaultClassification.AssessAsync(ticket.CategoryId, ticket.RequestTypeId));
    }
}
