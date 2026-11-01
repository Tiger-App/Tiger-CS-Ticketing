using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The customer's "Closed" email after an automatic inactivity closure, end to end: the REAL job service and lifecycle write the
/// <c>TicketClosed</c> Outbox event, then the REAL dispatcher and <see cref="TicketClosedNotificationHandler"/> (the same handler a
/// manual close uses) deliver it through the recording email provider. Covers both rules — Website/Chatbot (10 min) and bot-prompt (5 min).
/// </summary>
public class ChatbotInactivityClosedEmailTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string CustomerEmail = "customer@example.com";

    private sealed record Harness(GenesysServiceFixture G, NotificationServiceFixture N, OutboxDispatcher Dispatcher)
    {
        public async Task<ChatbotInactivityCloseOutcome> RunJobAsync()
        {
            var outcome = ChatbotInactivityCloseOutcome.NotDue;
            foreach (var id in await G.InactivityClose.ListDueAsync())
            {
                outcome = await G.InactivityClose.TryCloseAsync(id);
            }

            return outcome;
        }

        /// <summary>Hands the Outbox rows the closure committed to the dispatcher, as the recurring Outbox job would.</summary>
        public Task<OutboxDispatchResult> DispatchAsync()
        {
            foreach (var message in G.Outbox.Committed.Where(m => !N.OutboxMessages.Messages.Contains(m)))
            {
                N.OutboxMessages.Messages.Add(message);
            }

            return Dispatcher.DispatchPendingAsync();
        }
    }

    private static async Task<Harness> CloseableTicketAsync(bool botPromptRule)
    {
        var g = new GenesysServiceFixture();
        var (department, _) = g.SeedGenesysDepartment(botPromptRule ? "Call Center" : "Customer Service", botPromptRule ? "CC" : "CS");
        var ingested = await g.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                "conv-mail", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", CustomerEmail: CustomerEmail,
                DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, ingested.Outcome);

        if (botPromptRule)
        {
            await g.TicketUpdate.UpdateAsync(
                ServiceAccount, ingested.Ticket!.TicketId, new GenesysTicketUpdateDto("conv-mail", AwaitingCustomerReply: true));
        }

        g.Clock.Advance(TimeSpan.FromMinutes(11));

        var n = new NotificationServiceFixture();
        var handler = new TicketClosedNotificationHandler(
            g.Tickets, n.Notifications, new CustomerContactResolver(n.Snapshots, g.Interactions), n.CreateCustomerEmailSender(),
            n.Audit, n.NotificationPolicy, g.Clock, n.ModePolicy, g.Resolutions, n.CreateSubjectResolver(), n.CreateInvitationService());
        n.UnitOfWork.Track(n.OutboxMessages.Messages);
        var dispatcher = new OutboxDispatcher(
            n.OutboxMessages, [handler], n.UnitOfWork, n.Audit, n.Policy, g.Clock, NullLogger<OutboxDispatcher>.Instance);
        return new Harness(g, n, dispatcher);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticClosure_SendsTheClosedEmailToTheCustomer(bool botPromptRule)
    {
        var h = await CloseableTicketAsync(botPromptRule);

        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await h.RunJobAsync());
        var dispatched = await h.DispatchAsync();

        Assert.Equal(1, dispatched.Processed);
        var mail = Assert.Single(h.N.Email.Sent);
        Assert.Equal(CustomerEmail, mail.ToAddress);
        Assert.Contains(h.N.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationDeliverySucceeded);
        Assert.Single(h.N.Notifications.Notifications, x => x.NotificationType == NotificationType.Closed && x.DeliveryStatus == NotificationDeliveryStatus.Sent);

        // The closure itself is unchanged: Cancelled outcome, audited reason, ticket Closed.
        Assert.Equal(TicketStatus.Closed, h.G.Tickets.All.Single().TicketStatus);
        Assert.Equal(ResolutionOutcome.Cancelled, Assert.Single(h.G.Resolutions.Added).ResolutionOutcome);
        Assert.Single(h.G.Audit.Entries, e => e.Action == "AutoCloseCustomerInactivity");
    }

    [Fact]
    public async Task RepeatedJobAndDispatcherRuns_SendTheClosedEmailOnce()
    {
        var h = await CloseableTicketAsync(botPromptRule: false);

        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await h.RunJobAsync());
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await h.RunJobAsync());
        await h.DispatchAsync();
        await h.DispatchAsync();
        Assert.Equal(ChatbotInactivityCloseOutcome.NotDue, await h.RunJobAsync());
        await h.DispatchAsync();

        Assert.Single(h.G.Outbox.Committed, m => m.EventType == "TicketClosed");
        Assert.Single(h.N.Email.Sent);
    }

    [Fact]
    public async Task EmailProviderFailure_DoesNotUndoTheClosure_AndIsRecordedForRetry()
    {
        var h = await CloseableTicketAsync(botPromptRule: false);
        h.N.Email.ThenTransient("Simulated SMTP outage.").ThenSent();

        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await h.RunJobAsync());
        var ticket = h.G.Tickets.All.Single();

        var first = await h.DispatchAsync();
        Assert.Equal(1, first.Retried);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.NotNull(h.G.InteractionFor("conv-mail")!.InactivityClosedAtUtc);
        Assert.Contains(h.N.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationDeliveryFailed);

        // The retry (after the backoff) delivers it, still exactly once.
        h.G.Clock.Advance(TimeSpan.FromHours(1));
        var second = await h.DispatchAsync();
        Assert.Equal(1, second.Processed);
        Assert.Equal(2, h.N.Email.SendCount);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    [Fact]
    public async Task EmailProviderThrowing_DoesNotUndoTheClosure()
    {
        var h = await CloseableTicketAsync(botPromptRule: false);
        h.N.Email.ThrowOnNextSend = new InvalidOperationException("boom");

        Assert.Equal(ChatbotInactivityCloseOutcome.Closed, await h.RunJobAsync());
        var dispatched = await h.DispatchAsync();

        Assert.Equal(1, dispatched.Retried);
        Assert.Equal(TicketStatus.Closed, h.G.Tickets.All.Single().TicketStatus);
    }
}
