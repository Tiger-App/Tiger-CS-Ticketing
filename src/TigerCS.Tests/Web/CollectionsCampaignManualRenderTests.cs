extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Web;

/// <summary>
/// Collections Campaigns has exactly two sending modes. Manual: any campaign type at any time (no send date, no schedule), the four filters, eligible customers with checkboxes, an existing contact
/// list that suits the campaign, a confirmation with the exact count, destination and amount. Auto: the approved schedule (a read-only preview here; the automatic dispatcher sends on the date).
/// </summary>
public sealed class CollectionsCampaignManualRenderTests
{
    private static readonly DateTime Read = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly DubaiToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));
    private const string CurrentMonthList = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1", FollowUpList = "3a91c06e-47ab-4a5e-a720-004bd5cf5bba";
    private const string Url = "/Collections/Campaigns?render=full&stage=CurrentMonthReminder&sendingMode=Manual";
    private static readonly Guid DispatchId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static ReviewRunDto Run(bool reconciled = true, bool stale = false, DateOnly? asOf = null) => new(7, "Completed", "Completed", 100, asOf ?? DubaiToday, null,
        new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), "PACT receivables (companies 4 and 32)", "", reconciled, Read, Read, Read, Read, 1234, 56, null, true, stale);

    private static ReviewRecordDto Row(int n, string status, params string[] reasons) => new($"RK{n}", "CurrentMonth", 4, $"Manual Customer {n}", $"+9715000000{n:D2}", $"m{n}@example.test", "TP140", $"TP140-{n}",
        "Unpaid", 500m * n, "AED", new DateOnly(2026, 9, 20), status, reasons.Select(r => new ReviewReasonDto(r, "Exclusion", ReviewReasons.Explain(r))).ToList(),
        "PACT", Read, null, null, "140", "Tower 140", $"300{n}");

    private sealed class Api(FakeCollectionsApi inner) : DelegatingHandler(inner)
    {
        public ReviewRunDto? Run { get; set; } = CollectionsCampaignManualRenderTests.Run();
        public List<ReviewRecordDto> Records { get; set; } =
        [
            Row(1, "Ready"), Row(2, "Ready"),
            Row(3, "Excluded", ReviewReasons.ManualSharedPhone),
            Row(4, "Excluded", ReviewReasons.LegalNoticeReleaseRequired),
            Row(5, "NeedsReview", ReviewReasons.NoValidContact)
        ];
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
                return Json(new ReviewPageDto(Run, counts, 1, 25, Records.Count, Records, 0m, false, Records.Where(r => r.ValidationStatus == "Ready").Sum(r => r.RemainingAmount ?? 0m)));
            }
            if (path.EndsWith("/selection/summary"))
            {
                LastSummaryBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var chosen = JsonDocument.Parse(LastSummaryBody).RootElement.GetProperty("filter").TryGetProperty("contactList", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "CurrentMonth";
                var list = chosen == "FollowUp" ? FollowUpList : CurrentMonthList;
                return Json(new SelectionSummaryDto(7, 2, "FP", new Dictionary<string, decimal> { ["AED"] = 1500m }, new Dictionary<string, int> { ["CurrentMonth"] = 2 }, 0, 0, 1, 50,
                    [new SelectionContactDto("RK1", "CurrentMonth", "Manual Customer 1", "+971500000001", "m1@example.test", "TP140-1", 500m, "AED", new DateOnly(2026, 9, 20), false),
                     new SelectionContactDto("RK2", "CurrentMonth", "Manual Customer 2", "+971500000002", "m2@example.test", "TP140-2", 1000m, "AED", new DateOnly(2026, 9, 20), false)], [],
                    new Dictionary<string, string> { ["CurrentMonth"] = list }, true, chosen));
            }
            if (path.EndsWith("/overlaps")) return Json(new OverlapPageDto(0, 1, 25, []));
            if (path.EndsWith("/dispatches/readiness")) return Json(new DispatchReadinessDto(Blockers.Count == 0, Blockers));
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
    private static string Toggle(string html) => Regex.Match(html, "<div class=\"segmented\"[^>]*data-sending-mode>.*?</div>", RegexOptions.Singleline).Value;

    // ------------------------------------------------------------------ exactly two modes

    [Fact]
    public async Task TheSelector_OffersExactlyManualAndAuto_AndAutoStaysTheDefault()
    {
        var (html, _) = await LoadAsync(url: "/Collections/Campaigns?render=full");
        var toggle = Toggle(html);
        Assert.Equal(["Manual", "Auto"], Regex.Matches(toggle, "name=\"sendingMode\" value=\"(\\w+)\"").Select(m => m.Groups[1].Value));
        Assert.Matches("name=\"sendingMode\" value=\"Auto\"[^>]*checked", toggle);
        Assert.DoesNotContain("Direct Send", html); Assert.DoesNotContain("DirectSend", html);
        var (manual, _) = await LoadAsync();
        Assert.Matches("name=\"sendingMode\" value=\"Manual\"[^>]*checked", Toggle(manual));
        Assert.Matches("name=\"sendingMode\" value=\"Auto\"(?![^>]*checked)", Toggle(manual));
    }

    // ------------------------------------------------------------------ Manual: no date, the campaign type, the four filters

    [Fact]
    public async Task Manual_NeedsNoSendDate_OffersTheCampaignType_AndTheFourFilters()
    {
        var (html, _) = await LoadAsync();
        var stage = Regex.Match(html, "<select[^>]*data-campaign-stage[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("disabled", Regex.Match(stage, "<select[^>]*>").Value);                           // any existing campaign type can be chosen
        Assert.Equal(5, Regex.Matches(stage, "<option ").Count);
        Assert.Matches("<option value=\"CurrentMonthReminder\"[^>]*selected", stage);
        Assert.Contains(">Campaign type</label>", html);
        Assert.Contains("disabled", Regex.Match(html, "<input[^>]*name=\"businessDate\"[^>]*>").Value);         // no preview / send date is submitted
        Assert.Matches("<div class=\"field-group\" data-date-group hidden", html);
        Assert.DoesNotContain("data-policy-schedule-result", html); Assert.DoesNotContain(">Send date<", html);
        var advanced = Regex.Match(html, "<details[^>]*data-advanced-filters[^>]*>.*?</details>", RegexOptions.Singleline).Value;
        Assert.Matches("<details[^>]*open", advanced);                                                          // the filters are open
        Assert.Contains("name=\"paymentStatus\"", advanced); Assert.Contains("name=\"dateFrom\"", advanced); Assert.Contains("name=\"dateTo\"", advanced); Assert.Contains("name=\"minTotal\"", advanced);
        Assert.Contains("Minimum Total (AED)", advanced);
        Assert.Contains("campaignTower", html);                                                                 // the Tower picker
        Assert.Contains("Manual: choose any campaign type", Decode(Regex.Match(html, "id=\"tip-sending-mode\"[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value));
    }

    [Fact]
    public async Task Manual_ListsFromTheReviewRecords_WithTheCampaignTypeAndTheFilters_AndNoSendDate()
    {
        var (_, api) = await LoadAsync(url: Url + "&towerId=7&paymentStatus=Overdue&dateFrom=2026-08-01&dateTo=2026-09-30&minTotal=250");
        var query = Assert.Single(api.RecordQueries);
        Assert.Contains("reminderType=CurrentMonth", query); Assert.Contains("sendingMode=Manual", query);
        Assert.Contains("towerId=7", query); Assert.Contains("dueStatus=Overdue", query);
        Assert.Contains("dueFrom=2026-08-01", query); Assert.Contains("dueTo=2026-09-30", query); Assert.Contains("minRemaining=250", query);
        Assert.DoesNotContain("sendDate", query);
        Assert.DoesNotContain(api.All, call => call.Contains("/campaigns/preview"));                          // the schedule preview is never called in Manual
        var (_, other) = await LoadAsync(url: "/Collections/Campaigns?render=full&stage=FollowUpReminder&sendingMode=Manual");
        Assert.Contains("reminderType=FollowUp", Assert.Single(other.RecordQueries));                          // any existing campaign type
    }

    // ------------------------------------------------------------------ the contact list

    [Theory]
    [InlineData("CurrentMonthReminder", new[] { "CurrentMonth", "FollowUp", "Overdue" })]
    [InlineData("FollowUpReminder", new[] { "FollowUp", "CurrentMonth", "Overdue" })]
    [InlineData("OverdueReminder", new[] { "Overdue", "CurrentMonth", "FollowUp" })]
    [InlineData("LegalNotice", new[] { "LegalNotice" })]
    public async Task TheContactListDropdown_OffersTheExistingListsThatSuitTheCampaign_WithItsOwnListAsTheDefault(string stage, string[] expected)
    {
        var (html, _) = await LoadAsync(url: $"/Collections/Campaigns?render=full&stage={stage}&sendingMode=Manual");
        var select = Regex.Match(html, "<select[^>]*data-contact-list[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(select); Assert.Contains("name=\"contactList\"", select);
        Assert.Equal(expected, Regex.Matches(select, "<option value=\"(\\w+)\"").Select(m => m.Groups[1].Value));
        Assert.Matches($"<option value=\"{expected[0]}\"[^>]*selected", select);                                // the campaign's own list is selected
        Assert.Contains("this campaign's list", Decode(select));
    }

    [Fact]
    public async Task TheChosenContactList_IsKeptWhenItSuitsTheCampaign_OtherwiseTheOwnListApplies()
    {
        var (chosen, _) = await LoadAsync(url: Url + "&contactList=FollowUp");
        Assert.Matches("<option value=\"FollowUp\"[^>]*selected", chosen); Assert.Contains("Follow Up list", chosen);
        var (invalid, _) = await LoadAsync(url: "/Collections/Campaigns?render=full&stage=LegalNotice&sendingMode=Manual&contactList=CurrentMonth");
        Assert.Matches("<option value=\"LegalNotice\"[^>]*selected", invalid);                                  // a reminder list never receives legal wording
    }

    [Fact]
    public async Task OnlyManual_ShowsTheContactListDropdown_AndAutoOffersNoSendingControls()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode=Auto")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-contact-list", html); Assert.DoesNotContain("name=\"contactList\"", html);
        Assert.DoesNotContain("data-approve-form", html); Assert.DoesNotContain("data-approve-button", html); Assert.DoesNotContain("class=\"campaign-select\"", html);
        Assert.DoesNotContain("Approve &amp; Send", html);
        Assert.Contains("data-auto-note", html); Assert.Contains("automatic execution is <strong>Off</strong>", html);   // off by default
        Assert.Contains(api.All, call => call.Contains("/campaigns/preview") && call.Contains("sendingMode=Auto"));
        Assert.DoesNotContain(api.All, call => call.Contains("/review/records"));
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
        Assert.Contains(">Send to Genesys<", html); Assert.DoesNotContain("disabled", Button(html));
        Assert.Contains("0 selected", html);                                                                    // the selected count sits beside the button
        Assert.Contains("data-mode-name>Manual<", html); Assert.Contains("Current month reminder - day 14", html);
        Assert.Contains("On confirmation only", html);
        Assert.Contains("data-eligible-total>AED 1,500.00<", html);                                             // the exact calculated amount of the eligible customers
    }

    [Fact]
    public async Task HeldBackCustomers_ShowTheExactReason_AndTheReviewWorkflow()
    {
        var (html, _) = await LoadAsync();
        var text = Decode(html);
        Assert.Contains(ReviewReasons.Explain(ReviewReasons.ManualSharedPhone), text);
        Assert.Contains(ReviewReasons.Explain(ReviewReasons.LegalNoticeReleaseRequired), text);
        Assert.Contains(ReviewReasons.Explain(ReviewReasons.NoValidContact), text);
        Assert.Contains("data-reason-code=\"ManualSharedPhone\"", html);
        Assert.Contains("Why customers are held back", html);
        Assert.Contains("Review: the same number is on several customers", text);                               // the review workflow for a shared phone
        Assert.Contains("Legal notices have not been released", text);
        Assert.Contains("data-refresh-approval", html);
        Assert.Contains("Held back", text); Assert.Contains("Needs review", text);
    }

    [Fact]
    public async Task TheSendButton_IsDisabled_WithTheExactReason_WhenAMandatoryGateIsNotMet()
    {
        var (unreconciled, _) = await LoadAsync(a => a.Run = Run(reconciled: false));
        Assert.Contains("disabled", Button(unreconciled)); Assert.StartsWith("The financial source is not reconciled", Reason(unreconciled));

        var (notConfigured, _) = await LoadAsync(a => a.Blockers.Add("Customer dispatch to Genesys is switched off."));
        Assert.Contains("disabled", Button(notConfigured)); Assert.Contains("switched off", Reason(notConfigured));

        var (stale, _) = await LoadAsync(a => a.Run = Run(stale: true));
        Assert.Contains("disabled", Button(stale)); Assert.Contains("stale", Reason(stale));

        var (yesterday, _) = await LoadAsync(a => a.Run = Run(asOf: DubaiToday.AddDays(-1)));
        Assert.Contains("disabled", Button(yesterday)); Assert.Contains("not today", Reason(yesterday));

        var (none, _) = await LoadAsync(a => a.Records = [Row(3, "Excluded", ReviewReasons.ManualSharedPhone)]);
        Assert.Contains("disabled", Button(none)); Assert.Contains("No customers are eligible", Reason(none));
        Assert.DoesNotContain("class=\"campaign-select\"", none);
        Assert.Contains("disabled", Regex.Match(none, "<input[^>]*data-select-page[^>]*>").Value);
        Assert.Contains("data-eligible-total>AED 0.00<", none);

        var (noRun, _) = await LoadAsync(a => { a.Run = null; a.Records = []; });
        Assert.Contains("disabled", Button(noRun)); Assert.Contains("no approval data", Reason(noRun));
    }

    // ------------------------------------------------------------------ approve -> confirmation dialog -> confirm

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var page = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&sendingMode=Manual")).Content.ReadAsStringAsync();
        return Decode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static Dictionary<string, string> Form(string token, params (string Key, string Value)[] extra)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["sendingMode"] = "Manual", ["stage"] = "CurrentMonthReminder", ["dateFrom"] = "2026-08-01", ["dateTo"] = "2026-09-30", ["towerId"] = "7",
            ["minTotal"] = "250", ["paymentStatus"] = "Overdue"
        };
        foreach (var (key, value) in extra) form[key] = value;
        return form;
    }

    [Theory]
    [InlineData("CurrentMonth", "Current Month", CurrentMonthList)]
    [InlineData("FollowUp", "Follow Up", FollowUpList)]
    public async Task SendToGenesys_ShowsTheConfirmationDialog_WithCountDestinationAndAmount_BeforeAnythingIsSent(string chosen, string label, string listId)
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1,4|3002|TP140-2"), ("contactList", chosen))))).Content.ReadAsStringAsync();
        var dialog = Regex.Match(html, "<dialog[^>]*data-manual-confirm[^>]*>.*?</dialog>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(dialog);
        Assert.Contains($"Send 2 customer(s) to the {label} contact list?", Decode(dialog));                    // the selected count and the destination, before confirming
        Assert.Contains("data-snapshot-count>2<", dialog);
        Assert.Contains("data-snapshot-total>AED 1,500.00<", dialog);                                           // the filtered amount
        Assert.Contains("Manual (any time; no send date or schedule)", Decode(dialog));
        Assert.Contains("data-snapshot-stage>Current month reminder - day 14<", dialog);                        // the campaign purpose
        Assert.Contains($"{label} contact list", dialog); Assert.Contains(listId, dialog);                      // destination name and id
        Assert.Contains($"name=\"contactList\" value=\"{chosen}\"", dialog);
        Assert.Contains("data-confirm-button", dialog); Assert.Contains("required", Regex.Match(dialog, "<input[^>]*name=\"ackCampaign\"[^>]*>").Value);
        Assert.Null(api.LastConfirmBody);                                                                       // approving never sends
        Assert.Contains("\"reminderType\":\"CurrentMonth\"", api.LastSummaryBody); Assert.Contains("\"sendingMode\":\"Manual\"", api.LastSummaryBody); Assert.Contains($"\"contactList\":\"{chosen}\"", api.LastSummaryBody);
        Assert.Contains("SelectedUnits", api.LastSummaryBody); Assert.Contains("4|3001|TP140-1", api.LastSummaryBody);
        Assert.Contains("\"towerId\":7", api.LastSummaryBody); Assert.Contains("\"dueStatus\":\"Overdue\"", api.LastSummaryBody); Assert.Contains("\"minRemaining\":250", api.LastSummaryBody);
        Assert.Contains("\"dueFrom\":\"2026-08-01\"", api.LastSummaryBody); Assert.DoesNotContain("sendDate", api.LastSummaryBody!);
    }

    [Fact]
    public async Task Confirming_QueuesTheDispatch_WithTheIdempotencyKey_AndTheManualFilter()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync("/Collections/Campaigns?handler=Confirm", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "AllMatching"), ("expectedCount", "2"), ("expectedFingerprint", "FP"), ("idempotencyKey", "manual-key-1"), ("ackCampaign", "true"), ("contactList", "FollowUp"))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Collections/Campaigns?dispatch={DispatchId:D}", response.Headers.Location!.OriginalString);
        Assert.Contains("\"idempotencyKey\":\"manual-key-1\"", api.LastConfirmBody); Assert.Contains("\"expectedCount\":2", api.LastConfirmBody);
        Assert.Contains("\"reminderType\":\"CurrentMonth\"", api.LastConfirmBody); Assert.Contains("AllMatching", api.LastConfirmBody); Assert.Contains("\"contactList\":\"FollowUp\"", api.LastConfirmBody);
    }

    [Fact]
    public async Task AutoMode_CannotBeApprovedFromThePage_TheDispatcherSendsOnTheScheduledDate()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var token = await TokenAsync(client);
        foreach (var handler in new[] { "Approve", "Confirm" })
        {
            var html = await (await client.PostAsync($"/Collections/Campaigns?handler={handler}", new FormUrlEncodedContent(Form(token,
                ("sendingMode", "Auto"), ("selectionMode", "AllMatching"), ("expectedCount", "2"), ("expectedFingerprint", "FP"), ("idempotencyKey", "k"), ("ackCampaign", "true"))))).Content.ReadAsStringAsync();
            Assert.Contains("dispatched on their scheduled dates by the automatic dispatcher", Decode(html));
            Assert.Contains("switch to Manual", Decode(html));
        }
        Assert.Null(api.LastSummaryBody); Assert.Null(api.LastConfirmBody);                                       // nothing reached the API
    }

    [Fact]
    public async Task TheResultsFragment_HonoursTheSendingMode_ForPagingInBothModes()
    {
        var manual = NewApi();
        using (var factory = CollectionsWebHost.Factory(manual)) using (var client = factory.CreateClient())
        {
            var fragment = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=FollowUpReminder&sendingMode=Manual")).Content.ReadAsStringAsync();
            Assert.Contains("data-manual-send", fragment); Assert.Contains("reminderType=FollowUp", Assert.Single(manual.RecordQueries));
        }
        var auto = NewApi();
        using (var factory = CollectionsWebHost.Factory(auto)) using (var client = factory.CreateClient())
        {
            var fragment = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode=Auto")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("data-manual-send", fragment); Assert.Contains(auto.All, call => call.Contains("/campaigns/preview") && call.Contains("sendingMode=Auto"));
        }
    }
}
