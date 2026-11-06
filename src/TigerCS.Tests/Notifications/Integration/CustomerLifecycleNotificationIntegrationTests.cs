using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Notifications.Integration;

/// <summary>
/// The customer email pipeline end to end through the real API host:
/// resolve → close → reopen over HTTP, each committing its Outbox event
/// with the state change, then the real dispatcher routing each event to
/// the registered handler and the in-memory recording adapter receiving
/// exactly one customer email per transition. Also the regression check
/// that the three lifecycle endpoints' contracts are unchanged.
/// </summary>
public class CustomerLifecycleNotificationIntegrationTests : LifecycleIntegrationTestBase, IClassFixture<TigerCsApiFactory>
{
    public CustomerLifecycleNotificationIntegrationTests(TigerCsApiFactory factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task ResolvingATicket_CommitsAPendingTicketResolvedEventAndSendsNothingInline()
    {
        var (ticket, _, _, _) = await CreateAndResolveAsync();

        var message = await OutboxMessageFor(ticket.TicketId, OutboxEventTypes.TicketResolved);

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.DoesNotContain(
            _factory.EmailSender.Recorded,
            e => e.Subject.EndsWith("Has Been Resolved", StringComparison.Ordinal)
                && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveCloseReopen_EachDeliversExactlyOneCustomerEmailThroughTheDispatcher()
    {
        var (ticket, _, agent, afterResolve) = await CreateAndResolveAsync();

        var closeResponse = await agent.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        var afterClose = (await closeResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;

        var reopenResponse = await agent.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/reopen",
            new ReopenTicketRequestDto(
                "Customer reports the fault is back.", ticket.CurrentDepartmentId, Convert.FromBase64String(afterClose.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, reopenResponse.StatusCode);

        var result = await _factory.RunOutboxDispatchAsync();
        await _factory.RunOutboxDispatchAsync(); // a second pass must change nothing

        Assert.Equal(0, result.DeadLettered);
        Assert.Equal(0, result.Failed);

        // Customer mail only: the worker also receives the staff "assigned to you" email, which is not the customer's story.
        var mine = _factory.EmailSender.Recorded
            .Where(e => e.Audience == EmailAudience.Customer && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal))
            .Select(e => e.Subject)
            .OrderBy(s => s)
            .ToList();
        Assert.Equal(
            [
                $"Your request has been received – Ticket {ticket.TicketNumber}",
                $"Your request has been reopened – Ticket {ticket.TicketNumber}",
                $"Your Ticket #{ticket.TicketNumber} Has Been Closed",
                $"Your Ticket #{ticket.TicketNumber} Has Been Resolved"
            ],
            mine);
        Assert.All(
            _factory.EmailSender.Recorded.Where(e => e.Audience == EmailAudience.Customer && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal)),
            e => Assert.Equal("ahmed.alfarsi@example.com", e.ToAddress));

        var notifications = await _factory.GetNotificationsAsync(ticket.TicketId);
        Assert.Equal(
            [NotificationType.Acknowledgement, NotificationType.Resolved, NotificationType.Closed, NotificationType.Reopened],
            notifications.Where(n => n.DeliveryStatus == NotificationDeliveryStatus.Sent && n.RecipientEmployeeId is null)
                .OrderBy(n => n.NotificationType).Select(n => n.NotificationType).ToArray());
        // The assignment and the start of work happened within the same dispatch window as the resolution: coalesced into the
        // emails that describe where the ticket ended up, and recorded as skipped rather than silently dropped.
        Assert.All(
            notifications.Where(n => n.NotificationType is NotificationType.Assigned or NotificationType.InProgress),
            n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.Assigned);
        Assert.Contains(notifications, n => n.NotificationType == NotificationType.InProgress);
        Assert.DoesNotContain(notifications, n => n.DeliveryStatus is NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.DeadLettered);

        foreach (var eventType in new[] { OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketClosed, OutboxEventTypes.TicketReopened })
        {
            var message = await OutboxMessageFor(ticket.TicketId, eventType);
            Assert.Equal(OutboxMessageStatus.Processed, message.Status);
            Assert.Equal(1, message.Attempts);
        }

        Assert.Contains(await _factory.GetNotificationAuditAsync(), a =>
            a.Action == NotificationAuditActions.NotificationDeliverySucceeded
            && a.AfterValue!.Contains("NotificationType=Reopened", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneOnlyCustomer_LifecycleNotificationsAreSkippedAndTheOperationStillSucceeds()
    {
        var (ticket, _, _, _) = await CreateAndResolveAsync(contactIndex: 1);

        var result = await _factory.RunOutboxDispatchAsync();

        Assert.Equal(0, result.DeadLettered);
        Assert.DoesNotContain(_factory.EmailSender.Recorded, e => e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
        var resolved = Assert.Single(await _factory.GetNotificationsAsync(ticket.TicketId), n => n.NotificationType == NotificationType.Resolved);
        Assert.Equal(NotificationDeliveryStatus.Skipped, resolved.DeliveryStatus);
        Assert.Equal(OutboxMessageStatus.Processed, (await OutboxMessageFor(ticket.TicketId, OutboxEventTypes.TicketResolved)).Status);
    }
}
