extern alias TigerCsWeb;

using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Floor multi-select on Collections Campaigns (inside "More filters", next to Minimum Total) and Receivables: it defaults to All Floors, keeps the chosen floors through preview, paging, exports and the send
/// forms, reaches the API as repeated <c>floors</c>, and reloads its options for the chosen towers.
/// </summary>
public sealed class CollectionsFloorFilterRenderTests
{
    private const string Filters = "stage=CurrentMonthReminder&towerId=7&towerId=8&floor=1&floor=19&businessDate=2026-10-14&dateFrom=2026-09-01&dateTo=2026-10-31&minTotal=250&paymentStatus=Due";

    [Fact]
    public async Task Campaigns_ShowsTheFloorPickerInMoreFilters_NextToMinimumTotal_DefaultingToAllFloors()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder")).Content.ReadAsStringAsync();
        var advanced = html[html.IndexOf("data-advanced-filters", StringComparison.Ordinal)..html.IndexOf("</details>\n            <span class=\"receivables-loading\"", StringComparison.Ordinal)];
        Assert.True(advanced.IndexOf("campaignMin", StringComparison.Ordinal) < advanced.IndexOf("campaignFloor-label", StringComparison.Ordinal));   // after Minimum Total, inside More filters
        Assert.Contains(">All Floors</summary>", html);
        Assert.Contains("data-floors-url=\"/Collections/Campaigns\"", html);
        Assert.DoesNotMatch("name=\"floor\" value=\"\\d+\" checked", html);                                // nothing ticked: no filtering
        foreach (var label in new[] { "Ground floor", "Floor 1", "Floor 2", "Floor 19" }) Assert.Contains(label, html);
        Assert.Contains("Search floors", html);
        Assert.Single(api.Calls("/receivables/floors"));
        Assert.DoesNotContain("handler=Results", string.Join(' ', api.Requests));                          // the shell still loads no customers
    }

    [Fact]
    public async Task ChosenFloors_ReachThePreviewAndTheExports_AsRepeatedFloorsParameters_AndStayTickedInMoreFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var shell = await (await client.GetAsync($"/Collections/Campaigns?{Filters}")).Content.ReadAsStringAsync();
        Assert.Contains("<input type=\"checkbox\" name=\"floor\" value=\"1\" checked=\"checked\" />", shell);
        Assert.Contains("<input type=\"checkbox\" name=\"floor\" value=\"19\" checked=\"checked\" />", shell);
        Assert.DoesNotContain("name=\"floor\" value=\"2\" checked", shell);
        Assert.Contains(">2 floors selected</summary>", shell);
        Assert.Contains("open=\"open\"", shell[shell.IndexOf("campaign-advanced", StringComparison.Ordinal)..][..200]);   // an applied floor filter is never hidden in a closed section
        var floorCall = Assert.Single(api.Calls("/receivables/floors"));
        Assert.Contains("towerIds=7", floorCall); Assert.Contains("towerIds=8", floorCall);                  // the options are those of the chosen towers

        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&{Filters}")).Content.ReadAsStringAsync();
        var preview = Assert.Single(api.Calls("/campaigns/preview"));
        Assert.Contains("floors=1", preview); Assert.Contains("floors=19", preview);
        var link = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]*handler=Export[^\"]*mode=review[^\"]*)\"").Groups[1].Value);
        Assert.Contains("floor=1", link); Assert.Contains("floor=19", link);
        await client.GetAsync(link);
        var export = Assert.Single(api.Calls("/campaigns/export"));
        string[] Filter(string call) => call.Split('?')[1].Split('&').Where(p => !p.StartsWith("page=", StringComparison.Ordinal) && !p.StartsWith("mode=", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(Filter(preview), Filter(export));                                                       // the file holds exactly the previewed floors
    }

    [Fact]
    public async Task WithoutAFloorFilter_NoFloorsParameterIsSent_SoExistingCallsAreUnchanged()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14&towerId=7");
        Assert.DoesNotContain("floors=", Assert.Single(api.Calls("/campaigns/preview")));
    }

    [Fact]
    public async Task TheFloorsHandler_ReturnsTheFloorsOfTheChosenTowers_AsJson()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var json = await (await client.GetAsync("/Collections/Campaigns?handler=Floors&towerId=7&towerId=8")).Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("failed").GetBoolean());
        var floors = doc.RootElement.GetProperty("floors").EnumerateArray().Select(f => (f.GetProperty("value").GetInt32(), f.GetProperty("label").GetString())).ToArray();
        Assert.Equal([(0, "Ground floor"), (1, "Floor 1"), (2, "Floor 2"), (19, "Floor 19")], floors);
        Assert.Contains("towerIds=7", Assert.Single(api.Calls("/receivables/floors"))); 
    }

    [Fact]
    public async Task ApprovalAndExportForms_CarryTheFloorsAsHiddenFields_SoTheSendMatchesThePreview()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&{Filters}&sendingMode=Manual")).Content.ReadAsStringAsync();
        Assert.Contains("<input type=\"hidden\" name=\"floor\" value=\"1\" />", html);
        Assert.Contains("<input type=\"hidden\" name=\"floor\" value=\"19\" />", html);
    }

    [Fact]
    public async Task Receivables_ShowsTheFloorPickerNextToMinimumTotal_AndSendsTheChosenFloors()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var shell = await (await client.GetAsync("/Collections/Receivables?towerId=7&floor=2")).Content.ReadAsStringAsync();
        Assert.True(shell.IndexOf("receivableMin", StringComparison.Ordinal) < shell.IndexOf("receivableFloor-label", StringComparison.Ordinal));
        Assert.Contains("<input type=\"checkbox\" name=\"floor\" value=\"2\" checked=\"checked\" />", shell);
        Assert.Contains("data-floors-url=\"/Collections/Receivables\"", shell);
        await client.GetAsync("/Collections/Receivables?handler=Results&towerId=7&floor=2&floor=1&status=due");
        var call = Assert.Single(api.Calls("/receivables/instalments"));
        Assert.Contains("floors=1", call); Assert.Contains("floors=2", call); Assert.Contains("view=units", call);
        api.Requests.Clear();
        await client.GetAsync("/Collections/Receivables?handler=Results&towerId=7");
        Assert.DoesNotContain("floors=", Assert.Single(api.Calls("/receivables/instalments")));
    }

    [Fact]
    public async Task JunkInTheFloorQuery_IsIgnored_NeverBecomesAFilter()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14&floor=abc&floor=-3&floor=500&floor=");
        Assert.DoesNotContain("floors=", Assert.Single(api.Calls("/campaigns/preview")));
    }
}
