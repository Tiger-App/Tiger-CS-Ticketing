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
/// <item>The AI creates the ticket and OWNS it (no human assigned).</item>
/// <item>The AI conversation ENDS unresolved: flagged for follow-up and queued in the Call Center - nobody assigned, no Round Robin, AI Agent stays owner.</item>
/// <item>A handoff request shows Waiting for Human; an unmapped connected agent assigns nobody.</item>
/// <item>The employee who actually answers (mapped) is assigned the existing ticket directly; History records AI -> employee, department changes and the previous department.</item>
/// <item>A direct human call is owned by the answering employee, never the AI Agent; explicit workflow routing keeps its department.</item>
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

    private sealed record World(HttpClient Genesys, HttpClient Viewer, int CustomerService, int CallCenter, Guid[] CallCenterAgents, string[] GenesysIds);

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

        var genesysIds = new[] { $"genesys-{Guid.NewGuid():N}"[..20], $"genesys-{Guid.NewGuid():N}"[..20] };
        for (var i = 0; i < agents.Count; i++)
        {
            await _factory.MapGenesysAgentAsync(agents[i], genesysIds[i]);   // the existing Genesys-to-TigerCS user mapping
        }

        return new World(await ClientAsync(Roles.CsAgent), await ClientAsync(Roles.SystemAdministrator), cs, cc, [.. agents], genesysIds);
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
    public async Task Ticket1878_FullGenesysLifecycle_AiOwnsFirst_QueueNotAssignment_OnlyTheAnsweringAgentOwnsIt_HistoryAndClosedProtection()
    {
        var w = await StartAsync();
        var conversation = $"conv-{Guid.NewGuid():N}"[..20];
        // The 1878 shape: a UAE wall-clock start written WITHOUT a zone, i.e. four hours later than the real instant when read as UTC.
        var zonelessFuture = DateTime.UtcNow.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss");

        // 1. The AI creates the ticket: owned by the AI Agent, in its department, nobody assigned.
        var create = await w.Genesys.PostAsync("/api/genesys/tickets", Json(new
        {
            conversationId = conversation, channel = "Phone", customerPhone = "+971500000011", departmentId = w.CustomerService,
            startedAtUtc = zonelessFuture
        }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ticketId = (await create.Content.ReadFromJsonAsync<GenesysAcceptedDto>())!.TicketId;

        Guid aiAgent;
        using (var db = Db())
        {
            var interaction = await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId);
            Assert.Null(interaction.InteractionStartedAtUtc);                                          // the impossible start was NOT stored
            var anomaly = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == GenesysAuditActions.TimestampAnomaly && a.EntityId == ticketId.ToString());
            Assert.Contains("RejectedFuture", anomaly.AfterValue);
            Assert.Contains("inquiry.startedAtUtc", anomaly.AfterValue);                               // the boundary normalized the text first, so the audit names the field and the refused UTC value

            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
            aiAgent = Assert.NotNull(ticket.AiAgentOwnerEmployeeId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            Assert.Equal(w.CustomerService, ticket.CurrentDepartmentId);
            Assert.Equal(TicketAssignmentSource.GenesysAiAgent, Assert.Single(await db.TicketAssignmentHistory.AsNoTracking().Where(h => h.TicketId == ticketId).ToListAsync()).Source);
        }

        Assert.Equal("Ai", (await InteractionAsync(w, ticketId)).HandlingState);

        // 2. The call ENDS unresolved: ending alone changes nothing - not queued, not flagged, nobody assigned, nothing round-robins, and the AI Agent stays the owner.
        //    (Ticket 1878: this exact event dealt the ticket to an agent one second later.) Whether anyone answered is decided later, by VoiceCallReconciliation.
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
            Assert.Equal(w.CustomerService, ticket.CurrentDepartmentId);                               // not moved by the end of the call
            Assert.Equal(w.CustomerService, ticket.OriginatingDepartmentId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);                                                // NOT given to an employee
            Assert.Equal(aiAgent, ticket.AiAgentOwnerEmployeeId);                                      // the AI Agent keeps it
            Assert.Empty(await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToListAsync());
            Assert.False((await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId)).RequiresHumanFollowUp);   // no permanent flag from the end alone
            Assert.DoesNotContain(await db.AuditEntries.AsNoTracking().Select(a => a.Action).ToListAsync(), a => a == "AutoAssign");
            Assert.Null(ticket.FirstHumanResponseAtUtc);                                               // queue routing is not a human response

            var history = await db.TicketAssignmentHistory.AsNoTracking().Where(h => h.TicketId == ticketId).OrderBy(h => h.TicketAssignmentHistoryEntryId).ToListAsync();
            Assert.Single(history);                                                                    // only the AI-owner row: the end wrote no routing / ownership entry
            Assert.Equal(TicketAssignmentSource.GenesysAiAgent, history[0].Source);
        }

        var afterEnd = await InteractionAsync(w, ticketId);
        Assert.Equal("AiEnded", afterEnd.HandlingState);                                               // the call is over; not "AI is handling this customer"
        Assert.False(afterEnd.HumanAccepted);
        Assert.False(afterEnd.HumanResponded);

        // 3. Redelivery changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(new
        {
            conversationId = conversation, ended = new { endedAtUtc = Z(DateTime.UtcNow), endReason = "CustomerDisconnect" }
        }))).StatusCode);
        using (var db = Db())
        {
            Assert.Equal(1, await db.TicketAssignmentHistory.AsNoTracking().CountAsync(h => h.TicketId == ticketId));
            Assert.Null((await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId)).CurrentOwnerEmployeeId);
        }

        // 4. The AI asks for a human (handoff): Waiting for Human, still the AI Agent's, still nobody assigned, no Round Robin.
        Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(new
        {
            conversationId = conversation,
            handoff = new { required = true, mode = "HumanTakeover", trigger = "AiEscalated", reason = "The AI could not resolve the request." }
        }))).StatusCode);
        using (var db = Db())
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            Assert.Equal(aiAgent, ticket.AiAgentOwnerEmployeeId);
            Assert.Empty(await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToListAsync());
        }

        var waiting = await InteractionAsync(w, ticketId);
        Assert.Equal("WaitingForHuman", waiting.HandlingState);
        Assert.False(waiting.HumanAccepted);

        // 5. A connected agent the integration cannot map is NOT assigned, and nobody else is picked in their place.
        await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(new
        {
            conversationId = conversation, routing = new { agentId = "unmapped-genesys-user", agentName = "Ghost", connectedAtUtc = Z(DateTime.UtcNow) }
        }));
        using (var db = Db())
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            Assert.Equal(aiAgent, ticket.AiAgentOwnerEmployeeId);
            Assert.Empty(await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId).ToListAsync());
        }

        // 6. The employee who ACTUALLY answers (mapped Genesys user #2 - not the one a rotation would pick first) is assigned the EXISTING ticket directly.
        var answering = w.CallCenterAgents[1];
        Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{ticketId}", Json(new
        {
            conversationId = conversation, routing = new { agentId = w.GenesysIds[1], agentName = "Agent Two", connectedAtUtc = Z(DateTime.UtcNow) }
        }))).StatusCode);

        using (var db = Db())
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
            Assert.Equal(answering, ticket.CurrentOwnerEmployeeId);
            Assert.Null(ticket.AiAgentOwnerEmployeeId);                                                // a person now owns it
            Assert.Equal(1, await db.TicketInteractions.AsNoTracking().CountAsync(i => i.GenesysConversationId == conversation));   // no duplicate ticket
            Assert.Equal(1, await db.Tickets.AsNoTracking().CountAsync(t => t.TicketId == ticketId));
            Assert.Single(await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == ticketId && a.IsCurrent).ToListAsync());
            Assert.NotNull(ticket.FirstHumanResponseAtUtc);                                            // voice: the connect time IS the first human response
            Assert.Equal(answering, (await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == ticketId)).HandledByUserId);

            var history = await db.TicketAssignmentHistory.AsNoTracking().Where(h => h.TicketId == ticketId).OrderBy(h => h.TicketAssignmentHistoryEntryId).ToListAsync();
            var transfer = history[^1];
            Assert.Equal(TicketAssignmentSource.GenesysCallTransfer, transfer.Source);                 // AI -> human
            Assert.Equal((aiAgent, answering), (transfer.PreviousEmployeeId!.Value, transfer.NewEmployeeId));
        }

        var accepted = await InteractionAsync(w, ticketId);
        Assert.Equal("Human", accepted.HandlingState);
        Assert.True(accepted.HumanAccepted);
        Assert.False(accepted.HumanResponded);

        // The read-only diagnostic for ticket 1878 runs against this very schema and tells the same story.
        var sets = await RunDiagnosticAsync(ticketId);
        Assert.Contains(sets, set => set.Columns.Contains("Source") && set.Columns.Contains("NewEmployee") && set.Rows >= 3);   // section 4: Assignment History
        Assert.Contains(sets, set => set.Columns.Contains("StartMinusServerRecorded_Min") && set.Rows == 1);                      // section 2: the interaction

        var apiHistory = await w.Viewer.GetFromJsonAsync<List<TicketAssignmentHistoryItemDto>>($"/api/tickets/{ticketId}/assignment-history");
        Assert.Equal(answering, apiHistory![^1].NewEmployeeId);

        // 7. Closed is terminal: whoever closed it, late events change nothing.
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
            new { conversationId = conversation, routing = new { agentId = w.GenesysIds[0], connectedAtUtc = Z(DateTime.UtcNow) } }
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

    [VisibilitySqlServerFact]
    public async Task ADirectHumanCall_IsAssignedToTheAnsweringEmployee_NeverToTheAiAgent_AndATicketWithExplicitWorkflowRoutingKeepsItsDepartment()
    {
        var w = await StartAsync();

        // Direct human call: the answering mapped agent owns the ticket from creation; the AI Agent is never its owner.
        var direct = await w.Genesys.PostAsync("/api/genesys/tickets", Json(new
        {
            conversationId = $"direct-{Guid.NewGuid():N}"[..20], channel = "Phone", customerPhone = "+971500000012", departmentId = w.CustomerService,
            agentId = w.GenesysIds[0], agentName = "Agent One", direction = "Inbound"
        }));
        Assert.Equal(HttpStatusCode.Created, direct.StatusCode);
        var directId = (await direct.Content.ReadFromJsonAsync<GenesysAcceptedDto>())!.TicketId;
        using (var db = Db())
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == directId);
            Assert.Equal(w.CallCenterAgents[0], ticket.CurrentOwnerEmployeeId);
            Assert.Null(ticket.AiAgentOwnerEmployeeId);
            Assert.DoesNotContain(await db.TicketAssignmentHistory.AsNoTracking().Where(h => h.TicketId == directId).ToListAsync(), h => h.Source == TicketAssignmentSource.GenesysAiAgent);
        }

        // Explicit workflow routing (the department differs from the originating one): a human follow-up is FLAGGED where the ticket already is.
        var other = await _factory!.CreateDepartmentAsync("Legal", "LEG");
        var conversation = $"legal-{Guid.NewGuid():N}"[..20];
        var created = await w.Genesys.PostAsync("/api/genesys/tickets", Json(new
        {
            conversationId = conversation, channel = "Phone", customerPhone = "+971500000013", departmentId = w.CustomerService
        }));
        var legalTicket = (await created.Content.ReadFromJsonAsync<GenesysAcceptedDto>())!.TicketId;
        using (var db = Db())
        {
            var ticket = await db.Tickets.SingleAsync(t => t.TicketId == legalTicket);
            ticket.TransferToDepartment(other, keepAiAgentOwner: true);          // what an explicit request-type / person routing does
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{legalTicket}", Json(new
        {
            conversationId = conversation, humanFollowUpRequired = true
        }))).StatusCode);
        using (var db = Db())
        {
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == legalTicket);
            Assert.Equal(other, ticket.CurrentDepartmentId);                      // preserved: not pulled into the Call Center
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            Assert.NotNull(ticket.AiAgentOwnerEmployeeId);
            Assert.True((await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == legalTicket)).RequiresHumanFollowUp);
        }
    }

    /// <summary>
    /// Ticket 1878's defect, on a real SQL Server, over the real endpoints: Genesys sends UTC, in whichever spelling, and the database must hold that UTC instant - for the
    /// conversation start, every transcript line and the end. Before the fix an explicit-offset spelling (<c>+00:00</c>) was read into the HOST's zone and stored four hours late
    /// on the UTC+4 application server; this test fails on such a host without the fix (CI runs it with <c>TZ=Asia/Dubai</c> as well as on its UTC runner).
    /// </summary>
    [VisibilitySqlServerFact]
    public async Task GenesysUtc_InEveryWireSpelling_IsStoredAsTheSameUtcInstant_ForStartTranscriptAndEnd_WhateverTheHostZone()
    {
        var w = await StartAsync();
        // Ticket 1878's shape, moved to "three minutes ago" so the receipt-time future guard is not involved: the same UTC instant in every spelling.
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddMinutes(-3);

        string Spell(DateTime d, string form) => form switch
        {
            "Z" => d.ToString("yyyy-MM-ddTHH:mm:ss") + "Z",
            "offset0" => d.ToString("yyyy-MM-ddTHH:mm:ss") + "+00:00",
            "zoneless" => d.ToString("yyyy-MM-ddTHH:mm:ss"),
            _ => d.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00"       // the same instant written as UAE wall-clock WITH its offset
        };

        var tickets = new Dictionary<string, long>();
        foreach (var form in new[] { "Z", "offset0", "zoneless", "uae" })
        {
            var conversation = $"tz-{form}-{Guid.NewGuid():N}"[..24];
            var created = await w.Genesys.PostAsync("/api/genesys/tickets", Json(new
            {
                conversationId = conversation, channel = "WhatsApp", customerPhone = "+971500000014", departmentId = w.CustomerService,
                startedAtUtc = Spell(start, form)
            }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<GenesysAcceptedDto>())!.TicketId;
            tickets[form] = id;

            Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{id}", Json(new
            {
                conversationId = conversation,
                transcript = new object[]
                {
                    new { sender = "Customer", sentAtUtc = Spell(start.AddSeconds(5), form), body = "Hello" },
                    new { sender = "VirtualAgent", sentAtUtc = Spell(start.AddSeconds(9), form), body = "How can I help?" }
                }
            }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await w.Genesys.PatchAsync($"/api/genesys/tickets/{id}", Json(new
            {
                conversationId = conversation, ended = new { endedAtUtc = Spell(start.AddSeconds(40), form), endReason = "CustomerDisconnect" }
            }))).StatusCode);
        }

        using var db = Db();
        foreach (var (form, id) in tickets)
        {
            var interaction = await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.TicketId == id);
            Assert.True(interaction.InteractionStartedAtUtc!.Value.Ticks == start.Ticks, $"[{form}] start stored as {interaction.InteractionStartedAtUtc:O}, expected UTC {start:O}");
            Assert.True(interaction.EndedAtUtc!.Value.Ticks == start.AddSeconds(40).Ticks, $"[{form}] end stored as {interaction.EndedAtUtc:O}");

            var lines = await db.TicketInteractionMessages.AsNoTracking().Where(m => m.TicketInteractionId == interaction.TicketInteractionId).OrderBy(m => m.Sequence).ToListAsync();
            Assert.Equal([start.AddSeconds(5).Ticks, start.AddSeconds(9).Ticks], lines.Select(m => m.SentAtUtc.Ticks));

            // The API hands the Web exactly what is stored (no zone, no conversion); the Web adds +4 once.
            var shown = await InteractionAsync(w, id);
            Assert.Equal(start.Ticks, shown.StartedAtUtc.Ticks);
            Assert.Equal(DateTimeKind.Unspecified, shown.StartedAtUtc.Kind);
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
