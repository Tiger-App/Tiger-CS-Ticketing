using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Integration;

/// <summary>The Tower Assignments and ticket assignment-history endpoints over the real pipeline: authentication, the System Administrator override, and the service-side refusal of everyone else.</summary>
public sealed class CollectionsTowerAssignmentEndpointTests
{
    private sealed class OneTowerCatalog : ICollectionsTowerCatalog
    {
        public Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CollectionsTowerDto>>([new CollectionsTowerDto(1, "140", "Tiger Tower 140", 4, true)]);
    }

    private static async Task<HttpClient> Client(TigerCsApiFactory factory, string role)
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    [Fact]
    public async Task TowerAssignmentEndpoints_AreAuthenticated_OpenToTheSystemAdministrator_AndRefuseAnEmployeeOutsideCollections()
    {
        using var factory = new TigerCsApiFactory { ExtraServices = services => services.AddScoped<ICollectionsTowerCatalog, OneTowerCatalog>() };
        const string url = "/api/collections/tower-assignments?year=2026&month=11";
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);

        using var outsider = await Client(factory, Roles.DepartmentEmployee);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/collections/tower-assignments/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/collections/tower-assignments/activate", new ActivateTowerAssignmentsRequest(2026, 11))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/collections/tower-assignments/copy-previous", new CopyPreviousMonthRequest(2026, 11))).StatusCode);

        using var admin = await Client(factory, Roles.SystemAdministrator);
        var month = await admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, month.StatusCode);
        var dto = (await month.Content.ReadFromJsonAsync<TowerAssignmentMonthDto>())!;
        Assert.True(dto.CanManage);
        Assert.Equal([1], dto.UnassignedTowerIds);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/collections/tower-assignments/history?year=2026&month=11")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/collections/tower-assignments/activate", new ActivateTowerAssignmentsRequest(2026, 11))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/collections/tower-assignments/copy-previous", new CopyPreviousMonthRequest(2026, 11))).StatusCode);

        // An employee who is not an active Collections member is refused by the service (400), never silently accepted.
        var rejected = await admin.PutAsJsonAsync($"/api/collections/tower-assignments/employees/{Guid.NewGuid()}", new SaveEmployeeTowersRequest(2026, 11, [1], Activate: true));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync($"/api/collections/tower-assignments/employees/{Guid.NewGuid()}", new SaveEmployeeTowersRequest(2026, 11, [1]))).StatusCode);
    }

    [Fact]
    public async Task TicketAssignmentHistoryEndpoint_RequiresALogin_AndIsReachableByTheSystemAdministrator()
    {
        using var factory = new TigerCsApiFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tickets/1/assignment-history")).StatusCode);

        using var admin = await Client(factory, Roles.SystemAdministrator);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/tickets/999999/assignment-history")).StatusCode);   // authorized; just no such ticket
    }
}
