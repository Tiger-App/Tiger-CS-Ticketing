using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>The evidence query as real SQL over the real schema, classified end to end: every class in the diagnosis is reachable and an owned/non-Open ticket is never listed.</summary>
public sealed class TicketUnassignedWorkRepositorySqliteTests : IDisposable
{
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);
    private static DateTime Now => DashboardSqliteFixture.Now;

    public void Dispose() => _db.Dispose();

    private static TicketInteraction Genesys(Ticket ticket, byte channel = WellKnownChannels.Website, string? agentId = null, DateTime? started = null) =>
        TicketInteraction.CreateFromGenesys(
            ticket.TicketId, channel, "+971500000000", "conv-" + Guid.NewGuid().ToString("N")[..12], calledNumber: null, genesysQueueId: null,
            genesysQueueName: null, genesysAgentId: agentId, genesysAgentName: null, interactionStartedAtUtc: started ?? Now.AddHours(-1),
            direction: "Inbound", createdAtUtc: started ?? Now.AddHours(-1), isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8]);

    private async Task<Dictionary<long, (UnassignedTicketEvidence Evidence, UnassignedTicketClassification Class)>> ClassifyAsync(TimeSpan? stale = null)
    {
        using var context = _db.CreateContext();
        var cc = context.Departments.Single(d => d.Code == "CC").DepartmentId;
        var rows = await new TicketUnassignedWorkRepository(context).ListOpenUnownedAsync(0, 500);
        return rows.ToDictionary(r => r.TicketId, r => (r, UnassignedTicketClassifier.Classify(r, cc, Now, stale)));
    }

    [Fact]
    public async Task EveryKindOfRemainingOpenTicket_IsClassifiedByItsEvidence()
    {
        long manual, activeAi, staleAi, aiEnded, humanFlagged, answered, phoneUnmapped, owned, closed, transferredOut;
        var agent = Guid.NewGuid();
        using (var context = _db.CreateContext())
        {
            var cc = new Department("Call Center", "CC");
            context.Departments.Add(cc);
            context.SaveChanges();

            manual = _db.AddTicket(context, _db.CustomerServiceId).TicketId;

            var a = _db.AddTicket(context, _db.CustomerServiceId); activeAi = a.TicketId;
            context.TicketInteractions.Add(Genesys(a, started: Now.AddMinutes(-5)));

            var s = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-5)); staleAi = s.TicketId;
            context.TicketInteractions.Add(Genesys(s, started: Now.AddDays(-5)));

            var e = _db.AddTicket(context, _db.CustomerServiceId); aiEnded = e.TicketId;
            var ended = Genesys(e); ended.End(Now.AddMinutes(-30), "Customer disconnect"); context.TicketInteractions.Add(ended);

            var h = _db.AddTicket(context, _db.CustomerServiceId); humanFlagged = h.TicketId;
            var flagged = Genesys(h); flagged.MarkRequiresHumanFollowUp(); context.TicketInteractions.Add(flagged);

            var p = _db.AddTicket(context, _db.CustomerServiceId); phoneUnmapped = p.TicketId;
            context.TicketInteractions.Add(Genesys(p, WellKnownChannels.Phone, agentId: "unmapped-genesys-id"));

            context.SaveChanges();

            var n = _db.AddTicket(context, _db.CustomerServiceId); answered = n.TicketId;
            _db.AddHandler(context, n, agent);

            owned = _db.AddTicket(context, _db.CustomerServiceId, owner: Guid.NewGuid()).TicketId;
            closed = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed).TicketId;

            var t = _db.AddTicket(context, _db.CustomerServiceId); transferredOut = t.TicketId;
            t.TransferToDepartment(cc.DepartmentId);
            t.TransferToDepartment(_db.CollectionsId);
            context.TicketWorkflowEvents.Add(TicketWorkflowEvent.ForTransfer(
                t.TicketId, Now.AddHours(-3), actorEmployeeId: Guid.NewGuid(), TicketTransferReason.NeedsEscalation, cc.DepartmentId, _db.CollectionsId, null, "to collections", Guid.NewGuid()));

            context.SaveChanges();
        }

        var result = await ClassifyAsync(stale: TimeSpan.FromHours(24));

        Assert.DoesNotContain(owned, result.Keys);
        Assert.DoesNotContain(closed, result.Keys);
        Assert.Equal(UnassignedTicketClass.ManualOrIntegrationNotRouted, result[manual].Class.Class);
        Assert.Equal(UnassignedTicketClass.ActiveAiConversation, result[activeAi].Class.Class);
        Assert.Equal(UnassignedTicketClass.StaleAiNoEndEvent, result[staleAi].Class.Class);
        Assert.Equal(UnassignedTicketClass.AiEndedNoHumanNeed, result[aiEnded].Class.Class);
        Assert.Equal(UnassignedTicketClass.HumanRequiredNotRouted, result[humanFlagged].Class.Class);
        Assert.Equal(UnassignedTicketClass.HumanAnsweredNotAssigned, result[answered].Class.Class);
        Assert.Equal(agent, result[answered].Evidence.AnsweringEmployeeId);
        Assert.Equal(UnassignedTicketClass.HumanAnsweredAgentNotMapped, result[phoneUnmapped].Class.Class);
        Assert.Equal("unmapped-genesys-id", result[phoneUnmapped].Evidence.UnmappedGenesysAgentId);
        Assert.Equal(UnassignedTicketClass.TransferredOutOfCallCenter, result[transferredOut].Class.Class);
    }

    [Fact]
    public async Task GetEvidence_ReturnsOneTicket_OwnedOrNot_ForTheInTransactionRecheck()
    {
        long id;
        var owner = Guid.NewGuid();
        using (var context = _db.CreateContext()) id = _db.AddTicket(context, _db.CustomerServiceId, owner: owner).TicketId;

        using var read = _db.CreateContext();
        var evidence = await new TicketUnassignedWorkRepository(read).GetEvidenceAsync(id);

        Assert.Equal(owner, evidence!.OwnerEmployeeId);
        Assert.Null(await new TicketUnassignedWorkRepository(read).GetEvidenceAsync(999_999));
    }

    [Fact]
    public async Task Paging_ByTicketId_NeverRepeatsOrSkips()
    {
        using (var context = _db.CreateContext()) for (var i = 0; i < 25; i++) _db.AddTicket(context, _db.CustomerServiceId);

        using var read = _db.CreateContext();
        var repo = new TicketUnassignedWorkRepository(read);
        var seen = new List<long>();
        long after = 0;
        while (await repo.ListOpenUnownedAsync(after, 10) is { Count: > 0 } page)
        {
            seen.AddRange(page.Select(p => p.TicketId));
            after = page[^1].TicketId;
        }

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }
}
