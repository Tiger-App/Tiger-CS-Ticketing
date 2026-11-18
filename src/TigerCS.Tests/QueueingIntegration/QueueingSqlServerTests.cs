using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.QueueingIntegration.Services;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.Integration;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>
/// The same fixture as the endpoint tests, but on a REAL SQL Server with the real migrations, and with TWO application hosts sharing the one database - two
/// "nodes", each with its own in-process lock, so the only thing that can keep them consistent is the database itself. A throw-away database is created and dropped
/// per fixture. Without <c>TIGERCS_TEST_SQL_SERVER</c> the tests are SKIPPED, never "passed".
/// </summary>
public sealed class QueueingSqlServerFixture : QueueingFixture
{
    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsQueueingTest_{Guid.NewGuid():N}";

    public bool Enabled => !string.IsNullOrWhiteSpace(_master);

    public TigerCsApiFactory SecondNode { get; private set; } = null!;

    public string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    protected override TigerCsApiFactory CreateFactory()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        // The REAL migrations, through AddQueueingIntegration: the schema under test is the schema that ships.
        using (var context = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options))
        {
            context.Database.Migrate();
        }

        SecondNode = new TigerCsApiFactory { ExtraConfiguration = HostConfiguration(), SqlServerConnectionString = ConnectionString };
        return new TigerCsApiFactory { ExtraConfiguration = HostConfiguration(), SqlServerConnectionString = ConnectionString };
    }

    public override async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await InitializeCoreAsync();
        // The second node boots its own host (its own singleton options) against the same database.
        _ = SecondNode.Services;
        SecondNode.Services.GetRequiredService<TigerCS.Application.Modules.QueueingIntegration.QueueingIntegrationOptions>().ServiceAccountEmployeeId = ServiceAccountId;
    }

    public HttpClient CreateSecondNodeClient()
    {
        var client = SecondNode.CreateClient();
        client.DefaultRequestHeaders.Add(TigerCS.Api.Integrations.QueueingApiKeyDefaults.DefaultHeaderName, ApiKey);
        return client;
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public override Task DisposeAsync()
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}

public sealed class QueueingSqlServerTests(QueueingSqlServerFixture f) : IClassFixture<QueueingSqlServerFixture>
{
    private HttpClient[] Nodes => [f.CreateClient(), f.CreateSecondNodeClient()];

    private Task<int> CountAsync<T>(Func<TigerCsDbContext, IQueryable<T>> query) where T : class =>
        f.ReadAsync(db => query(db).AsNoTracking().CountAsync());

    // ------------------------------------------------------------------ the schema that ships

    [VisibilitySqlServerFact]
    public async Task TheRealMigrations_CreateTheIndexesTheGuaranteesRestOn()
    {
        var indexes = await f.ReadAsync(async db =>
        {
            var names = new List<string>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            await db.Database.OpenConnectionAsync();
            command.CommandText = "SELECT name + '|' + CAST(is_unique AS varchar(1)) + '|' + ISNULL(filter_definition, '') FROM sys.indexes WHERE name IN "
                + "('UX_QueueingEvents_Source_EventId','UX_Tickets_IntegrationSource_ExternalTicketId','UX_QueueingTicketLinks_TicketId','UX_QueueingTicketLinks_ExternalTicketId','IX_QueueingEvents_PurgeablePayloads')";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });

        Assert.Contains(indexes, i => i.StartsWith("UX_QueueingEvents_Source_EventId|1|"));
        Assert.Contains(indexes, i => i.StartsWith("UX_Tickets_IntegrationSource_ExternalTicketId|1|"));
        Assert.Contains(indexes, i => i.StartsWith("UX_QueueingTicketLinks_TicketId|1|"));
        Assert.Contains(indexes, i => i.StartsWith("UX_QueueingTicketLinks_ExternalTicketId|1|"));
        Assert.Contains(indexes, i => i.StartsWith("IX_QueueingEvents_PurgeablePayloads|0|"));
    }

    // ------------------------------------------------------------------ duplicate creation

