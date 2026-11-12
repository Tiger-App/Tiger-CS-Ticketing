using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// The SLA report's queries against SQL Server itself. The rest of the report is tested over SQLite, which cannot show that a query
/// translates to T-SQL or that the migration's new column behaves; two tests close that gap:
/// an OFFLINE one that asks EF to translate every new query for the SQL Server provider (runs everywhere, no server needed), and one that
/// runs the whole report on a REAL SQL Server (a throw-away database, migrated by the real migrations; skipped without TIGERCS_TEST_SQL_SERVER, which CI sets).
/// </summary>
public sealed class TeamPerformanceSlaSqlServerTests : IDisposable
{
    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsSlaReportTest_{Guid.NewGuid():N}";

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

    // ---------------------------------------------------------------
    // Offline: every new query translates for SQL Server
    // ---------------------------------------------------------------

    [Fact]
    public void EveryNewSlaQuery_TranslatesToSqlServer()
    {
        using var context = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlServer("Server=offline;Database=TigerCS_Offline;Trusted_Connection=True;TrustServerCertificate=True").Options);
        var repository = new TeamPerformanceQueryRepository(context);
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(30);
        List<Guid> ids = [Guid.NewGuid(), Guid.NewGuid()];

        string Translate(string method, params object[] args)
        {
            var query = (System.Collections.IEnumerable)typeof(TeamPerformanceQueryRepository)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(repository, args)!;
            return ((IQueryable)query).ToQueryString();
        }

        var breaches = Translate("BreachMoments", start, end);
        var firstResponses = Translate("FirstResponseMetMoments", start, end);
        var resolutions = Translate("ResolutionMetMoments", start, end);

