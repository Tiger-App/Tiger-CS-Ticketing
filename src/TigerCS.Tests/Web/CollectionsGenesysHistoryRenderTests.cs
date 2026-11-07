extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using TigerCS.Application.Modules.Collections.History;

namespace TigerCS.Tests.Web;

/// <summary>The Genesys Campaign History pages, rendered by the real Web host over a fake API: compact, read only, never claiming a call or delivery.</summary>
public sealed class CollectionsGenesysHistoryRenderTests
{
    private static readonly DateTime Approved = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);          // 12:00 Dubai
    private static readonly Guid Id1 = Guid.Parse("11111111-2222-3333-4444-555555555501"), Id2 = Guid.Parse("11111111-2222-3333-4444-555555555502");

    private static GenesysSubmissionDto Sub(Guid id, string status, string stage = "CurrentMonth", int records = 3, decimal amount = 306m, string mode = "Manual", string by = "Mona Manager", string? error = null,
        string? reference = null, DateTime? submitted = null, string basis = "Submitted") => new(
        id, Approved, submitted ?? Approved.AddSeconds(8), stage, stage == "CurrentMonth" ? "Current Month" : "Overdue", CampaignStage(stage), mode, stage == "CurrentMonth" ? "Current Month list" : "Overdue list",
        "79e5ae74-ea6e-4941-b76d-45ddf487d8d1", Guid.NewGuid(), by, records, basis, amount, "AED", 0, status, status == "NotSent" ? "Not sent" : status, "Explained: " + status, error, reference,
        status == "Uploaded" ? records : 0, status == "Failed" ? records : 0, status == "Unknown" ? records : 0, 0, "Call outcome unavailable");

    private static string CampaignStage(string stage) => stage == "CurrentMonth" ? "Current month reminder - day 14" : "Overdue reminder - day 1";

    private static GenesysHistoryOptionsDto Options() => new(
        [new("CurrentMonth", "Current month reminder - day 14"), new("Overdue", "Overdue reminder - day 1")], [new("CurrentMonth", "Current Month list"), new("Overdue", "Overdue list")],
        GenesysUploadStates.All.Select(s => new GenesysFilterOptionDto(s, GenesysUploadStates.Label(s))).ToList(), [new("Manual", "Manual"), new("Auto", "Auto")],
        [new(Guid.Parse("6f1d2a40-8f0e-4c7b-9b57-0a1f3c2d4e5f").ToString("D"), "Mona Manager")]);

    private static GenesysHistoryPageDto Page(IReadOnlyList<GenesysSubmissionDto> items, int total = -1, int page = 1, bool canExports = true) => new(page, 25, total < 0 ? items.Count : total, items,
        new(items.Count, items.Sum(i => i.UnitRecords), items.Sum(i => i.TotalAmount), "AED", items.Count(i => i.Status == "Uploaded"), items.Count(i => i.Status is "Failed" or "Partial" or "Unknown"), 0,
            GenesysUploadStates.All.ToDictionary(s => s, s => items.Count(i => i.Status == s)), items.Sum(i => i.UploadedRecords)),
        Options(), GenesysUploadStates.CallOutcomeNote, canExports);

    private static GenesysSubmissionRecordDto Rec(long id, string customer, string unit, string phone, string result = "Uploaded", int? crm = 7001, bool shared = false, string? error = null, decimal amount = 100m) => new(
        id, customer, crm, unit, phone, customer.Replace(' ', '.').ToLowerInvariant() + "@example.test", "CurrentMonth", "Current Month", amount, "AED", new DateOnly(2026, 10, 20), result, result.ToLowerInvariant().Replace(' ', '-').Split('-')[0] switch
        { "uploaded" => "uploaded", "failed" => "failed", "unknown" => "unknown", _ => "notsent" }, result == "Uploaded" ? Approved.AddSeconds(20) : null, Approved.AddSeconds(8), result == "Uploaded" ? "contact-" + id : null, error, shared, null, "Call outcome unavailable");

    private sealed class Api(FakeCollectionsApi inner) : DelegatingHandler(inner)
    {
        public GenesysHistoryPageDto Submissions { get; set; } = Page([]);
        public GenesysExportHistoryPageDto Exports { get; set; } = new(1, 25, 0, [], GenesysHistoryService.ExportNotice);
        public GenesysSubmissionDetailDto? Detail { get; set; }
        public GenesysRecordsPageDto Records { get; set; } = new(1, 50, 0, [], GenesysUploadStates.CallOutcomeNote);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/genesys-history/")) return base.SendAsync(request, cancellationToken);
            Calls.Add(request.Method + " " + request.RequestUri.PathAndQuery);
            HttpResponseMessage Json(object? value) => new(Status) { Content = JsonContent.Create(value) };
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            if (path.EndsWith("/submissions")) return Task.FromResult(Json(Submissions));
            if (path.EndsWith("/exports")) return Task.FromResult(Json(Exports));
            if (path.EndsWith("/records")) return Task.FromResult(Json(Records));
            return Task.FromResult(Detail is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Detail));
        }
    }

    private static Api NewApi() => new(new FakeCollectionsApi());

    private static async Task<(HttpStatusCode Status, string Html)> GetAsync(Api api, string url)
    {
        using var factory = CollectionsWebHost.Factory(api);
        using var client = factory.CreateClient();
        var response = await client.GetAsync(url);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The page's own content (the shared layout carries the sign-out form).</summary>
    private static string Content(string html) => html[html.IndexOf("history-page", StringComparison.Ordinal)..];

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Replace(" ", " ");

    // ------------------------------------------------------------ the history table

    [Fact]
    public async Task TheTable_ShowsEveryColumn_StatusWords_AndTheCallOutcomeNote()
    {
        var api = NewApi();
        api.Submissions = Page([
            Sub(Id1, "Uploaded", reference: "camp-123"),
            Sub(Id2, "Failed", "Overdue", 2, 200m, "Auto", "Automatic (scheduler)", error: "Genesys rejected the request (HTTP 400); nothing was created.")]);
        var (status, html) = await GetAsync(api, "/Collections/CampaignHistory");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["GET /api/collections/genesys-history/submissions?page=1&pageSize=25"], api.Calls);
        foreach (var header in new[] { "Submitted (Dubai)", "Campaign stage", "Mode", "Destination list", "Submitted by", "Unit records", "Amount (AED)", "Upload status", "Genesys reference" })
            Assert.Contains(header, html);
        var text = Text(html);
        Assert.Contains("14 Oct 2026 12:00 (Dubai)", text);
        Assert.Contains("Current month reminder - day 14", text);
        Assert.Contains("Overdue reminder - day 1", text);
        Assert.Contains("Automatic (scheduler)", text);
        Assert.Contains("camp-123", text);
        Assert.Contains("Not provided by Genesys", text);
        Assert.Contains("306.00", text);
        Assert.Matches(new Regex(@"data-status=""Uploaded""[\s\S]*?data-status-badge>Uploaded<"), html);
        Assert.Matches(new Regex(@"data-status=""Failed""[\s\S]*?data-status-badge>Failed<"), html);
        Assert.Contains("Explained: Failed", text);                                                       // the status explanation is on the row
        Assert.Contains("data-call-outcome-note", html);
        Assert.Contains("does not mean the customer was called, contacted or reached", text);
        Assert.Equal(2, Regex.Matches(html, "data-view-details").Count);
        Assert.Contains($"href=\"/Collections/CampaignHistory/{Id1:D}\"", html);
        // Summary cards: submissions, records, amount, confirmed records, attention.
        Assert.Matches(new Regex(@"data-card=""submissions"">2<"), html);
        Assert.Matches(new Regex(@"data-card=""records"">5<"), html);
        Assert.Matches(new Regex(@"data-card=""amount"">506\.00<"), html);
        Assert.Matches(new Regex(@"data-card=""confirmed"">3<"), html);
        Assert.Matches(new Regex(@"data-card=""attention"">1<"), html);
    }

    [Fact]
    public async Task TheHistoryNeverCallsAnythingContacted_AndOffersNoResendOrAnyPostForm()
    {
        var api = NewApi();
        api.Submissions = Page([Sub(Id1, "Uploaded"), Sub(Id2, "Unknown")]);
        var (_, html) = await GetAsync(api, "/Collections/CampaignHistory");
        var text = Text(html);

        Assert.DoesNotMatch(new Regex("<form[^>]*method=\"post\"", RegexOptions.IgnoreCase), Content(html));   // only GET filter forms
        foreach (var word in new[] { "Resend", "Retry", "Re-send", "Upload again", "Send again" }) Assert.DoesNotContain(word, Text(Content(html)), StringComparison.OrdinalIgnoreCase);
        foreach (var word in new[] { "Called", "Contacted", "Delivered" })
            Assert.DoesNotMatch(new Regex($@"data-status-badge>{word}<"), html);
        Assert.DoesNotContain("<button", Regex.Replace(Content(html), @"<button class=""btn btn-gold"" type=""submit"">Apply</button>", ""));   // the only button is Apply
        Assert.All(api.Calls, c => Assert.StartsWith("GET ", c));
    }

    [Fact]
    public async Task AnEmptyHistory_SaysNothingHasBeenSubmitted_AndAFilterThatMatchesNothingSaysSo()
    {
        var api = NewApi();
        var (_, html) = await GetAsync(api, "/Collections/CampaignHistory");
        Assert.Contains("data-history-empty", html);
        Assert.Contains("No campaign has been submitted to Genesys yet.", Text(html));
        Assert.DoesNotContain("data-history-table", html);

        var (_, filtered) = await GetAsync(api, "/Collections/CampaignHistory?status=Failed");
        Assert.Contains("No submissions match these filters.", Text(filtered));
        Assert.Contains("Clear filters", Text(filtered));
    }

    [Fact]
    public async Task TheFilters_AreSentToTheApi_KeptInTheForm_AndKeptByThePagingLinks()
    {
        var api = NewApi();
        api.Submissions = Page([Sub(Id1, "Uploaded")], total: 60, page: 2);
        var (_, html) = await GetAsync(api, "/Collections/CampaignHistory?from=2026-10-01&to=2026-10-31&stage=Overdue&status=Failed&mode=Auto&list=Overdue&search=TP140&pageNumber=2");

        var call = Assert.Single(api.Calls);
        foreach (var part in new[] { "from=2026-10-01", "to=2026-10-31", "stage=Overdue", "status=Failed", "mode=Auto", "list=Overdue", "search=TP140", "page=2" }) Assert.Contains(part, call);
        Assert.Contains("value=\"TP140\"", html);
        Assert.Contains("value=\"2026-10-01\"", html);
        Assert.Matches(new Regex(@"<option value=""Failed"" selected"), html);
        Assert.Matches(new Regex(@"<option value=""Auto"" selected"), html);
        Assert.Contains("Page 2 of 3", Text(html));
        var previous = WebUtility.HtmlDecode(Regex.Match(html, @"<a class=""btn btn-sm"" href=""([^""]+)"">Previous</a>").Groups[1].Value);
        var next = WebUtility.HtmlDecode(Regex.Match(html, @"<a class=""btn btn-sm"" href=""([^""]+)"">Next</a>").Groups[1].Value);
        Assert.Contains("status=Failed", previous); Assert.Contains("stage=Overdue", previous); Assert.DoesNotContain("pageNumber", previous);
        Assert.Contains("status=Failed", next); Assert.Contains("pageNumber=3", next);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "does not have permission")]
    [InlineData(HttpStatusCode.Unauthorized, "session has expired")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "could not be loaded")]
    public async Task AFailedLoad_ShowsAnExplanation_NotAnEmptyHistory(HttpStatusCode code, string expected)
    {
        var api = NewApi();
        api.Status = code;
        var (status, html) = await GetAsync(api, "/Collections/CampaignHistory");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("data-history-error", html);
        Assert.Contains(expected, Text(html));
        Assert.DoesNotContain("No campaign has been submitted", html);
    }

    // ------------------------------------------------------------ export history

    [Fact]
    public async Task TheExportHistory_IsASeparateTab_AndSaysADownloadIsNotAnUpload()
    {
        var api = NewApi();
        api.Submissions = Page([Sub(Id1, "Uploaded")]);
        api.Exports = new(1, 25, 1, [new(5, Approved, Guid.NewGuid(), "Mona Manager", "Overdue", "Overdue", "FollowUp", "Manual", 12, 14, 2, 1234.5m, "AED", false, true, 3, "abcdef012345")], GenesysHistoryService.ExportNotice);
        var (_, tab) = await GetAsync(api, "/Collections/CampaignHistory?tab=exports");
        var text = Text(tab);

        Assert.Equal(["GET /api/collections/genesys-history/exports?page=1&pageSize=25"], api.Calls);       // the submissions are not even asked for
        Assert.Contains("data-export-notice", tab);
        Assert.Contains("A download is not an upload", text);
        Assert.Contains("not counted in the submission history", text);
        Assert.Contains("Mona Manager", text); Assert.Contains("1,234.50", text); Assert.Contains("Financial data not verified", text); Assert.Contains("abcdef012345", text);
        Assert.DoesNotContain("data-history-table", tab);
        Assert.DoesNotContain("data-card=", tab);                                                           // no upload statistics on the downloads tab
        Assert.Matches(new Regex(@"data-history-tab=""exports""[^>]*>Export history<"), tab);
        Assert.Matches(new Regex(@"is-active"" href=""[^""]*tab=exports"), tab);

        var (_, main) = await GetAsync(api, "/Collections/CampaignHistory");
        Assert.DoesNotContain("abcdef012345", main);                                                        // the download is not in the submissions
        Assert.Contains("data-history-table", main);
    }

    [Fact]
    public async Task TheExportHistory_ExplainsAMissingRight_AndAnEmptyList()
    {
        var api = NewApi();
        var (_, empty) = await GetAsync(api, "/Collections/CampaignHistory?tab=exports");
        Assert.Contains("No file has been downloaded with Export for Genesys yet.", Text(empty));
        api.Status = HttpStatusCode.Forbidden;
        var (_, forbidden) = await GetAsync(api, "/Collections/CampaignHistory?tab=exports");
        Assert.Contains("needs the same right as Export for Genesys", Text(forbidden));
    }

    // ------------------------------------------------------------ the details page

    private static GenesysSubmissionDetailDto Detail(GenesysSubmissionDto s, int shared = 0, bool batchError = false) => new(s,
        [new(1, "CurrentMonth", "Current month reminder - day 14", "79e5ae74-ea6e-4941-b76d-45ddf487d8d1", "Current Month list", batchError ? "Failed" : "Uploaded", batchError ? "Failed" : "Uploaded", 3, batchError ? 0 : 3,
            batchError ? 400 : 200, 1, Approved.AddSeconds(8), Approved.AddSeconds(20), "Current month reminder - day 14", "camp-123", "ReusedExisting", "0123456789ab", batchError ? "Genesys rejected the request (HTTP 400); nothing was created." : null, null, null)],
        s.UnitRecords, 0, shared, null, "Finished", Approved.AddSeconds(8), Approved.AddSeconds(30), true, GenesysUploadStates.CallOutcomeNote);

    [Fact]
    public async Task TheDetails_ListEveryRecord_PreserveSharedPhones_AndNeverShowACallResult()
    {
        var api = NewApi();
        var s = Sub(Id1, "Uploaded", reference: "camp-123");
        api.Detail = Detail(s, shared: 2);
        api.Records = new(1, 50, 4, [
            Rec(1, "Ahmed Ali", "TP140-1", "+971500000099", shared: true, amount: 101m), Rec(2, "Sara Khan", "TP140-2", "+971500000099", shared: true, crm: null, amount: 102m),
            Rec(3, "Omar Said", "TP140-3", "+971500000003", "Failed", error: "Genesys rejected the request (HTTP 400)."), Rec(4, "Layla Nour", "TP140-4", "+971500000004", "Excluded before upload", crm: null, error: "Paid or settled: no qualifying balance remains")],
            GenesysUploadStates.CallOutcomeNote);
        var (status, html) = await GetAsync(api, $"/Collections/CampaignHistory/{Id1:D}");
        var text = Text(html);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, Regex.Matches(text, @"\+971500000099").Count);                                    // the same phone on two units: two rows, never one
        Assert.Equal(4, Regex.Matches(html, "data-record=").Count);
        foreach (var column in new[] { "Customer", "CRM customer ID", "Unit", "Phone", "Email", "Reminder", "Amount due (AED)", "Due date", "Upload result", "Submitted (Dubai)" }) Assert.Contains(column, html);
        Assert.Contains("TP140-1", text); Assert.Contains("TP140-2", text); Assert.Contains("ahmed.ali@example.test", text); Assert.Contains("7001", text); Assert.Contains("20 Oct 2026", text);
        Assert.Equal(2, Regex.Matches(html, @"title=""Another unit in this submission").Count);
        Assert.Contains("(each unit is its own record)", text);
        Assert.Contains("14 Oct 2026 12:00 (Dubai)", text);                                               // the exact submission timestamp
        Assert.Contains("Genesys rejected the request (HTTP 400).", text);                                // the record's error
        Assert.Contains("Paid or settled", text);
        Assert.Equal(4, Regex.Matches(html, @"data-call-outcome>Call outcome unavailable<").Count);        // on every record
        Assert.DoesNotMatch(new Regex(@"data-record-result>(Called|Contacted|Delivered|Reached)<"), html);
        Assert.Contains("camp-123", text);
        Assert.Contains("data-batches", html);
                Assert.Equal([$"GET /api/collections/genesys-history/submissions/{Id1:D}", $"GET /api/collections/genesys-history/submissions/{Id1:D}/records?page=1&pageSize=50"], api.Calls);
        Assert.DoesNotMatch(new Regex("<form[^>]*method=\"post\"", RegexOptions.IgnoreCase), Content(html));
        foreach (var word in new[] { "Resend", "Retry", "Send again" }) Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDetails_ShowTheSubmissionError_TheBatchResult_AndTheStatusExplanation()
    {
        var api = NewApi();
        var s = Sub(Id1, "Failed", error: "Genesys rejected the request (HTTP 400); nothing was created.");
        api.Detail = Detail(s, batchError: true);
        api.Records = new(1, 50, 1, [Rec(1, "Ahmed Ali", "TP140-1", "+971500000001", "Failed", error: "Genesys rejected the request (HTTP 400); nothing was created.")], GenesysUploadStates.CallOutcomeNote);
        var (_, html) = await GetAsync(api, $"/Collections/CampaignHistory/{Id1:D}");
        var text = Text(html);

        Assert.Contains("data-submission-error", html);
        Assert.Contains("data-batch-error", html);
        Assert.Contains("Explained: Failed", text);
        Assert.Matches(new Regex(@"data-status-badge>Failed<"), html);
        Assert.Contains("HTTP", text);
    }

    [Fact]
    public async Task TheDetails_PageTheRecords_AndKeepTheRecordFilters()
    {
        var api = NewApi();
        api.Detail = Detail(Sub(Id1, "Uploaded"));
        api.Records = new(2, 50, 120, [Rec(51, "Ahmed Ali", "TP140-51", "+971500000051")], GenesysUploadStates.CallOutcomeNote);
        var (_, html) = await GetAsync(api, $"/Collections/CampaignHistory/{Id1:D}?pageNumber=2&result=Uploaded&search=TP140");

        Assert.Contains("result=Uploaded&page=2", api.Calls.Last().Replace("&search=TP140", ""));
        Assert.Contains("Page 2 of 3", Text(html));
        Assert.Contains("value=\"TP140\"", html);
        Assert.Matches(new Regex(@"<option value=""Uploaded"" selected"), html);
        var next = WebUtility.HtmlDecode(Regex.Match(html, @"<a class=""btn btn-sm"" href=""([^""]+)"">Next</a>").Groups[1].Value);
        Assert.Contains("result=Uploaded", next); Assert.Contains("pageNumber=3", next);
    }

    [Fact]
    public async Task AnUnknownSubmission_IsNotFound_AndAFailedLoadIsExplained()
    {
        var api = NewApi();
        var (missing, _) = await GetAsync(api, $"/Collections/CampaignHistory/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        api.Status = HttpStatusCode.Forbidden;
        var (status, html) = await GetAsync(api, $"/Collections/CampaignHistory/{Id1:D}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("does not have permission", Text(html));
    }

    // ------------------------------------------------------------ navigation

    [Fact]
    public async Task TheHistory_IsReachableFromTheCollectionsPages_AndKeepsCollectionsHighlighted()
    {
        var api = NewApi();
        foreach (var page in new[] { "/Collections/Receivables", "/Collections/Campaigns", "/Collections/Reconciliation" })
        {
            var (_, html) = await GetAsync(api, page);
            Assert.Matches(new Regex(@"href=""/Collections/CampaignHistory""[^>]*data-history-link>Genesys Campaign History<"), html);
        }
        var (_, history) = await GetAsync(api, "/Collections/CampaignHistory");
        Assert.Contains("<title>Genesys Campaign History", history.Replace("\r", ""));
        Assert.Matches(new Regex(@"app-nav__link is-active"" href=""/Collections/Receivables"""), history);
    }
}
