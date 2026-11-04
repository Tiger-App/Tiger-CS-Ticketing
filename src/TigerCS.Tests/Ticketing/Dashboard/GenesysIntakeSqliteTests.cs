using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.TakeOwnership;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// The set-based SQL form of the CS intake / classification queue rule (<c>TicketQueryFilters.GenesysIntake</c>), on real SQL (SQLite): the
/// predicate must translate, include exactly the unassigned Genesys-originated claimable tickets, and add nothing else - and, without the
/// intake flag, must add nothing at all.
/// </summary>
public sealed class GenesysIntakeSqliteTests : IDisposable
{
    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private static TicketQuery Query(Guid agent, bool includeIntake) => new(
        VisibleDepartmentIds: [], DepartmentId: null, CategoryId: null, PriorityId: null, TicketStatus: null, VerificationStatus: null,
        OwnerEmployeeId: null, Search: null, SortBy: TicketSortBy.CreatedAtUtc, SortDescending: true, Page: 1, PageSize: 100,
        AgentScopeEmployeeId: agent, OwnerVisibilityEmployeeId: agent, IncludeGenesysIntake: includeIntake);

    private long Seed(Microsoft.EntityFrameworkCore.DbContext context, int departmentId, Guid? owner = null, bool genesys = false, bool requiresHuman = true, bool manualInteraction = false)
    {
        var dbContext = (TigerCS.Infrastructure.Persistence.TigerCsDbContext)context;
        var ticket = _db.AddTicket(dbContext, departmentId, owner: owner);
        if (genesys)
        {
            dbContext.TicketInteractions.Add(ClaimTestData.Genesys(ticket.TicketId, requiresHuman: requiresHuman));
        }

        if (manualInteraction)
        {
            dbContext.TicketInteractions.Add(TicketInteraction.CreateLocal(ticket.TicketId, 1, "+971500000000", DateTime.UtcNow, isOriginatingInteraction: true));
        }

        dbContext.SaveChanges();
        return ticket.TicketId;
    }

    [Fact]
    public async Task TheIntakeQueue_AddsOnlyUnassignedGenesysOriginatedClaimableTickets_AndOnlyWhenAskedFor()
    {
        long intake, intakeElsewhere, assignedGenesys, manual, manualWithInteraction, activeAi;
        var agent = _db.CsAgentId;
        using (var context = _db.CreateContext())
        {
            intake = Seed(context, _db.CustomerServiceId, genesys: true);
            intakeElsewhere = Seed(context, _db.CollectionsId, genesys: true);
            assignedGenesys = Seed(context, _db.CustomerServiceId, owner: _db.LifecycleWorkerId, genesys: true);
            manual = Seed(context, _db.CollectionsId);
            manualWithInteraction = Seed(context, _db.CollectionsId, manualInteraction: true);
            activeAi = Seed(context, _db.CustomerServiceId, genesys: true, requiresHuman: false);
        }

        using var read = _db.CreateContext();
        var repository = new TicketRepository(read);

        var without = (await repository.SearchAsync(Query(agent, includeIntake: false))).Items.Select(t => t.TicketId).ToList();
        Assert.Empty(without);   // an agent's scope is their own tickets: nothing is added unless the intake flag is set

        var with = (await repository.SearchAsync(Query(agent, includeIntake: true))).Items.Select(t => t.TicketId).Order().ToArray();
        Assert.Equal(new[] { intake, intakeElsewhere }.Order(), with);
        Assert.DoesNotContain(assignedGenesys, with);        // somebody else's ticket
        Assert.DoesNotContain(manual, with);                  // unrelated workflow
        Assert.DoesNotContain(manualWithInteraction, with);   // a local (non-Genesys) interaction is not Genesys intake
        Assert.DoesNotContain(activeAi, with);                // the chatbot is still handling it
    }

    [Fact]
    public async Task OnceAssigned_ATicketLeavesTheIntakeQueue_AndOnlyItsAssigneeStillSeesIt()
    {
        long ticketId;
        var agent = _db.CsAgentId;
        using (var context = _db.CreateContext())
        {
            ticketId = Seed(context, _db.CustomerServiceId, genesys: true);
        }

        using (var read = _db.CreateContext())
        {
            Assert.Contains(ticketId, (await new TicketRepository(read).SearchAsync(Query(agent, includeIntake: true))).Items.Select(t => t.TicketId));
        }

        using (var write = _db.CreateContext())
        {
            write.Tickets.Single(t => t.TicketId == ticketId).AssignTo(_db.LifecycleWorkerId);
            write.SaveChanges();
        }

        using var after = _db.CreateContext();
        var repository = new TicketRepository(after);
        Assert.DoesNotContain(ticketId, (await repository.SearchAsync(Query(agent, includeIntake: true))).Items.Select(t => t.TicketId));
        Assert.Contains(ticketId, (await repository.SearchAsync(Query(_db.LifecycleWorkerId, includeIntake: false))).Items.Select(t => t.TicketId));
    }
}
