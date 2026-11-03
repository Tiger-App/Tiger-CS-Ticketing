using TigerCS.Application.Modules.Ticketing.ActivityTimeline;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.ActivityTimeline;

/// <summary>The two read queries the Activity timeline adds, as real SQL over the real schema: they must translate, filter by ticket and conversation, and never write.</summary>
public sealed class ActivityTimelineQueriesSqliteTests : IDisposable
{
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);
    private static DateTime Now => DashboardSqliteFixture.Now;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AuditReader_ReturnsOnlyTheTicketsOwnEntriesAndItsConversations_ForTheRequestedActions_OldestFirst()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            ticketId = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            var other = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            var actor = Guid.NewGuid();

            AuditEntry E(string action, string type, string id, int minute) =>
                new(actor, action, type, id, null, "x", Guid.NewGuid(), Now.AddMinutes(minute));

            context.AuditEntries.AddRange(
                E(TimelineAuditActions.AutoCloseCustomerInactivity, "Ticket", ticketId.ToString(), 5),
                E("GenesysRoutingChanged", "GenesysConversation", "conv-a", 2),
                E("GenesysRoutingChanged", "GenesysConversation", "conv-other", 3),
                E(TimelineAuditActions.AutoCloseCustomerInactivity, "Ticket", other.ToString(), 4),
                E("SomeUnrelatedAction", "Ticket", ticketId.ToString(), 1),
                E("GenesysRoutingChanged", "Ticket", "conv-a", 6)); // right action and id, wrong entity type
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var before = read.AuditEntries.Count();
        var entries = await new AuditEntryReader(read).ListForTicketAsync(ticketId, ["conv-a"], TimelineAuditActions.All);

        Assert.Equal(["GenesysRoutingChanged", TimelineAuditActions.AutoCloseCustomerInactivity], entries.Select(e => e.Action).ToArray());
        Assert.Equal(before, read.AuditEntries.Count()); // read-only
    }

    [Fact]
    public async Task AuditReader_WithNoConversations_StillReturnsTheTicketEntries()
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            ticketId = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            context.AuditEntries.Add(new AuditEntry(null, TimelineAuditActions.AutoCloseByAi, "Ticket", ticketId.ToString(), null, "x", Guid.NewGuid(), Now));
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var entries = await new AuditEntryReader(read).ListForTicketAsync(ticketId, [], TimelineAuditActions.All);

        Assert.Single(entries);
    }

    [Fact]
    public async Task AssignmentRepository_ListsEveryAssignmentOfTheTicket_CurrentAndSuperseded_OldestFirst()
    {
        long ticketId;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var context = _db.CreateContext())
        {
            ticketId = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            var otherTicket = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
            var superseded = new TicketAssignment(ticketId, first, _db.CustomerServiceId, Now.AddMinutes(1), null);
            superseded.MarkSuperseded();
            context.TicketAssignments.AddRange(
                new TicketAssignment(ticketId, second, _db.CustomerServiceId, Now.AddMinutes(9), first),
                superseded,
                new TicketAssignment(otherTicket, Guid.NewGuid(), _db.CustomerServiceId, Now.AddMinutes(2), null));
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var list = await new TicketAssignmentRepository(read).ListByTicketIdAsync(ticketId);

        Assert.Equal([first, second], list.Select(a => a.AssignedEmployeeId).ToArray());
        Assert.Equal([false, true], list.Select(a => a.IsCurrent).ToArray());
    }
}
