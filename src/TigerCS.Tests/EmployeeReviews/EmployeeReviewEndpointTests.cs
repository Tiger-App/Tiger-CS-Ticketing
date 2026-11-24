using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.EmployeeReviews;

/// <summary>The real Api host: anonymous tablet endpoints, administrator-only tablet management, and role-scoped reporting.</summary>
public sealed class EmployeeReviewEndpointTests : IClassFixture<TigerCsApiFactory>
{
    private const string Tablet = "/api/public/employee-reviews";
    private const string Devices = "/api/admin/employee-review-devices";
    private const string Header = "X-Device-Token";

    private readonly TigerCsApiFactory _factory;

    public EmployeeReviewEndpointTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> SignedInAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return (client, employeeId);
    }

    private async Task<(string Token, Guid DeviceId, Guid EmployeeId, HttpClient Admin)> RegisteredTabletAsync()
    {
        var (admin, _) = await SignedInAsync(Roles.SystemAdministrator);
        var (_, _, agent) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var response = await admin.PostAsJsonAsync(Devices, new RegisterEmployeeReviewDeviceRequestDto("Desk " + Guid.NewGuid().ToString("N")[..6], agent));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var credential = (await response.Content.ReadFromJsonAsync<EmployeeReviewDeviceCredentialDto>())!;
        return (credential.Token, credential.Device.Id, agent, admin);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string url, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(Header, token);
        }

        return await client.SendAsync(request);
    }

    private static SubmitEmployeeReviewRequestDto Review(int rating = 5, string? comment = null, Guid? id = null) => new(id ?? Guid.NewGuid(), rating, comment);

    // ---------------------------------------------------------------- the tablet (anonymous)

    [Fact]
    public async Task RegisteredTablet_Session_ThenSubmit_Works_WithoutAnyLogin()
    {
        var (token, _, _, _) = await RegisteredTabletAsync();
        var anonymous = _factory.CreateClient();

        var session = await Post(anonymous, Tablet + "/session", token);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var info = (await session.Content.ReadFromJsonAsync<TabletSessionDto>())!;
        Assert.Equal(TabletSessionStatus.Ready, info.Status);
        Assert.Equal("Test Employee", info.Tablet!.EmployeeName);

        var submit = await Post(anonymous, Tablet + "/submit", token, Review(4, "Helpful"));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, (await submit.Content.ReadFromJsonAsync<SubmitEmployeeReviewResultDto>())!.Outcome);
    }

    [Fact]
    public async Task Submit_WithAnEmployeeIdInTheBody_StoresTheTabletsEmployee_NotTheOneTheBrowserSent()
    {
        var (token, _, tabletEmployee, admin) = await RegisteredTabletAsync();
        var (_, _, other) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var anonymous = _factory.CreateClient();

        // An extra "employeeId" the browser might try to smuggle in is not part of the contract and is ignored.
        var response = await Post(anonymous, Tablet + "/submit", token, new { submissionId = Guid.NewGuid(), rating = 5, comment = "x", employeeId = other });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var manager = await SignedInAsync(Roles.CsManager);
        var overview = (await manager.Client.GetFromJsonAsync<EmployeeReviewsOverviewDto>("/api/employee-reviews"))!;
        Assert.Contains(overview.Employees, e => e.EmployeeId == tabletEmployee);
        Assert.DoesNotContain(overview.Employees, e => e.EmployeeId == other);
    }

    [Fact]
    public async Task Submit_WithoutOrWithAWrongToken_Is401_AndNothingIsStored()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, Tablet + "/submit", null, Review())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, Tablet + "/submit", "tgr-" + new string('A', 43), Review())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, Tablet + "/submit", "garbage", Review())).StatusCode);

        var session = await Post(anonymous, Tablet + "/session", "garbage");
        Assert.Equal(TabletSessionStatus.Invalid, (await session.Content.ReadFromJsonAsync<TabletSessionDto>())!.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-3)]
    public async Task Submit_WithARatingOutsideOneToFive_Is400(int rating)
    {
        var (token, _, _, _) = await RegisteredTabletAsync();

        var response = await Post(_factory.CreateClient(), Tablet + "/submit", token, Review(rating));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidRating, (await response.Content.ReadFromJsonAsync<SubmitEmployeeReviewResultDto>())!.Outcome);
    }

    [Fact]
    public async Task Submit_WithAMissingRating_Is400()
    {
        var (token, _, _, _) = await RegisteredTabletAsync();

        var response = await Post(_factory.CreateClient(), Tablet + "/submit", token, new { submissionId = Guid.NewGuid(), comment = "no stars" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Submit_WithATooLongComment_Is400()
    {
        var (token, _, _, _) = await RegisteredTabletAsync();

        var response = await Post(_factory.CreateClient(), Tablet + "/submit", token, Review(3, new string('x', 1001)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Submit_TheSameSubmissionTwice_IsAcceptedTwice_ButStoredOnce()
    {
        var (token, _, employee, _) = await RegisteredTabletAsync();
        var anonymous = _factory.CreateClient();
        var id = Guid.NewGuid();

        var first = await Post(anonymous, Tablet + "/submit", token, Review(5, "once", id));
        var retry = await Post(anonymous, Tablet + "/submit", token, Review(1, "changed", id));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(SubmitEmployeeReviewOutcome.AlreadyRecorded, (await retry.Content.ReadFromJsonAsync<SubmitEmployeeReviewResultDto>())!.Outcome);

        var manager = await SignedInAsync(Roles.CsManager);
        var detail = (await manager.Client.GetFromJsonAsync<EmployeeReviewDetailDto>($"/api/employee-reviews/employees/{employee}"))!;
        var stored = Assert.Single(detail.Reviews);
        Assert.Equal(5, stored.Rating);
        Assert.Equal("once", stored.Comment);
    }

    [Fact]
    public async Task InactiveTablet_Is403_UntilAnAdministratorActivatesItAgain()
    {
        var (token, deviceId, _, admin) = await RegisteredTabletAsync();
        var anonymous = _factory.CreateClient();

        var off = await admin.PostAsJsonAsync($"{Devices}/{deviceId}/activation", new SetEmployeeReviewDeviceActiveRequestDto(false));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(anonymous, Tablet + "/submit", token, Review())).StatusCode);

        await admin.PostAsJsonAsync($"{Devices}/{deviceId}/activation", new SetEmployeeReviewDeviceActiveRequestDto(true));
        Assert.Equal(HttpStatusCode.OK, (await Post(anonymous, Tablet + "/submit", token, Review())).StatusCode);
    }

    [Fact]
    public async Task RegeneratingOrRevoking_StopsTheOldToken_Immediately()
    {
        var (token, deviceId, _, admin) = await RegisteredTabletAsync();
        var anonymous = _factory.CreateClient();

        var regenerated = (await (await admin.PostAsync($"{Devices}/{deviceId}/credential", null)).Content.ReadFromJsonAsync<EmployeeReviewDeviceCredentialDto>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, Tablet + "/submit", token, Review())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(anonymous, Tablet + "/submit", regenerated.Token, Review())).StatusCode);

        var revoke = await admin.DeleteAsync($"{Devices}/{deviceId}/credential");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, Tablet + "/submit", regenerated.Token, Review())).StatusCode);
    }

    [Fact]
    public async Task ATabletThatKeepsHammeringTheApi_IsRateLimited_WhileNormalUseIsNever()
    {
        var anonymous = _factory.CreateClient();
        var token = "tgr-" + Guid.NewGuid().ToString("N") + new string('B', 11); // its own rate-limit partition; never issued, so every call is a cheap 401
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 75; i++)
        {
            statuses.Add((await Post(anonymous, Tablet + "/session", token)).StatusCode);
        }

        Assert.All(statuses.Take(60), s => Assert.NotEqual(HttpStatusCode.TooManyRequests, s));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses.Skip(60));
    }

    // ---------------------------------------------------------------- tablet administration

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ReportingUser)]
    public async Task TabletAdministration_IsRefused_ForEveryRoleExceptTheSystemAdministrator(string role)
    {
        var (client, _) = await SignedInAsync(role);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Devices)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Devices + "/eligible-employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Devices, new RegisterEmployeeReviewDeviceRequestDto("x", Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Devices}/{id}", new UpdateEmployeeReviewDeviceRequestDto("x", Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Devices}/{id}/activation", new SetEmployeeReviewDeviceActiveRequestDto(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Devices}/{id}/credential", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Devices}/{id}/credential")).StatusCode);
    }

    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnEveryEmployeeReviewEndpoint()
    {
        var (token, deviceId, employeeId, admin) = await RegisteredTabletAsync();

        // Authorized means "never 401/403": the answer is whatever the request itself deserves.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Devices)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Devices + "/eligible-employees")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Devices}/{deviceId}", new UpdateEmployeeReviewDeviceRequestDto("Renamed " + deviceId.ToString("N")[..6], employeeId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Devices}/{deviceId}/activation", new SetEmployeeReviewDeviceActiveRequestDto(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/employee-reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/employee-reviews/employees/{employeeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Devices}/{deviceId}/credential", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"{Devices}/{deviceId}/credential")).StatusCode);
        Assert.NotNull(token);
    }

    [Fact]
    public async Task TabletAdministration_RequiresALogin()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Devices)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Devices, new RegisterEmployeeReviewDeviceRequestDto("x", Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/employee-reviews")).StatusCode);
    }

    [Fact]
    public async Task Administrator_ListsDevices_WithoutAnyCredentialInTheResponse()
    {
        var (token, _, _, admin) = await RegisteredTabletAsync();

        var raw = await (await admin.GetAsync(Devices)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_ForAnIneligibleOrUnknownEmployee_Is400()
    {
        var (admin, _) = await SignedInAsync(Roles.SystemAdministrator);
        var (_, _, collectionsOnly) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Devices, new RegisterEmployeeReviewDeviceRequestDto("Nope", collectionsOnly))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Devices, new RegisterEmployeeReviewDeviceRequestDto("Nope", Guid.NewGuid()))).StatusCode);
    }

    // ---------------------------------------------------------------- the dashboard

    [Fact]
    public async Task Dashboard_ShowsAnEmployeeOnlyTheirOwnReviews_AndAManagerEveryones()
    {
        // The signed-in CS Agent is the employee a tablet is registered for.
        var (agentClient, agentId) = await SignedInAsync(Roles.CsAgent);
        var (admin, _) = await SignedInAsync(Roles.SystemAdministrator);
        var (_, _, colleague) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var ownTablet = (await (await admin.PostAsJsonAsync(Devices, new RegisterEmployeeReviewDeviceRequestDto("Own " + Guid.NewGuid().ToString("N")[..5], agentId))).Content.ReadFromJsonAsync<EmployeeReviewDeviceCredentialDto>())!;
        var colleagueTablet = (await (await admin.PostAsJsonAsync(Devices, new RegisterEmployeeReviewDeviceRequestDto("Other " + Guid.NewGuid().ToString("N")[..5], colleague))).Content.ReadFromJsonAsync<EmployeeReviewDeviceCredentialDto>())!;
        var anonymous = _factory.CreateClient();
        await Post(anonymous, Tablet + "/submit", ownTablet.Token, Review(5, "mine"));
        await Post(anonymous, Tablet + "/submit", colleagueTablet.Token, Review(1, "theirs"));

        var own = (await agentClient.GetFromJsonAsync<EmployeeReviewsOverviewDto>("/api/employee-reviews"))!;
        Assert.Equal(agentId, Assert.Single(own.Employees).EmployeeId);
        Assert.DoesNotContain(own.Comments, c => c.Comment == "theirs");

        Assert.Equal(HttpStatusCode.Forbidden, (await agentClient.GetAsync($"/api/employee-reviews/employees/{colleague}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agentClient.GetAsync($"/api/employee-reviews/employees/{agentId}")).StatusCode);

        var manager = await SignedInAsync(Roles.CsManager);
        var all = (await manager.Client.GetFromJsonAsync<EmployeeReviewsOverviewDto>("/api/employee-reviews"))!;
        Assert.Contains(all.Employees, e => e.EmployeeId == agentId);
        Assert.Contains(all.Employees, e => e.EmployeeId == colleague);
        Assert.Contains(all.Comments, c => c.Comment == "theirs");
    }

    [Fact]
    public async Task Dashboard_IsRefused_ForARoleWithoutThePermission_AndForADeniedEmployee()
    {
        var (reporting, _) = await SignedInAsync(Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporting.GetAsync("/api/employee-reviews")).StatusCode);

        var (agent, agentId) = await SignedInAsync(Roles.CsAgent);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/employee-reviews")).StatusCode);
        await _factory.SetPermissionOverrideAsync(agentId, TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.EmployeeReviewsView, PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/employee-reviews")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_AcceptsTheUaeDateFilters()
    {
        var (token, _, employee, _) = await RegisteredTabletAsync();
        await Post(_factory.CreateClient(), Tablet + "/submit", token, Review(5, "today"));
        var manager = await SignedInAsync(Roles.CsManager);

        var thisMonth = (await manager.Client.GetFromJsonAsync<EmployeeReviewsOverviewDto>("/api/employee-reviews?dateFrom=2020-01-01&dateTo=2020-01-31"))!;
        Assert.DoesNotContain(thisMonth.Employees, e => e.EmployeeId == employee);

        var today = TigerCS.Domain.Common.UaeCalendar.Today(TimeProvider.System).ToString("yyyy-MM-dd");
        var current = (await manager.Client.GetFromJsonAsync<EmployeeReviewsOverviewDto>($"/api/employee-reviews?dateFrom={today}&dateTo={today}&employeeId={employee}&rating=5"))!;
        Assert.Equal(1, current.Summary.TotalReviews);
    }
}
