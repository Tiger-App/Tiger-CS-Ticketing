using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Notifications.Integration;
using P = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Permissions;

/// <summary>
/// Tickets.Resolve / Tickets.Close / Tickets.Transfer through the real pipeline.
/// Deny always blocks (assignee and System Administrator included). Grant stands in for the
/// role requirement only - department scope, ownership, visibility and status rules still apply.
/// The assigned employee resolves and closes their own ticket unless explicitly denied.
/// </summary>
public class TicketPermissionRulesTests : LifecycleIntegrationTestBase, IClassFixture<TigerCsApiFactory>
{
    public TicketPermissionRulesTests(TigerCsApiFactory factory) : base(factory) { }

    private sealed record Scenario(
        TicketResponseDto Ticket, int DepartmentId, HttpClient Worker, Guid WorkerId, HttpClient Manager, Guid ManagerId, string RowVersion);

    /// <summary>A ticket in a fresh department, assigned to a Department Employee of that department and moved to InProgress.</summary>
    private async Task<Scenario> AssignedTicketAsync()
    {
        var (agent, _) = await CreateClientAsync(Roles.CsAgent);
        var (manager, managerId) = await CreateClientAsync(Roles.CsManager);
        var (worker, workerId) = await CreateClientAsync(Roles.DepartmentEmployee);
        var (ticket, departmentId) = await CreateTicketAsync(agent);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);

        var assigned = await manager.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/assignment",
            new AssignTicketRequestDto(workerId, Convert.FromBase64String(ticket.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var afterAssign = (await assigned.Content.ReadFromJsonAsync<TicketDetailDto>())!;
        var status = await worker.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/status",
            new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var afterStatus = (await status.Content.ReadFromJsonAsync<TicketDetailDto>())!;
        return new Scenario(ticket, departmentId, worker, workerId, manager, managerId, afterStatus.RowVersion);
    }

    private async Task SetOverrideAsync(Guid employeeId, string key, PermissionEffect? effect)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.UserPermissionOverrides.RemoveRange(db.UserPermissionOverrides.Where(o => o.EmployeeId == employeeId && o.PermissionKey == key));
        if (effect is { } e)
        {
            db.UserPermissionOverrides.Add(new UserPermissionOverride(employeeId, key, e, null, DateTime.UtcNow));
        }

        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client, long ticketId, string rowVersion) =>
        client.PostAsJsonAsync($"/api/tickets/{ticketId}/resolution",
            new ResolveTicketRequestDto("Resolved", "Replaced the part.", null, null, Convert.FromBase64String(rowVersion)));

