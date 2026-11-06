using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// The three lifecycle handlers — Resolved, Closed, Reopened — over the
/// shared <c>CustomerTicketEmailHandler</c> base: one email per valid
/// transition, idempotent on redelivery, skipped without an email, and
/// nothing internal in the rendered content.
/// </summary>
public class TicketLifecycleNotificationHandlerTests
{
    [Fact]
    public async Task Resolved_SendsOnceWithTheCustomerFriendlyStatus()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com", "TG-CS-20260822-0100");
        await f.ResolveAsync(ticket, ResolutionOutcome.Resolved, "Internal: replaced part #4711, invoice INV-9");
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        var result = await f.CreateResolvedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("ahmed@example.com", email.ToAddress);
        Assert.Equal("Your Ticket #TG-CS-20260822-0100 Has Been Resolved", email.Subject);
        Assert.Contains("TG-CS-20260822-0100", email.Body, StringComparison.Ordinal);
        Assert.Contains("Status: Resolved", email.Body, StringComparison.Ordinal);
        Assert.Contains("Ahmed Al-Farsi", email.Body, StringComparison.Ordinal);
        // The resolution note is internal until configuration says otherwise.
        Assert.DoesNotContain("INV-9", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("#4711", email.Body, StringComparison.Ordinal);

        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationType.Resolved, notification.NotificationType);
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
        Assert.Equal(message.OutboxMessageId, notification.OutboxMessageId);
        Assert.Contains(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationDeliverySucceeded
            && e.AfterValue!.Contains("NotificationType=Resolved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolved_QuotesTheResolutionNoteOnlyWhenConfigured()
    {
        var f = new NotificationServiceFixture
        {
            NotificationPolicy = CustomerNotificationPolicy.EnabledDefault with { IncludeResolutionNote = true }
        };
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket, ResolutionOutcome.Resolved, "The thermostat was replaced and tested.");
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        await f.CreateResolvedHandler().HandleAsync(message);

        Assert.Contains("The thermostat was replaced and tested.", Assert.Single(f.Email.Sent).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ResolutionOutcome.Cancelled, "Cancelled")]
    [InlineData(ResolutionOutcome.Rejected, "Reviewed – no further action required")]
    public async Task Resolved_DescribesNonStandardOutcomesInCustomerWording(ResolutionOutcome outcome, string expected)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket, outcome);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        await f.CreateResolvedHandler().HandleAsync(message);

        var body = Assert.Single(f.Email.Sent).Body;
        Assert.Contains($"Status: {expected}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Rejected", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closed_SendsOnce()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com", "TG-CS-20260822-0101");
        await f.ResolveAsync(ticket);
        ticket.Close();
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        var result = await f.CreateClosedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("Your Ticket #TG-CS-20260822-0101 Has Been Closed", email.Subject);
        Assert.Equal(NotificationType.Closed, Assert.Single(f.Notifications.Notifications).NotificationType);
    }

