using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
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
using TigerCS.Tests.IdentityAndAccess.Integration;
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
            CrmCustomer(context, 3, "0112345678", inRange);          // a national number that is not UAE-shaped: unknown
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
            // UAE landlines (971 + 8 digits) and national UAE forms
            "041234567", "04 123 4567", "0097141234567", "97141234567", "021234567", "031234567", "061234567", "071234567", "091234567",
            "+971 2 123 4567", "081234567", "0112345678", "+97181234567", "+971112345678", "00 971 4 123 4567", "+0971501234567",
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

    /// <summary>The Unknown breakdown: every Unknown customer has exactly one reason, the reasons sum to the card's Unknown, and the stored number is shown untouched.</summary>
    [VisibilitySqlServerFact]
    public async Task TheUnknownBreakdown_ExplainsEveryUnknownCustomer_SumsToTheCardsUnknown_AndShowsTheStoredNumberUntouched()
    {
        CreateDatabase();
        string?[] unknownPhones =
        [
            null, "0812345678", "08 1234 5678", "081234567", "501234567", "447911123456", "+971412345678", "00971501234", "+1234567",
            "+0441234567", "call me", "0112345678",
        ];
        using (var context = CreateContext())
        {
            for (var i = 0; i < unknownPhones.Length; i++) CrmCustomer(context, 7000 + i, unknownPhones[i]);
            CrmCustomer(context, 7100, "+971501234567");   // placed: not in the breakdown
            CrmCustomer(context, 7101, "+447911123456");   // placed
            var both = CrmCustomer(context, 7200, "+971501234567");   // one customer, a UAE and a foreign number: ambiguous
            LinkIntakeOnNewTicketFor(context, both, "+447911123456");
        }

        var counts = await CountsAsync();
        using var db = CreateContext();
        var service = Service(db);
        var breakdown = await service.UnknownBreakdownAsync(Guid.NewGuid(), CrossDepartment, new CustomerDirectoryListRequestDto());
        Assert.Equal(counts.Unknown, breakdown.Total);
        Assert.Equal(unknownPhones.Length + 1, breakdown.Total);
        Assert.Equal(breakdown.Total, breakdown.Reasons.Sum(r => r.Count));
        Assert.Contains(breakdown.Reasons, r => r.Reason == nameof(CustomerPhoneLocationReason.ConflictingCountries) && r.Count == 1);
        Assert.Contains(breakdown.Reasons, r => r.Reason == nameof(CustomerPhoneLocationReason.Missing) && r.Count == 1);
        Assert.Contains(breakdown.Reasons, r => r.Reason == nameof(CustomerPhoneLocationReason.NationalFormatNoCountryCode) && r.Count == 4);

        var list = await ListAsync(new CustomerDirectoryListRequestDto(Location: "unknown"));
        Assert.Equal(counts.Unknown, list.TotalCount);
        foreach (var row in list.Items)
        {
            Assert.NotNull(row.LocationReason);
            Assert.NotNull(row.LocationReasonText);
            if (row.CustomerKey == "crm:7200") continue;
            var index = int.Parse(row.CustomerKey[4..]) - 7000;
            Assert.Equal(unknownPhones[index], row.LocationPhone);   // exactly as stored
            Assert.Equal(CustomerPhoneLocation.Diagnose(unknownPhones[index]).Reason.ToString(), row.LocationReason);
        }
    }

    private void LinkIntakeOnNewTicketFor(TigerCsDbContext context, Ticket sibling, string phone)
    {
        // A second ticket of the SAME CRM customer (same buyer id) carrying a different number.
        var n = ++_seq;
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-LS-{n:D6}", _departmentId, sibling.CrmBuyerCustomerId!.Value, 1, n, 7, "Customer", "Tiger Tower", $"U-{n}",
            _categoryId, (byte)PriorityLevel.Medium, $"CRM ticket {n}", Now.AddHours(-n));
        context.Tickets.Add(ticket);
        context.SaveChanges();
        LinkIntake(context, ticket, phone);
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

    /// <summary>
    /// A repeat caller: several tickets on one foreign (or UAE) number. The per-customer fold is a SUM; if it is typed <c>int</c> by SQL Server it
    /// overflows (Msg 8115) once a customer has a handful of tickets — and, being evaluated for every customer, it would break the card and BOTH
    /// drill-downs for everyone. SQLite's 64-bit integers can never show this, so only a real SQL Server can.
    /// </summary>
    [VisibilitySqlServerFact]
    public async Task ARepeatCallerWithManyTickets_DoesNotOverflowTheLocationFold_ForTheCardOrEitherDrillDown()
    {
        CreateDatabase();
        var inWindow = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        using (var context = CreateContext())
        {
            for (var i = 0; i < 12; i++) CrmCustomer(context, 1, "+447911123456", inWindow.AddMinutes(i));    // one UK customer, 12 tickets
            for (var i = 0; i < 12; i++) CrmCustomer(context, 2, "+971501234567", inWindow.AddMinutes(i));    // one UAE customer, 12 tickets
            for (var i = 0; i < 4; i++) CrmCustomer(context, 3, i % 2 == 0 ? "+971501234568" : "+447911123457", inWindow.AddMinutes(i));   // ambiguous
            CrmCustomer(context, 4, "+447911123458", inWindow);
        }

        var range = new CustomerDirectoryListRequestDto(DateFrom: new DateOnly(2026, 10, 19), DateTo: new DateOnly(2026, 11, 17));
        var card = await CountsAsync(range);

        Assert.Equal(new CustomerLocationCounts(1, 2, 1), card);
        Assert.Equal(1, (await ListAsync(range with { Location = CustomerLocationKeys.WithinUae, PageSize = 25 })).TotalCount);
        Assert.Equal(2, (await ListAsync(range with { Location = CustomerLocationKeys.OutsideUae, PageSize = 25 })).TotalCount);
        Assert.Equal(1, (await ListAsync(range with { Location = CustomerLocationKeys.Unknown, PageSize = 25 })).TotalCount);
        Assert.Equal(card.Total, (await ListAsync(range with { PageSize = 25 })).TotalCount);
    }

    /// <summary>
    /// The drill-down the Dashboard card opens: <c>/Customers?location=…&amp;dateFrom=…&amp;dateTo=…</c> with the page's DEFAULT page size, over thousands of
    /// customers on a real SQL Server — the request shape the earlier tests (page size 100, small data) never sent.
    /// </summary>
    [VisibilitySqlServerFact]
    public async Task TheDrillDownList_ForWithinAndOutsideUae_WorksOnARealSqlServer_AndItsTotalIsTheCardsCount()
    {
        CreateDatabase();
        const int perBucket = 2000;
        using (var context = CreateContext())
        {
            var tickets = new List<(Ticket Ticket, string Phone)>();
            for (var type = 0; type < 3; type++)
            {
                for (var k = 0; k < perBucket; k++)
                {
                    var phone = type switch { 0 => $"+97150{k:D7}", 1 => $"+4479{k:D8}", _ => $"050{k:D7}" };
                    tickets.Add((Ticket.CreateUnclassified($"TG-LS-{++_seq:D6}", _departmentId, "bulk", Now.AddMinutes(-_seq)), phone));
                }
            }

            context.Tickets.AddRange(tickets.Select(t => t.Ticket));
            context.SaveChanges();
            context.IntakeRecords.AddRange(tickets.Select(t => NewIntake(t.Ticket, t.Phone)));
            context.SaveChanges();
        }

        var range = new CustomerDirectoryListRequestDto(DateFrom: new DateOnly(2026, 1, 1), DateTo: new DateOnly(2026, 12, 31));
        var card = await CountsAsync(range);

        foreach (var (location, expected) in new[] { (CustomerLocationKeys.WithinUae, card.WithinUae), (CustomerLocationKeys.OutsideUae, card.OutsideUae), (CustomerLocationKeys.Unknown, card.Unknown) })
        {
            var watch = Stopwatch.StartNew();
            using var context = CreateContext();
            // PageSize 25 is what /Customers sends (CustomerDirectoryAppService.DefaultPageSize); Page 1 and a later page both execute.
            var first = await Service(context).ListAsync(Guid.NewGuid(), CrossDepartment, range with { Location = location, PageSize = 25 });
            var third = await Service(context).ListAsync(Guid.NewGuid(), CrossDepartment, range with { Location = location, PageSize = 25, Page = 3 });
            watch.Stop();

            Assert.Equal(expected, first.TotalCount);
            Assert.Equal(25, first.Items.Count);
            Assert.Equal(25, third.Items.Count);
            Assert.Empty(first.Items.Select(i => i.CustomerKey).Intersect(third.Items.Select(i => i.CustomerKey)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{location}: the list took {watch.Elapsed}.");
        }

        // The plain directory (no location, no dashboard filters) still works and counts everyone.
        using (var context = CreateContext())
        {
            var plain = await Service(context).ListAsync(Guid.NewGuid(), CrossDepartment, new CustomerDirectoryListRequestDto());
            Assert.Equal(card.Total, plain.TotalCount);
            Assert.Equal(CustomerDirectoryAppService.DefaultPageSize, plain.Items.Count);
        }
    }

    /// <summary>
    /// The drill-down end to end through the REAL HTTP API (routing, model binding, authorization, controller, application service, EF, SQL Server):
    /// the very URL the Dashboard card links to, as an authenticated supervisor. A failing response fails the test WITH its body, so the actual
    /// server-side error is what the report shows.
    /// </summary>
    [VisibilitySqlServerFact]
    public async Task TheCustomersApi_ServesTheDrillDownsAndThePlainList_AndTheirTotalsAreTheDashboardCardsCounts()
    {
        CreateDatabase();
        var inWindow = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        using (var context = CreateContext())
        {
            for (var i = 0; i < 3; i++) CrmCustomer(context, 100 + i, $"+97150123456{i}", inWindow);        // Within UAE
            for (var i = 0; i < 2; i++) CrmCustomer(context, 200 + i, $"+4479111234{50 + i}", inWindow);     // Outside UAE
            CrmCustomer(context, 300, "0112345678", inWindow);                                                // Unknown
        }

        using var factory = new TigerCsApiFactory { SqlServerConnectionString = ConnectionString };
        var (username, password, _) = await factory.SeedEmployeeAsync(Roles.CsSupervisor);
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponseDto>())!.AccessToken);

        async Task<T> GetAsync<T>(string url)
        {
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {body}");
            return System.Text.Json.JsonSerializer.Deserialize<T>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        }

        const string period = "dateFrom=2026-10-19&dateTo=2026-11-17";
        var card = await GetAsync<DashboardCustomerLocationDto>($"/api/dashboard/customers-by-location?{period}");
        Assert.Equal((3, 2, 1, 6), (card.WithinUae, card.OutsideUae, card.Unknown, card.Total));

        foreach (var (location, expected) in new[] { ("within-uae", card.WithinUae), ("outside-uae", card.OutsideUae), ("unknown", card.Unknown) })
        {
            var list = await GetAsync<CustomerDirectoryListResultDto>($"/api/customers?location={location}&{period}&page=1&pageSize=25");
            Assert.Equal(expected, list.TotalCount);
            Assert.Equal(expected, list.Items.Count);
        }

        var plain = await GetAsync<CustomerDirectoryListResultDto>("/api/customers?page=1&pageSize=25");
        Assert.Equal(card.Total, plain.TotalCount);
    }

    private IntakeRecord NewIntake(Ticket ticket, string phone)
    {
        var intake = new IntakeRecord(1, phone, _departmentId, false, null, null, _creator, ticket.CreatedAtUtc.AddMinutes(-5));
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
        return intake;
    }
}
