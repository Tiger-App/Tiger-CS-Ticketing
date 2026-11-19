using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;
using Xunit.Abstractions;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// What the Customers directory does when its caller goes away, when the
/// DATABASE fails, and how much work it asks the database for — the three
/// questions behind "TaskCanceledException in CustomerDirectoryRepository".
///
/// <list type="bullet">
/// <item>Cancellation still propagates: the CancellationToken the controller
/// hands down really does reach EF Core, so an abandoned request stops
/// instead of running to completion for nobody. That is the behaviour the
/// exception reported, and it is kept — the host, not the repository, is
/// where an abandoned request stops being an error
/// (<c>ClientDisconnectMiddleware</c>).</item>
/// <item>A genuine database failure — the SQL timeout this was first
/// mistaken for — is NOT a cancellation and is not swallowed anywhere in
/// this path.</item>
/// <item>The listing costs a fixed, small number of round trips whatever the
/// page holds: no query per row hiding behind the paging.</item>
/// </list>
/// </summary>
public sealed class CustomerDirectoryCancellationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private readonly DashboardSqliteFixture _db = new();
    private int _sequence = 9_000;

    public void Dispose() => _db.Dispose();

    private static CustomerDirectoryQuery Query(int page = 1, int pageSize = 25) =>
        new(VisibleDepartmentIds: null, Search: null, VerificationSource: null, DepartmentId: null, OpenOnly: false, page, pageSize);

    /// <summary>Counts the commands a repository call actually sends, so "no query per row" is measured rather than assumed.</summary>
    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Records what each command was and how long the database took over it, so the count and the paged query can be reported separately.</summary>
    private sealed class TimingCommandInterceptor : DbCommandInterceptor
    {
        public List<(string Sql, TimeSpan Duration)> Executed { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Executed.Add((command.CommandText, eventData.Duration));
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            Executed.Add((command.CommandText, eventData.Duration));
            return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Makes one command of a call fail the way a database does — used here
    /// to stand in for a command timeout. Addressed by ORDINAL, because
    /// ListAsync issues its commands in a fixed order
    /// (<see cref="CountCommand"/>, then <see cref="PagedCommand"/>, then the
    /// page dressing) and every one of them contains both COUNT and LIMIT:
    /// the phone-identity subquery is itself a LIMIT 1, and the paged query
    /// counts each customer's tickets.
    /// </summary>
    private sealed class FailingCommandInterceptor(int failAtCommandIndex, Exception failure) : DbCommandInterceptor
    {
        private int _seen;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Fail();
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Fail()
        {
            if (_seen++ == failAtCommandIndex)
            {
                throw failure;
            }
        }
    }

    /// <summary>ListAsync's first command: the directory's own <c>CountAsync</c>, which produces TotalCount.</summary>
    private const int CountCommand = 0;

    /// <summary>ListAsync's second command: the ordered, skipped and taken page.</summary>
    private const int PagedCommand = 1;

    // ---- seeding ----

    private void SeedCustomers(int crmCustomers, int ticketsEach)
    {
        using var context = _db.CreateContext();
        for (var customer = 1; customer <= crmCustomers; customer++)
        {
            for (var t = 0; t < ticketsEach; t++)
            {
                var seq = ++_sequence;
                var ticket = Ticket.CreateVerifiedFromCrmBuyer(
                    $"TG-PERF-{seq:D6}", _db.CustomerServiceId, 100_000 + customer, (100_000 + customer) * 10, seq, 7,
                    $"Customer {customer}", "Tiger Tower", $"T-{customer:D4}", _db.CsCategoryId,
                    (byte)PriorityLevel.Medium, $"Perf ticket {seq}", Now.AddMinutes(-seq));
                context.Tickets.Add(ticket);
            }
        }

        // One SaveChanges for the whole seed: this is fixture setup, not the
        // thing being measured.
        context.SaveChanges();
    }

    // ---- 1. cancellation still propagates through EF Core ----

    /// <summary>
    /// The behaviour that produced the reported exception, kept deliberately.
    /// A token cancelled before the call — exactly what RequestAborted looks
    /// like once the browser has reset the connection — stops the query
    /// rather than letting it run to completion for a reader who has gone.
    /// </summary>
    [Fact]
    public async Task ListAsync_WithAnAlreadyCancelledToken_StopsInsteadOfRunningTheQuery()
    {
        SeedCustomers(crmCustomers: 3, ticketsEach: 2);
        using var context = _db.CreateContext();
        var repository = new CustomerDirectoryRepository(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListAsync(Query(), cancelled.Token));
    }

    /// <summary>The same for the profile behind a row — one cancellation story for the whole directory.</summary>
    [Fact]
    public async Task GetProfileAsync_WithAnAlreadyCancelledToken_StopsInsteadOfRunningTheQuery()
    {
        SeedCustomers(crmCustomers: 1, ticketsEach: 1);
        using var context = _db.CreateContext();
        var repository = new CustomerDirectoryRepository(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetProfileAsync(CustomerIdentity.Crm(100_001), null, cancelled.Token));
    }

    /// <summary>An uncancelled call is unaffected — the token changes nothing about the answer.</summary>
    [Fact]
    public async Task ListAsync_WithALiveToken_ReturnsTheDirectoryUnchanged()
    {
        SeedCustomers(crmCustomers: 4, ticketsEach: 3);
        using var context = _db.CreateContext();
        var repository = new CustomerDirectoryRepository(context);
        using var live = new CancellationTokenSource();

        var page = await repository.ListAsync(Query(), live.Token);

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(4, page.Items.Count);
        Assert.All(page.Items, row => Assert.Equal(3, row.TotalTickets));
    }

    // ---- 2. a genuine database failure is still a failure ----

    /// <summary>
    /// A SQL command timeout on the directory's <c>CountAsync</c> — the
    /// failure this was first diagnosed as. It is not an
    /// <see cref="OperationCanceledException"/>, nothing in this path
    /// swallows or reshapes it, and it leaves the repository intact so the
    /// host logs it as the error it is and answers 500.
    /// </summary>
    [Fact]
    public async Task ListAsync_WhenTheCountQueryTimesOut_SurfacesTheFailure()
    {
        SeedCustomers(crmCustomers: 2, ticketsEach: 1);
        var timeout = new TimeoutException("Execution Timeout Expired. The timeout period elapsed prior to completion of the operation.");
        using var context = _db.CreateContext(new FailingCommandInterceptor(CountCommand, timeout));
        var repository = new CustomerDirectoryRepository(context);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => repository.ListAsync(Query(), CancellationToken.None));

        Assert.Same(timeout, thrown);
        Assert.IsNotType<OperationCanceledException>(thrown, exactMatch: false);
    }

    /// <summary>And on the paged query itself, after the count has already succeeded.</summary>
    [Fact]
    public async Task ListAsync_WhenThePagedQueryTimesOut_SurfacesTheFailure()
    {
        SeedCustomers(crmCustomers: 2, ticketsEach: 1);
        var timeout = new TimeoutException("Execution Timeout Expired.");
        using var context = _db.CreateContext(new FailingCommandInterceptor(PagedCommand, timeout));
        var repository = new CustomerDirectoryRepository(context);

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => repository.ListAsync(Query(), CancellationToken.None));

        Assert.Same(timeout, thrown);
    }

    // ---- 3. what the listing actually costs ----

    /// <summary>
    /// The listing is a fixed number of round trips — the count, the page,
    /// the page's latest tickets, and one query per fact table that dresses
    /// them — however many rows the page holds. A page of 50 costs what a
    /// page of 5 does; there is no per-row query hiding behind the paging.
    /// </summary>
    private static readonly Dictionary<int, int> Measured = [];

    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    public async Task ListAsync_CostsTheSameFewQueries_WhateverThePageSize(int pageSize)
    {
        var counts = Measured;
        SeedCustomers(crmCustomers: 120, ticketsEach: 3);
        var counter = new CountingCommandInterceptor();
        using var context = _db.CreateContext(counter);
        var repository = new CustomerDirectoryRepository(context);

        var page = await repository.ListAsync(Query(pageSize: pageSize), CancellationToken.None);

        Assert.Equal(120, page.TotalCount);
        Assert.Equal(pageSize, page.Items.Count);
        output.WriteLine($"pageSize={pageSize}: {counter.Commands.Count} database commands for {page.Items.Count} rows.");
        // 9 today: count, page, last tickets, intakes, interactions, requester snapshots, the edited details, the saved contact records,
        // and the names from ALL of the page customers' conversations (two reads: their tickets, then their interactions' names - so a
        // name captured on an older ticket shows on the list exactly as on the profile). The ceiling is what matters: it must not
        // scale with the page, and the Theory's two page sizes must cost the same.
        Assert.InRange(counter.Commands.Count, 1, 10);
        counts[pageSize] = counter.Commands.Count;
        if (counts.Count == 2) Assert.Equal(counts[5], counts[50]);
    }

    /// <summary>
    /// The measurement behind "is this actually a slow query?": the
    /// directory's CountAsync and its paged query, timed separately, over a
    /// dataset larger than the pilot's. The assertion is a deliberately loose
    /// ceiling — a timing assertion tight enough to be interesting would be a
    /// flaky one — but the numbers are written to the test output, which is
    /// what the investigation needed.
    /// </summary>
    [Fact]
    public async Task ListAsync_CountAndPagedQuery_AreMeasured()
    {
        SeedCustomers(crmCustomers: 500, ticketsEach: 4);
        var timing = new TimingCommandInterceptor();
        using var context = _db.CreateContext(timing);
        var repository = new CustomerDirectoryRepository(context);

        // One untimed call first, so EF Core's model/query compilation is not
        // charged to the measurement.
        await repository.ListAsync(Query(), CancellationToken.None);
        timing.Executed.Clear();

        var stopwatch = Stopwatch.StartNew();
        var page = await repository.ListAsync(Query(pageSize: 25), CancellationToken.None);
        stopwatch.Stop();

        output.WriteLine($"2,000 tickets / 500 customers, page 1 of 25 — full ListAsync {stopwatch.Elapsed.TotalMilliseconds:F1} ms across {timing.Executed.Count} commands:");
        for (var i = 0; i < timing.Executed.Count; i++)
        {
            var (sql, duration) = timing.Executed[i];
            // By ordinal: every one of these commands contains both COUNT and
            // LIMIT, so the SQL text cannot tell them apart. ListAsync issues
            // them in a fixed order.
            var shape = i == CountCommand ? "CountAsync" : i == PagedCommand ? "paged query" : "page dressing";
            var oneLine = sql.ReplaceLineEndings(" ");
            output.WriteLine($"  {shape,-14} {duration.TotalMilliseconds,7:F1} ms  {oneLine[..Math.Min(oneLine.Length, 110)]}…");
        }

        Assert.Equal(500, page.TotalCount);
        Assert.Equal(25, page.Items.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"The Customers listing took {stopwatch.Elapsed.TotalMilliseconds:F0} ms, which is far past anything a query timeout could be blamed on.");
    }
}
