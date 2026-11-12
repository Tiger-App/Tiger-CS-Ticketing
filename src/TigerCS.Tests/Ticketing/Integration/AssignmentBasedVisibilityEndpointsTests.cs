using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.TakeOwnership;

using DomainPermissions = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Assignment-based ticket visibility through the REAL HTTP pipeline (DI, the ticket-access filter, services, repositories).
///
/// <para>
/// A regular <b>Department Employee</b> sees ONLY the tickets assigned to them - in any department - on every surface: the Queue / My
/// Tickets / Closed / search lists, the totals, the dashboard counts, Ticket Details and every endpoint under <c>api/tickets/{ticketId}</c>
/// (activity, notes, interactions, customer history/profile, approvals, SLA, workflow actions). Department membership shows them nothing,
/// and a colleague's ticket is refused by id before any action runs. CS Manager and System Administrator see everything; Department Head,
/// CS Supervisor and Reporting User keep their existing scope. Ownership is evaluated per request from the ticket's current data, so
/// reassignment and transfer take effect immediately.
/// </para>
///
/// <para>
/// The CS intake / classification queue: an eligible Call Center CS Agent also sees (read-only until they take ownership) the
/// <b>unassigned, Genesys-originated</b> tickets in their default Queue - and nothing else unassigned - while employees, ineligible
/// agents and unrelated workflows' unassigned tickets stay hidden. (The SQL translation of the same predicate is proven on SQLite in
/// <c>GenesysIntakeSqliteTests</c>.)
/// </para>
/// </summary>
public sealed class AssignmentBasedVisibilityEndpointsTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;
    private static int _counter;

    private sealed record Departments(int CallCenter, int Finance, int Facilities);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TigerCsApiFactory, Task<Departments>> DepartmentsByFactory = new();

    /// <summary>One set of departments per shared factory (a second department coded CC would break the Call Center lookup).</summary>
    private Task<Departments> DepartmentsAsync() => DepartmentsByFactory.GetValue(_factory, async f =>
    {
        await f.SeedPrioritiesAsync();
        return new Departments(
            await f.CreateDepartmentAsync("Call Center", "CC"),
            await f.CreateDepartmentAsync("Finance AV", "FINAV"),
            await f.CreateDepartmentAsync("Facilities AV", "FACAV"));
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

    /// <summary>An open, unclassified ticket in <paramref name="departmentId"/>, optionally assigned (with its current assignment row) and shaped.</summary>
    private async Task<long> TicketAsync(
        int departmentId, Guid? assignedTo = null, string summary = "AC not cooling", Action<Ticket>? shape = null,
        Func<long, TigerCsDbContext, Task>? afterSave = null)
    {
        long id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var ticket = ClaimTestData.Open($"TG-AV-{Interlocked.Increment(ref _counter):D6}-{Guid.NewGuid():N}"[..28], departmentId);
            ticket.GetType().GetProperty(nameof(Ticket.RequestSummary))!.SetValue(ticket, summary);
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

    private static long[] Ids(params long[] ids) => [.. ids.Order()];

    // =====================================================================
    // A regular employee: own tickets only, in every department
    // =====================================================================

    [Fact]
    public async Task AnEmployee_SeesTheirOwnTicket_InTheListDetailSearchAndTotals()
    {
        var d = await DepartmentsAsync();
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var own = await TicketAsync(d.Facilities, employeeId, summary: "Leak in unit 12 av-own");

        var list = await ListAsync(employee);
        Assert.Equal([own], list.Items.Select(i => i.TicketId));
        Assert.Equal(1, list.TotalCount);

        Assert.Equal(own, (await DetailAsync(employee, own)).TicketId);
        Assert.Equal([own], await IdsAsync(employee, "&search=av-own"));
        Assert.Equal([own], await IdsAsync(employee, $"&ownerEmployeeId={employeeId}"));   // My Tickets
    }

    [Fact]
    public async Task AnEmployee_SeesTheirOwnTicket_EvenWhenItBelongsToAnotherDepartment()
    {
        var d = await DepartmentsAsync();
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var inAnotherDepartment = await TicketAsync(d.Finance, employeeId);   // assigned to them, but sits in Finance (they are in Facilities)

        Assert.Contains(inAnotherDepartment, await IdsAsync(employee));
        Assert.Contains(inAnotherDepartment, await IdsAsync(employee, $"&ownerEmployeeId={employeeId}"));
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(employee, inAnotherDepartment));
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync($"/api/tickets/{inAnotherDepartment}/notes")).StatusCode);
    }

    [Fact]
    public async Task AnEmployee_NeverSees_ASameDepartmentColleaguesTicket_OrTheDepartmentsUnassignedQueue()
    {
        var d = await DepartmentsAsync();
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var (_, colleagueId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var own = await TicketAsync(d.Facilities, employeeId, "shared summary av-colleague");
        var colleagues = await TicketAsync(d.Facilities, colleagueId, "shared summary av-colleague");
        var unassigned = await TicketAsync(d.Facilities, summary: "shared summary av-colleague");
        var (_, financeOwnerId) = await ClientAsync(Roles.DepartmentEmployee, d.Finance);
        var otherDepartment = await TicketAsync(d.Finance, financeOwnerId, "shared summary av-colleague");

        // Queue, My Tickets, search and the totals all stop at the employee's own ticket.
        var queue = await ListAsync(employee);
        Assert.Equal([own], queue.Items.Select(i => i.TicketId));
        Assert.Equal(1, queue.TotalCount);
        Assert.Equal([own], await IdsAsync(employee, "&search=av-colleague"));
        Assert.Empty(await IdsAsync(employee, "&inDepartmentQueue=true"));                    // the department queue is not theirs to see
        Assert.Empty(await IdsAsync(employee, $"&departmentId={d.Facilities}&ownerEmployeeId={colleagueId}"));   // a filter can only narrow
        Assert.Equal([own], await IdsAsync(employee, $"&departmentId={d.Facilities}"));

        foreach (var hidden in new[] { colleagues, unassigned, otherDepartment })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(employee, hidden));
        }
    }

    [Fact]
    public async Task DirectAccessByTicketId_IsRefusedOnEveryTicketEndpoint_ForReadsAndWrites_BeforeAnythingRuns()
    {
        var d = await DepartmentsAsync();
        var (employee, _) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var (_, colleagueId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var colleagues = await TicketAsync(d.Facilities, colleagueId);
        var unassigned = await TicketAsync(d.Facilities);
        var version = Convert.FromBase64String((await DetailAsync(await ManagerClientAsync(), colleagues)).RowVersion);

        foreach (var id in new[] { colleagues, unassigned })
        {
            foreach (var read in new[]
            {
                "", "/history", "/activity-timeline", "/interactions", "/notes", "/customer-history", "/customer-profile", "/assignment-history",
                "/reviews", "/approvals", "/sla", "/sla/priority-downgrade-requests", "/collections-transfer-options"
            })
            {
                var status = (await employee.GetAsync($"/api/tickets/{id}{read}")).StatusCode;
                Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"GET /api/tickets/{id}{read} answered {(int)status}");
                Assert.NotEqual(HttpStatusCode.OK, status);
            }

            var writes = new (string Path, object Body)[]
            {
                ("/notes", new CreateNoteRequestDto("Should not be allowed.")),
                ("/status", new ChangeStatusRequestDto("InProgress", version)),
                ("/resolution", new ResolveTicketRequestDto("Resolved", "x", null, null, version)),
                ("/close", new CloseTicketRequestDto(version)),
                ("/reopen", new ReopenTicketRequestDto("x", d.Facilities, version)),
                ("/assignment", new AssignTicketRequestDto(colleagueId, version)),
                ("/transfer", new TransferTicketRequestDto(d.Finance, "x", version)),
                ("/approvals", new RequestApprovalRequestDto("ReopenApproval", "x")),
                ("/ownership", new TakeOwnershipRequestDto(version)),
            };
            foreach (var (path, body) in writes)
            {
                var status = (await employee.PostAsJsonAsync($"/api/tickets/{id}{path}", body)).StatusCode;
                Assert.Equal(HttpStatusCode.Forbidden, status);
            }
        }

        // The refusals changed nothing.
        Assert.Equal(colleagueId, (await DetailAsync(await ManagerClientAsync(), colleagues)).CurrentOwnerEmployeeId);
        Assert.Null((await DetailAsync(await ManagerClientAsync(), unassigned)).CurrentOwnerEmployeeId);
    }

    private HttpClient? _manager;

    private async Task<HttpClient> ManagerClientAsync() => _manager ??= (await ClientAsync(Roles.CsManager)).Client;

    [Fact]
    public async Task Counts_FollowTheSameRule_TheDashboardAndTheListTotalsNeverLeakColleaguesTickets()
    {
        var d = await DepartmentsAsync();
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var (_, colleagueId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        await TicketAsync(d.Facilities, employeeId);
        await TicketAsync(d.Finance, employeeId);
        await TicketAsync(d.Facilities, colleagueId);
        await TicketAsync(d.Facilities);

        var summary = (await employee.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard"))!;

        Assert.Equal(2, summary.OpenTickets);
        Assert.Equal(2, summary.MyTickets);
        Assert.Equal(0, summary.Unassigned);   // the department's unassigned queue is not theirs
        Assert.Equal(2, (await ListAsync(employee)).TotalCount);
        Assert.Equal(2, (await ListAsync(employee, "&activeOnly=true")).TotalCount);
    }

    [Fact]
    public async Task TheClosedTab_ListsOnlyTheEmployeesOwnClosedTickets()
    {
        var d = await DepartmentsAsync();
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var (_, colleagueId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var mineClosed = await TicketAsync(d.Facilities, shape: t => ClaimTestData.MakeClosedUnowned(t, employeeId));
        // MakeClosedUnowned releases the owner: give the ticket back to its closer so "closed and assigned to me" is the shape under test.
        await ReleaseToAsync(mineClosed, employeeId);
        var colleaguesClosed = await TicketAsync(d.Facilities, shape: t => ClaimTestData.MakeClosedUnowned(t, colleagueId));
        await ReleaseToAsync(colleaguesClosed, colleagueId);
        var unownedClosed = await TicketAsync(d.Facilities, shape: t => ClaimTestData.MakeClosedUnowned(t, colleagueId));

        Assert.Equal([mineClosed], await IdsAsync(employee, "&ticketStatus=Closed"));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(employee, colleaguesClosed));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(employee, unownedClosed));
        Assert.Equal(1, (await ListAsync(employee, "&ticketStatus=Closed")).TotalCount);
    }

    private async Task ReleaseToAsync(long ticketId, Guid owner)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(db.Tickets, t => t.TicketId == ticketId);
        typeof(Ticket).GetProperty(nameof(Ticket.CurrentOwnerEmployeeId))!.SetValue(ticket, owner);
        await db.SaveChangesAsync();
    }

    // =====================================================================
    // Reassignment and transfer: access follows ownership immediately
    // =====================================================================

    [Fact]
    public async Task Reassigning_MovesAccessImmediately_TheFormerOwnerLosesTheTicket_TheNewOwnerGainsIt()
    {
        var d = await DepartmentsAsync();
        var manager = await ManagerClientAsync();
        var (first, firstId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var (second, secondId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var ticket = await TicketAsync(d.Facilities, firstId);

        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(first, ticket));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(second, ticket));

        var current = await DetailAsync(manager, ticket);
        var reassigned = await manager.PostAsJsonAsync(
            $"/api/tickets/{ticket}/assignment",
            new AssignTicketRequestDto(secondId, Convert.FromBase64String(current.RowVersion), TicketTransferReason.Other));
        Assert.Equal(HttpStatusCode.OK, reassigned.StatusCode);

        // No cache, no session state: the very next request sees the new owner.
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(first, ticket));
        Assert.Equal(HttpStatusCode.Forbidden, (await first.GetAsync($"/api/tickets/{ticket}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync($"/api/tickets/{ticket}/notes", new CreateNoteRequestDto("late"))).StatusCode);
        Assert.DoesNotContain(ticket, await IdsAsync(first));
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(second, ticket));
        Assert.Contains(ticket, await IdsAsync(second, $"&ownerEmployeeId={secondId}"));
    }

    [Fact]
    public async Task Transfer_ToAnotherDepartmentWithAnAssignee_MovesAccessToTheAssignee_AndWithoutOneTheOwnerKeepsIt()
    {
        var d = await DepartmentsAsync();
        var manager = await ManagerClientAsync();
        var (owner, ownerId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var (receiver, receiverId) = await ClientAsync(Roles.DepartmentEmployee, d.Finance);
        var (financeColleague, _) = await ClientAsync(Roles.DepartmentEmployee, d.Finance);
        var ticket = await TicketAsync(d.Facilities, ownerId);

        // Transfer without naming an assignee: the department changes, the owner does not - and still sees it (My Tickets after a transfer);
        // the receiving department's staff do not gain access merely because the ticket now sits in their department.
        var current = await DetailAsync(manager, ticket);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync(
            $"/api/tickets/{ticket}/transfer", new TransferTicketRequestDto(d.Finance, "Belongs to Finance.", Convert.FromBase64String(current.RowVersion)))).StatusCode);
        Assert.Equal(d.Finance, (await DetailAsync(owner, ticket)).CurrentDepartmentId);
        Assert.Contains(ticket, await IdsAsync(owner));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(financeColleague, ticket));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(receiver, ticket));

        // Transfer back to Facilities naming the Finance receiver is not possible (not a member); transfer onward with the receiver assigned.
        var next = await DetailAsync(manager, ticket);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync(
            $"/api/tickets/{ticket}/assignment",
            new AssignTicketRequestDto(receiverId, Convert.FromBase64String(next.RowVersion), TicketTransferReason.Other))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(receiver, ticket));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(owner, ticket));
        Assert.DoesNotContain(ticket, await IdsAsync(owner));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(financeColleague, ticket));
    }

    // =====================================================================
    // Roles that keep their existing scope
    // =====================================================================

    [Theory]
    [InlineData("")]
    [InlineData("&excludeClosed=true")]
    [InlineData("&excludeClosed=true&ticketStatus=Open&priorityId=2&verificationStatus=Verified&sortBy=priority&sortDir=asc&search=AC")]
    [InlineData("&inDepartmentQueue=true&slaBreached=true&unclassified=true&escalated=true&createdByMe=true")]
    [InlineData("&closureCategory=Reviewed&closedFrom=2026-01-01&closedTo=2026-12-31&createdFrom=2026-01-01&createdTo=2026-12-31&channelId=1")]
    public async Task SystemAdministrator_EveryTicketsPageQueryShape_LoadsWithoutError_AcrossDepartments_WithDepartmentAndAssigneeFilters(string extra)
    {
        var d = await DepartmentsAsync();
        var (_, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var assigned = await TicketAsync(d.Facilities, employeeId);
        var other = await TicketAsync(d.Finance);
        var (admin, _) = await ClientAsync(Roles.SystemAdministrator);   // a member of no department

        // The page's own request shape (pageSize 20, page 1) never errors, with or without the new Department / Assigned To filters.
        var plain = await admin.GetAsync("/api/tickets?page=1&pageSize=20" + extra);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);

        var byDepartment = await IdsAsync(admin, $"&departmentId={d.Facilities}");
        Assert.Contains(assigned, byDepartment);
        Assert.DoesNotContain(other, byDepartment);

        var byDepartmentAndOwner = await IdsAsync(admin, $"&departmentId={d.Facilities}&ownerEmployeeId={employeeId}");
        Assert.Equal([assigned], byDepartmentAndOwner);
        Assert.Empty(await IdsAsync(admin, $"&departmentId={d.Finance}&ownerEmployeeId={employeeId}"));
        Assert.Contains(other, await IdsAsync(admin, "&inDepartmentQueue=true"));
    }

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.SystemAdministrator)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.GeneralManager)]
    public async Task ManagementAndAdministratorRoles_SeeEveryTicket_OfEveryDepartment_AssignedOrNot(string role)
    {
        var d = await DepartmentsAsync();
        var (_, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var assigned = await TicketAsync(d.Facilities, employeeId);
        var unassigned = await TicketAsync(d.Finance);
        var (viewer, _) = await ClientAsync(role);   // a member of no department

        var seen = await IdsAsync(viewer);
        Assert.Contains(assigned, seen);
        Assert.Contains(unassigned, seen);
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(viewer, assigned));
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(viewer, unassigned));
    }

    [Fact]
    public async Task ADepartmentHead_KeepsTheirDepartmentView_ButNotAnotherDepartments_AndAReportingUserHasNoTicketAccessBeyondTheirDepartment()
    {
        var d = await DepartmentsAsync();
        var (head, headId) = await ClientAsync(Roles.DepartmentHead, d.Facilities);
        var (_, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var colleagues = await TicketAsync(d.Facilities, employeeId);
        var unassigned = await TicketAsync(d.Facilities);
        var otherDepartment = await TicketAsync(d.Finance);
        var headsOwnElsewhere = await TicketAsync(d.Finance, headId);

        var seen = await IdsAsync(head);
        Assert.Contains(colleagues, seen);          // the approved department-management view is unchanged
        Assert.Contains(unassigned, seen);
        Assert.Contains(headsOwnElsewhere, seen);   // and their own assigned ticket in another department
        Assert.DoesNotContain(otherDepartment, seen);
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(head, otherDepartment));

        var (reporter, _) = await ClientAsync(Roles.ReportingUser);
        Assert.Empty(await IdsAsync(reporter));    // no department, no ticket
    }

    [Fact]
    public async Task APermissionGrant_NeverWidensAnEmployeesVisibility()
    {
        var d = await DepartmentsAsync();
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var (_, colleagueId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var colleagues = await TicketAsync(d.Facilities, colleagueId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            foreach (var permission in new[]
            {
                DomainPermissions.TicketsView,
                DomainPermissions.TicketsEdit,
                DomainPermissions.TicketsAssign
            })
            {
                db.UserPermissionOverrides.Add(new UserPermissionOverride(
                    employeeId, permission, PermissionEffect.Grant, null, DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(employee, colleagues));
        Assert.DoesNotContain(colleagues, await IdsAsync(employee));
    }

    // =====================================================================
    // The CS intake / classification queue for Genesys-created unassigned tickets
    // =====================================================================

    private Task<long> GenesysIntakeTicketAsync(int departmentId, bool requiresHuman = true) =>
        TicketAsync(departmentId, afterSave: (id, db) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id, requiresHuman: requiresHuman));
            return Task.CompletedTask;
        });

    [Fact]
    public async Task AnEligibleCallCenterAgent_SeesUnassignedGenesysTickets_InTheDefaultQueue_ReadOnly_AndNothingElseUnassigned()
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await ClientAsync(Roles.CsAgent, d.CallCenter);
        var genesysUnassigned = await GenesysIntakeTicketAsync(d.CallCenter);
        var genesysUnassignedOtherDepartment = await GenesysIntakeTicketAsync(d.Finance);
        var manualUnassigned = await TicketAsync(d.Finance);                                   // an unrelated workflow's unassigned ticket
        var activeAi = await TicketAsync(d.CallCenter, afterSave: (id, db) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id));                              // the chatbot is still handling it
            return Task.CompletedTask;
        });
        var (_, colleagueAgentId) = await ClientAsync(Roles.CsAgent, d.CallCenter);
        var assignedToColleague = await TicketAsync(d.CallCenter, colleagueAgentId, afterSave: (id, db) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id, requiresHuman: true));
            return Task.CompletedTask;
        });
        var closedGenesys = await TicketAsync(d.CallCenter, shape: t => ClaimTestData.MakeClosedUnowned(t, agentId), afterSave: (id, db) =>
        {
            db.TicketInteractions.Add(ClaimTestData.Genesys(id, requiresHuman: true, ended: true));
            return Task.CompletedTask;
        });

        var queue = await IdsAsync(agent);

        Assert.Contains(genesysUnassigned, queue);
        Assert.Contains(genesysUnassignedOtherDepartment, queue);   // the intake queue is the Genesys-originated unassigned work, wherever it was created
        Assert.DoesNotContain(manualUnassigned, queue);              // unrelated workflow: still hidden
        Assert.DoesNotContain(activeAi, queue);                      // the bot's live conversation is not claimable work
        Assert.DoesNotContain(assignedToColleague, queue);
        Assert.DoesNotContain(closedGenesys, queue);

        // Read-only until taken, and the UI is told the same thing the API enforces.
        var detail = await DetailAsync(agent, genesysUnassigned);
        Assert.True(detail.IsReadOnlyAccess);
        Assert.True(detail.CanTakeOwnership);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/tickets/{genesysUnassigned}/notes", new CreateNoteRequestDto("x"))).StatusCode);

        // Counts and the dashboard follow the list.
        var page = await ListAsync(agent);
        Assert.Equal(page.Items.Count, page.TotalCount);
        var summary = (await agent.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard"))!;
        // (The factory database is shared by every test in this class, so the expectation is derived from the agent's own list.)
        var active = page.Items.Where(i => i.TicketStatus is "Open" or "InProgress" or "PendingCustomer" or "PendingThirdParty").ToList();
        Assert.Equal(active.Count, summary.OpenTickets);
        Assert.Equal(active.Count(i => i.CurrentOwnerEmployeeId is null), summary.Unassigned);
        Assert.True(summary.Unassigned >= 2);   // at least the two Genesys intake tickets this test seeded
        Assert.Equal(0, summary.MyTickets);
    }

    [Fact]
    public async Task OnceAssigned_ATicketLeavesTheIntakeQueue_AndAppearsInTheAssigneesMyTickets_RegardlessOfDepartment()
    {
        var d = await DepartmentsAsync();
        var manager = await ManagerClientAsync();
        var (agent, agentId) = await ClientAsync(Roles.CsAgent, d.CallCenter);
        var (otherAgent, otherAgentId) = await ClientAsync(Roles.CsAgent, d.CallCenter);
        var (employee, employeeId) = await ClientAsync(Roles.DepartmentEmployee, d.Facilities);
        var ticket = await GenesysIntakeTicketAsync(d.CallCenter);

        Assert.Contains(ticket, await IdsAsync(agent));
        Assert.Contains(ticket, await IdsAsync(otherAgent));
        Assert.DoesNotContain(ticket, await IdsAsync(employee));   // an employee never sees the unassigned intake queue

        // Transfer to Facilities and assign the employee in one step (the Call Center's classification hand-over).
        var current = await DetailAsync(manager, ticket);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync(
            $"/api/tickets/{ticket}/transfer",
            new TransferTicketRequestDto(d.Facilities, "Classified: Facilities.", Convert.FromBase64String(current.RowVersion), employeeId))).StatusCode);

        // Assigned to the employee: in their My Tickets whatever its department, gone from every agent's intake queue.
        Assert.Contains(ticket, await IdsAsync(employee, $"&ownerEmployeeId={employeeId}"));
        Assert.Equal(HttpStatusCode.OK, await DetailStatusAsync(employee, ticket));
        Assert.DoesNotContain(ticket, await IdsAsync(agent));
        Assert.DoesNotContain(ticket, await IdsAsync(otherAgent));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(otherAgent, ticket));
        _ = (agentId, otherAgentId);
    }

    [Fact]
    public async Task TakingOwnership_MovesAnIntakeTicketIntoTheAgentsMyTickets_AndOutOfEveryoneElsesQueue()
    {
        var d = await DepartmentsAsync();
        var (agent, agentId) = await ClientAsync(Roles.CsAgent, d.CallCenter);
        var (otherAgent, _) = await ClientAsync(Roles.CsAgent, d.CallCenter);
        var ticket = await GenesysIntakeTicketAsync(d.CallCenter);

        var taken = await agent.PostAsJsonAsync($"/api/tickets/{ticket}/ownership", new TakeOwnershipRequestDto(null));
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);

        Assert.Contains(ticket, await IdsAsync(agent, $"&ownerEmployeeId={agentId}"));
        Assert.False((await DetailAsync(agent, ticket)).IsReadOnlyAccess);
        Assert.DoesNotContain(ticket, await IdsAsync(otherAgent));
        Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(otherAgent, ticket));
    }

    [Fact]
    public async Task TheIntakeQueue_IsNotOfferedToEmployees_ToAgentsOutsideTheCallCenter_OrToAServiceOnlyAgentWithoutClaimRights()
    {
        var d = await DepartmentsAsync();
        var ticket = await GenesysIntakeTicketAsync(d.CallCenter);
        var (employeeInCc, _) = await ClientAsync(Roles.DepartmentEmployee, d.CallCenter);
        var (agentOutsideCc, _) = await ClientAsync(Roles.CsAgent, d.Facilities);   // a CS Agent who is not a Call Center member
        var (agentWithoutDepartment, _) = await ClientAsync(Roles.CsAgent);

        foreach (var client in new[] { employeeInCc, agentOutsideCc, agentWithoutDepartment })
        {
            Assert.DoesNotContain(ticket, await IdsAsync(client));
            Assert.Equal(HttpStatusCode.Forbidden, await DetailStatusAsync(client, ticket));
        }

        // CS Manager (a management role) still sees it; it is simply not an employee-visible queue.
        Assert.Contains(ticket, await IdsAsync(await ManagerClientAsync()));
    }
}
