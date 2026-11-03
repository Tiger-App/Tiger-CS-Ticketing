using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Notifications.Integration;

/// <summary>Shared helpers for the end-to-end customer-notification tests: sign in as a role, create a ticket, drive it to Resolved.</summary>
public abstract class LifecycleIntegrationTestBase
{
    protected readonly TigerCsApiFactory _factory;

    protected LifecycleIntegrationTestBase(TigerCsApiFactory factory)
    {
        _factory = factory;
        _factory.EmailSender.Clear();
    }

    protected async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    /// <summary>A CRM-verified ticket whose snapshot contact channel is the fixture's email contact (index 0).</summary>
    protected async Task<(TicketResponseDto Ticket, int DepartmentId)> CreateTicketAsync(HttpClient client, int contactIndex = 0)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Corrective Maintenance", departmentId);

        var intake = await (await client.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971500000001", null, true, "1204", null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var unit = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001"))
            .Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        var contacts = await (await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts"))
            .Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        var createResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, unit!.UnitReferenceId, contacts![contactIndex].ContactReferenceId, categoryId,
                (byte)PriorityLevel.High, "AC unit not cooling"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<TicketResponseDto>())!;
        await _factory.RecordHandlerAsync(created.TicketId, client);
        return (created, departmentId);
    }

    protected async Task<(TicketResponseDto Ticket, HttpClient Worker, HttpClient Agent, TicketDetailDto AfterResolve)> CreateAndResolveAsync(int contactIndex = 0)
    {
        var (agent, _) = await CreateClientAsync(Roles.CsAgent);
        var (manager, _) = await CreateClientAsync(Roles.CsManager);
        var (worker, workerId) = await CreateClientAsync(Roles.DepartmentEmployee);
        var (ticket, departmentId) = await CreateTicketAsync(agent, contactIndex);
        await _factory.AssignPrimaryDepartmentAsync(workerId, departmentId);

        var assignResponse = await manager.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/assignment",
            new AssignTicketRequestDto(workerId, Convert.FromBase64String(ticket.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);
        var afterAssign = await assignResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var statusResponse = await worker.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/status",
            new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var afterStatus = await statusResponse.Content.ReadFromJsonAsync<TicketDetailDto>();

        var resolveResponse = await worker.PostAsJsonAsync(
            $"/api/tickets/{ticket.TicketId}/resolution",
            new ResolveTicketRequestDto(
                "Resolved", "Replaced the thermostat.", null, null, Convert.FromBase64String(afterStatus!.RowVersion)));
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var afterResolve = (await resolveResponse.Content.ReadFromJsonAsync<TicketDetailDto>())!;
        Assert.Equal("Resolved", afterResolve.TicketStatus);

        // The third element is the client that closes/reopens afterwards. It is the CS Manager, not the agent who created the ticket:
        // that agent is the ticket's responsible agent but - having assigned it onwards - a previous owner, who may no longer Close it.
        return (ticket, worker, manager, afterResolve);
    }

    protected async Task<OutboxMessage> OutboxMessageFor(long ticketId, string eventType) =>
        Assert.Single(
            await _factory.GetOutboxMessagesAsync(),
            m => m.EventType == eventType && m.Payload.Contains($"\"ticketId\":{ticketId}", StringComparison.Ordinal));
}