    private static Task<HttpResponseMessage> CloseAsync(HttpClient client, long ticketId, string rowVersion) =>
        client.PostAsJsonAsync($"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(rowVersion)));

    private static async Task<string> RowVersionAsync(HttpClient client, long ticketId) =>
        (await client.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{ticketId}"))!.RowVersion;

    // ---- Resolve ----

    [Fact]
    public async Task The_assignee_resolves_their_own_ticket_unless_resolve_is_denied()
    {
        var s = await AssignedTicketAsync();

        await SetOverrideAsync(s.WorkerId, P.TicketsResolve, PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(s.Worker, s.Ticket.TicketId, s.RowVersion)).StatusCode);

        await SetOverrideAsync(s.WorkerId, P.TicketsResolve, null);
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(s.Worker, s.Ticket.TicketId, s.RowVersion)).StatusCode);
    }

    [Fact]
    public async Task Denying_resolve_blocks_even_the_system_administrator()
    {
        var s = await AssignedTicketAsync();
        var (admin, adminId) = await CreateClientAsync(Roles.SystemAdministrator);
        await SetOverrideAsync(adminId, P.TicketsResolve, PermissionEffect.Deny);

        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(admin, s.Ticket.TicketId, s.RowVersion)).StatusCode);
    }

    [Fact]
    public async Task A_resolve_grant_satisfies_the_role_requirement_but_not_department_scope()
    {
        var s = await AssignedTicketAsync();
        var (member, memberId) = await CreateClientAsync(Roles.CsSupervisor);
        await _factory.AssignPrimaryDepartmentAsync(memberId, s.DepartmentId);
        var otherDepartment = await _factory.CreateDepartmentAsync("Elsewhere " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (outsider, outsiderId) = await CreateClientAsync(Roles.CsSupervisor);
        await _factory.AssignPrimaryDepartmentAsync(outsiderId, otherDepartment);

        // No grant: a CS Supervisor holds no Resolve right.
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(member, s.Ticket.TicketId, s.RowVersion)).StatusCode);

        await SetOverrideAsync(memberId, P.TicketsResolve, PermissionEffect.Grant);
        await SetOverrideAsync(outsiderId, P.TicketsResolve, PermissionEffect.Grant);

        // The grant does not reach a ticket outside the holder's own department...
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(outsider, s.Ticket.TicketId, s.RowVersion)).StatusCode);
        // ...but works inside it.
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(member, s.Ticket.TicketId, s.RowVersion)).StatusCode);
    }

    // ---- Close ----

    [Fact]
    public async Task The_assignee_closes_their_own_ticket_unless_close_is_denied()
    {
        var s = await AssignedTicketAsync();
        var resolved = await ResolveAsync(s.Worker, s.Ticket.TicketId, s.RowVersion);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var version = (await resolved.Content.ReadFromJsonAsync<TicketDetailDto>())!.RowVersion;

        await SetOverrideAsync(s.WorkerId, P.TicketsClose, PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(s.Worker, s.Ticket.TicketId, version)).StatusCode);

        await SetOverrideAsync(s.WorkerId, P.TicketsClose, null);
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(s.Worker, s.Ticket.TicketId, version)).StatusCode);
    }

    [Fact]
    public async Task A_close_grant_never_widens_visibility_to_a_colleagues_ticket_inside_or_outside_the_department()
    {
        var s = await AssignedTicketAsync();
        var resolved = await ResolveAsync(s.Worker, s.Ticket.TicketId, s.RowVersion);
        var version = (await resolved.Content.ReadFromJsonAsync<TicketDetailDto>())!.RowVersion;

        var (colleague, colleagueId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(colleagueId, s.DepartmentId);
        var otherDepartment = await _factory.CreateDepartmentAsync("Elsewhere " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (outsider, outsiderId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(outsiderId, otherDepartment);

        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(colleague, s.Ticket.TicketId, version)).StatusCode);

        await SetOverrideAsync(colleagueId, P.TicketsClose, PermissionEffect.Grant);
        await SetOverrideAsync(outsiderId, P.TicketsClose, PermissionEffect.Grant);

        // Employees see only tickets assigned to them: a permission grant is an action right, never a visibility grant, so it is
        // inert on a colleague's ticket whether the colleague shares the department or not.
        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(outsider, s.Ticket.TicketId, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(colleague, s.Ticket.TicketId, version)).StatusCode);

        // The assignee (once the grant exists) can still close their own ticket.
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(s.Worker, s.Ticket.TicketId, version)).StatusCode);
    }

    // ---- Transfer ----

    [Fact]
    public async Task Transfer_is_blocked_by_a_denial_and_a_grant_reaches_only_a_ticket_assigned_to_the_holder()
    {
        var s = await AssignedTicketAsync();
        var target = await _factory.CreateDepartmentAsync("Target " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        TransferTicketRequestDto Body(string version) => new(target, "Needs another team", Convert.FromBase64String(version));

        // Deny beats the CS Manager role.
        await SetOverrideAsync(s.ManagerId, P.TicketsTransfer, PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await s.Manager.PostAsJsonAsync($"/api/tickets/{s.Ticket.TicketId}/transfer", Body(s.RowVersion))).StatusCode);

        // A grant for a department employee outside the ticket's department does not reach the ticket (visibility is not widened).
        var otherDepartment = await _factory.CreateDepartmentAsync("Elsewhere " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (outsider, outsiderId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(outsiderId, otherDepartment);
        await SetOverrideAsync(outsiderId, P.TicketsTransfer, PermissionEffect.Grant);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync($"/api/tickets/{s.Ticket.TicketId}/transfer", Body(s.RowVersion))).StatusCode);

        // A colleague in the ticket's department who is not its assignee is refused even WITH the grant (a grant never widens visibility).
        var (member, memberId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(memberId, s.DepartmentId);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync($"/api/tickets/{s.Ticket.TicketId}/transfer", Body(s.RowVersion))).StatusCode);
        await SetOverrideAsync(memberId, P.TicketsTransfer, PermissionEffect.Grant);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync($"/api/tickets/{s.Ticket.TicketId}/transfer", Body(s.RowVersion))).StatusCode);

        // The assignee with the grant may transfer; without it they may not.
        var (assignee, assigneeId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(assigneeId, s.DepartmentId);
        await _factory.AssignOwnerAsync(s.Ticket.TicketId, assigneeId);
        var current = (await assignee.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{s.Ticket.TicketId}"))!.RowVersion;
        Assert.Equal(HttpStatusCode.Forbidden,
            (await assignee.PostAsJsonAsync($"/api/tickets/{s.Ticket.TicketId}/transfer", Body(current))).StatusCode);
        await SetOverrideAsync(assigneeId, P.TicketsTransfer, PermissionEffect.Grant);
        Assert.Equal(HttpStatusCode.OK,
            (await assignee.PostAsJsonAsync($"/api/tickets/{s.Ticket.TicketId}/transfer", Body(current))).StatusCode);
    }

    // ---- My Tickets ----

    [Fact]
    public async Task My_tickets_lists_a_ticket_transferred_in_and_assigned_to_the_employee()
    {
        var s = await AssignedTicketAsync();
        var target = await _factory.CreateDepartmentAsync("Receiving " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var (receiver, receiverId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(receiverId, target);

        var transfer = await s.Manager.PostAsJsonAsync($"/api/tickets/{s.Ticket.TicketId}/transfer",
            new TransferTicketRequestDto(target, "Handing over", Convert.FromBase64String(s.RowVersion), receiverId));
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);

        var mine = await receiver.GetFromJsonAsync<TicketListResultDto>($"/api/tickets?ownerEmployeeId={receiverId}");
        Assert.Contains(mine!.Items, t => t.TicketId == s.Ticket.TicketId);
        // ...and it left the previous assignee's list.
        var previous = await s.Worker.GetFromJsonAsync<TicketListResultDto>($"/api/tickets?ownerEmployeeId={s.WorkerId}");
        Assert.DoesNotContain(previous!.Items, t => t.TicketId == s.Ticket.TicketId);
    }

    [Fact]
    public async Task My_tickets_still_lists_a_ticket_assigned_to_the_employee_outside_their_department_scope()
    {
        var s = await AssignedTicketAsync();
        // The assignee's membership of the ticket's department ends (or they are re-homed): they still own the ticket.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            db.UserDepartmentAssignments.RemoveRange(db.UserDepartmentAssignments.Where(a => a.EmployeeId == s.WorkerId));
            await db.SaveChangesAsync();
        }

        var mine = await s.Worker.GetFromJsonAsync<TicketListResultDto>($"/api/tickets?ownerEmployeeId={s.WorkerId}");
        Assert.Contains(mine!.Items, t => t.TicketId == s.Ticket.TicketId);
        Assert.Equal(HttpStatusCode.OK, (await s.Worker.GetAsync($"/api/tickets/{s.Ticket.TicketId}")).StatusCode);

        // Visibility is not widened to anyone else's tickets: another department employee sees nothing of it.
        var (stranger, strangerId) = await CreateClientAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(strangerId, await _factory.CreateDepartmentAsync("Other " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]));
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/tickets/{s.Ticket.TicketId}")).StatusCode);
    }

    [Fact]
    public async Task A_restricted_cs_agent_assigned_a_ticket_sees_it_in_my_tickets()
    {
        var (agent, agentId) = await CreateClientAsync(Roles.CsAgent);
        var (manager, _) = await CreateClientAsync(Roles.CsManager);
        var (creator, _) = await CreateClientAsync(Roles.CsAgent);
        var (ticket, departmentId) = await CreateTicketAsync(creator);
        await _factory.AssignPrimaryDepartmentAsync(agentId, departmentId);

        var assigned = await manager.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/assignment",
            new AssignTicketRequestDto(agentId, Convert.FromBase64String(ticket.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        var mine = await agent.GetFromJsonAsync<TicketListResultDto>($"/api/tickets?ownerEmployeeId={agentId}");
        Assert.Contains(mine!.Items, t => t.TicketId == ticket.TicketId);
    }

    // ---- /me feeds the UI ----

    [Fact]
    public async Task Me_reports_the_employees_grants_and_denials_for_the_ui()
    {
        var (client, id) = await CreateClientAsync(Roles.CsAgent);
        await SetOverrideAsync(id, P.TicketsReopen, PermissionEffect.Deny);
        await SetOverrideAsync(id, P.ReportsView, PermissionEffect.Grant);

        var me = await client.GetFromJsonAsync<CurrentUserResponseDto>("/api/users/me");

        Assert.Contains(P.TicketsReopen, me!.DeniedPermissions!);
        Assert.DoesNotContain(P.TicketsReopen, me.EffectivePermissions!);
        Assert.Contains(P.ReportsView, me.GrantedPermissions!);
        Assert.Contains(P.ReportsView, me.EffectivePermissions!);
        Assert.Contains(P.TicketsCreate, me.EffectivePermissions!);
    }

    [Fact]
    public async Task A_reopen_denial_blocks_the_cs_layer_reopen_endpoint()
    {
        var (agent, agentId) = await CreateClientAsync(Roles.CsAgent);
        await SetOverrideAsync(agentId, P.TicketsReopen, PermissionEffect.Deny);

        var response = await agent.PostAsJsonAsync("/api/tickets/1/reopen",
            new ReopenTicketRequestDto("Customer called back", 1, [1]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
