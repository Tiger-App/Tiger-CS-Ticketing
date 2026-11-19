using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Call Center first on the Genesys entry points: a human handoff routes the ticket to the Call Center QUEUE (the AI Agent stays the owner, no Round Robin, nobody is assigned until a person accepts);
/// a human-follow-up inquiry enters the Call Center at creation and is not moved past it by system request-type routing; an AI-handled
/// inquiry is untouched.
/// </summary>
public class GenesysCallCenterFirstTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static Guid[] AddCallCenterAgents(GenesysServiceFixture f, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToArray();
        foreach (var id in ids)
        {
            f.DepartmentAssignments.Assignments.Add(
                new UserDepartmentAssignment(id, f.CallCenter!.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        return ids;
    }

    private static async Task<long> IngestAiTicketAsync(GenesysServiceFixture f, string conversationId, int departmentId)
    {
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: departmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return result.Ticket!.TicketId;
    }

    [Fact]
    public async Task Handoff_RoutesToCallCenterQueue_KeepsTheAiOwner_AndAssignsNobody_PerTicket()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = AddCallCenterAgents(f, 2);
        var outsider = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(outsider, cs.DepartmentId, true, DateTime.UtcNow, null));

        var t1 = await IngestAiTicketAsync(f, "conv-1", cs.DepartmentId);
        var t2 = await IngestAiTicketAsync(f, "conv-2", cs.DepartmentId);
        var t3 = await IngestAiTicketAsync(f, "conv-3", cs.DepartmentId);

        // The AI-handled ticket is untouched until a person is asked for: the AI Agent owns it, no person does.
        Assert.All(f.Tickets.All, t => Assert.Equal(cs.DepartmentId, t.CurrentDepartmentId));
        Assert.All(f.Tickets.All, t => Assert.Null(t.CurrentOwnerEmployeeId));
        Assert.All(f.Tickets.All, t => Assert.Equal(ServiceAccount, t.AiAgentOwnerEmployeeId));

        foreach (var conversation in new[] { "conv-1", "conv-2", "conv-3" })
        {
            var handoff = await f.AgentHandoff.RequestAsync(
                ServiceAccount, new GenesysHandoffRequestDto(conversation, AgentAvailable: false, Mode: "HumanTakeover"));
            Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        }

        Ticket Get(long id) => f.Tickets.All.Single(t => t.TicketId == id);
        Assert.All(new[] { t1, t2, t3 }, id => Assert.Equal(f.CallCenter!.DepartmentId, Get(id).CurrentDepartmentId));
        Assert.All(new[] { t1, t2, t3 }, id => Assert.Equal(cs.DepartmentId, Get(id).OriginatingDepartmentId));

        // Queue only: the AI Agent is still the owner, nobody is dealt the ticket, the rotation cursor never moves.
        Assert.All(new[] { t1, t2, t3 }, id => Assert.Null(Get(id).CurrentOwnerEmployeeId));
        Assert.All(new[] { t1, t2, t3 }, id => Assert.Equal(ServiceAccount, Get(id).AiAgentOwnerEmployeeId));
        Assert.DoesNotContain(f.Tickets.All, t => t.CurrentOwnerEmployeeId == outsider || (t.CurrentOwnerEmployeeId is { } o && agents.Contains(o)));
        Assert.Empty(f.TicketAssignments.Added);
        Assert.All(f.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign");
    }

    [Fact]
    public async Task Handoff_WithNoEligibleCallCenterAgent_LeavesTheTicketInTheCallCenterQueue_WithTheAiOwner()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var ticketId = await IngestAiTicketAsync(f, "conv-empty", cs.DepartmentId);

        await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-empty", AgentAvailable: false, Mode: "HumanTakeover"));

        var ticket = f.Tickets.All.Single(t => t.TicketId == ticketId);
        Assert.Equal(f.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(ServiceAccount, ticket.AiAgentOwnerEmployeeId);
        Assert.Empty(f.TicketAssignments.Added);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign");
    }

    [Fact]
    public async Task HumanFollowUpInquiryWithARequestType_EntersCallCenterQueue_IsNotMovedPastItByRequestTypeRouting_AndAssignsNobody()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var (bot, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        f.QueueMappings.Map("queue-bot", bot.DepartmentId);
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var type = f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, "Leasing", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var leasingEmployee = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(leasingEmployee, leasing.DepartmentId, true, DateTime.UtcNow, null));
        f.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(type.RequestTypeId, leasingEmployee));
        var agent = AddCallCenterAgents(f, 1)[0];

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-lease", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", QueueId: "queue-bot",
                RequestType: new GenesysRequestTypeDto(Name: "Leasing"), HumanFollowUpRequired: true));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var ticket = f.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);

        // The Call Center queue is the first stop: request-type routing does not move the ticket past it, and the system never picks the person.
        Assert.Equal(f.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(bot.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(type.RequestTypeId, ticket.RequestTypeId);                 // still classified

        // Nobody is dealt the ticket: not the Call Center agent (no Round Robin), not the request type's configured employee (the AI handles first).
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(ServiceAccount, ticket.AiAgentOwnerEmployeeId);
        Assert.Empty(f.TicketAssignments.Added);
        Assert.All(f.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign");

        // A person who takes it from the queue (Take Ownership) can only transfer it to the request type's department.
        var assignment = new TicketAssignmentAppService(
            f.Tickets, f.TicketAssignments, f.DepartmentAssignments, f.Departments, f.UnitOfWork, f.Audit, TimeProvider.System,
            f.DepartmentSettings,
            new TicketAutoAssignmentService(f.AssignmentRules, f.DepartmentSettings, f.DepartmentAssignments, f.TicketAssignments, f.Audit, f.Rotation, f.Departments),
            f.WorkflowEvents, requestTypeRepository: f.RequestTypes);
        ticket.AssignTo(agent);                                                 // the person takes it: the AI Agent stops being the owner
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.AiAgentOwnerEmployeeId);

        var wrong = await assignment.TransferAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(bot.DepartmentId, "x", []));
        Assert.Equal(TicketMutationOutcome.TransferTargetMustMatchRequestType, wrong.Outcome);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);

        var right = await assignment.TransferAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(leasing.DepartmentId, "x", []));
        Assert.Equal(TicketMutationOutcome.Success, right.Outcome);
        Assert.Equal(leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(leasingEmployee, ticket.CurrentOwnerEmployeeId);           // the destination department's rule assigns its configured employee
    }

    [Fact]
    public async Task AiHandledInquiryWithARequestType_StillRoutesByRequestType_WithoutTouchingTheCallCenter()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var (bot, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        f.QueueMappings.Map("queue-bot", bot.DepartmentId);
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, "Leasing", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        AddCallCenterAgents(f, 1);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-ai", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", QueueId: "queue-bot",
                RequestType: new GenesysRequestTypeDto(Name: "Leasing")));

        var ticket = f.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
        Assert.Equal(leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.DoesNotContain(f.Audit.Entries, e => e.AfterValue is not null && e.AfterValue.Contains("Call Center first"));
    }
}
