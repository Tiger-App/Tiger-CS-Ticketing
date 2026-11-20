using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The Dashboard's Customers by Location card, proven as SQL on the real EF Core schema: distinct customers (never tickets) placed by
/// their phone's country code, mutually exclusive, scoped exactly like the Customers directory, and — the point of the feature — every
/// count equal to the length of the list its link opens.
/// </summary>
public sealed class CustomerLocationSqliteTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];
    private readonly DashboardSqliteFixture _db = new();
    private int _sequence = 900;

    public void Dispose() => _db.Dispose();

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
        using var context = _db.CreateContext();
        return await Service(context).CountByLocationAsync(caller ?? Guid.NewGuid(), roles ?? CrossDepartment, request ?? new CustomerDirectoryListRequestDto());
    }

    private async Task<CustomerDirectoryListResultDto> ListAsync(CustomerDirectoryListRequestDto request, string[]? roles = null, Guid? caller = null)
    {
        using var context = _db.CreateContext();
        return await Service(context).ListAsync(caller ?? Guid.NewGuid(), roles ?? CrossDepartment, request with { PageSize = 100 });
    }

    /// <summary>A CRM-identified customer whose phone is only on the intake — CRM tickets carry no phone of their own.</summary>
    private Ticket CrmCustomer(TigerCsDbContext context, int crmId, string? phone, DateTime? createdAtUtc = null, int? departmentId = null)
    {
        var seq = ++_sequence;
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-LOC-{seq:D5}", departmentId ?? _db.CustomerServiceId, crmId, crmId * 10, seq, 7, $"Customer {crmId}", "Tiger Tower", $"U-{seq}",
            _db.CsCategoryId, (byte)PriorityLevel.Medium, $"CRM ticket {seq}", createdAtUtc ?? Now.AddHours(-seq));
        context.Tickets.Add(ticket);
        context.SaveChanges();
        if (phone is not null)
        {
            var intake = new IntakeRecord(1, phone, _db.CustomerServiceId, false, null, null, _db.CsAgentId, ticket.CreatedAtUtc.AddMinutes(-5));
            intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
            context.IntakeRecords.Add(intake);
            context.SaveChanges();
        }

        return ticket;
    }

    private Ticket PhoneCustomer(TigerCsDbContext context, string phone, DateTime? createdAtUtc = null)
    {
        var seq = ++_sequence;
        var ticket = Ticket.CreateUnverified(
            $"TG-PHL-{seq:D5}", _db.CustomerServiceId, _db.CsCategoryId, (byte)PriorityLevel.Low, $"Phone ticket {seq}", createdAtUtc ?? Now.AddHours(-seq), null, null);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        var intake = new IntakeRecord(1, phone, _db.CustomerServiceId, false, null, null, _db.CsAgentId, ticket.CreatedAtUtc.AddMinutes(-5));
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
        context.IntakeRecords.Add(intake);
        context.SaveChanges();
        return ticket;
    }

    [Fact]
    public async Task CountsDistinctCustomers_NotTickets_AndTheThreeBucketsAreMutuallyExclusive()
    {
        using (var context = _db.CreateContext())
        {
            // One UAE customer with THREE tickets, written three ways: still one customer.
            CrmCustomer(context, 1, "+971501234567");
            CrmCustomer(context, 1, "971501234567");
            CrmCustomer(context, 1, "+971 50 123 4567");
            CrmCustomer(context, 2, "+447911123456");        // outside
            CrmCustomer(context, 3, "+14155552671");         // outside
            CrmCustomer(context, 4, "0112345678");           // a national number that is not a UAE mobile / landline: unknown
            CrmCustomer(context, 5, null);                   // no phone: unknown
            PhoneCustomer(context, "+971521234567");         // phone-identified UAE customer
            PhoneCustomer(context, "971521234567");          // the SAME customer, written differently
        }

        var counts = await CountsAsync();

        Assert.Equal(2, counts.WithinUae);      // crm:1 and the phone customer
        Assert.Equal(2, counts.OutsideUae);
        Assert.Equal(2, counts.Unknown);
        Assert.Equal(6, counts.Total);

        // Total unique customers equals the Customers list for the same query: every customer is in exactly one bucket.
        Assert.Equal(counts.Total, (await ListAsync(new CustomerDirectoryListRequestDto())).TotalCount);
    }

    [Fact]
    public async Task UaeLandlines_AreCountedWithinUae_InEveryWrittenForm_ForEveryAreaCode()
    {
        using (var context = _db.CreateContext())
        {
            // 02 Abu Dhabi, 03 Al Ain, 04 Dubai, 06 Sharjah/Ajman/UAQ, 07 Ras Al Khaimah, 09 Fujairah - each written locally, with +971 and with 00971.
            var n = 0;
            foreach (var number in new[]
                     {
                         "021234567", "+97121234567", "0097121234567", "031234567", "+97131234567", "0097131234567", "041234567", "+97141234567", "0097141234567",
                         "061234567", "+97161234567", "0097161234567", "071234567", "+97171234567", "0097171234567", "091234567", "+97191234567", "0097191234567",
                     })
            {
                CrmCustomer(context, 7000 + n++, number);
            }

            CrmCustomer(context, 7100, "+447911123456");   // outside
            CrmCustomer(context, 7101, "081234567");       // 08 is not a UAE area code: not guessed
            CrmCustomer(context, 7102, "+971412345678");   // 971 + 9 digits starting 4 is neither a mobile nor a landline
        }

        var counts = await CountsAsync();

        Assert.Equal(new CustomerLocationCounts(18, 1, 2), counts);
        var within = await ListAsync(new CustomerDirectoryListRequestDto(Location: CustomerLocationKeys.WithinUae));
        Assert.Equal(18, within.TotalCount);                                       // the list a card opens agrees with the card
        Assert.Equal(counts.Total, (await ListAsync(new CustomerDirectoryListRequestDto())).TotalCount);
    }

    [Fact]
    public async Task ACustomersCountryOfResidence_NeverMovesThemBetweenLocationBuckets_OnlyTheirPhoneNumberDoes()
    {
        // Location is the country of the PHONE number. A customer with a UAE number who says they live in India is still a UAE number,
        // and one with a UK number who lives in Dubai is still a UK number: residence is a separate fact, never inferred from or into a phone.
        using (var context = _db.CreateContext())
        {
            CrmCustomer(context, 8001, "+97141234567");     // a Dubai landline
            CrmCustomer(context, 8002, "+447911123456");    // a UK mobile
            context.SaveChanges();
        }

        Assert.Equal(new CustomerLocationCounts(1, 1, 0), await CountsAsync());

        using (var context = _db.CreateContext())
        {
            var admin = Guid.NewGuid();
            foreach (var (key, crmId, country, emirate) in new[] { ("crm:8001", 8001, "India", (string?)null), ("crm:8002", 8002, "United Arab Emirates", "Dubai") })
            {
                var edit = new CustomerProfileOverride(key, 1, crmId, null, null, null, admin, Now);
                edit.SetDetails(null, null, null, null, "Anything", country, emirate, "Somewhere", null, admin, Now);
                context.CustomerProfileOverrides.Add(edit);
            }

            context.SaveChanges();
        }

        Assert.Equal(new CustomerLocationCounts(1, 1, 0), await CountsAsync());   // nothing moved
        var profile = await new CustomerDirectoryAppServiceFactory(this).ProfileAsync("crm:8002");
        Assert.Equal("Dubai", profile.Details!.Emirate);                           // what the customer said is kept as stated...
        Assert.Equal("United Arab Emirates", profile.Details.Country);             // ...and the phone-derived bucket is unaffected
    }

    /// <summary>Opens a profile through the real service on the SQLite schema.</summary>
    private sealed class CustomerDirectoryAppServiceFactory(CustomerLocationSqliteTests owner)
    {
        public async Task<CustomerDirectoryProfileDto> ProfileAsync(string key)
        {
            using var context = owner._db.CreateContext();
            var result = await owner.Service(context).GetProfileAsync(Guid.NewGuid(), CrossDepartment, key);
            return Assert.IsType<CustomerDirectoryProfileDto>(result.Response);
        }
    }

    [Fact]
    public async Task ConflictingNumbers_MakeTheCustomerUnknown_NeverCountedTwice()
    {
        using (var context = _db.CreateContext())
        {
            CrmCustomer(context, 10, "+971501234567");
            CrmCustomer(context, 10, "+447911123456");   // same customer, a UAE and a UK number: ambiguous
            CrmCustomer(context, 11, "+971501234567");
            CrmCustomer(context, 11, "0112345678");      // an unplaceable number adds no evidence
        }

        var counts = await CountsAsync();

        Assert.Equal(new CustomerLocationCounts(1, 0, 1), counts);
    }

    [Fact]
    public async Task TheTicketsFirstInteractionPhone_IsUsedWhenThereIsNoIntake()
    {
        using (var context = _db.CreateContext())
        {
            var ticket = CrmCustomer(context, 20, phone: null);
            context.TicketInteractions.Add(TicketInteraction.CreateLocal(ticket.TicketId, WellKnownChannels.Phone, "+966501234567", Now, isOriginatingInteraction: true));
            context.SaveChanges();
        }

        Assert.Equal(new CustomerLocationCounts(0, 1, 0), await CountsAsync());
    }

    [Fact]
    public async Task EveryCountEqualsTheListItsLinkOpens_ForEachLocation()
    {
        using (var context = _db.CreateContext())
        {
            for (var i = 0; i < 7; i++) CrmCustomer(context, 100 + i, $"+97150123456{i}");
            for (var i = 0; i < 4; i++) CrmCustomer(context, 200 + i, $"+4479111234{50 + i}");
            for (var i = 0; i < 3; i++) CrmCustomer(context, 300 + i, $"01123456{60 + i}");   // national, not UAE-shaped: Unknown
            CrmCustomer(context, 400, null);
        }

        var counts = await CountsAsync();
        Assert.Equal(new CustomerLocationCounts(7, 4, 4), counts);

        foreach (var (location, expected) in new[] { (CustomerLocationKeys.WithinUae, 7), (CustomerLocationKeys.OutsideUae, 4), (CustomerLocationKeys.Unknown, 4) })
        {
            var list = await ListAsync(new CustomerDirectoryListRequestDto(Location: location));
            Assert.Equal(expected, list.TotalCount);
            Assert.Equal(expected, list.Items.Count);
        }

        // The three lists partition the whole directory: no customer on two of them, none on none.
        var keys = new List<string>();
        foreach (var location in new[] { CustomerLocationKeys.WithinUae, CustomerLocationKeys.OutsideUae, CustomerLocationKeys.Unknown })
        {
            keys.AddRange((await ListAsync(new CustomerDirectoryListRequestDto(Location: location))).Items.Select(r => r.CustomerKey));
        }

        Assert.Equal(counts.Total, keys.Distinct().Count());
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task DashboardDateRangeAndFilters_SelectCustomersIdenticallyForTheCountAndTheList()
    {
        var inRange = new DateOnly(2026, 9, 5);
        using (var context = _db.CreateContext())
        {
            CrmCustomer(context, 1, "+971501234567", createdAtUtc: new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc));   // 14:00 UAE
            CrmCustomer(context, 2, "+447911123456", createdAtUtc: new DateTime(2026, 9, 5, 19, 59, 0, DateTimeKind.Utc));   // 23:59 UAE: last minute of the last UAE day
            CrmCustomer(context, 3, "+971501234568", createdAtUtc: new DateTime(2026, 9, 5, 20, 0, 0, DateTimeKind.Utc));    // 00:00 UAE on the 6th: day after, out
            CrmCustomer(context, 4, "+971501234569", createdAtUtc: new DateTime(2026, 9, 4, 19, 59, 0, DateTimeKind.Utc));  // 23:59 UAE on the 4th: day before the range, out
            CrmCustomer(context, 5, "+971501234570", createdAtUtc: new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc), departmentId: _db.CollectionsId);
        }

        var day = new CustomerDirectoryListRequestDto(DateFrom: inRange, DateTo: inRange);
        Assert.Equal(new CustomerLocationCounts(2, 1, 0), await CountsAsync(day));

        var inDepartment = day with { DepartmentId = _db.CollectionsId };
        Assert.Equal(new CustomerLocationCounts(1, 0, 0), await CountsAsync(inDepartment));
        Assert.Equal(1, (await ListAsync(inDepartment with { Location = CustomerLocationKeys.WithinUae })).TotalCount);

        foreach (var location in new[] { CustomerLocationKeys.WithinUae, CustomerLocationKeys.OutsideUae })
        {
            var expected = location == CustomerLocationKeys.WithinUae ? 2 : 1;
            Assert.Equal(expected, (await ListAsync(day with { Location = location })).TotalCount);
        }

        // A customer is classified on ALL of their visible tickets, not just the ones in range, so the range never flips a customer's bucket.
        using (var context = _db.CreateContext())
        {
            CrmCustomer(context, 1, "+447911123456", createdAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));   // an older UK number for crm:1
        }

        Assert.Equal(new CustomerLocationCounts(1, 1, 1), await CountsAsync(day));
    }

    [Fact]
    public async Task Visibility_IsTheDirectorysOwn_ADepartmentEmployeeCountsOnlyTheirAssignedTicketsCustomers_AndAFilterNeverWidensIt()
    {
        using (var context = _db.CreateContext())
        {
            var mine = CrmCustomer(context, 1, "+971501234567", departmentId: _db.RegistrationId);
            mine.AssignTo(_db.RegistrationEmployeeId);
            CrmCustomer(context, 2, "+447911123456", departmentId: _db.RegistrationId);    // own department but not assigned to them: assignment-only role
            CrmCustomer(context, 3, "+971501234568", departmentId: _db.CollectionsId);
            context.SaveChanges();
        }

        var employee = await CountsAsync(roles: [Roles.DepartmentEmployee], caller: _db.RegistrationEmployeeId);
        Assert.Equal(new CustomerLocationCounts(1, 0, 0), employee);

        // Asking for another department yields nobody: the filter narrows the scope, never widens it.
        var other = await CountsAsync(new CustomerDirectoryListRequestDto(DepartmentId: _db.CollectionsId, DateFrom: new DateOnly(2026, 1, 1), DateTo: new DateOnly(2026, 12, 31)),
            roles: [Roles.DepartmentEmployee], caller: _db.RegistrationEmployeeId);
        Assert.Equal(new CustomerLocationCounts(0, 0, 0), other);

        // A cross-department role sees all three.
        Assert.Equal(new CustomerLocationCounts(2, 1, 0), await CountsAsync());

        // And the list a restricted viewer opens from the card is exactly their count.
        Assert.Equal(1, (await ListAsync(new CustomerDirectoryListRequestDto(Location: CustomerLocationKeys.WithinUae), [Roles.DepartmentEmployee], _db.RegistrationEmployeeId)).TotalCount);
        Assert.Equal(0, (await ListAsync(new CustomerDirectoryListRequestDto(Location: CustomerLocationKeys.OutsideUae), [Roles.DepartmentEmployee], _db.RegistrationEmployeeId)).TotalCount);
    }

    /// <summary>The SQL mirror of <see cref="CustomerPhoneLocation.Classify"/> must place every number exactly where the C# authority does.</summary>
    [Fact]
    public async Task SqlClassification_AgreesWithTheDomainClassifier_ForEveryNumberShape()
    {
        string[] samples =
        [
            "+971501234567", "971501234567", "+971 50 123 4567", "+971-50-123-4567", "(+971) 50.123.4567", "00971501234567", "00 971 50 123 4567",
            "  +971501234567  ", "+971412345678", "+97141234567", "+97150123456", "+9715012345678", "0501234567", "501234567", "447911123456",
            "041234567", "04 123 4567", "0097141234567", "97141234567", "021234567", "031234567", "061234567", "071234567", "091234567",
            "+971 2 123 4567", "081234567", "0112345678", "+97181234567", "+971112345678", "00 971 4 123 4567",
            "+447911123456", "0044 7911 123456", "+14155552671", "+1 (415) 555-2671", "+919876543210", "+966501234567", "+971501234567 ext 12", "call me",
            "+0441234567", "+1234567", "+1234567890123456", "00", "+", "+12345678", "+123456789012345",
        ];

        using (var context = _db.CreateContext())
        {
            for (var i = 0; i < samples.Length; i++) CrmCustomer(context, 5000 + i, samples[i]);
        }

        foreach (var location in new[] { CustomerLocation.WithinUae, CustomerLocation.OutsideUae })
        {
            var expected = samples.Select((p, i) => (p, key: $"crm:{5000 + i}")).Where(x => CustomerPhoneLocation.Classify(x.p) == location).Select(x => x.key).Order().ToArray();
            var actual = (await ListAsync(new CustomerDirectoryListRequestDto(Location: CustomerLocationKeys.ToKey(location)))).Items.Select(r => r.CustomerKey).Order().ToArray();
            Assert.Equal(expected, actual);
            Assert.NotEmpty(expected);
        }

        var unknownExpected = samples.Count(p => CustomerPhoneLocation.Classify(p) == CustomerLocation.Unknown);
        var counts = await CountsAsync();
        Assert.Equal(unknownExpected, counts.Unknown);
        Assert.Equal(samples.Length, counts.Total);
    }

    [Fact]
    public async Task TheCard_HasPercentagesOfTheTotal_AndTheSameNumbersAsTheCustomersList()
    {
        using (var context = _db.CreateContext())
        {
            var inRange = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 3; i++) CrmCustomer(context, 700 + i, $"+97150123456{i}", inRange);
            CrmCustomer(context, 710, "+447911123456", inRange);
            CrmCustomer(context, 711, null, inRange);
        }

        using var scope = _db.CreateContext();
        var request = new DashboardOverviewRequestDto(DateFrom: new DateOnly(2026, 9, 1), DateTo: new DateOnly(2026, 9, 10));
        var card = (await _db.CreateService(scope).GetCustomerLocationAsync(Guid.NewGuid(), CrossDepartment, request))!;
        Assert.Equal((3, 1, 1, 5), (card.WithinUae, card.OutsideUae, card.Unknown, card.Total));
        Assert.Equal((60.0, 20.0, 20.0), (card.WithinUaePercentage, card.OutsideUaePercentage, card.UnknownPercentage));

        // The card's request, replayed against the directory with the applied filters, returns the same number of customers.
        var list = await ListAsync(new CustomerDirectoryListRequestDto(DateFrom: request.DateFrom, DateTo: request.DateTo, Location: CustomerLocationKeys.WithinUae));
        Assert.Equal(card.WithinUae, list.TotalCount);
    }

    [Fact]
    public async Task AnEmptyScope_YieldsZeroEverywhere_WithoutDividingByZero()
    {
        using var scope = _db.CreateContext();
        var card = await _db.CreateService(scope).GetCustomerLocationAsync(Guid.NewGuid(), CrossDepartment, new DashboardOverviewRequestDto());

        Assert.Equal(new DashboardCustomerLocationDto(0, 0, 0, 0, 0, 0, 0), card);
    }
}
