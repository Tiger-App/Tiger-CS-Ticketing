using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Integrations;
using TigerCS.Application.Modules.QueueingIntegration;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.QueueingIntegration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>
/// A real Api host with the Queueing integration switched ON, plus the reference data an environment configures once: a department (with a request type
/// and its same-named category), company + ticket-type mappings, two employees in that department with their queue-user mappings, and the integration's
/// own service account. Each test uses its own queue ticket (a fresh ExternalTicketId), so tests share the host and never each other's data.
/// </summary>
public class QueueingFixture : IAsyncLifetime
{
    public const string ApiKey = "test-queueing-api-key-0123456789";
    public const string CompanyId = "company-a";
    public const string TicketTypeId = "type-complaint";

    public TigerCsApiFactory Factory { get; protected set; } = null!;
    public int DepartmentId { get; private set; }
    public int RequestTypeId { get; private set; }
    public Guid ServiceAccountId { get; private set; }
    public Guid EmployeeA { get; private set; }
    public Guid EmployeeB { get; private set; }
    public string QueueUserA { get; } = "queue-user-a";
    public string QueueUserB { get; } = "queue-user-b";

    public virtual Task InitializeAsync() => InitializeCoreAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The configuration every host of this fixture runs with: the integration switched on and two valid API keys (the second proves a key can be rotated).</summary>
    public static Dictionary<string, string?> HostConfiguration() => new()
    {
        ["QueueingIntegration:Enabled"] = "true",
        ["QueueingIntegration:Authentication:ApiKeys:0:KeyId"] = "queue-prod",
        ["QueueingIntegration:Authentication:ApiKeys:0:Sha256"] = QueueingApiKeyDefaults.Hash(ApiKey),
        ["QueueingIntegration:Authentication:ApiKeys:1:KeyId"] = "queue-next",
        ["QueueingIntegration:Authentication:ApiKeys:1:Sha256"] = QueueingApiKeyDefaults.Hash(ApiKey + "-next")
    };

    /// <summary>The host this fixture seeds and reads through. The default is the InMemory one; the SQL Server fixture overrides it.</summary>
    protected virtual TigerCsApiFactory CreateFactory() => new() { ExtraConfiguration = HostConfiguration() };

    protected async Task InitializeCoreAsync()
    {
        Factory = CreateFactory();

        await Factory.SeedPrioritiesAsync();
        DepartmentId = await Factory.CreateDepartmentAsync("Queue Customer Service " + Guid.NewGuid().ToString("N")[..6], "Q" + Guid.NewGuid().ToString("N")[..8]);
        await Factory.CreateCategoryAsync("Queue Complaint", DepartmentId);
        RequestTypeId = await Factory.CreateRequestTypeAsync("Queue Complaint", DepartmentId);

        ServiceAccountId = (await Factory.SeedEmployeeAsync(Roles.CsAgent)).EmployeeId;
        EmployeeA = await SeedDepartmentEmployeeAsync();
        EmployeeB = await SeedDepartmentEmployeeAsync();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var now = DateTime.UtcNow;
        db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(CompanyId, DepartmentId, now));
        db.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping(CompanyId, TicketTypeId, RequestTypeId, now));
        db.QueueingUserMappings.Add(new QueueingUserMapping(QueueUserA, EmployeeA, now));
        db.QueueingUserMappings.Add(new QueueingUserMapping(QueueUserB, EmployeeB, now));
        await db.SaveChangesAsync();

        Options.ServiceAccountEmployeeId = ServiceAccountId;
    }

    public QueueingIntegrationOptions Options => Factory.Services.GetRequiredService<QueueingIntegrationOptions>();

    public async Task<Guid> SeedDepartmentEmployeeAsync()
    {
        var employee = (await Factory.SeedEmployeeAsync(Roles.DepartmentEmployee)).EmployeeId;
        await Factory.AssignPrimaryDepartmentAsync(employee, DepartmentId);
        return employee;
    }

    public HttpClient CreateClient(string? apiKey = ApiKey)
    {
        var client = Factory.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(QueueingApiKeyDefaults.DefaultHeaderName, apiKey);
        }

        return client;
    }

    public static string NewTicketId() => "qt-" + Guid.NewGuid().ToString("N");

    public static QueueingEventRequest Created(string externalTicketId, long sequence = 1, DateTime? at = null, string? eventId = null,
        string? companyId = CompanyId, string? ticketTypeId = TicketTypeId) =>
        new(eventId ?? NewEventId(), "TicketCreated", externalTicketId, sequence, QueueNumber: "A-017", CompanyId: companyId, TicketTypeId: ticketTypeId,
            CustomerName: "Aisha Khan", CustomerPhone: "+971500000123", CustomerEmail: "aisha@example.test", OccurredAtUtc: at ?? DateTime.UtcNow.AddMinutes(-5));

    public QueueingEventRequest Assigned(string externalTicketId, long sequence, string? user = null, DateTime? at = null, string type = "TicketAssigned") =>
        new(NewEventId(), type, externalTicketId, sequence, AssignedUserId: user ?? QueueUserA, PerformedByUserId: user ?? QueueUserA,
            CounterId: "counter-3", OccurredAtUtc: at ?? DateTime.UtcNow.AddMinutes(-4));

    public static QueueingEventRequest Simple(string type, string externalTicketId, long sequence, DateTime? at = null, string? comment = null, string? reason = null) =>
        new(NewEventId(), type, externalTicketId, sequence, Comment: comment, Reason: reason, PerformedByUserId: "queue-user-a", CounterId: "counter-3",
            OccurredAtUtc: at ?? DateTime.UtcNow.AddMinutes(-3));

    public static string NewEventId() => "evt-" + Guid.NewGuid().ToString("N");

    public async Task<(HttpResponseMessage Http, QueueingEventResponse? Body)> PostAsync(QueueingEventRequest request, HttpClient? client = null)
    {
        var http = await (client ?? CreateClient()).PostAsJsonAsync("/api/integrations/queueing/events", request);
        QueueingEventResponse? body = null;
        if (http.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            body = await http.Content.ReadFromJsonAsync<QueueingEventResponse>();
        }

        return (http, body);
    }

    // ---- reads (a fresh scope each time, so nothing is served from a stale tracker)

    public async Task<T> ReadAsync<T>(Func<TigerCsDbContext, Task<T>> read)
    {
        using var scope = Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<TigerCsDbContext>());
    }

    public Task<Ticket?> FindTicketAsync(string externalTicketId) =>
        ReadAsync(db => db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.ExternalTicketId == externalTicketId));

    public Task<int> CountTicketsAsync(string externalTicketId) =>
        ReadAsync(db => db.Tickets.AsNoTracking().CountAsync(t => t.ExternalTicketId == externalTicketId));

    public Task<QueueingEvent?> FindEventAsync(string eventId) =>
        ReadAsync(db => db.QueueingEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId));

    public Task<QueueingTicketLink?> FindLinkAsync(long ticketId) =>
        ReadAsync(db => db.QueueingTicketLinks.AsNoTracking().FirstOrDefaultAsync(l => l.TicketId == ticketId));

    public async Task<int> RetryAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<QueueingEventProcessor>().RetryDueAsync(100);
    }

    /// <summary>Makes every Unresolved event due now, as the clock passing the backoff would.</summary>
    public async Task MakeRetriesDueAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        foreach (var row in await db.QueueingEvents.Where(e => e.Status == QueueingEventStatus.Unresolved).ToListAsync())
        {
            db.Entry(row).Property(nameof(QueueingEvent.NextRetryAtUtc)).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
        }

        await db.SaveChangesAsync();
    }
}
