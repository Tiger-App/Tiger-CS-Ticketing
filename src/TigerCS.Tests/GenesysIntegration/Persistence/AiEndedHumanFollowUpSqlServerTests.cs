using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.GenesysIntegration.Persistence;

/// <summary>
/// Production ticket 1878 (TG-REG-20261119-0027) replayed over real HTTP against a REAL SQL Server with the real migrations: the exact JSON a Genesys flow
/// sends, the real controllers, services, repositories and audit writer. Without <c>TIGERCS_TEST_SQL_SERVER</c> the tests are reported SKIPPED.
///
/// <list type="number">
/// <item>The conversation "started" 4 hours in the future (a wall-clock written without a zone): never stored, audited with its wire form.</item>
/// <item>The AI conversation ENDS: nothing is marked as human work, moved or assigned - ending is not evidence of a handoff.</item>
/// <item>The AI conversation is shown as ended (not "AI is handling"), no human accepted, no human responded.</item>
/// <item>An explicit human follow-up request routes to the Call Center and a provisional agent, and Assignment History shows that agent.</item>
/// <item>That assignment is not acceptance and not a response (no handler, no first human response).</item>
/// <item>A Closed ticket never changes again - owner, department, status, history - whatever late event arrives.</item>
/// </list>
/// </summary>
public sealed class AiEndedHumanFollowUpSqlServerTests : IDisposable
{
    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsAiEndedTest_{Guid.NewGuid():N}";
    private TigerCsApiFactory? _factory;

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    public void Dispose()
    {
        _factory?.Dispose();
        if (string.IsNullOrWhiteSpace(_master))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(_master);
        master.Open();
        using var command = master.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        command.ExecuteNonQuery();
    }

    private sealed record World(HttpClient Genesys, HttpClient Viewer, int CustomerService, int CallCenter, Guid[] CallCenterAgents);

