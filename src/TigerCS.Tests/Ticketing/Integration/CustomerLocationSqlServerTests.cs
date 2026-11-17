using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
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
/// The Customers by Location aggregate on a REAL SQL Server (the migrated schema, the production provider), because the SQLite and in-memory
/// providers translate this query differently — the SQL Server branch of the phone classification (<c>LIKE '%[^0-9]%'</c>) runs nowhere else.
/// Proves, over real rows: distinct customer identity (CRM id / external id / canonical phone), the three mutually exclusive buckets, the SQL
/// classification agreeing with <see cref="CustomerPhoneLocation.Classify"/>, the date range, the permission scopes, count == list, and that the
/// query stays fast over thousands of customers. A throw-away database is created and dropped per test.
/// </summary>
public sealed class CustomerLocationSqlServerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];

    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsLocationTest_{Guid.NewGuid():N}";
    private int _seq;
    private int _departmentId, _collectionsId, _categoryId;
    private Guid _creator;

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database, Pooling = true, MaxPoolSize = 50 }.ConnectionString;

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

    private void CreateDatabase()
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
        var cs = new Department("Customer Service", "CSV");
        var collections = new Department("Collections", "COLV");
        context.Departments.AddRange(cs, collections);
        context.SaveChanges();
        _departmentId = cs.DepartmentId;
        _collectionsId = collections.DepartmentId;
        foreach (var priority in new[] { new Priority((byte)PriorityLevel.Medium, "Medium", 3) })
        {
            if (!context.Priorities.Any(p => p.PriorityId == priority.PriorityId)) context.Priorities.Add(priority);
        }

        var category = new Category("General Inquiry", cs.DepartmentId);
        context.Categories.Add(category);
        _creator = AddPerson(context, "Intake Creator");
        context.SaveChanges();
        _categoryId = category.CategoryId;
        if (!context.Channels.Any(c => c.ChannelId == 1)) throw new InvalidOperationException("The migrated schema is expected to seed channel 1.");
    }

    private static Guid AddPerson(TigerCsDbContext context, string name)
    {
        var id = Guid.NewGuid();
        context.Users.Add(new ApplicationUser { Id = id, UserName = $"u-{id:N}", NormalizedUserName = $"U-{id:N}".ToUpperInvariant(), Email = $"{id:N}@example.test", SecurityStamp = Guid.NewGuid().ToString() });
        context.Employees.Add(new Employee(id, name, isGeynessStaff: false, Now.AddDays(-60)));
        return id;
    }

    private CustomerDirectoryAppService Service(TigerCsDbContext context)
    {
        var time = new DashboardSqliteFixture.FixedTimeProvider(Now);
        var queries = new TicketQueryAppService(
            new TicketRepository(context), new UserDepartmentAssignmentRepository(context), new FakeTicketResolutionRepository(),
            new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, time);
        return new CustomerDirectoryAppService(new CustomerDirectoryRepository(context, ReopenPolicy.Default, time), queries);
    }

    private async Task<CustomerLocationCounts> CountsAsync(CustomerDirectoryListRequestDto? request = null, string[]? roles = null, Guid? caller = null)
    {
        using var context = CreateContext();
        return await Service(context).CountByLocationAsync(caller ?? Guid.NewGuid(), roles ?? CrossDepartment, request ?? new CustomerDirectoryListRequestDto());
    }

    private async Task<CustomerDirectoryListResultDto> ListAsync(CustomerDirectoryListRequestDto request, string[]? roles = null, Guid? caller = null)
    {
        using var context = CreateContext();
        return await Service(context).ListAsync(caller ?? Guid.NewGuid(), roles ?? CrossDepartment, request with { PageSize = 100 });
    }

    private Ticket CrmCustomer(TigerCsDbContext context, int crmId, string? phone, DateTime? createdAtUtc = null, int? departmentId = null)
    {
        var n = ++_seq;
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-LS-{n:D6}", departmentId ?? _departmentId, crmId, crmId * 10, n, 7, $"Customer {crmId}", "Tiger Tower", $"U-{n}",
            _categoryId, (byte)PriorityLevel.Medium, $"CRM ticket {n}", createdAtUtc ?? Now.AddHours(-n));
        context.Tickets.Add(ticket);
        context.SaveChanges();
        if (phone is not null) LinkIntake(context, ticket, phone);
        return ticket;
    }

    private void LinkIntake(TigerCsDbContext context, Ticket ticket, string phone)
    {
        var intake = new IntakeRecord(1, phone, _departmentId, false, null, null, _creator, ticket.CreatedAtUtc.AddMinutes(-5));
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
        context.IntakeRecords.Add(intake);
        context.SaveChanges();
    }

    private Ticket PhoneCustomer(TigerCsDbContext context, string phone, DateTime? createdAtUtc = null)
    {
        var n = ++_seq;
        var ticket = Ticket.CreateUnclassified($"TG-LS-{n:D6}", _departmentId, $"Phone ticket {n}", createdAtUtc ?? Now.AddHours(-n));
        context.Tickets.Add(ticket);
        context.SaveChanges();
        LinkIntake(context, ticket, phone);
        return ticket;
    }

    [VisibilitySqlServerFact]
    public async Task DistinctCustomers_AreBucketedOnce_CountEqualsTheList_AndTheDateRangeAndDepartmentApply()
    {
        CreateDatabase();
        var inRange = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
        using (var context = CreateContext())
        {
            CrmCustomer(context, 1, "+971501234567", inRange);       // one UAE customer, three tickets, three spellings
            CrmCustomer(context, 1, "971501234567", inRange.AddHours(1));
            CrmCustomer(context, 1, "+971 50 123 4567", inRange.AddHours(2));
            CrmCustomer(context, 2, "+447911123456", inRange);       // outside
            CrmCustomer(context, 3, "0501234567", inRange);          // national form: unknown
            CrmCustomer(context, 4, "+971501234568", inRange);       // UAE and UK number on one customer: ambiguous -> unknown
            CrmCustomer(context, 4, "+447911123457", inRange);
            PhoneCustomer(context, "+971521234567", inRange);        // phone-identified customer, two spellings: ONE customer
            PhoneCustomer(context, "971521234567", inRange.AddHours(1));
            CrmCustomer(context, 7, null, inRange);                  // no phone anywhere: unknown
            CrmCustomer(context, 9, null, inRange);
            context.TicketInteractions.Add(TicketInteraction.CreateLocal(context.Tickets.OrderBy(t => t.TicketId).Last().TicketId, WellKnownChannels.Phone, "+966501234567", inRange, isOriginatingInteraction: true));   // the interaction phone is the fallback
            context.SaveChanges();
            CrmCustomer(context, 10, "+971501234569", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));   // outside the date range
            CrmCustomer(context, 11, "+971501234570", inRange, departmentId: _collectionsId);
        }

        var range = new CustomerDirectoryListRequestDto(DateFrom: new DateOnly(2026, 9, 1), DateTo: new DateOnly(2026, 9, 10));
        var counts = await CountsAsync(range);

        // Within: crm 1 + phone customer + crm 11 = 3 | Outside: crm 2 + crm 9 (interaction fallback) = 2 | Unknown: crm 3, crm 4 (conflict), crm 7 = 3
        Assert.Equal(new CustomerLocationCounts(3, 2, 3), counts);
        Assert.Equal(8, counts.Total);

        // Count == list, for every bucket and for the whole set.
        Assert.Equal(counts.Total, (await ListAsync(range)).TotalCount);
        Assert.Equal(counts.WithinUae, (await ListAsync(range with { Location = CustomerLocationKeys.WithinUae })).TotalCount);
        Assert.Equal(counts.OutsideUae, (await ListAsync(range with { Location = CustomerLocationKeys.OutsideUae })).TotalCount);
        Assert.Equal(counts.Unknown, (await ListAsync(range with { Location = CustomerLocationKeys.Unknown })).TotalCount);

        // A customer is classified on ALL of their visible tickets, so the range never flips a bucket; crm 10 simply is not in the range.
        Assert.Equal(new CustomerLocationCounts(4, 2, 3), await CountsAsync());   // no range: crm 10 joins Within

        // The department filter selects customers with a ticket there.
        Assert.Equal(new CustomerLocationCounts(1, 0, 0), await CountsAsync(range with { DepartmentId = _collectionsId }));
    }

    [VisibilitySqlServerFact]
    public async Task Permissions_AnAssignmentOnlyEmployeeAndARestrictedAgentCountOnlyTheirOwnCustomers_AndAFilterNeverWidensTheScope()
    {
        CreateDatabase();
        Guid employee, agent;
        using (var context = CreateContext())
        {
            employee = AddPerson(context, "Department Employee");
            agent = AddPerson(context, "CS Agent");
            context.SaveChanges();
            var csAgentRole = context.Roles.FirstOrDefault(r => r.Name == Roles.CsAgent);
            if (csAgentRole is null)
            {
                csAgentRole = new ApplicationRole(Roles.CsAgent, "CS Agent") { Id = Guid.NewGuid(), NormalizedName = Roles.CsAgent.ToUpperInvariant() };
                context.Roles.Add(csAgentRole);
                context.SaveChanges();
            }

            context.UserRoles.Add(new IdentityUserRole<Guid> { UserId = agent, RoleId = csAgentRole.Id });
            context.SaveChanges();

            var mine = CrmCustomer(context, 1, "+971501234567");
            mine.AssignTo(employee);
            CrmCustomer(context, 2, "+447911123456");                                   // same department, not assigned to the employee
            var agents = CrmCustomer(context, 3, "+447911123457");
            agents.AssignTo(agent);
            CrmCustomer(context, 4, "+971501234568", departmentId: _collectionsId);     // another department
            context.SaveChanges();
        }

        Assert.Equal(new CustomerLocationCounts(1, 0, 0), await CountsAsync(roles: [Roles.DepartmentEmployee], caller: employee));
        Assert.Equal(new CustomerLocationCounts(0, 1, 0), await CountsAsync(roles: [Roles.CsAgent], caller: agent));
        Assert.Equal(new CustomerLocationCounts(2, 2, 0), await CountsAsync());

        // Asking for a department the caller cannot see yields nobody: the filter narrows, never widens.
        var outsideScope = new CustomerDirectoryListRequestDto(DateFrom: new DateOnly(2026, 1, 1), DateTo: new DateOnly(2026, 12, 31), DepartmentId: _collectionsId);
        Assert.Equal(new CustomerLocationCounts(0, 0, 0), await CountsAsync(outsideScope, [Roles.DepartmentEmployee], employee));

        // And the list the employee opens from the card is exactly what the card counted.
        Assert.Equal(1, (await ListAsync(new CustomerDirectoryListRequestDto(Location: CustomerLocationKeys.WithinUae), [Roles.DepartmentEmployee], employee)).TotalCount);
        Assert.Equal(0, (await ListAsync(new CustomerDirectoryListRequestDto(Location: CustomerLocationKeys.OutsideUae), [Roles.DepartmentEmployee], employee)).TotalCount);
    }

    /// <summary>The SQL Server branch of the classification (character-class LIKE, flat pattern tests) must place every number where the C# rule does.</summary>
    [VisibilitySqlServerFact]
    public async Task SqlServerClassification_AgreesWithTheDomainClassifier_ForEveryNumberShape()
    {
        CreateDatabase();
        string[] samples =
        [
            "+971501234567", "971501234567", "+971 50 123 4567", "+971-50-123-4567", "(+971) 50.123.4567", "00971501234567", "00 971 50 123 4567",
            "  +971501234567  ", "+971412345678", "+97141234567", "+97150123456", "+9715012345678", "0501234567", "501234567", "447911123456",
            "+447911123456", "0044 7911 123456", "+14155552671", "+1 (415) 555-2671", "+919876543210", "+966501234567", "+971501234567 ext 12", "call me",
            "+0441234567", "+1234567", "+1234567890123456", "00", "+", "+12345678", "+123456789012345", "+00971501234567", "000971501234567",
            "+٩٧١٥٠١٢٣٤٥٦٧", "+971501234567x", "++971501234567", "+971/50/123/4567",
        ];

        using (var context = CreateContext())
        {
            for (var i = 0; i < samples.Length; i++) CrmCustomer(context, 5000 + i, samples[i]);
        }

        foreach (var location in new[] { CustomerLocation.WithinUae, CustomerLocation.OutsideUae })
        {
            var expected = samples.Select((p, i) => (p, key: $"crm:{5000 + i}")).Where(x => CustomerPhoneLocation.Classify(x.p) == location).Select(x => x.key).Order().ToArray();
            var actual = (await ListAsync(new CustomerDirectoryListRequestDto(Location: CustomerLocationKeys.ToKey(location)))).Items.Select(r => r.CustomerKey).Order().ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }

        var counts = await CountsAsync();
        Assert.Equal(samples.Count(p => CustomerPhoneLocation.Classify(p) == CustomerLocation.WithinUae), counts.WithinUae);
        Assert.Equal(samples.Count(p => CustomerPhoneLocation.Classify(p) == CustomerLocation.OutsideUae), counts.OutsideUae);
        Assert.Equal(samples.Count(p => CustomerPhoneLocation.Classify(p) == CustomerLocation.Unknown), counts.Unknown);
        Assert.Equal(samples.Length, counts.Total);
    }

    /// <summary>The query must stay cheap over thousands of customers (EF generated one grouped statement; nothing runs per customer).</summary>
    [VisibilitySqlServerFact]
    public async Task OverThousandsOfCustomers_TheCountIsExactAndFast_ForAnEmployeeScopeToo()
    {
        CreateDatabase();
        const int perBucket = 2000;
        using (var context = CreateContext())
        {
            // Set-style seeding: all tickets in one save (EF batches the INSERTs and reads the generated ids back), then all intakes in one save.
            var tickets = new List<(Ticket Ticket, string Phone)>();
            for (var type = 0; type < 3; type++)
            {
                for (var k = 0; k < perBucket; k++)
                {
                    var phone = type switch
                    {
                        0 => $"+97150{k:D7}",     // 12 digits: UAE
                        1 => $"+4479{k:D8}",      // UK
                        _ => $"050{k:D7}",        // national form: unknown
                    };
                    tickets.Add((Ticket.CreateUnclassified($"TG-LS-{++_seq:D6}", _departmentId, "bulk", Now.AddMinutes(-_seq)), phone));
                }
            }

            context.Tickets.AddRange(tickets.Select(t => t.Ticket));
            context.SaveChanges();
            context.IntakeRecords.AddRange(tickets.Select(t => NewIntake(t.Ticket, t.Phone)));
            context.SaveChanges();
        }

        var watch = Stopwatch.StartNew();
        var counts = await CountsAsync(new CustomerDirectoryListRequestDto(DateFrom: new DateOnly(2026, 1, 1), DateTo: new DateOnly(2026, 12, 31)));
        watch.Stop();

        Assert.Equal(new CustomerLocationCounts(perBucket, perBucket, perBucket), counts);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"The count took {watch.Elapsed}.");
    }

    private IntakeRecord NewIntake(Ticket ticket, string phone)
    {
        var intake = new IntakeRecord(1, phone, _departmentId, false, null, null, _creator, ticket.CreatedAtUtc.AddMinutes(-5));
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
        return intake;
    }
}
