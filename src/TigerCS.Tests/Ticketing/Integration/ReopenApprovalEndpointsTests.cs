using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Reopen Approval end to end, through the real endpoints: a role without
/// direct Reopen asks, the targeted CS Manager decides, and — whatever the
/// decision — the ticket only ever moves when someone with direct Reopen
/// performs the existing Reopen. The approval is a request, never a reopen
/// and never a bypass.
/// </summary>
public class ReopenApprovalEndpointsTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;

    /// <summary>
    /// Reopened messages queued for one ticket. Read straight from the Outbox
    /// rather than dispatched: what matters here is whether the approval
    /// enqueued anything customer-facing, not whether SMTP ran.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReopenedEventsForAsync(long ticketId) =>
        [.. (await _factory.GetOutboxMessagesAsync())
            .Where(m => m.EventType == "TicketReopened" && m.Payload.Contains($"\"ticketId\":{ticketId}", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.EventType)];

    private async Task<HttpClient> ClientForAsync(string role, int? departmentId = null)
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
        return client;
    }

    /// <summary>
    /// A Closed/Resolved ticket whose request type configures ReopenApproval
    /// targeting the CS Manager role — driven there through the real
    /// lifecycle, so the closure moment the reopen window is measured from is
    /// the one Close itself wrote.
    /// </summary>
    /// <summary>The Department Employee the last seeded closed ticket is assigned to (an employee sees only tickets assigned to them).</summary>
    private HttpClient? _assignee;

    private async Task<(long TicketId, int DepartmentId, string ClosedRowVersion)> SeedClosedTicketAsync(
        HttpClient csClient, bool allowReopen = true)
    {
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync(
            "Facilities " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Reopenable " + Guid.NewGuid(), departmentId);
        var requestTypeId = await _factory.CreateRequestTypeAsync(
            "Reopenable " + Guid.NewGuid(), departmentId, ApprovalType.ReopenApproval,
            approvalTargetRoleName: Roles.CsManager, allowReopen: allowReopen);

        var intake = await (await csClient.PostAsJsonAsync(
                "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+9715099" + Random.Shared.Next(10000, 99999), null, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var created = await (await csClient.PostAsJsonAsync(
                "/api/tickets",
                new CreateTicketRequestDto(
                    intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "AC not cooling",
                    RequestTypeId: requestTypeId)))
            .Content.ReadFromJsonAsync<TicketResponseDto>();

        var ticketId = created!.TicketId;
        // The creating CS Agent is the ticket's responsible CS Agent (nobody else was ever assigned), so once the department employee
        // holds it they can close it. An unassigned ticket is not visible to them, so the RowVersion comes from the create response.

        var workerClient = await ClientForAsync(Roles.DepartmentEmployee, departmentId);
        var headClient = await ClientForAsync(Roles.DepartmentHead, departmentId);
        var workerId = TigerCsApiFactory.UserIdOf(workerClient);
        _assignee = workerClient;   // the Department Employee the ticket is assigned to: the only employee who can see it

        var afterAssign = await (await headClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/assignment",
                new AssignTicketRequestDto(workerId, Convert.FromBase64String(created.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        var afterStatus = await (await headClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/status",
                new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(afterAssign!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterResolve = await (await headClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/resolution",
                new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, Convert.FromBase64String(afterStatus!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        var afterClose = await (await csClient.PostAsJsonAsync(
                $"/api/tickets/{ticketId}/close", new CloseTicketRequestDto(Convert.FromBase64String(afterResolve!.RowVersion))))
            .Content.ReadFromJsonAsync<TicketDetailDto>();

        Assert.Equal("Closed", afterClose!.TicketStatus);
        return (ticketId, departmentId, afterClose.RowVersion);
    }

    private static RequestApprovalRequestDto ReopenRequest(string? reason = "Customer called back — still not cooling.") =>
        new(nameof(ApprovalType.ReopenApproval), reason);

    [Fact]
    public async Task ReopenApproval_RequiresAReason_AndIsRefusedWhenTheRequestTypeForbidsReopen()
    {
        var agentClient = await ClientForAsync(Roles.CsAgent);
        var (ticketId, departmentId, _) = await SeedClosedTicketAsync(agentClient);
        var requesterClient = _assignee!;

        var blank = await requesterClient.PostAsJsonAsync($"/api/tickets/{ticketId}/approvals", ReopenRequest("   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);

        // A request type that forbids Reopen offers no Reopen Approval either.
        var (forbiddenTicketId, forbiddenDepartmentId, _) = await SeedClosedTicketAsync(agentClient, allowReopen: false);
        var forbiddenRequester = _assignee!;
        var refused = await forbiddenRequester.PostAsJsonAsync($"/api/tickets/{forbiddenTicketId}/approvals", ReopenRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var view = await (await forbiddenRequester.GetAsync($"/api/tickets/{forbiddenTicketId}/approvals"))
            .Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
        Assert.False(Assert.Single(view!.RequestableApprovals).CallerCanRequest);
    }

}
