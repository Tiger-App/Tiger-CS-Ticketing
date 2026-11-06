using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// <c>CustomerNotifications:IsTestMode</c> — on: every customer email goes to <c>TestEmail</c> and every customer SMS to
/// <c>TestPhone</c>, never to the customer; off: messages go to their intended recipients; missing/invalid: test mode. Exercised
/// at the handlers, at the customer-email boundary and at the final provider guards.
/// </summary>
public class CustomerNotificationModeTests
{
    private const string TestInbox = "uat-inbox@example.org";
    private const string TestNumber = "971500000001";
    private const string CustomerEmail = "ahmed@example.com";

    private static CustomerNotificationModePolicy Test(string? testEmail = TestInbox, string? testPhone = TestNumber) =>
        new("true", testEmail, testPhone, NotificationServiceFixture.PublicBaseUrl);

    private static NotificationServiceFixture TestModeFixture(string? testEmail = TestInbox) =>
        new() { ModePolicy = Test(testEmail) };

    private static async Task<(NotificationServiceFixture F, Ticket Ticket)> ResolvedTicketAsync(
        NotificationServiceFixture f, string contact = CustomerEmail, string number = "TG-CS-20260822-0300")
    {
        var ticket = await f.SeedVerifiedTicketAsync(contact, number);
        await f.ResolveAsync(ticket);
        return (f, ticket);
    }

    // ---------------------------------------------------------------- test mode: tickets

