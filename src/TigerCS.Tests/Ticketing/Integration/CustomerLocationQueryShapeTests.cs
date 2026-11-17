using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Regression guard for the Customers by Location aggregate, on the T-SQL the SQL Server provider would send (no database needed: the connection
/// and the command are suppressed, exactly as <see cref="CustomerDirectoryQueryShapeTests"/> does). The first version selected customers with
/// the Dashboard's date range through a correlated EXISTS over the whole identification pipeline: it translated, passed on SQLite, and took over
/// 100 s on 6,000 customers on a real SQL Server. The shape that must hold: ONE grouped statement, the ticket filter folded into that same pass
/// (<c>MAX(flag)</c>), the phone resolved once, and a bounded size however many filters ride on it.
/// </summary>
public sealed class CustomerLocationQueryShapeTests
{
    private sealed class CaptureCommands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            var table = new DataTable();
            table.Columns.Add("c", typeof(int));
            table.Rows.Add(0);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }

    private sealed class SuppressConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private static async Task<List<string>> CaptureAsync(Func<CustomerDirectoryRepository, Task> run)
    {
        var capture = new CaptureCommands();
        using var db = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlServer("Server=(never-connected);Database=(never-connected);TrustServerCertificate=True;")
            .AddInterceptors(capture, new SuppressConnection())
            .Options);

        try { await run(new CustomerDirectoryRepository(db)); } catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or ArgumentOutOfRangeException) { }

        return capture.Sql;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static readonly CustomerDirectoryTicketFilter EveryFilter = new(
        new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), 2, Guid.NewGuid(), 3, 4, TicketStatus.Open, 2);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TheCount_IsOneGroupedStatement_WithTheFilterFoldedIntoTheSamePass_AndABoundedSize(bool restrictedAgent, bool withFilter)
    {
        var sql = await CaptureAsync(r => r.CountByLocationAsync(new CustomerDirectoryQuery(
            restrictedAgent ? [] : [1, 2], null, null, null, false, 1, 25, restrictedAgent ? Guid.NewGuid() : null, Guid.NewGuid(), null, withFilter ? EveryFilter : null)));

        var statement = Assert.Single(sql);
        Assert.Contains("GROUP BY", statement, StringComparison.Ordinal);

        if (withFilter)
        {
            // The customers are selected by a per-customer MAX over the same grouped pass.
            Assert.Contains("MAX(", statement, StringComparison.Ordinal);
        }

        Assert.Equal(1, Count(statement, "MIN([i].[IntakeRecordId])"));   // each ticket's captured phone is resolved once, as one derived table
        Assert.Equal(2, Count(statement, "[IntakeRecords]"));            // that aggregate and its join target: never once per reference

        // The location weight is referenced once per aggregate (EF inlines at every reference): the digits-only test appears a handful of times, not dozens.
        Assert.InRange(Count(statement, "%[^0-9]%"), 1, 3);
        Assert.True(statement.Length < 90_000, $"The statement is {statement.Length} characters.");
    }

    /// <summary>
    /// The decisive property: asking for the Dashboard's filters must not add another pass over the tickets. The old correlated EXISTS referenced the
    /// whole identification a second time, so its statement had more <c>[Tickets]</c> references with the filter than without.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTicketFilter_DoesNotAddASecondPassOverTheTickets(bool restrictedAgent)
    {
        var without = Assert.Single(await CaptureAsync(r => r.CountByLocationAsync(new CustomerDirectoryQuery(
            restrictedAgent ? [] : [1, 2], null, null, null, false, 1, 25, restrictedAgent ? Guid.NewGuid() : null, Guid.NewGuid()))));
        var with = Assert.Single(await CaptureAsync(r => r.CountByLocationAsync(new CustomerDirectoryQuery(
            restrictedAgent ? [] : [1, 2], null, null, null, false, 1, 25, restrictedAgent ? Guid.NewGuid() : null, Guid.NewGuid(), null, EveryFilter))));

        Assert.Equal(Count(without, "[Tickets]"), Count(with, "[Tickets]"));
        Assert.Equal(Count(without, "[IntakeRecords]"), Count(with, "[IntakeRecords]"));
    }
}
