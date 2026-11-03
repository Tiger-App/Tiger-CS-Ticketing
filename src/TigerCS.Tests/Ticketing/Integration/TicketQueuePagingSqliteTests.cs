using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Queue paging as real SQL: the total is the full filtered count, every page holds exactly its slice, pages never overlap or skip a
/// ticket (including when many tickets share the same CreatedAtUtc, as bulk-created ones do), and a page past the end is empty with the
/// right total. Paging was reported broken on /Tickets?view=queue&amp;pageSize=20 ("Page 1 of 10", Next did nothing).
/// </summary>
public sealed class TicketQueuePagingSqliteTests : IDisposable
{
    private static readonly string[] Manager = [Roles.CsManager];
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);

    public void Dispose() => _db.Dispose();

    private async Task<TicketListResultDto> PageAsync(int page, int pageSize, string? sortDir = null)
    {
        using var context = _db.CreateContext();
        return await _db.CreateQueryService(context).GetQueueAsync(
            Guid.NewGuid(), Manager, new TicketListRequestDto(null, null, null, null, null, null, null, null, sortDir, page, pageSize));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task First_Middle_Last_AndBeyond_PartitionTheQueueExactly(string? sortDir)
    {
        using (var context = _db.CreateContext())
        {
            for (var i = 0; i < 45; i++) _db.AddTicket(context, _db.CustomerServiceId); // same timestamp for all: ordering must still be stable
        }

        var first = await PageAsync(1, 20, sortDir);
        var middle = await PageAsync(2, 20, sortDir);
        var last = await PageAsync(3, 20, sortDir);
        var beyond = await PageAsync(4, 20, sortDir);

        Assert.All(new[] { first, middle, last, beyond }, p => Assert.Equal(45, p.TotalCount));
        Assert.Equal([20, 20, 5, 0], new[] { first, middle, last, beyond }.Select(p => p.Items.Count));
        var all = first.Items.Concat(middle.Items).Concat(last.Items).Select(i => i.TicketId).ToList();
        Assert.Equal(45, all.Distinct().Count()); // no ticket repeated across pages, none skipped
    }

    [Fact]
    public async Task AnEmptyQueue_HasTotalZero_AndNoItems()
    {
        var page = await PageAsync(1, 20);

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }
}
