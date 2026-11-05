using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Regression for the Customers directory (<c>/Customers</c>) and the phone-keyed Customer Profile
/// (<c>/Customers/phone:…?fromTicket=…</c>) failing together.
///
/// <para>
/// Both pages read the same identity projection. It used to resolve each ticket's captured number with a correlated
/// <c>SELECT TOP(1)</c> over <c>IntakeRecords</c> that EF Core inlined ~14 times per ticket, for EVERY visible ticket (the
/// phone-keyed profile can only filter after the identity is computed), under SqlClient's default 30 s command timeout.
/// The SQLite tests cannot see that — they prove the rows, not the plan — so this runs the real repository through the SQL
/// Server provider, with the connection and the command suppressed (no database needed), and inspects the T-SQL it
/// would have sent.
/// </para>
/// </summary>
public sealed class CustomerDirectoryQueryShapeTests
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

        // The fake reader has no real rows, so materialization may throw after the SQL has been captured — only the SQL is under test.
        try { await run(new CustomerDirectoryRepository(db)); } catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or ArgumentOutOfRangeException) { }

        return capture.Sql;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void AssertIntakePhoneIsResolvedOnce(string sql)
    {
        // No correlated per-ticket look-up of the captured number…
        Assert.DoesNotContain("TOP(1)", sql, StringComparison.Ordinal);
        // …but ONE pre-aggregated derived table (first intake id per ticket) joined once to the intake rows.
        Assert.Contains("LEFT JOIN", sql, StringComparison.Ordinal);
        Assert.Contains("MIN([i].[IntakeRecordId])", sql, StringComparison.Ordinal);
        Assert.Equal(2, Count(sql, "[IntakeRecords]")); // the aggregate (FROM) and its join target (INNER JOIN) — never one per reference
    }

    [Fact]
    public async Task List_CountAndPageQueries_ResolveEachTicketsPhoneWithOneJoin_NotCorrelatedSubqueries()
    {
        var sql = await CaptureAsync(r => r.ListAsync(new CustomerDirectoryQuery([1, 2], null, null, null, false, 1, 25)));

        Assert.Equal(2, sql.Count); // the count, then the page — and nothing else (the fake reader yields no rows)
        Assert.All(sql, AssertIntakePhoneIsResolvedOnce);
    }

    [Fact]
    public async Task List_WithSearchAndFilters_StillResolvesThePhoneWithOneJoinPerStatement()
    {
        var sql = await CaptureAsync(r => r.ListAsync(new CustomerDirectoryQuery([1, 2], "971501234567", "Unverified", 2, true, 1, 25)));

        Assert.NotEmpty(sql);
        Assert.All(sql, s => Assert.DoesNotContain("TOP(1)", s, StringComparison.Ordinal));
        // The search/department predicates reuse the identity projection as EXISTS sub-selects; each is still a join, never a TOP(1).
        Assert.All(sql, s => Assert.Contains("MIN([i].[IntakeRecordId])", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneKeyedProfile_ResolvesEachTicketsPhoneWithOneJoin_NotCorrelatedSubqueries()
    {
        var sql = await CaptureAsync(r => r.GetProfileAsync(CustomerIdentity.Phone("971501234567"), [1, 2]));

        var tickets = Assert.Single(sql.Take(1));
        AssertIntakePhoneIsResolvedOnce(tickets);
        Assert.Contains("@identity_PhoneNumber", tickets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrmKeyedProfile_ResolvesThePhoneWithOneJoin_Too()
    {
        var sql = await CaptureAsync(r => r.GetProfileAsync(CustomerIdentity.Crm(9001), [1, 2]));

        AssertIntakePhoneIsResolvedOnce(sql[0]);
    }
}
