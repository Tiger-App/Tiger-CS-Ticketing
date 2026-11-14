extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    private const string ManualList = "11111111-2222-4333-8444-555555555555";
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
        public bool CanSend { get; set; } = true;
        public string? ExportError { get; set; }
        public bool SchemaVerified { get; set; } = true;
        /// <summary>What the fake export answers about the file: units left out, whether the data is verified, and customers exported before.</summary>
        public int ExportExcluded { get; set; }
        public bool ExportDataVerified { get; set; } = true;
        public int ExportRepeated { get; set; }
        public string? LastExportQuery { get; private set; }
        public string? LastReviewExportBody { get; private set; }
        public string? ReviewExportError { get; set; }
        public string? LastExportBody { get; private set; }
        public int Exports { get; private set; }
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
            if (path.EndsWith("/review/refresh")) return Json(Run, HttpStatusCode.Accepted);
            if (path.EndsWith("/export/genesys"))
            {
                Exports++;
                LastExportBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastExportQuery = request.RequestUri.Query;
                if (ExportError is not null) return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { title = "InvalidRequest", detail = ExportError, status = 400 }) };
                var fileName = "genesys-CurrentMonth-to-FollowUp-20261106-0930" + (SchemaVerified ? "" : " (Template not verified against Genesys)") + (ExportDataVerified ? "" : " (Financial data not verified)") + ".csv";
                var notices = new List<string>();
                if (!SchemaVerified) notices.Add("Template not verified against Genesys: the Follow Up list's columns were not checked in Genesys.");
                if (!ExportDataVerified) notices.Add("The financial source has not been reconciled against PACT, so the amounts are unverified.");
                if (ExportExcluded > 0) notices.Add($"{ExportExcluded} of the {1 + ExportExcluded} matching customer unit(s) are not in the file: {ExportExcluded} NoCallablePhone (No valid international (E.164) phone number).");
                if (ExportRepeated > 0) notices.Add($"{ExportRepeated} of the 1 customer unit(s) in this file were exported before within the last 30 days (latest 05 Nov 2026 09:30 UTC, 1 earlier export(s)).");
                return Json(new GenesysExportDto(fileName, "\"Phone\",\"CustomerName\",\"Email Address\",\"ReminderType\",\"AmountDue\",\"DueDate\"\r\n\"+971500000001\",\"Manual Customer 1\",\"m1@example.test\",\"Current Month\",\"500.00\",\"2026-09-20\"\r\n",
                    1, "Current month reminder - day 14", "Follow Up", FollowUpList, 500m, "AED", ["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], SchemaVerified,
                    notices, "ABC", ExportRepeated, ExportRepeated > 0 ? new DateTime(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc) : null, 1,
                    ExportExcluded > 0 ? [new GenesysExportExclusionDto("NoCallablePhone", ExportExcluded, "No valid international (E.164) phone number.")] : [],
                    new Dictionary<string, int> { ["Ready"] = 1 }, ExportDataVerified, ExportDataVerified ? [] : ["SourceReconciliationRequired"]));
            }
            if (path.EndsWith("/export/review"))
            {
                LastReviewExportBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (ReviewExportError is not null) return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "ReviewRequired", detail = ReviewExportError, status = 409 }) };
                return Json(new ReviewExportDto("collections-CurrentMonth-manual-review-20261106-0930.csv", "\"Customer\",\"Status\"\r\n\"Manual Customer 3\",\"Excluded\"\r\n", 5, 2, "DEF"));
            }
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
                var chosen = JsonDocument.Parse(LastSummaryBody).RootElement.GetProperty("filter").TryGetProperty("contactList", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "Manual";
                var list = ManualList;
                return Json(new SelectionSummaryDto(7, 2, "FP", new Dictionary<string, decimal> { ["AED"] = 1500m }, new Dictionary<string, int> { ["CurrentMonth"] = 2 }, 0, 0, 1, 50,
                    [new SelectionContactDto("RK1", "CurrentMonth", "Manual Customer 1", "+971500000001", "m1@example.test", "TP140-1", 500m, "AED", new DateOnly(2026, 9, 20), false),
                     new SelectionContactDto("RK2", "CurrentMonth", "Manual Customer 2", "+971500000002", "m2@example.test", "TP140-2", 1000m, "AED", new DateOnly(2026, 9, 20), false)], [],
                    new Dictionary<string, string> { ["CurrentMonth"] = list }, true, chosen));
            }
            if (path.EndsWith("/overlaps")) return Json(new OverlapPageDto(0, 1, 25, []));
            if (path.EndsWith("/dispatches/readiness")) return Json(new DispatchReadinessDto(Blockers.Count == 0, Blockers, CanSend));
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
    [InlineData("CurrentMonthReminder")]
    [InlineData("FollowUpReminder")]
    [InlineData("OverdueReminder")]
    [InlineData("LegalNotice")]
    public async Task Manual_HasNoContactListChooser_TheDestinationIsTheDedicatedManualList(string stage)
    {
        var (html, _) = await LoadAsync(url: $"/Collections/Campaigns?render=full&stage={stage}&sendingMode=Manual");
        Assert.DoesNotContain("data-contact-list", html); Assert.DoesNotContain("<select name=\"contactList\"", html);
        Assert.Contains("TigerCS Manual Collections", Decode(html));
    }

    [Fact]
    public async Task AnotherListInTheUrl_IsIgnored_TheManualListStillApplies()
    {
        var (html, _) = await LoadAsync(url: Url + "&contactList=FollowUp");
        Assert.Contains("TigerCS Manual Collections", Decode(html)); Assert.DoesNotContain("Follow Up list", html);
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
        Assert.Contains("built before the per-unit calling decision", text);                                        // a stored shared-phone hold: refresh clears it
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
    [InlineData("Manual", "TigerCS Manual Collections", ManualList)]
    [InlineData("FollowUp", "TigerCS Manual Collections", ManualList)]   // a stale or tampered value is replaced by the Manual list
    public async Task SendToGenesys_ShowsTheConfirmationDialog_WithCountDestinationAndAmount_BeforeAnythingIsSent(string posted, string label, string listId)
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1,4|3002|TP140-2"), ("contactList", posted))))).Content.ReadAsStringAsync();
        var dialog = Regex.Match(html, "<dialog[^>]*data-manual-confirm[^>]*>.*?</dialog>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(dialog);
        Assert.Contains($"Send 2 unit record(s) to the {label} contact list?", Decode(dialog));                    // the selected count and the destination, before confirming
        Assert.Contains("data-snapshot-count>2<", dialog); Assert.Contains("<dt>Unit records</dt>", dialog);
        Assert.DoesNotContain("customer(s)", Decode(dialog));
        Assert.Contains("data-confirm-table", dialog); Assert.Contains("confirm-table", dialog);                     // only the list of unit records scrolls
        Assert.DoesNotContain("table-scroll", dialog);                                                               // no second scroll area (no sideways scrolling)
        Assert.Contains("data-confirm-hint", dialog); Assert.Contains("Confirm is unavailable until you tick the box", Decode(dialog));   // why Confirm waits for the acknowledgement
        Assert.Contains("Confirm: send 2 unit record(s) to Genesys", Decode(dialog));
        Assert.Contains("data-snapshot-total>AED 1,500.00<", dialog);                                           // the filtered amount
        Assert.Contains("Manual (any time; no send date or schedule)", Decode(dialog));
        Assert.Contains("data-snapshot-stage>Current month reminder - day 14<", dialog);                        // the campaign purpose
        Assert.Contains($"{label} contact list", dialog); Assert.Contains(listId, dialog);                      // destination name and id
        Assert.Contains("name=\"contactList\" value=\"Manual\"", dialog);
        Assert.Contains("data-confirm-button", dialog); Assert.Contains("required", Regex.Match(dialog, "<input[^>]*name=\"ackCampaign\"[^>]*>").Value);
        Assert.Null(api.LastConfirmBody);                                                                       // approving never sends
        Assert.Contains("\"reminderType\":\"CurrentMonth\"", api.LastSummaryBody); Assert.Contains("\"sendingMode\":\"Manual\"", api.LastSummaryBody); Assert.Contains("\"contactList\":\"Manual\"", api.LastSummaryBody);
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
        Assert.Contains("\"reminderType\":\"CurrentMonth\"", api.LastConfirmBody); Assert.Contains("AllMatching", api.LastConfirmBody); Assert.Contains("\"contactList\":\"Manual\"", api.LastConfirmBody);
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

    // ------------------------------------------------------------------ Export for Genesys

    private static string ExportButton(string html) => Regex.Match(html, "<button[^>]*data-export-genesys-button[^>]*>").Value;

    [Fact]
    public async Task ExportForGenesys_IsItsOwnButton_BesideSendToGenesys()
    {
        var (html, _) = await LoadAsync();
        var export = ExportButton(html); var send = Button(html);
        Assert.NotEmpty(export); Assert.Contains(">Export for Genesys</button>", html);
        Assert.Contains("handler=ExportGenesys", export); Assert.DoesNotContain("handler=ExportGenesys", send);       // a different action from sending
        Assert.DoesNotContain("data-approve-button", export); Assert.DoesNotContain("data-export-genesys-button", send);
        Assert.DoesNotContain("disabled", export); Assert.Contains("data-blocked=\"false\"", export);
        var tip = Decode(Regex.Match(html, "id=\"tip-export-genesys\"[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);
        Assert.Contains("Phone, CustomerName, Email Address, ReminderType, AmountDue, DueDate", tip); Assert.Contains("uploads nothing", tip); Assert.Contains("audit trail", tip);
        // The review export is its own third action, in Manual as in Auto: it is neither the Genesys export nor the send.
        var review = Regex.Match(html, "<button[^>]*data-export-review-manual[^>]*>Export review CSV</button>").Value;
        Assert.NotEmpty(review); Assert.Contains("handler=ExportReview", review); Assert.DoesNotContain("handler=ExportGenesys", review);
    }

    private static string ExportTip(string html) => Decode(Regex.Match(html, "id=\"tip-export-genesys\"[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);

    [Fact]
    public async Task ExportForGenesys_IsAvailableWhateverTheSendingEligibility_AndBlockedOnlyByPermissionOrMissingData()
    {
        // Every gate that stops a SEND leaves the file export available: background jobs / Genesys configuration, reconciliation, freshness, the build date, and the Ready count.
        var (jobsOff, _) = await LoadAsync(a => a.Blockers.Add("Background jobs are switched off (BackgroundJobs:Enabled), so an approved send could not run."));
        Assert.Contains("disabled", Button(jobsOff)); Assert.Contains("Background jobs are switched off", Reason(jobsOff));
        Assert.DoesNotContain("disabled", ExportButton(jobsOff));

        var (unreconciled, _) = await LoadAsync(a => a.Run = Run(reconciled: false));
        Assert.Contains("disabled", Button(unreconciled)); Assert.DoesNotContain("disabled", ExportButton(unreconciled));
        Assert.Contains("unverified data is labelled on the file", ExportTip(unreconciled));

        var (stale, _) = await LoadAsync(a => a.Run = Run(stale: true));
        Assert.Contains("disabled", Button(stale)); Assert.DoesNotContain("disabled", ExportButton(stale));

        var (yesterday, _) = await LoadAsync(a => a.Run = Run(asOf: DubaiToday.AddDays(-1)));
        Assert.Contains("disabled", Button(yesterday)); Assert.DoesNotContain("disabled", ExportButton(yesterday));

        var (none, _) = await LoadAsync(a => a.Records = [Row(3, "Excluded", ReviewReasons.ManualSharedPhone), Row(5, "NeedsReview", ReviewReasons.PaymentStatusUnknown)]);
        Assert.Contains("disabled", Button(none)); Assert.DoesNotContain("disabled", ExportButton(none));
        Assert.Contains("Nothing to send; export available", none); Assert.DoesNotContain("No eligible customers", none);     // no misleading "nothing" for a download-only file
        Assert.Contains("held-back ones included", ExportTip(none)); Assert.Contains("TigerCS Manual Collections Genesys contact list", ExportTip(none));

        var (reader, _) = await LoadAsync(a => a.CanSend = false);
        Assert.Contains("disabled", ExportButton(reader)); Assert.Contains("disabled", Button(reader));
        Assert.Contains("does not have permission", ExportTip(reader));

        var (noRun, _) = await LoadAsync(a => { a.Run = null; a.Records = []; });
        Assert.Contains("disabled", ExportButton(noRun)); Assert.Contains("no approval data yet", ExportTip(noRun));

        var (empty, _) = await LoadAsync(a => a.Records = []);
        Assert.Contains("disabled", ExportButton(empty)); Assert.Contains("nothing to export", ExportTip(empty));
    }

    [Fact]
    public async Task TheExportTip_NamesTheManualDestinationList()
    {
        var (html, _) = await LoadAsync(url: Url);
        Assert.Contains("TigerCS Manual Collections Genesys contact list", ExportTip(html));
    }

    [Fact]
    public async Task PostingTheExport_DownloadsTheCsv_ForTheSelectionAndFilters_AndSendsNothing()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=ExportGenesys", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1"), ("contactList", "FollowUp"))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType); Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("genesys-CurrentMonth-to-FollowUp-20261106-0930.csv", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);                                                  // UTF-8 with a byte-order mark, like the page's other CSV downloads
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("\"Phone\",\"CustomerName\",\"Email Address\",\"ReminderType\",\"AmountDue\",\"DueDate\"", text); Assert.Contains("\"500.00\"", text);
        // The API got the selection and the filters, exactly as the preview and the send use them.
        var body = api.LastExportBody!;
        Assert.Equal(1, api.Exports);
        Assert.Contains("\"reminderType\":\"CurrentMonth\"", body); Assert.Contains("\"sendingMode\":\"Manual\"", body); Assert.Contains("\"contactList\":\"Manual\"", body);
        Assert.Contains("SelectedUnits", body); Assert.Contains("4|3001|TP140-1", body); Assert.Contains("\"towerId\":7", body); Assert.Contains("\"dueStatus\":\"Overdue\"", body);
        Assert.Contains("\"minRemaining\":250", body); Assert.Contains("\"dueFrom\":\"2026-08-01\"", body); Assert.DoesNotContain("sendDate", body);
        Assert.Null(api.LastConfirmBody); Assert.Null(api.LastSummaryBody);                                         // an export is not an approval
    }

    [Fact]
    public async Task NothingTicked_ExportsEveryMatchingCustomer_NoSelectionIsRequired()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=ExportGenesys", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", ""), ("contactList", "FollowUp"))));
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("AllMatching", api.LastExportBody); Assert.DoesNotContain("SelectedUnits", api.LastExportBody);
        Assert.Null(api.LastConfirmBody); Assert.Null(api.LastSummaryBody);                                         // no approval, no confirmation
    }

    [Fact]
    public async Task AnExportThatTheApiRefuses_ReturnsToTheSameModeFiltersAndList_WithTheExactReason_AndNoFile()
    {
        var api = NewApi(a => a.ExportError = "The Follow Up contact list does not match the template: missing column AmountDue. Nothing was exported.");
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=ExportGenesys", new FormUrlEncodedContent(Form(await TokenAsync(client), ("selectionMode", "AllMatching"), ("contactList", "FollowUp"))));
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        // The refusal redirects back to the list that was on screen: Manual stays Manual, every filter and the chosen list travel in the URL.
        var landed = response.RequestMessage!.RequestUri!.PathAndQuery;
        foreach (var kept in new[] { "sendingMode=Manual", "stage=CurrentMonthReminder", "dateFrom=2026-08-01", "dateTo=2026-09-30", "towerId=7", "minTotal=250", "paymentStatus=Overdue", "render=full" })
            Assert.Contains(kept, landed);
        var html = Decode(await response.Content.ReadAsStringAsync());
        Assert.Contains("data-approval-error", html); Assert.Contains("missing column AmountDue", html);
        Assert.Matches("name=\"sendingMode\" value=\"Manual\"[^>]*checked", Toggle(html)); Assert.Contains("TigerCS Manual Collections", Decode(html));
        Assert.Contains("data-manual-send", html); Assert.Contains("Manual Customer 1", html);                   // the Manual list is on screen again
    }

    [Fact]
    public async Task ExportingFromAutoMode_DownloadsTheSameFile_ForEveryEligibleCustomer_AndSendsNothing()
    {
        // Auto has no ticks: the export is always of all eligible customers of the filters. The mode travels to the API (for the audit); the API builds the same financially eligible population.
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=ExportGenesys", new FormUrlEncodedContent(Form(await TokenAsync(client), ("sendingMode", "Auto"), ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1"))));
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, api.Exports);
        Assert.Contains("\"sendingMode\":\"Auto\"", api.LastExportBody); Assert.Contains("AllMatching", api.LastExportBody); Assert.DoesNotContain("4|3001|TP140-1", api.LastExportBody);
        Assert.Null(api.LastConfirmBody);
    }

    // ------------------------------------------------------------------ the notice after a download, the "not verified" labels, and the Manual review export

    [Fact]
    public async Task AfterADownload_ThePageSaysWhatWasExported_WhatWasLeftOut_WhatIsUnverified_AndWhoWasExportedBefore()
    {
        var api = NewApi(a => { a.ExportExcluded = 2; a.ExportDataVerified = false; a.ExportRepeated = 1; });
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=ExportGenesys", new FormUrlEncodedContent(Form(await TokenAsync(client), ("selectionMode", "AllMatching"), ("contactList", "FollowUp"))));
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);                                 // a repeat and unverified data never stop the download
        Assert.Contains("(Financial data not verified).csv", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(1, api.Exports); Assert.DoesNotContain("confirmRepeat", api.LastExportQuery);
        var page = Decode(await (await client.GetAsync(Url)).Content.ReadAsStringAsync());
        var notice = Regex.Match(page, "data-approval-notice>(.*?)</p>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("Exported 1 row(s) for the Follow Up list to genesys-CurrentMonth-to-FollowUp-20261106-0930 (Financial data not verified).csv (AED 500.00).", notice);
        Assert.Contains("Left out: 2 unit(s).", notice); Assert.Contains("2 NoCallablePhone", notice);
        Assert.Contains("Financial data not verified.", notice); Assert.Contains("has not been reconciled", notice);
        Assert.Contains("1 of the 1 customer unit(s) in this file were exported before", notice);
        Assert.DoesNotContain("Export again", page); Assert.DoesNotContain("data-repeat-export", page);           // no confirmation step exists
    }

    [Fact]
    public static void TheExportNotice_IsCompact_WhenNothingIsLeftOutOrUnverified()
    {
        var file = new GenesysExportDto("genesys-CurrentMonth-to-CurrentMonth-20261106-0930.csv", "", 37, "Current month reminder - day 14", "Current Month", CurrentMonthList, 123456.5m, "AED",
            ["Phone"], true, [], "ABC", UnitCount: 37, DataVerified: true);
        Assert.Equal("Exported 37 row(s) for the Current Month list to genesys-CurrentMonth-to-CurrentMonth-20261106-0930.csv (AED 123,456.50).", TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel.ExportNotice(file));
        var leftOut = file with { RowCount = 36, UnitCount = 36, Exclusions = [new("NoCallablePhone", 1, "No valid phone.")], ExcludedRecords = [new(4, "T0009", "TP140-9", "Customer 9", "CurrentMonth", "NoCallablePhone")] };
        Assert.Contains("Left out: 1 unit(s) (TP140-9: NoCallablePhone).", TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel.ExportNotice(leftOut));
    }

    [Fact]
    public async Task AnUnverifiedTemplate_IsLabelledInTheFileNameAndOnThePage()
    {
        var api = NewApi(a => a.SchemaVerified = false);
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=ExportGenesys", new FormUrlEncodedContent(Form(await TokenAsync(client), ("selectionMode", "AllMatching"), ("contactList", "FollowUp"))));
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("genesys-CurrentMonth-to-FollowUp-20261106-0930 (Template not verified against Genesys).csv",
            response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var page = Decode(await (await client.GetAsync(Url)).Content.ReadAsStringAsync());
        Assert.Contains("data-approval-notice", page); Assert.Contains("Template not verified against Genesys", page);
    }

    [Fact]
    public async Task ManualHasItsOwnExportReviewCsv_ThatDownloadsTheFilteredList_AndSendsNothing()
    {
        var (html, _) = await LoadAsync();
        Assert.Contains("data-export-review-manual", html); Assert.Contains("Export review CSV", html); Assert.Contains("Export for Genesys", html);   // both, as separate buttons
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=ExportReview", new FormUrlEncodedContent(Form(await TokenAsync(client), ("contactList", "FollowUp"))));
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("collections-CurrentMonth-manual-review-20261106-0930.csv", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); Assert.Contains("Manual Customer 3", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Contains("\"sendingMode\":\"Manual\"", api.LastReviewExportBody); Assert.Contains("\"reminderType\":\"CurrentMonth\"", api.LastReviewExportBody);
        Assert.Equal(0, api.Exports); Assert.Null(api.LastConfirmBody);
    }

    [Fact]
    public async Task ManualExportReviewCsv_IsUnavailableWithoutThePermission_AndShowsRefusalsOnThePage()
    {
        var (html, _) = await LoadAsync(a => a.CanSend = false);
        var button = Regex.Match(html, "<button[^>]*data-export-review-manual[^>]*>").Value;
        Assert.Contains("disabled", button);
        Assert.Contains("Collections send permission required", Decode(html));

        var api = NewApi(a => a.ReviewExportError = "There is no approval data yet. Refresh the approval data, then export.");
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var page = Decode(await (await client.PostAsync("/Collections/Campaigns?handler=ExportReview", new FormUrlEncodedContent(Form(await TokenAsync(client))))).Content.ReadAsStringAsync());
        Assert.Contains("data-approval-error", page); Assert.Contains("no approval data yet", page);
    }

    [Fact]
    public async Task AutoKeepsItsExportReviewCsv_AndHasTheSameExportForGenesysButton_ButNoSendControls()
    {
        var api = NewApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        // Auto keeps its own Export review CSV (the schedule preview's file) and gets the same audited Export for Genesys as Manual; it never gets Send to Genesys or the Manual review export.
        var html = await (await client.GetAsync("/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode=Auto")).Content.ReadAsStringAsync();
        Assert.Matches("data-export-review(-unavailable)?[ >]", html);
        var export = Regex.Match(html, "<button[^>]*data-export-genesys-button[^>]*>").Value;
        Assert.NotEmpty(export); Assert.Contains("Export for Genesys", html); Assert.DoesNotContain("Export Genesys CSV", html);   // the old unaudited Auto file is gone
        Assert.DoesNotContain("disabled", export); Assert.Contains("data-blocked=\"false\"", export);                             // approval data is current in the fake
        Assert.Contains("name=\"selectionMode\" value=\"AllMatching\"", html); Assert.Contains("name=\"sendingMode\" value=\"Auto\"", html);
        Assert.DoesNotContain("data-export-review-manual", html); Assert.DoesNotContain("data-approve-button", html); Assert.DoesNotContain("data-send-to-genesys", html);
    }

    [Fact]
    public async Task OnTheAutoPage_TheExportIsBlockedOnlyByMissingApprovalData_OldDataIsExportedWithARefreshOffered()
    {
        const string auto = "/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode=Auto";
        var (missing, _) = await LoadAsync(a => a.Run = null, auto);
        var button = Regex.Match(missing, "<button[^>]*data-export-genesys-button[^>]*>").Value;
        Assert.Contains("disabled", button); Assert.Contains("no approval data yet", Decode(missing)); Assert.Contains("data-refresh-approval", missing);
        // Stale, unreconciled, or built on another day: the export stays available (the file says what is unverified); the refresh is offered beside it.
        foreach (var old in new Func<ReviewRunDto>[] { () => Run(stale: true), () => Run(reconciled: false), () => Run(asOf: DubaiToday.AddDays(-1)) })
        {
            var (page, _) = await LoadAsync(a => a.Run = old(), auto);
            Assert.DoesNotContain("disabled", Regex.Match(page, "<button[^>]*data-export-genesys-button[^>]*>").Value); Assert.Contains("handler=ExportGenesys", page);
            Assert.Contains("held-back ones included", ExportTip(page));
        }
        Assert.Contains("data-refresh-approval", (await LoadAsync(a => a.Run = Run(stale: true), auto)).Html);
        Assert.DoesNotContain("data-refresh-approval", (await LoadAsync(a => a.Run = Run(reconciled: false), auto)).Html);
        // Without the send permission (the preview's CanExportReview) the button is disabled, and no export request can be posted from the page.
        var (noRight, _) = await LoadAsync(a => ((FakeCollectionsApi)a.InnerHandler!).ExportForbidden = true, "/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14&sendingMode=Auto");
        Assert.Contains("disabled", Regex.Match(noRight, "<button[^>]*data-export-genesys-button[^>]*>").Value); Assert.DoesNotContain("handler=ExportGenesys", noRight);
        Assert.Contains("does not have permission", Decode(noRight));
    }

    // ------------------------------------------------------------------ refreshing the approval data by hand

    [Fact]
    public async Task RefreshApprovalData_StartsTheExistingRefresh_AndAFreshResultIsExplained()
    {
        var api = NewApi();                                                                                      // the API answers with the run that was just published (the in-process cooldown)
        using var factory = CollectionsWebHost.Factory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync("/Collections/Campaigns?handler=RefreshApproval", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = await TokenAsync(client), ["returnUrl"] = "/Collections/Campaigns?stage=CurrentMonthReminder&sendingMode=Manual" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(api.All, call => call.StartsWith("POST") && call.Contains("/review/refresh"));
        var page = await (await client.GetAsync(response.Headers.Location)).Content.ReadAsStringAsync();
        Assert.Contains("data-approval-notice", page); Assert.Contains("is current. A refresh is not repeated within a minute", Decode(page));
    }

    // ------------------------------------------------------------------ clickable summary cards (view filters)

    private static string CardTag(string html, string card) => Regex.Match(html, $"<li[^>]*data-card=\"{card}\"[^>]*>").Value;
    private static string CardLink(string html, string card) => Decode(Regex.Match(html, $"<a[^>]*data-card-filter=\"{card}\"[^>]*>").Value);

    [Fact]
    public async Task SummaryCards_AreLinks_ExceptTotalAmount_AndTheirCountsAreUnchanged()
    {
        var (html, _) = await LoadAsync();
        foreach (var card in new[] { "all", "eligible", "heldback", "sent" }) Assert.Contains("data-results-link", CardLink(html, card));
        var total = Regex.Match(html, "<li[^>]*>\\s*<span class=\"campaign-card__label\">Total amount.*?</li>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("<a ", total); Assert.DoesNotContain("data-card", total); Assert.Contains("data-eligible-total", total);   // not clickable
        Assert.Matches("data-ready-count>2<", html); Assert.Matches("data-blocked-count>3<", html); Assert.Matches("data-eligible-count>5<", html); Assert.Matches("data-sent-count>0<", html);
    }

    [Theory]
    [InlineData("", "all", null)]
    [InlineData("&card=all", "all", null)]
    [InlineData("&card=eligible", "eligible", "validationStatus=Ready")]
    [InlineData("&card=heldback", "heldback", "validationStatus=HeldBack")]
    [InlineData("&card=sent", "sent", "validationStatus=AlreadySent")]
    [InlineData("&card=bogus", "all", null)]
    public async Task EachCard_AsksForItsOwnStatus_HighlightsItself_AndKeepsEveryFilter(string cardParam, string active, string? expectedStatus)
    {
        var (html, api) = await LoadAsync(url: Url + "&towerId=7&paymentStatus=Overdue&dateFrom=2026-08-01&dateTo=2026-09-30&minTotal=250&search=Manual" + cardParam);
        var query = Assert.Single(api.RecordQueries);
        if (expectedStatus is null) Assert.DoesNotContain("validationStatus", query); else Assert.Contains(expectedStatus, query);
        // the existing filters, dates, campaign and search are untouched
        Assert.Contains("reminderType=CurrentMonth", query); Assert.Contains("towerId=7", query); Assert.Contains("dueStatus=Overdue", query);
        Assert.Contains("dueFrom=2026-08-01", query); Assert.Contains("dueTo=2026-09-30", query); Assert.Contains("minRemaining=250", query); Assert.Contains("customer=Manual", query);
        // only the active card is highlighted (a subtle border via data-active)
        foreach (var card in new[] { "all", "eligible", "heldback", "sent" })
            Assert.Contains($"data-active=\"{(card == active ? "true" : "false")}\"", CardTag(html, card));
        Assert.Contains("aria-pressed=\"true\"", CardLink(html, active));
        // every card link keeps the filters and goes to page 1
        foreach (var card in new[] { "eligible", "heldback", "sent" })
        {
            var href = CardLink(html, card);
            Assert.Contains($"card={card}", href); Assert.Contains("page=1&", href + "&"); Assert.Contains("towerId=7", href); Assert.Contains("dateFrom=2026-08-01", href);
            Assert.Contains("minTotal=250", href); Assert.Contains("paymentStatus=Overdue", href); Assert.Contains("search=Manual", href); Assert.Contains("sendingMode=Manual", href);
        }
        var reset = CardLink(html, "all");
        Assert.DoesNotContain("card=", reset); Assert.Contains("page=1&", reset + "&"); Assert.Contains("towerId=7", reset);       // Customers resets the card filter
    }

    [Fact]
    public async Task SwitchingCard_ResetsPagination_AndPaginationKeepsTheActiveCard()
    {
        var (html, _) = await LoadAsync(url: Url + "&card=eligible&page=3");
        foreach (var card in new[] { "all", "eligible", "heldback", "sent" }) Assert.Contains("page=1", CardLink(html, card));      // never page 3
        Assert.DoesNotContain("page=3", string.Concat(new[] { "all", "eligible", "heldback", "sent" }.Select(c => CardLink(html, c))));
    }

    [Fact]
    public async Task ClickingACard_IsAReadOnlyGet_SendsNothing_AndTheApprovalIsNotNarrowedByIt()
    {
        var (html, api) = await LoadAsync(url: Url + "&card=heldback");
        Assert.All(api.All, call => Assert.StartsWith("GET ", call));                                           // no POST: nothing is approved, exported or dispatched
        Assert.DoesNotContain(api.All, call => call.Contains("/dispatches") && call.StartsWith("POST"));
        Assert.Equal(0, api.Exports);
        var form = Regex.Match(html, "<form[^>]*data-approve-form.*?</form>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("name=\"card\"", form); Assert.DoesNotContain("validationStatus", form);          // the send / export always re-resolves the whole filtered population
        Assert.Contains("name=\"sendingMode\" value=\"Manual\"", form);
    }

    [Fact]
    public async Task HeldBackCard_ShowsEveryHeldBackRowWithItsReason_AndItsCountIsExcludedPlusNeedsReview()
    {
        var (html, api) = await LoadAsync(a => a.Records = [Row(3, "Excluded", ReviewReasons.ManualSharedPhone), Row(4, "Excluded", ReviewReasons.LegalNoticeReleaseRequired), Row(5, "NeedsReview", ReviewReasons.NoValidContact)],
            url: Url + "&card=heldback");
        Assert.Contains("validationStatus=HeldBack", Assert.Single(api.RecordQueries));
        Assert.Equal(3, Regex.Matches(html, "data-reason-code=\"").Count);                                    // each row states its reason
        Assert.Matches("data-blocked-count>3<", html);
        Assert.Equal(3, Regex.Matches(html, "<tr data-campaign-status=").Count);                               // rows shown == the card count
    }

    [Fact]
    public async Task TheSnapshotStrip_NoLongerSaysNotLoadedYet_OnceAManualListHasLoaded()
    {
        var (html, _) = await LoadAsync(url: Url.Replace("render=full", "handler=Results"));
        Assert.Contains("data-manual-snapshot-status", html); Assert.Contains("data-status-pill=\"reconciliation\"", html); Assert.Contains("data-status-pill=\"snapshot\"", html);
        Assert.DoesNotContain("Snapshot not loaded yet", html);
        var (stale, _) = await LoadAsync(a => a.Run = null, url: Url.Replace("render=full", "handler=Results"));
        Assert.DoesNotContain("Snapshot not loaded yet", stale);
    }
}
