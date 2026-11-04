using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// My Tickets: an employee sees EVERY ticket currently assigned to them — in the ticket list, the "mine" filter, the dashboard counts and the
/// ticket detail — whatever department it originated in or sits in now, and never another employee's ticket, or an unassigned one, merely
/// because it sits in a department they belong to (a regular Department Employee's visibility is assignment-only).
/// Real SQL (SQLite) so the set-based visibility predicate is exercised, not just the per-ticket rule.
/// </summary>
public sealed class MyTicketsVisibilityTests : IDisposable
{
    private static readonly string[] DepartmentEmployee = [Roles.DepartmentEmployee];
    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private static TicketListRequestDto List(Guid? owner = null) =>
        new(null, null, null, null, null, owner, null, null, null, 1, 50);

    private async Task<IReadOnlyCollection<long>> ListedAsync(Guid caller, Guid? owner = null)
    {
        using var context = _db.CreateContext();
        var result = await _db.CreateQueryService(context).GetQueueAsync(caller, DepartmentEmployee, List(owner));
        return [.. result.Items.Select(i => i.TicketId)];
    }

    [Fact]
    public async Task AssignedTicket_InAnotherDepartment_IsListedForItsOwner_AndOnlyForThem()
    {
        long mine, someoneElses, ownDepartment;
        using (var context = _db.CreateContext())
        {
            // Originated in (and now sits in) a department the employee does not belong to, but is assigned to them.
            mine = _db.AddTicket(context, _db.CustomerServiceId, owner: _db.RegistrationEmployeeId).TicketId;
            // Another employee's ticket in that same foreign department.
            someoneElses = _db.AddTicket(context, _db.CustomerServiceId, owner: _db.CsAgentId).TicketId;
            ownDepartment = _db.AddTicket(context, _db.RegistrationId).TicketId;
        }

        var listed = await ListedAsync(_db.RegistrationEmployeeId);

        Assert.Contains(mine, listed);
        Assert.DoesNotContain(ownDepartment, listed);  // their own department's unassigned queue is NOT theirs to see
        Assert.DoesNotContain(someoneElses, listed);   // never someone else's ticket
    }

    [Fact]
    public async Task MineFilter_ReturnsEveryTicketAssignedToTheEmployee_IncludingOtherDepartments()
    {
        long inOther, inOwn;
        using (var context = _db.CreateContext())
        {
            inOther = _db.AddTicket(context, _db.CollectionsId, owner: _db.RegistrationEmployeeId).TicketId;
            inOwn = _db.AddTicket(context, _db.RegistrationId, owner: _db.RegistrationEmployeeId).TicketId;
            _db.AddTicket(context, _db.RegistrationId);   // unowned queue ticket: not "mine"
        }

        var mine = await ListedAsync(_db.RegistrationEmployeeId, owner: _db.RegistrationEmployeeId);

        Assert.Equal([inOther, inOwn], mine.Order());
    }

    [Fact]
    public async Task DashboardMyTickets_CountsTicketsAssignedToTheEmployeeInAnyDepartment()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId, owner: _db.RegistrationEmployeeId);
            _db.AddTicket(context, _db.RegistrationId, owner: _db.RegistrationEmployeeId);
            _db.AddTicket(context, _db.CollectionsId, owner: _db.CollectionsHeadId);   // not theirs
        }

        using var scope = _db.CreateContext();
        var overview = await _db.CreateService(scope).GetOverviewAsync(
            _db.RegistrationEmployeeId, DepartmentEmployee, new DashboardOverviewRequestDto());

        Assert.Equal(2, overview.Kpis.MyTickets);
        Assert.Equal(2, overview.Kpis.OpenTickets);   // the foreign-department ticket they own is visible; the head's is not
    }

    [Fact]
    public async Task TicketDetail_IsOpenToItsAssignee_WhateverTheDepartment_ButNotToAnotherEmployee()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            ticketId = _db.AddTicket(context, _db.CollectionsId, owner: _db.RegistrationEmployeeId).TicketId;
        }

        using var scope = _db.CreateContext();
        var query = _db.CreateQueryService(scope);

        var owner = await query.GetDetailAsync(_db.RegistrationEmployeeId, DepartmentEmployee, ticketId);
        var stranger = await query.GetDetailAsync(Guid.NewGuid(), DepartmentEmployee, ticketId);

        Assert.Equal(TicketQueryOutcome.Success, owner.Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, stranger.Outcome);
    }

    [Fact]
    public async Task AfterATransfer_ThePreviousOwnerNoLongerSeesTheTicketInAnotherDepartmentOrActsOnIt()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            ticketId = _db.AddTicket(context, _db.CollectionsId, owner: _db.RegistrationEmployeeId).TicketId;
            var ticket = context.Tickets.Single(t => t.TicketId == ticketId);
            ticket.TransferToDepartment(_db.CustomerServiceId);   // ownership ends with the transfer
            ticket.AssignTo(_db.CsAgentId);
            context.SaveChanges();
        }

        var listed = await ListedAsync(_db.RegistrationEmployeeId);

        Assert.DoesNotContain(ticketId, listed);
        Assert.Contains(ticketId, await ListedAsync(_db.CsAgentId));   // the new owner sees it (their own department anyway)
    }
}
