using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// The staff-facing emails: "a ticket was assigned to you" and "the customer replied". Addressed from the staff member's own account,
/// never redirected by the customer test mode, idempotent, and skipped (not failed) when there is nobody to tell.
/// </summary>
public class InternalNotificationHandlerTests
{
    private static readonly Guid Agent = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Manager = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static async Task<(NotificationServiceFixture F, Ticket Ticket)> SeedAsync(string? agentEmail = "sara.ali@tigergroup.ae")
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com", "TG-CS-20261106-0200");
        f.Accounts.Add(Agent, "sara.ali", Roles.CsAgent);
        await f.Accounts.UpdateEmailAsync(Agent, agentEmail);
        f.Accounts.Add(Manager, "mona", Roles.CsManager);
        f.Employees.Add(new Employee(Agent, "Sara Ali", true, f.Time.GetUtcNow().UtcDateTime));
        f.Employees.Add(new Employee(Manager, "Mona Supervisor", true, f.Time.GetUtcNow().UtcDateTime));
        ticket.AssignTo(Agent);
        return (f, ticket);
    }

    [Fact]
    public async Task AgentAssigned_SendsToTheAgentsAccountEmail_WithTheTicketLink()
    {
        var (f, ticket) = await SeedAsync();
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);

        var result = await f.CreateAgentAssignedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("sara.ali@tigergroup.ae", email.ToAddress);
        Assert.Equal(EmailAudience.Internal, email.Audience);
        Assert.Equal("Ticket TG-CS-20261106-0200 assigned to you", email.Subject);
        Assert.Contains("Hello Sara Ali,", email.Body, StringComparison.Ordinal);
        Assert.Contains("assigned to you by Mona Supervisor", email.Body, StringComparison.Ordinal);
        Assert.Contains("Customer: Ahmed Al-Farsi", email.Body, StringComparison.Ordinal);
        Assert.Contains("Status: Open", email.Body, StringComparison.Ordinal);
        Assert.Contains($"{NotificationServiceFixture.PublicBaseUrl}/Tickets/{ticket.TicketId}", email.Body, StringComparison.Ordinal);
        Assert.Contains($"href=\"{NotificationServiceFixture.PublicBaseUrl}/Tickets/{ticket.TicketId}\"", email.HtmlBody, StringComparison.Ordinal);
        // The customer's contact details never reach a colleague's inbox through this email.
        Assert.DoesNotContain("ahmed@example.com", email.Body + email.HtmlBody, StringComparison.OrdinalIgnoreCase);

        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationType.AgentAssigned, notification.NotificationType);
        Assert.Equal(Agent, notification.RecipientEmployeeId);
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
        Assert.Contains(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationDeliverySucceeded
            && e.EntityType == NotificationAuditActions.NotificationEntityType
            && e.EntityId == ticket.TicketId.ToString()
            && e.AfterValue!.Contains("Audience=Internal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentAssigned_AfterATransfer_NamesThePreviousHandler()
    {
        var (f, ticket) = await SeedAsync();
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, previousOwnerEmployeeId: Manager, actorEmployeeId: null);

        await f.CreateAgentAssignedHandler().HandleAsync(message);

        var body = Assert.Single(f.Email.Sent).Body;
        Assert.Contains("assigned to you automatically", body, StringComparison.Ordinal);
        Assert.Contains("previously handled by Mona Supervisor", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentAssigned_InCustomerTestMode_StillGoesToTheAgent()
    {
        var (f, ticket) = await SeedAsync();
        f.ModePolicy = new CustomerNotificationModePolicy("true", "uat-inbox@tigergroup.ae", "971500000000", NotificationServiceFixture.PublicBaseUrl);
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);

        await f.CreateAgentAssignedHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("sara.ali@tigergroup.ae", email.ToAddress);
        Assert.DoesNotContain("[TEST]", email.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentAssigned_WithoutAnAccountEmail_IsSkippedWithTheReason()
    {
        var (f, ticket) = await SeedAsync(agentEmail: null);
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);

        var result = await f.CreateAgentAssignedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.NoAgentEmail}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentAssigned_ForAnUnknownAccount_IsSkippedAsAgentNotFound()
    {
        var (f, ticket) = await SeedAsync();
        var stranger = Guid.NewGuid();
        ticket.AssignTo(stranger);
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, stranger, actorEmployeeId: Manager);

        await f.CreateAgentAssignedHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.AgentNotFound}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentAssigned_WhenTheTicketMovedOnAgain_IsSkippedAsSuperseded()
    {
        var (f, ticket) = await SeedAsync();
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);
        ticket.AssignTo(Manager);

        await f.CreateAgentAssignedHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.SupersededByLaterUpdate}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentAssigned_Disabled_IsSkippedWithoutTouchingTheProvider()
    {
        var (f, ticket) = await SeedAsync();
        f.NotificationPolicy = CustomerNotificationPolicy.Disabled;
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);

        await f.CreateAgentAssignedHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.NotificationsDisabled}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentAssigned_RedeliveredMessage_SendsExactlyOnce()
    {
        var (f, ticket) = await SeedAsync();
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);
        var handler = f.CreateAgentAssignedHandler();

        await handler.HandleAsync(message);
        var again = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, again.Outcome);
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Notifications.Notifications);
    }

    [Fact]
    public async Task AgentAssigned_TransientProviderFailure_IsRetried()
    {
        var (f, ticket) = await SeedAsync();
        f.Email.ThenTransient();
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);

        var result = await f.CreateAgentAssignedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.TransientFailure, result.Outcome);
        Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
        Assert.Contains(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationDeliveryFailed);
    }

    [Fact]
    public async Task CustomerReply_SendsToTheOwner_WithTheMessageCount()
    {
        var (f, ticket) = await SeedAsync();
        ticket.ChangeStatus(TicketStatus.InProgress);
        var message = f.EnqueueCustomerReply(ticket, interactionId: 7, Agent, messageCount: 2, latestSequence: 5);

        var result = await f.CreateCustomerReplyHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("sara.ali@tigergroup.ae", email.ToAddress);
        Assert.Equal(EmailAudience.Internal, email.Audience);
        Assert.Equal("Customer replied on ticket TG-CS-20261106-0200", email.Subject);
        Assert.Contains("2 new messages", email.Body, StringComparison.Ordinal);
        Assert.Contains("Status: InProgress", email.Body, StringComparison.Ordinal);
        Assert.Contains($"/Tickets/{ticket.TicketId}", email.Body, StringComparison.Ordinal);
        Assert.Equal(NotificationType.CustomerReplyReceived, Assert.Single(f.Notifications.Notifications).NotificationType);
    }

    [Fact]
    public async Task CustomerReply_WhenTheTicketWasClosedMeanwhile_IsSkipped()
    {
        var (f, ticket) = await SeedAsync();
        ticket.ChangeStatus(TicketStatus.InProgress);
        var message = f.EnqueueCustomerReply(ticket, interactionId: 7, Agent);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();

        await f.CreateCustomerReplyHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.SupersededByClosure}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomerReply_WhenTheTicketChangedHands_IsSkipped()
    {
        var (f, ticket) = await SeedAsync();
        ticket.ChangeStatus(TicketStatus.InProgress);
        var message = f.EnqueueCustomerReply(ticket, interactionId: 7, Agent);
        ticket.AssignTo(Manager);

        await f.CreateCustomerReplyHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    [Fact]
    public async Task InternalHandlers_RouteThroughTheDispatcher_NextToTheCustomerHandlers()
    {
        var (f, ticket) = await SeedAsync();
        ticket.ChangeStatus(TicketStatus.InProgress);
        f.EnqueueAssigned(OutboxEventTypes.TicketAssignedToAgent, ticket, Agent, actorEmployeeId: Manager);
        f.EnqueueCustomerReply(ticket, interactionId: 7, Agent);
        f.EnqueueStatusChanged(ticket, TicketStatus.Open, TicketStatus.InProgress);

        var result = await f.CreateDispatcher(
            f.CreateAgentAssignedHandler(), f.CreateCustomerReplyHandler(), f.CreateInProgressHandler()).DispatchPendingAsync();

        Assert.Equal(3, result.Processed);
        Assert.Equal(0, result.DeadLettered);
        Assert.Equal(2, f.Email.Sent.Count(e => e.Audience == EmailAudience.Internal));
        Assert.Equal(1, f.Email.Sent.Count(e => e.Audience == EmailAudience.Customer));
        Assert.All(f.OutboxMessages.Messages, m => Assert.Equal(OutboxMessageStatus.Processed, m.Status));
    }
}
