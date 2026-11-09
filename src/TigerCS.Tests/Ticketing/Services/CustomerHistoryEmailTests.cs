using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// <c>GET /api/tickets/{id}/customer-history</c> carries the customer's email as the notification resolver reads it, so Ticket Details
/// shows exactly the address the lifecycle emails go to.
/// </summary>
public class CustomerHistoryEmailTests
{
    private static CustomerHistoryAppService CreateService(NotificationServiceFixture f)
    {
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var queryService = new TicketQueryAppService(f.Tickets, departmentAssignments, f.Resolutions, statusHistory, ReopenPolicy.Default, TimeProvider.System);
        return new CustomerHistoryAppService(
            f.Tickets, new FakeIntakeRecordRepository(), f.Resolutions, statusHistory, queryService, ReopenPolicy.Default, TimeProvider.System,
            f.ContactDetails, f.CreateContactResolver());
    }

    [Fact]
    public async Task ManualCustomer_HistoryCarriesTheSavedEmail()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync();

        var result = await CreateService(f).GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("manual.customer@example.com", result.Response!.CustomerEmail);
    }

    [Fact]
    public async Task GenesysCustomer_HistoryCarriesTheGenesysEmail()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync("fatima@example.com");

        var result = await CreateService(f).GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("fatima@example.com", result.Response!.CustomerEmail);
    }

    [Fact]
    public async Task ExternalCustomer_HistoryCarriesTheSnapshottedEmail()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedExternalCustomerTicketAsync("omar.saleh@example.com");

        var result = await CreateService(f).GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal("omar.saleh@example.com", result.Response!.CustomerEmail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-email")]
    public async Task NoValidEmail_HistoryCarriesNone(string? savedEmail)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync(savedEmail: savedEmail);

        var result = await CreateService(f).GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Null(result.Response!.CustomerEmail);
    }

    [Fact]
    public async Task WithoutTheResolver_TheHistoryIsAsBefore()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedManualCustomerTicketAsync();
        var service = new CustomerHistoryAppService(
            f.Tickets, new FakeIntakeRecordRepository(), f.Resolutions, new FakeTicketStatusHistoryRepository(),
            new TicketQueryAppService(f.Tickets, new FakeUserDepartmentAssignmentRepository(), f.Resolutions, new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System),
            ReopenPolicy.Default, TimeProvider.System);

        var result = await service.GetForTicketAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Null(result.Response!.CustomerEmail);
    }
}
