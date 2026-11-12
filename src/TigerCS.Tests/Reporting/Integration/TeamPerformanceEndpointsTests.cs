using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Reporting.Integration;

/// <summary>
/// The Team Performance endpoints through the real HTTP pipeline: the CS
/// Manager tier reads them, a CS Agent is refused, and the System
/// Administrator passes through the ADR-0024 override. Also the business
/// refusals of the records endpoint (unknown metric, employee not on the
/// report) and of the SLA details endpoint (employee not on the report),
/// which are status codes — never exceptions.
/// </summary>
public class TeamPerformanceEndpointsTests(TigerCsApiFactory factory) : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> ClientForAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    [Fact]
    public async Task CsAgent_IsRefused_OnEveryEndpoint()
    {
        var (client, employeeId) = await ClientForAsync(Roles.CsAgent);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/team-performance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/reports/team-performance/records?employeeId={employeeId}&metric=TicketsWorked")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/reports/team-performance/sla-details?employeeId={employeeId}")).StatusCode);
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.ReportingUser)]
    public async Task OtherRoles_AreRefused(string role)
    {
        var (client, employeeId) = await ClientForAsync(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/team-performance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/reports/team-performance/sla-details?employeeId={employeeId}")).StatusCode);
    }

    [Fact]
    public async Task CsManager_ReadsTheReport_WithEveryActiveEmployeeListed()
    {
        var (_, agentId) = await ClientForAsync(Roles.CsAgent);
        var (manager, managerId) = await ClientForAsync(Roles.CsManager);

        var response = await manager.GetAsync("/api/reports/team-performance?dateFrom=2026-08-01&dateTo=2026-09-01");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<TeamPerformanceReportDto>();
        Assert.NotNull(report);
        Assert.Equal(new DateOnly(2026, 8, 1), report!.Filters.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 1), report.Filters.DateTo);
        var agent = Assert.Single(report.Rows, r => r.EmployeeId == agentId);
        Assert.Equal([Roles.CsAgent], agent.Roles);
        // Every active employee is on the report, whatever their role — the manager included.
        var manager_ = Assert.Single(report.Rows, r => r.EmployeeId == managerId);
        Assert.Equal([Roles.CsManager], manager_.Roles);
        Assert.Contains(Roles.CsManager, report.FilterOptions.Roles);
        Assert.Contains(Roles.CsAgent, report.FilterOptions.Roles);

        // The Role filter narrows by Identity role.
        var onlyManagers = await manager.GetFromJsonAsync<TeamPerformanceReportDto>($"/api/reports/team-performance?role={Uri.EscapeDataString(Roles.CsManager)}");
        Assert.All(onlyManagers!.Rows, r => Assert.Contains(Roles.CsManager, r.Roles));
        Assert.DoesNotContain(onlyManagers.Rows, r => r.EmployeeId == agentId);
    }

    [Theory]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task GeneralManagerAndChairman_ReadTheReport(string role)
    {
        var (client, _) = await ClientForAsync(role);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/team-performance")).StatusCode);
    }

    /// <summary>Referenced by ProtectedEndpointInventoryTests: the System Administrator is authorized on both endpoints through the override.</summary>
    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnBothEndpoints_ThroughTheOverride()
    {
        var (_, agentId) = await ClientForAsync(Roles.CsAgent);
        var (admin, _) = await ClientForAsync(Roles.SystemAdministrator);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports/team-performance")).StatusCode);

        var records = await admin.GetAsync($"/api/reports/team-performance/records?employeeId={agentId}&metric=CurrentlyAssigned");
        Assert.Equal(HttpStatusCode.OK, records.StatusCode);
        var body = await records.Content.ReadFromJsonAsync<TeamPerformanceRecordsDto>();
        Assert.Equal(agentId, body!.EmployeeId);
        Assert.Equal("CurrentlyAssigned", body.Metric);
        Assert.Empty(body.Records);

        var slaDetails = await admin.GetAsync($"/api/reports/team-performance/sla-details?employeeId={agentId}");
        Assert.Equal(HttpStatusCode.OK, slaDetails.StatusCode);
    }

    [Fact]
    public async Task SlaDetails_AreServedToTheManagerTier_WithSummaryRulesAndPaging_AndRefuseAnUnknownEmployee()
    {
        var (_, agentId) = await ClientForAsync(Roles.CsAgent);
        var (manager, _) = await ClientForAsync(Roles.CsManager);

        var response = await manager.GetAsync($"/api/reports/team-performance/sla-details?employeeId={agentId}&dateFrom=2026-08-01&dateTo=2026-09-01&status=Breached&sortBy=due&sortDir=asc&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.Content.ReadFromJsonAsync<TeamPerformanceSlaDetailsDto>();
        Assert.Equal(agentId, details!.EmployeeId);
        Assert.Equal(new DateOnly(2026, 8, 1), details.DateFrom);
        Assert.Equal("Breached", details.StatusFilter);
        Assert.Equal("due", details.SortBy);
        Assert.Equal("asc", details.SortDir);
        Assert.Equal(10, details.PageSize);
        Assert.Equal(0, details.Summary.BreachEvents);
        Assert.Empty(details.Records);
        Assert.Contains(details.Rules.Rules, r => r.Topic == "How breaches are attributed to employees");

        // The same number as the report says.
        var report = await manager.GetFromJsonAsync<TeamPerformanceReportDto>("/api/reports/team-performance?dateFrom=2026-08-01&dateTo=2026-09-01");
        Assert.Equal(report!.Rows.Single(r => r.EmployeeId == agentId).TicketsWithSlaBreach, details.Summary.TicketsWithBreach);

        Assert.Equal(HttpStatusCode.NotFound,
            (await manager.GetAsync($"/api/reports/team-performance/sla-details?employeeId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Records_RefuseAnUnknownMetricWith400_AndAnEmployeeNotOnTheReportWith404()
    {
        var (_, agentId) = await ClientForAsync(Roles.CsAgent);
        var (manager, managerId) = await ClientForAsync(Roles.CsManager);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await manager.GetAsync($"/api/reports/team-performance/records?employeeId={agentId}&metric=Velocity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await manager.GetAsync($"/api/reports/team-performance/records?employeeId={Guid.NewGuid()}&metric=TicketsWorked")).StatusCode);
        // The manager is an active employee, so they have a row and their records open.
        Assert.Equal(HttpStatusCode.OK,
            (await manager.GetAsync($"/api/reports/team-performance/records?employeeId={managerId}&metric=TicketsWorked")).StatusCode);
    }
}
