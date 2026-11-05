extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Direct Send sending mode of Collections Campaigns (next to Manual and Auto): no campaign stage, no stage dates, customers picked by the user, a confirmation with the exact count,
/// and every held-back customer shown with its exact reason. Manual and Auto keep their established behaviour (see the last tests).
/// </summary>
public sealed class CollectionsCampaignDirectSendRenderTests
{
    private static readonly DateTime Read = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly DubaiToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));
    private const string DirectList = "6d2b9a0e-5c1f-4c7e-9a43-1b8f0e2d7a55";
    private const string Url = "/Collections/Campaigns?render=full&sendingMode=DirectSend";
    private static readonly Guid DispatchId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static ReviewRunDto Run(bool reconciled = true, bool stale = false, DateOnly? asOf = null) => new(7, "Completed", "Completed", 100, asOf ?? DubaiToday, null,
        new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), "PACT receivables (companies 4 and 32)", "", reconciled, Read, Read, Read, Read, 1234, 56, null, true, stale);

    private static ReviewRecordDto Row(int n, string status, params string[] reasons) => new($"RK{n}", "DirectSend", 4, $"Direct Customer {n}", $"+9715000000{n:D2}", $"d{n}@example.test", "TP140", $"TP140-{n}",
        "Unpaid", 500m * n, "AED", new DateOnly(2026, 9, 20), status, reasons.Select(r => new ReviewReasonDto(r, "Exclusion", ReviewReasons.Explain(r))).ToList(),
        "PACT", Read, null, null, "140", "Tower 140", $"300{n}");

    private sealed class Api(FakeCollectionsApi inner) : DelegatingHandler(inner)
    {
        public ReviewRunDto? Run { get; set; } = CollectionsCampaignDirectSendRenderTests.Run();
        public List<ReviewRecordDto> Records { get; set; } =
        [
            Row(1, "Ready"), Row(2, "Ready"),
            Row(3, "Excluded", ReviewReasons.SharedPhoneMultipleUnits),
            Row(4, "Excluded", ReviewReasons.LegalStageCustomer),
            Row(5, "NeedsReview", ReviewReasons.NoValidContact)
        ];
        public List<string> DirectSendBlockers { get; } = [];
        public List<string> Blockers { get; } = [];
        public string? LastSummaryBody { get; private set; }
        public string? LastConfirmBody { get; private set; }
        public List<string> RecordQueries { get; } = [];
        public List<string> All { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            All.Add(request.Method + " " + request.RequestUri.PathAndQuery);
            HttpResponseMessage Json(object? value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
            if (path.EndsWith("/runs/current")) return Json(Run);
            if (path.EndsWith("/review/records"))
            {
                RecordQueries.Add(request.RequestUri.Query);
                var ready = Records.Count(r => r.ValidationStatus == "Ready");
                var counts = new ReviewCountsDto(Records.Count, ready, Records.Count(r => r.ValidationStatus == "NeedsReview"), Records.Count(r => r.ValidationStatus == "Excluded"), Records.Count(r => r.ValidationStatus == "AlreadySent"));
                return Json(new ReviewPageDto(Run, counts, 1, 25, Records.Count, Records, 0m));
            }
            if (path.EndsWith("/selection/summary"))
            {
                LastSummaryBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(new SelectionSummaryDto(7, 2, "FP", new Dictionary<string, decimal> { ["AED"] = 1500m }, new Dictionary<string, int> { ["DirectSend"] = 2 }, 0, 0, 1, 50,
                    [new SelectionContactDto("RK1", "DirectSend", "Direct Customer 1", "+971500000001", "d1@example.test", "TP140-1", 500m, "AED", new DateOnly(2026, 9, 20), false),
                     new SelectionContactDto("RK2", "DirectSend", "Direct Customer 2", "+971500000002", "d2@example.test", "TP140-2", 1000m, "AED", new DateOnly(2026, 9, 20), false)], [],
                    new Dictionary<string, string> { ["DirectSend"] = DirectList }));
            }
            if (path.EndsWith("/overlaps")) return Json(new OverlapPageDto(0, 1, 25, []));
            if (path.EndsWith("/dispatches/readiness")) return Json(new DispatchReadinessDto(Blockers.Count == 0, Blockers, DirectSendBlockers));
            if (path.EndsWith("/dispatches") && request.Method == HttpMethod.Post)
            {
                LastConfirmBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(new DispatchDto(DispatchId, "Queued", null, Guid.NewGuid(), Read, null, null, 2, 0, 0, 0, 0, new Dictionary<string, decimal> { ["AED"] = 1500m }, [],
                    DispatchService.UploadDisclaimer), HttpStatusCode.Accepted);
            }
            if (path.EndsWith("/dispatches")) return Json(new List<DispatchDto>());
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static Api NewApi(Action<Api>? configure = null)
    {
        var api = new Api(new FakeCollectionsApi { IncludeNotEligible = true });
        configure?.Invoke(api);
        return api;
    }

    private static async Task<(string Html, Api Api)> LoadAsync(Action<Api>? configure = null, string url = Url)
    {
        var api = NewApi(configure);
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        return (await (await client.GetAsync(url)).Content.ReadAsStringAsync(), api);
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value);
    private static string Button(string html) => Regex.Match(html, "<button[^>]*data-approve-button[^>]*>").Value;
    private static string Reason(string html) => Decode(Regex.Match(html, "data-approve-reason[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);

    // ------------------------------------------------------------------ the selector and the filters

    [Fact]
    public async Task TheSelector_OffersManualAutoAndDirectSend_AndAutoStaysTheDefault()
    {
        var (html, _) = await LoadAsync(url: "/Collections/Campaigns?render=full");
        var toggle = Regex.Match(html, "<div class=\"segmented\"[^>]*data-sending-mode>.*?</div>", RegexOptions.Singleline).Value;
        Assert.Equal(["Manual", "Auto", "DirectSend"], Regex.Matches(toggle, "name=\"sendingMode\" value=\"(\\w+)\"").Select(m => m.Groups[1].Value));
        Assert.Matches("name=\"sendingMode\" value=\"Auto\"[^>]*checked", toggle);
        Assert.Matches("name=\"sendingMode\" value=\"DirectSend\"(?![^>]*checked)", toggle);
        Assert.Contains(">Direct Send<", toggle);

        var (direct, _) = await LoadAsync();
        var picked = Regex.Match(direct, "<div class=\"segmented\"[^>]*data-sending-mode>.*?</div>", RegexOptions.Singleline).Value;
        Assert.Matches("name=\"sendingMode\" value=\"DirectSend\"[^>]*checked", picked);
        Assert.Matches("name=\"sendingMode\" value=\"Auto\"(?![^>]*checked)", picked);
    }

    [Fact]
    public async Task DirectSend_NeedsNoStageAndNoDate_AndOffersTheFourFilters()
    {
        var (html, _) = await LoadAsync();
        var stage = Regex.Match(html, "<select[^>]*data-campaign-stage[^>]*>").Value;
        Assert.Contains("disabled", stage);                                                                    // never submitted: no campaign stage is required
        Assert.Matches("<div class=\"field-group\" data-mode-not=\"DirectSend\" hidden", html);
        Assert.Contains("disabled", Regex.Match(html, "<input[^>]*name=\"businessDate\"[^>]*>").Value);        // no preview / send date is submitted
        Assert.DoesNotContain("data-policy-schedule-result", html);                                            // no stage schedule is shown
        Assert.DoesNotContain("Schedule:", Regex.Replace(html, "<span[^>]*data-policy-schedule[^>]*hidden[^>]*>.*?</span>", "", RegexOptions.Singleline));
        var advanced = Regex.Match(html, "<details[^>]*data-advanced-filters[^>]*>.*?</details>", RegexOptions.Singleline).Value;
        Assert.Matches("<details[^>]*open", advanced);                                                          // the filters are open, not hidden
        Assert.Contains("name=\"paymentStatus\"", advanced); Assert.Contains("name=\"dateFrom\"", advanced); Assert.Contains("name=\"dateTo\"", advanced); Assert.Contains("name=\"minTotal\"", advanced);
        Assert.Contains("Minimum amount (AED)", advanced);
        Assert.Contains("campaignTower", html);                                                                 // the Tower picker
        Assert.Contains("Direct Send: you pick customers yourself", Decode(Regex.Match(html, "id=\"tip-sending-mode\"[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value));
    }

    [Fact]
    public async Task DirectSend_ListsFromTheReviewRecords_WithTheFiltersAndNoStageOrSendDate()
    {
        var (_, api) = await LoadAsync(url: Url + "&towerId=7&paymentStatus=Overdue&dateFrom=2026-08-01&dateTo=2026-09-30&minTotal=250");
        var query = Assert.Single(api.RecordQueries);
        Assert.Contains("reminderType=DirectSend", query); Assert.Contains("sendingMode=DirectSend", query);
        Assert.Contains("towerId=7", query); Assert.Contains("dueStatus=Overdue", query);
        Assert.Contains("dueFrom=2026-08-01", query); Assert.Contains("dueTo=2026-09-30", query); Assert.Contains("minRemaining=250", query);
        Assert.DoesNotContain("sendDate", query);
        Assert.DoesNotContain(api.All, call => call.Contains("/campaigns/preview"));                          // the stage-based preview is never called
    }

    // ------------------------------------------------------------------ eligible customers, selection and the button

    [Fact]
    public async Task EligibleCustomers_GetCheckboxes_SelectAllAndTheSendButton_AndHeldBackOnesDoNot()
    {
        var (html, _) = await LoadAsync();
        Assert.Equal(2, Regex.Matches(html, "class=\"campaign-select\"").Count);
        Assert.Contains("data-unit-key=\"4|3001|TP140-1\"", html);
        Assert.Equal(3, Regex.Matches(html, "class=\"row-select-disabled\"").Count);
        var header = Regex.Match(html, "<input[^>]*data-select-page[^>]*>").Value;
        Assert.DoesNotContain("disabled", header); Assert.Contains("Select all eligible customers on this page", header);
        Assert.Contains("All eligible (<span data-all-count>2</span>)", html);                                  // Select All eligible customers
        Assert.Contains("data-ready-count>2<", html); Assert.Contains("data-blocked-count>3<", html);
        var button = Button(html);
        Assert.Contains(">Send to Genesys<", html); Assert.DoesNotContain("disabled", button);
        Assert.Contains("0 selected", html);                                                                    // the selected count sits beside the button
        Assert.Contains("data-mode-name>Direct Send<", html); Assert.Contains("Direct send - manual contact list", html);
        Assert.Contains("data-auto-sending>Off<", html);
    }

    [Fact]
    public async Task HeldBackCustomers_ShowTheExactReason_AndTheReviewWorkflow()
    {
        var (html, _) = await LoadAsync();
        var text = Decode(html);
        Assert.Contains(ReviewReasons.Explain(ReviewReasons.SharedPhoneMultipleUnits), text);
        Assert.Contains(ReviewReasons.Explain(ReviewReasons.LegalStageCustomer), text);
        Assert.Contains(ReviewReasons.Explain(ReviewReasons.NoValidContact), text);
        Assert.Contains("data-reason-code=\"SharedPhoneMultipleUnits\"", html);
        Assert.Contains("Why customers are held back", html);
        Assert.Contains("Review: the same number is on several customers", text);                               // the review workflow for a shared phone
        Assert.Contains("Refer to the legal workflow", text);
        Assert.Contains("data-refresh-approval", html);
        Assert.Contains("Held back", text); Assert.Contains("Needs review", text);
    }

    [Fact]
    public async Task TheSendButton_IsDisabled_WithTheExactReason_WhenAMandatoryGateIsNotMet()
    {
        var (unreconciled, _) = await LoadAsync(a => a.Run = Run(reconciled: false));
        Assert.Contains("disabled", Button(unreconciled)); Assert.StartsWith("The financial source is not reconciled", Reason(unreconciled));

        var (noList, _) = await LoadAsync(a => a.DirectSendBlockers.Add("No Genesys contact list is configured for Direct Send."));
        Assert.Contains("disabled", Button(noList)); Assert.Contains("No Genesys contact list is configured for Direct Send.", Reason(noList));

        var (notConfigured, _) = await LoadAsync(a => a.Blockers.Add("Customer dispatch to Genesys is switched off."));
        Assert.Contains("disabled", Button(notConfigured)); Assert.Contains("switched off", Reason(notConfigured));

        var (stale, _) = await LoadAsync(a => a.Run = Run(stale: true));
        Assert.Contains("disabled", Button(stale)); Assert.Contains("stale", Reason(stale));

        var (yesterday, _) = await LoadAsync(a => a.Run = Run(asOf: DubaiToday.AddDays(-1)));
        Assert.Contains("disabled", Button(yesterday)); Assert.Contains("not today", Reason(yesterday));

        var (none, _) = await LoadAsync(a => a.Records = [Row(3, "Excluded", ReviewReasons.SharedPhoneMultipleUnits)]);
        Assert.Contains("disabled", Button(none)); Assert.Contains("No customers are eligible", Reason(none));
        Assert.DoesNotContain("class=\"campaign-select\"", none);
        Assert.Contains("disabled", Regex.Match(none, "<input[^>]*data-select-page[^>]*>").Value);

        var (noRun, _) = await LoadAsync(a => { a.Run = null; a.Records = []; });
        Assert.Contains("disabled", Button(noRun)); Assert.Contains("no approval data", Reason(noRun));
    }

    // ------------------------------------------------------------------ approve -> confirmation dialog -> confirm

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var page = await (await client.GetAsync("/Collections/Campaigns?handler=Results&sendingMode=DirectSend")).Content.ReadAsStringAsync();
        return Decode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static Dictionary<string, string> Form(string token, params (string Key, string Value)[] extra)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["sendingMode"] = "DirectSend", ["dateFrom"] = "2026-08-01", ["dateTo"] = "2026-09-30", ["towerId"] = "7", ["minTotal"] = "250", ["paymentStatus"] = "Overdue"
        };
        foreach (var (key, value) in extra) form[key] = value;
        return form;
    }

    [Fact]
    public async Task SendToGenesys_ShowsTheConfirmationDialog_WithTheSelectedCount_BeforeAnythingIsSent()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1,4|3002|TP140-2"))))).Content.ReadAsStringAsync();
        var dialog = Regex.Match(html, "<dialog[^>]*data-direct-confirm[^>]*>.*?</dialog>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(dialog);
        Assert.Contains("Send 2 customer(s) to Genesys?", Decode(dialog));                                     // the selected count, before confirming
        Assert.Contains("data-snapshot-count>2<", dialog);
        Assert.Contains("Direct Send (manual contact list; no campaign stage or stage dates)", Decode(dialog));
        Assert.Contains("Campaign purpose", dialog); Assert.Contains("data-snapshot-stage>Direct send - manual contact list<", dialog);
        Assert.Contains(DirectList, dialog);                                                                   // the destination contact list
        Assert.Contains("data-confirm-button", dialog); Assert.Contains("required", Regex.Match(dialog, "<input[^>]*name=\"ackCampaign\"[^>]*>").Value);
        Assert.Null(api.LastConfirmBody);                                                                      // approving never sends
        // The selection travels with the Direct Send filter: no stage, no send date.
        Assert.Contains("\"reminderType\":\"DirectSend\"", api.LastSummaryBody); Assert.Contains("\"sendingMode\":\"DirectSend\"", api.LastSummaryBody);
        Assert.Contains("SelectedUnits", api.LastSummaryBody); Assert.Contains("4|3001|TP140-1", api.LastSummaryBody);
        Assert.Contains("\"towerId\":7", api.LastSummaryBody); Assert.Contains("\"dueStatus\":\"Overdue\"", api.LastSummaryBody); Assert.Contains("\"minRemaining\":250", api.LastSummaryBody);
        Assert.DoesNotContain("sendDate\":\"2", api.LastSummaryBody!);
    }

    [Fact]
    public async Task Confirming_QueuesTheDispatch_WithTheIdempotencyKey_AndTheDirectSendFilter()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync("/Collections/Campaigns?handler=Confirm", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "AllMatching"), ("expectedCount", "2"), ("expectedFingerprint", "FP"), ("idempotencyKey", "direct-key-1"), ("ackCampaign", "true"))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Collections/Campaigns?dispatch={DispatchId:D}", response.Headers.Location!.OriginalString);
        Assert.Contains("\"idempotencyKey\":\"direct-key-1\"", api.LastConfirmBody); Assert.Contains("\"expectedCount\":2", api.LastConfirmBody);
        Assert.Contains("\"reminderType\":\"DirectSend\"", api.LastConfirmBody); Assert.Contains("AllMatching", api.LastConfirmBody);
    }

    // ------------------------------------------------------------------ Manual and Auto are unchanged

    [Fact]
    public async Task Manual_AndAuto_StillUseTheStagePreview_AndTheirOwnButtonAndFilters()
    {
        foreach (var mode in new[] { "Manual", "Auto" })
        {
            var api = NewApi();
            using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
            var html = await (await client.GetAsync($"/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode={mode}")).Content.ReadAsStringAsync();
            Assert.Contains(api.All, call => call.Contains("/campaigns/preview") && call.Contains($"sendingMode={mode}"));
            Assert.DoesNotContain(api.All, call => call.Contains("/review/records"));
            Assert.Contains("Approve &amp; Send to Genesys", html); Assert.DoesNotContain(">Send to Genesys<", html);
            Assert.DoesNotContain("<select class=\"field-select\" id=\"campaignStage\" name=\"stage\" data-campaign-stage disabled", html);   // the stage is required
            Assert.DoesNotContain("data-direct-send", html);
            Assert.Contains("data-preview-only-count", html);
        }
    }

    [Fact]
    public async Task TheResultsFragment_HonoursTheSendingMode_ForPagingInEveryMode()
    {
        foreach (var mode in new[] { "Manual", "Auto" })
        {
            var api = NewApi();
            using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
            await client.GetAsync($"/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode={mode}");
            Assert.Contains(api.All, call => call.Contains("/campaigns/preview") && call.Contains($"sendingMode={mode}"));
        }
        var direct = NewApi();
        using (var factory = CollectionsWebHost.Factory(direct)) using (var client = factory.CreateClient())
        {
            var fragment = await (await client.GetAsync("/Collections/Campaigns?handler=Results&sendingMode=DirectSend")).Content.ReadAsStringAsync();
            Assert.Contains("data-direct-send", fragment); Assert.Single(direct.RecordQueries);
        }
    }
}
