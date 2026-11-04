using System.Net;
using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>
/// Collections > Receivables uses the SAME searchable multi-select tower picker as Campaigns (Pages/Shared/_TowerMultiPicker.cshtml, collections-tower-multi.js/.css).
/// No selection = All towers; the selection is carried by repeated <c>towerId</c> values, so it survives every filter change, paging link and old single-tower URL.
/// </summary>
public sealed class ReceivablesTowerMultiSelectRenderTests
{
    private static readonly List<TigerCS.Application.Modules.Collections.Dto.CollectionsTowerDto> Catalogue =
        [new(7, "124", "Tower 124", 4, true), new(8, "127", "Faradis", 32, true), new(9, "136", "Tower 136", 4, true)];

    private static FakeCollectionsApi Api() => new() { TowerCatalogue = Catalogue };

    private static async Task<string> Shell(HttpClient client, string query) => await (await client.GetAsync("/Collections/Receivables" + query)).Content.ReadAsStringAsync();

    private static string Summary(string html) => Regex.Match(html, "<summary[^>]*data-tower-summary[^>]*>(.*?)</summary>", RegexOptions.Singleline).Groups[1].Value.Trim();
    private static List<int> Checked(string html) =>
        Regex.Matches(html, "<input type=\"checkbox\" name=\"towerId\" value=\"(\\d+)\" checked=\"checked\" />").Select(m => int.Parse(m.Groups[1].Value)).Order().ToList();

    [Fact]
    public async Task TheReceivablesPage_ReusesTheSharedMultiPicker_NotTheOldSingleSelect()
    {
        using var factory = CollectionsWebHost.Factory(Api()); using var client = factory.CreateClient();
        var html = await Shell(client, "");
        Assert.Contains("data-tower-multi", html);
        Assert.Contains("data-tower-search", html); Assert.Contains("data-tower-all", html); Assert.Contains("data-tower-clear", html);   // search, Select all, Clear all
        Assert.Equal(3, Regex.Matches(html, "<input type=\"checkbox\" name=\"towerId\"").Count);
        Assert.DoesNotContain("data-tower-picker", html); Assert.DoesNotContain("data-tower-select", html); Assert.DoesNotContain("collections-towers.js", html);
        Assert.Contains("collections-tower-multi.js", html); Assert.Contains("collections-tower-multi.css", html);
        Assert.Equal("All towers", Summary(html));                                    // default
        Assert.Empty(Checked(html));
    }

    [Theory]
    [InlineData("", "All towers", new int[0])]
    [InlineData("?towerId=7", "1 tower selected", new[] { 7 })]                       // an old bookmark with one tower
    [InlineData("?towerId=7&towerId=9", "2 towers selected", new[] { 7, 9 })]
    [InlineData("?towerId=9&towerId=7&towerId=7", "2 towers selected", new[] { 7, 9 })]   // order and duplicates do not matter
    [InlineData("?towerId=abc&towerId=&towerId=-3&towerId=8", "1 tower selected", new[] { 8 })]   // junk is ignored
    public async Task TheSelectionIsRestoredFromTheUrl_WithItsCount(string query, string summary, int[] ticked)
    {
        using var factory = CollectionsWebHost.Factory(Api()); using var client = factory.CreateClient();
        var html = await Shell(client, query);
        Assert.Equal(summary, Summary(html));
        Assert.Equal(ticked, Checked(html));
    }

    [Fact]
    public async Task AnIdOutsideTheCatalogue_StaysVisibleAndTicked_SoItCanBeRemoved()
    {
        using var factory = CollectionsWebHost.Factory(Api()); using var client = factory.CreateClient();
        var html = await Shell(client, "?towerId=7&towerId=404");
        Assert.Equal([7, 404], Checked(html));
        Assert.Contains("Tower #404 (not in the list)", html);
    }

