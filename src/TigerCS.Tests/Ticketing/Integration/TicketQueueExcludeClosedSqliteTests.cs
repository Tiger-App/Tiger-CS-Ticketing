using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The Tickets page Queue tab (<c>excludeClosed=true</c>) as real SQL: it leaves Closed tickets out of both the list and the total,
/// the All tab (no flag) still returns them, and the Closed tab (<c>ticketStatus=Closed</c>) returns only them. The flag narrows the
/// caller's normal visibility - it never adds a ticket they could not already see.
/// </summary>
public sealed class TicketQueueExcludeClosedSqliteTests : IDisposable
{
    private static readonly string[] Manager = [Roles.CsManager];
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);

    public void Dispose() => _db.Dispose();

    private async Task<TicketListResultDto> ListAsync(bool? excludeClosed = null, string? ticketStatus = null, string? search = null, int page = 1, int pageSize = 50)
    {
        using var context = _db.CreateContext();
        return await _db.CreateQueryService(context).GetQueueAsync(
            Guid.NewGuid(), Manager,
            new TicketListRequestDto(null, null, null, ticketStatus, null, null, search, null, null, page, pageSize, ExcludeClosed: excludeClosed));
    }

    private void Seed()
    {
        using var context = _db.CreateContext();
        _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Open);
        _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress);
        _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Resolved);
        _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed);
        _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed);
    }

    [Fact]
    public async Task Queue_ExcludesClosed_FromItemsAndTotal()
    {
        Seed();

        var queue = await ListAsync(excludeClosed: true);

        Assert.Equal(3, queue.TotalCount);
        Assert.DoesNotContain(queue.Items, i => i.TicketStatus == nameof(TicketStatus.Closed));
    }

    [Fact]
    public async Task All_IncludesClosed_AndClosedTabIsOnlyClosed()
    {
        Seed();

        var all = await ListAsync();
        var closed = await ListAsync(ticketStatus: "Closed");

        Assert.Equal(5, all.TotalCount);
        Assert.Equal(2, closed.TotalCount);
        Assert.All(closed.Items, i => Assert.Equal(nameof(TicketStatus.Closed), i.TicketStatus));
    }

    [Fact]
    public async Task Queue_PagingIsOverTheNonClosedSet_AndSearchStillApplies()
    {
        Seed();

        var first = await ListAsync(excludeClosed: true, page: 1, pageSize: 2);
        var second = await ListAsync(excludeClosed: true, page: 2, pageSize: 2);
        var searched = await ListAsync(excludeClosed: true, search: "Seed ticket");

        Assert.Equal([2, 1], new[] { first.Items.Count, second.Items.Count });
        Assert.All(new[] { first, second }, p => Assert.Equal(3, p.TotalCount));
        Assert.Equal(3, searched.TotalCount);
    }

    [Fact]
    public async Task Queue_WithAnExplicitClosedStatus_ReturnsNothing_NeverWidensTheSet()
    {
        Seed();

        var result = await ListAsync(excludeClosed: true, ticketStatus: "Closed");

        Assert.Equal(0, result.TotalCount);
    }
}
