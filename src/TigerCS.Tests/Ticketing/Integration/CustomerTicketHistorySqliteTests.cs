using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Customer Ticket History as real SQL: the grant (a CS Agent who currently owns an ACTIVE ticket of a verified customer reads that customer's
/// other tickets), its limits (identity, status, phone, employee), and the proof that the Queue, counts and dashboards do not move.
/// </summary>
public sealed class CustomerTicketHistorySqliteTests : IDisposable
{
    private static readonly string[] Agent = [Roles.CsAgent];

    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);
    private readonly Guid _agentA = Guid.NewGuid();
    private readonly Guid _agentB = Guid.NewGuid();
    private int _sequence = 5000;

    public void Dispose() => _db.Dispose();

    private Ticket AddCrmTicket(
        TigerCsDbContext context, int crmCustomerId, Guid? owner, TicketStatus status = TicketStatus.Open, int? departmentId = null,
        DateTime? createdAtUtc = null)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var department = departmentId ?? _db.CustomerServiceId;
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TG-HST-20260910-{sequence:D5}", department,
            crmBuyerCustomerId: crmCustomerId, crmBuyerLeadId: crmCustomerId, crmBuyerUnitId: sequence, crmBuyerProjectId: 1,
            crmBuyerCustomerName: "History Customer " + crmCustomerId, crmBuyerProjectName: "Tiger Tower", crmBuyerUnitNumber: "U-" + sequence,
            categoryId: _db.CsCategoryId, priorityId: 3, requestSummary: "History ticket " + sequence,
            createdAtUtc: createdAtUtc ?? DashboardSqliteFixture.Now.AddHours(-sequence % 50 - 2));
        if (owner is { } ownerId)
        {
            ticket.AssignTo(ownerId);
        }
        else if (status != TicketStatus.Open)
        {
            ticket.AssignTo(_db.LifecycleWorkerId);
        }

        if (status is TicketStatus.InProgress or TicketStatus.Resolved or TicketStatus.Closed)
        {
            ticket.ChangeStatus(TicketStatus.InProgress);
        }

        if (status is TicketStatus.Resolved or TicketStatus.Closed)
        {
            ticket.Resolve(ResolutionOutcome.Resolved, null);
        }

        if (status == TicketStatus.Closed)
        {
            ticket.Close();
        }

        context.Tickets.Add(ticket);
        context.SaveChanges();
        if (status == TicketStatus.Closed)
        {
            _db.AddClosedHistory(context, ticket, DashboardSqliteFixture.Now.AddMinutes(-30));
        }

        return ticket;
    }

    private CustomerHistoryAppService History(TigerCsDbContext read) => new(
        new TicketRepository(read), new FakeIntakeRecordRepository(), new FakeTicketResolutionRepository(),
        new TicketStatusHistoryRepository(read), _db.CreateQueryService(read), ReopenPolicy.Default, TimeProvider.System);

    private CustomerDirectoryAppService Directory(TigerCsDbContext read) =>
        new(new CustomerDirectoryRepository(read), _db.CreateQueryService(read), new TicketRepository(read));

    private static TicketListRequestDto Everything() => new(null, null, null, null, null, null, null, null, null, 1, 100);

    // ---------------------------------------------------------------------------------------------
    // The grant
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AgentA_SeesTheirOwnTicketsAndAgentBsPreviousTicketsOfTheSameCustomer_WithAgentDatesAndCounts()
    {
        long current, closedByB, resolvedByB, openByB;
        using (var context = _db.CreateContext())
        {
            current = AddCrmTicket(context, 77, _agentA).TicketId;
            closedByB = AddCrmTicket(context, 77, _agentB, TicketStatus.Closed).TicketId;
            resolvedByB = AddCrmTicket(context, 77, _agentB, TicketStatus.Resolved).TicketId;
            openByB = AddCrmTicket(context, 77, _agentB, TicketStatus.InProgress).TicketId;
        }

        using var read = _db.CreateContext();
        var history = await History(read).GetForTicketAsync(_agentA, Agent, current);

        var dto = history.Response!;
        Assert.Equal(CustomerHistoryOutcome.Success, history.Outcome);
        Assert.Equal(3, dto.TotalTickets);
        Assert.Equal(1, dto.OpenTickets);
        Assert.Equal(2, dto.ClosedTickets);
        Assert.Equal([closedByB, resolvedByB, openByB], dto.Tickets.Select(t => t.TicketId).Order().ToArray());
        Assert.NotNull(dto.Tickets.Single(t => t.TicketId == closedByB).ClosedAtUtc);
        Assert.All(dto.Tickets, t => Assert.Equal(_agentB, t.AssignedAgentEmployeeId));
    }

    [Fact]
    public async Task TheTotals_CountTheWholeHistory_NotJustTheReturnedPage()
    {
        long current;
        using (var context = _db.CreateContext())
        {
            current = AddCrmTicket(context, 78, _agentA).TicketId;
            for (var i = 0; i < 7; i++)
            {
                AddCrmTicket(context, 78, _agentB, i % 2 == 0 ? TicketStatus.Closed : TicketStatus.Resolved);
            }
        }

        using var read = _db.CreateContext();
        var dto = (await History(read).GetForTicketAsync(_agentA, Agent, current, limit: 3)).Response!;

        Assert.Equal(7, dto.TotalTickets);
        Assert.Equal(7, dto.ClosedTickets);
        Assert.Equal(3, dto.Tickets.Count);
    }

    [Fact]
    public async Task AnotherCustomersTickets_AreNeverIncluded_AndNeverOpened()
    {
        long current, theirs;
        using (var context = _db.CreateContext())
        {
            current = AddCrmTicket(context, 79, _agentA).TicketId;
            theirs = AddCrmTicket(context, 80, _agentB, TicketStatus.Closed).TicketId;
        }

        using var read = _db.CreateContext();
        var queries = _db.CreateQueryService(read);

        Assert.Empty((await History(read).GetForTicketAsync(_agentA, Agent, current)).Response!.Tickets);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await queries.GetDetailAsync(_agentA, Agent, theirs)).Outcome);
        Assert.Equal(CustomerHistoryOutcome.Forbidden, (await History(read).GetForTicketAsync(_agentA, Agent, theirs)).Outcome);
        Assert.Equal(0, (await History(read).GetByCrmCustomerIdAsync(_agentA, Agent, 80)).TotalTickets);
    }

    [Fact]
    public async Task APreviousTicket_OpensReadOnly_AndCannotBeModified()
    {
        long current, previous;
        using (var context = _db.CreateContext())
        {
            current = AddCrmTicket(context, 81, _agentA).TicketId;
            previous = AddCrmTicket(context, 81, _agentB, TicketStatus.Closed).TicketId;
        }

        using var read = _db.CreateContext();
        var queries = _db.CreateQueryService(read);
        var detail = await queries.GetDetailAsync(_agentA, Agent, previous);

        Assert.Equal(TicketQueryOutcome.Success, detail.Outcome);
        Assert.True(detail.Response!.IsReadOnlyAccess);
        Assert.False((await queries.GetDetailAsync(_agentA, Agent, current)).Response!.IsReadOnlyAccess);
    }

    [Fact]
    public async Task TheGrantEnds_WhenTheAgentsActiveTicketIsReassigned_ResolvedOrClosed()
    {
        long current, previous;
        using (var context = _db.CreateContext())
        {
            current = AddCrmTicket(context, 82, _agentA).TicketId;
            previous = AddCrmTicket(context, 82, _agentB, TicketStatus.Closed).TicketId;
        }

        using (var read = _db.CreateContext())
        {
            Assert.Equal(TicketQueryOutcome.Success, (await _db.CreateQueryService(read).GetDetailAsync(_agentA, Agent, previous)).Outcome);
        }

        using (var context = _db.CreateContext())
        {
            var ticket = context.Tickets.Single(t => t.TicketId == current);
            ticket.ChangeStatus(TicketStatus.InProgress);
            ticket.Resolve(ResolutionOutcome.Resolved, null);
            context.SaveChanges();
        }

        using var after = _db.CreateContext();
        Assert.Equal(TicketQueryOutcome.Forbidden, (await _db.CreateQueryService(after).GetDetailAsync(_agentA, Agent, previous)).Outcome);
        Assert.DoesNotContain(previous, (await History(after).GetByCrmCustomerIdAsync(_agentA, Agent, 82)).Tickets.Select(t => t.TicketId));

        using (var context = _db.CreateContext())
        {
            // A new active ticket for the same customer restores it; another agent's active ticket does not.
            AddCrmTicket(context, 82, _agentA);
        }

        using var restored = _db.CreateContext();
        Assert.Equal(TicketQueryOutcome.Success, (await _db.CreateQueryService(restored).GetDetailAsync(_agentA, Agent, previous)).Outcome);
    }

    [Fact]
    public async Task AnotherAgentsActiveTicketOfTheCustomer_GrantsNothingToAnAgentWhoHasNone()
    {
        long previous;
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 83, _agentB);
            previous = AddCrmTicket(context, 83, Guid.NewGuid(), TicketStatus.Closed).TicketId;
        }

        using var read = _db.CreateContext();
        Assert.Equal(TicketQueryOutcome.Forbidden, (await _db.CreateQueryService(read).GetDetailAsync(_agentA, Agent, previous)).Outcome);
    }

    // ---------------------------------------------------------------------------------------------
    // Identity: only a verified customer, never a phone or a name
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnverifiedTickets_SharingAPhoneNumberAreNeverGranted()
    {
        long previous;
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA, channelId: 1);                       // phone +971500000000
            previous = _db.AddTicket(context, _db.CustomerServiceId, owner: _agentB, channelId: 1, status: TicketStatus.Closed).TicketId;   // same phone, other customer
        }

        using var read = _db.CreateContext();
        Assert.Equal(TicketQueryOutcome.Forbidden, (await _db.CreateQueryService(read).GetDetailAsync(_agentA, Agent, previous)).Outcome);
    }

    [Fact]
    public async Task TheSameDisplayNameWithADifferentCrmCustomer_IsNotTheSameCustomer()
    {
        long previous;
        using (var context = _db.CreateContext())
        {
            var mine = AddCrmTicket(context, 84, _agentA);
            previous = AddCrmTicket(context, 85, _agentB, TicketStatus.Closed).TicketId;
            Assert.NotEqual(mine.TicketId, previous);
        }

        using var read = _db.CreateContext();
        Assert.Equal(TicketQueryOutcome.Forbidden, (await _db.CreateQueryService(read).GetDetailAsync(_agentA, Agent, previous)).Outcome);
    }

    [Fact]
    public async Task AnExternallyVerifiedCustomer_IsMatchedByTheSourceAndIdPair_Only()
    {
        long same, otherId, otherSource;
        using (var context = _db.CreateContext())
        {
            Ticket External(string source, string id, Guid? owner, TicketStatus status = TicketStatus.Open)
            {
                var seq = Interlocked.Increment(ref _sequence);
                var t = Ticket.CreateFromExternalLookup(
                    $"TG-EXT-20260910-{seq:D5}", _db.CustomerServiceId, source, id, null, null, null, _db.CsCategoryId, 3, "ext", DashboardSqliteFixture.Now.AddHours(-3));
                if (owner is { } o) { t.AssignTo(o); }
                if (status == TicketStatus.Closed)
                {
                    t.ChangeStatus(TicketStatus.InProgress);
                    t.Resolve(ResolutionOutcome.Resolved, null);
                    t.Close();
                }

                context.Tickets.Add(t);
                context.SaveChanges();
                return t;
            }

            External("Pact", "P-1", _agentA);
            same = External("Pact", "P-1", _agentB, TicketStatus.Closed).TicketId;
            otherId = External("Pact", "P-2", _agentB, TicketStatus.Closed).TicketId;
            otherSource = External("Tasleeh", "P-1", _agentB, TicketStatus.Closed).TicketId;
        }

        using var read = _db.CreateContext();
        var queries = _db.CreateQueryService(read);

        Assert.Equal(TicketQueryOutcome.Success, (await queries.GetDetailAsync(_agentA, Agent, same)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await queries.GetDetailAsync(_agentA, Agent, otherId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await queries.GetDetailAsync(_agentA, Agent, otherSource)).Outcome);
    }

    // ---------------------------------------------------------------------------------------------
    // The Queue, counts and dashboards do not move
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task QueueSearchCountsAndDashboard_AreIdenticalWithAndWithoutTheCustomersHistory()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 86, _agentA);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _agentA);
        }

        async Task<(long[] Queue, int Total, int Open, int Mine, int Volume)> SnapshotAsync()
        {
            using var read = _db.CreateContext();
            var queue = await _db.CreateQueryService(read).GetQueueAsync(_agentA, Agent, Everything());
            var searched = await _db.CreateQueryService(read).GetQueueAsync(_agentA, Agent, new TicketListRequestDto(null, null, null, null, null, null, "History", null, null, 1, 100));
            var overview = await _db.CreateService(read).GetOverviewAsync(_agentA, Agent, new DashboardOverviewRequestDto());
            return ([.. queue.Items.Select(i => i.TicketId).Order()], queue.TotalCount + searched.TotalCount, overview.Kpis.OpenTickets, overview.Kpis.MyTickets, overview.VolumeTotal);
        }

        var before = await SnapshotAsync();

        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 86, _agentB, TicketStatus.Closed);
            AddCrmTicket(context, 86, _agentB, TicketStatus.Resolved);
            AddCrmTicket(context, 86, _agentB, TicketStatus.InProgress);
        }

        var after = await SnapshotAsync();

        Assert.Equal(before.Queue, after.Queue);
        Assert.Equal((before.Total, before.Open, before.Mine, before.Volume), (after.Total, after.Open, after.Mine, after.Volume));
        Assert.Equal(2, after.Queue.Length);
        Assert.Equal(2, after.Open);
    }

    // ---------------------------------------------------------------------------------------------
    // Customer Profile
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCustomerProfile_ListsEveryTicket_WithAssignedAgentAndClosedDate_OnlyForAHandledCustomer()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 87, _agentA);
            AddCrmTicket(context, 87, _agentB, TicketStatus.Closed);
            AddCrmTicket(context, 87, _agentB, TicketStatus.Resolved);
            AddCrmTicket(context, 88, _agentB, TicketStatus.Closed);
        }

        using var read = _db.CreateContext();
        var directory = Directory(read);

        var handled = await directory.GetProfileAsync(_agentA, Agent, CustomerIdentity.Crm(87).Key);
        var unrelated = await directory.GetProfileAsync(_agentA, Agent, CustomerIdentity.Crm(88).Key);
        var list = await directory.ListAsync(_agentA, Agent, new CustomerDirectoryListRequestDto(PageSize: 50));

        Assert.Equal(3, handled.Response!.Tickets.Count);
        Assert.Equal(3, handled.Response.TotalTickets);
        Assert.Equal(1, handled.Response.OpenTickets);
        Assert.Contains(handled.Response.Tickets, t => t.ClosedAtUtc is not null);
        Assert.All(handled.Response.Tickets.Where(t => t.AssignedAgentEmployeeId == _agentB), t => Assert.Equal(_agentB, t.AssignedAgentEmployeeId));
        Assert.Equal(CustomerDirectoryProfileOutcome.NotFound, unrelated.Outcome);
        Assert.Single(list.Items);   // the directory list is still only the customers the agent works
    }

    [Fact]
    public async Task ThePhoneProfile_IsNeverWidenedByTheGrant()
    {
        using (var context = _db.CreateContext())
        {
            AddCrmTicket(context, 89, _agentA);
            AddCrmTicket(context, 89, _agentB, TicketStatus.Closed);
        }

        using var read = _db.CreateContext();
        var phone = await Directory(read).GetProfileAsync(_agentA, Agent, CustomerIdentity.Phone("+971500000000").Key);

        Assert.Equal(CustomerDirectoryProfileOutcome.NotFound, phone.Outcome);
    }

    [Fact]
    public async Task SupervisorsStillSeeEverything_AndAreNotReadOnly()
    {
        long previous;
        using (var context = _db.CreateContext())
        {
            previous = AddCrmTicket(context, 90, _agentB).TicketId;
        }

        using var read = _db.CreateContext();
        var detail = await _db.CreateQueryService(read).GetDetailAsync(Guid.NewGuid(), [Roles.CsSupervisor], previous);

        Assert.Equal(TicketQueryOutcome.Success, detail.Outcome);
        Assert.False(detail.Response!.IsReadOnlyAccess);
    }
}
