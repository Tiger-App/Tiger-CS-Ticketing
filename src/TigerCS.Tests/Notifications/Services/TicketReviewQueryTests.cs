using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.CustomerReviews.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.CustomerReviews;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Fakes;
using TigerCS.Tests.Ticketing;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>The Ticket Details Customer Satisfaction read model: persisted reviews only, per cycle, authorized, token-free.</summary>
public class TicketReviewQueryTests
{
    private static (TicketReviewQueryAppService Query, FakeUserDepartmentAssignmentRepository Assignments) CreateQuery(NotificationServiceFixture f)
    {
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var ticketQuery = new TicketQueryAppService(
            f.Tickets, assignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, f.Time);
        return (new TicketReviewQueryAppService(f.Tickets, f.Reviews, ticketQuery), assignments);
    }

    private static string TokenFrom(string url) => Uri.UnescapeDataString(url.Split("t=")[1].Split('&')[0]);

    private static async Task<(NotificationServiceFixture F, Ticket Ticket)> ResolvedAsync()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com", "TG-CS-20261022-0500");
        await f.ResolveAsync(ticket);
        return (f, ticket);
    }

    private static Task<TigerCS.Application.Modules.Ticketing.Dto.TicketQueryResultDto<TicketReviewsDto>> Read(
        TicketReviewQueryAppService query, Ticket ticket) =>
        query.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

    [Fact]
    public async Task NoInvitationAndNoReview_ReturnsNoCycles_SoNothingIsInvented()
    {
        var (f, ticket) = await ResolvedAsync();

        var result = await Read(CreateQuery(f).Query, ticket);

        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
        Assert.Empty(result.Response!.Cycles);
    }

    [Fact]
    public async Task InvitedButNotSubmitted_IsPending_WithNoRatingOrComment()
    {
        var (f, ticket) = await ResolvedAsync();
        await f.CreateInvitationService().PrepareAsync(ticket);

        var cycle = Assert.Single((await Read(CreateQuery(f).Query, ticket)).Response!.Cycles);

        Assert.Equal("Pending", cycle.Status);
        Assert.Null(cycle.Rating);
        Assert.Null(cycle.Comment);
        Assert.Null(cycle.SubmittedAtUtc);
        Assert.NotNull(cycle.RequestedAtUtc);
    }

    [Fact]
    public async Task SubmittedReview_IsReturnedWithRatingCommentAndDate()
    {
        var (f, ticket) = await ResolvedAsync();
        var links = await f.CreateInvitationService().PrepareAsync(ticket);
        f.Time.Advance(TimeSpan.FromHours(2));
        await f.CreateReviewService().SubmitAsync(TokenFrom(links!.PageUrl), new SubmitReviewRequestDto(4, "  Quick and polite.  "));

        var cycle = Assert.Single((await Read(CreateQuery(f).Query, ticket)).Response!.Cycles);

        Assert.Equal("Reviewed", cycle.Status);
        Assert.Equal(4, cycle.Rating);
        Assert.Equal("Quick and polite.", cycle.Comment);
        Assert.Equal(f.Time.GetUtcNow().UtcDateTime, cycle.SubmittedAtUtc);
        Assert.False(cycle.IsTest);
        Assert.Equal(0, cycle.ReopenCycle);
    }

    [Fact]
    public async Task ReopenedTicket_ShowsEachCycleSeparately_OriginalReviewedAndNewPending()
    {
        var (f, ticket) = await ResolvedAsync();
        var first = await f.CreateInvitationService().PrepareAsync(ticket);
        await f.CreateReviewService().SubmitAsync(TokenFrom(first!.PageUrl), new SubmitReviewRequestDto(3, "Fine."));

        ticket.Close();
        LegacyReopenedTicket.Apply(ticket, ticket.CurrentDepartmentId);
        await f.ResolveAsync(ticket);
        var second = await f.CreateInvitationService().PrepareAsync(ticket);

        var query = CreateQuery(f).Query;
        var pending = (await Read(query, ticket)).Response!.Cycles;
        Assert.Equal([0, 1], pending.Select(c => c.ReopenCycle));
        Assert.Equal(["Reviewed", "Pending"], pending.Select(c => c.Status));
        Assert.Equal(3, pending[0].Rating);
        Assert.Null(pending[1].Rating);

        await f.CreateReviewService().SubmitAsync(TokenFrom(second!.PageUrl), new SubmitReviewRequestDto(5, null));

        var reviewed = (await Read(query, ticket)).Response!.Cycles;
        Assert.Equal(["Reviewed", "Reviewed"], reviewed.Select(c => c.Status));
        Assert.Equal([3, 5], reviewed.Select(c => c.Rating));
        Assert.Null(reviewed[1].Comment);
    }

    [Fact]
    public async Task TestReviews_AreFlagged_AndStayOutOfProductionStatistics()
    {
        var (f, ticket) = await ResolvedAsync();
        f.ModePolicy = new TigerCS.Application.Modules.Notifications.CustomerNotificationModePolicy(
            "true", "t@example.com", null, NotificationServiceFixture.PublicBaseUrl);
        var links = await f.CreateInvitationService().PrepareAsync(ticket);
        await f.CreateReviewService().SubmitAsync(TokenFrom(links!.PageUrl), new SubmitReviewRequestDto(1, "test"));

        var cycle = Assert.Single((await Read(CreateQuery(f).Query, ticket)).Response!.Cycles);

        Assert.True(cycle.IsTest);
        Assert.Equal(0, (await f.Reviews.GetProductionSummaryAsync()).ReviewCount);
    }

    [Fact]
    public void TheDto_CarriesNoTokenOrLinkMembers()
    {
        var members = typeof(TicketReviewCycleDto).GetProperties().Select(p => p.Name)
            .Concat(typeof(TicketReviewsDto).GetProperties().Select(p => p.Name)).ToList();

        Assert.DoesNotContain(members, m => m.Contains("Token", StringComparison.OrdinalIgnoreCase) || m.Contains("Hash", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Url", StringComparison.OrdinalIgnoreCase) || m.Contains("Link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnauthorizedCaller_GetsForbidden_AndUnknownTicketNotFound()
    {
        var (f, ticket) = await ResolvedAsync();
        var (query, assignments) = CreateQuery(f);
        var outsider = Guid.NewGuid();
        assignments.Assignments.Add(new UserDepartmentAssignment(outsider, 999, true, DateTime.UtcNow, null));

        var forbidden = await query.GetForTicketAsync(outsider, [Roles.DepartmentEmployee], ticket.TicketId);
        var missing = await query.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], 987654);

        Assert.Equal(TicketQueryOutcome.Forbidden, forbidden.Outcome);
        Assert.Null(forbidden.Response);
        Assert.Equal(TicketQueryOutcome.NotFound, missing.Outcome);
    }
}
