using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// What stops the First Response SLA — and what must not. Only a HUMAN reply sent to the customer
/// (a <see cref="InteractionMessageSender.HumanAgent"/> transcript line, or a person recording it) satisfies it.
/// Bot messages, customer messages, platform lines, internal notes, assignment, transfer and accepting work never do.
/// (Manual recording rules are in SlaFirstResponseAppServiceTests; accepting a handoff in AiHumanHandoffFlowTests; adding an
/// internal note in TicketNoteAppServiceTests.)
/// </summary>
public class FirstResponseTriggerTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime Start = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static GenesysTranscriptMessageDto Line(string sender, int minute, string body) => new(sender, Start.AddMinutes(minute), body, null);

    private static async Task<(GenesysServiceFixture F, Ticket Ticket)> SeedAsync(string conversationId)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var result = await f.Ingestion.IngestAsync(ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId, StartedAtUtc: Start));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, f.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId));
    }

    private static Task EndAsync(GenesysServiceFixture f, string conversationId, params GenesysTranscriptMessageDto[] transcript) =>
        f.ConversationEnd.EndAsync(ServiceAccount, new GenesysConversationEndDto(conversationId, Start.AddMinutes(30), "Completed", Transcript: transcript));

    [Fact]
    public async Task ANewTicket_HasNoFirstHumanResponse_AndItsFirstResponseClockIsRunning()
    {
        var (f, ticket) = await SeedAsync("fr-new");

        Assert.Null(ticket.FirstHumanResponseAtUtc);
        var period = Assert.Single(f.Sla.SlaInstances.All);
        Assert.Null(period.PeriodEndAtUtc);
        Assert.False(period.FirstResponseBreached);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("VirtualAgent")]
    [InlineData("System")]
    public async Task ALineFromTheCustomerTheBotOrThePlatform_DoesNotStopFirstResponse(string sender)
    {
        var (f, ticket) = await SeedAsync("fr-" + sender);

        await EndAsync(f, "fr-" + sender, Line(sender, 1, "Hello, how can I help?"), Line(sender, 2, "Anything else?"));

        Assert.Null(ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task AWholeConversationOfCustomerBotAndPlatformLines_NeverStopsIt()
    {
        var (f, ticket) = await SeedAsync("fr-mixed");

        await EndAsync(f, "fr-mixed",
            Line("Customer", 0, "I need a payment receipt."),
            Line("VirtualAgent", 1, "Sure, let me connect you."),
            Line("System", 2, "Conversation transferred to queue"),
            Line("VirtualAgent", 3, "An agent will be with you shortly."),
            Line("Customer", 4, "Hello? Anyone?"));

        Assert.Null(ticket.FirstHumanResponseAtUtc);
        Assert.False(Assert.Single(f.Sla.SlaInstances.All).FirstResponseBreached);
    }

    [Fact]
    public async Task TheFirstHumanLine_StopsIt_AtThatLinesTime_NotTheBotsOrTheCustomers()
    {
        var (f, ticket) = await SeedAsync("fr-human");

        await EndAsync(f, "fr-human",
            Line("Customer", 0, "I need a payment receipt."),
            Line("VirtualAgent", 1, "Connecting you to an agent."),
            Line("System", 2, "Agent joined"),
            Line("HumanAgent", 7, "Hello, I can help with that."),
            Line("HumanAgent", 9, "Could you confirm your unit?"));

        Assert.Equal(Start.AddMinutes(7), ticket.FirstHumanResponseAtUtc);        // the first HUMAN line, not minute 1 (bot) or 2 (platform)
        Assert.Contains(f.Audit.Entries, e => e.Action.Contains("FirstResponse", StringComparison.OrdinalIgnoreCase) || e.Action.Contains("FirstHuman", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARedeliveredTranscript_DoesNotMoveTheRecordedFirstResponse()
    {
        var (f, ticket) = await SeedAsync("fr-redeliver");
        await EndAsync(f, "fr-redeliver", Line("HumanAgent", 5, "First reply."));
        var recorded = ticket.FirstHumanResponseAtUtc;

        await EndAsync(f, "fr-redeliver", Line("HumanAgent", 5, "First reply."), Line("HumanAgent", 20, "Later reply."));

        Assert.Equal(Start.AddMinutes(5), recorded);
        Assert.Equal(recorded, ticket.FirstHumanResponseAtUtc);                   // write-once
    }

    [Fact]
    public async Task AFirstResponseThatIsLate_IsRecordedAsABreach_NotErased()
    {
        var (f, ticket) = await SeedAsync("fr-late");
        var period = Assert.Single(f.Sla.SlaInstances.All);
        var late = period.FirstResponseDueAtUtc.AddMinutes(10) - Start;

        await EndAsync(f, "fr-late", Line("HumanAgent", (int)late.TotalMinutes, "Sorry for the wait."));

        Assert.NotNull(ticket.FirstHumanResponseAtUtc);
        Assert.True(period.FirstResponseBreached);
    }

    [Fact]
    public async Task RoutingToTheResponsibleDepartmentAndAutoAssignment_DoNotStopIt()
    {
        var f = new GenesysServiceFixture();
        var (arrival, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var finance = f.Departments.AddDepartment("Finance", "FIN");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 301));
        var requestType = f.RequestTypes.Add(new RequestType(
            finance.DepartmentId, "Payment Receipt", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var employee = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employee, finance.DepartmentId, true, DateTime.UtcNow, null));
        f.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(requestType.RequestTypeId, employee));

        var result = await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            "fr-route", GenesysChannel.WebsiteChat, DepartmentId: arrival.DepartmentId, RequestType: new GenesysRequestTypeDto(requestType.RequestTypeId)));

        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(finance.DepartmentId, ticket.CurrentDepartmentId);            // transferred
        Assert.Equal(employee, ticket.CurrentOwnerEmployeeId);                     // assigned
        Assert.Null(ticket.FirstHumanResponseAtUtc);                               // neither is a reply to the customer
        Assert.False(Assert.Single(f.Sla.SlaInstances.All).FirstResponseBreached);
    }

    [Fact]
    public async Task RequestingAHumanHandoff_DoesNotStopIt()
    {
        var (f, ticket) = await SeedAsync("fr-handoff");

        var requested = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("fr-handoff"));

        Assert.NotNull(requested.TicketAgentHandoffId);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task ALateDeliveredEarlierLine_IsTheFirstResponse_NotTheOneWithTheLowerDeliverySequence()
    {
        var (f, ticket) = await SeedAsync("fr-out-of-order");

        // The minute-9 line is delivered (sequenced) before the minute-7 one, but the minute-7 line was SENT first.
        await EndAsync(f, "fr-out-of-order",
            Line("Customer", 0, "I need a receipt."),
            Line("HumanAgent", 9, "Could you confirm your unit?"),
            Line("HumanAgent", 7, "Hello, I can help."));

        Assert.Equal(Start.AddMinutes(7), ticket.FirstHumanResponseAtUtc);
    }
}
