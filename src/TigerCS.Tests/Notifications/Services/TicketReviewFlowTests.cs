using System.Security.Cryptography;
using System.Text;
using TigerCS.Application.Modules.CustomerReviews;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// The customer satisfaction review: link generation, token security (random, hashed, bound to the ticket, expiring), the
/// read-only look at a link versus the explicit submission, duplicate protection, no second request after a review, and the
/// separation of test reviews from production figures.
/// </summary>
public class TicketReviewFlowTests
{
    private const string Base = NotificationServiceFixture.PublicBaseUrl;

    private static async Task<(NotificationServiceFixture F, Ticket Ticket)> ResolvedAsync(
        NotificationServiceFixture? fixture = null, string number = "TG-CS-20260822-0400")
    {
        var f = fixture ?? new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com", number);
        await f.ResolveAsync(ticket);
        return (f, ticket);
    }

    private static string TokenFrom(string url) => Uri.UnescapeDataString(url.Split("t=")[1].Split('&')[0]);

    // ------------------------------------------------------------------ links

    [Fact]
    public async Task Links_UseTheConfiguredPublicBaseUrl_FiveStars_AndCarryOnlyTheOpaqueToken()
    {
        var (f, ticket) = await ResolvedAsync();

        var links = await f.CreateInvitationService().PrepareAsync(ticket);

        Assert.NotNull(links);
        var token = TokenFrom(links.PageUrl);
        Assert.Equal($"{Base}/Review?t={token}", links.PageUrl);
        Assert.Equal(5, links.StarUrls.Count);
        for (var rating = 1; rating <= 5; rating++)
        {
            Assert.Equal($"{Base}/Review?t={token}&r={rating}", links.StarUrls[rating - 1]);
        }

        // Nothing identifying is in the URL: not the ticket number, id, email, or name.
        foreach (var url in links.StarUrls.Append(links.PageUrl))
        {
            Assert.DoesNotContain("0400", url, StringComparison.Ordinal);
            Assert.DoesNotContain("ahmed", url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"={ticket.TicketId}&", url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BaseUrlWithTrailingSlashOrPath_IsNormalized_AndABadOneYieldsNoLinks()
    {
        Assert.Equal("https://tickets.example.com/Review?t=abc", new CustomerNotificationModePolicy("false", null, null, "https://tickets.example.com/").BuildReviewUrl("abc"));
        Assert.Equal("https://x.example.com/ts/Review?t=abc&r=4", new CustomerNotificationModePolicy("false", null, null, "https://x.example.com/ts/").BuildReviewUrl("abc", 4));
        foreach (var bad in new[] { null, "", "tickets.example.com", "ftp://x.example.com", "javascript:alert(1)", "https://x.example.com/?q=1" })
        {
            Assert.Null(new CustomerNotificationModePolicy("false", null, null, bad).PublicBaseUrl);
        }

        var f = new NotificationServiceFixture { ModePolicy = new CustomerNotificationModePolicy("false", null, null, null) };
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        await f.ResolveAsync(ticket);
        Assert.Null(await f.CreateInvitationService().PrepareAsync(ticket));
        Assert.Empty(f.Reviews.Invitations);
    }

    [Fact]
    public async Task Tokens_AreRandomExpiring_AndOnlyTheirHashIsStored()
    {
        var (f, ticket) = await ResolvedAsync();
        var service = f.CreateInvitationService();

        var first = await service.PrepareAsync(ticket);
        var second = await service.PrepareAsync(ticket);

        var t1 = TokenFrom(first!.PageUrl);
        var t2 = TokenFrom(second!.PageUrl);
        Assert.NotEqual(t1, t2);
        Assert.Equal(ReviewToken.EncodedLength, t1.Length);
        Assert.True(ReviewToken.IsWellFormed(t1));

        var stored = f.Reviews.Invitations[0];
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(t1)), stored.TokenHash);
        Assert.DoesNotContain(t1, Convert.ToBase64String(stored.TokenHash), StringComparison.Ordinal);
        Assert.Equal(ticket.TicketId, stored.TicketId);
        Assert.Equal(f.Time.GetUtcNow().UtcDateTime + CustomerNotificationModePolicy.DefaultReviewLinkLifetime, stored.ExpiresAtUtc);
        Assert.Equal(stored.ExpiresAtUtc, first.ExpiresAtUtc);
    }

    // ------------------------------------------------- server-side validation

    [Fact]
    public async Task ValidToken_ShowsTheForm_AndLookingAtItNeverSavesAReview()
    {
        var (f, ticket) = await ResolvedAsync();
        var links = (await f.CreateInvitationService().PrepareAsync(ticket))!;
        var service = f.CreateReviewService();

        // An email scanner / link preview opens the star link (and the page) repeatedly.
        for (var i = 0; i < 5; i++)
        {
            var link = await service.GetLinkAsync(TokenFrom(links.StarUrls[4]));
            Assert.Equal(ReviewLinkStatus.Valid, link.Status);
            Assert.Equal("TG-CS-20260822-0400", link.TicketNumber);
        }

        Assert.Empty(f.Reviews.Reviews);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("a-token-with-the-right-length-but-never-issued-1")]
    [InlineData("../../etc/passwd")]
    [InlineData("<script>alert(1)</script>")]
    public async Task InvalidTokens_AreRejected_RevealingNothing(string? token)
    {
        var (f, _) = await ResolvedAsync();
        var service = f.CreateReviewService();

        var link = await service.GetLinkAsync(token);
        var submit = await service.SubmitAsync(token, new SubmitReviewRequestDto(5, "great"));

        Assert.Equal(ReviewLinkStatus.Invalid, link.Status);
        Assert.Null(link.TicketNumber);
        Assert.Equal(SubmitReviewOutcome.Invalid, submit.Outcome);
        Assert.Null(submit.TicketNumber);
        Assert.Empty(f.Reviews.Reviews);
    }

    [Fact]
    public async Task ATamperedToken_IsRejected_AtEveryPosition()
    {
        var (f, ticket) = await ResolvedAsync();
        var token = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        var service = f.CreateReviewService();

        for (var i = 0; i < token.Length; i += 7)
        {
            var chars = token.ToCharArray();
            chars[i] = chars[i] == 'A' ? 'B' : 'A';
            Assert.Equal(ReviewLinkStatus.Invalid, (await service.GetLinkAsync(new string(chars))).Status);
        }

        Assert.Equal(ReviewLinkStatus.Invalid, (await service.GetLinkAsync(token + "x")).Status);
        Assert.Equal(ReviewLinkStatus.Invalid, (await service.GetLinkAsync(token[..^1])).Status);
        Assert.Equal(ReviewLinkStatus.Valid, (await service.GetLinkAsync(token)).Status);
    }

    [Fact]
    public async Task AnExpiredToken_IsRejected_ForBothTheLinkAndTheSubmission()
    {
        var (f, ticket) = await ResolvedAsync();
        var token = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        var service = f.CreateReviewService();

        f.Time.Advance(CustomerNotificationModePolicy.DefaultReviewLinkLifetime - TimeSpan.FromMinutes(1));
        Assert.Equal(ReviewLinkStatus.Valid, (await service.GetLinkAsync(token)).Status);

        f.Time.Advance(TimeSpan.FromMinutes(1));
        var link = await service.GetLinkAsync(token);
        Assert.Equal(ReviewLinkStatus.Expired, link.Status);
        Assert.Null(link.TicketNumber);
        Assert.Equal(SubmitReviewOutcome.Expired, (await service.SubmitAsync(token, new SubmitReviewRequestDto(5, null))).Outcome);
        Assert.Empty(f.Reviews.Reviews);
    }

    [Fact]
    public async Task ATokenIsBoundToItsOwnTicket()
    {
        var (f, ticketA) = await ResolvedAsync(number: "TG-CS-20260822-0401");
        var ticketB = await f.SeedVerifiedTicketAsync("other@example.com", "TG-CS-20260822-0402");
        await f.ResolveAsync(ticketB);
        var tokenA = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticketA))!.PageUrl);

        var result = await f.CreateReviewService().SubmitAsync(tokenA, new SubmitReviewRequestDto(4, "ok"));

        Assert.Equal(SubmitReviewOutcome.Submitted, result.Outcome);
        Assert.Equal("TG-CS-20260822-0401", result.TicketNumber);
        var review = Assert.Single(f.Reviews.Reviews);
        Assert.Equal(ticketA.TicketId, review.TicketId);
        Assert.NotEqual(ticketB.TicketId, review.TicketId);
    }

    // ------------------------------------------------------------ submission

    [Fact]
    public async Task Submission_StoresRatingCommentAndTimestamp()
    {
        var (f, ticket) = await ResolvedAsync();
        var token = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);

        var result = await f.CreateReviewService().SubmitAsync(token, new SubmitReviewRequestDto(4, "  Quick and polite.  "));

        Assert.Equal(SubmitReviewOutcome.Submitted, result.Outcome);
        var review = Assert.Single(f.Reviews.Reviews);
        Assert.Equal(4, review.Rating);
        Assert.Equal("Quick and polite.", review.Comment);
        Assert.Equal(f.Time.GetUtcNow().UtcDateTime, review.SubmittedAtUtc);
        Assert.False(review.IsTest);
    }

    [Fact]
    public async Task TheCommentIsOptional()
    {
        var (f, ticket) = await ResolvedAsync();
        var token = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);

        var result = await f.CreateReviewService().SubmitAsync(token, new SubmitReviewRequestDto(5, "   "));

        Assert.Equal(SubmitReviewOutcome.Submitted, result.Outcome);
        Assert.Null(Assert.Single(f.Reviews.Reviews).Comment);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(100)]
    public async Task ARatingOutsideOneToFive_IsRejected_AndTheLinkStaysUsable(int rating)
    {
        var (f, ticket) = await ResolvedAsync();
        var token = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        var service = f.CreateReviewService();

        Assert.Equal(SubmitReviewOutcome.InvalidRating, (await service.SubmitAsync(token, new SubmitReviewRequestDto(rating, null))).Outcome);

        Assert.Empty(f.Reviews.Reviews);
        Assert.Equal(SubmitReviewOutcome.Submitted, (await service.SubmitAsync(token, new SubmitReviewRequestDto(3, null))).Outcome);
    }

    [Fact]
    public async Task AnOverlongComment_IsRejected_AndControlCharactersAreStripped()
    {
        var (f, ticket) = await ResolvedAsync();
        var token = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        var service = f.CreateReviewService();

        Assert.Equal(SubmitReviewOutcome.InvalidComment, (await service.SubmitAsync(token, new SubmitReviewRequestDto(5, new string('x', 1001)))).Outcome);
        Assert.Empty(f.Reviews.Reviews);

        await service.SubmitAsync(token, new SubmitReviewRequestDto(5, "line one\r\nline\u0000 two\u0007"));
        Assert.Equal("line one\nline two", Assert.Single(f.Reviews.Reviews).Comment);
    }

    [Fact]
    public async Task ADuplicateSubmission_IsRefused_AndTheFirstReviewIsPreserved()
    {
        var (f, ticket) = await ResolvedAsync();
        var token = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        var service = f.CreateReviewService();

        var first = await service.SubmitAsync(token, new SubmitReviewRequestDto(5, "Excellent"));
        var second = await service.SubmitAsync(token, new SubmitReviewRequestDto(1, "Changed my mind"));

        Assert.Equal(SubmitReviewOutcome.Submitted, first.Outcome);
        Assert.Equal(SubmitReviewOutcome.AlreadySubmitted, second.Outcome);
        var review = Assert.Single(f.Reviews.Reviews);
        Assert.Equal(5, review.Rating);
        Assert.Equal("Excellent", review.Comment);
        Assert.Equal(ReviewLinkStatus.AlreadySubmitted, (await service.GetLinkAsync(token)).Status);
    }

    [Fact]
    public async Task ASecondLinkForTheSameTicket_CannotAddAnotherReview()
    {
        // The Resolved and the Closed email each carry their own link; only one review can ever exist.
        var (f, ticket) = await ResolvedAsync();
        var resolvedToken = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        var closedToken = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        var service = f.CreateReviewService();

        await service.SubmitAsync(resolvedToken, new SubmitReviewRequestDto(5, null));
        var again = await service.SubmitAsync(closedToken, new SubmitReviewRequestDto(2, "no"));

        Assert.Equal(SubmitReviewOutcome.AlreadySubmitted, again.Outcome);
        Assert.Single(f.Reviews.Reviews);
    }

    [Fact]
    public async Task AReopenedTicket_CanBeReviewedAgainForItsNewCycle()
    {
        var (f, ticket) = await ResolvedAsync();
        var service = f.CreateReviewService();
        var first = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);
        await service.SubmitAsync(first, new SubmitReviewRequestDto(3, null));

        ticket.Close();
        LegacyReopenedTicket.Apply(ticket, ticket.CurrentDepartmentId);
        await f.ResolveAsync(ticket);
        var second = await f.CreateInvitationService().PrepareAsync(ticket);

        Assert.NotNull(second);
        Assert.Equal(SubmitReviewOutcome.Submitted, (await service.SubmitAsync(TokenFrom(second.PageUrl), new SubmitReviewRequestDto(5, null))).Outcome);
        Assert.Equal(2, f.Reviews.Reviews.Count);
    }

    // ------------------------------------------ the emails and the review ask

    [Fact]
    public async Task Resolved_ThenClosed_WithNoReview_BothInviteTheCustomer()
    {
        var (f, ticket) = await ResolvedAsync();
        await f.CreateResolvedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId));
        ticket.Close();
        await f.CreateClosedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId));

        Assert.Equal(2, f.Email.Sent.Count);
        Assert.All(f.Email.Sent, e => Assert.Contains("How was your experience?", e.HtmlBody, StringComparison.Ordinal));
        Assert.Equal(2, f.Reviews.Invitations.Count);
    }

    [Fact]
    public async Task Closed_AfterAReviewWasSubmitted_DoesNotAskAgain_AndPreservesTheReview()
    {
        var (f, ticket) = await ResolvedAsync();
        await f.CreateResolvedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId));
        var resolvedEmail = Assert.Single(f.Email.Sent);
        var token = TokenFrom(System.Text.RegularExpressions.Regex.Match(resolvedEmail.Body, @"https://\S+/Review\?t=[A-Za-z0-9_\-]+").Value);
        await f.CreateReviewService().SubmitAsync(token, new SubmitReviewRequestDto(5, "Great"));

        ticket.Close();
        await f.CreateClosedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId));

        var closedEmail = f.Email.Sent[1];
        Assert.EndsWith("Has Been Closed", closedEmail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("How was your experience?", closedEmail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("/Review", closedEmail.HtmlBody + closedEmail.Body, StringComparison.Ordinal);
        Assert.Single(f.Reviews.Invitations); // no new link was issued
        var review = Assert.Single(f.Reviews.Reviews);
        Assert.Equal(5, review.Rating);
        Assert.Equal("Great", review.Comment);
    }

    [Fact]
    public async Task Closed_WithoutAReview_IncludesTheInvitation()
    {
        var (f, ticket) = await ResolvedAsync();
        ticket.Close();

        await f.CreateClosedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId));

        var email = Assert.Single(f.Email.Sent);
        Assert.Contains("How was your experience?", email.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(email.HtmlBody!, @"&amp;r=[1-5]""").Count);
    }

    // ------------------------------------------------ test vs production data

    [Fact]
    public async Task TestReviews_NeverReachProductionFigures_AndDoNotBlockProductionReviews()
    {
        var f = new NotificationServiceFixture { ModePolicy = new CustomerNotificationModePolicy("true", "uat@example.org", null, Base) };
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com", "TG-CS-20260822-0410");
        await f.ResolveAsync(ticket);
        var testToken = TokenFrom((await f.CreateInvitationService().PrepareAsync(ticket))!.PageUrl);

        Assert.Equal(SubmitReviewOutcome.Submitted, (await f.CreateReviewService().SubmitAsync(testToken, new SubmitReviewRequestDto(1, "test"))).Outcome);

        Assert.True(Assert.Single(f.Reviews.Reviews).IsTest);
        var summary = await f.Reviews.GetProductionSummaryAsync();
        Assert.Equal(0, summary.ReviewCount);
        Assert.Null(summary.AverageRating);

        // Later, in Production: the earlier test review does not count as "already reviewed".
        f.ModePolicy = CustomerNotificationModePolicy.ProductionDefault(Base);
        var productionLinks = await f.CreateInvitationService().PrepareAsync(ticket);
        Assert.NotNull(productionLinks);
        Assert.False(f.Reviews.Invitations[^1].IsTest);
        await f.CreateReviewService().SubmitAsync(TokenFrom(productionLinks.PageUrl), new SubmitReviewRequestDto(4, null));

        var after = await f.Reviews.GetProductionSummaryAsync();
        Assert.Equal(1, after.ReviewCount);
        Assert.Equal(4d, after.AverageRating);
    }
}
