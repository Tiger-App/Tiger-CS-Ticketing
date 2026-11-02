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
/// Ownership changes made by the SYSTEM or by lifecycle operations — automatic request-type routing and Handoff Accept —
/// are recorded in the existing workflow event history under their own event types, with where ownership landed, and are
/// never classified as an escalation or counted as a ticket transfer. (Reopen and Genesys call transfers have their own
/// dedicated tests beside the services they exercise.)
/// </summary>
public class TicketOwnershipChangeEventTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly string[] AgentRoles = [Roles.CsAgent];

    // ---- Automatic request-type routing ----

    private sealed record World(GenesysServiceFixture F, Department Arrival, Department Finance, RequestType RequestType);

    private static World BuildRouting()
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

    private static async Task<Ticket> IngestWithRequestTypeAsync(World w, string conversationId)
    {
        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali",
            DepartmentId: w.Arrival.DepartmentId, RequestType: new GenesysRequestTypeDto(w.RequestType.RequestTypeId)));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return w.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    [Fact]
    public async Task AutomaticRouting_ToTheRequestTypesDepartment_IsRecordedAsSystemRouting_WithNoActor()
    {
        var w = BuildRouting();
        var financeOfficer = Guid.NewGuid();
        w.F.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(financeOfficer, w.Finance.DepartmentId, isPrimary: true, DateTime.UtcNow, null));
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.RequestType.RequestTypeId, financeOfficer));

        var ticket = await IngestWithRequestTypeAsync(w, "conv-routed");

        Assert.Equal(w.Finance.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(financeOfficer, ticket.CurrentOwnerEmployeeId);
        var routed = Assert.Single(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.TicketTransferred);
        Assert.Null(routed.ActorEmployeeId);                                        // the system, not an agent
        Assert.Equal((byte)TicketTransferReason.SystemRouting, routed.TransferReasonCode);
        Assert.Equal(w.Arrival.DepartmentId, routed.FromDepartmentId);
        Assert.Equal(w.Finance.DepartmentId, routed.ToDepartmentId);
        Assert.Equal(financeOfficer, routed.ToEmployeeId);                          // where the configured rule landed it
        Assert.False(routed.IsEscalation);                                          // never an agent's judgement
    }

    [Fact]
    public async Task AutomaticRouting_ToADepartmentQueue_RecordsNoReceivingEmployee()
    {
        var w = BuildRouting();

        var ticket = await IngestWithRequestTypeAsync(w, "conv-routed-queue");

        Assert.Null(ticket.CurrentOwnerEmployeeId);
        var routed = Assert.Single(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.TicketTransferred);
        Assert.Null(routed.ToEmployeeId);
        Assert.False(routed.IsEscalation);
    }

    [Fact]
    public async Task ClassifyingATicketThatDoesNotMoveDepartments_WritesNoTransferEvent()
    {
        var w = BuildRouting();
        // A request type of the ticket's own (arrival) department: nothing moves, so nothing is a transfer.
        var template = w.F.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 301));
        var sameDepartment = w.F.RequestTypes.Add(new RequestType(
            w.Arrival.DepartmentId, "General Enquiry", template.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true));

        var result = await w.F.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            "conv-same", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: w.Arrival.DepartmentId,
            RequestType: new GenesysRequestTypeDto(sameDepartment.RequestTypeId)));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.EventType == WorkflowEventType.TicketTransferred);
    }

    // ---- Handoff Accept ----

    private static async Task<(GenesysServiceFixture F, long HandoffId, Guid Agent, int DepartmentId)> PendingHandoffAsync(string conversationId)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId));
        var requested = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(conversationId));
        var agent = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(agent, department.DepartmentId, isPrimary: true, DateTime.UtcNow, null));
        return (f, requested.TicketAgentHandoffId!.Value, agent, department.DepartmentId);
    }

    [Fact]
    public async Task HandoffAccept_RecordsTheNewOwnerOnTheHandoffStartedEvent_NotAsATransferOrEscalation()
    {
        var (f, handoffId, agent, departmentId) = await PendingHandoffAsync("conv-accept");

        var accepted = await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);

        Assert.Equal(AgentHandoffOutcome.Success, accepted.Outcome);
        var started = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffStarted);
        Assert.Equal(agent, started.ActorEmployeeId);
        Assert.Equal(agent, started.ToEmployeeId);
        Assert.Equal(departmentId, started.FromDepartmentId);
        Assert.Equal(departmentId, started.ToDepartmentId);
        Assert.Null(started.TransferReasonCode);
        Assert.False(started.IsEscalation);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.TicketTransferred);
        Assert.Equal(agent, f.Tickets.All.Single().CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task HandoffAccept_ByTheCurrentOwner_DoesNotRecordASecondOwnershipChange()
    {
        var (f, handoffId, agent, _) = await PendingHandoffAsync("conv-accept-twice");

        await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);
        await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);

        Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffStarted);
    }

    [Fact]
    public async Task HandoffAccept_DoesNotCountAsATransfer_InTheTicketDetail()
    {
        var (f, handoffId, agent, _) = await PendingHandoffAsync("conv-accept-detail");
        await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);
        var ticketId = f.Tickets.All.Single().TicketId;
        var query = new TicketQueryAppService(
            f.Tickets, f.DepartmentAssignments, f.Resolutions, f.StatusHistory, ReopenPolicy.Default, TimeProvider.System,
            interactionRepository: f.Interactions, workflowEventRepository: f.WorkflowEvents);

        var detail = (await query.GetDetailAsync(agent, [Roles.CsManager], ticketId)).Response!;

        Assert.Equal(0, detail.TransferCount);
        Assert.Equal(0, detail.EscalationCount);
        Assert.Equal(agent, detail.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task HandoffAccept_RepeatedAfterAPersonReassignedTheTicket_DoesNotTakeItBack()
    {
        var (f, handoffId, agent, departmentId) = await PendingHandoffAsync("conv-accept-retake");
        var other = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(other, departmentId, true, DateTime.UtcNow, null));
        var ticket = f.Tickets.All.Single();

        await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);                 // claim + take ownership: event 1
        ticket.AssignTo(other);                                                        // a supervisor deliberately moves it away
        var again = await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);     // accepting again must not silently undo that

        Assert.Equal(AgentHandoffOutcome.TicketOwnedByAnotherEmployee, again.Outcome);
        Assert.Equal(other, ticket.CurrentOwnerEmployeeId);                            // one owner, and it is not overwritten
        var started = Assert.Single(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.HandoffStarted);
        Assert.Equal(agent, started.ToEmployeeId);
        Assert.DoesNotContain(f.WorkflowEvents.All, e => e.EventType == WorkflowEventType.TicketTransferred);
    }
}
