using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// A voice call is answered BEFORE its ticket exists (the ticket is created a moment after pickup), so the first response is dated at the real connect time - earlier than the
/// ticket's creation and the answering agent's assignment. Employee performance must still credit the person who answered (the holder from the ticket's creation), never leave
/// the response unattributed. Run over real SQL (SQLite in-memory) through the repository's own query.
/// </summary>
public sealed class VoiceFirstResponseAttributionSqliteTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);

    public void Dispose() => _db.Dispose();

    private async Task<(Guid? Holder, DateTime At)> MomentAsync(long ticketId)
    {
        using var context = _db.CreateContext();
        var repository = new TeamPerformanceQueryRepository(context);
        var query = (IQueryable)typeof(TeamPerformanceQueryRepository)
            .GetMethod("FirstResponseMetMoments", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repository, [Now.AddDays(-2), Now.AddDays(1)])!;
        var rows = await ((IQueryable<object>)query.Cast<object>()).ToListAsync();
        var row = rows.Single(r => (long)r.GetType().GetProperty("TicketId")!.GetValue(r)! == ticketId);
        return ((Guid?)row.GetType().GetProperty("HolderEmployeeId")!.GetValue(row), (DateTime)row.GetType().GetProperty("AtUtc")!.GetValue(row)!);
    }

    private long Seed(DateTime created, DateTime firstResponseAt, Guid agent)
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: created, owner: agent);
        // The assignment of the answering agent is dated at the ticket's creation (never before the ticket existed).
        context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, agent, ticket.CurrentDepartmentId, created, agent));
        context.SaveChanges();
        context.TicketSlaInstances.Add(TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, (byte)PriorityLevel.Medium, created, created.AddHours(4), created.AddDays(2),
            new AppliedSlaPolicy(SlaClockBasis.TwentyFourSeven, AppliedFirstResponseTargetMinutes: 240, AppliedResolutionTargetMinutes: 2880)));
        ticket.RecordFirstHumanResponse(firstResponseAt);
        context.SaveChanges();
        return ticket.TicketId;
    }

    [Fact]
    public async Task AFirstResponseAtTheConnectTime_BeforeTheTicketExisted_IsCreditedToTheAnsweringAgent_AtItsRealTime()
    {
        var agent = Guid.NewGuid();
        var created = Now.AddHours(-3);
        var connected = created.AddSeconds(-20);                                              // the call was answered 20 s before the ticket was created
        var ticketId = Seed(created, connected, agent);

        var (holder, at) = await MomentAsync(ticketId);

        Assert.Equal(agent, holder);                                                           // credited to the person who answered ...
        Assert.Equal(connected, at);                                                           // ... and dated at the real connect time
    }

    [Fact]
    public async Task AnOrdinaryFirstResponseAfterCreation_IsStillCreditedToTheHolderAtThatMoment()
    {
        var agent = Guid.NewGuid();
        var created = Now.AddHours(-3);
        var ticketId = Seed(created, created.AddMinutes(30), agent);

        var (holder, _) = await MomentAsync(ticketId);

        Assert.Equal(agent, holder);
    }
}