    [Theory]
    [InlineData("created")]
    [InlineData("resolved")]
    [InlineData("closed")]
    [InlineData("reopened")]
    public async Task TestMode_EveryLifecycleEmail_GoesToTheTestMailboxOnly(string kind)
    {
        var f = TestModeFixture();
        var ticket = await f.SeedVerifiedTicketAsync(CustomerEmail, "TG-CS-20260822-0300");
        OutboxMessage message;
        IOutboxEventHandler handler;
        switch (kind)
        {
            case "created":
                message = f.EnqueueTicketCreated(ticket.TicketId);
                handler = f.CreateHandler();
                break;
            case "resolved":
                await f.ResolveAsync(ticket);
                message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
                handler = f.CreateResolvedHandler();
                break;
            case "closed":
                await f.ResolveAsync(ticket);
                ticket.Close();
                message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);
                handler = f.CreateClosedHandler();
                break;
            default:
                await f.ResolveAsync(ticket);
                ticket.Close();
                ticket.Reopen(ticket.CurrentDepartmentId);
                message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId, ticket.ReopenCount);
                handler = f.CreateReopenedHandler();
                break;
        }

        var result = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal(TestInbox, email.ToAddress);
        Assert.StartsWith("[TEST] ", email.Subject, StringComparison.Ordinal);
        Assert.Contains("TEST NOTIFICATION", email.HtmlBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomerEmail, email.HtmlBody + email.Body + email.Subject, StringComparison.Ordinal);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
        Assert.Equal(TestInbox, notification.RecipientAddress);
    }

    [Fact]
    public async Task TestMode_Resolved_IsMarkedAsATest_WithTheReview()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);

        await f.CreateResolvedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId));

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("[TEST] Your Ticket #TG-CS-20260822-0300 Has Been Resolved", email.Subject);
        Assert.Contains("How was your experience?", email.HtmlBody!, StringComparison.Ordinal);
        Assert.True(Assert.Single(f.Reviews.Invitations).IsTest);
    }

    [Fact]
    public async Task TestMode_Closed_CarriesTheReview()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);
        ticket.Close();

        await f.CreateClosedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId));

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("[TEST] Your Ticket #TG-CS-20260822-0300 Has Been Closed", email.Subject);
        Assert.Contains("How was your experience?", email.HtmlBody!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("two@example.com;three@example.com")]
    public async Task TestMode_MissingOrInvalidTestEmail_SkipsEveryEmail_NeverFallsBackToTheCustomer(string? testEmail)
    {
        var f = TestModeFixture(testEmail);
        var ticket = await f.SeedVerifiedTicketAsync(CustomerEmail, "TG-CS-20260822-0301");
        var created = f.EnqueueTicketCreated(ticket.TicketId);
        await f.ResolveAsync(ticket);
        var resolved = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        ticket.Close();
        var closed = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        await f.CreateHandler().HandleAsync(created);
        Assert.Equal(OutboxHandlingOutcome.Succeeded, (await f.CreateResolvedHandler().HandleAsync(resolved)).Outcome);
        Assert.Equal(OutboxHandlingOutcome.Succeeded, (await f.CreateClosedHandler().HandleAsync(closed)).Outcome);

        Assert.Empty(f.Email.Sent);
        Assert.All(f.Notifications.Notifications, n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains(CustomerEmailSkipReasons.TestEmailMissing, StringComparison.Ordinal));
        Assert.Empty(f.Reviews.Invitations);
    }

    [Fact]
    public async Task TestMode_MissingTestEmail_LogsAWarning_WithoutTheCustomersAddress()
    {
        var provider = new FakeEmailSender();
        var log = new CapturingLogger<CustomerEmailSender>();
        var sender = new CustomerEmailSender(provider, CustomerNotificationPolicy.EnabledDefault, Test(testEmail: null), log);

        var result = await sender.SendAsync(Request(NotificationType.Resolved, CustomerEmail));

        Assert.Equal(CustomerEmailSendOutcome.Skipped, result.Outcome);
        Assert.Equal(CustomerEmailSkipReasons.TestEmailMissing, result.Reason);
        Assert.Empty(provider.Sent);
        var line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("TestEmail", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomerEmailAddressForLogs, line.Message, StringComparison.Ordinal);
    }

    private const string CustomerEmailAddressForLogs = "ahmed@example.com";

    [Fact]
    public async Task TestMode_StillTestsATicketWhoseCustomerHasNoEmail()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f, contact: "+971500000009");

        await f.CreateResolvedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId));

        Assert.Equal(TestInbox, Assert.Single(f.Email.Sent).ToAddress);
    }

    [Fact]
    public async Task TestMode_RedeliveredMessage_SendsOnce()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        var handler = f.CreateResolvedHandler();

        await handler.HandleAsync(message);
        await handler.HandleAsync(message);

        Assert.Single(f.Email.Sent);
        Assert.Single(f.Reviews.Invitations);
    }

    [Fact]
    public async Task TestMode_AutomaticClosure_FollowsTheSamePolicy()
    {
        // Auto-closure (inactivity, completed document delivery, AI) goes straight to Closed and enqueues the single Closed event,
        // which runs through the same dispatcher and handlers.
        var f = TestModeFixture();
        var ticket = await f.SeedGenesysTicketAsync(CustomerEmail);
        await f.ResolveAsync(ticket);
        ticket.Close();
        f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        var result = await f.CreateDispatcher(
            f.CreateHandler(), f.CreateResolvedHandler(), f.CreateClosedHandler(), f.CreateReopenedHandler()).DispatchPendingAsync();

        Assert.Equal(1, result.Processed);
        Assert.Single(f.Email.Sent);
        Assert.All(f.Email.Sent, e => Assert.Equal(TestInbox, e.ToAddress));
    }

    // ------------------------------------------------------ the switch itself

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData(" FALSE ", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("no", true)]
    [InlineData("0", true)]
    [InlineData("off", true)]
    [InlineData("production", true)]
    [InlineData("garbage", true)]
    public void OnlyAnExplicitFalse_TurnsTestModeOff(string? value, bool expectedTestMode)
    {
        var policy = new CustomerNotificationModePolicy(value, TestInbox, TestNumber, null);

        Assert.Equal(expectedTestMode, policy.IsTest);
        Assert.Equal(!expectedTestMode, policy.IsProduction);
    }

    [Fact]
    public void TheUnconfiguredPolicy_IsTestModeWithNoRecipient_SoNothingIsDelivered()
    {
        var policy = CustomerNotificationModePolicy.Unconfigured;

        Assert.True(policy.IsTest);
        Assert.False(policy.DecideEmail().Deliver);
        Assert.False(policy.DecideSms().Deliver);
        Assert.NotNull(policy.ConfigurationWarning);
    }

    [Theory]
    [InlineData("971500000001", "971500000001")]
    [InlineData("+971 50 000 0001", "971500000001")]
    [InlineData("00971500000001", "971500000001")]
    [InlineData("(971) 50-000-0001", "971500000001")]
    [InlineData("0500000001", null)]
    [InlineData("12345", null)]
    [InlineData("97150000000199999", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TestPhone_IsNormalizedToInternationalDigits_OrRejected(string? input, string? expected) =>
        Assert.Equal(expected, new CustomerNotificationModePolicy("true", null, input, null).TestPhone);

    // --------------------------------------------------------------- production

    [Fact]
    public async Task Production_Resolved_GoesToTheActualCustomer_WithTheReview_AndNoTestMarking()
    {
        var f = new NotificationServiceFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);

        await f.CreateResolvedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId));

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal(CustomerEmail, email.ToAddress);
        Assert.Equal("Your Ticket #TG-CS-20260822-0300 Has Been Resolved", email.Subject);
        Assert.DoesNotContain("TEST", email.HtmlBody!, StringComparison.Ordinal);
        Assert.Contains("How was your experience?", email.HtmlBody!, StringComparison.Ordinal);
        Assert.False(Assert.Single(f.Reviews.Invitations).IsTest);
    }

    [Fact]
    public async Task Production_Closed_GoesToTheActualCustomer_WithTheReview()
    {
        var f = new NotificationServiceFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);
        ticket.Close();

        await f.CreateClosedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId));

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal(CustomerEmail, email.ToAddress);
        Assert.Contains("How was your experience?", email.HtmlBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_CreatedAndReopened_StillGoToTheCustomer()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync(CustomerEmail);
        await f.CreateHandler().HandleAsync(f.EnqueueTicketCreated(ticket.TicketId));
        await f.ResolveAsync(ticket);
        ticket.Close();
        ticket.Reopen(ticket.CurrentDepartmentId);
        await f.CreateReopenedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId, ticket.ReopenCount));

        Assert.Equal(2, f.Email.Sent.Count);
        Assert.All(f.Email.Sent, e => Assert.Equal(CustomerEmail, e.ToAddress));
        Assert.NotNull(ticket.AcknowledgementSentAtUtc);
    }

    [Fact]
    public async Task Production_ADisabledEmailSwitch_StillSendsNothing()
    {
        var f = new NotificationServiceFixture { NotificationPolicy = CustomerNotificationPolicy.Disabled };
        var (_, ticket) = await ResolvedTicketAsync(f);

        await f.CreateResolvedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId));

        Assert.Empty(f.Email.Sent);
    }

    // ------------------------------------------- final delivery boundary (guards)

    private static EmailMessage Message(EmailAudience audience, string to = CustomerEmail) =>
        new(to, "Subject", "Body", Guid.NewGuid(), "<p>Body</p>", Audience: audience);

    private static CustomerDeliveryGuardEmailSender Guard(FakeEmailSender provider, CustomerNotificationModePolicy mode) =>
        new(provider, mode, NullLogger<CustomerDeliveryGuardEmailSender>.Instance);

    [Fact]
    public async Task Guard_TestMode_RedirectsEveryOtherCustomerEmail_ToTheTestMailbox()
    {
        // Document copies, OTP codes, Collections reminders and anything added later build an EmailMessage that defaults to the
        // Customer audience: in test mode it is re-addressed to TestEmail, attachments and all, and marked [TEST].
        var provider = new FakeEmailSender();
        var attachment = new EmailAttachment("contract.pdf", "application/pdf", [1, 2, 3]);

        var result = await Guard(provider, Test()).SendAsync(
            new EmailMessage(CustomerEmail, "Payment reminder", "Body", Guid.NewGuid(), Attachments: [attachment]));

        Assert.Equal(EmailSendOutcome.Sent, result.Outcome);
        var sent = Assert.Single(provider.Sent);
        Assert.Equal(TestInbox, sent.ToAddress);
        Assert.Equal("[TEST] Payment reminder", sent.Subject);
        Assert.Same(attachment, Assert.Single(sent.Attachments!));
    }

    [Fact]
    public async Task Guard_TestMode_DoesNotDoublePrefixTheSubject()
    {
        var provider = new FakeEmailSender();

        await Guard(provider, Test()).SendAsync(new EmailMessage(CustomerEmail, "[TEST] Already marked", "Body", Guid.NewGuid()));

        Assert.Equal("[TEST] Already marked", Assert.Single(provider.Sent).Subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public async Task Guard_TestMode_WithoutAValidTestEmail_SkipsTheMessage_NeverSendsToTheCustomer(string? testEmail)
    {
        var provider = new FakeEmailSender();
        var log = new CapturingLogger<CustomerDeliveryGuardEmailSender>();
        var guard = new CustomerDeliveryGuardEmailSender(provider, Test(testEmail), log);

        var result = await guard.SendAsync(Message(EmailAudience.Customer));

        Assert.Equal(EmailSendOutcome.Suppressed, result.Outcome);
        Assert.Empty(provider.Sent);
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Entries).Level);
    }

    [Fact]
    public async Task Guard_TestMode_PassesInternalEmailUntouched()
    {
        var provider = new FakeEmailSender();

        await Guard(provider, Test()).SendAsync(Message(EmailAudience.Internal, "manager@tigergroup.example"));

        var sent = Assert.Single(provider.Sent);
        Assert.Equal("manager@tigergroup.example", sent.ToAddress);
        Assert.Equal("Subject", sent.Subject);
    }

    [Fact]
    public async Task Guard_UnconfiguredPolicy_SkipsCustomerEmail_ButNotInternal()
    {
        var provider = new FakeEmailSender();
        var guard = Guard(provider, CustomerNotificationModePolicy.Unconfigured);

        Assert.Equal(EmailSendOutcome.Suppressed, (await guard.SendAsync(Message(EmailAudience.Customer))).Outcome);
        Assert.Equal(EmailSendOutcome.Sent, (await guard.SendAsync(Message(EmailAudience.Internal, "ops@tigergroup.example"))).Outcome);
    }

    [Fact]
    public async Task Guard_Production_SendsToTheOriginalRecipient_Unchanged()
    {
        var provider = new FakeEmailSender();

        await Guard(provider, CustomerNotificationModePolicy.ProductionDefault()).SendAsync(Message(EmailAudience.Customer));

        var sent = Assert.Single(provider.Sent);
        Assert.Equal(CustomerEmail, sent.ToAddress);
        Assert.Equal("Subject", sent.Subject);
    }

    [Fact]
    public async Task Guard_ASuppressedMessage_IsReportedAsASkip_NotAFailureThatWouldBeRetried()
    {
        var provider = new FakeEmailSender();
        var sender = new CustomerEmailSender(
            Guard(provider, Test(testEmail: null)), CustomerNotificationPolicy.EnabledDefault, CustomerNotificationModePolicy.ProductionDefault(),
            NullLogger<CustomerEmailSender>.Instance);

        // The façade says production while the guard is in test mode without a mailbox: the guard still refuses, and the
        // result is a terminal skip rather than a transient failure the dispatcher would retry.
        var result = await sender.SendAsync(Request(NotificationType.Resolved, CustomerEmail));

        Assert.Equal(CustomerEmailSendOutcome.Skipped, result.Outcome);
        Assert.Empty(provider.Sent);
    }

    // ------------------------------------------------------------------- SMS

    private static TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender FakeSms() => new(
        Microsoft.Extensions.Options.Options.Create(new TigerCS.Integrations.Modules.SmsIntegration.SmsOptions()),
        NullLogger<TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender>.Instance);

    [Fact]
    public async Task SmsGuard_TestMode_RedirectsEverySmsToTheTestPhone_NeverTheCustomer()
    {
        var inner = FakeSms();
        var guard = new CustomerDeliveryGuardSmsSender(inner, Test(), NullLogger<CustomerDeliveryGuardSmsSender>.Instance);

        var result = await guard.SendAsync(new SmsMessage("971555555555", "code 123", "en"));

        Assert.Equal(SmsSendOutcome.Accepted, result.Outcome);
        var sent = Assert.Single(inner.Sent);
        Assert.Equal(TestNumber, sent.Destination);
        Assert.Equal("code 123", sent.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    public async Task SmsGuard_TestMode_WithoutAValidTestPhone_SkipsTheSms_AndLogsAWarning(string? testPhone)
    {
        var inner = FakeSms();
        var log = new CapturingLogger<CustomerDeliveryGuardSmsSender>();
        var guard = new CustomerDeliveryGuardSmsSender(inner, Test(testPhone: testPhone), log);

        var result = await guard.SendAsync(new SmsMessage("971555555555", "code 123", "en"));

        Assert.Equal(SmsSendOutcome.Suppressed, result.Outcome);
        Assert.Empty(inner.Sent);
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Entries).Level);
        Assert.DoesNotContain("971555555555", log.Entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmsGuard_Production_SendsToTheOriginalNumber()
    {
        var inner = FakeSms();
        var guard = new CustomerDeliveryGuardSmsSender(inner, CustomerNotificationModePolicy.ProductionDefault(), NullLogger<CustomerDeliveryGuardSmsSender>.Instance);

        await guard.SendAsync(new SmsMessage("971555555555", "code 123", "en"));

        Assert.Equal("971555555555", Assert.Single(inner.Sent).Destination);
    }

    [Fact]
    public async Task SmsGuard_Unconfigured_SendsNothing()
    {
        var inner = FakeSms();
        var guard = new CustomerDeliveryGuardSmsSender(inner, CustomerNotificationModePolicy.Unconfigured, NullLogger<CustomerDeliveryGuardSmsSender>.Instance);

        Assert.Equal(SmsSendOutcome.Suppressed, (await guard.SendAsync(new SmsMessage("971555555555", "x", "en"))).Outcome);
        Assert.Empty(inner.Sent);
    }

    private static CustomerEmailRequest Request(NotificationType type, string? recipient) => new(
        type, TicketId: 7, TicketNumber: "TG-CS-20260822-0007", recipient,
        new CustomerEmailContent("Subject", "Body", "<p>Body</p>"), Guid.NewGuid());
}
