// TigerCS.Web is referenced under an alias - see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Text.RegularExpressions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using TowerPage = TigerCsWeb::TigerCS.Web.Pages.Collections.TowerAssignmentsModel;
using WebOutcome = TigerCsWeb::TigerCS.Web.Services.Api.ApiOutcome;

namespace TigerCS.Tests.Web;

/// <summary>The page model's pure rules: the month the page opens on and how an Api refusal reads. (The page itself is plain server-rendered Razor over the Api.)</summary>
public sealed class CollectionsTowerAssignmentsPageTests
{
    [Theory]
    [InlineData(2026, 11, 2026, 11)]
    [InlineData(2027, 1, 2027, 1)]
    public void AValidMonthAndYear_AreKept(int year, int month, int expectedYear, int expectedMonth) =>
        Assert.Equal((expectedYear, expectedMonth), TowerPage.Resolve(year, month));

    [Theory]
    [InlineData(null, null)]
    [InlineData(1999, 5)]
    [InlineData(2026, 13)]
    [InlineData(2026, 0)]
    public void AMissingOrInvalidMonth_OpensOnTheCurrentDubaiMonth(int? year, int? month)
    {
        var (resolvedYear, resolvedMonth) = TowerPage.Resolve(year, month);

        Assert.InRange(resolvedMonth, 1, 12);
        Assert.InRange(resolvedYear, 2026, 2200);
    }

    [Fact]
    public void ARefusal_ReadsAsForbiddenForTheCollectionsManagerRole_NotAsAnError()
    {
        var text = TowerPage.Describe(WebOutcome.Forbidden, null);

        Assert.Contains("Collections manager", text);
    }

    private static readonly List<CollectionsTowerDto> Towers = [new(1, "140", "Tiger Tower 140", 4, true), new(2, "141", "Tiger Tower 141", 4, true), new(3, "142", "Tiger Tower 142", 4, true)];
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private static TowerAssignmentMonthDto Month(bool canManage) => new(
        2026, 11, canManage,
        [new(Alice, "Alice", true, [new(1, "140", "Tiger Tower 140", 4, "Active"), new(2, "141", "Tiger Tower 141", 4, "Draft")]), new(Bob, "Bob", true, [])],
        Towers, [3], 1);

    [Fact]
    public async Task TheManager_SeesACompactTable_WithTags_DraftMarkers_Search_AndTheMonthActions()
    {
        using var factory = CollectionsWebHost.Factory(new FakeCollectionsApi { TowerMonth = Month(canManage: true) });
        using var client = factory.CreateClient();

        var html = System.Net.WebUtility.HtmlDecode(await (await client.GetAsync("/Collections/TowerAssignments?year=2026&month=11")).Content.ReadAsStringAsync());

        Assert.Equal(2, Regex.Matches(html, "data-ta-row").Count);                       // one row per Collections employee
        Assert.Contains("140 - Tiger Tower 140", html);
        Assert.Contains("tower-tag--draft", html);                                        // a draft is marked
        Assert.Contains("Copy from previous month", html);
        Assert.Contains("Activate drafts (1)", html);
        Assert.Contains("data-ta-search", html);
        Assert.Contains("1 tower(s) without a collector", html);
        Assert.Equal(2, Regex.Matches(html, "Edit</a>").Count);
        Assert.DoesNotContain("data-ta-editor", html);                                    // nothing is being edited
    }

    [Fact]
    public async Task EditingACollector_ShowsTheSharedSearchableMultiSelect_WithSelectAll_ClearAll_AndTheirTowersTicked()
    {
        using var factory = CollectionsWebHost.Factory(new FakeCollectionsApi { TowerMonth = Month(canManage: true) });
        using var client = factory.CreateClient();

        var html = System.Net.WebUtility.HtmlDecode(await (await client.GetAsync($"/Collections/TowerAssignments?year=2026&month=11&edit={Alice}")).Content.ReadAsStringAsync());

        Assert.Contains("data-ta-editor", html);
        Assert.Contains("data-tower-multi", html);
        Assert.Contains("data-tower-search", html); Assert.Contains("data-tower-all", html); Assert.Contains("data-tower-clear", html);
        Assert.Equal(3, Regex.Matches(html, "<input type=\"checkbox\" name=\"towerId\"").Count);
        Assert.Equal(2, Regex.Matches(html, "name=\"towerId\" value=\"\\d+\" checked=\"checked\"").Count);   // Alice's two towers
        Assert.Contains("Save draft", html); Assert.Contains("Save and activate", html);
        Assert.Contains("data-ta-tags", html);                                            // removable tags are drawn here by the script
    }

    [Fact]
    public async Task ARegularCollectionsEmployee_SeesTheTableButNoEditingControls()
    {
        using var factory = CollectionsWebHost.Factory(new FakeCollectionsApi { TowerMonth = Month(canManage: false) });
        using var client = factory.CreateClient();

        var html = System.Net.WebUtility.HtmlDecode(await (await client.GetAsync($"/Collections/TowerAssignments?edit={Alice}")).Content.ReadAsStringAsync());

        Assert.Contains("View only", html);
        Assert.DoesNotContain("Copy from previous month", html);
        Assert.DoesNotContain("Edit</a>", html);
        Assert.DoesNotContain("data-ta-editor", html);                                    // even a hand-typed ?edit= opens nothing
    }
}