    [Fact]
    public async Task ASingleTower_TravelsAsTowerId_SeveralAsTowerIds_AndEveryTowerOrNoneAsNoFilter()
    {
        var api = Api();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Receivables?handler=Results&towerId=7");
        await client.GetAsync("/Collections/Receivables?handler=Results&towerId=9&towerId=7");
        await client.GetAsync("/Collections/Receivables?handler=Results&towerId=7&towerId=8&towerId=9");     // every tower of the catalogue = All towers
        await client.GetAsync("/Collections/Receivables?handler=Results");
        var calls = api.Calls("/receivables/instalments").Select(Uri.UnescapeDataString).ToList();
        Assert.Equal(4, calls.Count);                                                                          // ONE data call per request, never one per tower
        Assert.Contains("towerId=7", calls[0]); Assert.DoesNotContain("towerIds", calls[0]);
        Assert.Contains("towerIds=7", calls[1]); Assert.Contains("towerIds=9", calls[1]); Assert.DoesNotContain("towerId=", calls[1]);
        Assert.DoesNotContain("towerId", calls[2]);
        Assert.DoesNotContain("towerId", calls[3]);
    }

    [Fact]
    public async Task EveryFilterAndPagingLink_KeepsTheSelectedTowers()
    {
        var api = Api();
        for (var n = 0; n < 30; n++) api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 9, 1 + n % 20), 300m, "INV-" + n, "TP124-" + (1000 + n), 200 + n, "Customer " + n, "T" + n));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results&towerId=7&towerId=9&status=overdue&month=9&year=2026&minTotal=250&search=Customer")).Content.ReadAsStringAsync();
        var links = Regex.Matches(html, "data-results-link href=\"([^\"]+)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(links);                                                                                // 30 units = 2 pages
        Assert.All(links, link =>
        {
            Assert.Contains("towerId=7", link); Assert.Contains("towerId=9", link);
            foreach (var kept in new[] { "status=overdue", "month=9", "year=2026", "minTotal=250", "search=Customer" }) Assert.Contains(kept, link);
        });
        // The no-JavaScript fallback link and the form carry the selection too.
        var shell = await Shell(client, "?towerId=7&towerId=9&status=due&minTotal=");
        Assert.Equal([7, 9], Checked(shell));
        Assert.Contains("Open the full page without JavaScript", shell);
        Assert.Matches("href=\"[^\"]*towerId=7&amp;towerId=9[^\"]*render=full", shell);
    }

    [Fact]
    public async Task TheFullPageWithoutJavaScript_FiltersByTheSelectedTowers_AndShowsTheirUnits()
    {
        var api = Api();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await Shell(client, "?towerId=7&towerId=8&render=full");
        Assert.Equal("2 towers selected", Summary(html));
        Assert.Contains("TP124-1001", html);
        Assert.Contains("towerIds=7", Assert.Single(api.Calls("/receivables/instalments")));
    }

    [Fact]
    public async Task AnInvalidTowerSelection_IsReportedAsAFilterError_NotAnEmptyOrBrokenPage()
    {
        var api = Api(); api.Status = HttpStatusCode.BadRequest;
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.GetAsync("/Collections/Receivables?handler=Results&towerId=7&towerId=404");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-unit-row", html);
        Assert.Contains("filters are invalid", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothPagesShareOneStylesheet_WithAPhoneLayout()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src/TigerCS.Web/wwwroot/css/collections-tower-multi.css"));
        Assert.Contains(".tower-multi__panel", css);
        Assert.Matches(@"@media \(max-width: 640px\)[^}]*\.tower-multi", css.Replace("\n", " "));              // the dropdown has its own phone rules
        Assert.DoesNotContain("tower-multi", File.ReadAllText(Path.Combine(RepoRoot(), "src/TigerCS.Web/wwwroot/css/collections-campaigns.css")));   // not duplicated
        foreach (var page in new[] { "Receivables", "Campaigns" })
            Assert.Contains("collections-tower-multi.css", File.ReadAllText(Path.Combine(RepoRoot(), $"src/TigerCS.Web/Pages/Collections/{page}.cshtml")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
