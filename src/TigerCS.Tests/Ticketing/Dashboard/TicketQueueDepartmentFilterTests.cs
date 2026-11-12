using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// The Tickets page's Department filter: <c>departmentId</c> only NARROWS the caller's server-resolved scope, so a hand-edited id can never
/// reveal another department. Real SQL (SQLite) so the filter, total count and paging run in the actual query.
/// </summary>
public sealed class TicketQueueDepartmentFilterTests : IDisposable
{
    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<TicketListResultDto> QueueAsync(Guid caller, string role, int? departmentId, int page = 1, int pageSize = 50, string? ticketStatus = null) =>
        await _db.CreateQueryService(_db.CreateContext()).GetQueueAsync(
            caller, [role],
            new TicketListRequestDto(departmentId, null, null, ticketStatus, null, null, null, null, null, page, pageSize));

    private (long Collections1, long Collections2, long Collections3, long Registration) Seed()
    {
        using var context = _db.CreateContext();
        var c1 = _db.AddTicket(context, _db.CollectionsId).TicketId;
        var c2 = _db.AddTicket(context, _db.CollectionsId).TicketId;
        var c3 = _db.AddTicket(context, _db.CollectionsId).TicketId;
        var r = _db.AddTicket(context, _db.RegistrationId).TicketId;
        _db.AddTicket(context, _db.CustomerServiceId);
        return (c1, c2, c3, r);
    }

    [Fact]
    public async Task Admin_AllDepartments_SeesEverything_AndAnyDepartmentNarrowsIt()
    {
        var t = Seed();

        var all = await QueueAsync(_db.CsAgentId, Roles.SystemAdministrator, null);
        var collections = await QueueAsync(_db.CsAgentId, Roles.SystemAdministrator, _db.CollectionsId);
        var registration = await QueueAsync(_db.CsAgentId, Roles.SystemAdministrator, _db.RegistrationId);

        Assert.Equal(5, all.TotalCount);
        Assert.Equal(3, collections.TotalCount);
        Assert.Equal([t.Collections1, t.Collections2, t.Collections3], collections.Items.Select(i => i.TicketId).Order());
        Assert.Equal([t.Registration], registration.Items.Select(i => i.TicketId));
    }

    [Fact]
    public async Task Manager_AllDepartments_SeesEverything_AndFilterNarrows()
    {
        Seed();

        Assert.Equal(5, (await QueueAsync(_db.CsAgentId, Roles.CsManager, null)).TotalCount);
        Assert.Equal(1, (await QueueAsync(_db.CsAgentId, Roles.CsManager, _db.RegistrationId)).TotalCount);
    }

    [Fact]
    public async Task DepartmentHead_SeesOnlyOwnDepartment_AndTamperedDepartmentIdsRevealNothing()
    {
        Seed();

        var defaultScope = await QueueAsync(_db.CollectionsHeadId, Roles.DepartmentHead, null);
        var own = await QueueAsync(_db.CollectionsHeadId, Roles.DepartmentHead, _db.CollectionsId);
        var otherDepartment = await QueueAsync(_db.CollectionsHeadId, Roles.DepartmentHead, _db.RegistrationId);
        var nonexistent = await QueueAsync(_db.CollectionsHeadId, Roles.DepartmentHead, 99999);

        Assert.Equal(3, defaultScope.TotalCount);
        Assert.Equal(3, own.TotalCount);
        Assert.Equal(0, otherDepartment.TotalCount);
        Assert.Empty(otherDepartment.Items);
        Assert.Equal(0, nonexistent.TotalCount);
    }

    [Fact]
    public async Task RegularEmployee_OtherDepartmentsFilter_NeverWidensTheirAssignmentOnlyScope()
    {
        long mine;
        using (var context = _db.CreateContext())
        {
            mine = _db.AddTicket(context, _db.RegistrationId, owner: _db.RegistrationEmployeeId).TicketId;
            _db.AddTicket(context, _db.RegistrationId);
            _db.AddTicket(context, _db.CollectionsId);
        }

        var all = await QueueAsync(_db.RegistrationEmployeeId, Roles.DepartmentEmployee, null);
        var foreign = await QueueAsync(_db.RegistrationEmployeeId, Roles.DepartmentEmployee, _db.CollectionsId);

        Assert.Equal([mine], all.Items.Select(i => i.TicketId));
        Assert.Equal(0, foreign.TotalCount);
    }

    [Fact]
    public async Task DepartmentFilter_PagesAndTotals_ReflectTheFilteredResults_AndCombineWithOtherFilters()
    {
        Seed();

        var page1 = await QueueAsync(_db.CsAgentId, Roles.SystemAdministrator, _db.CollectionsId, page: 1, pageSize: 2);
        var page2 = await QueueAsync(_db.CsAgentId, Roles.SystemAdministrator, _db.CollectionsId, page: 2, pageSize: 2);
        var closedOnly = await QueueAsync(_db.CsAgentId, Roles.SystemAdministrator, _db.CollectionsId, ticketStatus: "Closed");

        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(3, page2.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        Assert.Empty(page1.Items.Select(i => i.TicketId).Intersect(page2.Items.Select(i => i.TicketId)));
        Assert.Equal(0, closedOnly.TotalCount);
    }
}
