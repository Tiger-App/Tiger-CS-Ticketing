using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.Integration;

namespace TigerCS.Tests.Ticketing.TakeOwnership;

/// <summary>
/// The one-time Call Center distribution, END TO END: the REAL Preview / Execute / Rollback scripts run against a real SQL Server database whose schema is the
/// application's OWN (created from the EF model, not from hand-written DDL), and then the REAL API is driven as the people involved. This is what proves that the
/// rows the scripts write are what the application's ownership rules expect:
/// <list type="bullet">
/// <item>the receiving agent sees the ticket in My Tickets and can move it InProgress, Resolve, Close and Transfer it through the normal workflow;</item>
/// <item>a previous owner (a department employee, or another Call Center agent) can no longer do any of that on the ticket - 403;</item>
/// <item>a ticket answered by an eligible agent goes to that agent, not to Round Robin;</item>
/// <item>tickets that are not Open are left alone; and Rollback gives every previous owner their ticket back.</item>
/// </list>
/// (The scripts' own exhaustive, oracle-based suite is <c>tests/backlog-distribution/run_tests.py</c>.) Without <c>TIGERCS_TEST_SQL_SERVER</c> it is SKIPPED.
/// </summary>
public sealed class BulkDistributionEndToEndSqlServerTests : IDisposable
{
    private readonly string? _master = Environment.GetEnvironmentVariable(VisibilitySqlServerFactAttribute.Variable);
    private readonly string _database = $"TigerCsBulkE2E_{Guid.NewGuid():N}";

    private string ConnectionString => new SqlConnectionStringBuilder(_master!) { InitialCatalog = _database }.ConnectionString;