    private async Task<World> StartAsync()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var command = master.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]";
            command.ExecuteNonQuery();
        }

        using (var migrate = new TigerCsDbContext(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options))
        {
            migrate.Database.Migrate();
        }

        _factory = new TigerCsApiFactory { SqlServerConnectionString = ConnectionString };
        await _factory.SeedPrioritiesAsync();
        var cs = await _factory.CreateDepartmentAsync("Customer Service", "CS");
        var cc = await _factory.CreateDepartmentAsync("Call Center", TeamPerformanceAppService.CallCenterDepartmentCode);

        var agents = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var (_, _, id) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
            await _factory.AssignPrimaryDepartmentAsync(id, cc);
            agents.Add(id);
        }

        return new World(await ClientAsync(Roles.CsAgent), await ClientAsync(Roles.SystemAdministrator), cs, cc, [.. agents]);
    }

    private async Task<HttpClient> ClientAsync(string role)
    {
        var (username, password, _) = await _factory!.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static StringContent Json(object body) => new(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string Z(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

    private TigerCsDbContext Db() => new(new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlServer(ConnectionString).Options);

    [VisibilitySqlServerFact]
    public async Task Ticket1878_AiConversationEnds_NothingIsAssigned_FollowUpIsExplicit_HistoryShowsIt_ClosedStaysClosed()
    {
        var w = await StartAsync();
        var conversation = $"conv-{Guid.NewGuid():N}"[..20];
        var receivedAround = DateTime.UtcNow;
        // The 1878 shape: a UAE wall-clock start written WITHOUT a zone, i.e. four hours later than the real instant when read as UTC.
        var zonelessFuture = receivedAround.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss");

        // 1. The AI creates the ticket.
        var create = await w.Genesys.PostAsync("/api/genesys/tickets", Json(new
        {
            conversationId = conversation, channel = "LiveChat", customerPhone = "+971500000011", departmentId = w.CustomerService,
            startedAtUtc = zonelessFuture
        }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ticketId = (await create.Content.ReadFromJsonAsync<GenesysAcceptedDto>())!.TicketId;

        using (var db = Db())
        {
            var interaction = await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId);
            Assert.Null(interaction.InteractionStartedAtUtc);                                          // the impossible start was NOT stored
            var anomaly = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == GenesysAuditActions.TimestampAnomaly && a.EntityId == ticketId.ToString());
            Assert.Contains("RejectedFuture", anomaly.AfterValue);
            Assert.Contains("Zoneless", anomaly.AfterValue);
            var history = await db.TicketAssignmentHistory.AsNoTracking().Where(h => h.TicketId == ticketId).ToListAsync();
            Assert.Equal(TicketAssignmentSource.GenesysAiAgent, Assert.Single(history).Source);        // "Genesys Integration" only, so far
        }

        // 2. The AI conversation ends (no human line, no follow-up flag). Exactly the 14:13:31 event of the real ticket.
        var end = await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(new
        {
            conversationId = conversation,
            ended = new
            {
                endedAtUtc = Z(DateTime.UtcNow), endReason = "CustomerDisconnect",
                transcript = new object[]
                {
                    new { sender = "Customer", sentAtUtc = Z(DateTime.UtcNow.AddMinutes(-2)), body = "Hello" },
                    new { sender = "VirtualAgent", sentAtUtc = Z(DateTime.UtcNow.AddMinutes(-1)), body = "How can I help?" }
                }
            }
        }));
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);

        using (var db = Db())
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
            Assert.Equal(w.CustomerService, ticket.CurrentDepartmentId);                               // NOT transferred to the Call Center
            Assert.Null(ticket.CurrentOwnerEmployeeId);                                                // NOT given to an employee
            Assert.Empty(await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToListAsync());
            Assert.False((await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId)).RequiresHumanFollowUp);
            Assert.DoesNotContain(await db.AuditEntries.AsNoTracking().Select(a => a.Action).ToListAsync(), a => a is "Transfer" or "AutoAssign" or GenesysAuditActions.HumanFollowUpRequired);
            Assert.Single(await db.TicketAssignmentHistory.AsNoTracking().Where(h => h.TicketId == ticketId).ToListAsync());
        }

        var afterEnd = await InteractionAsync(w, ticketId);
        Assert.Equal("AiEnded", afterEnd.HandlingState);                                               // not "AI is handling this customer"
        Assert.False(afterEnd.HumanAccepted);
        Assert.False(afterEnd.HumanResponded);

        // 3. Redelivery of the same end changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(new
        {
            conversationId = conversation, ended = new { endedAtUtc = Z(DateTime.UtcNow), endReason = "CustomerDisconnect" }
        }))).StatusCode);
        using (var db = Db())
        {
            Assert.Null((await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId)).CurrentOwnerEmployeeId);
        }

        // 4. An EXPLICIT follow-up request (a business decision, not the end of the conversation): Call Center first, provisional agent by Round Robin.
        Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(
            new { conversationId = conversation, humanFollowUpRequired = true }))).StatusCode);

        Guid owner;
        using (var db = Db())
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
            Assert.Equal(w.CallCenter, ticket.CurrentDepartmentId);
            owner = Assert.NotNull(ticket.CurrentOwnerEmployeeId);
            Assert.Contains(owner, w.CallCenterAgents);
            Assert.True((await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId)).RequiresHumanFollowUp);

            // Assignment History now carries the Call Center assignment (it used to stop at "Genesys Integration").
            var history = await db.TicketAssignmentHistory.AsNoTracking().Where(h => h.TicketId == ticketId).OrderBy(h => h.ChangedAtUtc).ThenBy(h => h.TicketAssignmentHistoryEntryId).ToListAsync();
            Assert.Equal(2, history.Count);
            Assert.Equal(TicketAssignmentSource.GenesysHumanFollowUp, history[1].Source);
            Assert.Equal(history[0].NewEmployeeId, history[1].PreviousEmployeeId);
            Assert.Equal(owner, history[1].NewEmployeeId);
            Assert.Equal(w.CallCenter, history[1].DepartmentId);

            // Assignment is NOT acceptance and NOT a response.
            Assert.Null((await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId)).HandledByUserId);
            Assert.False(await db.TicketInteractionHandlers.AsNoTracking().AnyAsync(h => h.TicketInteractionId == (db.TicketInteractions.Where(i => i.TicketId == ticketId).Select(i => i.TicketInteractionId).First())));
            Assert.Null(ticket.FirstHumanResponseAtUtc);
        }

        var followUp = await InteractionAsync(w, ticketId);
        Assert.Equal("HumanFollowUp", followUp.HandlingState);
        Assert.False(followUp.HumanAccepted);
        Assert.False(followUp.HumanResponded);

        var apiHistory = await w.Viewer.GetFromJsonAsync<List<TicketAssignmentHistoryItemDto>>($"/api/tickets/{ticketId}/assignment-history");
        Assert.Equal(2, apiHistory!.Count);
        Assert.Equal(owner, apiHistory[1].NewEmployeeId);

        // The read-only diagnostic for ticket 1878 runs against this very schema and tells the same story (Assignment History: AI Agent -> provisional agent).
        var sets = await RunDiagnosticAsync(ticketId);
        Assert.Contains(sets, set => set.Columns.Contains("Source") && set.Columns.Contains("NewEmployee") && set.Rows == 2);      // section 4: Assignment History
        Assert.Contains(sets, set => set.Columns.Contains("StartMinusServerRecorded_Min") && set.Rows == 1);                      // section 2: the interaction

        // Redelivery of the follow-up request writes no further history, assignment or transfer.
        await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(new { conversationId = conversation, humanFollowUpRequired = true }));
        using (var db = Db())
        {
            Assert.Equal(2, await db.TicketAssignmentHistory.AsNoTracking().CountAsync(h => h.TicketId == ticketId));
            Assert.Single(await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId && a.IsCurrent).ToListAsync());
        }

        // 5. Closed is terminal: whoever closed it, late events change nothing.
        Snapshot before;
        using (var db = Db())
        {
            var ticket = await db.Tickets.SingleAsync(t => t.TicketId == ticketId);
            ticket.ChangeStatus(TicketStatus.InProgress);
            ticket.Resolve(ResolutionOutcome.Resolved, null);
            ticket.Close();
            await db.SaveChangesAsync();
            before = await SnapshotAsync(db, ticketId);
        }

        foreach (var late in new object[]
        {
            new { conversationId = conversation, ended = new { endedAtUtc = Z(DateTime.UtcNow), endReason = "Timeout" } },
            new { conversationId = conversation, humanFollowUpRequired = true },
            new { conversationId = conversation, routing = new { agentId = "someone-else", connectedAtUtc = Z(DateTime.UtcNow) } }
        })
        {
            await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(late));
        }

        using (var db = Db())
        {
            Assert.Equal(before, await SnapshotAsync(db, ticketId));
        }

        // A genuinely NEW conversation creates a NEW ticket and leaves the closed one alone.
        var second = await w.Genesys.PostAsync("/api/genesys/tickets", Json(new
        {
            conversationId = conversation + "-new", channel = "LiveChat", customerPhone = "+971500000011", departmentId = w.CustomerService
        }));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(ticketId, (await second.Content.ReadFromJsonAsync<GenesysAcceptedDto>())!.TicketId);
        using (var db = Db())
        {
            Assert.Equal(before, await SnapshotAsync(db, ticketId));
        }
    }

    private sealed record GenesysAcceptedDto(long TicketId);

    private const string DiagnosticFile = "Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql";

    private static string DiagnosticScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, DiagnosticFile);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException(DiagnosticFile);
    }

    [Fact]
    public void TheTicket1878Diagnostic_IsReadOnly_ByConstruction()
    {
        var code = System.Text.RegularExpressions.Regex.Replace(DiagnosticScript(), @"/\*.*?\*/|--[^\r\n]*", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.DoesNotMatch(@"(?im)^\s*(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|GRANT|REVOKE)\b", code);
        Assert.DoesNotContain("WITH (UPDLOCK", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@TicketId bigint = 1878", code);
    }

    private async Task<List<(HashSet<string> Columns, int Rows)>> RunDiagnosticAsync(long ticketId)
    {
        var sets = new List<(HashSet<string>, int)>();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = DiagnosticScript().Replace("@TicketId bigint = 1878", $"@TicketId bigint = {ticketId}");
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToHashSet();
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
            }

            sets.Add((columns, rows));
        }
        while (await reader.NextResultAsync());
        return sets;
    }

    private sealed record Snapshot(TicketStatus Status, Guid? Owner, int Department, int Assignments, int History, int ReopenCount);

    private static async Task<Snapshot> SnapshotAsync(TigerCsDbContext db, long ticketId)
    {
        var t = await db.Tickets.AsNoTracking().SingleAsync(x => x.TicketId == ticketId);
        return new Snapshot(
            t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId,
            await db.TicketAssignments.AsNoTracking().CountAsync(a => a.TicketId == ticketId),
            await db.TicketAssignmentHistory.AsNoTracking().CountAsync(h => h.TicketId == ticketId), t.ReopenCount);
    }

    private static async Task<TicketInteractionDto> InteractionAsync(World w, long ticketId)
    {
        var history = await w.Viewer.GetFromJsonAsync<TicketInteractionHistoryDto>($"/api/tickets/{ticketId}/interactions");
        return Assert.Single(history!.Interactions);
    }
}
