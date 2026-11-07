using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// A Genesys transcript delivery that appends customer lines to a ticket a person is working records one internal "customer replied"
/// event — keyed on the transcript position, so the duplicate deliveries Genesys produces never notify twice — and nothing while the AI
/// is still handling the conversation.
/// </summary>
public class CustomerReplyNotificationEventTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000001";

    private static async Task<(GenesysServiceFixture F, Ticket Ticket, string Conv)> IngestAsync()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-reply", GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, f.Tickets.All.Single(), "conv-reply");
    }

    private static GenesysTicketUpdateDto Transcript(string conv, params GenesysTranscriptMessageDto[] lines) => new(conv, Transcript: lines);

    [Fact]
    public async Task CustomerLines_OnATicketAnAgentIsWorking_QueueOneReplyEventForTheOwner()
    {
        var (f, ticket, conv) = await IngestAsync();
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);
        ticket.ChangeStatus(TicketStatus.InProgress);
        var at = DateTime.UtcNow;

        var update = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv,
            new GenesysTranscriptMessageDto("Customer", at, "I am still waiting for the technician."),
            new GenesysTranscriptMessageDto("Customer", at.AddSeconds(30), "Any update?")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        var message = Assert.Single(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.CustomerReplyReceived);
        var payload = CustomerReplyEventPayload.FromJson(message.Payload)!;
        Assert.Equal(ticket.TicketId, payload.TicketId);
        Assert.Equal(agent, payload.AssignedEmployeeId);
        Assert.Equal(2, payload.MessageCount);
        Assert.DoesNotContain("technician", message.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSameTranscriptDeliveredAgain_QueuesNothingMore()
    {
        var (f, ticket, conv) = await IngestAsync();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        var line = new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow, "Hello?", ExternalMessageId: "m-1");

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv, line));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv, line));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv, line));

        Assert.Single(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.CustomerReplyReceived);
    }

    [Fact]
    public async Task ALaterCustomerLine_IsANewEvent()
    {
        var (f, ticket, conv) = await IngestAsync();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        var at = DateTime.UtcNow;

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv, new GenesysTranscriptMessageDto("Customer", at, "Hello?")));
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv,
            new GenesysTranscriptMessageDto("Customer", at, "Hello?"),
            new GenesysTranscriptMessageDto("Customer", at.AddMinutes(5), "Still there?")));

        Assert.Equal(2, f.Outbox.Committed.Count(m => m.EventType == OutboxEventTypes.CustomerReplyReceived));
    }

    [Fact]
    public async Task CustomerLines_WhileTheAiHandlesAnOpenTicket_QueueNothing()
    {
        var (f, ticket, conv) = await IngestAsync();
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv,
            new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow, "I want to sell my apartment."),
            new GenesysTranscriptMessageDto("VirtualAgent", DateTime.UtcNow.AddSeconds(5), "Are you asking about an NOC?")));

        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.CustomerReplyReceived);
    }

    [Fact]
    public async Task AgentLinesOnly_QueueNothing()
    {
        var (f, ticket, conv) = await IngestAsync();
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);

        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, Transcript(conv,
            new GenesysTranscriptMessageDto("HumanAgent", DateTime.UtcNow, "Hello, this is Sara.", SenderName: "Sara")));

        Assert.DoesNotContain(f.Outbox.Committed, m => m.EventType == OutboxEventTypes.CustomerReplyReceived);
    }
}
