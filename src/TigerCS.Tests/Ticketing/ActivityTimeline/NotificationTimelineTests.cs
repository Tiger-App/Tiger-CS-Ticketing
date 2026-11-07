using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Ticketing.ActivityTimeline;
using TigerCS.Domain.Audit;
using TigerCS.Infrastructure.Audit;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.ActivityTimeline;

/// <summary>Email notification events (queued / sent / failed / skipped) appear in the ticket Activity timeline, read from the audit trail.</summary>
public sealed class NotificationTimelineTests : IDisposable
{
    private const long TicketId = 187;
    private static readonly DateTime T0 = new(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);

    public void Dispose() => _db.Dispose();

    private static AuditEntry Notification(string action, DateTime at, string after, long ticketId = TicketId) =>
        new(null, action, NotificationAuditActions.NotificationEntityType, ticketId.ToString(), null, after, Guid.NewGuid(), at);

    [Fact]
    public void Builder_ListsEveryNotificationOutcome_InPlainWording()
    {
        var outbox = Guid.NewGuid();
        var timeline = TicketActivityTimelineBuilder.Build(new TicketTimelineSources
        {
            TicketId = TicketId,
            TicketNumber = "TG-CS-20261106-0187",
            CreatedAtUtc = T0,
            AuditEntries =
            [
                Notification(NotificationAuditActions.NotificationQueued, T0.AddMinutes(1), $"EventType=TicketResolved;TicketId={TicketId};OutboxMessageId={outbox};Status=Pending"),
                Notification(NotificationAuditActions.NotificationDeliveryFailed, T0.AddMinutes(2), $"DeliveryStatus=Failed;NotificationType=Resolved;RetryCount=1;OutboxMessageId={outbox}"),
                Notification(NotificationAuditActions.NotificationDeliverySucceeded, T0.AddMinutes(4), $"DeliveryStatus=Sent;NotificationType=Resolved;Channel=Email;OutboxMessageId={outbox}"),
                Notification(NotificationAuditActions.NotificationSkipped, T0.AddMinutes(5), $"DeliveryStatus=Skipped;NotificationType=InProgress;Reason=SupersededByClosure;OutboxMessageId={Guid.NewGuid()}"),
                Notification(NotificationAuditActions.NotificationDeliverySucceeded, T0.AddMinutes(6), $"DeliveryStatus=Sent;NotificationType=AgentAssigned;Channel=Email;Audience=Internal;RecipientEmployeeId={Guid.NewGuid()};OutboxMessageId={Guid.NewGuid()}"),
                Notification(NotificationAuditActions.NotificationDeadLettered, T0.AddMinutes(7), $"DeliveryStatus=DeadLettered;NotificationType=Closed;Reason=PermanentProviderFailure;OutboxMessageId={Guid.NewGuid()}"),
                // Another ticket's row that slipped into the same list is ignored.
                Notification(NotificationAuditActions.NotificationQueued, T0.AddMinutes(8), "EventType=TicketClosed;TicketId=999;Status=Pending")
            ]
        });

        var events = timeline.Events.Where(e => e.Category == "Notification").ToList();

        Assert.Equal(
            ["EmailQueued", "EmailFailed", "EmailSent", "EmailSkipped", "EmailSent", "EmailFailed"],
            events.Select(e => e.EventType).ToArray());
        Assert.Equal("Email queued: ticket resolved", events[0].EventName);
        Assert.Contains("customer email", events[0].Description, StringComparison.Ordinal);
        Assert.Equal("Warning", events[1].Severity);
        Assert.Equal("Email sent: ticket resolved", events[2].EventName);
        Assert.Equal("Email skipped: in progress", events[3].EventName);
        Assert.Equal("The ticket was already closed; the closure email carries the outcome.", events[3].Reason);
        Assert.Equal("Email sent: assigned to agent", events[4].EventName);
        Assert.Contains("staff email", events[4].Description, StringComparison.Ordinal);
        Assert.Equal("The email provider rejected the message.", events[5].Reason);
        Assert.All(events, e => Assert.Equal(TimelineActorTypes.System, e.ActorType));
        Assert.All(events, e => Assert.Equal("AuditEntries", e.Source));
        Assert.DoesNotContain(events, e => e.Description.Contains("999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuditReader_ReturnsTheTicketsNotificationRows_AndNotAnotherTickets()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            ticketId = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            var other = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            context.AuditEntries.AddRange(
                Notification(NotificationAuditActions.NotificationQueued, T0.AddMinutes(1), $"EventType=TicketCreated;TicketId={ticketId};Status=Pending", ticketId),
                Notification(NotificationAuditActions.NotificationDeliverySucceeded, T0.AddMinutes(2), "DeliveryStatus=Sent;NotificationType=Acknowledgement", ticketId),
                Notification(NotificationAuditActions.NotificationDeliverySucceeded, T0.AddMinutes(3), "DeliveryStatus=Sent;NotificationType=Acknowledgement", other),
                // The dispatcher's own entries are keyed by the Outbox message, not the ticket: not part of the ticket's timeline.
                new AuditEntry(null, NotificationAuditActions.NotificationRetryScheduled, NotificationAuditActions.OutboxMessageEntityType, Guid.NewGuid().ToString(), null, "Attempts=1", Guid.NewGuid(), T0.AddMinutes(4)));
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var entries = await new AuditEntryReader(read).ListForTicketAsync(ticketId, [], TimelineAuditActions.All);

        Assert.Equal(
            [NotificationAuditActions.NotificationQueued, NotificationAuditActions.NotificationDeliverySucceeded],
            entries.Select(e => e.Action).ToArray());
        Assert.All(entries, e => Assert.Equal(ticketId.ToString(), e.EntityId));
    }
}
