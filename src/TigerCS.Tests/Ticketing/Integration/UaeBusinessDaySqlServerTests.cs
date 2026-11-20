using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The UAE business-day boundaries as SQL Server executes them: the created-date range, Due Today, and the dashboard volume are translated to
/// range predicates on the UTC columns, so a ticket at 23:30 / 00:30 / 03:59 UAE lands on the right UAE day on the real engine too (a UTC-day
/// bucket, or a function wrapped around the column, would not). A throw-away database per test; SKIPPED without <c>TIGERCS_TEST_SQL_SERVER</c>.
/// </summary>
public sealed class UaeBusinessDaySqlServerTests : IDisposable
{
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];
    private static readonly DateOnly Day = new(2026, 9, 10);

    private static DateTime Uae(DateOnly day, int hour, int minute, int second = 0) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute, second)).AddHours(-4), DateTimeKind.Utc);

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsUaeDayTest_{Guid.NewGuid():N}";

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    private TigerCsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_master))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }

    private int CreateDatabaseWithDepartment()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using var context = CreateContext();
        context.Database.Migrate();
        var department = new Department("UAE Day Test", "UDT");
        context.Departments.Add(department);
        context.SaveChanges();
        return department.DepartmentId;
    }

    private static void AddTicket(TigerCsDbContext context, int departmentId, int n, DateTime createdUtc, DateTime? resolutionDueUtc = null)
    {
        var ticket = Ticket.CreateUnclassified($"TG-UAE-20260910-{n:D4}", departmentId, "AC not cooling", createdUtc);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        if (resolutionDueUtc is { } due)
        {
            context.TicketSlaInstances.Add(TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, (byte)PriorityLevel.Medium, createdUtc, due, due));
            context.SaveChanges();
        }
    }

    private static (DashboardAppService Dashboard, TicketQueryAppService Queue) Compose(TigerCsDbContext context, DateTime nowUtc)
    {
        var time = new DashboardSqliteFixture.FixedTimeProvider(nowUtc);
        var assignments = new UserDepartmentAssignmentRepository(context);
        var tickets = new TicketRepository(context, ReopenPolicy.Default, time);
        var queue = new TicketQueryAppService(
            tickets, assignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, time,
            handlerRepository: new TicketInteractionHandlerRepository(context));
        var dashboard = new DashboardAppService(tickets, queue, time, new DashboardQueryRepository(context, ReopenPolicy.Default, time), assignments);
        return (dashboard, queue);
    }

    [VisibilitySqlServerFact]
    public async Task CreatedRange_AndDashboardVolume_PutThe2330_0030_And0359TicketsOnTheirUaeDay_OnSqlServer()
    {
        var departmentId = CreateDatabaseWithDepartment();
        using (var seed = CreateContext())
        {
            var n = 0;
            foreach (var created in new[]
            {
                Uae(Day.AddDays(-1), 23, 59, 59), Uae(Day, 0, 0), Uae(Day, 0, 30), Uae(Day, 3, 59), Uae(Day, 23, 30), Uae(Day.AddDays(1), 0, 0)
            })
            {
                AddTicket(seed, departmentId, ++n, created);
            }
        }

        using var context = CreateContext();
        var (dashboard, queue) = Compose(context, Uae(Day, 12, 0));

        var list = await queue.GetQueueAsync(
            Guid.NewGuid(), CrossDepartment,
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100, CreatedFrom: Day, CreatedTo: Day));
        var overview = await dashboard.GetOverviewAsync(Guid.NewGuid(), CrossDepartment, new DashboardOverviewRequestDto(Day, Day));

        Assert.Equal(4, list.TotalCount);
        Assert.Equal(4, overview.VolumeTotal);
    }

    [VisibilitySqlServerFact]
    public async Task DueToday_AtHalfPastMidnightUae_IsTheNewUaeDay_OnSqlServer()
    {
        var departmentId = CreateDatabaseWithDepartment();
        using (var seed = CreateContext())
        {
            var n = 0;
            var longAgo = Uae(Day.AddDays(-3), 9, 0);
            foreach (var due in new[]
            {
                Uae(Day.AddDays(-1), 23, 59, 59), Uae(Day, 0, 0), Uae(Day, 3, 59), Uae(Day, 23, 30), Uae(Day.AddDays(1), 0, 0)
            })
            {
                AddTicket(seed, departmentId, ++n, longAgo, due);
            }
        }

        using var context = CreateContext();
        var (dashboard, queue) = Compose(context, Uae(Day, 0, 30));

        var list = await queue.GetQueueAsync(
            Guid.NewGuid(), CrossDepartment,
            new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100, DueToday: true));
        var overview = await dashboard.GetOverviewAsync(Guid.NewGuid(), CrossDepartment, new DashboardOverviewRequestDto());

        Assert.Equal(3, list.TotalCount);
        Assert.Equal(3, overview.Kpis.DueToday);
    }
}
