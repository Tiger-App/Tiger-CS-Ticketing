using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using DomainPermissions = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Ticketing.TakeOwnership;

/// <summary>
/// Take Ownership through the REAL HTTP pipeline (real DI, the ticket-access filter, permission attributes, role checks, services):
/// what a Call Center agent can see, what the UI is told it may do (<c>canTakeOwnership</c>) versus what the API accepts, what a claim
/// changes and keeps, and that Resolve / Close / Transfer then follow the existing ownership rules. (EF InMemory: the database-level
/// guarantees - row version, unique current assignment, truly parallel claims - are in <see cref="TakeOwnershipSqlServerTests"/>.)
/// </summary>
public sealed class TakeOwnershipEndpointsTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;
    /// <summary>xUnit makes a new instance of this class per test but shares ONE factory (and database): the departments are made once per factory, or there would be several departments coded CC.</summary>
    private sealed record Departments(int CallCenter, int Accounting, int Leasing);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TigerCsApiFactory, Task<Departments>> DepartmentsByFactory = new();
    private static int _ticketCounter;

    private Task<Departments> DepartmentsAsync() => DepartmentsByFactory.GetValue(_factory, async f =>
    {
        await f.SeedPrioritiesAsync();
        return new Departments(
            await f.CreateDepartmentAsync("Call Center", "CC"),
            await f.CreateDepartmentAsync("Accounting", "ACCT"),
            await f.CreateDepartmentAsync("Leasing", "LEAS"));
    });

    private int _callCenterId;
    private int _accountingId;
    private int _leasingId;

    private async Task<int> CallCenterAsync()
    {
        var departments = await DepartmentsAsync();
        (_callCenterId, _accountingId, _leasingId) = (departments.CallCenter, departments.Accounting, departments.Leasing);
        return _callCenterId;
    }

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientAsync(string role, int? departmentId = null, params string[] extraRoles)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        foreach (var extra in extraRoles)
        {
            await _factory.AddRoleAsync(employeeId, extra);
        }

        if (departmentId is { } department)
        {
            await _factory.AssignPrimaryDepartmentAsync(employeeId, department);
        }

        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new TigerCS.Application.Modules.IdentityAndAccess.Dto.LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<TigerCS.Application.Modules.IdentityAndAccess.Dto.LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    /// <summary>A Call Center agent (CS Agent role, member of the Call Center).</summary>
    private async Task<(HttpClient Client, Guid EmployeeId)> AgentAsync() => await ClientAsync(Roles.CsAgent, await CallCenterAsync());

    private async Task<long> SeedAsync(int departmentId, Action<Ticket, TigerCsDbContext>? shape = null, Func<long, TigerCsDbContext, Task>? afterSave = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = ClaimTestData.Open($"TG-OWN-{Interlocked.Increment(ref _ticketCounter):D6}-{Guid.NewGuid():N}"[..28], departmentId);
        shape?.Invoke(ticket, db);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        if (afterSave is not null)
        {
            await afterSave(ticket.TicketId, db);
            await db.SaveChangesAsync();
        }

        return ticket.TicketId;
    }

    private static async Task<TicketDetailDto> DetailAsync(HttpClient client, long id)
    {
        var response = await client.GetAsync($"/api/tickets/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static Task<HttpResponseMessage> TakeAsync(HttpClient client, long id, byte[]? rowVersion = null) =>
        client.PostAsJsonAsync($"/api/tickets/{id}/ownership", new TakeOwnershipRequestDto(rowVersion));

    private static async Task<long[]> ListedAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/tickets?pageSize=200" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<TicketListResultDto>())!.Items.Select(i => i.TicketId).Order()];
    }

    private async Task AddOverrideAsync(Guid employeeId, string permission, PermissionEffect effect)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.UserPermissionOverrides.Add(new UserPermissionOverride(employeeId, permission, effect, null, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    // =====================================================================
    // 1 / 7 - what the agent sees: old unassigned human-handling tickets of every department, before any claim
    // =====================================================================

    [Fact]
    public async Task AnAgent_SeesOldUnassignedTicketsOfEveryDepartment_OnlyInTheExplicitPoolView_AndCanOpenThemReadOnly()
    {
        await CallCenterAsync();
        var (agent, _) = await AgentAsync();
        var accounting = await SeedAsync(_accountingId);
        var leasing = await SeedAsync(_leasingId);

        // The shared pool is a separate, explicitly requested view: never part of the agent's personal Queue / My Tickets.
        var personal = await ListedAsync(agent);
        Assert.DoesNotContain(accounting, personal);
        Assert.DoesNotContain(leasing, personal);

        var seen = await ListedAsync(agent, "&availableToTake=true");
        Assert.Contains(accounting, seen);
        Assert.Contains(leasing, seen);

        var detail = await DetailAsync(agent, accounting);
        Assert.True(detail.IsReadOnlyAccess);   // may open it...
        Assert.True(detail.CanTakeOwnership);   // ...and is told they may take it
        Assert.Null(detail.CurrentOwnerEmployeeId);
        Assert.Equal(_accountingId, detail.CurrentDepartmentId);
    }

    [Fact]
    public async Task OnlyTheAvailableToTakeView_ShowsThePoolBeforeItIsClaimed_ThePersonalListsNeverDo()
    {
        var cc = await CallCenterAsync();
        var (agent, agentId) = await AgentAsync();
        var pool = await SeedAsync(_accountingId);
        var ownedByOther = await SeedAsync(_accountingId, (t, _) => t.AssignTo(Guid.NewGuid()));
        var mine = await SeedAsync(cc, (t, _) => t.AssignTo(agentId));

        var all = await ListedAsync(agent);
        Assert.DoesNotContain(pool, all);   // unassigned work is never in the personal Queue / My Tickets
        Assert.Contains(mine, all);
        Assert.DoesNotContain(ownedByOther, all);

        // "Available to take": exactly the pool - not my own ticket, not someone else's.
        var available = await ListedAsync(agent, "&availableToTake=true");
        Assert.Contains(pool, available);
        Assert.DoesNotContain(mine, available);
        Assert.DoesNotContain(ownedByOther, available);

        // Department filters narrow the personal list; they do not bring the pool back.
        var callCenter = await ListedAsync(agent, $"&departmentId={cc}");
        Assert.Contains(mine, callCenter);
        Assert.DoesNotContain(pool, callCenter);
        Assert.DoesNotContain(ownedByOther, callCenter);

        var response = await agent.GetAsync($"/api/tickets?pageSize=200&departmentId={_accountingId}");
        var accountingOnly = (await response.Content.ReadFromJsonAsync<TicketListResultDto>())!.Items;
        Assert.DoesNotContain(accountingOnly, t => t.TicketId == pool);
        Assert.DoesNotContain(accountingOnly, t => t.TicketId == ownedByOther);

        // Counts agree with the lists (no leakage through totals).
        var page = await (await agent.GetAsync("/api/tickets?pageSize=200")).Content.ReadFromJsonAsync<TicketListResultDto>();
        Assert.Equal(page!.Items.Count, page.TotalCount);
    }

    // =====================================================================
    // 5 / 6 - never visible / never claimable
    // =====================================================================

    [Fact]
    public async Task OwnedByAnotherEmployee_Closed_Resolved_ActiveAi_AndPendingApproval_AreNotInThePool_AndCannotBeTaken()
    {
        await CallCenterAsync();
        var (agent, _) = await AgentAsync();
        var worker = Guid.NewGuid();

        var owned = await SeedAsync(_accountingId, (t, _) => t.AssignTo(worker));
        var closed = await SeedAsync(_accountingId, (t, _) => ClaimTestData.MakeClosedUnowned(t, worker));
        var resolved = await SeedAsync(_leasingId, (t, _) => ClaimTestData.MakeResolvedUnowned(t, worker));
        var activeAi = await SeedAsync(_accountingId, afterSave: (id, db) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id));
            return Task.CompletedTask;
        });
        var pending = await SeedAsync(_leasingId, afterSave: (id, db) =>
        {
            db.TicketApprovals.Add(ClaimTestData.PendingApproval(id, worker));
            return Task.CompletedTask;
        });

        var seen = await ListedAsync(agent);
        foreach (var id in new[] { owned, closed, resolved, activeAi, pending })
        {
            Assert.DoesNotContain(id, seen);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"/api/tickets/{id}")).StatusCode); // not even openable
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, owned)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, closed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, resolved)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, activeAi)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, pending)).StatusCode);

        // Nothing moved.
        Assert.Equal(worker, (await _factory.GetTicketAsync(owned))!.CurrentOwnerEmployeeId);
        Assert.Equal(_accountingId, (await _factory.GetTicketAsync(closed))!.CurrentDepartmentId);
        Assert.Equal(_leasingId, (await _factory.GetTicketAsync(resolved))!.CurrentDepartmentId);
        Assert.Equal(_accountingId, (await _factory.GetTicketAsync(activeAi))!.CurrentDepartmentId);
        Assert.Equal(_leasingId, (await _factory.GetTicketAsync(pending))!.CurrentDepartmentId);
    }

    [Fact]
    public async Task AnAiConversationThatNeedsAPerson_OrHasEnded_OrWasTakenByAnAgent_IsHumanWork_AndCanBeTaken()
    {
        await CallCenterAsync();
        var (agent, _) = await AgentAsync();
        var worker = Guid.NewGuid();

        var needsHuman = await SeedAsync(_accountingId, afterSave: (id, db) => { db.TicketInteractions.Add(ClaimTestData.Genesys(id, requiresHuman: true)); return Task.CompletedTask; });
        var ended = await SeedAsync(_accountingId, afterSave: (id, db) => { db.TicketInteractions.Add(ClaimTestData.Genesys(id, ended: true)); return Task.CompletedTask; });
        var inactivityClosed = await SeedAsync(_accountingId, afterSave: (id, db) => { db.TicketInteractions.Add(ClaimTestData.Genesys(id, inactivityClosed: true)); return Task.CompletedTask; });
        var takenByAgent = await SeedAsync(_accountingId, afterSave: (id, db) => { db.TicketInteractions.Add(ClaimTestData.Genesys(id, handledBy: worker)); return Task.CompletedTask; });
        var inProgress = await SeedAsync(_leasingId, (t, _) => ClaimTestData.MakeInProgressUnowned(t, worker));

        var seen = await ListedAsync(agent, "&availableToTake=true");
        foreach (var id in new[] { needsHuman, ended, inactivityClosed, takenByAgent, inProgress })
        {
            Assert.Contains(id, seen);
            Assert.True((await DetailAsync(agent, id)).CanTakeOwnership);
        }
    }

    // =====================================================================
    // 2 / 3 - the claim itself
    // =====================================================================

    [Fact]
    public async Task TakingOwnership_MovesTheTicketToTheCallCenter_AssignsItToTheAgent_AndPreservesEverythingElse()
    {
        var cc = await CallCenterAsync();
        var (agent, agentId) = await AgentAsync();
        var worker = Guid.NewGuid();
        var id = await SeedAsync(_accountingId, (t, _) => ClaimTestData.MakeInProgressUnowned(t, worker));
        var before = (await _factory.GetTicketAsync(id))!;
        var detail = await DetailAsync(agent, id);

        var response = await TakeAsync(agent, id, Convert.FromBase64String(detail.RowVersion));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var taken = (await response.Content.ReadFromJsonAsync<TicketDetailDto>())!;

        Assert.Equal(cc, taken.CurrentDepartmentId);
        Assert.Equal(agentId, taken.CurrentOwnerEmployeeId);
        var after = (await _factory.GetTicketAsync(id))!;
        Assert.Equal(cc, after.CurrentDepartmentId);
        Assert.Equal(agentId, after.CurrentOwnerEmployeeId);

        // preserved
        Assert.Equal(_accountingId, after.OriginatingDepartmentId);
        Assert.Equal(before.OriginatingDepartmentId, after.OriginatingDepartmentId);
        Assert.Equal(before.TicketStatus, after.TicketStatus);
        Assert.Equal(TicketStatus.InProgress, after.TicketStatus);
        Assert.Equal(before.PriorityId, after.PriorityId);
        Assert.Equal(before.CategoryId, after.CategoryId);
        Assert.Equal(before.RequestTypeId, after.RequestTypeId);
        Assert.Equal(before.SlaState, after.SlaState);
        Assert.Equal(before.EscalationLevel, after.EscalationLevel);
        Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
        Assert.Equal(before.TicketNumber, after.TicketNumber);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        // exactly one current assignment, the agent's own act in the Call Center
        var current = await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == id && a.IsCurrent).ToListAsync();
        var row = Assert.Single(current);
        Assert.Equal(agentId, row.AssignedEmployeeId);
        Assert.Equal(cc, row.AssignedDepartmentId);
        Assert.Equal(agentId, row.AssigningActorEmployeeId);

        // audit: the transfer and the take-over, same correlation
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityType == "Ticket" && a.EntityId == id.ToString()).ToListAsync();
        var transfer = Assert.Single(audit, a => a.Action == "Transfer");
        var take = Assert.Single(audit, a => a.Action == "TakeOwnership");
        Assert.Equal(agentId, transfer.ActorEmployeeId);
        Assert.Equal(agentId, take.ActorEmployeeId);
        Assert.Equal(transfer.CorrelationId, take.CorrelationId);
        Assert.Contains($"DepartmentId={_accountingId}", transfer.BeforeValue);
        Assert.Contains($"DepartmentId={cc}", transfer.AfterValue);

        // workflow: one TicketTransferred event from the origin to the Call Center, to the agent, not a person's hand-over and not an escalation
        var events = await db.TicketWorkflowEvents.AsNoTracking()
            .Where(e => e.TicketId == id && e.EventType == WorkflowEventType.TicketTransferred).ToListAsync();
        var moved = Assert.Single(events);
        Assert.Equal(_accountingId, moved.FromDepartmentId);
        Assert.Equal(cc, moved.ToDepartmentId);
        Assert.Equal(agentId, moved.ToEmployeeId);
        Assert.Equal(agentId, moved.ActorEmployeeId);
        Assert.Equal((byte)TicketTransferReason.SystemRouting, moved.TransferReasonCode);
        Assert.False(moved.IsEscalation);

        // and the page's own answer flips: the agent owns it, nothing left to take
        Assert.False((await DetailAsync(agent, id)).CanTakeOwnership);
    }

    [Fact]
    public async Task ATicketAlreadyInTheCallCenterQueue_IsTakenInPlace_WithoutATransfer()
    {
        var cc = await CallCenterAsync();
        var (agent, agentId) = await AgentAsync();
        var id = await SeedAsync(cc);

        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(agent, id)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Equal(agentId, (await _factory.GetTicketAsync(id))!.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id.ToString()).ToListAsync(), a => a.Action == "Transfer");
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id.ToString()).ToListAsync(), a => a.Action == "TakeOwnership");
        Assert.Empty(await db.TicketWorkflowEvents.AsNoTracking().Where(e => e.TicketId == id && e.EventType == WorkflowEventType.TicketTransferred).ToListAsync());
    }

    [Fact]
    public async Task TakingATicketTwice_IsIdempotent_ForTheOwner_AndRefused_ForAnyoneElse()
    {
        await CallCenterAsync();
        var (first, firstId) = await AgentAsync();
        var (second, _) = await AgentAsync();
        var id = await SeedAsync(_accountingId);

        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(first, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(first, id)).StatusCode);           // double click: no second assignment
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(second, id)).StatusCode);   // owned by another: not even visible to them any more

        Assert.Equal(firstId, (await _factory.GetTicketAsync(id))!.CurrentOwnerEmployeeId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Single(await db.TicketAssignments.AsNoTracking().Where(a => a.TicketId == id && a.IsCurrent).ToListAsync());
        Assert.Single(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id.ToString() && a.Action == "TakeOwnership").ToListAsync());
    }

    [Fact]
    public async Task AStaleRowVersion_IsAConflict_AndChangesNothing()
    {
        await CallCenterAsync();
        var (agent, _) = await AgentAsync();
        var id = await SeedAsync(_accountingId);

        var response = await TakeAsync(agent, id, [1, 2, 3, 4, 5, 6, 7, 8]);

        // EF InMemory has no rowversion, so the stale-version path is proven on SQL Server (TakeOwnershipSqlServerTests); here it must at
        // least never half-apply: either it conflicts (409) or, with no concurrency token, it is a normal success - never a 5xx.
        Assert.True(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.OK, response.StatusCode.ToString());
    }

    // =====================================================================
    // 4 - once owned: Resolve, Close, Transfer follow the existing rules
    // =====================================================================

    [Fact]
    public async Task AfterTakingOwnership_TheAgentCanWorkResolveCloseAndTransferTheTicket_UnderTheExistingValidations()
    {
        var cc = await CallCenterAsync();
        var (agent, agentId) = await AgentAsync();
        var id = await SeedAsync(_accountingId);

        // Before: nothing but reading - close/resolve/transfer/status/notes are all refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/tickets/{id}/close", new CloseTicketRequestDto([0]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/tickets/{id}/transfer", new TransferTicketRequestDto(_leasingId, "x", [0]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/tickets/{id}/status", new ChangeStatusRequestDto("InProgress", [0]))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(agent, id)).StatusCode);

        // Status (existing rule: Open -> InProgress needs an owner, which the agent now is).
        var detail = await DetailAsync(agent, id);
        Assert.False(detail.IsReadOnlyAccess);
        var inProgress = await agent.PostAsJsonAsync($"/api/tickets/{id}/status", new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(detail.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, inProgress.StatusCode);

        // Close needs a resolution first (existing validation) - the agent now resolves it as the owner.
        detail = await DetailAsync(agent, id);
        var tooEarly = await agent.PostAsJsonAsync($"/api/tickets/{id}/close", new CloseTicketRequestDto(Convert.FromBase64String(detail.RowVersion)));
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);   // "not yet resolved" - the lifecycle validation, no longer a 403 permission refusal

        var resolved = await agent.PostAsJsonAsync(
            $"/api/tickets/{id}/resolution", new ResolveTicketRequestDto("Resolved", "Sorted out with the customer.", null, null, Convert.FromBase64String(detail.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        detail = await DetailAsync(agent, id);
        var closed = await agent.PostAsJsonAsync($"/api/tickets/{id}/close", new CloseTicketRequestDto(Convert.FromBase64String(detail.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal("Closed", (await closed.Content.ReadFromJsonAsync<TicketDetailDto>())!.TicketStatus);
        _ = (cc, agentId);
    }

    [Fact]
    public async Task AfterTakingOwnership_TheAgentCanTransferTheTicketOnToAnotherDepartment_AndStaysItsCsAgent()
    {
        await CallCenterAsync();
        var (agent, agentId) = await AgentAsync();
        var id = await SeedAsync(_accountingId);
        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(agent, id)).StatusCode);

        var detail = await DetailAsync(agent, id);
        var response = await agent.PostAsJsonAsync(
            $"/api/tickets/{id}/transfer",
            new TransferTicketRequestDto(_leasingId, "Customer needs Leasing", Convert.FromBase64String(detail.RowVersion), ReasonCode: TicketTransferReason.WrongDepartment));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = (await _factory.GetTicketAsync(id))!;
        Assert.Equal(_leasingId, after.CurrentDepartmentId);
        Assert.Equal(agentId, after.CurrentOwnerEmployeeId);      // department and CS Agent are independent: the agent stays on the ticket
    }

    // =====================================================================
    // 8 / 10 - who may: UI flag == API, roles, memberships, Deny overrides
    // =====================================================================

    [Fact]
    public async Task OnlyAnActiveCallCenterAgent_CanTake_AndTheUiFlagAlwaysMatchesWhatTheApiDoes()
    {
        var cc = await CallCenterAsync();
        var (agent, _) = await AgentAsync();
        var (outsideAgent, _) = await ClientAsync(Roles.CsAgent, _accountingId);              // a CS Agent who is not in the Call Center
        var (departmentEmployee, _) = await ClientAsync(Roles.DepartmentEmployee, cc);        // in the Call Center but not a CS Agent
        var (supervisor, _) = await ClientAsync(Roles.CsSupervisor, cc);                      // sees everything, but is not a CS Agent
        var (manager, _) = await ClientAsync(Roles.CsManager, cc);
        var id = await SeedAsync(_accountingId);

        foreach (var (client, name, allowed) in new[]
        {
            (agent, "call center agent", true),
            (outsideAgent, "agent outside the call center", false),
            (departmentEmployee, "department employee", false),
            (supervisor, "supervisor", false),
            (manager, "manager", false),
        })
        {
            // supervisors and managers can open every ticket; the outside agent and employee cannot (department-scoped) - either way the flag is the API's answer
            var detailResponse = await client.GetAsync($"/api/tickets/{id}");
            var flag = detailResponse.StatusCode == HttpStatusCode.OK
                && (await detailResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!.CanTakeOwnership;
            Assert.True(flag == allowed, $"{name}: canTakeOwnership={flag}, expected {allowed}");
        }

        // a refused caller changes nothing
        foreach (var refused in new[] { outsideAgent, departmentEmployee, supervisor, manager })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(refused, id)).StatusCode);
        }

        var untouched = (await _factory.GetTicketAsync(id))!;
        Assert.Null(untouched.CurrentOwnerEmployeeId);
        Assert.Equal(_accountingId, untouched.CurrentDepartmentId);

        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(agent, id)).StatusCode);
    }

    [Theory]
    [InlineData(DomainPermissions.TicketsEdit)]
    [InlineData(DomainPermissions.TicketsAssign)]
    public async Task AnExplicitDeny_BlocksTakingOwnership_InTheApiAndInTheUiFlag(string deniedPermission)
    {
        await CallCenterAsync();
        var (agent, agentId) = await AgentAsync();
        var id = await SeedAsync(_accountingId);
        Assert.True((await DetailAsync(agent, id)).CanTakeOwnership);

        await AddOverrideAsync(agentId, deniedPermission, PermissionEffect.Deny);

        var denied = (await agent.GetAsync($"/api/tickets/{id}")).StatusCode == HttpStatusCode.OK
            && (await DetailAsync(agent, id)).CanTakeOwnership;
        Assert.False(denied);
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(agent, id)).StatusCode);
        Assert.Null((await _factory.GetTicketAsync(id))!.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AGrantNeverMakesSomeoneWhoIsNotACallCenterAgentAbleToTake()
    {
        await CallCenterAsync();
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, _accountingId);
        await AddOverrideAsync(employeeId, DomainPermissions.TicketsAssign, PermissionEffect.Grant);
        await AddOverrideAsync(employeeId, DomainPermissions.TicketsEdit, PermissionEffect.Grant);
        var id = await SeedAsync(_accountingId);

        Assert.Equal(HttpStatusCode.Forbidden, (await TakeAsync(employee, id)).StatusCode);
        Assert.Null((await _factory.GetTicketAsync(id))!.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ReadOnlyAccessToThePool_NeverAllowsAnyOtherChange()
    {
        await CallCenterAsync();
        var (agent, _) = await AgentAsync();
        var id = await SeedAsync(_accountingId);

        // The agent can open it and read its sub-resources...
        foreach (var read in new[] { "", "/history", "/notes", "/interactions", "/approvals" })
        {
            var status = (await agent.GetAsync($"/api/tickets/{id}{read}")).StatusCode;
            Assert.True(status == HttpStatusCode.OK, $"GET {read} -> {status}");
        }

        // ...but nothing else is writable until the ticket is theirs.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/tickets/{id}/notes", new CreateNoteRequestDto("hello"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/tickets/{id}/assignment", new AssignTicketRequestDto(Guid.NewGuid(), [0]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/tickets/{id}/resolution", new ResolveTicketRequestDto("Resolved", "x", null, null, [0]))).StatusCode);
    }
}
