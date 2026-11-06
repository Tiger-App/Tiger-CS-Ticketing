using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Notifications.Integration;

/// <summary>
/// <c>IsTestMode = true</c> through the real API host and the real dispatcher: a ticket created, resolved, closed and reopened
/// over HTTP produces all four emails — every one addressed to <c>TestEmail</c>, none to the customer.
/// </summary>
public sealed class CustomerNotificationTestModeIntegrationTests : LifecycleIntegrationTestBase, IDisposable
{
    private const string TestInbox = "uat-inbox@example.org";
    private const string Customer = "ahmed.alfarsi@example.com";

    public CustomerNotificationTestModeIntegrationTests()
        : base(new TigerCsApiFactory
        {
            ExtraConfiguration =
            {
                ["CustomerNotifications:IsTestMode"] = "true",
                ["CustomerNotifications:TestEmail"] = TestInbox
            }
        })
    {
    }

    public void Dispose() => _factory.Dispose();

    private async Task<TicketResponseDto> ResolveCloseReopenAsync()
    {
        var (ticket, _, agent, afterResolve) = await CreateAndResolveAsync();
        var afterClose = (await (await agent.PostAsJsonAsync(
                $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>())!;
        var reopen = await agent.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/reopen",
            new ReopenTicketRequestDto("Fault is back.", ticket.CurrentDepartmentId, Convert.FromBase64String(afterClose.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
        return ticket;
    }

    [Fact]
    public async Task EveryLifecycleEmail_GoesToTheTestMailboxOnly_AndIsNotRepeated()
    {
        var ticket = await ResolveCloseReopenAsync();

        var first = await _factory.RunOutboxDispatchAsync();
        await _factory.RunOutboxDispatchAsync();
        await _factory.RunOutboxDispatchAsync();

        Assert.Equal(0, first.DeadLettered);
        var mine = _factory.EmailSender.Recorded
            .Where(e => e.Audience == EmailAudience.Customer && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal)).ToList();
        Assert.Equal(4, mine.Count);
        Assert.All(mine, e => Assert.Equal(TestInbox, e.ToAddress));
        Assert.All(mine, e => Assert.StartsWith("[TEST] ", e.Subject, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.EmailSender.Recorded, e => e.ToAddress == Customer);
        // Staff mail is never redirected by the customer test mode.
        Assert.DoesNotContain(_factory.EmailSender.Recorded, e => e.Audience == EmailAudience.Internal && e.ToAddress == TestInbox);
        var notifications = await _factory.GetNotificationsAsync(ticket.TicketId);
        Assert.All(
            notifications.Where(n => n.RecipientEmployeeId is null && n.NotificationType is not (NotificationType.Assigned or NotificationType.InProgress)),
            n => Assert.Equal(NotificationDeliveryStatus.Sent, n.DeliveryStatus));
        Assert.DoesNotContain(notifications, n => n.DeliveryStatus is NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.DeadLettered);
    }

    [Fact]
    public async Task TheTestEmails_CarryTheReviewLinks_ToTheConfiguredPublicBaseUrl()
    {
        var ticket = await ResolveCloseReopenAsync();
        await _factory.RunOutboxDispatchAsync();

        var resolved = _factory.EmailSender.Recorded.Single(e => e.Subject.EndsWith("Has Been Resolved", StringComparison.Ordinal) && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));

        Assert.Matches(@"https://tickets\.example\.com/Review\?t=[A-Za-z0-9_\-]{43}&amp;r=5", resolved.HtmlBody!);
    }
}

public sealed class CustomerNotificationMisconfiguredIntegrationTests : LifecycleIntegrationTestBase, IDisposable
{
    public CustomerNotificationMisconfiguredIntegrationTests()
        : base(new TigerCsApiFactory
        {
            ExtraConfiguration =
            {
                ["CustomerNotifications:IsTestMode"] = "true",
                ["CustomerNotifications:TestEmail"] = ""
            }
        })
    {
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task TestModeWithoutATestEmail_SendsNothingToAnyone_AndTheTicketOperationsStillSucceed()
    {
        var (ticket, _, agent, afterResolve) = await CreateAndResolveAsync();
        var close = await agent.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);

        var result = await _factory.RunOutboxDispatchAsync();

        Assert.Equal(0, result.DeadLettered);
        Assert.Empty(_factory.EmailSender.Recorded);
        Assert.All(await _factory.GetNotificationsAsync(ticket.TicketId), n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
    }
}

public sealed class CustomerNotificationUnsetModeIntegrationTests : LifecycleIntegrationTestBase, IDisposable
{
    public CustomerNotificationUnsetModeIntegrationTests()
        : base(new TigerCsApiFactory { ExtraConfiguration = { ["CustomerNotifications:IsTestMode"] = null } })
    {
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AMissingSetting_MeansTestMode_NoCustomerMessageIsSent()
    {
        var (ticket, _, _, _) = await CreateAndResolveAsync();

        await _factory.RunOutboxDispatchAsync();

        Assert.DoesNotContain(_factory.EmailSender.Recorded, e => e.ToAddress == "ahmed.alfarsi@example.com");
        Assert.Empty(_factory.EmailSender.Recorded); // no TestEmail configured either
        Assert.All(await _factory.GetNotificationsAsync(ticket.TicketId), n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
    }
}

/// <summary>The anonymous review endpoints, driven the way the public page drives them, with a link taken from a real email.</summary>
public sealed class PublicTicketReviewEndpointsTests : LifecycleIntegrationTestBase, IClassFixture<TigerCsApiFactory>
{
    public PublicTicketReviewEndpointsTests(TigerCsApiFactory factory)
        : base(factory)
    {
    }

    private async Task<(TicketResponseDto Ticket, string Token, HttpClient Agent, TicketDetailDto AfterResolve)> ResolvedWithLinkAsync()
    {
        var (ticket, _, agent, afterResolve) = await CreateAndResolveAsync();
        await _factory.RunOutboxDispatchAsync();
        var email = _factory.EmailSender.Recorded.Single(e =>
            e.Subject.EndsWith("Has Been Resolved", StringComparison.Ordinal) && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
        var token = Regex.Match(email.Body, @"/Review\?t=(?<t>[A-Za-z0-9_\-]{43})&r=5").Groups["t"].Value;
        Assert.NotEmpty(token);
        return (ticket, token, agent, afterResolve);
    }

    private static Task<HttpResponseMessage> Lookup(HttpClient c, string? token) =>
        c.PostAsJsonAsync("/api/public/ticket-reviews/lookup", new ReviewTokenRequestDto(token));

    private static Task<HttpResponseMessage> Submit(HttpClient c, string? token, int rating, string? comment = null) =>
        c.PostAsJsonAsync("/api/public/ticket-reviews/submit", new SubmitTicketReviewRequestDto(token, rating, comment));

    [Fact]
    public async Task TheEndpoints_NeedNoLogin_LookupIsReadOnly_AndSubmitIsOnce()
    {
        var (ticket, token, _, _) = await ResolvedWithLinkAsync();
        using var anonymous = _factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await Lookup(anonymous, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var link = (await response.Content.ReadFromJsonAsync<ReviewLinkDto>())!;
            Assert.Equal(ReviewLinkStatus.Valid, link.Status);
            Assert.Equal(ticket.TicketNumber, link.TicketNumber);
        }

        var submitted = (await (await Submit(anonymous, token, 5, "Very helpful")).Content.ReadFromJsonAsync<SubmitReviewResultDto>())!;
        var repeated = (await (await Submit(anonymous, token, 1, "Changed")).Content.ReadFromJsonAsync<SubmitReviewResultDto>())!;

        Assert.Equal(SubmitReviewOutcome.Submitted, submitted.Outcome);
        Assert.Equal(SubmitReviewOutcome.AlreadySubmitted, repeated.Outcome);
        var after = (await (await Lookup(anonymous, token)).Content.ReadFromJsonAsync<ReviewLinkDto>())!;
        Assert.Equal(ReviewLinkStatus.AlreadySubmitted, after.Status);
    }

    [Fact]
    public async Task AnInvalidToken_IsRejected_WithoutRevealingAnything()
    {
        using var anonymous = _factory.CreateClient();

        var link = (await (await Lookup(anonymous, "not-a-real-token")).Content.ReadFromJsonAsync<ReviewLinkDto>())!;
        var submit = (await (await Submit(anonymous, new string('A', 43), 5)).Content.ReadFromJsonAsync<SubmitReviewResultDto>())!;

        Assert.Equal(ReviewLinkStatus.Invalid, link.Status);
        Assert.Null(link.TicketNumber);
        Assert.Equal(SubmitReviewOutcome.Invalid, submit.Outcome);
    }

    [Fact]
    public async Task ThereIsNoGetEndpoint_ARatingLinkOpenedByAScannerCannotCommitAReview()
    {
        var (_, token, _, _) = await ResolvedWithLinkAsync();
        using var anonymous = _factory.CreateClient();

        foreach (var path in new[] { "/api/public/ticket-reviews/submit", "/api/public/ticket-reviews/lookup", $"/api/public/ticket-reviews/{token}" })
        {
            var response = await anonymous.GetAsync($"{path}?token={token}&rating=5");
            Assert.False(response.IsSuccessStatusCode, $"{path} -> {(int)response.StatusCode}");
        }

        // Nothing was committed by any of those GETs: the link is still open for the explicit submission.
        Assert.Equal(ReviewLinkStatus.Valid, (await (await Lookup(anonymous, token)).Content.ReadFromJsonAsync<ReviewLinkDto>())!.Status);
    }

    [Fact]
    public async Task ClosingAfterAReview_DoesNotAskAgain_ButClosingWithoutOneDoes()
    {
        var (ticket, token, agent, afterResolve) = await ResolvedWithLinkAsync();
        using var anonymous = _factory.CreateClient();
        await Submit(anonymous, token, 4, "Good");

        var close = await agent.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        await _factory.RunOutboxDispatchAsync();

        var closed = _factory.EmailSender.Recorded.Single(e =>
            e.Subject.EndsWith("Has Been Closed", StringComparison.Ordinal) && e.Subject.Contains(ticket.TicketNumber, StringComparison.Ordinal));
        Assert.DoesNotContain("How was your experience?", closed.HtmlBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("/Review", closed.Body, StringComparison.Ordinal);

        // A different ticket closed with no review yet does carry the invitation.
        var (other, _, otherAgent, otherResolve) = await CreateAndResolveAsync();
        await otherAgent.PostAsJsonAsync(
            $"/api/tickets/{other.TicketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(otherResolve.RowVersion)));
        await _factory.RunOutboxDispatchAsync();
        var otherClosed = _factory.EmailSender.Recorded.Single(e =>
            e.Subject.EndsWith("Has Been Closed", StringComparison.Ordinal) && e.Subject.Contains(other.TicketNumber, StringComparison.Ordinal));
        Assert.Contains("How was your experience?", otherClosed.HtmlBody!, StringComparison.Ordinal);
    }
}
