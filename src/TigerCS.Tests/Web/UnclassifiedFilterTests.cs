// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>The Tickets page's Unclassified filter: forwarded to the same queue endpoint (which applies department visibility) and surfaced as a control.</summary>
public sealed class UnclassifiedFilterTests
{
    private static string Read(string relativeToSrc, [CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..")), relativeToSrc));

    [Theory]
    [InlineData(true, true)]
    [InlineData(null, false)]
    public async Task TheClientForwardsTheFilter_OnlyWhenItIsOn(bool? unclassified, bool expected)
    {
        string? seen = null;
        var handler = new FakeApiHandler((request, _) =>
        {
            seen = request.RequestUri!.Query;
            return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new TicketListResultDto([], 0, 1, 20));
        });
        var client = new TicketsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<TicketsApiClient>.Instance);

        await client.GetQueueAsync(new TicketListRequestDto(
            null, null, null, null, null, null, null, null, null, 1, 20, Unclassified: unclassified), CancellationToken.None);

        Assert.Equal(expected, seen!.Contains("unclassified=true", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTicketsPage_HasAnUnclassifiedControl_ThatSurvivesPagingAndTheRememberedView()
    {
        var list = Read("TigerCS.Web/Pages/Shared/_TicketListView.cshtml");
        var page = Read("TigerCS.Web/Pages/Tickets.cshtml.cs");
        var views = Read("TigerCS.Web/Models/TicketsView.cs");

        Assert.Contains("name=\"unclassified\"", list);
        Assert.Contains("[\"unclassified\"] = Model.Unclassified ? \"true\" : null", list);
        Assert.Contains("Unclassified ? true : null", page);
        Assert.Contains("\"unclassified\"", views);
    }
}
