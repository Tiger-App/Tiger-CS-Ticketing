using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The Customers directory across the production cut-over of <c>AddTicketAiAgentOwner</c> (PR #288), on a REAL SQL Server.
/// The directory carries the whole <see cref="Ticket"/> entity through its query, so every mapped Tickets column is in its SELECT list:
/// against a database that lacks <c>Tickets.AiAgentOwnerEmployeeId</c> it fails with "Invalid column name", and once the committed
/// <c>AddTicketAiAgentOwner.sql</c> script (what production ran) has been applied it must work. Proves both halves, so a "column exists but the
/// page still fails" report can be bisected: if this passes, the failure is in the environment (wrong database / stale deployment / data), not the query.
/// </summary>
public sealed class CustomerDirectoryMigrationCutoverSqlServerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private const string Column = "AiAgentOwnerEmployeeId";

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsCutoverTest_{Guid.NewGuid():N}";

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;
    private TigerCsDbContext CreateContext() => new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_master)) return;
        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(name);
    }

    private async Task<CustomerDirectoryListResultDto> ListAsync()
    {
        using var context = CreateContext();
        var time = new DashboardSqliteFixture.FixedTimeProvider(Now);
        var queries = new TicketQueryAppService(
            new TicketRepository(context), new UserDepartmentAssignmentRepository(context), new FakeTicketResolutionRepository(),
            new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, time);
        var service = new CustomerDirectoryAppService(new CustomerDirectoryRepository(context, ReopenPolicy.Default, time), queries);
        return await service.ListAsync(Guid.NewGuid(), [Roles.CsSupervisor], new CustomerDirectoryListRequestDto(PageSize: 50));
    }

    [VisibilitySqlServerFact]
    public async Task Directory_FailsWithoutTheColumn_AndWorksOnceTheProductionScriptHasBeenApplied()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            var department = new Department("Customer Service", "CSV");
            context.Departments.Add(department);
            context.SaveChanges();
            if (!context.Priorities.Any(x => x.PriorityId == (byte)PriorityLevel.Medium)) context.Priorities.Add(new Priority((byte)PriorityLevel.Medium, "Medium", 3));
            var category = new Category("General Inquiry", department.DepartmentId);
            context.Categories.Add(category);
            context.SaveChanges();
            for (var n = 1; n <= 3; n++)
            {
                context.Tickets.Add(Ticket.CreateVerifiedFromCrmBuyer(
                    $"TG-CO-{n:D6}", department.DepartmentId, n, n * 10, n, 7, $"Customer {n}", "Tiger Tower", $"U-{n}",
                    category.CategoryId, (byte)PriorityLevel.Medium, $"CRM ticket {n}", Now.AddHours(-n)));
            }

            context.SaveChanges();
        }

        Assert.Equal(3, (await ListAsync()).TotalCount);   // the fully migrated schema (what the code was built for)

        // The database as it was before the migration was applied.
        Exec($"ALTER TABLE [Tickets] DROP COLUMN [{Column}]; DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261118124858_AddTicketAiAgentOwner';");
        var failure = await Assert.ThrowsAsync<SqlException>(ListAsync);
        Assert.Contains($"Invalid column name '{Column}'", failure.Message, StringComparison.Ordinal);

        // Apply exactly the script production ran (batches split on GO).
        var script = File.ReadAllText(RepoFile("AddTicketAiAgentOwner.sql"));
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(batch)) Exec(batch);
        }

        Assert.Equal(3, (await ListAsync()).TotalCount);
    }
}
