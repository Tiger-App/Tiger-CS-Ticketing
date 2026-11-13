using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using TigerCS.Tests.Ticketing.TakeOwnership;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// <c>POST /api/tickets/{id}/reassign</c> through the REAL HTTP pipeline, as a regular Department Employee (not Admin / Manager / Supervisor):
/// employee-to-employee, cross-department, department-only and re-reassignment; assignment history; and the access rules that stay in force.
/// </summary>
public sealed class ReassignEndpointTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private static int _counter;

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientAsync(string role, int? departmentId = null)
    {
        var (username, password, employeeId) = await factory.SeedEmployeeAsync(role);
        if (departmentId is { } department)
        {
            await factory.AssignPrimaryDepartmentAsync(employeeId, department);
        }

        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private async Task<long> TicketAsync(int departmentId, Guid? owner)
    {
        long id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var ticket = ClaimTestData.Open($"TG-RA-{Interlocked.Increment(ref _counter):D6}-{Guid.NewGuid():N}"[..28], departmentId);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            id = ticket.TicketId;
        }

        if (owner is { } o)
        {
            await factory.AssignOwnerAsync(id, o);
        }

        return id;
    }

    private static async Task<TicketDetailDto> DetailAsync(HttpClient client, long id)
    {
        var response = await client.GetAsync($"/api/tickets/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketDetailDto>())!;
    }

    private static async Task<HttpResponseMessage> ReassignAsync(HttpClient client, long id, int departmentId, Guid? employee, string? comment = null)
    {
        var rowVersion = Convert.FromBase64String((await DetailAsync(client, id)).RowVersion);
        return await client.PostAsJsonAsync($"/api/tickets/{id}/reassign", new ReassignTicketRequestDto(departmentId, rowVersion, employee, comment));
    }

    [Fact]
    public async Task RegularEmployee_ReassignsToAColleague_ThenTheColleagueHandsItBack_WithHistory()
    {
        await factory.SeedPrioritiesAsync();
        var department = await factory.CreateDepartmentAsync($"Reassign Dept {Guid.NewGuid():N}"[..30], $"RA{Guid.NewGuid():N}"[..8]);
        var (alice, aliceId) = await ClientAsync(Roles.DepartmentEmployee, department);
        var (bob, bobId) = await ClientAsync(Roles.DepartmentEmployee, department);
        var id = await TicketAsync(department, aliceId);
        var statusBefore = (await DetailAsync(alice, id)).TicketStatus;

        var toBob = await ReassignAsync(alice, id, department, bobId, "Covering my leave");
        Assert.Equal(HttpStatusCode.OK, toBob.StatusCode);
        var updated = (await toBob.Content.ReadFromJsonAsync<TicketDetailDto>())!;
        Assert.Equal(bobId, updated.CurrentOwnerEmployeeId);
        Assert.Equal(statusBefore, updated.TicketStatus);

        // Alice no longer owns it, so she can no longer see or touch it; Bob can reassign it again (an already assigned ticket).
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/tickets/{id}")).StatusCode);
        var back = await ReassignAsync(bob, id, department, aliceId);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(aliceId, (await DetailAsync(alice, id)).CurrentOwnerEmployeeId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var rows = await db.TicketAssignments.Where(a => a.TicketId == id).OrderBy(a => a.TicketAssignmentId).ToListAsync();
        // The seeded owner row, then Bob (by Alice), then Alice again (by Bob) - each earlier row superseded, only the last current.
        Assert.Equal(new[] { aliceId, bobId, aliceId }, rows.Select(r => r.AssignedEmployeeId));
        Assert.Equal(new Guid?[] { null, aliceId, bobId }, rows.Select(r => r.AssigningActorEmployeeId));
        Assert.All(rows.Take(2), r => Assert.NotNull(r.SupersededAtUtc));
        Assert.Null(rows[^1].SupersededAtUtc);
    }

    [Fact]
    public async Task RegularEmployee_TransfersToAnotherDepartment_AssigningAnEmployeeThere_OrTheDepartmentOnly()
    {
        await factory.SeedPrioritiesAsync();
        var from = await factory.CreateDepartmentAsync($"From {Guid.NewGuid():N}"[..20], $"FR{Guid.NewGuid():N}"[..8]);
        var to = await factory.CreateDepartmentAsync($"To {Guid.NewGuid():N}"[..20], $"TO{Guid.NewGuid():N}"[..8]);
        var (me, meId) = await ClientAsync(Roles.DepartmentEmployee, from);
        var (_, saraId) = await ClientAsync(Roles.DepartmentEmployee, to);

        var withEmployee = await TicketAsync(from, meId);
        var response = await ReassignAsync(me, withEmployee, to, saraId, "Wrong department");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = (await response.Content.ReadFromJsonAsync<TicketDetailDto>())!;
        Assert.Equal(to, moved.CurrentDepartmentId);
        Assert.Equal(saraId, moved.CurrentOwnerEmployeeId);

        var departmentOnly = await TicketAsync(from, meId);
        var only = await ReassignAsync(me, departmentOnly, to, null);
        Assert.Equal(HttpStatusCode.OK, only.StatusCode);
        Assert.Equal(to, (await only.Content.ReadFromJsonAsync<TicketDetailDto>())!.CurrentDepartmentId);
    }

    [Fact]
    public async Task InactiveEmployee_NonMember_AndSameDepartmentWithNobody_AreRefused_AndNothingChanges()
    {
        await factory.SeedPrioritiesAsync();
        var department = await factory.CreateDepartmentAsync($"Refuse {Guid.NewGuid():N}"[..20], $"RF{Guid.NewGuid():N}"[..8]);
        var other = await factory.CreateDepartmentAsync($"Other {Guid.NewGuid():N}"[..20], $"OT{Guid.NewGuid():N}"[..8]);
        var (me, meId) = await ClientAsync(Roles.DepartmentEmployee, department);
        var (_, outsiderId) = await ClientAsync(Roles.DepartmentEmployee, other);
        var id = await TicketAsync(department, meId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ReassignAsync(me, id, department, outsiderId)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ReassignAsync(me, id, department, null)).StatusCode);

        var (_, inactiveId) = await ClientAsync(Roles.DepartmentEmployee, department);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var employee = await db.Employees.FirstAsync(e => e.EmployeeId == inactiveId);
            employee.Deactivate(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ReassignAsync(me, id, department, inactiveId)).StatusCode);
        Assert.Equal(meId, (await DetailAsync(me, id)).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task SomeoneWhoCannotSeeTheTicket_CannotReassignIt_AndReadOnlyRolesCannotEither()
    {
        await factory.SeedPrioritiesAsync();
        var department = await factory.CreateDepartmentAsync($"Private {Guid.NewGuid():N}"[..20], $"PV{Guid.NewGuid():N}"[..8]);
        var (_, ownerId) = await ClientAsync(Roles.DepartmentEmployee, department);
        var (colleague, colleagueId) = await ClientAsync(Roles.DepartmentEmployee, department);
        var (chairman, _) = await ClientAsync(Roles.ChairmanCeo, department);
        var id = await TicketAsync(department, ownerId);
        var body = new ReassignTicketRequestDto(department, [], colleagueId);

        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PostAsJsonAsync($"/api/tickets/{id}/reassign", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await chairman.PostAsJsonAsync($"/api/tickets/{id}/reassign", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"/api/tickets/{id}/reassign", body)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Equal(ownerId, (await db.Tickets.AsNoTracking().FirstAsync(t => t.TicketId == id)).CurrentOwnerEmployeeId);
    }
}