    public void Dispose()
    {
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

    private static string RepoFile(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..")), name);

    /// <summary>Sets <c>DECLARE @Name type = value;</c> defaults in a script, exactly as the operator does by hand (and as the Python suite's patch() does).</summary>
    private string Script(string file, params (string Name, string Value)[] settings)
    {
        var text = File.ReadAllText(RepoFile(file));
        text = text.Replace("N'TigerCS_Ticketing_Prod'", $"N'{_database}'", StringComparison.Ordinal);
        foreach (var (name, value) in settings)
        {
            var pattern = new Regex(@"(DECLARE @" + name + @"\s+\w+(?:\([^)]*\))?\s*=\s*)[^;]*;");
            Assert.True(pattern.IsMatch(text), $"@{name} not found in {file}");
            text = pattern.Replace(text, m => m.Groups[1].Value + value + ";", 1);
        }

        return text;
    }

    private async Task RunAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs the PREVIEW (read only) and returns the seven approval values it prints - what an approver copies into Execute.</summary>
    private async Task<int[]> PreviewApprovalValuesAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            Script("Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql", ("ServiceIdsConfirmed", "1"), ("NoServiceAccountsExist", "1")), connection) { CommandTimeout = 180 };
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 7 && reader.GetName(0) == "ApprovedTicketCount" && await reader.ReadAsync())
            {
                return [.. Enumerable.Range(0, 7).Select(i => reader.GetInt32(i))];
            }
        }
        while (await reader.NextResultAsync());

        throw new InvalidOperationException("The preview did not print its approval values.");
    }

    private Task ExecuteAsync(Guid batch, int[] approved, bool dryRun = false) => RunAsync(Script(
        "Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql",
        ("DryRun", dryRun ? "1" : "0"), ("ServiceIdsConfirmed", "1"), ("NoServiceAccountsExist", "1"), ("BatchId", $"'{batch}'"),
        ("ApprovedTicketCount", approved[0].ToString()), ("ApprovedTransferCount", approved[1].ToString()), ("ApprovedAgentCount", approved[2].ToString()),
        ("ApprovedHeldBackPendingApprovalCount", approved[3].ToString()), ("ApprovedHeldBackActiveAiCount", approved[4].ToString()),
        ("ApprovedReassignedCount", approved[5].ToString()), ("ApprovedPlanFingerprint", approved[6].ToString())));

    private Task RollbackAsync(Guid batch) => RunAsync(Script("Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql", ("BatchId", $"'{batch}'"), ("DryRun", "0")));

    private async Task<(HttpClient Client, Guid EmployeeId)> LoginAsync(TigerCsApiFactory factory, string role, int departmentId)
    {
        var (username, password, employeeId) = await factory.SeedEmployeeAsync(role);
        await factory.AssignPrimaryDepartmentAsync(employeeId, departmentId);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private static async Task<TicketDetailDto> DetailAsync(HttpClient client, long id)
    {
        var response = await client.GetAsync($"/api/tickets/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static byte[] Version(TicketDetailDto ticket) => Convert.FromBase64String(ticket.RowVersion);

    [VisibilitySqlServerFact]
    public async Task Preview_Execute_Rollback_RunOnTheRealSchema_AndTheApplicationHonoursEveryOwnershipRule()
    {
        using (var master = new SqlConnection(_master))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_database}]";
            create.ExecuteNonQuery();
        }

        using var factory = new TigerCsApiFactory { SqlServerConnectionString = ConnectionString };
        await factory.SeedPrioritiesAsync();
        var cc = await factory.CreateDepartmentAsync("Call Center", "CC");
        var accounting = await factory.CreateDepartmentAsync("Accounting", "ACCT");

        // Three Call Center agents (A, B and X, the one who answers a call below), a department employee who owns an Open ticket today, and a Call Center agent (P2) who does.
        var (agentA, a) = await LoginAsync(factory, Roles.CsAgent, cc);
        var (agentB, b) = await LoginAsync(factory, Roles.CsAgent, cc);
        var (agentX, x) = await LoginAsync(factory, Roles.CsAgent, cc);
        var (previousOwner, p) = await LoginAsync(factory, Roles.DepartmentEmployee, accounting);
        var (previousAgent, p2) = await LoginAsync(factory, Roles.CsAgent, cc);
        var agents = new[] { a, b, x, p2 };

        long unowned, ownedByEmployee, ownedByAgentAnsweredByX, inProgressOwned;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var now = DateTime.UtcNow;
            Ticket Add(string number, int department, int ageDays, Guid? owner = null, bool inProgress = false)
            {
                var ticket = Ticket.CreateUnclassified(number, department, "AC not cooling", now.AddDays(-ageDays));
                if (owner is { } o)
                {
                    ticket.AssignTo(o);
                    if (inProgress)
                    {
                        ticket.ChangeStatus(TicketStatus.InProgress);
                    }
                }

                db.Tickets.Add(ticket);
                db.SaveChanges();
                if (owner is { } assigned)
                {
                    db.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, assigned, department, now.AddDays(-ageDays), assigned));
                    db.SaveChanges();
                }

                return ticket;
            }

            unowned = Add("TG-E2E-0001", accounting, 40).TicketId;                       // Open, nobody owns it
            ownedByEmployee = Add("TG-E2E-0002", accounting, 39, owner: p).TicketId;    // Open, a department employee owns it
            var answered = Add("TG-E2E-0003", cc, 38, owner: p2);                        // Open, in CC, owned by agent P2 ...
            ownedByAgentAnsweredByX = answered.TicketId;
            var interaction = TicketInteraction.CreateFromGenesys(
                answered.TicketId, WellKnownChannels.Phone, "+971500000000", "conv-e2e-0003", null, null, null, null, null,
                now.AddDays(-38), "Inbound", now.AddDays(-38), isOriginatingInteraction: true, genesysServiceRequestId: "e2e0003");
            interaction.End(now.AddDays(-38), "customer-disconnect");
            interaction.RecordHandlingAgentIfAbsent("genesys-x", x);                     // ... but the call was ANSWERED by X
            db.TicketInteractions.Add(interaction);
            inProgressOwned = Add("TG-E2E-0004", accounting, 37, owner: p, inProgress: true).TicketId;   // NOT Open: must be left alone
            db.SaveChanges();
        }

        // ---- the approver's read-only Preview, then the gated execution (a dry run first, as the runbook says) ----
        var approved = await PreviewApprovalValuesAsync();
        Assert.Equal([3, 2, 4, 0, 0, 2], approved[..6]);   // 3 Open tickets in the plan (2 move into CC), 4 agents, nothing held back, 2 lose their current owner
        await ExecuteAsync(Guid.NewGuid(), approved, dryRun: true);
        Assert.Null((await factory.GetTicketAsync(unowned))!.CurrentOwnerEmployeeId);                 // the dry run changed nothing
        var batch = Guid.NewGuid();
        await ExecuteAsync(batch, approved);

        // ---- who owns what now ----
        var t1 = (await factory.GetTicketAsync(unowned))!;
        var t2 = (await factory.GetTicketAsync(ownedByEmployee))!;
        var t3 = (await factory.GetTicketAsync(ownedByAgentAnsweredByX))!;
        var t4 = (await factory.GetTicketAsync(inProgressOwned))!;
        Assert.All(new[] { t1, t2, t3 }, t => Assert.Equal(cc, t.CurrentDepartmentId));
        Assert.Equal(accounting, t1.OriginatingDepartmentId);                                       // originating department preserved
        Assert.All(new[] { t1, t2, t3 }, t => Assert.Contains(t.CurrentOwnerEmployeeId!.Value, agents));
        Assert.Equal(x, t3.CurrentOwnerEmployeeId);                                                 // the agent who answered the call, not Round Robin
        Assert.NotEqual(p, t2.CurrentOwnerEmployeeId);
        Assert.NotEqual(t1.CurrentOwnerEmployeeId, t2.CurrentOwnerEmployeeId);                      // Round Robin: the two dealt tickets went to different agents
        Assert.Equal(p, t4.CurrentOwnerEmployeeId);                                                 // InProgress: untouched ...
        Assert.Equal(accounting, t4.CurrentDepartmentId);                                           // ... still with the department employee, still in Accounting
        Assert.Equal(TicketStatus.InProgress, t4.TicketStatus);
        Assert.All(new[] { t1, t2, t3 }, t => Assert.Equal(TicketStatus.Open, t.TicketStatus));     // status preserved

        HttpClient ClientOf(Guid employee) => employee == a ? agentA : employee == b ? agentB : employee == x ? agentX : previousAgent;

        // ---- the receiving agent: My Tickets, then the normal workflow: status, Resolve, Close ----
        var ownerOfT1 = ClientOf(t1.CurrentOwnerEmployeeId!.Value);
        var mine = (await (await ownerOfT1.GetAsync($"/api/tickets?ownerEmployeeId={t1.CurrentOwnerEmployeeId}&pageSize=100")).Content.ReadFromJsonAsync<TicketListResultDto>())!;
        Assert.Contains(mine.Items, i => i.TicketId == unowned);
        var detail = await DetailAsync(ownerOfT1, unowned);
        Assert.False(detail.IsReadOnlyAccess);
        Assert.Equal(HttpStatusCode.OK, (await ownerOfT1.PostAsJsonAsync($"/api/tickets/{unowned}/status", new ChangeStatusRequestDto("InProgress", Version(detail)))).StatusCode);
        detail = await DetailAsync(ownerOfT1, unowned);
        Assert.Equal(HttpStatusCode.OK, (await ownerOfT1.PostAsJsonAsync(
            $"/api/tickets/{unowned}/resolution", new ResolveTicketRequestDto("Resolved", "Sorted with the customer.", null, null, Version(detail)))).StatusCode);
        detail = await DetailAsync(ownerOfT1, unowned);
        Assert.Equal(HttpStatusCode.OK, (await ownerOfT1.PostAsJsonAsync($"/api/tickets/{unowned}/close", new CloseTicketRequestDto(Version(detail)))).StatusCode);

        // ---- the receiving agent: Transfer (of a ticket that was reassigned to them) ----
        var ownerOfT2 = ClientOf(t2.CurrentOwnerEmployeeId!.Value);
        detail = await DetailAsync(ownerOfT2, ownedByEmployee);
        var transferred = await ownerOfT2.PostAsJsonAsync(
            $"/api/tickets/{ownedByEmployee}/transfer",
            new TransferTicketRequestDto(accounting, "Needs Accounting", Version(detail), ReasonCode: TicketTransferReason.WrongDepartment));
        Assert.Equal(HttpStatusCode.OK, transferred.StatusCode);

        // ---- previous owners lose ordinary access ----
        // (a) the department employee: not the owner, the ticket is no longer in their department's scope
        Assert.Equal(HttpStatusCode.Forbidden, (await previousOwner.GetAsync($"/api/tickets/{unowned}")).StatusCode);
        // (b) the Call Center agent P2 owned T3 and lost it to the agent who answered it: no Resolve / Close / Transfer / status change on it any more
        var t3Detail = await DetailAsync(agentX, ownedByAgentAnsweredByX);
        Assert.Equal(HttpStatusCode.Forbidden, (await previousAgent.PostAsJsonAsync($"/api/tickets/{ownedByAgentAnsweredByX}/close", new CloseTicketRequestDto(Version(t3Detail)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await previousAgent.PostAsJsonAsync($"/api/tickets/{ownedByAgentAnsweredByX}/status", new ChangeStatusRequestDto("InProgress", Version(t3Detail)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await previousAgent.PostAsJsonAsync(
            $"/api/tickets/{ownedByAgentAnsweredByX}/transfer", new TransferTicketRequestDto(accounting, "x", Version(t3Detail), ReasonCode: TicketTransferReason.Other))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await previousAgent.PostAsJsonAsync(
            $"/api/tickets/{ownedByAgentAnsweredByX}/resolution", new ResolveTicketRequestDto("Resolved", "x", null, null, Version(t3Detail)))).StatusCode);
        Assert.Equal(x, (await factory.GetTicketAsync(ownedByAgentAnsweredByX))!.CurrentOwnerEmployeeId);   // and nothing of that moved it
        // (c) the InProgress ticket the batch left alone is still fully the department employee's to work
        Assert.Equal(HttpStatusCode.OK, (await previousOwner.GetAsync($"/api/tickets/{inProgressOwned}")).StatusCode);

        // ---- Rollback: only what is still exactly as the batch left it; here T3 is untouched, T1 closed and T2 transferred are skipped ----
        await RollbackAsync(batch);
        var afterRollback = (await factory.GetTicketAsync(ownedByAgentAnsweredByX))!;
        Assert.Equal(p2, afterRollback.CurrentOwnerEmployeeId);                                       // the previous owner owns it again ...
        Assert.Equal(cc, afterRollback.CurrentDepartmentId);
        var restored = await DetailAsync(previousAgent, ownedByAgentAnsweredByX);                      // ... and the application agrees: she can open it as owner
        Assert.False(restored.IsReadOnlyAccess);
        Assert.Equal(HttpStatusCode.OK, (await previousAgent.PostAsJsonAsync(
            $"/api/tickets/{ownedByAgentAnsweredByX}/status", new ChangeStatusRequestDto("InProgress", Version(restored)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentX.PostAsJsonAsync(
            $"/api/tickets/{ownedByAgentAnsweredByX}/close", new CloseTicketRequestDto(Version(await DetailAsync(previousAgent, ownedByAgentAnsweredByX))))).StatusCode);   // and X, who held it meanwhile, no longer can
        Assert.Equal("Closed", (await DetailAsync(ownerOfT1, unowned)).TicketStatus);                  // a ticket already worked to Closed since the batch is left exactly as it is

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Empty(await verifyDb.TicketAssignments.AsNoTracking().Where(a2 => a2.IsCurrent).GroupBy(a2 => a2.TicketId).Where(g => g.Count() > 1).ToListAsync());
    }
}