    [Fact]
    public async Task Reopened_SendsOnce()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com", "TG-CS-20260822-0102");
        await f.ResolveAsync(ticket);
        // Reopen is Closed-only under the approved rule, so the ticket has to
        // be closed before there is anything to reopen.
        ticket.Close();
        ticket.Reopen(ticket.CurrentDepartmentId);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId, reopenCount: ticket.ReopenCount);

        var result = await f.CreateReopenedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("Your request has been reopened – Ticket TG-CS-20260822-0102", email.Subject);
        Assert.Contains("working on it again", email.Body, StringComparison.Ordinal);
        Assert.Equal(NotificationType.Reopened, Assert.Single(f.Notifications.Notifications).NotificationType);
    }

    [Fact]
    public async Task RedeliveredMessage_NeverSendsTwice()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        var handler = f.CreateResolvedHandler();

        await handler.HandleAsync(message);
        var second = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, second.Outcome);
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Notifications.Notifications);
    }

    [Fact]
    public async Task ResolvedThenClosed_AreTwoDistinctNotificationsOnOneTicket()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket);
        var resolved = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        // Delivered while the ticket is still Resolved; a closure that follows later is its own notification.
        await f.CreateResolvedHandler().HandleAsync(resolved);
        ticket.Close();
        var closed = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        await f.CreateClosedHandler().HandleAsync(closed);

        Assert.Equal(2, f.Email.Sent.Count);
        Assert.Equal(
            [NotificationType.Resolved, NotificationType.Closed],
            f.Notifications.Notifications.Select(n => n.NotificationType).ToArray());
    }

    [Fact]
    public async Task NoCustomerEmail_SkipsEveryLifecycleNotification()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("+971501234567");
        await f.ResolveAsync(ticket);
        var resolved = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        ticket.Close();
        var closed = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);
        ticket.Reopen(ticket.CurrentDepartmentId);
        var reopened = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId, ticket.ReopenCount);

        var results = new[]
        {
            await f.CreateResolvedHandler().HandleAsync(resolved),
            await f.CreateClosedHandler().HandleAsync(closed),
            await f.CreateReopenedHandler().HandleAsync(reopened)
        };

        Assert.All(results, r => Assert.Equal(OutboxHandlingOutcome.Succeeded, r.Outcome));
        Assert.Empty(f.Email.Sent);
        Assert.Equal(3, f.Notifications.Notifications.Count);
        Assert.All(f.Notifications.Notifications, n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
        Assert.Equal(3, f.Audit.Entries.Count(e => e.Action == NotificationAuditActions.NotificationSkipped));
    }

    [Fact]
    public async Task Disabled_SkipsWithoutContactingTheProvider()
    {
        var f = new NotificationServiceFixture { NotificationPolicy = CustomerNotificationPolicy.Disabled };
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        var result = await f.CreateResolvedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    [Fact]
    public async Task TransientProviderFailure_StaysRetryableAndSendsOnRetry()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        f.Email.ThenTransient().ThenSent();
        var handler = f.CreateResolvedHandler();

        var first = await handler.HandleAsync(message);
        var second = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.TransientFailure, first.Outcome);
        Assert.Equal(OutboxHandlingOutcome.Succeeded, second.Outcome);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
        Assert.Equal(1, notification.RetryCount);
    }

    [Fact]
    public async Task PermanentProviderFailure_DeadLettersTheNotification()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket);
        ticket.Close();
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);
        f.Email.ThenPermanent();

        var result = await f.CreateClosedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(NotificationDeliveryStatus.DeadLettered, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    [Fact]
    public async Task Dispatcher_RoutesEachLifecycleEventToItsOwnHandler()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket);
        f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        var dispatcher = f.CreateDispatcher(
            f.CreateHandler(), f.CreateResolvedHandler(), f.CreateClosedHandler(), f.CreateReopenedHandler());
        var first = await dispatcher.DispatchPendingAsync();
        ticket.Close();
        f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);
        var second = await dispatcher.DispatchPendingAsync();

        Assert.Equal(1, first.Processed);
        Assert.Equal(1, second.Processed);
        Assert.Equal(0, first.DeadLettered + second.DeadLettered);
        Assert.Collection(
            f.Email.Sent.OrderBy(e => e.Subject),
            e => Assert.EndsWith("Has Been Closed", e.Subject, StringComparison.Ordinal),
            e => Assert.EndsWith("Has Been Resolved", e.Subject, StringComparison.Ordinal));
        Assert.All(f.OutboxMessages.Messages, m => Assert.Equal(OutboxMessageStatus.Processed, m.Status));
    }

    [Fact]
    public async Task MalformedPayload_IsPermanentAndSendsNothing()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId);
        typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.Payload))!.SetValue(message, "nope");

        var result = await f.CreateReopenedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.PermanentFailure, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Notifications.Notifications);
    }
}