    [VisibilitySqlServerFact]
    public async Task DuplicateCreation_AcrossTwoNodes_NeverMakesASecondTicket()
    {
        var id = NewTicketId();
        var request = Created(id);
        var (first, firstBody) = await f.PostAsync(request, f.CreateClient());
        var (sameEventOtherNode, sameBody) = await f.PostAsync(request, f.CreateSecondNodeClient());
        var (newEventOtherNode, newBody) = await f.PostAsync(Created(id), f.CreateSecondNodeClient());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(QueueingResultStatus.Duplicate, sameBody!.Status);
        Assert.Equal(QueueingOutcomeCodes.TicketAlreadyExists, newBody!.Outcome);
        Assert.Equal(firstBody!.TicketId, newBody.TicketId);
        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Equal(1, await CountAsync(db => db.QueueingTicketLinks.Where(l => l.ExternalTicketId == id)));
    }

    // ------------------------------------------------------------------ concurrent requests

    [VisibilitySqlServerFact]
    public async Task ConcurrentDeliveriesOfTheSameEvent_AcrossTwoNodes_CreateExactlyOneTicketOneLinkAndOneInboxRow()
    {
        var id = NewTicketId();
        var request = Created(id);
        var nodes = Nodes;
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => f.PostAsync(request, nodes[i % 2])));

        Assert.All(results, r => Assert.True(
            r.Http.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Accepted,
            r.Http.StatusCode.ToString()));
        Assert.True(results.Count(r => r.Http.StatusCode == HttpStatusCode.Created) <= 1);

        // Whatever raced, the database holds exactly one of each - and a redelivery settles any loser.
        await f.MakeRetriesDueAsync();
        await f.RetryAsync();
        var (again, _) = await f.PostAsync(request, nodes[0]);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Equal(1, await CountAsync(db => db.QueueingTicketLinks.Where(l => l.ExternalTicketId == id)));
        Assert.Equal(1, await CountAsync(db => db.QueueingEvents.Where(e => e.EventId == request.EventId)));
        Assert.Equal(QueueingEventStatus.Processed, (await f.FindEventAsync(request.EventId!))!.Status);
    }

    [VisibilitySqlServerFact]
    public async Task ConcurrentCreationsOfOneQueueTicket_UnderDifferentEventIds_AcrossTwoNodes_CreateExactlyOneTicket()
    {
        var id = NewTicketId();
        var nodes = Nodes;
        var requests = Enumerable.Range(0, 24).Select(_ => Created(id)).ToList();
        var results = await Task.WhenAll(requests.Select((r, i) => f.PostAsync(r, nodes[i % 2])));

        Assert.True(results.Count(r => r.Http.StatusCode == HttpStatusCode.Created) <= 1);
        await f.MakeRetriesDueAsync();
        await f.RetryAsync();
        foreach (var r in requests)   // every event is eventually settled; none was lost
        {
            var (http, _) = await f.PostAsync(r, nodes[0]);
            Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        }

        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Equal(1, await CountAsync(db => db.QueueingTicketLinks.Where(l => l.ExternalTicketId == id)));
        Assert.Equal(requests.Count, await CountAsync(db => db.QueueingEvents.Where(e => e.ExternalTicketId == id && e.Status == QueueingEventStatus.Processed)));
    }

    [VisibilitySqlServerFact]
    public async Task ConcurrentStateEvents_AcrossTwoNodes_SettleOnTheNewestState_WithExactlyOneCurrentOwner()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id), f.CreateClient());
        var events = new[]
        {
            f.Assigned(id, 2, f.QueueUserA),
            f.Assigned(id, 3, f.QueueUserB, type: "TicketReassigned"),
            Simple("TicketReturned", id, 4),
            f.Assigned(id, 5, f.QueueUserA, type: "TicketReassigned"),
            Simple("TicketCommentAdded", id, 6, comment: "n"),
            f.Assigned(id, 7, f.QueueUserB, type: "TicketReassigned")
        };
        var nodes = Nodes;
        await Task.WhenAll(events.Select((e, i) => f.PostAsync(e, nodes[i % 2])));
        for (var round = 0; round < 3; round++)     // anything that lost a race is retried; the newest still wins
        {
            await f.MakeRetriesDueAsync();
            await f.RetryAsync();
        }

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeB, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(7, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
        Assert.Equal(1, await CountAsync(db => db.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent)));
    }

    // ------------------------------------------------------------------ the 1686 lifecycle, persisted

    [VisibilitySqlServerFact]
    public async Task TheFullLifecycle_PersistsTheQueuesTimes_TheClosureActor_AndTheInteractionEnd_OnRealSqlServer()
    {
        var id = NewTicketId();
        var t0 = DateTime.UtcNow.AddMinutes(-60);
        await f.PostAsync(Created(id, at: t0), f.CreateClient());
        await f.PostAsync(f.Assigned(id, 2, f.QueueUserA, t0.AddMinutes(10)), f.CreateSecondNodeClient());
        await f.PostAsync(new TigerCS.Application.Modules.QueueingIntegration.Dto.QueueingEventRequest(
            NewEventId(), "TicketCommentAdded", id, 3, Comment: "note", PerformedByUserId: f.QueueUserA, OccurredAtUtc: t0.AddMinutes(12)), f.CreateClient());
        await f.PostAsync(Simple("TicketReturned", id, 4, t0.AddMinutes(20)), f.CreateSecondNodeClient());
        await f.PostAsync(f.Assigned(id, 5, f.QueueUserB, t0.AddMinutes(30), "TicketReassigned"), f.CreateClient());
        var (http, _) = await f.PostAsync(new TigerCS.Application.Modules.QueueingIntegration.Dto.QueueingEventRequest(
            NewEventId(), "TicketClosed", id, 6, PerformedByUserId: f.QueueUserB, OccurredAtUtc: t0.AddMinutes(40)), f.CreateSecondNodeClient());
        Assert.Equal(HttpStatusCode.OK, http.StatusCode);

        var ticket = (await f.FindTicketAsync(id))!;
        var assignments = (await f.ReadAsync(db => db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticket.TicketId).ToListAsync())).OrderBy(a => a.AssignedAtUtc).ToList();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(t0.AddMinutes(20), assignments[0].SupersededAtUtc!.Value, TimeSpan.FromMilliseconds(5));
        Assert.Equal(t0.AddMinutes(30), assignments[1].AssignedAtUtc, TimeSpan.FromMilliseconds(5));
        Assert.Equal(t0.AddMinutes(40), assignments[1].SupersededAtUtc!.Value, TimeSpan.FromMilliseconds(5));
        Assert.DoesNotContain(assignments, a => a.IsCurrent);

        var interaction = await f.ReadAsync(db => db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticket.TicketId));
        Assert.Equal(t0.AddMinutes(40), interaction.EndedAtUtc!.Value, TimeSpan.FromMilliseconds(5));
        Assert.Equal("QueueTicketClosed", interaction.EndReason);

        var resolution = await f.ReadAsync(db => db.TicketResolutions.AsNoTracking().SingleAsync(r => r.TicketId == ticket.TicketId));
        Assert.Equal(f.EmployeeB, resolution.ResolvingEmployeeId);
        var closing = await f.ReadAsync(db => db.TicketStatusHistoryEntries.AsNoTracking()
            .Where(h => h.TicketId == ticket.TicketId && h.NewValue == (byte)TicketStatus.Closed).ToListAsync());
        Assert.All(closing, h => { Assert.Equal(f.EmployeeB, h.ActorEmployeeId); Assert.False(h.ActorIsSystem); });
        var note = await f.ReadAsync(db => db.TicketNotes.AsNoTracking().SingleAsync(n => n.TicketId == ticket.TicketId));
        Assert.Equal(f.EmployeeA, note.AuthorEmployeeId);
        Assert.Null(ticket.FirstHumanResponseAtUtc);

        // The Human Agent closure card and the employee's closed count, evaluated by SQL Server (the audit-value predicate included).
        using var scope = f.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Ticketing.Abstractions.ITicketRepository>();
        async Task<bool> CountedAsync(Guid employee) =>
            (await repo.SearchAsync(new TigerCS.Application.Modules.Ticketing.Abstractions.TicketQuery(
                null, null, null, null, null, null, null, null, TigerCS.Application.Modules.Ticketing.Abstractions.TicketSortBy.CreatedAtUtc, true, 1, 50,
                ClosureCategory: TigerCS.Application.Modules.Ticketing.Abstractions.TicketClosureCategory.ClosedByAgent, ClosedAssigneeEmployeeId: employee)))
            .Items.Any(t => t.TicketId == ticket.TicketId);
        Assert.True(await CountedAsync(f.EmployeeB));
        Assert.False(await CountedAsync(f.EmployeeA));

        // Team Performance: the resolution is credited to the holder at the closure instant, which is exactly when the assignment ended.
        var events = await scope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Reporting.Abstractions.ITeamPerformanceQueryRepository>()
            .GetSlaEventsAsync([f.EmployeeA, f.EmployeeB], t0.AddHours(-1), DateTime.UtcNow.AddHours(1));
        Assert.Contains(events.Met, e => e.TicketId == ticket.TicketId && e.EmployeeId == f.EmployeeB);
    }

    // ------------------------------------------------------------------ transaction rollback

    [VisibilitySqlServerFact]
    public async Task ACreationThatFailsAtTheLastStep_RollsBackTheTicketAndEverythingWrittenWithIt_AndRetriesCleanly()
    {
        var id = NewTicketId();
        var request = Created(id);
        var before = new
        {
            Tickets = await CountAsync(db => db.Tickets),
            Outbox = await CountAsync(db => db.OutboxMessages),
            Sla = await CountAsync(db => db.TicketSlaInstances),
            Links = await CountAsync(db => db.QueueingTicketLinks),
            Audit = await CountAsync(db => db.AuditEntries.Where(a => a.Action == "QueueingTicketCreated")),
            History = await CountAsync(db => db.TicketStatusHistoryEntries)
        };

        // The queue-state row is the LAST thing staged in the creation transaction: make its insert fail, as a crash or a constraint at commit time would.
        await f.ExecuteSqlAsync("CREATE TRIGGER trg_QueueingLinks_ForceFailure ON QueueingTicketLinks AFTER INSERT AS BEGIN THROW 50001, 'forced failure', 1; END");
        try
        {
            var (http, body) = await f.PostAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);                          // stored for retry, not applied
            Assert.Equal(QueueingOutcomeCodes.ProcessingFailed, body!.Outcome);
        }
        finally
        {
            await f.ExecuteSqlAsync("DROP TRIGGER trg_QueueingLinks_ForceFailure");
        }

        // Nothing at all survived: no ticket, no queue state, no history, no SLA period, no outbox row, no creation audit.
        Assert.Equal(0, await f.CountTicketsAsync(id));
        Assert.Equal(before.Tickets, await CountAsync(db => db.Tickets));
        Assert.Equal(before.Links, await CountAsync(db => db.QueueingTicketLinks));
        Assert.Equal(before.Sla, await CountAsync(db => db.TicketSlaInstances));
        Assert.Equal(before.Outbox, await CountAsync(db => db.OutboxMessages));
        Assert.Equal(before.History, await CountAsync(db => db.TicketStatusHistoryEntries));
        Assert.Equal(before.Audit, await CountAsync(db => db.AuditEntries.Where(a => a.Action == "QueueingTicketCreated")));
        Assert.Equal(QueueingEventStatus.Unresolved, (await f.FindEventAsync(request.EventId!))!.Status);   // the event itself is kept for retry

        // The very same event now applies cleanly - by its own retry, with no help from any later event.
        await f.MakeRetriesDueAsync();
        Assert.True(await f.RetryAsync() >= 1);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.NotNull(await f.FindLinkAsync(ticket.TicketId));                                       // the link is there because it was committed WITH the ticket
        Assert.Equal(1, await f.CountTicketsAsync(id));
        var settled = (await f.FindEventAsync(request.EventId!))!;
        Assert.True(settled.Status == QueueingEventStatus.Processed, $"{settled.Status} {settled.OutcomeCode} {settled.OutcomeDetail} attempts={settled.AttemptCount}");
    }

    [VisibilitySqlServerFact]
    public async Task AReturnThatFailsAtCommit_LeavesTheTicketOwnedInProgressAndItsHistoryUntouched()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        var ticket = (await f.FindTicketAsync(id))!;
        var historyBefore = await CountAsync(db => db.TicketStatusHistoryEntries.Where(h => h.TicketId == ticket.TicketId));
        var auditBefore = await CountAsync(db => db.AuditEntries.Where(a => a.Action == "ReturnToQueue" && a.EntityId == ticket.TicketId.ToString()));

        await f.ExecuteSqlAsync("CREATE TRIGGER trg_QueueingLinks_ForceFailure ON QueueingTicketLinks AFTER UPDATE AS BEGIN THROW 50001, 'forced failure', 1; END");
        var returned = Simple("TicketReturned", id, 3, reason: "stepped away");
        try
        {
            var (http, _) = await f.PostAsync(returned);
            Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        }
        finally
        {
            await f.ExecuteSqlAsync("DROP TRIGGER trg_QueueingLinks_ForceFailure");
        }

        var after = (await f.FindTicketAsync(id))!;
        Assert.Equal(f.EmployeeA, after.CurrentOwnerEmployeeId);                                       // still owned
        Assert.Equal(TicketStatus.InProgress, after.TicketStatus);                                     // not moved to Open
        Assert.Equal(1, await CountAsync(db => db.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent)));
        Assert.Equal(historyBefore, await CountAsync(db => db.TicketStatusHistoryEntries.Where(h => h.TicketId == ticket.TicketId)));
        Assert.Equal(auditBefore, await CountAsync(db => db.AuditEntries.Where(a => a.Action == "ReturnToQueue" && a.EntityId == ticket.TicketId.ToString())));
        Assert.Equal(2, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);                // the queue state did not advance either

        await f.MakeRetriesDueAsync();
        await f.RetryAsync();
        var applied = (await f.FindTicketAsync(id))!;
        Assert.Null(applied.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.Open, applied.TicketStatus);
        Assert.Equal(3, (await f.FindLinkAsync(ticket.TicketId))!.LastAppliedSequence);
    }

    [VisibilitySqlServerFact]
    public async Task AClosureThatFailsAtCommit_LeavesNothingHalfClosed()
    {
        var id = NewTicketId();
        await f.PostAsync(Created(id));
        await f.PostAsync(f.Assigned(id, 2));
        var ticket = (await f.FindTicketAsync(id))!;
        var outboxBefore = await CountAsync(db => db.OutboxMessages);

        await f.ExecuteSqlAsync("CREATE TRIGGER trg_QueueingLinks_ForceFailure ON QueueingTicketLinks AFTER UPDATE AS BEGIN THROW 50001, 'forced failure', 1; END");
        var close = Simple("TicketClosed", id, 3);
        try
        {
            var (http, _) = await f.PostAsync(close);
            Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        }
        finally
        {
            await f.ExecuteSqlAsync("DROP TRIGGER trg_QueueingLinks_ForceFailure");
        }

        var after = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketStatus.InProgress, after.TicketStatus);
        Assert.Equal(f.EmployeeA, after.CurrentOwnerEmployeeId);
        Assert.Equal(0, await CountAsync(db => db.TicketResolutions.Where(r => r.TicketId == ticket.TicketId)));
        Assert.Equal(1, await CountAsync(db => db.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent)));
        Assert.Equal(outboxBefore, await CountAsync(db => db.OutboxMessages));

        await f.MakeRetriesDueAsync();
        await f.RetryAsync();
        Assert.Equal(TicketStatus.Closed, (await f.FindTicketAsync(id))!.TicketStatus);
        Assert.Equal(1, await CountAsync(db => db.TicketResolutions.Where(r => r.TicketId == ticket.TicketId)));
    }

    // ------------------------------------------------------------------ retention on the real schema

    [VisibilitySqlServerFact]
    public async Task Retention_OnTheRealSchema_ErasesTheOldProcessedPayload_AndKeepsTheUnresolvedOne()
    {
        var id = NewTicketId();
        var old = Created(id);
        await f.PostAsync(old);
        var waiting = f.Assigned(NewTicketId(), 2, "queue-user-never-mapped-sql");
        await f.PostAsync(waiting);
        await f.ExecuteSqlAsync($"UPDATE QueueingEvents SET ProcessedAtUtc = DATEADD(day, -91, SYSUTCDATETIME()) WHERE EventId = N'{old.EventId}'");
        await f.ExecuteSqlAsync($"UPDATE QueueingEvents SET ReceivedAtUtc = DATEADD(day, -91, SYSUTCDATETIME()) WHERE EventId = N'{waiting.EventId}'");

        using (var scope = f.Factory.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<QueueingEventProcessor>().PurgeExpiredPayloadsAsync(100) >= 1);
        }

        Assert.Null((await f.FindEventAsync(old.EventId!))!.Payload);
        Assert.NotNull((await f.FindEventAsync(waiting.EventId!))!.Payload);
        var (dup, body) = await f.PostAsync(old);
        Assert.Equal(QueueingResultStatus.Duplicate, body!.Status);
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
    }
}