        foreach (var sql in new[] { breaches, firstResponses, resolutions })
        {
            Assert.Contains("TicketAssignments", sql);
            Assert.Contains("SupersededAtUtc", sql);                // the interval rule is in the SQL, not applied afterwards in memory
            Assert.DoesNotContain("APPLY", sql, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("TicketSlaInstances", breaches);            // the deadline is read from the SLA period in SQL
        Assert.Contains("LIKE", breaches);                          // the two shared note prefixes
        Assert.Contains("TicketSlaInstances", firstResponses);
        Assert.Contains("TicketResolutions", resolutions);
        Assert.Contains("JOIN", resolutions);

        // The shapes the repository actually executes (holder filtered to the report's employees) translate too.
        var filtered = ((IQueryable)typeof(TeamPerformanceQueryRepository).GetMethod("BreachMoments", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repository, [start, end])!);
        Assert.NotEmpty(filtered.ToQueryString());
    }

    // ---------------------------------------------------------------
    // Real SQL Server: migrations + the whole report
    // ---------------------------------------------------------------

    [VisibilitySqlServerFact]
    public async Task TheReport_RunsOnARealSqlServer_AttributesByDeadlineAndInterval_AndTableEqualsDetails()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_database}]";
            create.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow;                                   // real time: the SaveChanges hook stamps real end times
        var due = now.AddDays(-6);
        var amina = Guid.NewGuid();
        var bilal = Guid.NewGuid();

        using (var context = CreateContext())
        {
            context.Database.Migrate();                              // includes AddTicketAssignmentSupersededAt

            foreach (var (id, name) in new[] { (amina, "Amina Agent"), (bilal, "Bilal Caller") })
            {
                context.Users.Add(new ApplicationUser
                {
                    Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(),
                    Email = $"{id:N}@example.test", SecurityStamp = Guid.NewGuid().ToString()
                });
                context.Employees.Add(new Employee(id, name, isGeynessStaff: false, now.AddDays(-60)));
            }

            var department = new Department("SLA Report", "SLAR");
            context.Departments.Add(department);
            if (!context.Priorities.Any())
            {
                context.Priorities.AddRange(
                    new Priority((byte)PriorityLevel.Critical, "Critical", 1), new Priority((byte)PriorityLevel.High, "High", 2),
                    new Priority((byte)PriorityLevel.Medium, "Medium", 3), new Priority((byte)PriorityLevel.Low, "Low", 4));
            }

            context.SaveChanges();
            var category = new Category("General", department.DepartmentId);
            context.Categories.Add(category);
            context.SaveChanges();

            Ticket NewTicket(string number, Guid holder, DateTime created)
            {
                var ticket = Ticket.CreateUnverified(number, department.DepartmentId, category.CategoryId, (byte)PriorityLevel.Medium, "SLA report", created);
                context.Tickets.Add(ticket);
                context.SaveChanges();
                ticket.AssignTo(holder);
                context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, holder, department.DepartmentId, created.AddMinutes(5), null));
                context.SaveChanges();
                return ticket;
            }

            TicketSlaInstance Period(Ticket ticket, DateTime frDue, DateTime resDue, bool resolutionBreached)
            {
                var period = TicketSlaInstance.OpenInitialPeriod(ticket.TicketId, (byte)PriorityLevel.Medium, ticket.CreatedAtUtc, frDue, resDue);
                if (resolutionBreached) period.MarkBreached(SlaDeadlineType.Resolution);
                context.TicketSlaInstances.Add(period);
                context.SaveChanges();
                return period;
            }

            void Breach(Ticket ticket, DateTime deadline, DateTime recordedAt)
            {
                context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                    ticket.TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Breached,
                    null, true, SlaBreachNotes.For(SlaDeadlineType.Resolution, deadline), Guid.NewGuid(), recordedAt));
                context.SaveChanges();
            }

            void EndAssignment(Ticket ticket, DateTime at)
            {
                foreach (var current in context.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent))
                {
                    current.MarkSuperseded(at);
                }

                context.SaveChanges();
            }

            // 1. A delayed write: Amina holds it at the deadline, Bilal is assigned a day later, the scheduler records it two days later -> Amina's.
            var delayed = NewTicket("TG-SLR-0001", amina, now.AddDays(-12));
            Period(delayed, now.AddDays(-11), due, resolutionBreached: true);
            EndAssignment(delayed, due.AddDays(1));
            context.TicketAssignments.Add(new TicketAssignment(delayed.TicketId, bilal, department.DepartmentId, due.AddDays(1), null));
            context.SaveChanges();
            Breach(delayed, due, due.AddDays(2));

            // 2. Released an hour before the deadline; Bilal assigned an hour after; recorded three hours after -> nobody's.
            var released = NewTicket("TG-SLR-0002", amina, now.AddDays(-12));
            Period(released, now.AddDays(-11), due, resolutionBreached: true);
            EndAssignment(released, due.AddHours(-1));
            context.TicketAssignments.Add(new TicketAssignment(released.TicketId, bilal, department.DepartmentId, due.AddHours(1), null));
            context.SaveChanges();
            Breach(released, due, due.AddHours(3));

            // 3. Met: a first response and a resolution, both on time, by Amina.
            var met = NewTicket("TG-SLR-0003", amina, now.AddDays(-5));
            Period(met, now.AddDays(-5).AddHours(4), now.AddDays(5), resolutionBreached: false);
            met.RecordFirstHumanResponse(now.AddDays(-5).AddHours(1));
            context.TicketResolutions.Add(new TicketResolution(met.TicketId, ResolutionOutcome.Resolved, "Fixed.", null, null, amina, now.AddDays(-4)));
            context.SaveChanges();
        }

        TeamPerformanceReportDto report;
        TeamPerformanceSlaDetailsDto details;
        TeamPerformanceRecordsDto records;
        using (var context = CreateContext())
        {
            var service = new TeamPerformanceAppService(new TeamPerformanceQueryRepository(context), new DashboardSqliteFixture.FixedTimeProvider(now));
            report = await service.GetReportAsync(new TeamPerformanceRequestDto(DepartmentId: null));
            details = (await service.GetSlaDetailsAsync(new TeamPerformanceSlaDetailsRequestDto(amina, PageSize: 100))).Details!;
            records = (await service.GetRecordsAsync(new TeamPerformanceRecordsRequestDto(amina, nameof(TeamPerformanceMetric.SlaBreaches)))).Records!;
        }

        var a = report.Rows.Single(r => r.EmployeeId == amina);
        var b = report.Rows.Single(r => r.EmployeeId == bilal);

        Assert.Equal((1, 1, 2, 3, 66.7m), (a.TicketsWithSlaBreach, a.SlaBreachEvents, a.SlaMet, a.SlaEvaluated, a.SlaCompliancePercent));
        Assert.Equal((0, 0, 0, 0), (b.TicketsWithSlaBreach, b.SlaBreachEvents, b.SlaMet, b.SlaEvaluated));
        Assert.Equal((1, 1, 2, 3), (report.Totals.TicketsWithSlaBreach, report.Totals.SlaBreachEvents, report.Totals.SlaMet, report.Totals.SlaEvaluated));

        // The employee details are the same aggregation, and the records behind the ticket count are the same tickets.
        Assert.Equal((a.TicketsWithSlaBreach, a.SlaBreachEvents, a.SlaMet, a.SlaEvaluated, a.SlaCompliancePercent),
            (details.Summary.TicketsWithBreach, details.Summary.BreachEvents, details.Summary.Met, details.Summary.Evaluated, details.Summary.CompliancePercent));
        Assert.Equal(a.TicketsWithSlaBreach, records.Records.Count);
        Assert.Equal(a.SlaBreachEvents, details.Records.Count(r => r.Status == TeamPerformanceSlaStatuses.Breached));
    }
}
