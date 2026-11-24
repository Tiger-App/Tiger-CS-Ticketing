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
    public async Task FullFlow_DepartmentEmployeeRequests_IsRefused_AndNeitherAnApprovalNorARealReopenCanMoveTheTicket_ClosedIsTerminal()
    {
        var agentClient = await ClientForAsync(Roles.CsAgent);
        var (ticketId, departmentId, closedRowVersion) = await SeedClosedTicketAsync(agentClient);

        // 1. The Department Employee the (closed) ticket is assigned to - with no direct Reopen - is not offered a Reopen Approval any more, and asking is
        //    refused. A department colleague the ticket is NOT assigned to still cannot even open it (assignment-only visibility), so cannot ask.
        var requesterClient = _assignee!;
        var colleagueClient = await ClientForAsync(Roles.DepartmentEmployee, departmentId);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleagueClient.GetAsync($"/api/tickets/{ticketId}/approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleagueClient.PostAsJsonAsync($"/api/tickets/{ticketId}/approvals", ReopenRequest())).StatusCode);
        var offered = await (await requesterClient.GetAsync($"/api/tickets/{ticketId}/approvals"))
            .Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
        var requestable = Assert.Single(offered!.RequestableApprovals);
        Assert.Equal(nameof(ApprovalType.ReopenApproval), requestable.ApprovalType);
        Assert.False(requestable.CallerCanRequest);

        var requestResponse = await requesterClient.PostAsJsonAsync($"/api/tickets/{ticketId}/approvals", ReopenRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, requestResponse.StatusCode);

        // The ticket has not moved, and no approval record exists.
        var stillClosed = await (await agentClient.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", stillClosed!.TicketStatus);
        Assert.Equal(0, stillClosed.ReopenCount);
        Assert.False(stillClosed.IsReopenEligible);
        Assert.Equal(departmentId, stillClosed.CurrentDepartmentId);

        var supervisorClient = await ClientForAsync(Roles.CsSupervisor);
        var viewAfterRequest = await (await supervisorClient.GetAsync($"/api/tickets/{ticketId}/approvals"))
            .Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
        Assert.Empty(viewAfterRequest!.Approvals);

        // 2. An approval would have granted the requester nothing anyway: they still cannot reopen (role).
        var stillForbidden = await requesterClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Approved, so let me.", departmentId, Convert.FromBase64String(closedRowVersion)));
        Assert.Equal(HttpStatusCode.Forbidden, stillForbidden.StatusCode);

        // 3. A CS Agent trying the real reopen through the endpoint is refused: Closed is terminal.
        var reopenResponse = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Approved reopen request.", departmentId, Convert.FromBase64String(closedRowVersion)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reopenResponse.StatusCode);
        Assert.Contains("A closed ticket cannot be reopened", await reopenResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var afterAttempt = await (await agentClient.GetAsync($"/api/tickets/{ticketId}"))
            .Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Closed", afterAttempt!.TicketStatus);
        Assert.Equal(0, afterAttempt.ReopenCount);
        Assert.Equal(stillClosed.RowVersion, afterAttempt.RowVersion);

        // Nothing about a reopen reached the history or the outbox.
        var view = await (await supervisorClient.GetAsync($"/api/tickets/{ticketId}/approvals"))
            .Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
        Assert.DoesNotContain(view!.Events, e => e.EventType == "Reopened");
        Assert.DoesNotContain(view.Events, e => e.EventType == "ApprovalRequested");
        Assert.Empty(await ReopenedEventsForAsync(ticketId));
    }

    [Fact]
    public async Task RequestingReopenApproval_IsForbiddenForRolesWithoutIt_AndNotOfferedToAnyoneAnyMore_ClosedIsTerminal()
    {
        var agentClient = await ClientForAsync(Roles.CsAgent);
        var (ticketId, departmentId, _) = await SeedClosedTicketAsync(agentClient);

        // Reporting User: no request capability at all, department membership
        // notwithstanding.
        var reporterClient = await ClientForAsync(Roles.ReportingUser, departmentId);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reporterClient.PostAsJsonAsync($"/api/tickets/{ticketId}/approvals", ReopenRequest())).StatusCode);

        // A Department Employee of a DIFFERENT department cannot ask either.
        var otherDepartmentId = await _factory.CreateDepartmentAsync(
            "Unrelated " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var outsiderClient = await ClientForAsync(Roles.DepartmentEmployee, otherDepartmentId);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsiderClient.PostAsJsonAsync($"/api/tickets/{ticketId}/approvals", ReopenRequest())).StatusCode);

        // Executive roles could ask from anywhere before; now the offer is gone and the request is refused (the ticket is Closed, which is terminal).
        foreach (var role in new[] { Roles.GeneralManager, Roles.ChairmanCeo })
        {
            var executiveClient = await ClientForAsync(role);
            var view = await (await executiveClient.GetAsync($"/api/tickets/{ticketId}/approvals"))
                .Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
            Assert.False(Assert.Single(view!.RequestableApprovals).CallerCanRequest);
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await executiveClient.PostAsJsonAsync($"/api/tickets/{ticketId}/approvals", ReopenRequest())).StatusCode);
        }

        // ...and the roles that hold direct Reopen are never offered it either.
        foreach (var role in new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager, Roles.SystemAdministrator })
        {
            var csClient = await ClientForAsync(role);
            if (role == Roles.CsAgent)
            {
                // A CS Agent sees a ticket's approvals only if the ticket is theirs to see: this one took the call too.
                await _factory.RecordHandlerAsync(ticketId, csClient);
            }

            var view = await (await csClient.GetAsync($"/api/tickets/{ticketId}/approvals"))
                .Content.ReadFromJsonAsync<TicketApprovalsViewDto>();
            Assert.False(Assert.Single(view!.RequestableApprovals).CallerCanRequest);
        }
    }

    [Fact]
    public async Task AskingForAReopenApproval_SendsNoCustomerEmail_AndTheRefusedRealReopenSendsNone_ClosedIsTerminal()
    {
        var agentClient = await ClientForAsync(Roles.CsAgent);
        var (ticketId, departmentId, closedRowVersion) = await SeedClosedTicketAsync(agentClient);

        var requesterClient = _assignee!;
        var request = await requesterClient.PostAsJsonAsync($"/api/tickets/{ticketId}/approvals", ReopenRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, request.StatusCode);

        // Nothing customer-facing was queued by the (refused) request.
        Assert.Empty(await ReopenedEventsForAsync(ticketId));

        var reopen = await agentClient.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/reopen",
            new ReopenTicketRequestDto("Approved reopen.", departmentId, Convert.FromBase64String(closedRowVersion)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reopen.StatusCode);

        // ...and the refused reopen queues nothing either: no reopened email exists for this ticket.
        Assert.Empty(await ReopenedEventsForAsync(ticketId));
    }

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
