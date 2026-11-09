using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// Which address a ticket's emails go to: the CRM contact, the Genesys email, the external (PACT) snapshot, then the email an agent typed
/// in for a manually entered customer — first valid one wins — and that every lifecycle notification uses that one answer.
/// </summary>
public class CustomerEmailResolutionTests
{
    // ---------------------------------------------------------------- manual entry

    [Theory]
    [InlineData("+971 50 777 0001")]
    [InlineData("971507770001")]
    [InlineData("+971-50-777-0001")]
    [InlineData("(971) 50 777 0001")]
    public async Task ManualCustomer_EmailSavedForThePhone_IsFoundWhateverTheFormatting(string savedAs)
    {
        var f = new NotificationServiceFixture();
        // The ticket's originating interaction carries the number as typed; the saved contact may have been entered in any format.
        var ticket = await f.SeedManualCustomerTicketAsync(phone: "+971 50 777 0001", savedEmail: null);
        f.ContactDetails.Save(savedAs, "Manual Customer", "manual.customer@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("manual.customer@example.com", contact.EmailAddress);
        Assert.True(contact.IsDeliverable);
        Assert.Null(contact.SkipReason);
    }

    [Fact]
    public async Task ManualCustomer_WithoutASavedEmail_IsNotDeliverable_WithNoCustomerEmail()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync(savedEmail: null);

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.False(contact.IsDeliverable);
        Assert.Equal(CustomerEmailSkipReasons.NoCustomerEmail, contact.SkipReason);
    }

