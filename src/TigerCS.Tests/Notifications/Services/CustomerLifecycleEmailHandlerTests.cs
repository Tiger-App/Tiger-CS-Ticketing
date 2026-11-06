using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// The customer emails for assignment, in-progress, waiting-for-customer, cancellation and the richer closed/reopened wording — plus
/// the dispatch-time rule that a ticket which has already moved on gets the email for where it is now, not one per intermediate step.
/// </summary>
public class CustomerLifecycleEmailHandlerTests
{
    private static async Task<(NotificationServiceFixture F, Ticket Ticket, Guid Agent)> SeedAsync(string email = "ahmed@example.com")
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync(email, "TG-CS-20261106-0100");
        // Unclassified in the fake directory: the subject falls back to the request summary's first line.
        return (f, ticket, Guid.NewGuid());
    }

    // ---------------------------------------------------------------- assigned

    [Fact]
    public async Task Assigned_SendsOnce_WithNameNumberSubjectAndStatus()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssigned, ticket, agent);

        var result = await f.CreateAssignedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("ahmed@example.com", email.ToAddress);
        Assert.Equal("Your request has been assigned – Ticket TG-CS-20261106-0100", email.Subject);
        Assert.Contains("Dear Ahmed Al-Farsi,", email.Body, StringComparison.Ordinal);
        Assert.Contains("Ticket number: TG-CS-20261106-0100", email.Body, StringComparison.Ordinal);
        Assert.Contains("Status: Open", email.Body, StringComparison.Ordinal);
        Assert.Contains("Subject: AC unit not cooling.", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(agent.ToString(), email.Body + email.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EmailAudience.Customer, email.Audience);

        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationType.Assigned, notification.NotificationType);
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
    }

    [Fact]
    public async Task Assigned_WhenTheTicketHasMovedToInProgress_IsSkippedAsSuperseded()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssigned, ticket, agent);
        ticket.ChangeStatus(TicketStatus.InProgress); // the agent started work before the dispatcher ran

        var result = await f.CreateAssignedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Skipped, notification.DeliveryStatus);
        Assert.Contains(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationSkipped
            && e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.SupersededByLaterUpdate}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assigned_WhenTheOwnerChangedAgain_IsSkippedAsSuperseded()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssigned, ticket, agent);
        ticket.AssignTo(Guid.NewGuid());

        await f.CreateAssignedHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    // ---------------------------------------------------------------- in progress

    [Fact]
    public async Task InProgress_SendsOnce_AndSaysWorkHasStarted()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        ticket.ChangeStatus(TicketStatus.InProgress);
        var message = f.EnqueueStatusChanged(ticket, TicketStatus.Open, TicketStatus.InProgress);

        var result = await f.CreateInProgressHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("We are working on your request – Ticket TG-CS-20261106-0100", email.Subject);
        Assert.Contains("Work on your request has started", email.Body, StringComparison.Ordinal);
        Assert.Contains("Status: In progress", email.Body, StringComparison.Ordinal);
        Assert.Equal(NotificationType.InProgress, Assert.Single(f.Notifications.Notifications).NotificationType);
    }

    [Fact]
    public async Task InProgress_WhenTheTicketWasResolvedMeanwhile_IsSkippedAsSuperseded()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        ticket.ChangeStatus(TicketStatus.InProgress);
        var message = f.EnqueueStatusChanged(ticket, TicketStatus.Open, TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, null);

        await f.CreateInProgressHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.SupersededByLaterUpdate}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InProgress_WhenTheTicketWasClosedMeanwhile_IsSkippedAsSupersededByClosure()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        ticket.ChangeStatus(TicketStatus.InProgress);
        var message = f.EnqueueStatusChanged(ticket, TicketStatus.Open, TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();

        await f.CreateInProgressHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.SupersededByClosure}", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- waiting for customer

    [Fact]
    public async Task PendingCustomer_SendsOnce_WithoutTheAgentsReasonByDefault()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.ChangeStatus(TicketStatus.PendingCustomer);
        await f.PendingRecords.AddAsync(new TicketPendingRecord(
            ticket.TicketId, PendingKind.Customer, "Internal: waiting for the tenancy contract scan", TicketStatus.InProgress, agent,
            f.Time.GetUtcNow().UtcDateTime, Guid.NewGuid()));
        var message = f.EnqueueStatusChanged(ticket, TicketStatus.InProgress, TicketStatus.PendingCustomer);

        var result = await f.CreatePendingCustomerHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("We need your input – Ticket TG-CS-20261106-0100", email.Subject);
        Assert.Contains("Status: Waiting for your reply", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenancy contract", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NotificationType.PendingCustomer, Assert.Single(f.Notifications.Notifications).NotificationType);
    }

    [Fact]
    public async Task PendingCustomer_QuotesTheReasonOnlyWhenConfigured()
    {
        var (f, ticket, agent) = await SeedAsync();
        f.NotificationPolicy = CustomerNotificationPolicy.EnabledDefault with { IncludeResolutionNote = true };
        ticket.AssignTo(agent);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.ChangeStatus(TicketStatus.PendingCustomer);
        await f.PendingRecords.AddAsync(new TicketPendingRecord(
            ticket.TicketId, PendingKind.Customer, "Please send a copy of the tenancy contract", TicketStatus.InProgress, agent,
            f.Time.GetUtcNow().UtcDateTime, Guid.NewGuid()));
        var message = f.EnqueueStatusChanged(ticket, TicketStatus.InProgress, TicketStatus.PendingCustomer);

        await f.CreatePendingCustomerHandler().HandleAsync(message);

        Assert.Contains("What we need from you: Please send a copy of the tenancy contract", Assert.Single(f.Email.Sent).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingCustomer_WhenTheCustomerAlreadyReplied_IsSkippedAsSuperseded()
    {
        var (f, ticket, agent) = await SeedAsync();
        ticket.AssignTo(agent);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.ChangeStatus(TicketStatus.PendingCustomer);
        var message = f.EnqueueStatusChanged(ticket, TicketStatus.InProgress, TicketStatus.PendingCustomer);
        ticket.ChangeStatus(TicketStatus.InProgress);

        await f.CreatePendingCustomerHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    // ---------------------------------------------------------------- resolved / cancelled / closed

    [Fact]
    public async Task Resolved_WhenTheTicketIsAlreadyClosed_IsSkipped_AndTheClosedEmailCarriesTheOutcome()
    {
        var (f, ticket, _) = await SeedAsync();
        await f.ResolveAsync(ticket);
        var resolved = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        ticket.Close();
        var closed = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        await f.CreateResolvedHandler().HandleAsync(resolved);
        await f.CreateClosedHandler().HandleAsync(closed);

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("Your Ticket #TG-CS-20261106-0100 Has Been Closed", email.Subject);
        Assert.Contains("Status: Closed", email.Body, StringComparison.Ordinal);
        Assert.Contains("Outcome: Resolved", email.Body, StringComparison.Ordinal);
        Assert.Equal(
            [NotificationDeliveryStatus.Skipped, NotificationDeliveryStatus.Sent],
            f.Notifications.Notifications.Select(n => n.DeliveryStatus).ToArray());
        Assert.Contains(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationSkipped
            && e.AfterValue!.Contains("NotificationType=Resolved", StringComparison.Ordinal)
            && e.AfterValue.Contains($"Reason={CustomerEmailSkipReasons.SupersededByClosure}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolved_FromAnEarlierCycle_IsStillSentAfterAReopen()
    {
        var (f, ticket, _) = await SeedAsync();
        await f.ResolveAsync(ticket);
        var resolved = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        ticket.Close();
        ticket.Reopen(ticket.CurrentDepartmentId);

        await f.CreateResolvedHandler().HandleAsync(resolved);

        Assert.Single(f.Email.Sent);
    }

    [Fact]
    public async Task Resolved_WithCancelledOutcome_SendsTheCancelledEmail()
    {
        var (f, ticket, _) = await SeedAsync();
        await f.ResolveAsync(ticket, ResolutionOutcome.Cancelled, "Customer withdrew the request.");
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        var result = await f.CreateResolvedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("Your request has been cancelled – Ticket TG-CS-20261106-0100", email.Subject);
        Assert.Contains("Status: Cancelled", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("How was your experience?", email.Body, StringComparison.Ordinal);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationType.Cancelled, notification.NotificationType);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains("NotificationType=Cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closed_ForCustomerInactivity_SaysNoReplyWasReceived()
    {
        var (f, ticket, _) = await SeedAsync();
        ticket.CloseForCustomerInactivity();
        await f.Resolutions.AddAsync(TicketResolution.ForCustomerInactivity(ticket.TicketId, "No reply for 10 minutes", Guid.NewGuid(), f.Time.GetUtcNow().UtcDateTime));
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        await f.CreateClosedHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Contains("We did not receive a reply from you", email.Body, StringComparison.Ordinal);
        Assert.Contains("Outcome: Cancelled", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("No reply for 10 minutes", email.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closed_ByTheAiWithoutAResolvedEmail_CarriesTheResolvedOutcome()
    {
        var (f, ticket, _) = await SeedAsync();
        ticket.CloseForAiCompletion();
        await f.Resolutions.AddAsync(new TicketResolution(ticket.TicketId, ResolutionOutcome.Resolved, "Closed by AI", null, null, Guid.NewGuid(), f.Time.GetUtcNow().UtcDateTime));
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        await f.CreateClosedHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Contains("Outcome: Resolved", email.Body, StringComparison.Ordinal);
        Assert.Contains("Subject: AC unit not cooling.", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Closed by AI", email.Body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- reopened

    [Fact]
    public async Task Reopened_IncludesTheSubjectAndTheNewStatus()
    {
        var (f, ticket, _) = await SeedAsync();
        await f.ResolveAsync(ticket);
        ticket.Close();
        ticket.Reopen(ticket.CurrentDepartmentId);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId, reopenCount: 1);

        await f.CreateReopenedHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Contains("Subject: AC unit not cooling.", email.Body, StringComparison.Ordinal);
        Assert.Contains("Status: In progress", email.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reopened_ThenClosedAgainBeforeDelivery_IsSkippedAsSupersededByClosure()
    {
        var (f, ticket, _) = await SeedAsync();
        await f.ResolveAsync(ticket);
        ticket.Close();
        ticket.Reopen(ticket.CurrentDepartmentId);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId, reopenCount: 1);
        ticket.AssignTo(Guid.NewGuid());
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();

        await f.CreateReopenedHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.SupersededByClosure}", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- shared guarantees for every new handler

    public static TheoryData<string> NewEvents => new()
    {
        OutboxEventTypes.TicketAssigned, OutboxEventTypes.TicketInProgress, OutboxEventTypes.TicketPendingCustomer
    };

    private static (IOutboxEventHandler Handler, OutboxMessage Message) Prepare(NotificationServiceFixture f, Ticket ticket, string eventType)
    {
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);
        switch (eventType)
        {
            case OutboxEventTypes.TicketAssigned:
                return (f.CreateAssignedHandler(), f.EnqueueAssigned(eventType, ticket, agent));
            case OutboxEventTypes.TicketInProgress:
                ticket.ChangeStatus(TicketStatus.InProgress);
                return (f.CreateInProgressHandler(), f.EnqueueStatusChanged(ticket, TicketStatus.Open, TicketStatus.InProgress));
            default:
                ticket.ChangeStatus(TicketStatus.InProgress);
                ticket.ChangeStatus(TicketStatus.PendingCustomer);
                return (f.CreatePendingCustomerHandler(), f.EnqueueStatusChanged(ticket, TicketStatus.InProgress, TicketStatus.PendingCustomer));
        }
    }

    [Theory]
    [MemberData(nameof(NewEvents))]
    public async Task MissingCustomerEmail_IsSkippedWithTheReason_AndTheMessageIsProcessed(string eventType)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedUnverifiedTicketAsync();
        var (handler, message) = Prepare(f, ticket, eventType);

        var result = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Skipped, notification.DeliveryStatus);
        Assert.Contains(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationSkipped
            && e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.NoCustomerEmail}", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(NewEvents))]
    public async Task RedeliveredMessage_SendsExactlyOnce(string eventType)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        var (handler, message) = Prepare(f, ticket, eventType);

        await handler.HandleAsync(message);
        await handler.HandleAsync(message);

        Assert.Single(f.Email.Sent);
        Assert.Single(f.Notifications.Notifications);
    }

    [Theory]
    [MemberData(nameof(NewEvents))]
    public async Task Disabled_IsSkippedWithoutTouchingTheProvider(string eventType)
    {
        var f = new NotificationServiceFixture { NotificationPolicy = CustomerNotificationPolicy.Disabled };
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        var (handler, message) = Prepare(f, ticket, eventType);

        await handler.HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains($"Reason={CustomerEmailSkipReasons.NotificationsDisabled}", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(NewEvents))]
    public async Task TransientProviderFailure_IsRetriedNotDeadLettered(string eventType)
    {
        var f = new NotificationServiceFixture();
        f.Email.ThenTransient();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        var (handler, message) = Prepare(f, ticket, eventType);

        var result = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.TransientFailure, result.Outcome);
        Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
        Assert.Contains(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationDeliveryFailed);
    }

    [Fact]
    public async Task ArabicCustomer_ReceivesTheArabicWording()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync("fatima@example.com", customerName: "فاطمة المنصوري");
        var agent = Guid.NewGuid();
        ticket.AssignTo(agent);
        var message = f.EnqueueAssigned(OutboxEventTypes.TicketAssigned, ticket, agent);

        await f.CreateAssignedHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.StartsWith("تم إسناد طلبك", email.Subject, StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("عزيزي/عزيزتي فاطمة المنصوري،", email.Body, StringComparison.Ordinal);
    }
}
