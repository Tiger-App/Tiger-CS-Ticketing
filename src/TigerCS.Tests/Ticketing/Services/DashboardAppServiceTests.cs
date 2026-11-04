using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The Dashboard aggregate (Customer Workspace phase). What matters here is
/// scoping: every count must honor the same visible-department resolution
/// as the ticket queue — a department user's dashboard covers their own
/// departments only, a CS-layer role's covers everything — and My Tickets
/// counts the caller's own active tickets, never someone else's.
/// </summary>
public class DashboardAppServiceTests
{
    private sealed record Fixture(
        DashboardAppService Service,
        FakeTicketRepository Tickets,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketResolutionRepository Resolutions);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var resolutions = new FakeTicketResolutionRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        tickets.Resolutions = resolutions;
        var queryService = new TicketQueryAppService(
            tickets, departmentAssignments, resolutions, statusHistory, ReopenPolicy.Default, TimeProvider.System);
        return new Fixture(
            new DashboardAppService(tickets, queryService, TimeProvider.System, new FakeDashboardQueryRepository(), departmentAssignments),
            tickets, departmentAssignments, resolutions);
    }

    private static async Task<Ticket> SeedTicketAsync(
        FakeTicketRepository repo, int departmentId, byte priorityId = (byte)PriorityLevel.Medium, Guid? owner = null)
    {
        var ticket = Ticket.CreateUnverified(
            $"TG-CS-20260901-{Guid.NewGuid():N}"[..22], departmentId, categoryId: 5, priorityId,
            "Dashboard seed ticket", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        if (owner is { } ownerId)
        {
            ticket.AssignTo(ownerId);
        }

        return ticket;
    }

    [Fact]
    public async Task GetSummaryAsync_CrossDepartmentRole_CountsAcrossEveryDepartment()
    {
        var f = CreateService();
        await SeedTicketAsync(f.Tickets, departmentId: 1);
        await SeedTicketAsync(f.Tickets, departmentId: 2, priorityId: (byte)PriorityLevel.Critical);
        await SeedTicketAsync(f.Tickets, departmentId: 3, owner: Guid.NewGuid());

        var result = await f.Service.GetSummaryAsync(Guid.NewGuid(), [Roles.CsSupervisor]);

        Assert.Equal(3, result.OpenTickets);
        Assert.Equal(2, result.Unassigned);
        Assert.Equal(1, result.CriticalOrHigh);
    }

    [Fact]
    public async Task GetSummaryAsync_DepartmentEmployee_CountsOnlyTicketsAssignedToThem_WhateverTheirDepartment()
    {
        var f = CreateService();
        var caller = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 2, true, DateTime.UtcNow, null));
        // Assigned to the employee: one in their own department and one that now sits in ANOTHER department (e.g. after a transfer).
        await SeedTicketAsync(f.Tickets, departmentId: 2, owner: caller);
        await SeedTicketAsync(f.Tickets, departmentId: 9, priorityId: (byte)PriorityLevel.Critical, owner: caller);
        // Not theirs: a colleague's ticket and an unassigned one in their own department, and another department's ticket.
        await SeedTicketAsync(f.Tickets, departmentId: 2, owner: Guid.NewGuid());
        await SeedTicketAsync(f.Tickets, departmentId: 2);
        await SeedTicketAsync(f.Tickets, departmentId: 9, priorityId: (byte)PriorityLevel.Critical);

        var result = await f.Service.GetSummaryAsync(caller, [Roles.DepartmentEmployee]);

        Assert.Equal(2, result.OpenTickets);
        Assert.Equal(1, result.CriticalOrHigh);
        Assert.Equal(0, result.Unassigned);   // department-queue work is not theirs to see
        Assert.Equal(2, result.MyTickets);
        Assert.All(result.AttentionTickets, t => Assert.Equal(caller, t.CurrentOwnerEmployeeId));
    }

    [Fact]
    public async Task GetSummaryAsync_MyTickets_CountsOnlyTicketsTheCallerCurrentlyOwns()
    {
        var f = CreateService();
        var caller = Guid.NewGuid();
        await SeedTicketAsync(f.Tickets, departmentId: 1, owner: caller);
        await SeedTicketAsync(f.Tickets, departmentId: 1, owner: caller);
        await SeedTicketAsync(f.Tickets, departmentId: 1, owner: Guid.NewGuid());
        await SeedTicketAsync(f.Tickets, departmentId: 1);

        // A supervisor sees every open ticket; MyTickets is still only the caller's own.
        var result = await f.Service.GetSummaryAsync(caller, [Roles.CsSupervisor]);

        Assert.Equal(2, result.MyTickets);
        Assert.Equal(4, result.OpenTickets);

        // A CS Agent's whole dashboard is the tickets assigned to them (nothing here was handled on Genesys).
        var agentResult = await f.Service.GetSummaryAsync(caller, [Roles.CsAgent]);

        Assert.Equal(2, agentResult.MyTickets);
        Assert.Equal(2, agentResult.OpenTickets);
    }

    [Fact]
    public async Task GetSummaryAsync_ResolvedAndClosedTickets_AreNotCountedAsOpen()
    {
        var f = CreateService();
        var open = await SeedTicketAsync(f.Tickets, departmentId: 1);
        var resolved = await SeedTicketAsync(f.Tickets, departmentId: 1, owner: Guid.NewGuid());
        resolved.ChangeStatus(TicketStatus.InProgress);
        resolved.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);

        var result = await f.Service.GetSummaryAsync(Guid.NewGuid(), [Roles.CsManager]);

        Assert.Equal(1, result.OpenTickets);
        Assert.Contains(result.AttentionTickets, t => t.TicketId == open.TicketId);
        Assert.DoesNotContain(result.AttentionTickets, t => t.TicketId == resolved.TicketId);
    }

    [Fact]
    public async Task GetSummaryAsync_ReopenedActiveTickets_AreCounted()
    {
        var f = CreateService();
        var caller = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 1, owner: caller);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        ticket.Reopen(ticket.CurrentDepartmentId);

        var result = await f.Service.GetSummaryAsync(caller, [Roles.CsManager]);

        Assert.Equal(1, result.Reopened);
        Assert.Equal(1, result.OpenTickets);
    }

    [Fact]
    public async Task GetSummaryAsync_ResolvedToday_CountsCurrentResolutionsSinceTheUtcDayStart()
    {
        var f = CreateService();
        var today = await SeedTicketAsync(f.Tickets, departmentId: 1, owner: Guid.NewGuid());
        today.ChangeStatus(TicketStatus.InProgress);
        today.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        await f.Resolutions.AddAsync(new TicketResolution(
            today.TicketId, ResolutionOutcome.Resolved, "Done.", null, null, Guid.NewGuid(), DateTime.UtcNow));

        var yesterday = await SeedTicketAsync(f.Tickets, departmentId: 1, owner: Guid.NewGuid());
        yesterday.ChangeStatus(TicketStatus.InProgress);
        yesterday.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        await f.Resolutions.AddAsync(new TicketResolution(
            yesterday.TicketId, ResolutionOutcome.Resolved, "Done earlier.", null, null, Guid.NewGuid(), DateTime.UtcNow.AddDays(-2)));

        var result = await f.Service.GetSummaryAsync(Guid.NewGuid(), [Roles.CsManager]);

        Assert.Equal(1, result.ResolvedToday);
    }

    [Fact]
    public async Task GetSummaryAsync_AttentionList_PutsBreachedAndCriticalFirst_AndNeverExposesLongText()
    {
        var f = CreateService();
        var routine = await SeedTicketAsync(f.Tickets, departmentId: 1, priorityId: (byte)PriorityLevel.High, owner: Guid.NewGuid());
        var breached = await SeedTicketAsync(f.Tickets, departmentId: 1, owner: Guid.NewGuid());
        breached.MarkSlaBreached();

        var result = await f.Service.GetSummaryAsync(Guid.NewGuid(), [Roles.CsManager]);

        Assert.Equal(breached.TicketId, result.AttentionTickets[0].TicketId);
        Assert.Contains(result.AttentionTickets, t => t.TicketId == routine.TicketId);
        // Compact by design: the row carries the one-line request summary and
        // display snapshots — no description field exists to leak.
        Assert.All(result.AttentionTickets, t => Assert.Equal("Dashboard seed ticket", t.RequestSummary));
    }
}