    [Fact]
    public async Task ManualCustomer_WithAnInvalidSavedEmail_IsNotDeliverable_WithInvalidCustomerEmail()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync(savedEmail: "not-an-email");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.False(contact.IsDeliverable);
        Assert.Equal(CustomerEmailSkipReasons.InvalidCustomerEmail, contact.SkipReason);
    }

    [Fact]
    public async Task AnotherCustomersSavedEmail_IsNeverUsed()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync(phone: "+971507770001", savedEmail: null);
        f.ContactDetails.Save("+971507770002", "Someone Else", "someone.else@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Null(contact.EmailAddress);
        Assert.Equal(CustomerEmailSkipReasons.NoCustomerEmail, contact.SkipReason);
    }

    [Fact]
    public async Task ManualCustomer_TicketWithoutAnyPhone_IsNotDeliverable_AndNeverThrows()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedUnverifiedTicketAsync();
        f.ContactDetails.Save("+971507770001", "Manual Customer", "manual.customer@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.False(contact.IsDeliverable);
        Assert.Equal(CustomerEmailSkipReasons.NoCustomerEmail, contact.SkipReason);
    }

    [Fact]
    public async Task ManualCustomer_AlsoUsesTheLatestSavedEmail_WhenAnAgentCorrectsIt()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync(savedEmail: "old@example.com");
        f.ContactDetails.Save("+971 50 777 0001", "Manual Customer", "corrected@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("corrected@example.com", contact.EmailAddress);
    }

    // ---------------------------------------------------------------- CRM / Genesys / external, and priority

    [Fact]
    public async Task ExternalCustomer_EmailSnapshottedOnTheTicket_IsUsed()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedExternalCustomerTicketAsync("omar.saleh@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("omar.saleh@example.com", contact.EmailAddress);
        Assert.Equal("Omar Saleh", ticket.ExternalCustomerName);
    }

    [Fact]
    public async Task ExternalCustomer_WithAnInvalidSnapshotEmail_IsNotDeliverable()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedExternalCustomerTicketAsync("n/a");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.False(contact.IsDeliverable);
        Assert.Equal(CustomerEmailSkipReasons.InvalidCustomerEmail, contact.SkipReason);
    }

    [Fact]
    public async Task CrmContact_Email_StillWinsOverEveryNewSource()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("crm.contact@example.com");
        f.ContactDetails.Save("+971507770001", "Manual", "manual@example.com");
        await f.Interactions.AddAsync(TicketInteraction.CreateLocal(ticket.TicketId, WellKnownChannels.Phone, "+971507770001", f.Time.GetUtcNow().UtcDateTime, true));

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("crm.contact@example.com", contact.EmailAddress);
    }

    [Fact]
    public async Task GenesysEmail_StillWinsOverTheExternalAndManualSources()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync("genesys@example.com");
        f.ContactDetails.Save("+971500000001", "Manual", "manual@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("genesys@example.com", contact.EmailAddress);
    }

    [Fact]
    public async Task GenesysTicketWithoutAnEmail_FallsBackToTheEmailAnAgentSavedForThePhone()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync(customerEmail: null);
        f.ContactDetails.Save("+971500000001", "Fatima", "agent.saved@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("agent.saved@example.com", contact.EmailAddress);
    }

    [Fact]
    public async Task AnInvalidHigherSource_IsSkipped_AndTheNextValidOneIsUsed()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync("not-an-email");
        f.ContactDetails.Save("+971500000001", "Fatima", "agent.saved@example.com");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("agent.saved@example.com", contact.EmailAddress);
    }

    [Fact]
    public async Task Resolver_WithoutAContactDetailStore_BehavesAsBefore()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync();

        var contact = await new CustomerContactResolver(f.Snapshots, f.Interactions).ResolveAsync(ticket);

        Assert.Equal(CustomerEmailSkipReasons.NoCustomerEmail, contact.SkipReason);
    }

    // ---------------------------------------------------------------- every lifecycle notification uses the corrected resolver

    private static (IOutboxEventHandler Handler, OutboxMessage Message) Prepare(NotificationServiceFixture f, Ticket ticket, string eventType)
    {
        var agent = Guid.NewGuid();
        switch (eventType)
        {
            case OutboxEventTypes.TicketCreated:
                return (f.CreateHandler(), f.EnqueueTicketCreated(ticket.TicketId));
            case OutboxEventTypes.TicketAssigned:
                ticket.AssignTo(agent);
                return (f.CreateAssignedHandler(), f.EnqueueAssigned(eventType, ticket, agent));
            case OutboxEventTypes.TicketInProgress:
                ticket.AssignTo(agent);
                ticket.ChangeStatus(TicketStatus.InProgress);
                return (f.CreateInProgressHandler(), f.EnqueueStatusChanged(ticket, TicketStatus.Open, TicketStatus.InProgress));
            case OutboxEventTypes.TicketPendingCustomer:
                ticket.AssignTo(agent);
                ticket.ChangeStatus(TicketStatus.InProgress);
                ticket.ChangeStatus(TicketStatus.PendingCustomer);
                return (f.CreatePendingCustomerHandler(), f.EnqueueStatusChanged(ticket, TicketStatus.InProgress, TicketStatus.PendingCustomer));
            case OutboxEventTypes.TicketResolved:
                f.ResolveAsync(ticket).GetAwaiter().GetResult();
                return (f.CreateResolvedHandler(), f.EnqueueLifecycleEvent(eventType, ticket.TicketId));
            case OutboxEventTypes.TicketClosed:
                f.ResolveAsync(ticket).GetAwaiter().GetResult();
                ticket.Close();
                return (f.CreateClosedHandler(), f.EnqueueLifecycleEvent(eventType, ticket.TicketId));
            default:
                f.ResolveAsync(ticket).GetAwaiter().GetResult();
                ticket.Close();
                ticket.Reopen(ticket.CurrentDepartmentId);
                return (f.CreateReopenedHandler(), f.EnqueueLifecycleEvent(eventType, ticket.TicketId, ticket.ReopenCount));
        }
    }

    public static TheoryData<string> CustomerEvents => new()
    {
        OutboxEventTypes.TicketCreated, OutboxEventTypes.TicketAssigned, OutboxEventTypes.TicketInProgress,
        OutboxEventTypes.TicketPendingCustomer, OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketClosed, OutboxEventTypes.TicketReopened
    };

    [Theory]
    [MemberData(nameof(CustomerEvents))]
    public async Task EveryLifecycleEmail_ForAManualCustomer_GoesToTheEmailSavedForThePhone(string eventType)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync();
        var (handler, message) = Prepare(f, ticket, eventType);

        var result = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("manual.customer@example.com", email.ToAddress);
        Assert.Contains("TG-HO-20261109-0001", email.Subject + email.Body, StringComparison.Ordinal);
        Assert.Contains("Dear Manual Customer,", email.Body, StringComparison.Ordinal);
        Assert.Equal(NotificationDeliveryStatus.Sent, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    [Theory]
    [MemberData(nameof(CustomerEvents))]
    public async Task EveryLifecycleEmail_ForAManualCustomerWithoutAnEmail_IsRecordedAsSkippedNoCustomerEmail(string eventType)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync(savedEmail: null);
        var (handler, message) = Prepare(f, ticket, eventType);

        var result = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
        var skipped = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationSkipped);
        Assert.Equal(ticket.TicketId.ToString(), skipped.EntityId);
        Assert.Contains($"Reason={CustomerEmailSkipReasons.NoCustomerEmail}", skipped.AfterValue, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CustomerEvents))]
    public async Task EveryLifecycleEmail_ForAnExternalCustomer_GoesToTheSnapshottedEmail(string eventType)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedExternalCustomerTicketAsync("omar.saleh@example.com");
        var (handler, message) = Prepare(f, ticket, eventType);

        await handler.HandleAsync(message);

        Assert.Equal("omar.saleh@example.com", Assert.Single(f.Email.Sent).ToAddress);
    }

    [Theory]
    [MemberData(nameof(CustomerEvents))]
    public async Task EveryLifecycleEmail_ForAGenesysCustomer_GoesToTheGenesysEmail(string eventType)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync("fatima@example.com");
        var (handler, message) = Prepare(f, ticket, eventType);

        await handler.HandleAsync(message);

        Assert.Equal("fatima@example.com", Assert.Single(f.Email.Sent).ToAddress);
    }

    // ---------------------------------------------------------------- duplicate prevention

    [Fact]
    public async Task ManualCustomer_RedeliveredMessage_SendsExactlyOnce_ThroughTheDispatcher()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync();
        f.EnqueueTicketCreated(ticket.TicketId);
        var dispatcher = f.CreateDispatcher(f.CreateHandler());

        await dispatcher.DispatchPendingAsync();
        await dispatcher.DispatchPendingAsync();
        await dispatcher.DispatchPendingAsync();

        Assert.Single(f.Email.Sent);
        Assert.Single(f.Notifications.Notifications);
    }

    [Fact]
    public async Task AnEmailAddedAfterASkippedNotification_DoesNotResendTheSkippedOne()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync(savedEmail: null);
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        var handler = f.CreateHandler();
        await handler.HandleAsync(message);

        // An agent now saves the customer's email; the redelivered, already-skipped event must stay skipped (terminal, never sent later).
        f.ContactDetails.Save("+971 50 777 0001", "Manual Customer", "manual.customer@example.com");
        await handler.HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }
}
