using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.TakeOwnership;

/// <summary>
/// The operational off-switch (<c>TicketClaim:Enabled=false</c>, used for the window of the one-time Call Center backlog distribution): the
/// whole feature goes away together - list scope, details flag, endpoint - and nothing else changes.
/// </summary>
public sealed class TakeOwnershipOffSwitchTests : IDisposable
{
    private readonly TigerCsApiFactory _factory = new() { ExtraConfiguration = { ["TicketClaim:Enabled"] = "false" } };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task WithTheSwitchOff_TheAgentSeesNoPool_GetsNoAction_AndTheEndpointRefuses_ButTheirOwnTicketsStillWork()
    {
        await _factory.SeedPrioritiesAsync();
        var cc = await _factory.CreateDepartmentAsync("Call Center", "CC");
        var accounting = await _factory.CreateDepartmentAsync("Accounting", "ACCT");
        var (username, password, agentId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        await _factory.AssignPrimaryDepartmentAsync(agentId, cc);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        long pool, mine;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var a = ClaimTestData.Open("TG-OFF-000001", accounting);
            var b = ClaimTestData.Open("TG-OFF-000002", cc);
            b.AssignTo(agentId);
            db.Tickets.AddRange(a, b);
            await db.SaveChangesAsync();
            (pool, mine) = (a.TicketId, b.TicketId);
        }

        var listed = (await (await client.GetAsync("/api/tickets?pageSize=100")).Content.ReadFromJsonAsync<TicketListResultDto>())!.Items.Select(i => i.TicketId).ToArray();
        Assert.Contains(mine, listed);               // the agent's own work is untouched by the switch
        Assert.DoesNotContain(pool, listed);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/tickets/{pool}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/tickets/{pool}/ownership", new TakeOwnershipRequestDto())).StatusCode);
        Assert.Null((await _factory.GetTicketAsync(pool))!.CurrentOwnerEmployeeId);
        Assert.False((await (await client.GetAsync($"/api/tickets/{mine}")).Content.ReadFromJsonAsync<TicketDetailDto>())!.CanTakeOwnership);
    }
}

/// <summary>
/// The pages only OFFER what the API accepts: the button and the filter are tied to the server's own answer, with no role list of their own to
/// drift from it (the same style of source-level guard <c>DepartmentPrimaryAssignmentTests</c> uses for the other display rules).
/// </summary>
public sealed class TakeOwnershipUiContractTests
{
    private static string Source(string relativeToSrc, [CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", "..")), relativeToSrc));

    [Fact]
    public void TheTakeOwnershipForm_IsRenderedOnlyWhenTheServerSaysTheViewerCanTakeIt()
    {
        var page = Source(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml"));

        var formAt = page.IndexOf("asp-page-handler=\"TakeOwnership\"", StringComparison.Ordinal);
        Assert.True(formAt > 0, "the details page has no Take Ownership form");
        var guardAt = page.LastIndexOf("@if (Model.CanTakeOwnership)", formAt, StringComparison.Ordinal);
        Assert.True(guardAt > 0 && formAt - guardAt < 2500, "the Take Ownership form is not inside @if (Model.CanTakeOwnership)");
        Assert.Equal(1, page.Split("asp-page-handler=\"TakeOwnership\"").Length - 1);
    }

    [Fact]
    public void CanTakeOwnership_IsTheServersAnswer_NotAClientSideRoleGuess()
    {
        var model = Source(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml.cs"));

        Assert.Contains("public bool CanTakeOwnership => Ticket?.CanTakeOwnership == true;", model, StringComparison.Ordinal);
        Assert.DoesNotContain("TicketRoleSets", model[model.IndexOf("CanTakeOwnership", StringComparison.Ordinal)..][..400], StringComparison.Ordinal);
    }

    [Fact]
    public void ThePostHandler_CallsTheOwnershipEndpoint_WithTheRowVersionTheViewerSaw()
    {
        var model = Source(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml.cs"));
        var client = Source(Path.Combine("TigerCS.Web", "Services", "Api", "TicketsApiClient.cs"));

        Assert.Contains("OnPostTakeOwnershipAsync", model, StringComparison.Ordinal);
        Assert.Contains("new TakeOwnershipRequestDto(rowVersion)", model, StringComparison.Ordinal);
        Assert.Contains("api/tickets/{ticketId}/ownership", client, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListOffersTheAvailableToTakeFilter_OnlyToCallCenterStyleAgents_AndSendsItToTheApi()
    {
        var model = Source(Path.Combine("TigerCS.Web", "Pages", "Tickets.cshtml.cs"));
        var list = Source(Path.Combine("TigerCS.Web", "Pages", "Shared", "_TicketListView.cshtml"));
        var client = Source(Path.Combine("TigerCS.Web", "Services", "Api", "TicketsApiClient.cs"));

        Assert.Contains("OffersAvailableToTakeFilter", list, StringComparison.Ordinal);
        Assert.Contains("TicketVisibilityRule.IsRestrictedCsAgent", model, StringComparison.Ordinal);
        Assert.Contains("query[\"availableToTake\"]", client, StringComparison.Ordinal);
    }
}
