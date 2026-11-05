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
/// The Customers directory, proven as SQL on the real EF Core schema: the
/// customers TigerCS knows are its persisted tickets grouped by identity —
/// CRM Buyer id, else external source + external customer id, else the
/// intake phone — one row per identity however many tickets, with the whole
/// footprint counted, newest first, paged, searchable, and scoped to the
/// caller's visible departments; a ticket with no identity is nobody.
/// </summary>
public sealed class CustomerDirectorySqliteTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];
    private readonly DashboardSqliteFixture _db = new();
    private int _sequence = 500;

    public void Dispose() => _db.Dispose();

    private CustomerDirectoryAppService Service(TigerCsDbContext context)
    {
        var assignments = new UserDepartmentAssignmentRepository(context);
        var queries = new TicketQueryAppService(
            new TicketRepository(context), assignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default,
            new DashboardSqliteFixture.FixedTimeProvider(Now));
        return new CustomerDirectoryAppService(new CustomerDirectoryRepository(context), queries);
    }

    private async Task<CustomerDirectoryListResultDto> ListAsync(CustomerDirectoryListRequestDto? request = null, string[]? roles = null, Guid? caller = null)
    {
        using var context = _db.CreateContext();
        return await Service(context).ListAsync(caller ?? Guid.NewGuid(), roles ?? CrossDepartment, request ?? new CustomerDirectoryListRequestDto(PageSize: 50));
    }

    private async Task<CustomerDirectoryProfileResult> ProfileAsync(string key, string[]? roles = null, Guid? caller = null)
    {
        using var context = _db.CreateContext();
        return await Service(context).GetProfileAsync(caller ?? Guid.NewGuid(), roles ?? CrossDepartment, key);
    }

    [Fact]
    public async Task LinkedHistoryUsesOneScopedSqlUnion_WithCountsOverTheWholeSetBeforeLimit()
    {
        using var context = _db.CreateContext();
        var crm = AddCrmTicket(context, 9001, "Fatima", "0304");
        var pact = AddExternalTicket(context, "Pact", "3001", "0304");
        AddExternalTicket(context, "Pact", "9999", "0304");
        AddCrmTicket(context, 9001, "Fatima", "0304", departmentId: _db.CollectionsId);
        var result = await new TicketRepository(context).SearchCustomerHistoryAsync(new(
            [_db.CustomerServiceId], 9001, null, null, 1, "Pact", "3001", IncludeLinkedExternalIdentity: true));
        Assert.Equal(2, result.TotalCount);
        Assert.Single(result.Tickets);
        var all = await new TicketRepository(context).SearchCustomerHistoryAsync(new(
            [_db.CustomerServiceId], 9001, null, null, 50, "Pact", "3001", IncludeLinkedExternalIdentity: true));
        Assert.Equal(new[] { crm.TicketId, pact.TicketId }.Order(), all.Tickets.Select(t => t.TicketId).Order());
    }

    // ---- seeding ----

    private Ticket AddCrmTicket(TigerCsDbContext context, int crmCustomerId, string name, string unit, string project = "Tiger Tower",
        TicketStatus status = TicketStatus.Open, DateTime? createdAtUtc = null, int? departmentId = null, int crmUnitId = 0)
    {
        var seq = ++_sequence;
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-CRM-{seq:D5}", departmentId ?? _db.CustomerServiceId, crmCustomerId, crmCustomerId * 10, crmUnitId == 0 ? seq : crmUnitId, 7,
            name, project, unit, _db.CsCategoryId, (byte)PriorityLevel.Medium, $"CRM ticket {seq}", createdAtUtc ?? Now.AddHours(-seq));
        Finish(ticket, status);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        return ticket;
    }

    private Ticket AddExternalTicket(
        TigerCsDbContext context, string source, string externalId, string unit, TicketStatus status = TicketStatus.Open,
        DateTime? createdAtUtc = null, string? customerName = null, string? customerEmail = null)
    {
        var seq = ++_sequence;
        var ticket = Ticket.CreateFromExternalLookup(
            $"TG-EXT-{seq:D5}", _db.CustomerServiceId, source, externalId, $"{externalId}-U", "Palm Residence", unit,
            _db.CsCategoryId, (byte)PriorityLevel.Medium, $"External ticket {seq}", createdAtUtc ?? Now.AddHours(-seq),
            customerName, customerEmail);
        Finish(ticket, status);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        return ticket;
    }

    private Ticket AddPhoneTicket(TigerCsDbContext context, string? phone, string? unit = null, TicketStatus status = TicketStatus.Open, DateTime? createdAtUtc = null)
    {
        var seq = ++_sequence;
        var ticket = Ticket.CreateUnverified(
            $"TG-PHN-{seq:D5}", _db.CustomerServiceId, _db.CsCategoryId, (byte)PriorityLevel.Low, $"Phone ticket {seq}",
            createdAtUtc ?? Now.AddHours(-seq), unit is null ? null : "Marina Heights", unit);
        Finish(ticket, status);
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

    /// <summary>Attaches an intake with the given phone to an already-persisted ticket — the fallback identity's raw source.</summary>
    private void LinkIntakePhone(TigerCsDbContext context, Ticket ticket, string phone)
    {
        var intake = new IntakeRecord(1, phone, _db.CustomerServiceId, false, null, null, _db.CsAgentId, ticket.CreatedAtUtc.AddMinutes(-5));
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
        context.IntakeRecords.Add(intake);
        context.SaveChanges();
    }

    private void Finish(Ticket ticket, TicketStatus status)
    {
        if (status == TicketStatus.Open) return;
        ticket.AssignTo(_db.LifecycleWorkerId);
        ticket.ChangeStatus(TicketStatus.InProgress);
        if (status is TicketStatus.Resolved or TicketStatus.Closed) ticket.Resolve(ResolutionOutcome.Resolved, null);
        if (status == TicketStatus.Closed) ticket.Close();
    }

    // ---- list ----

    [Fact]
    public async Task List_GroupsTicketsIntoOneRowPerIdentity_WithTheWholeFootprintCounted_NewestFirst()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 9001, "Mariam Al Falasi", "T-1204", createdAtUtc: Now.AddDays(-10));
            AddCrmTicket(context, 9001, "Mariam Al Falasi", "T-1204", status: TicketStatus.Closed, createdAtUtc: Now.AddDays(-2));
            AddCrmTicket(context, 9001, "Mariam Al Falasi", "T-1310", status: TicketStatus.InProgress, createdAtUtc: Now.AddDays(-1));
            AddExternalTicket(context, "Pact", "PACT-77", "P-08", createdAtUtc: Now.AddDays(-5));
            AddPhoneTicket(context, "+971501112222", createdAtUtc: Now.AddDays(-20));
            AddPhoneTicket(context, "+971501112222", "M-401", createdAtUtc: Now.AddDays(-3));
            // No CRM id, no external id, no intake — not a customer.
            AddPhoneTicket(context, phone: null, createdAtUtc: Now.AddHours(-1));
        }

        var result = await ListAsync();

        Assert.Equal(3, result.TotalCount);
        // The phone key is the number's canonical form — digits only —
        // however the '+971501112222' on these tickets was captured.
        Assert.Equal(["crm:9001", "phone:971501112222", "ext:Pact:PACT-77"], result.Items.Select(r => r.CustomerKey).ToArray());

        var crm = result.Items[0];
        Assert.Equal("Mariam Al Falasi", crm.DisplayName);
        Assert.Equal("Crm", crm.VerificationSource);
        Assert.Equal(3, crm.TotalTickets);
        Assert.Equal(2, crm.OpenTickets);
        Assert.Equal("T-1310", crm.UnitNumber);
        Assert.Equal("Tiger Tower", crm.ProjectName);
        Assert.Equal(Now.AddDays(-1), crm.LastTicketCreatedAtUtc);

        var phone = result.Items[1];
        Assert.Equal("+971501112222", phone.PhoneNumber);
        Assert.Equal("Unverified", phone.VerificationSource);
        Assert.Equal(2, phone.TotalTickets);
        Assert.Equal("M-401", phone.UnitNumber);
        Assert.Null(phone.DisplayName);

        var external = result.Items[2];
        Assert.Equal("Pact", external.VerificationSource);
        Assert.Equal("P-08", external.UnitNumber);
        Assert.Equal(1, external.TotalTickets);
    }

    // ---- the verified external customer's own name and email ----

    /// <summary>
    /// PACT returns the customer's own <c>customerName</c> and
    /// <c>customerEmail</c>; once they are snapshotted on the ticket, the
    /// directory and the profile show the real person instead of falling back
    /// to a "Pact customer" placeholder.
    /// </summary>
    [Fact]
    public async Task ExternalCustomer_WithNameAndEmailOnFile_ShowsThemInTheDirectoryAndTheProfile()
    {
        using (var context = _db.CreateContext())
        {
            AddExternalTicket(context, "Pact", "PACT-7001", "P-08", createdAtUtc: Now.AddDays(-2),
                customerName: "Fatima Noor", customerEmail: "fatima@example.com");
        }

        var row = Assert.Single((await ListAsync()).Items);
        Assert.Equal("ext:Pact:PACT-7001", row.CustomerKey);
        Assert.Equal("Fatima Noor", row.DisplayName);
        Assert.Equal("Pact", row.VerificationSource);

        var profile = (await ProfileAsync(row.CustomerKey)).Response!;
        Assert.Equal("Fatima Noor", profile.DisplayName);
        Assert.Equal(["fatima@example.com"], profile.Emails.ToArray());
    }

    /// <summary>
    /// The snapshot is per-ticket, so a customer whose latest ticket carried
    /// no name is still named from an earlier one that did — and the newest
    /// email on file leads.
    /// </summary>
    [Fact]
    public async Task ExternalCustomer_NameAndEmail_AreFoundAcrossAllOfTheirTickets()
    {
        using (var context = _db.CreateContext())
        {
            AddExternalTicket(context, "Pact", "PACT-7002", "P-01", createdAtUtc: Now.AddDays(-9),
                customerName: "Youssef Noor", customerEmail: "youssef.old@example.com");
            // The newest ticket happens to carry no name at all.
            AddExternalTicket(context, "Pact", "PACT-7002", "P-02", createdAtUtc: Now.AddDays(-1),
                customerName: null, customerEmail: "youssef@example.com");
        }

        var profile = (await ProfileAsync("ext:Pact:PACT-7002")).Response!;
        Assert.Equal("Youssef Noor", profile.DisplayName);
        Assert.Equal(2, profile.TotalTickets);
        // Newest first, and one entry per mailbox.
        Assert.Equal(["youssef@example.com", "youssef.old@example.com"], profile.Emails.ToArray());

        // And the DIRECTORY ROW agrees: a customer the profile can name must
        // never read as a placeholder one screen earlier.
        var row = Assert.Single((await ListAsync()).Items);
        Assert.Equal("ext:Pact:PACT-7002", row.CustomerKey);
        Assert.Equal("Youssef Noor", row.DisplayName);
    }

    /// <summary>
    /// The list and the profile must never name the same customer
    /// differently. Whatever the newest ticket happens to carry, both screens
    /// resolve to the newest ticket that actually snapshotted a name.
    /// </summary>
    [Fact]
    public async Task ListAndProfile_NeverNameTheSameCustomerDifferently()
    {
        using (var context = _db.CreateContext())
        {
            // Newest ticket has no name; an older one does.
            AddExternalTicket(context, "Pact", "PACT-7500", "P-01", createdAtUtc: Now.AddDays(-9), customerName: "Youssef Noor");
            AddExternalTicket(context, "Pact", "PACT-7500", "P-02", createdAtUtc: Now.AddDays(-1), customerName: null);
            // A CRM customer in the same shape, to prove the widening is not PACT-specific.
            AddCrmTicket(context, 9301, "Mariam Al Falasi", "T-1", createdAtUtc: Now.AddDays(-8));
            AddCrmTicket(context, 9301, null!, "T-2", createdAtUtc: Now.AddDays(-2));
        }

        var rows = (await ListAsync()).Items;
        foreach (var row in rows)
        {
            var profile = (await ProfileAsync(row.CustomerKey)).Response!;
            Assert.Equal(profile.DisplayName, row.DisplayName);
        }

        Assert.Equal("Youssef Noor", rows.Single(r => r.CustomerKey == "ext:Pact:PACT-7500").DisplayName);
        Assert.Equal("Mariam Al Falasi", rows.Single(r => r.CustomerKey == "crm:9301").DisplayName);
    }

    /// <summary>
    /// A whitespace-only name is no name. CrmBuyerCustomerName is stored
    /// verbatim, so a blank one on the newest ticket must not shadow the real
    /// name an older ticket carries — otherwise the list and the profile
    /// disagree again.
    /// </summary>
    [Fact]
    public async Task ABlankNameOnTheNewestTicket_DoesNotShadowAnOlderRealName()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 9401, "Mariam Al Falasi", "T-1", createdAtUtc: Now.AddDays(-8));
            AddCrmTicket(context, 9401, "   ", "T-2", createdAtUtc: Now.AddDays(-1));
        }

        var row = Assert.Single((await ListAsync()).Items);
        var profile = (await ProfileAsync("crm:9401")).Response!;

        Assert.Equal("Mariam Al Falasi", row.DisplayName);
        Assert.Equal(profile.DisplayName, row.DisplayName);
    }

    /// <summary>
    /// The SQL mirror of the canonical rule strips the same separators the
    /// authority does, so a number typed with dots or slashes is still one
    /// customer — one row, one key.
    /// </summary>
    [Fact]
    public async Task PhoneNumbersWrittenWithDotsOrSlashes_AreStillOneCustomer()
    {
        using (var context = _db.CreateContext())
        {
            AddPhoneTicket(context, "+971501234567", createdAtUtc: Now.AddDays(-4));
            AddPhoneTicket(context, "971.50.123.4567", createdAtUtc: Now.AddDays(-3));
            AddPhoneTicket(context, "971/50/123/4567", createdAtUtc: Now.AddDays(-2));
        }

        var result = await ListAsync();

        var row = Assert.Single(result.Items);
        Assert.Equal("phone:971501234567", row.CustomerKey);
        Assert.Equal(3, row.TotalTickets);
        // No two rows may ever carry the same key.
        Assert.Equal(result.Items.Count, result.Items.Select(r => r.CustomerKey).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A search term is only matched as a phone number when it is SHAPED like
    /// one. "M-401" is a unit code: it must keep finding unit M-401 and must
    /// not drag in every caller whose number merely contains 401.
    /// </summary>
    [Fact]
    public async Task List_ASearchTermThatIsNotPhoneShaped_IsNotMatchedAgainstPhoneNumbers()
    {
        using (var context = _db.CreateContext())
        {
            AddPhoneTicket(context, "+971 50 401 9988", "X-1");
            AddExternalTicket(context, "Pact", "PACT-7600", "M-401", customerName: "Fatima Noor");
        }

        // The unit code finds the unit, and nothing else.
        var row = Assert.Single((await ListAsync(new CustomerDirectoryListRequestDto(Search: "M-401"))).Items);
        Assert.Equal("ext:Pact:PACT-7600", row.CustomerKey);

        // A phone-shaped term still matches the caller canonically.
        Assert.Equal(
            "phone:971504019988",
            Assert.Single((await ListAsync(new CustomerDirectoryListRequestDto(Search: "+971504019988"))).Items).CustomerKey);
    }

    /// <summary>
    /// A source that holds no name and no email must not be papered over: the
    /// directory reports no name rather than inventing one, and no email
    /// rather than borrowing another customer's. The Web then chooses the
    /// "Pact customer" placeholder — a label, not a fabricated identity.
    /// </summary>
    [Fact]
    public async Task ExternalCustomer_WithNeitherNameNorEmail_FallsBackSafely_AndInventsNothing()
    {
        using (var context = _db.CreateContext())
        {
            AddExternalTicket(context, "Tasleeh", "TAS-1", "X-1", customerName: null, customerEmail: null);
        }

        var row = Assert.Single((await ListAsync()).Items);
        Assert.Null(row.DisplayName);

        var profile = (await ProfileAsync(row.CustomerKey)).Response!;
        Assert.Null(profile.DisplayName);
        Assert.Empty(profile.Emails);
    }

    /// <summary>Blank is the same as absent — a source that answered "" or "   " has nothing on file and must not out-rank the real fallbacks.</summary>
    [Fact]
    public async Task ExternalCustomer_WithBlankNameOrEmail_IsTreatedAsHavingNone()
    {
        using (var context = _db.CreateContext())
        {
            AddExternalTicket(context, "Pact", "PACT-7003", "P-03", customerName: "   ", customerEmail: "  ");
        }

        var profile = (await ProfileAsync("ext:Pact:PACT-7003")).Response!;
        Assert.Null(profile.DisplayName);
        Assert.Empty(profile.Emails);
    }

    /// <summary>
    /// The snapshot is display only. Two PACT customers with the same name and
    /// the same email are still two customers — identity is source + external
    /// customer id, and nothing else.
    /// </summary>
    [Fact]
    public async Task ExternalCustomerNameAndEmail_NeverMergeTwoDifferentExternalCustomers()
    {
        using (var context = _db.CreateContext())
        {
            AddExternalTicket(context, "Pact", "PACT-8001", "P-01", customerName: "Fatima Noor", customerEmail: "shared@example.com");
            AddExternalTicket(context, "Pact", "PACT-8002", "P-02", customerName: "Fatima Noor", customerEmail: "shared@example.com");
            // Same name and email again, on a different source entirely.
            AddExternalTicket(context, "Tasleeh", "PACT-8001", "X-1", customerName: "Fatima Noor", customerEmail: "shared@example.com");
        }

        var result = await ListAsync();

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            ["ext:Pact:PACT-8001", "ext:Pact:PACT-8002", "ext:Tasleeh:PACT-8001"],
            result.Items.Select(r => r.CustomerKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Identity precedence is untouched by the new snapshot: a CRM id still
    /// wins over an external identity, which still wins over the canonical
    /// phone — and a CRM-verified ticket never adopts an external name.
    /// </summary>
    [Fact]
    public async Task IdentityPrecedence_IsUnchangedByTheExternalCustomerSnapshot()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 9201, "Mariam Al Falasi", "T-1");
            AddExternalTicket(context, "Pact", "PACT-9001", "P-01", customerName: "Fatima Noor", customerEmail: "fatima@example.com");
            AddPhoneTicket(context, "+971501234567");
        }

        var result = await ListAsync();

        Assert.Equal(
            ["crm:9201", "ext:Pact:PACT-9001", "phone:971501234567"],
            result.Items.Select(r => r.CustomerKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("Mariam Al Falasi", result.Items.Single(r => r.CustomerKey == "crm:9201").DisplayName);
        Assert.Equal("Fatima Noor", result.Items.Single(r => r.CustomerKey == "ext:Pact:PACT-9001").DisplayName);
    }

    /// <summary>The CRM Buyer snapshot still leads: an external name never displaces the system of record's own.</summary>
    [Fact]
    public async Task CrmCustomerName_StillLeads_AndCrmCustomersAreUnaffected()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 9202, "Mariam Al Falasi", "T-1204", createdAtUtc: Now.AddDays(-1));
        }

        var row = Assert.Single((await ListAsync()).Items);
        Assert.Equal("crm:9202", row.CustomerKey);
        Assert.Equal("Mariam Al Falasi", row.DisplayName);
        Assert.Equal("Crm", row.VerificationSource);

        var profile = (await ProfileAsync("crm:9202")).Response!;
        Assert.Equal("Mariam Al Falasi", profile.DisplayName);
        // TigerCS persists no CRM email — the Customer Profile reads that live
        // from CRM — so the directory reports none rather than guessing.
        Assert.Empty(profile.Emails);
    }

    /// <summary>The external customer's name is searchable, like the CRM Buyer name beside it.</summary>
    [Fact]
    public async Task List_SearchMatchesTheExternalCustomerName()
    {
        using (var context = _db.CreateContext())
        {
            AddExternalTicket(context, "Pact", "PACT-7100", "P-08", customerName: "Fatima Noor", customerEmail: null);
            AddExternalTicket(context, "Pact", "PACT-7200", "P-09", customerName: "Omar Haddad", customerEmail: null);
        }

        var row = Assert.Single((await ListAsync(new CustomerDirectoryListRequestDto(Search: "Fatima"))).Items);
        Assert.Equal("ext:Pact:PACT-7100", row.CustomerKey);
    }

    // ---- one phone, one customer, however it was written ----

    /// <summary>
    /// The Customer Directory identity bug: the same mobile captured with a
    /// '+', without one, with spaces and with hyphens used to become four
    /// customers. Every written form of one number is one row, one key and
    /// one footprint.
    /// </summary>
    [Fact]
    public async Task List_TheSamePhoneWrittenFourWays_IsOneCustomerWithOneCanonicalKey()
    {
        using (var context = _db.CreateContext())
        {
            AddPhoneTicket(context, "+971501234567", createdAtUtc: Now.AddDays(-8));
            AddPhoneTicket(context, "971501234567", createdAtUtc: Now.AddDays(-6));
            AddPhoneTicket(context, "+971 50 123 4567", createdAtUtc: Now.AddDays(-4));
            AddPhoneTicket(context, "971-50-123-4567", "M-401", createdAtUtc: Now.AddDays(-2));
        }

        var result = await ListAsync();

        var row = Assert.Single(result.Items);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("phone:971501234567", row.CustomerKey);
        Assert.Equal("Unverified", row.VerificationSource);
        Assert.Equal(4, row.TotalTickets);
        Assert.Equal(4, row.OpenTickets);
        // The row still shows the number as the latest ticket captured it.
        Assert.Equal("971-50-123-4567", row.PhoneNumber);
        Assert.Equal("M-401", row.UnitNumber);
    }

    /// <summary>
    /// The identity phone is the ticket's FIRST linked intake with a non-empty number (lowest IntakeRecordId) — resolved in SQL by
    /// a single pre-aggregated join (see <see cref="CustomerDirectoryQueryShapeTests"/>). A blank first intake is skipped, a later
    /// intake never re-homes the ticket, and a ticket with no usable intake is nobody — for the list AND the profile, so the
    /// row's key always opens the profile it names (the ticket-to-profile link depends on it).
    /// </summary>
    [Fact]
    public async Task PhoneIdentity_IsTheFirstNonEmptyLinkedIntake_ForTheListAndTheProfile()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            var ticket = AddPhoneTicket(context, phone: null, createdAtUtc: Now.AddDays(-2));
            ticketId = ticket.TicketId;
            LinkIntakePhone(context, ticket, "");                 // blank: skipped
            LinkIntakePhone(context, ticket, "+971 50 123 4567"); // first usable: the identity
            LinkIntakePhone(context, ticket, "+971509999999");    // later capture: never re-homes the ticket
            AddPhoneTicket(context, phone: null, createdAtUtc: Now.AddDays(-1)); // no intake at all: nobody
        }

        var list = await ListAsync();
        var row = Assert.Single(list.Items);
        Assert.Equal("phone:971501234567", row.CustomerKey);
        Assert.Equal(1, row.TotalTickets);
        Assert.Equal(ticketId, row.LastTicketId);

        var profile = await ProfileAsync(row.CustomerKey);
        Assert.Equal(CustomerDirectoryProfileOutcome.Success, profile.Outcome);
        Assert.Equal(ticketId, Assert.Single(profile.Response!.Tickets).TicketId);

        var other = await ProfileAsync("phone:971509999999");
        Assert.Equal(CustomerDirectoryProfileOutcome.NotFound, other.Outcome);
    }

    /// <summary>
    /// Searching with or without the '+', with spaces or with hyphens, finds
    /// that one customer — and so does a plain fragment of the digits.
    /// </summary>
    [Theory]
    [InlineData("+971501234567")]
    [InlineData("971501234567")]
    [InlineData("+971 50 123 4567")]
    [InlineData("971-50-123-4567")]
    [InlineData("(971) 50 123 4567")]
    [InlineData("501234567")]
    public async Task List_SearchByAnyWrittenFormOfThePhone_FindsTheSameCustomer(string search)
    {
        using (var context = _db.CreateContext())
        {
            AddPhoneTicket(context, "+971501234567", createdAtUtc: Now.AddDays(-3));
            AddPhoneTicket(context, "971501234567", createdAtUtc: Now.AddDays(-1));
            AddPhoneTicket(context, "+971509999111", createdAtUtc: Now.AddDays(-2));
        }

        var result = await ListAsync(new CustomerDirectoryListRequestDto(Search: search));

        var row = Assert.Single(result.Items);
        Assert.Equal("phone:971501234567", row.CustomerKey);
        Assert.Equal(2, row.TotalTickets);
    }

    /// <summary>Every written form of the key opens the one profile, and it carries every ticket — old '+'-bearing bookmarks included.</summary>
    [Theory]
    [InlineData("phone:971501234567")]
    [InlineData("phone:%2B971501234567")]
    [InlineData("phone:+971501234567")]
    [InlineData("phone:971%2050%20123%204567")]
    public async Task Profile_ByAnyWrittenFormOfThePhoneKey_ResolvesToTheOneCustomer(string key)
    {
        using (var context = _db.CreateContext())
        {
            AddPhoneTicket(context, "+971501234567", createdAtUtc: Now.AddDays(-5));
            AddPhoneTicket(context, "971-50-123-4567", createdAtUtc: Now.AddDays(-1));
        }

        var result = await ProfileAsync(key);

        Assert.Equal(CustomerDirectoryProfileOutcome.Success, result.Outcome);
        var profile = result.Response!;
        Assert.Equal("phone:971501234567", profile.CustomerKey);
        Assert.Equal(2, profile.TotalTickets);
        // Both tickets are the same phone, so the profile lists one number —
        // the way it was most recently captured.
        Assert.Equal(["971-50-123-4567"], profile.PhoneNumbers.ToArray());
    }

    /// <summary>
    /// Canonicalizing the phone must not merge anything stronger: two CRM
    /// customers, or two external customers, that happen to share a phone
    /// stay two customers. Phone is only ever the last resort.
    /// </summary>
    [Fact]
    public async Task List_ASharedPhoneNeverMergesTwoCrmOrTwoExternalCustomers()
    {
        using (var context = _db.CreateContext())
        {
            var first = AddCrmTicket(context, 9101, "Mariam Al Falasi", "T-1");
            var second = AddCrmTicket(context, 9102, "Omar Haddad", "T-2");
            var pact = AddExternalTicket(context, "Pact", "PACT-1", "P-1");
            var tasleeh = AddExternalTicket(context, "Tasleeh", "TAS-1", "X-1");
            // One household phone, written differently on each ticket.
            LinkIntakePhone(context, first, "+971501234567");
            LinkIntakePhone(context, second, "971501234567");
            LinkIntakePhone(context, pact, "+971 50 123 4567");
            LinkIntakePhone(context, tasleeh, "971-50-123-4567");
            AddPhoneTicket(context, "+971501234567");
        }

        var result = await ListAsync();

        // Two CRM customers, two external customers, one phone-only caller.
        Assert.Equal(
            ["crm:9101", "crm:9102", "ext:Pact:PACT-1", "ext:Tasleeh:TAS-1", "phone:971501234567"],
            result.Items.Select(r => r.CustomerKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.All(result.Items, r => Assert.Equal(1, r.TotalTickets));
    }

    /// <summary>A number that is nothing but separators identifies nobody — the ticket is not a customer, exactly as a missing number is not.</summary>
    [Fact]
    public async Task List_APhoneWithNoDigits_IsNotACustomer()
    {
        using (var context = _db.CreateContext())
        {
            AddPhoneTicket(context, "+");
            AddPhoneTicket(context, "( ) - ");
            AddPhoneTicket(context, "+971501234567");
        }

        var result = await ListAsync();

        Assert.Equal(["phone:971501234567"], result.Items.Select(r => r.CustomerKey).ToArray());
    }

    [Fact]
    public async Task List_TheSamePhoneOnAnInteraction_IsFoundByEitherWrittenForm()
    {
        using (var context = _db.CreateContext())
        {
            var ticket = AddPhoneTicket(context, "971501234567");
            context.TicketInteractions.Add(TicketInteraction.CreateLocal(ticket.TicketId, 1, "+971 50 123 4567", Now.AddHours(-2), isOriginatingInteraction: true));
            context.SaveChanges();
        }

        foreach (var search in new[] { "+971501234567", "971501234567", "+971-50-123-4567" })
        {
            var row = Assert.Single((await ListAsync(new CustomerDirectoryListRequestDto(Search: search))).Items);
            Assert.Equal("phone:971501234567", row.CustomerKey);
        }
    }

    [Fact]
    public async Task List_PhoneIsOnlyAFallbackIdentity_AVerifiedTicketNeverJoinsAPhoneGroup()
    {
        using (var context = _db.CreateContext())
        {
            // Same phone on a CRM-verified ticket and on an unverified one:
            // two customers, because the CRM id is the stronger identity.
            var crm = AddCrmTicket(context, 9002, "Omar Haddad", "T-2");
            var intake = new IntakeRecord(1, "+971509999000", _db.CustomerServiceId, false, null, null, _db.CsAgentId, Now.AddDays(-1));
            intake.LinkToTicket(crm.TicketId, CrmVerificationStatus.Verified, hasSelectedUnit: true);
            context.IntakeRecords.Add(intake);
            context.SaveChanges();
            AddPhoneTicket(context, "+971509999000");
        }

        var result = await ListAsync();

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, r => r.CustomerKey == "crm:9002" && r.PhoneNumber == "+971509999000");
        Assert.Contains(result.Items, r => r.CustomerKey == "phone:971509999000" && r.TotalTickets == 1);
    }

    [Fact]
    public async Task List_SearchSelectsCustomers_AndKeepsTheirFullCounts()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 9003, "Mariam Al Falasi", "T-1204");
            AddCrmTicket(context, 9003, "Mariam Al Falasi", "T-1310", status: TicketStatus.Closed);
            AddCrmTicket(context, 9004, "Omar Haddad", "T-77");
            AddPhoneTicket(context, "+971501112222", "M-401");
        }

        var byName = await ListAsync(new CustomerDirectoryListRequestDto(Search: "falasi".ToUpperInvariant()[..1] + "alasi"));
        var row = Assert.Single(byName.Items);
        Assert.Equal("crm:9003", row.CustomerKey);
        // The match was on unit T-1204's ticket, but the row still counts both tickets.
        Assert.Equal(2, (await ListAsync(new CustomerDirectoryListRequestDto(Search: "1204"))).Items.Single().TotalTickets);
        Assert.Equal("phone:971501112222", (await ListAsync(new CustomerDirectoryListRequestDto(Search: "50111"))).Items.Single().CustomerKey);
        Assert.Equal("crm:9004", (await ListAsync(new CustomerDirectoryListRequestDto(Search: "Haddad"))).Items.Single().CustomerKey);
        Assert.Empty((await ListAsync(new CustomerDirectoryListRequestDto(Search: "nobody"))).Items);
    }

    [Fact]
    public async Task List_FiltersByVerificationSource_OpenTickets_AndDepartment_AndPages()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 9005, "Closed Only", "T-1", status: TicketStatus.Closed);
            AddCrmTicket(context, 9006, "Open One", "T-2");
            AddCrmTicket(context, 9007, "Collections Customer", "T-3", departmentId: _db.CollectionsId);
            AddExternalTicket(context, "Tasleeh", "TAS-1", "X-1");
            AddPhoneTicket(context, "+971500000001");
        }

        Assert.Equal(3, (await ListAsync(new CustomerDirectoryListRequestDto(VerificationSource: "Crm"))).TotalCount);
        Assert.Equal(1, (await ListAsync(new CustomerDirectoryListRequestDto(VerificationSource: "Tasleeh"))).TotalCount);
        Assert.Equal(1, (await ListAsync(new CustomerDirectoryListRequestDto(VerificationSource: "Unverified"))).TotalCount);
        Assert.DoesNotContain((await ListAsync(new CustomerDirectoryListRequestDto(OpenOnly: true))).Items, r => r.CustomerKey == "crm:9005");
        Assert.Equal(["crm:9007"], (await ListAsync(new CustomerDirectoryListRequestDto(DepartmentId: _db.CollectionsId))).Items.Select(r => r.CustomerKey).ToArray());

        var page1 = await ListAsync(new CustomerDirectoryListRequestDto(Page: 1, PageSize: 2));
        var page2 = await ListAsync(new CustomerDirectoryListRequestDto(Page: 2, PageSize: 2));
        var page3 = await ListAsync(new CustomerDirectoryListRequestDto(Page: 3, PageSize: 2));
        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.Single(page3.Items);
        Assert.Equal(5, page1.Items.Concat(page2.Items).Concat(page3.Items).Select(r => r.CustomerKey).Distinct().Count());
    }

    [Fact]
    public async Task List_AndProfile_ForADepartmentEmployee_ReachOnlyCustomersOfTicketsAssignedToThem()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 9008, "Collections Customer", "T-3", departmentId: _db.CollectionsId);   // another department, not theirs
            var assigned = AddCrmTicket(context, 9009, "Registration Customer", "T-4", departmentId: _db.RegistrationId);
            AddCrmTicket(context, 9012, "Unassigned Registration Customer", "T-5", departmentId: _db.RegistrationId);   // own department, unassigned
            var colleagues = AddCrmTicket(context, 9014, "Colleague Customer", "T-7", departmentId: _db.RegistrationId);
            var elsewhere = AddCrmTicket(context, 9013, "Assigned Elsewhere", "T-6", departmentId: _db.CollectionsId);
            assigned.AssignTo(_db.RegistrationEmployeeId);
            elsewhere.AssignTo(_db.RegistrationEmployeeId);   // assigned to them although it sits in another department
            colleagues.AssignTo(_db.LifecycleWorkerId);
            context.SaveChanges();
        }

        var mine = await ListAsync(roles: [Roles.DepartmentEmployee], caller: _db.RegistrationEmployeeId);
        Assert.Equal(["crm:9009", "crm:9013"], mine.Items.Select(r => r.CustomerKey).Order().ToArray());

        Assert.Equal(CustomerDirectoryProfileOutcome.Success, (await ProfileAsync("crm:9013", roles: [Roles.DepartmentEmployee], caller: _db.RegistrationEmployeeId)).Outcome);
        foreach (var hiddenKey in new[] { "crm:9008", "crm:9012", "crm:9014" })
        {
            var hidden = await ProfileAsync(hiddenKey, roles: [Roles.DepartmentEmployee], caller: _db.RegistrationEmployeeId);
            Assert.Equal(CustomerDirectoryProfileOutcome.NotFound, hidden.Outcome);
        }

        Assert.Equal(CustomerDirectoryProfileOutcome.Success, (await ProfileAsync("crm:9008")).Outcome);
    }

    // ---- profile ----

    [Fact]
    public async Task Profile_ListsEveryTicket_DeduplicatesUnits_AndCollectsPhonesAndInteractions()
    {
        long firstTicketId;
        using (var context = _db.CreateContext())
        {
            var first = AddCrmTicket(context, 9010, "Mariam Al Falasi", "T-1204", createdAtUtc: Now.AddDays(-10), crmUnitId: 41);
            var closed = AddCrmTicket(context, 9010, "Mariam Al Falasi", "T-1204", status: TicketStatus.Closed, createdAtUtc: Now.AddDays(-4), crmUnitId: 41);
            context.TicketResolutions.Add(new TicketResolution(closed.TicketId, ResolutionOutcome.Resolved, "Fixed", null, null, _db.LifecycleWorkerId, Now.AddDays(-3)));
            var latest = AddCrmTicket(context, 9010, "Mariam Al Falasi", "T-1310", status: TicketStatus.InProgress, createdAtUtc: Now.AddDays(-1), crmUnitId: 42);
            AddCrmTicket(context, 9011, "Someone Else", "T-9");
            firstTicketId = first.TicketId;

            var intake = new IntakeRecord(1, "+971501234567", _db.CustomerServiceId, false, null, null, _db.CsAgentId, Now.AddDays(-10));
            intake.LinkToTicket(first.TicketId, CrmVerificationStatus.Verified, hasSelectedUnit: true);
            context.IntakeRecords.Add(intake);
            context.TicketInteractions.Add(TicketInteraction.CreateLocal(latest.TicketId, 1, "+971501234567", Now.AddHours(-3), isOriginatingInteraction: true));
            context.TicketInteractions.Add(TicketInteraction.CreateLocal(first.TicketId, 1, "+971509990000", Now.AddDays(-10)));
            context.TicketNotes.Add(new TicketNote(first.TicketId, "Called back", _db.CsAgentId, Now.AddHours(-2)));
            context.SaveChanges();
        }

        var result = await ProfileAsync("crm:9010");

        Assert.Equal(CustomerDirectoryProfileOutcome.Success, result.Outcome);
        var profile = result.Response!;
        Assert.Equal("Mariam Al Falasi", profile.DisplayName);
        Assert.Equal("Crm", profile.VerificationSource);
        Assert.Equal(9010, profile.CrmBuyerCustomerId);
        Assert.Equal(3, profile.TotalTickets);
        Assert.Equal(2, profile.OpenTickets);
        Assert.Equal(Now.AddDays(-10), profile.FirstSeenAtUtc);
        Assert.Equal(Now.AddDays(-1), profile.LastSeenAtUtc);

        // Newest first; the note on the oldest ticket is its last activity.
        Assert.Equal(["T-1310", "T-1204", "T-1204"], profile.Tickets.Select(t => t.UnitNumber!).ToArray());
        Assert.Equal(Now.AddHours(-2), profile.Tickets.Single(t => t.TicketId == firstTicketId).LastActivityAtUtc);
        Assert.NotNull(profile.Tickets.Single(t => t.TicketStatus == "Closed").ResolvedAtUtc);

        // Two distinct units across three tickets, with per-unit ticket counts.
        Assert.Equal(2, profile.Units.Count);
        Assert.Equal(2, profile.Units.Single(u => u.UnitNumber == "T-1204").TicketCount);
        Assert.Equal(41, profile.Units.Single(u => u.UnitNumber == "T-1204").CrmBuyerUnitId);

        Assert.Equal(["+971501234567", "+971509990000"], profile.PhoneNumbers.OrderBy(p => p).ToArray());
        Assert.Equal(2, profile.Interactions.Count);
        Assert.True(profile.Interactions[0].IsOriginatingInteraction);
        Assert.Equal("Phone", profile.Interactions[0].ChannelName);

        Assert.Equal(CustomerDirectoryProfileOutcome.InvalidKey, (await ProfileAsync("bogus")).Outcome);
        Assert.Equal(CustomerDirectoryProfileOutcome.NotFound, (await ProfileAsync("crm:424242")).Outcome);
    }

    [Fact]
    public async Task Profile_ByExternalAndPhoneKeys_RoundTripsThroughTheDirectoryKey()
    {
        using (var context = _db.CreateContext())
        {
            AddExternalTicket(context, "Pact", "PACT/77", "P-08");
            AddPhoneTicket(context, "+971501112222", "M-401");
            AddPhoneTicket(context, "+971501112222");
        }

        var list = await ListAsync();
        foreach (var row in list.Items)
        {
            var profile = await ProfileAsync(row.CustomerKey);
            Assert.Equal(CustomerDirectoryProfileOutcome.Success, profile.Outcome);
            Assert.Equal(row.CustomerKey, profile.Response!.CustomerKey);
            Assert.Equal(row.TotalTickets, profile.Response.TotalTickets);
        }

        var external = list.Items.Single(r => r.IdentityKind == "External");
        Assert.Equal("ext:Pact:PACT%2F77", external.CustomerKey);
        Assert.Equal("PACT/77", (await ProfileAsync(external.CustomerKey)).Response!.ExternalCustomerId);
    }

    [Fact]
    public void CustomerIdentity_KeysParseBackToTheSameIdentity()
    {
        foreach (var identity in new[]
        {
            CustomerIdentity.Crm(12),
            CustomerIdentity.External("Pact", "A/B:C d"),
            CustomerIdentity.Phone("+971 50 123 4567"),
        })
        {
            Assert.True(CustomerIdentity.TryParse(identity.Key, out var parsed));
            Assert.Equal(identity, parsed);
        }

        Assert.False(CustomerIdentity.TryParse("crm:0", out _));
        Assert.False(CustomerIdentity.TryParse("ext:Pact:", out _));
        Assert.False(CustomerIdentity.TryParse("phone:", out _));
        Assert.False(CustomerIdentity.TryParse("", out _));
        Assert.Null(CustomerIdentity.FromTicketFacts(null, null, null, " "));
        Assert.Equal(CustomerIdentityKind.External, CustomerIdentity.FromTicketFacts(null, "Pact", "X", "+971")!.Kind);
        Assert.Equal(CustomerIdentityKind.Crm, CustomerIdentity.FromTicketFacts(3, "Pact", "X", "+971")!.Kind);
    }
}
