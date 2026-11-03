using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Genesys tickets start on the default (Normal) priority with their SLA
/// running from creation, and a request type — supplied at ingestion or
/// later by the bot — routes the ticket to its configured department, runs
/// the existing automatic assignment and applies its SLA policy without
/// restarting the clock. A missing/unusable request type leaves the ticket
/// Unclassified for the existing manual classification workflow — never a
/// human handoff.
/// </summary>
public class GenesysRequestTypeRoutingTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    /// <summary>An arrival department (Customer Service), a responsible department (Finance) and one Finance request type with a published workflow.</summary>
    private sealed record World(
        GenesysServiceFixture F, Department Arrival, Department Finance, RequestType RequestType);

    private static World Build()
    {
        var f = new GenesysServiceFixture();
        var (arrival, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var finance = f.Departments.AddDepartment("Finance", "FIN");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var requestType = f.RequestTypes.Add(new RequestType(
            finance.DepartmentId, "Payment Receipt", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));
        return new World(f, arrival, finance, requestType);
    }

    private static GenesysInquiryDto Inquiry(World w, string conversationId, GenesysRequestTypeDto? requestType = null) =>
        new(conversationId, GenesysChannel.WebsiteChat,
            CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali",
            DepartmentId: w.Arrival.DepartmentId, RequestType: requestType);

    private static Guid AddFinanceEmployee(World w)
    {
        var employee = Guid.NewGuid();
        w.F.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(employee, w.Finance.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        return employee;
    }

    private static async Task<Ticket> IngestAsync(World w, string conversationId, GenesysRequestTypeDto? requestType = null)
    {
        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, conversationId, requestType));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    // ---- New Genesys ticket → Normal priority and applicable SLA ----

    [Fact]
    public async Task NewTicket_DefaultsToTheConfiguredNormalPriority_AndStartsItsSlaAtCreation()
    {
        var w = Build();

        var ticket = await IngestAsync(w, "conv-default");

        // "Normal" resolves through the Priorities data + the documented alias
        // (Medium), not a hard-coded id in the ingestion path.
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.False(ticket.IsClassified);
        Assert.Equal(SlaState.Running, ticket.SlaState);

        var period = Assert.Single(w.F.Sla.SlaInstances.All);
        Assert.Equal(ticket.CreatedAtUtc, period.PeriodStartAtUtc); // clock start = creation
        Assert.Equal((byte)PriorityLevel.Medium, period.PriorityId);
        Assert.Null(period.RequestTypeSlaPolicyId);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task DefaultPriority_ResolvesByConfiguredName_AndAnUnknownNameDegradesToNoPriority_Audited()
    {
        var byName = Build();
        byName.F.Options.DefaultTicketPriority = "High";
        Assert.Equal((byte)PriorityLevel.High, (await IngestAsync(byName, "conv-high")).PriorityId);

        var unknown = Build();
        unknown.F.Options.DefaultTicketPriority = "Whenever";
        var ticket = await IngestAsync(unknown, "conv-unknown");

        // The inquiry is never lost over configuration; it just starts no SLA and says so.
        Assert.Null(ticket.PriorityId);
        Assert.Equal(SlaState.NotApplicable, ticket.SlaState);
        Assert.Empty(unknown.F.Sla.SlaInstances.All);
        Assert.Contains(unknown.F.Audit.Entries, e =>
            e.Action == "GenesysInquiryIngested" && e.AfterValue!.Contains("DefaultPriority=(none", StringComparison.Ordinal));
    }

    // ---- Valid request type → configured department + existing auto-assignment ----

    [Fact]
    public async Task ValidRequestType_AtIngestion_RoutesToTheConfiguredDepartment_AndAutoAssigns()
    {
        var w = Build();
        var employee = AddFinanceEmployee(w);
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.RequestType.RequestTypeId, employee));

        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(w, "conv-routed", new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal("Classified", result.Classification!.Status);
        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal(w.RequestType.RequestTypeId, ticket.RequestTypeId);
        Assert.NotNull(ticket.WorkflowTemplateId);
        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Arrival.DepartmentId, ticket.OriginatingDepartmentId); // write-once
        Assert.Equal(employee, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(w.Finance.DepartmentId, result.Ticket!.CurrentDepartmentId);

        // Classification, routing and assignment are each audited.
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "ClassifyRequestType");
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "Transfer" && e.AfterValue!.Contains($"DepartmentId={w.Finance.DepartmentId}", StringComparison.Ordinal));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains($"AssignedEmployeeId={employee}", StringComparison.Ordinal));

        // A request type that classified the ticket means no human classification queue.
        Assert.Empty(w.F.Handoffs.All);
    }

    [Fact]
    public async Task ValidRequestType_WithNoEligibleEmployee_LeavesTheTicketInTheResponsibleDepartmentQueue()
    {
        var w = Build();
        // The rule's employee is not a member of Finance, so there is nobody eligible.
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.RequestType.RequestTypeId, Guid.NewGuid()));

        var ticket = await IngestAsync(w, "conv-queue", new GenesysRequestTypeDto(Name: "payment receipt"));

        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.StartsWith("DepartmentQueue", StringComparison.Ordinal));
    }

    // ---- Missing / unresolved request type → human classification queue ----

    [Fact]
    public async Task MissingRequestType_StaysUnclassified_AndRaisesNoHumanHandoff()
    {
        var w = Build();

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-missing"));

        var ticket = Assert.Single(w.F.Tickets.All);
        Assert.Equal("AwaitingClassification", result.Classification!.Status);
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal(w.Arrival.DepartmentId, ticket.CurrentDepartmentId);
        // Missing classification is NOT a request for a human.
        Assert.Empty(w.F.Handoffs.All);
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffRequested);
    }

    [Fact]
    public async Task MissingRequestType_CreatesTheTicketOnNormalPriority_AndLeavesItForManualClassification()
    {
        var w = Build();

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-missing-normal"));
        var blank = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-blank-normal", new GenesysRequestTypeDto(Name: "   ")));

        // Absent and blank are the same thing: no request type was supplied. Not an error.
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, blank.Outcome);
        Assert.All(w.F.Tickets.All, t =>
        {
            Assert.Equal((byte)PriorityLevel.Medium, t.PriorityId);          // Normal
            Assert.Null(t.RequestTypeId);
            Assert.False(t.IsClassified);
            Assert.Equal(SlaState.Running, t.SlaState);                       // the Normal SLA runs from creation
        });
        Assert.Empty(w.F.Handoffs.All);                                       // no human work item for a missing classification
        Assert.All([result, blank], r => Assert.Equal("AwaitingClassification", r.Classification!.Status));
    }

    [Theory]
    [InlineData("unknown-id")]
    [InlineData("unknown-name")]
    [InlineData("inactive")]
    [InlineData("ambiguous-name")]
    public async Task ExplicitlyInvalidRequestType_IsRefused_BeforeAnythingIsWritten(string kind)
    {
        var w = Build();
        var inactive = w.F.RequestTypes.Add(new RequestType(
            w.Finance.DepartmentId, "Retired", w.RequestType.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true, isActive: false));
        var other = w.F.Departments.AddDepartment("Legal", "LGL");
        w.F.RequestTypes.Add(new RequestType(
            other.DepartmentId, "Payment Receipt", w.RequestType.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));   // same name, second department
        var dto = kind switch
        {
            "unknown-id" => new GenesysRequestTypeDto(RequestTypeId: 99999),
            "unknown-name" => new GenesysRequestTypeDto(Name: "No Such Request"),
            "inactive" => new GenesysRequestTypeDto(inactive.RequestTypeId),
            _ => new GenesysRequestTypeDto(Name: "Payment Receipt")
        };
        var audit = w.F.Audit.Entries.Count;

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-bad", dto));

        // "422, nothing written": no ticket, intake record, interaction, SLA period, human work item, audit entry or notification.
        Assert.Equal(GenesysIngestionOutcome.RequestTypeInvalid, result.Outcome);
        Assert.Null(result.Ticket);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.Empty(w.F.Tickets.All);
        Assert.Empty(w.F.IntakeRecords.All);
        Assert.Empty(w.F.Interactions.All);
        Assert.Empty(w.F.Handoffs.All);
        Assert.Empty(w.F.Sla.SlaInstances.All);
        Assert.Equal(audit, w.F.Audit.Entries.Count);
        Assert.Empty(w.F.Outbox.Staged);

        // Nothing was half-created, so the caller fixes the value and retries the same conversation.
        var retry = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-bad", new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, retry.Outcome);
        Assert.Equal(w.RequestType.RequestTypeId, Assert.Single(w.F.Tickets.All).RequestTypeId);
    }

    [Fact]
    public async Task AnInvalidRequestType_OnAnAlreadyIngestedConversation_ReturnsTheExistingTicket_AndChangesNothing()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-again");
        var audit = w.F.Audit.Entries.Count;

        var retry = await w.F.Ingestion.IngestAsync(ServiceAccount, Inquiry(w, "conv-again", new GenesysRequestTypeDto(RequestTypeId: 99999)));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, retry.Outcome);   // idempotent retry; the original ticket is returned
        Assert.Equal(ticket.TicketId, retry.Ticket!.TicketId);
        Assert.Null(ticket.RequestTypeId);
        Assert.Equal(audit, w.F.Audit.Entries.Count);
    }

    // ---- Classification received later → routing without losing SLA history ----

    [Fact]
    public async Task ClassificationReceivedLater_Routes_AndNeverRestartsTheSlaClock()
    {
        var w = Build();
        var employee = AddFinanceEmployee(w);
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.RequestType.RequestTypeId, employee));
        // A request-type SLA on the Normal tier: 8 calendar hours to resolve.
        await w.F.Sla.RequestTypeSla.AddAsync(new RequestTypeSlaPolicy(
            w.RequestType.RequestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Hours,
            null, null, 8, null, clockBasis: SlaClockBasis.TwentyFourSeven));

        var ticket = await IngestAsync(w, "conv-later");
        var before = Assert.Single(w.F.Sla.SlaInstances.All);
        var startedAt = before.PeriodStartAtUtc;
        var priorResolutionDue = before.ResolutionDueAtUtc;
        Assert.Empty(w.F.Handoffs.All);

        var result = await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId,
            new GenesysTicketUpdateDto("conv-later", RequestType: new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal("Classified", result.Classification!.Status);
        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Arrival.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(employee, ticket.CurrentOwnerEmployeeId);

        // Classification raises no human work and leaves none behind.
        Assert.Empty(w.F.Handoffs.All);

        // The SAME period, from the SAME start: the request-type policy was
        // applied to it, measured from the original clock start.
        var after = Assert.Single(w.F.Sla.SlaInstances.All);
        Assert.Same(before, after);
        Assert.Equal(startedAt, after.PeriodStartAtUtc);
        Assert.Null(after.PeriodEndAtUtc);
        Assert.NotNull(after.RequestTypeSlaPolicyId);
        Assert.Equal(startedAt.AddHours(8), after.ResolutionDueAtUtc);
        Assert.NotEqual(priorResolutionDue, after.ResolutionDueAtUtc);
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "ReapplySlaPolicy" && e.AfterValue!.Contains("\"clockRestarted\":false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassificationReceivedLater_KeepsABreachAndAnAnsweredFirstResponse()
    {
        var w = Build();
        await w.F.Sla.RequestTypeSla.AddAsync(new RequestTypeSlaPolicy(
            w.RequestType.RequestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Hours,
            1, null, 72, null, clockBasis: SlaClockBasis.TwentyFourSeven));
        var ticket = await IngestAsync(w, "conv-breach");
        var period = Assert.Single(w.F.Sla.SlaInstances.All);

        // First Response already breached (and the Resolution target still pending).
        period.MarkBreached(SlaDeadlineType.FirstResponse);
        var firstResponseDue = period.FirstResponseDueAtUtc;

        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId,
            new GenesysTicketUpdateDto("conv-breach", RequestType: new GenesysRequestTypeDto(Name: "Payment Receipt")));

        Assert.True(period.FirstResponseBreached);                    // never erased
        Assert.Equal(firstResponseDue, period.FirstResponseDueAtUtc); // never moved
        Assert.Equal(period.PeriodStartAtUtc.AddHours(72), period.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Update_RepeatingTheSameRequestType_IsIdempotent_AnInvalidOneIsRefused_ADifferentOneReclassifiesWhileNoHumanHasWorkedTheTicket()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-idem");
        var other = w.F.RequestTypes.Add(new RequestType(
            w.Finance.DepartmentId, "Refund", w.RequestType.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));

        GenesysTicketUpdateDto Update(GenesysRequestTypeDto rt) => new("conv-idem", RequestType: rt);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Update(new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));
        var auditCount = w.F.Audit.Entries.Count;

        var again = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Update(new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, again.Outcome);
        Assert.Equal(GenesysClassificationOutcome.AlreadyClassified, again.Classification!.Outcome);
        Assert.Equal(auditCount, w.F.Audit.Entries.Count); // nothing re-audited, nothing re-routed

        var different = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Update(new GenesysRequestTypeDto(other.RequestTypeId)));
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, different.Outcome);
        Assert.Equal(GenesysClassificationOutcome.Classified, different.Classification!.Outcome);
        Assert.Equal(other.RequestTypeId, ticket.RequestTypeId);

        var invalid = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Update(new GenesysRequestTypeDto(Name: "No Such Thing")));
        Assert.Equal(GenesysTicketUpdateOutcome.InvalidRequestType, invalid.Outcome); // validated first, whatever the ticket already has
    }

    [Fact]
    public async Task Update_AnInvalidRequestType_IsRefused_AndWritesNothing()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-invalid");
        var auditCount = w.F.Audit.Entries.Count;

        var inactive = w.F.RequestTypes.Add(new RequestType(
            w.Finance.DepartmentId, "Retired", w.RequestType.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true, isActive: false));

        foreach (var bad in new[] { new GenesysRequestTypeDto(RequestTypeId: 4242), new GenesysRequestTypeDto(Name: "Nope"), new GenesysRequestTypeDto(inactive.RequestTypeId) })
        {
            var result = await w.F.TicketUpdate.UpdateAsync(
                ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto("conv-invalid", RequestType: bad));
            Assert.Equal(GenesysTicketUpdateOutcome.InvalidRequestType, result.Outcome);
        }

        Assert.Null(ticket.RequestTypeId);
        Assert.Equal(w.Arrival.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(auditCount, w.F.Audit.Entries.Count);
    }

    // ---- Repeated conversation ingestion ----

    [Fact]
    public async Task RepeatedIngestion_NeverDuplicatesTheTicket_NorResetsPriorityOrSla()
    {
        var w = Build();
        var ticket = await IngestAsync(w, "conv-repeat");
        var period = Assert.Single(w.F.Sla.SlaInstances.All);
        var started = period.PeriodStartAtUtc;

        // A staff member raised the priority since (the SLA period was replaced under the earlier-of rule).
        await w.F.Sla.DueDates.ReplaceCurrentPeriodForUpgradeAsync(
            ticket, (byte)PriorityLevel.High, DateTime.UtcNow, ServiceAccount, Guid.NewGuid());
        ticket.ChangePriority((byte)PriorityLevel.High);
        var instancesBefore = w.F.Sla.SlaInstances.All.Count;

        // The same conversation arrives again — with a request type this time.
        var again = await w.F.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(w, "conv-repeat", new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, again.Outcome);
        Assert.Single(w.F.Tickets.All);
        Assert.Equal((byte)PriorityLevel.High, ticket.PriorityId);       // priority not overwritten
        Assert.Equal(instancesBefore, w.F.Sla.SlaInstances.All.Count);   // no new SLA period
        Assert.Equal(started, w.F.Sla.SlaInstances.All.First().PeriodStartAtUtc);
        Assert.Null(ticket.RequestTypeId);                               // a retry never classifies
        Assert.Equal(w.Arrival.DepartmentId, ticket.CurrentDepartmentId);
    }

    // ---- First Response ----

    [Fact]
    public async Task BotReplyAssignmentAndTransfer_DoNotCountAsFirstResponse_ButTheFirstHumanReplyDoes()
    {
        var w = Build();
        var employee = AddFinanceEmployee(w);
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.RequestType.RequestTypeId, employee));
        var ticket = await IngestAsync(w, "conv-fr", new GenesysRequestTypeDto(w.RequestType.RequestTypeId));

        // Routed and assigned...
        Assert.Equal(employee, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.FirstHumanResponseAtUtc);

        // ...and the bot replying does not stop First Response either.
        var t0 = ticket.CreatedAtUtc;
        var botOnly = await w.F.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-fr", t0.AddMinutes(2), "Timeout",
                Transcript:
                [
                    new GenesysTranscriptMessageDto("Customer", t0.AddMinutes(0), "Hi"),
                    new GenesysTranscriptMessageDto("VirtualAgent", t0.AddMinutes(1), "Hello! How can I help?")
                ]));
        Assert.Equal(GenesysConversationEndOutcome.Ended, botOnly.Outcome);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
        var period = Assert.Single(w.F.Sla.SlaInstances.All);
        Assert.False(period.FirstResponseBreached);
        Assert.Equal(SlaState.Running, ticket.SlaState);

        // The first qualifying human reply stops it.
        var human = await w.F.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-fr", t0.AddMinutes(2), "Timeout",
                Transcript:
                [
                    new GenesysTranscriptMessageDto("Customer", t0.AddMinutes(0), "Hi"),
                    new GenesysTranscriptMessageDto("VirtualAgent", t0.AddMinutes(1), "Hello! How can I help?"),
                    new GenesysTranscriptMessageDto("HumanAgent", t0.AddMinutes(3), "Hi Ahmed, this is Finance.", "Sara")
                ]));
        Assert.Equal(t0.AddMinutes(3), ticket.FirstHumanResponseAtUtc);
        _ = human;
    }

    // ---- Tickets that predate default priorities ----

    [Fact]
    public async Task ATicketWithNoPriority_GetsTheDefault_AndStartsItsClockAtClassification_NotBackdated()
    {
        var w = Build();
        w.F.Options.DefaultTicketPriority = null; // as before this change: no priority, no SLA
        var ticket = await IngestAsync(w, "conv-legacy");
        Assert.Null(ticket.PriorityId);
        Assert.Empty(w.F.Sla.SlaInstances.All);

        w.F.Options.DefaultTicketPriority = "Normal";
        var before = DateTime.UtcNow;
        await w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId,
            new GenesysTicketUpdateDto("conv-legacy", RequestType: new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));

        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);
        Assert.Equal(SlaState.Running, ticket.SlaState);
        var period = Assert.Single(w.F.Sla.SlaInstances.All);
        Assert.True(period.PeriodStartAtUtc >= before);
        Assert.True(period.PeriodStartAtUtc > ticket.CreatedAtUtc);
    }

    // ---- Agent classification of a defaulted ticket ----

    [Fact]
    public async Task AgentClassification_KeepsTheRunningSlaPeriod_RaisesItOnlyForAMoreUrgentPriority_AndRefusesALessUrgentOne()
    {
        var w = Build();
        var (_, category) = (w.Arrival, w.F.Categories.Seed(w.Arrival.DepartmentId));
        var ticket = await IngestAsync(w, "conv-agent");
        var original = Assert.Single(w.F.Sla.SlaInstances.All);
        var roles = new[] { Roles.CsAgent };
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent); // a CS Agent works only a ticket assigned to them

        // Less urgent than the default: still a downgrade, still needs approval.
        var low = await w.F.Classification.ClassifyAsync(
            agent, roles, ticket.TicketId,
            new ClassifyTicketRequestDto(category.CategoryId, (byte)PriorityLevel.Low, null, ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.DowngradeRequiresApproval, low.Outcome);
        Assert.False(ticket.IsClassified);

        // The same priority: the category is added to the running period; nothing restarts.
        var same = await w.F.Classification.ClassifyAsync(
            agent, roles, ticket.TicketId,
            new ClassifyTicketRequestDto(category.CategoryId, (byte)PriorityLevel.Medium, null, ticket.RowVersion));
        Assert.Equal(TicketMutationOutcome.Success, same.Outcome);
        Assert.True(ticket.IsClassified);
        Assert.Same(original, Assert.Single(w.F.Sla.SlaInstances.All));
        Assert.Null(original.PeriodEndAtUtc);
        Assert.Equal(SlaState.Running, ticket.SlaState);
    }

    [Fact]
    public async Task AgentClassification_AsMoreUrgent_ReplacesThePeriodUnderTheEarlierOfRule()
    {
        var w = Build();
        var category = w.F.Categories.Seed(w.Arrival.DepartmentId);
        var ticket = await IngestAsync(w, "conv-urgent");
        var original = Assert.Single(w.F.Sla.SlaInstances.All);
        var originalResolutionDue = original.ResolutionDueAtUtc;
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent); // a CS Agent works only a ticket assigned to them

        var result = await w.F.Classification.ClassifyAsync(
            agent, [Roles.CsAgent], ticket.TicketId,
            new ClassifyTicketRequestDto(category.CategoryId, (byte)PriorityLevel.Critical, null, ticket.RowVersion));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal((byte)PriorityLevel.Critical, ticket.PriorityId);
        Assert.NotNull(original.PeriodEndAtUtc);                         // history kept
        var current = w.F.Sla.SlaInstances.All.Single(i => i.PeriodEndAtUtc is null);
        Assert.Equal(SlaChangeReason.Upgrade, current.ChangeReason);
        Assert.True(current.ResolutionDueAtUtc <= originalResolutionDue); // an upgrade only tightens
        Assert.Equal(original.FirstResponseDueAtUtc, current.FirstResponseDueAtUtc);
    }
}
