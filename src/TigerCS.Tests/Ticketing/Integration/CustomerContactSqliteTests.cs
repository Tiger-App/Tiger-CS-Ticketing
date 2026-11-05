using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The customer name/e-mail an agent types in (New Ticket manual entry, Customer Details edit), proven on the real EF schema: one
/// record per canonical phone however the number is written, validated server-side, refused for read-only roles, audited, and — the
/// point of it — what the Customers list and profile show afterwards. Nothing else about the customer may change.
/// </summary>
public sealed class CustomerContactSqliteTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private static readonly string[] Agent = [Roles.CsAgent];
    private readonly DashboardSqliteFixture _db = new();
    private readonly FakeAuditEntryWriter _audit = new();
    private int _sequence = 700;

    public void Dispose() => _db.Dispose();

    private CustomerContactAppService ContactService(TigerCsDbContext context) =>
        new(new CustomerContactDetailRepository(context), new TicketingUnitOfWork(context), _audit, new DashboardSqliteFixture.FixedTimeProvider(Now));

    private CustomerDirectoryAppService DirectoryService(TigerCsDbContext context)
    {
        var queries = new TicketQueryAppService(
            new TicketRepository(context), new UserDepartmentAssignmentRepository(context), new FakeTicketResolutionRepository(),
            new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, new DashboardSqliteFixture.FixedTimeProvider(Now));
        return new CustomerDirectoryAppService(new CustomerDirectoryRepository(context), queries);
    }

    private Task<CustomerContactResult> SaveAsync(string? phone, string? name, string? email, string[]? roles = null)
    {
        using var context = _db.CreateContext();
        return ContactService(context).SaveAsync(Guid.NewGuid(), roles ?? Agent, new SaveCustomerContactRequestDto(phone, name, email));
    }

    private void AddPhoneTicket(string phone)
    {
        using var context = _db.CreateContext();
        var seq = ++_sequence;
        var ticket = Ticket.CreateUnverified(
            $"TG-CC-{seq:D5}", _db.CustomerServiceId, _db.CsCategoryId, (byte)PriorityLevel.Low, "Phone ticket", Now.AddHours(-seq), null, null);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        var intake = new IntakeRecord(1, phone, _db.CustomerServiceId, false, null, null, _db.CsAgentId, ticket.CreatedAtUtc.AddMinutes(-5));
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
        context.IntakeRecords.Add(intake);
        context.SaveChanges();
    }

    [Fact]
    public async Task Save_CreatesTheRecord_ThenUpdatesTheSameOne_WhateverWayThePhoneIsWritten()
    {
        var first = await SaveAsync("+971 50 123 4567", "Aisha Rahman", null);
        var second = await SaveAsync("971501234567", "Aisha M. Rahman", "aisha@example.com");

        Assert.Equal(CustomerContactOutcome.Success, first.Outcome);
        Assert.True(first.Response!.Created);
        Assert.Equal(CustomerContactOutcome.Success, second.Outcome);
        Assert.False(second.Response!.Created);

        using var context = _db.CreateContext();
        var row = Assert.Single(context.CustomerContactDetails);
        Assert.Equal("971501234567", row.PhoneKey);
        Assert.Equal("Aisha M. Rahman", row.Name);
        Assert.Equal("aisha@example.com", row.Email);
        Assert.Equal(["CreateCustomerContact", "UpdateCustomerContact"], _audit.Written.Select(w => w.Action));
    }

    [Theory]
    [InlineData("+971501234567", "", null, "name")]
    [InlineData("+971501234567", "   ", null, "name")]
    [InlineData("+971501234567", "UNKNOWN", null, "name")]
    [InlineData("+971501234567", "Aisha", "not-an-email", "email")]
    [InlineData("+971501234567", "Aisha", "a b@example.com", "email")]
    [InlineData("", "Aisha", null, "phoneNumber")]
    [InlineData("+", "Aisha", null, "phoneNumber")]
    public async Task Save_RejectsInvalidInput_AndWritesNothing(string phone, string name, string? email, string field)
    {
        var result = await SaveAsync(phone, name, email);

        Assert.Equal(CustomerContactOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(field, result.Errors!.Keys);
        using var context = _db.CreateContext();
        Assert.Empty(context.CustomerContactDetails);
        Assert.Empty(_audit.Written);
    }

    [Fact]
    public async Task Save_RejectsOverlongName_AndReadOnlyRoles()
    {
        Assert.Equal(CustomerContactOutcome.ValidationFailed, (await SaveAsync("0501234567", new string('a', 201), null)).Outcome);
        Assert.Equal(CustomerContactOutcome.Forbidden, (await SaveAsync("0501234567", "Aisha", null, [Roles.ChairmanCeo])).Outcome);

        using var context = _db.CreateContext();
        Assert.Empty(context.CustomerContactDetails);
    }

    [Fact]
    public async Task SavedName_ShowsInTheCustomersListAndProfile_WithoutChangingPhoneOrVerification()
    {
        AddPhoneTicket("+971501234567");
        AddPhoneTicket("971 50 123 4567");

        await SaveAsync("0501234567x".Replace("x", ""), "Ignored — different number", null);
        await SaveAsync("+971-50-123-4567", "Aisha Rahman", "aisha@example.com");

        using var context = _db.CreateContext();
        var directory = DirectoryService(context);
        var list = await directory.ListAsync(Guid.NewGuid(), [Roles.CsSupervisor], new CustomerDirectoryListRequestDto(PageSize: 50));
        var row = Assert.Single(list.Items);
        Assert.Equal("Aisha Rahman", row.DisplayName);
        Assert.Equal("Unverified", row.VerificationSource);
        Assert.Equal("Phone", row.IdentityKind);
        Assert.Equal(2, row.TotalTickets);

        var profile = (await directory.GetProfileAsync(Guid.NewGuid(), [Roles.CsSupervisor], row.CustomerKey)).Response!;
        Assert.Equal("Aisha Rahman", profile.DisplayName);
        Assert.Equal(["aisha@example.com"], profile.Emails);
        Assert.Equal("Phone", profile.IdentityKind);

        // The name is searchable too, and the ticket rows themselves were never rewritten.
        var found = await directory.ListAsync(Guid.NewGuid(), [Roles.CsSupervisor], new CustomerDirectoryListRequestDto(Search: "Rahman", PageSize: 50));
        Assert.Single(found.Items);
        Assert.All(context.Tickets, t => Assert.Null(t.ExternalCustomerName));
    }

    [Fact]
    public async Task NoSavedRecord_LeavesTheListExactlyAsBefore()
    {
        AddPhoneTicket("+971509990000");

        using var context = _db.CreateContext();
        var list = await DirectoryService(context).ListAsync(Guid.NewGuid(), [Roles.CsSupervisor], new CustomerDirectoryListRequestDto(PageSize: 50));

        Assert.Null(Assert.Single(list.Items).DisplayName);
    }
}
