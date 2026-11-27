using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.TakeOwnership;
using DomainPermissions = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Authorization regression suite for the Call Agent (CS Agent) ticket rules, through the REAL HTTP pipeline (DI, the ticket-access filter,
/// permission attributes, services, repositories):
/// <list type="bullet">
/// <item>a Call Agent sees ONLY the tickets currently assigned to their own user id - never an unassigned ticket (Call Center queue,
/// Genesys intake, another department's), never an AI-owned ticket, never a department-queue ticket, never another agent's ticket;</item>
/// <item>that holds on every surface: lists and totals, global search, Ticket Details and every <c>api/tickets/{id}</c> route by direct URL,
/// the dashboard counts and drill-downs, the customer directory / profile / ticket history;</item>
/// <item>a Call Agent can NOT take ownership of an unassigned ticket (403, nothing changes), whatever their permission overrides, and is
/// never told they can (<c>canTakeOwnership</c>);</item>
/// <item>CS Manager and CS Supervisor keep their full scope and still assign: a ticket assigned to an agent is visible to them at once, and a
/// reassignment takes it away from the previous agent at once.</item>
/// </list>
/// Regression for: "a Call Agent could open an unassigned Call Center ticket read-only and see the Take Ownership button".
/// </summary>
public sealed class CallAgentTicketAuthorizationTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;
    private static int _counter;
    private static int _customerCounter = 7_000;

    private sealed record Departments(int CallCenter, int Finance, int Facilities);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TigerCsApiFactory, Task<Departments>> DepartmentsByFactory = new();

    /// <summary>One set of departments per shared factory (a second department coded CC would break the Call Center lookup).</summary>
    private Task<Departments> DepartmentsAsync() => DepartmentsByFactory.GetValue(_factory, async f =>
    {
        await f.SeedPrioritiesAsync();
        return new Departments(
            await f.CreateDepartmentAsync("Call Center", "CC"),
            await f.CreateDepartmentAsync("Finance CA", "FINCA"),
            await f.CreateDepartmentAsync("Facilities CA", "FACCA"));
    });

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientAsync(string role, int? departmentId = null)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        if (departmentId is { } department)
        {
            await _factory.AssignPrimaryDepartmentAsync(employeeId, department);
        }

        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private async Task<(HttpClient Client, Guid EmployeeId)> CallAgentAsync() =>
        await ClientAsync(Roles.CsAgent, (await DepartmentsAsync()).CallCenter);

    /// <summary>An open ticket in <paramref name="departmentId"/>, optionally assigned (with its current assignment row) and shaped.</summary>
    private async Task<long> TicketAsync(
        int departmentId, Guid? assignedTo = null, Action<Ticket>? shape = null, Func<long, TigerCsDbContext, Task>? afterSave = null)
    {
        long id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var ticket = ClaimTestData.Open($"TG-CA-{Interlocked.Increment(ref _counter):D6}-{Guid.NewGuid():N}"[..28], departmentId);
            shape?.Invoke(ticket);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            id = ticket.TicketId;
            if (afterSave is not null)
            {
                await afterSave(id, db);
                await db.SaveChangesAsync();
            }
        }

        if (assignedTo is { } owner)
        {
            await _factory.AssignOwnerAsync(id, owner);
        }

        return id;
    }

    /// <summary>An unassigned ticket that entered through Genesys and needs a person - the "Genesys intake queue" shape.</summary>
    private Task<long> GenesysIntakeTicketAsync(int departmentId) =>
        TicketAsync(departmentId, afterSave: (id, db) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id, requiresHuman: true));
            return Task.CompletedTask;
        });

    private async Task<long> AiOwnedTicketAsync(int departmentId)
    {
        var aiAgent = Guid.NewGuid();
        return await TicketAsync(departmentId, shape: t => Assert.True(t.AssignToAiAgent(aiAgent)), afterSave: (id, db) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id));   // the chatbot's live conversation
            return Task.CompletedTask;
        });
    }

    private static async Task<TicketListResultDto> ListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/tickets?pageSize=200" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketListResultDto>())!;
    }

    private static async Task<long[]> IdsAsync(HttpClient client, string query = "") =>
        [.. (await ListAsync(client, query)).Items.Select(i => i.TicketId).Order()];

    private static async Task<HttpStatusCode> DetailStatusAsync(HttpClient client, long ticketId) =>
        (await client.GetAsync($"/api/tickets/{ticketId}")).StatusCode;

    private static async Task<TicketDetailDto> DetailAsync(HttpClient client, long ticketId)
    {
        var response = await client.GetAsync($"/api/tickets/{ticketId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static Task<HttpResponseMessage> TakeAsync(HttpClient client, long ticketId, byte[]? rowVersion = null) =>
        client.PostAsJsonAsync($"/api/tickets/{ticketId}/ownership", new TakeOwnershipRequestDto(rowVersion));

    private static void AssertRefused(HttpStatusCode status, string what) =>
        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{what} answered {(int)status}");

    /// <summary>The manager assigns (the real endpoint), so the assignment row, owner and audit are exactly what production writes.</summary>
    private static async Task AssignAsync(HttpClient manager, long ticketId, Guid assignee)
    {
        var current = await DetailAsync(manager, ticketId);
        var response = await manager.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/assignment",
            new AssignTicketRequestDto(
                assignee, Convert.FromBase64String(current.RowVersion),
                current.CurrentOwnerEmployeeId is null ? null : TicketTransferReason.Other));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient? _manager;
    private HttpClient? _supervisor;

    private async Task<HttpClient> ManagerAsync() => _manager ??= (await ClientAsync(Roles.CsManager)).Client;

    private async Task<HttpClient> SupervisorAsync() => _supervisor ??= (await ClientAsync(Roles.CsSupervisor)).Client;

    // =====================================================================
    // The reported defect, end to end
    // =====================================================================

    [Fact]
    public async Task AnUnassignedCallCenterQueueTicket_IsNotVisibleToACallAgent_InAnyForm_AndThereIsNoTakeOwnershipButton()
    {
        var d = await DepartmentsAsync();
        var (agent, _) = await CallAgentAsync();
        var queued = await TicketAsync(d.CallCenter);

        // Not in the Queue, not under the Call Center department filter, not in the (retired) "available to take" view.
        Assert.DoesNotContain(queued, await IdsAsync(agent));
        Assert.DoesNotContain(queued, await IdsAsync(agent, $"&departmentId={d.CallCenter}"));
        Assert.DoesNotContain(queued, await IdsAsync(agent, "&availableToTake=true"));
        Assert.DoesNotContain(queued, await IdsAsync(agent, "&inDepartmentQueue=true"));

        // Not openable - not even read-only - so there is no page on which a Take Ownership button could appear.
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(agent, queued));

        // And the backend refuses the action itself, not only the hidden button.
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, queued)).StatusCode);
        Assert.Null((await _factory.GetTicketAsync(queued))!.CurrentOwnerEmployeeId);
    }

    // =====================================================================
    // Lists, queues, totals, search: only my own assigned tickets
    // =====================================================================

    [Fact]
    public async Task ACallAgentsListsTotalsAndSearch_HoldOnlyTheTicketsAssignedToThem()
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await CallAgentAsync();
        var (_, otherAgentId) = await CallAgentAsync();
        var (_, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);

        var mine = await TicketAsync(d.CallCenter, agentId);
        var mineInAnotherDepartment = await TicketAsync(d.Finance, agentId);
        var hidden = new Dictionary<string, long>
        {
            ["unassigned in the Call Center queue"] = await TicketAsync(d.CallCenter),
            ["unassigned in another department"] = await TicketAsync(d.Finance),
            ["unassigned Genesys intake"] = await GenesysIntakeTicketAsync(d.CallCenter),
            ["unassigned Genesys intake, other department"] = await GenesysIntakeTicketAsync(d.Facilities),
            ["AI-owned"] = await AiOwnedTicketAsync(d.CallCenter),
            ["active AI conversation"] = await TicketAsync(d.CallCenter, afterSave: (id, db) =>
            {
                db.TicketInteractions.Add(ClaimTestData.Genesys(id));
                return Task.CompletedTask;
            }),
            ["assigned to another Call Agent"] = await TicketAsync(d.CallCenter, otherAgentId),
            ["assigned to a department employee"] = await TicketAsync(d.Facilities, employeeId),
            ["waiting on a pending approval"] = await TicketAsync(d.CallCenter, afterSave: (id, db) =>
            {
                db.TicketApprovals.Add(ClaimTestData.PendingApproval(id, otherAgentId));
                return Task.CompletedTask;
            }),
        };

        var queue = await IdsAsync(agent);
        Assert.Contains(mine, queue);
        Assert.Contains(mineInAnotherDepartment, queue);
        foreach (var (what, id) in hidden)
        {
            Assert.DoesNotContain(id, queue);
            Assert.DoesNotContain(id, await IdsAsync(agent, $"&ownerEmployeeId={agentId}"));
            Assert.DoesNotContain(id, await IdsAsync(agent, "&activeOnly=true"));
            Assert.True((await DetailStatusAsync(agent, id)) == HttpStatusCode.Forbidden, $"{what}: Ticket Details was not refused");
        }

        // Totals agree with the visible rows - no leakage through counts or paging.
        var page = await ListAsync(agent);
        Assert.Equal(page.Items.Count, page.TotalCount);
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, i => Assert.Equal(agentId, i.CurrentOwnerEmployeeId));

        // Global search: a hit on someone else's ticket's own number returns nothing; on my own ticket it returns it.
        foreach (var (what, id) in hidden)
        {
            var number = (await _factory.GetTicketAsync(id))!.TicketNumber;
            Assert.True((await ListAsync(agent, $"&search={number}")).Items.Count == 0, $"{what}: search disclosed the ticket");
        }

        var ownNumber = (await _factory.GetTicketAsync(mine))!.TicketNumber;
        Assert.Equal([mine], (await ListAsync(agent, $"&search={ownNumber}")).Items.Select(i => i.TicketId).ToArray());

        // ...whereas the CS Manager and CS Supervisor reach every one of them.
        foreach (var privileged in new[] { await ManagerAsync(), await SupervisorAsync() })
        {
            var all = await IdsAsync(privileged);
            Assert.Contains(mine, all);
            foreach (var (_, id) in hidden)
            {
                Assert.Contains(id, all);
                Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(privileged, id));
            }
        }
    }

    // =====================================================================
    // Direct URL / API access: refused before any action runs
    // =====================================================================

    [Fact]
    public async Task DirectAccessById_IsRefusedForEveryTicketThatIsNotAssignedToTheAgent_OnEveryReadAndWriteRoute()
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await CallAgentAsync();
        var (_, otherAgentId) = await CallAgentAsync();
        var unassigned = await TicketAsync(d.CallCenter);
        var intake = await GenesysIntakeTicketAsync(d.CallCenter);
        var aiOwned = await AiOwnedTicketAsync(d.CallCenter);
        var someonesElse = await TicketAsync(d.CallCenter, otherAgentId);
        var version = Convert.FromBase64String((await DetailAsync(await ManagerAsync(), someonesElse)).RowVersion);

        foreach (var id in new[] { unassigned, intake, aiOwned, someonesElse })
        {
            foreach (var read in new[]
            {
                "", "/history", "/activity-timeline", "/interactions", "/notes", "/customer-history", "/customer-profile", "/assignment-history",
                "/reviews", "/approvals", "/sla", "/sla/priority-downgrade-requests", "/collections-transfer-options"
            })
            {
                var status = (await agent.GetAsync($"/api/tickets/{id}{read}")).StatusCode;
                AssertRefused(status, $"GET /api/tickets/{id}{read}");
            }

            var writes = new (string Path, object Body)[]
            {
                ("/notes", new CreateNoteRequestDto("Should not be allowed.")),
                ("/status", new ChangeStatusRequestDto("InProgress", version)),
                ("/resolution", new ResolveTicketRequestDto("Resolved", "x", null, null, version)),
                ("/close", new CloseTicketRequestDto(version)),
                ("/reopen", new ReopenTicketRequestDto("x", d.CallCenter, version)),
                ("/assignment", new AssignTicketRequestDto(agentId, version)),   // not even to themselves
                ("/transfer", new TransferTicketRequestDto(d.Finance, "x", version)),
                ("/approvals", new RequestApprovalRequestDto("ReopenApproval", "x")),
                ("/ownership", new TakeOwnershipRequestDto(version)),             // Take Ownership
            };
            foreach (var (path, body) in writes)
            {
                var status = (await agent.PostAsJsonAsync($"/api/tickets/{id}{path}", body)).StatusCode;
                Assert.True(status == HttpStatusCode.Forbidden, $"POST /api/tickets/{id}{path} answered {(int)status}");
            }
        }

        // Nothing changed.
        Assert.Null((await _factory.GetTicketAsync(unassigned))!.CurrentOwnerEmployeeId);
        Assert.Null((await _factory.GetTicketAsync(intake))!.CurrentOwnerEmployeeId);
        Assert.Equal(otherAgentId, (await _factory.GetTicketAsync(someonesElse))!.CurrentOwnerEmployeeId);
    }

    // =====================================================================
    // Take Ownership: refused for every Call Agent, whatever their rights
    // =====================================================================

    [Fact]
    public async Task TakeOwnership_IsRefusedForACallAgent_ForEveryUnassignedTicketShape_AndTheFlagIsNeverSet()
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await CallAgentAsync();
        var worker = Guid.NewGuid();
        var shapes = new Dictionary<string, long>
        {
            ["call center queue"] = await TicketAsync(d.CallCenter),
            ["other department queue"] = await TicketAsync(d.Finance),
            ["genesys intake"] = await GenesysIntakeTicketAsync(d.CallCenter),
            ["released in-progress"] = await TicketAsync(d.Facilities, shape: t => ClaimTestData.MakeInProgressUnowned(t, worker)),
            ["ai conversation that ended"] = await TicketAsync(d.Finance, afterSave: (id, db) =>
            {
                db.TicketInteractions.Add(ClaimTestData.Genesys(id, ended: true));
                return Task.CompletedTask;
            }),
        };

        foreach (var (what, id) in shapes)
        {
            Assert.True((await TakeAsync(agent, id)).StatusCode == HttpStatusCode.Forbidden, $"{what}: Take Ownership was not refused");
            Assert.Null((await _factory.GetTicketAsync(id))!.CurrentOwnerEmployeeId);
        }

        // No ticket was moved, assigned or audited by the refused attempts.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ids = shapes.Values.ToArray();
        Assert.Empty(await db.TicketAssignments.AsNoTracking().Where(a => ids.Contains(a.TicketId) && a.AssignedEmployeeId == agentId).ToListAsync());
        var idStrings = ids.Select(i => i.ToString()).ToArray();
        Assert.Empty(await db.AuditEntries.AsNoTracking().Where(a => a.Action == "TakeOwnership" && idStrings.Contains(a.EntityId)).ToListAsync());
        Assert.Equal(d.CallCenter, (await _factory.GetTicketAsync(shapes["call center queue"]))!.CurrentDepartmentId);
        Assert.Equal(d.Finance, (await _factory.GetTicketAsync(shapes["other department queue"]))!.CurrentDepartmentId);

        // The flag the Web page renders its button from is never set for the agent's own ticket either (nothing left to take).
        var mine = await TicketAsync(d.CallCenter, agentId);
        Assert.False((await DetailAsync(agent, mine)).CanTakeOwnership);
    }

    [Theory]
    [InlineData(DomainPermissions.TicketsEdit)]
    [InlineData(DomainPermissions.TicketsAssign)]
    public async Task APermissionGrant_NeverLetsACallAgentTakeOwnershipOrSeeTheQueue(string grantedPermission)
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await CallAgentAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            db.UserPermissionOverrides.Add(new UserPermissionOverride(agentId, grantedPermission, PermissionEffect.Grant, null, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var queued = await TicketAsync(d.CallCenter);

        Assert.DoesNotContain(queued, await IdsAsync(agent));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(agent, queued));
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, queued)).StatusCode);
        Assert.Null((await _factory.GetTicketAsync(queued))!.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task OtherRoles_CannotUseTheCallAgentTakeOwnershipRoute_Either_AndManagersKeepAssigning()
    {
        var d = await DepartmentsAsync();
        var (employee, _) = await ClientAsync(Roles.DepartmentEmployee, d.CallCenter);
        var queued = await TicketAsync(d.CallCenter);

        // A department employee, a supervisor and a manager are not Call Agents: the self-claim route is not theirs. (Managers and supervisors
        // place work through Assign, below.)
        foreach (var client in new[] { employee, await SupervisorAsync(), await ManagerAsync() })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(client, queued)).StatusCode);
        }

        Assert.Null((await _factory.GetTicketAsync(queued))!.CurrentOwnerEmployeeId);

        var (_, agentId) = await CallAgentAsync();
        await AssignAsync(await ManagerAsync(), queued, agentId);
        Assert.Equal(agentId, (await _factory.GetTicketAsync(queued))!.CurrentOwnerEmployeeId);
    }

    // =====================================================================
    // Assignment and reassignment take effect immediately
    // =====================================================================

    [Fact]
    public async Task OnceAssignedToACallAgent_TheTicketIsVisibleToThemAtOnce_AndReassigningItRemovesItFromThePreviousAgentAtOnce()
    {
        var d = await DepartmentsAsync();
        var manager = await ManagerAsync();
        var (first, firstId) = await CallAgentAsync();
        var (second, secondId) = await CallAgentAsync();
        var ticket = await GenesysIntakeTicketAsync(d.CallCenter);

        // Unassigned: neither agent sees it.
        Assert.DoesNotContain(ticket, await IdsAsync(first));
        Assert.DoesNotContain(ticket, await IdsAsync(second));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(first, ticket));

        // Assigned to the first agent: visible and workable for them immediately, still invisible to the other.
        await AssignAsync(manager, ticket, firstId);
        Assert.Contains(ticket, await IdsAsync(first));
        Assert.Contains(ticket, await IdsAsync(first, $"&ownerEmployeeId={firstId}"));
        var detail = await DetailAsync(first, ticket);
        Assert.False(detail.IsReadOnlyAccess);
        Assert.False(detail.CanTakeOwnership);
        Assert.DoesNotContain(ticket, await IdsAsync(second));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(second, ticket));
        Assert.Equal(1, (await first.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard"))!.MyTickets);

        // Reassigned to the second agent: gone from the first agent on the very next request - list, detail, sub-resources, writes, counts.
        await AssignAsync(manager, ticket, secondId);
        Assert.DoesNotContain(ticket, await IdsAsync(first));
        Assert.DoesNotContain(ticket, await IdsAsync(first, $"&ownerEmployeeId={firstId}"));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(first, ticket));
        Assert.Equal(HttpStatusCode.Forbidden, (await first.GetAsync($"/api/tickets/{ticket}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync($"/api/tickets/{ticket}/notes", new CreateNoteRequestDto("late"))).StatusCode);
        Assert.Equal(0, (await first.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard"))!.MyTickets);
        Assert.Contains(ticket, await IdsAsync(second));
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(second, ticket));
        Assert.Equal(1, (await second.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard"))!.MyTickets);
    }

    // =====================================================================
    // Dashboard counts and drill-downs
    // =====================================================================

    [Fact]
    public async Task DashboardCountsAndDrillDowns_ForACallAgent_CoverOnlyTheirOwnTickets_WhileManagersCountEverything()
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await CallAgentAsync();
        var (_, otherAgentId) = await CallAgentAsync();
        var mine = await TicketAsync(d.CallCenter, agentId);
        var unassigned = await TicketAsync(d.CallCenter);
        var intake = await GenesysIntakeTicketAsync(d.CallCenter);
        var aiOwned = await AiOwnedTicketAsync(d.CallCenter);
        var someonesElse = await TicketAsync(d.CallCenter, otherAgentId);

        var summary = (await agent.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard"))!;
        Assert.Equal(1, summary.OpenTickets);
        Assert.Equal(1, summary.MyTickets);
        Assert.Equal(0, summary.Unassigned);   // the unassigned queue is not the agent's to see or count

        // Drill-downs are the same SQL as the cards: every one of them is limited to the agent's own ticket.
        foreach (var drill in new[]
        {
            "&activeOnly=true", "&unclassified=true", "&inDepartmentQueue=true", "&slaBreached=true", "&dueToday=true", "&ticketStatus=Open",
            $"&departmentId={d.CallCenter}",
        })
        {
            var ids = await IdsAsync(agent, drill);
            Assert.DoesNotContain(unassigned, ids);
            Assert.DoesNotContain(intake, ids);
            Assert.DoesNotContain(aiOwned, ids);
            Assert.DoesNotContain(someonesElse, ids);
        }

        Assert.Equal([mine], await IdsAsync(agent, "&activeOnly=true"));

        var overview = (await agent.GetFromJsonAsync<DashboardOverviewDto>("/api/dashboard/overview"))!;
        Assert.Equal(1, overview.Kpis.OpenTickets);
        Assert.Equal(1, overview.Kpis.MyTickets);

        // A CS Manager's numbers include the whole queue (the factory database is shared, so assert "at least" what this test seeded).
        var managerSummary = (await (await ManagerAsync()).GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard"))!;
        Assert.True(managerSummary.OpenTickets >= 5);
        Assert.True(managerSummary.Unassigned >= 3);
    }

    // =====================================================================
    // Customer directory, profile and ticket history
    // =====================================================================

    private async Task<long> CustomerTicketAsync(int customerId, Guid? assignedTo)
    {
        var d = await DepartmentsAsync();
        var categoryId = await _factory.CreateCategoryAsync("General", d.CallCenter);
        long id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var ticket = Ticket.CreateVerifiedFromCrmBuyer(
                $"TCK-{Guid.NewGuid():N}"[..24], d.CallCenter, customerId, 1, 1, 1, "Mohamed Ismaik", null, null, categoryId,
                (byte)PriorityLevel.High, "request", DateTime.UtcNow);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            id = ticket.TicketId;
        }

        if (assignedTo is { } owner)
        {
            await _factory.AssignOwnerAsync(id, owner);
        }

        return id;
    }

    [Fact]
    public async Task CustomerProfileAndTicketHistory_ShowACallAgentOnlyTicketsAssignedToThem()
    {
        var (agent, agentId) = await CallAgentAsync();
        var (_, otherAgentId) = await CallAgentAsync();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var shared = Interlocked.Increment(ref _customerCounter);       // one customer, three tickets: mine, someone else's, unassigned
        var onlyQueued = Interlocked.Increment(ref _customerCounter);   // a customer whose only ticket is in the queue
        var mine = await CustomerTicketAsync(shared, agentId);
        var theirs = await CustomerTicketAsync(shared, otherAgentId);
        var queued = await CustomerTicketAsync(shared, null);
        var queuedOnly = await CustomerTicketAsync(onlyQueued, null);

        // The profile's ticket count is the agent's own tickets, not the customer's whole history.
        var profile = await agent.GetFromJsonAsync<CustomerDirectoryProfileDto>($"/api/customers/profile/{Uri.EscapeDataString($"crm:{shared}")}", web);
        Assert.Equal(1, profile!.TotalTickets);

        // A customer who only has unassigned tickets is not even discoverable by the agent.
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/api/customers/profile/{Uri.EscapeDataString($"crm:{onlyQueued}")}")).StatusCode);
        var directory = await agent.GetFromJsonAsync<CustomerDirectoryListResultDto>("/api/customers?pageSize=200", web);
        Assert.DoesNotContain(directory!.Items, c => c.CustomerKey == $"crm:{onlyQueued}");

        // Per-ticket customer history: the agent's own ticket lists only their visible history; the others are refused outright.
        var history = await agent.GetAsync($"/api/tickets/{mine}/customer-history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyBody = await history.Content.ReadAsStringAsync();
        Assert.DoesNotContain((await _factory.GetTicketAsync(theirs))!.TicketNumber, historyBody, StringComparison.Ordinal);
        Assert.DoesNotContain((await _factory.GetTicketAsync(queued))!.TicketNumber, historyBody, StringComparison.Ordinal);
        Assert.DoesNotContain((await _factory.GetTicketAsync(queuedOnly))!.TicketNumber, historyBody, StringComparison.Ordinal);
        AssertRefused((await agent.GetAsync($"/api/tickets/{queued}/customer-history")).StatusCode, "customer-history of an unassigned ticket");
        AssertRefused((await agent.GetAsync($"/api/tickets/{theirs}/customer-history")).StatusCode, "customer-history of another agent's ticket");

        // A CS Manager sees the customer's whole history.
        var managerProfile = await (await ManagerAsync()).GetFromJsonAsync<CustomerDirectoryProfileDto>($"/api/customers/profile/{Uri.EscapeDataString($"crm:{shared}")}", web);
        Assert.Equal(3, managerProfile!.TotalTickets);
    }

    // =====================================================================
    // Closed and preserved behaviour
    // =====================================================================

    [Fact]
    public async Task AnAgentsOwnTicket_StaysFullyWorkable_AndASupervisorManagerStillSeeAndAssignAcrossDepartments()
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await CallAgentAsync();
        var mine = await TicketAsync(d.CallCenter, agentId);

        var detail = await DetailAsync(agent, mine);
        Assert.False(detail.IsReadOnlyAccess);
        var noted = await agent.PostAsJsonAsync($"/api/tickets/{mine}/notes", new CreateNoteRequestDto("Called the customer."));
        Assert.True(noted.IsSuccessStatusCode, noted.StatusCode.ToString());

        // The supervisor and the manager see tickets of every department, assigned or not.
        var finance = await TicketAsync(d.Finance);
        foreach (var privileged in new[] { await ManagerAsync(), await SupervisorAsync() })
        {
            Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(privileged, finance));
            Assert.Contains(finance, await IdsAsync(privileged));
        }
    }
}
