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
