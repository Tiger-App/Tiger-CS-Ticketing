using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Integrations;
using TigerCS.Application.Modules.QueueingIntegration;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.Dashboard;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>The integration's switches, its clock, and the database-level guarantees the application logic leans on (proved on real relational SQL, not InMemory).</summary>
public sealed class QueueingPolicyAndPersistenceTests : IClassFixture<QueueingFixture>
{
    private readonly QueueingFixture _f;

    public QueueingPolicyAndPersistenceTests(QueueingFixture f) => _f = f;

    [Fact]
    public async Task WhenSwitchedOff_TheEndpointAnswers503_AndStoresNothing()
    {
        await using var host = new TigerCsApiFactory
        {
            ExtraConfiguration = new Dictionary<string, string?>
            {
                ["QueueingIntegration:Enabled"] = "false",
                ["QueueingIntegration:Authentication:ApiKeys:0:KeyId"] = "k",
                ["QueueingIntegration:Authentication:ApiKeys:0:Sha256"] = QueueingApiKeyDefaults.Hash(ApiKey)
            }
        };
        await host.SeedEmployeeAsync(TigerCS.Domain.Modules.IdentityAndAccess.Roles.CsAgent);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(QueueingApiKeyDefaults.DefaultHeaderName, ApiKey);

        var response = await client.PostAsJsonAsync("/api/integrations/queueing/events", Created(NewTicketId()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var scope = host.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().QueueingEvents.CountAsync());
    }

    [Fact]
    public async Task WithNoServiceAccountConfigured_CreationIsHeldNotGuessed()
    {
        var previous = _f.Options.ServiceAccountEmployeeId;
        _f.Options.ServiceAccountEmployeeId = null;
        try
        {
            var id = NewTicketId();
            var (http, body) = await _f.PostAsync(Created(id));
            Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
            Assert.Equal(QueueingOutcomeCodes.ServiceAccountNotConfigured, body!.Outcome);
            Assert.Equal(0, await _f.CountTicketsAsync(id));
        }
        finally
        {
            _f.Options.ServiceAccountEmployeeId = previous;
        }
    }

    [Fact]
    public async Task WithTheDefaultClassificationPolicy_AnUnmappedTicketTypeIsClassifiedByTheExistingGeneralRule_AndSaysSo()
    {
        var department = await _f.Factory.CreateDepartmentAsync("General Dept " + Guid.NewGuid().ToString("N")[..6], "G" + Guid.NewGuid().ToString("N")[..8]);
        await _f.Factory.CreateCategoryAsync("General", department);
        var general = await _f.Factory.CreateRequestTypeAsync("General", department);
        await _f.ReadAsync(async db =>
        {
            db.QueueingCompanyMappings.Add(new QueueingCompanyMapping("company-general", department, DateTime.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });

        _f.Options.MissingRequestTypePolicy = QueueingMissingRequestTypePolicy.DefaultClassification;
        try
        {
            var id = NewTicketId();
            var (http, body) = await _f.PostAsync(Created(id, companyId: "company-general", ticketTypeId: "type-never-mapped"));
            Assert.Equal(HttpStatusCode.Created, http.StatusCode);
            var ticket = (await _f.FindTicketAsync(id))!;
            Assert.Equal(general, ticket.RequestTypeId);
            Assert.Equal(department, ticket.CurrentDepartmentId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            var audit = await _f.Factory.GetAuditEntriesAsync(ticket.TicketId.ToString());
            Assert.Contains(audit, a => a.Action == "QueueingTicketCreated" && a.AfterValue!.Contains("RequestTypeMapping=Missing") && a.AfterValue.Contains("DefaultClassification"));
            Assert.Equal(body!.TicketId, ticket.TicketId);
        }
        finally
        {
            _f.Options.MissingRequestTypePolicy = QueueingMissingRequestTypePolicy.Hold;
        }
    }

    [Fact]
    public async Task WithTheEmployeeIdLookupMode_TheQueueUserIdIsTheTigerCsEmployeeId()
    {
        _f.Options.UserLookupMode = QueueingUserLookupMode.EmployeeId;
        try
        {
            var id = NewTicketId();
            await _f.PostAsync(Created(id));
            var (_, body) = await _f.PostAsync(_f.Assigned(id, 2, _f.EmployeeB.ToString()));
            Assert.Equal(QueueingResultStatus.Processed, body!.Status);
            Assert.Equal(_f.EmployeeB, (await _f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);

            var (_, notAGuid) = await _f.PostAsync(_f.Assigned(id, 3, "queue-user-a", type: "TicketReassigned"));
            Assert.Equal(QueueingOutcomeCodes.UserNotMapped, notAGuid!.Outcome);
            Assert.Equal(_f.EmployeeB, (await _f.FindTicketAsync(id))!.CurrentOwnerEmployeeId);
        }
        finally
        {
            _f.Options.UserLookupMode = QueueingUserLookupMode.Mapping;
        }
    }

    // ------------------------------------------------------------------ the original time

    [Fact]
    public void TheClock_IsTheWrappedClockOutsideAScope_AndTheEventTimeInsideIt()
    {
        var real = new DashboardSqliteFixture.FixedTimeProvider(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
        var clock = new IntegrationAwareTimeProvider(real);
        var eventTime = new DateTime(2026, 10, 31, 8, 30, 0, DateTimeKind.Utc);

        Assert.Equal(real.GetUtcNow(), clock.GetUtcNow());
        using (IntegrationEventScope.Begin(eventTime, real.GetUtcNow().UtcDateTime))
        {
            Assert.Equal(eventTime, clock.GetUtcNow().UtcDateTime);
            Assert.True(IntegrationEventScope.IsActive);
        }

        Assert.False(IntegrationEventScope.IsActive);
        Assert.Equal(real.GetUtcNow(), clock.GetUtcNow());
    }

    [Fact]
    public async Task TheScope_IsPerRequest_NeverSeenByAConcurrentFlow()
    {
        var real = new DashboardSqliteFixture.FixedTimeProvider(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
        var clock = new IntegrationAwareTimeProvider(real);
        var gate = new TaskCompletionSource();
        var inside = Task.Run(async () =>
        {
            using var scope = IntegrationEventScope.Begin(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), real.GetUtcNow().UtcDateTime);
            await gate.Task;
            return clock.GetUtcNow().Year;
        });
        var outside = await Task.Run(() => clock.GetUtcNow().Year);
        gate.SetResult();
        Assert.Equal(2026, outside);
        Assert.Equal(2026, await inside);
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task AFutureDatedEvent_IsRefused_AndALateOneIsStampedWithItsOwnTime()
    {
        var (future, body) = await _f.PostAsync(Created(NewTicketId(), at: DateTime.UtcNow.AddHours(2)));
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Contains("OccurredAtUtc", body!.Errors!.Keys);

        var id = NewTicketId();
        var longAgo = DateTime.UtcNow.AddDays(-3);
        await _f.PostAsync(Created(id, at: longAgo));
        Assert.Equal(longAgo, (await _f.FindTicketAsync(id))!.CreatedAtUtc, TimeSpan.FromMilliseconds(5));
    }

    // ------------------------------------------------------------------ database-level idempotency

    [Fact]
    public void TheDatabase_RefusesASecondRowForTheSameEventId()
    {
        using var db = new DashboardSqliteFixture();
        using var context = db.CreateContext();
        var now = DateTime.UtcNow;
        context.QueueingEvents.Add(new QueueingEvent("evt-1", QueueingEventType.TicketCreated, "qt-1", 1, now, "{}", now));
        context.SaveChanges();

        using var second = db.CreateContext();
        second.QueueingEvents.Add(new QueueingEvent("evt-1", QueueingEventType.TicketClosed, "qt-9", 2, now, "{}", now));
        var ex = Assert.Throws<DbUpdateException>(() => second.SaveChanges());
        Assert.Contains("UNIQUE", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDatabase_RefusesASecondTicketForTheSameQueueTicket_ButNotForTicketsWithNoExternalId()
    {
        using var db = new DashboardSqliteFixture();
        using var context = db.CreateContext();
        Ticket Make(string number, string? external)
        {
            var ticket = Ticket.CreateUnverified(number, db.CustomerServiceId, db.CsCategoryId, (byte)TigerCS.Domain.Modules.SlaAndEscalation.PriorityLevel.Medium, "x", DateTime.UtcNow);
            if (external is not null)
            {
                ticket.RecordExternalOrigin(TicketIntegrationSources.QueueingSystem, external, "A-1");
            }

            return ticket;
        }

        // Ordinary tickets (no external id) never collide with each other - the index is filtered.
        context.Tickets.AddRange(Make("TG-QT-1", null), Make("TG-QT-2", null), Make("TG-QT-3", "qt-100"));
        context.SaveChanges();

        using var second = db.CreateContext();
        second.Tickets.Add(Make("TG-QT-4", "qt-100"));
        var ex = Assert.Throws<DbUpdateException>(() => second.SaveChanges());
        Assert.Contains("UNIQUE", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheExternalOrigin_IsWriteOnce()
    {
        var ticket = Ticket.CreateUnverified("TG-QT-5", 1, 1, 3, "x", DateTime.UtcNow);
        ticket.RecordExternalOrigin(TicketIntegrationSources.QueueingSystem, "qt-1", "A-1");
        Assert.Throws<InvalidOperationException>(() => ticket.RecordExternalOrigin(TicketIntegrationSources.QueueingSystem, "qt-2", "A-2"));
        Assert.Equal("qt-1", ticket.ExternalTicketId);
    }

    [Fact]
    public void TheDatabase_KeepsOneLinkPerTicket_AndOneMappingPerKey()
    {
        using var db = new DashboardSqliteFixture();
        using var context = db.CreateContext();
        var now = DateTime.UtcNow;
        var ticket = Ticket.CreateUnverified("TG-QT-6", db.CustomerServiceId, db.CsCategoryId, 3, "x", now);
        ticket.RecordExternalOrigin(TicketIntegrationSources.QueueingSystem, "qt-link", null);
        context.Tickets.Add(ticket);
        context.SaveChanges();
        context.QueueingTicketLinks.Add(new QueueingTicketLink(ticket.TicketId, "qt-link", null, null, null, 1, now, now));
        context.QueueingCompanyMappings.Add(new QueueingCompanyMapping("c-1", db.CustomerServiceId, now));
        context.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping("c-1", "t-1", db.CsRequestTypeId, now));
        context.QueueingUserMappings.Add(new QueueingUserMapping("u-1", Guid.NewGuid(), now));
        context.SaveChanges();

        void AssertRefused(Action<TigerCsDbContext> add)
        {
            using var other = db.CreateContext();
            add(other);
            Assert.Throws<DbUpdateException>(() => other.SaveChanges());
        }

        AssertRefused(c => c.QueueingTicketLinks.Add(new QueueingTicketLink(ticket.TicketId, "qt-other", null, null, null, 1, now, now)));
        AssertRefused(c => c.QueueingCompanyMappings.Add(new QueueingCompanyMapping("c-1", db.CustomerServiceId, now)));
        AssertRefused(c => c.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping("c-1", "t-1", db.CsRequestTypeId, now)));
        AssertRefused(c => c.QueueingUserMappings.Add(new QueueingUserMapping("u-1", Guid.NewGuid(), now)));
    }
}
