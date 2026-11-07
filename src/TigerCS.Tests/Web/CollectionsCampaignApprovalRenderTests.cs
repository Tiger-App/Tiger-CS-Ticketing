extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Tests.Web;

/// <summary>Select Customers, then Approve &amp; Send, all on Collections Campaigns: no separate Review step, with run, send and email status shown in place.</summary>
public sealed class CollectionsCampaignApprovalRenderTests
{
    private static readonly DateTime Read = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid DispatchId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string ListId = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1";

    private static ReviewRunDto Run(string state = "Completed", string? error = null, bool stale = false) => new(7, state, state == "Running" ? "Validating records" : state,
        state == "Running" ? 45 : 100, new DateOnly(2026, 10, 14), null, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), "PACT receivables (companies 4 and 32)",
        "", true, Read, Read, Read, Read, 1234, 56, error, state == "Completed", stale);

    private static DispatchDto Dispatch(string status = "Completed", int uploaded = 2, int failed = 0, int unknown = 0, string email = "Sent", string? emailError = null,
        string batchStatus = "Uploaded") => new(DispatchId, status, null, Guid.NewGuid(), Read, Read, Read, uploaded + failed + unknown, uploaded, 0, failed, unknown,
        new Dictionary<string, decimal> { ["AED"] = 1234.5m },
        [new DispatchBatchDto(1, "LegalNotice", ListId, 1, uploaded + failed + unknown, batchStatus, 1, 200, uploaded, null, Read, Read, null, null,
            "Legal notice - days 12 and 14", "Legal notice - days 12 and 14", "cmp-123", "ReusedExisting")],
        DispatchService.UploadDisclaimer, null, null, 0, 0, 0, "Nida Basem", email, emailError, Read, 1);

    private sealed class Api(FakeCollectionsApi inner) : DelegatingHandler(inner)
    {
        public ReviewRunDto? Run { get; set; } = CollectionsCampaignApprovalRenderTests.Run();
        public DispatchDto? Sent { get; set; }
        public SelectionSummaryDto Summary { get; set; } = new(7, 2, "FP", new Dictionary<string, decimal> { ["AED"] = 1234.5m }, new Dictionary<string, int> { ["LegalNotice"] = 2 }, 0, 0, 1, 50,
            [new SelectionContactDto("K1", "LegalNotice", "A", "+971500000001", "a@x.test", "TP140-1", 600m, "AED", new DateOnly(2026, 9, 10), false),
             new SelectionContactDto("K2", "LegalNotice", "B", "+971500000002", "b@x.test", "TP140-2", 634.5m, "AED", new DateOnly(2026, 9, 11), false)], [],
            new Dictionary<string, string> { ["LegalNotice"] = ListId });
        public int Refreshes { get; private set; }
        public int EmailRetries { get; private set; }
        public string? LastSummaryBody { get; private set; }
        public string? LastConfirmBody { get; private set; }
        /// <summary>What the read-only Genesys connection check answers (null = the API refuses it); <see cref="Checks"/> counts the calls.</summary>
        public GenesysConnectionCheckDto? Check { get; set; }
        public int Checks { get; private set; }
        /// <summary>When set, the confirmation is refused with this status and problem detail (as the API does); <see cref="ConfirmUnreachable"/> makes the API unreachable instead.</summary>
        public HttpStatusCode? ConfirmFailure { get; set; }
        public string? ConfirmDetail { get; set; }
        public bool ConfirmUnreachable { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(object? value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
            if (path.EndsWith("/runs/current")) return Json(Run);
            if (path.EndsWith("/refresh")) { Refreshes++; Run = CollectionsCampaignApprovalRenderTests.Run("Queued"); return Json(Run, HttpStatusCode.Accepted); }
            if (path.EndsWith("/selection/summary")) { LastSummaryBody = await request.Content!.ReadAsStringAsync(cancellationToken); return Json(Summary); }
            if (path.EndsWith("/overlaps")) return Json(new OverlapPageDto(0, 1, 25, []));
            if (path.EndsWith("/admin-email/retry")) { EmailRetries++; Sent = Dispatch(email: "Sent"); return Json(Sent); }
            if (path.EndsWith("/dispatches/readiness")) return Json(new DispatchReadinessDto(true, []));
            if (path.EndsWith("/dispatches/genesys-check"))
            {
                Checks++;
                return Check is { } check ? Json(check) : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"title\":\"Forbidden\",\"detail\":\"no\",\"status\":403}", System.Text.Encoding.UTF8, "application/problem+json") };
            }
            if (path.EndsWith("/dispatches") && request.Method == HttpMethod.Post)
            {
                LastConfirmBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (ConfirmUnreachable) throw new HttpRequestException("connection reset");
                if (ConfirmFailure is { } failure)
                    return new HttpResponseMessage(failure) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { title = "Refused", detail = ConfirmDetail, status = (int)failure }), System.Text.Encoding.UTF8, "application/problem+json") };
                return Json(Dispatch("Queued", 0), HttpStatusCode.Accepted);
            }
            if (path.EndsWith("/dispatches")) return Json(Sent is null ? new List<DispatchDto>() : new List<DispatchDto> { Sent });
            if (path.Contains("/dispatches/")) return Sent is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Sent);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static WebApplicationFactory<TigerCsWeb::Program> Factory(Api api) => CollectionsWebHost.Factory(api);

    private static Api NewApi(Action<FakeCollectionsApi>? configure = null)
    {
        var inner = new FakeCollectionsApi { IncludeNotEligible = true };
        configure?.Invoke(inner);
        return new Api(inner);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var page = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&sendingMode=Manual")).Content.ReadAsStringAsync();
        return WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static Dictionary<string, string> Form(string token, params (string Key, string Value)[] extra)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["sendingMode"] = "Manual", ["stage"] = "LegalNotice", ["businessDate"] = "2026-10-14", ["dateFrom"] = "2026-09-01", ["dateTo"] = "2026-10-31",
            ["towerId"] = "7", ["minTotal"] = "250", ["paymentStatus"] = "Overdue", ["search"] = "3001"
        };
        foreach (var (key, value) in extra) form[key] = value;
        return form;
    }

    // ------------------------------------------------------------------ one page, no Review step

    [Fact]
    public async Task ManualApproval_SendsTheModeAndTheCampaign_ToTheApi_WithNoSendDate()
    {
        var api = NewApi();
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1"))))).Content.ReadAsStringAsync();
        Assert.Contains("\"sendingMode\":\"Manual\"", api.LastSummaryBody); Assert.Contains("\"reminderType\":\"LegalNotice\"", api.LastSummaryBody); Assert.DoesNotContain("sendDate", api.LastSummaryBody!);
        Assert.Contains("data-snapshot-mode>Manual (any time; no send date or schedule)<", html);
        Assert.Null(api.LastConfirmBody);                                          // approving never sends
    }

    [Fact]
    public async Task Campaigns_OffersSendToGenesys_OnlyForReadyCustomers_AndLinksToNoReviewPage()
    {
        using var factory = Factory(NewApi()); using var client = factory.CreateClient();
        var full = await (await client.GetAsync("/Collections/Campaigns?render=full&stage=CurrentMonthReminder&sendingMode=Manual")).Content.ReadAsStringAsync();
        Assert.Contains(">Send to Genesys<", full);
        Assert.Equal(2, Regex.Matches(full, "class=\"campaign-select\"").Count);          // the Ready customers only
        Assert.Contains("data-all-matching", full);                                          // "all eligible customers matching the filters"
        Assert.Contains("data-unit-key=\"4|3001|TP140-1\"", full);
        Assert.DoesNotContain("/Collections/Review", full);
        Assert.DoesNotContain("Review &amp; approve", full);
        Assert.Contains("TP140-1", full);
    }

    [Fact]
    public async Task TheOldReviewAddress_OnlyForwardsToCampaigns_AndKeepsASendLinkWorking()
    {
        using var factory = Factory(NewApi());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var plain = await client.GetAsync("/Collections/Review");
        Assert.Equal(HttpStatusCode.Redirect, plain.StatusCode);
        Assert.Equal("/Collections/Campaigns", plain.Headers.Location!.OriginalString);
        var send = await client.GetAsync($"/Collections/Review?dispatch={DispatchId}");
        Assert.Equal($"/Collections/Campaigns?dispatch={DispatchId:D}", send.Headers.Location!.OriginalString);
    }

    // ------------------------------------------------------------------ the approval data refresh is never left on Queued

    [Fact]
    public async Task AFailedRefresh_IsShownWithItsReasonAndARetry_NeverAsQueued()
    {
        using var factory = Factory(NewApi() is var api ? Set(api, a => a.Run = Run("Failed", "Background jobs are disabled on this server.")) : null!); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns")).Content.ReadAsStringAsync();
        Assert.Contains("data-run-error", html);
        Assert.Contains("Background jobs are disabled on this server.", html);
        Assert.Contains("data-run-retry", html);
        Assert.DoesNotContain("data-run-active=\"true\"", html);
        Assert.DoesNotContain("Queued", html);
    }

    [Fact]
    public async Task ARunningRefresh_ShowsItsProgress_AndBrowsingNeverStartsOne()
    {
        var api = Set(NewApi(), a => a.Run = Run("Running"));
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns")).Content.ReadAsStringAsync();
        Assert.Contains("data-run-active=\"true\"", html);
        Assert.Contains("value=\"45\"", html);
        Assert.Contains("Validating records", html);
        Assert.Equal(0, api.Refreshes);
    }

    private static Api Set(Api api, Action<Api> set) { set(api); return api; }

    [Fact]
    public async Task ApprovingWithoutPreparedData_StartsAVisibleRefresh_InsteadOfSendingFromAGuess()
    {
        var api = Set(NewApi(), a => a.Run = null);
        using var factory = Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client), ("selectionMode", "AllMatching"))));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, api.Refreshes);
        Assert.Contains("is being prepared", html);
        Assert.DoesNotContain("data-selection-snapshot", html);
        Assert.Null(api.LastConfirmBody);
    }

    // ------------------------------------------------------------------ approval: exact confirmation

    [Fact]
    public async Task TheConfirmation_ShowsExactCount_Total_Stage_AndDestinationContactList_AndSendsNothingYet()
    {
        var api = NewApi();
        using var factory = Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1,4|3002|TP140-2"))));
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        var snapshot = Regex.Match(html, "data-selection-snapshot.*?</dl>", RegexOptions.Singleline).Value;
        Assert.Contains("data-snapshot-count>2<", snapshot);
        Assert.Contains("data-snapshot-total>AED 1,234.50<", snapshot);
        Assert.Contains("data-snapshot-stage>Legal notice - days 12 and 14<", snapshot);
        Assert.Contains(ListId, snapshot);
        Assert.Null(api.LastConfirmBody);                                                    // nothing was sent by approving
        // The selection and the preview's filters travel to the API exactly.
        Assert.Contains("SelectedUnits", api.LastSummaryBody); Assert.Contains("4|3001|TP140-1", api.LastSummaryBody);
        Assert.Contains("\"reminderType\":\"LegalNotice\"", api.LastSummaryBody); Assert.Contains("\"towerId\":7", api.LastSummaryBody);
        Assert.Contains("\"dueStatus\":\"Overdue\"", api.LastSummaryBody); Assert.Contains("\"minRemaining\":250", api.LastSummaryBody);
        Assert.Contains("\"dueFrom\":\"2026-09-01\"", api.LastSummaryBody);
        Assert.Contains("name=\"idempotencyKey\"", html); Assert.Contains("name=\"expectedFingerprint\" value=\"FP\"", html); Assert.Contains("required", Regex.Match(html, "<input[^>]*name=\"ackCampaign\"[^>]*>").Value);
    }

    [Fact]
    public async Task ConfirmingCreatesTheApproval_AndOpensTheSendOnTheSamePage()
    {
        var api = NewApi();
        using var factory = Factory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync("/Collections/Campaigns?handler=Confirm", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "AllMatching"), ("expectedCount", "2"), ("expectedFingerprint", "FP"), ("idempotencyKey", "key-1"), ("ackCampaign", "true"))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Collections/Campaigns?dispatch={DispatchId:D}", response.Headers.Location!.OriginalString);
        Assert.Contains("\"idempotencyKey\":\"key-1\"", api.LastConfirmBody); Assert.Contains("\"expectedCount\":2", api.LastConfirmBody);
        Assert.Contains("\"expectedFingerprint\":\"FP\"", api.LastConfirmBody); Assert.Contains("AllMatching", api.LastConfirmBody);
    }

    // ------------------------------------------------------------------ the confirmation dialog: loading state, JSON answers, status counts

    private static async Task<(HttpResponseMessage Response, System.Text.Json.JsonElement Body)> ConfirmAjaxAsync(Api api, string mode = "Manual")
    {
        using var factory = Factory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Collections/Campaigns?handler=Confirm")
        {
            Content = new FormUrlEncodedContent(Form(await TokenAsync(client), ("sendingMode", mode), ("selectionMode", "AllMatching"), ("expectedCount", "2"), ("expectedFingerprint", "FP"), ("idempotencyKey", "key-1"), ("ackCampaign", "true")))
        };
        request.Headers.Add("X-Requested-With", "tigercs-confirm"); request.Headers.Add("Accept", "application/json");
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, System.Text.Json.JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task TheDialogsConfirmation_IsAnsweredWithJson_AcceptedIsNotSuccess_AndTheSameChecksRun()
    {
        var api = NewApi();
        var (response, body) = await ConfirmAjaxAsync(api);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                                // no redirect: the dialog follows the send itself
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(DispatchId.ToString("D"), body.GetProperty("dispatchId").GetString());
        Assert.Equal("Queued", body.GetProperty("status").GetString());                      // queued, not uploaded: the upload result comes from the send's status
        Assert.Contains("\"idempotencyKey\":\"key-1\"", api.LastConfirmBody); Assert.Contains("\"expectedFingerprint\":\"FP\"", api.LastConfirmBody);
        Assert.Contains("\"acknowledgeActiveCampaignRisk\":true", api.LastConfirmBody);   // the same request the plain post sends
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "The records, amounts or contacts changed since this list was shown. Review the current list and confirm again.", "Conflict", false)]
    [InlineData(HttpStatusCode.BadRequest, "The financial source is not reconciled (no valid sign-off and snapshot-consistency evidence for the PACT procedure in use), so nothing can be approved or sent. Reconcile it first.", "ValidationError", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Customer dispatch to Genesys is switched off in this environment.", "ServiceUnavailable", true)]
    [InlineData(HttpStatusCode.Locked, "Another send of this list is being prepared.", "Locked", true)]
    public async Task ARefusal_CarriesTheBackendsOwnMessage_AndRetryIsOfferedOnlyForADefiniteRefusalThatCreatedNothing(HttpStatusCode status, string detail, string outcome, bool retry)
    {
        var api = Set(NewApi(), a => { a.ConfirmFailure = status; a.ConfirmDetail = detail; });
        var (response, body) = await ConfirmAjaxAsync(api);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(detail, body.GetProperty("message").GetString());                       // verbatim, never replaced by a generic text
        Assert.Equal(outcome, body.GetProperty("outcome").GetString());
        Assert.Equal(retry, body.GetProperty("retry").GetBoolean()); Assert.False(body.GetProperty("unknown").GetBoolean());
    }

    [Fact]
    public async Task APermissionRefusal_NeverOffersARetry()
    {
        var (_, body) = await ConfirmAjaxAsync(Set(NewApi(), a => { a.ConfirmFailure = HttpStatusCode.Forbidden; a.ConfirmDetail = "no"; }));
        Assert.False(body.GetProperty("ok").GetBoolean()); Assert.False(body.GetProperty("retry").GetBoolean());
        Assert.Contains("does not have permission", body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.InternalServerError)]
    [InlineData(false, HttpStatusCode.BadGateway)]
    public async Task AnAnswerThatDoesNotSayWhetherTheApprovalWasCreated_IsUnknown_NeverRetryable(bool unreachable, HttpStatusCode status)
    {
        var api = Set(NewApi(), a => { a.ConfirmUnreachable = unreachable; if (!unreachable) { a.ConfirmFailure = status; a.ConfirmDetail = "boom"; } });
        var (_, body) = await ConfirmAjaxAsync(api);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("unknown").GetBoolean()); Assert.False(body.GetProperty("retry").GetBoolean());
        Assert.Contains("not known whether this send was approved", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheDialogsConfirmation_IsRefusedInAutoMode_WithoutCallingTheApi()
    {
        var api = NewApi();
        var (_, body) = await ConfirmAjaxAsync(api, mode: "Auto");
        Assert.False(body.GetProperty("ok").GetBoolean()); Assert.Contains("Manual only", body.GetProperty("message").GetString());
        Assert.Null(api.LastConfirmBody);
    }

    [Fact]
    public async Task WithoutTheDialogHeader_ConfirmingStillRedirects_AndARefusalStillReRendersThePage()
    {
        var failing = Set(NewApi(), a => { a.ConfirmFailure = HttpStatusCode.ServiceUnavailable; a.ConfirmDetail = "Customer dispatch to Genesys is switched off in this environment."; });
        using var factory = Factory(failing); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync("/Collections/Campaigns?handler=Confirm", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "AllMatching"), ("expectedCount", "2"), ("expectedFingerprint", "FP"), ("idempotencyKey", "key-1"), ("ackCampaign", "true"))));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-approval-error", html); Assert.Contains("Customer dispatch to Genesys is switched off in this environment.", html);
    }

    [Fact]
    public async Task TheSendStatus_CarriesTheCountsTheDialogReports_AndIsActiveOnlyWhileTheSendRuns()
    {
        var api = Set(NewApi(), a => a.Sent = Dispatch("CompletedWithErrors", uploaded: 3, failed: 1));
        using var factory = Factory(api); using var client = factory.CreateClient();
        var done = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/Collections/Campaigns?handler=Send&dispatch={DispatchId:D}")).RootElement;
        Assert.Equal("CompletedWithErrors", done.GetProperty("status").GetString()); Assert.False(done.GetProperty("active").GetBoolean());
        Assert.Equal((4, 3, 1, 0, 0), (done.GetProperty("approvedCount").GetInt32(), done.GetProperty("uploadedCount").GetInt32(), done.GetProperty("failedCount").GetInt32(),
            done.GetProperty("unknownCount").GetInt32(), done.GetProperty("excludedCount").GetInt32()));
        api.Sent = Dispatch("Sending", uploaded: 0);
        var running = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/Collections/Campaigns?handler=Send&dispatch={DispatchId:D}")).RootElement;
        Assert.True(running.GetProperty("active").GetBoolean()); Assert.Equal(0, running.GetProperty("uploadedCount").GetInt32());
        api.Sent = null;                                                                     // the API cannot answer: never active, never success
        var gone = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/Collections/Campaigns?handler=Send&dispatch={DispatchId:D}")).RootElement;
        Assert.Equal("Unknown", gone.GetProperty("status").GetString()); Assert.False(gone.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task TheConfirmationDialog_KeepsItsStateAtTheTop_ItsListScrolling_AndItsButtonsInView()
    {
        var api = NewApi();
        using var factory = Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client), ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1"))));
        var html = await response.Content.ReadAsStringAsync();
        var form = Regex.Match(html, "<form[^>]*data-confirm-form.*?</form>", RegexOptions.Singleline).Value;
        // Order inside the dialog: title, then the sending / result state, then the scrolling list, then the footer with Confirm and Cancel.
        var order = new[] { "manualConfirmTitle", "data-confirm-status", "data-confirm-body", "data-confirm-footer", "data-confirm-button", "data-confirm-cancel" }.Select(m => form.IndexOf(m, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        var status = Regex.Match(form, "<div[^>]*data-confirm-status[^>]*>").Value;
        Assert.Contains("hidden", status);                                                   // nothing is shown until the person confirms
        Assert.Contains("data-confirm-spinner", form); Assert.Contains("data-confirm-title", form); Assert.Contains("data-confirm-detail", form); Assert.Contains("data-confirm-actions", form);
        // What the script needs to keep the page state: where "Back to the list" returns (campaign, filters, Manual mode) and the status / details endpoints.
        var back = WebUtility.HtmlDecode(Regex.Match(form, "data-return-url=\"([^\"]+)\"").Groups[1].Value);
        Assert.Contains("sendingMode=Manual", back); Assert.Contains("stage=LegalNotice", back); Assert.Contains("minTotal=250", back);
        Assert.Contains("data-send-status-url=\"/Collections/Campaigns?handler=Send&amp;dispatch=\"", form); Assert.Contains("data-send-details-url=\"/Collections/Campaigns?dispatch=\"", form);
        Assert.Contains("name=\"idempotencyKey\"", form); Assert.Contains("name=\"ackCampaign\"", form);
        Assert.Contains("/js/collections-campaign-confirm.js", html);
        Assert.Null(api.LastConfirmBody);
    }

    // ------------------------------------------------------------------ the read-only Genesys connection check

    private static GenesysConnectionCheckDto CheckResult() => new(Read, false, "login.mypurecloud.de", "api.mypurecloud.de",
    [
        new("Region", "Ok", "Both name the region mypurecloud.de."),
        new("OAuth client and organization", "Ok", "Authenticated; the client acts in organization \"Tiger Test Org\" (id org-1)."),
        new("ContactListIds:FollowUp", "Failed", "HTTP 404: list 3a91c06e-47ab-4a5e-a720-004bd5cf5bba does not exist in organization \"Tiger Test Org\". Correct Collections:GenesysOutbound:ContactListIds:FollowUp.")
    ]);

    private static async Task<string> RefusedConfirmationAsync(Api api, string detail, HttpStatusCode status)
    {
        api.ConfirmFailure = status; api.ConfirmDetail = detail;
        using var factory = Factory(api); using var client = factory.CreateClient();
        var response = await client.PostAsync("/Collections/Campaigns?handler=Confirm", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "AllMatching"), ("expectedCount", "2"), ("expectedFingerprint", "FP"), ("idempotencyKey", "key-1"), ("ackCampaign", "true"))));
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task TheCheckIsOfferedOnlyWhereASendStoppedBecauseOfGenesys_AndSendsNothing()
    {
        var genesys = await RefusedConfirmationAsync(NewApi(), "The Follow Up contact list could not be checked in Genesys (HTTP 404). Nothing was uploaded.", HttpStatusCode.BadRequest);
        Assert.Contains("data-check-genesys", genesys); Assert.Contains("Read-only: nothing is uploaded or changed.", genesys);
        var returnUrl = WebUtility.HtmlDecode(Regex.Match(genesys, "<form[^>]*>\\s*<input type=\"hidden\" name=\"returnUrl\" value=\"([^\"]+)\"", RegexOptions.Singleline).Groups[1].Value);
        Assert.Contains("sendingMode=Manual", returnUrl); Assert.Contains("stage=LegalNotice", returnUrl);          // the same preview: campaign, filters and Manual mode
        var other = await RefusedConfirmationAsync(NewApi(), "No Ready records are selected.", HttpStatusCode.BadRequest);
        Assert.DoesNotContain("data-check-genesys", other);                                                          // not offered for an unrelated refusal
    }

    [Fact]
    public async Task PressingTheCheck_CallsTheReadOnlyEndpoint_ReturnsToTheSamePreview_AndShowsEachLineOnce()
    {
        var api = Set(NewApi(), a => a.Check = CheckResult());
        using var factory = Factory(api); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        const string back = "/Collections/Campaigns?stage=LegalNotice&sendingMode=Manual&minTotal=250";
        var response = await client.PostAsync("/Collections/Campaigns?handler=CheckGenesys", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = await TokenAsync(client), ["returnUrl"] = back }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(back, response.Headers.Location!.OriginalString);
        Assert.Equal(1, api.Checks); Assert.Null(api.LastConfirmBody);                                                  // one read-only call, nothing approved or sent
        var html = await (await client.GetAsync(back)).Content.ReadAsStringAsync();
        Assert.Contains("data-genesys-check", html); Assert.Contains("data-genesys-check-ok=\"false\"", html); Assert.Contains("a problem was found", html);
        Assert.Contains("login.mypurecloud.de", html); Assert.Contains("api.mypurecloud.de", html);
        Assert.Equal(3, Regex.Matches(html, "data-check-line").Count);
        Assert.Contains("data-check-status=\"Failed\"", html); Assert.Contains("ContactListIds:FollowUp", html);
        Assert.Contains("HTTP 404: list 3a91c06e-47ab-4a5e-a720-004bd5cf5bba does not exist", html);
        Assert.Contains("nothing was uploaded or changed, and no customer was contacted", html);
        var again = await (await client.GetAsync(back)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-genesys-check", again);                                                          // shown once
    }

    [Fact]
    public async Task ARefusedCheck_ShowsTheRealReason_AndAnUnsafeReturnAddressIsIgnored()
    {
        var api = NewApi();                                                                                            // no result: the API refuses (403)
        using var factory = Factory(api); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync("/Collections/Campaigns?handler=CheckGenesys", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = await TokenAsync(client), ["returnUrl"] = "https://evil.test/" }));
        Assert.Equal("/Collections/Campaigns", response.Headers.Location!.OriginalString);
        var html = await (await client.GetAsync("/Collections/Campaigns")).Content.ReadAsStringAsync();
        Assert.Contains("data-approval-error", html); Assert.DoesNotContain("data-genesys-check=", html);
    }

    [Fact]
    public async Task ASourceThatIsNotReconciled_ExplainsWhyNothingCanBeSent()
    {
        var api = Set(NewApi(), a => a.Summary = a.Summary with { Count = 0, SourceReconciled = false, Contacts = [], TotalsByCurrency = new Dictionary<string, decimal>() });
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client), ("selectionMode", "AllMatching"))))).Content.ReadAsStringAsync();
        Assert.Contains("has not been reconciled against PACT", html);
        Assert.DoesNotContain("data-selection-snapshot", html);
    }

    // ------------------------------------------------------------------ send status, email status, retry

    [Theory]
    [InlineData("Completed", 2, 0, 0, "Sent", "send-verdict--success", "Success")]
    [InlineData("CompletedWithErrors", 1, 1, 0, "Sent", "send-verdict--partial", "Partial failure")]
    [InlineData("CompletedWithErrors", 0, 2, 0, "Sent", "send-verdict--failure", "Failure")]
    [InlineData("Sending", 0, 0, 0, "None", "send-verdict--running", "In progress")]
    public async Task TheSendShowsSuccessPartialFailureOrProgress_NeverSentBeforeGenesysConfirms(string status, int uploaded, int failed, int unknown, string email, string css, string text)
    {
        var api = Set(NewApi(), a => a.Sent = Dispatch(status, uploaded, failed, unknown, email));
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?dispatch={DispatchId}")).Content.ReadAsStringAsync();
        Assert.Contains(css, html); Assert.Contains(text, html);
        Assert.Contains("approved by Nida Basem", html);
        Assert.Contains("data-campaign-id>cmp-123<", html);
        Assert.Contains(ListId, html);
        if (status == "Sending") Assert.Contains("data-dispatch-active=\"true\"", html);
        Assert.Contains("does not mean the customer was called", html);
    }

    [Theory]
    [InlineData("Sent", false, "Administrator report emailed")]
    [InlineData("Failed", true, "email failed")]
    [InlineData("NotConfigured", true, "NOT sent")]
    [InlineData("Disabled", false, "switched off")]
    public async Task TheAdminEmailStatus_IsShownSeparately_AndRetryOnlyOffersTheEmail(string email, bool retry, string text)
    {
        var api = Set(NewApi(), a => a.Sent = Dispatch(email: email, emailError: email == "Sent" ? null : "no recipient"));
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?dispatch={DispatchId}")).Content.ReadAsStringAsync();
        Assert.Contains($"data-admin-email-status=\"{email}\"", html);
        Assert.Contains(text, html);
        Assert.Equal(retry, html.Contains("data-retry-email"));
        Assert.Contains("never repeated", retry ? html : "never repeated");
        Assert.Contains("Success", html);                                                      // the Genesys result is unaffected by the mail status
    }

    [Fact]
    public async Task TheConfirmation_KeepsTheAcknowledgementVisible_AndMovesTheCheckExplanationIntoAnInfoTooltip()
    {
        using var factory = Factory(NewApi()); using var client = factory.CreateClient();
        var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
            ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1"))))).Content.ReadAsStringAsync();
        var tip = Regex.Match(html, "id=\"tip-confirm-checks\" role=\"tooltip\" data-info-tip-content>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("checked again against the financial snapshot", tip); Assert.Contains("emailed to the Collections administrator", tip);
        var visible = Regex.Replace(html, "<span class=\"info-tip__content\".*?</span>", "", RegexOptions.Singleline);
        Assert.DoesNotContain("checked again against the financial snapshot", visible);
        Assert.Contains("I understand that an active Genesys campaign using these lists may start calling the uploaded contacts immediately", visible);   // the mandatory warning is never tooltip-only
        Assert.Contains("data-confirm-button", visible);
    }

    [Fact]
    public async Task TheSendResult_KeepsItsFiguresAndStatuses_AndPutsTheirGlossInInfoTooltips()
    {
        var api = Set(NewApi(), a => a.Sent = Dispatch(email: "Failed", emailError: "smtp busy"));
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?dispatch={DispatchId}")).Content.ReadAsStringAsync();
        var tally = Regex.Match(html, "<ul class=\"review-tally\">.*?</ul>", RegexOptions.Singleline).Value;
        Assert.Contains("uploaded to Genesys<span class=\"info-tip\"", tally); Assert.Contains("excluded at sending<span class=\"info-tip\"", tally); Assert.Contains("rejected by Genesys<span class=\"info-tip\"", tally);
        foreach (var gloss in new[] { "Accepted by Genesys.", "Paid or settled.", "Nothing created." }) Assert.Contains($"data-info-tip-content>{gloss}</span>", tally);
        Assert.Contains("unconfirmed", tally);
        // The retry-email button keeps its hook and form; its note is a tooltip, and the failure itself stays visible.
        var retry = Regex.Match(html, "<span class=\"campaign-action\">\\s*<button[^>]*data-retry-email[^>]*>Retry administrator email</button>\\s*<span class=\"info-tip\".*?</form>", RegexOptions.Singleline).Value;
        Assert.Contains("aria-describedby=\"tip-retry-email\"", retry); Assert.Contains("Sends the report only. The Genesys upload is never repeated.", retry);
        Assert.DoesNotContain("<span class=\"review-note\">Sends the report only", html);
        Assert.Contains("data-admin-email-error", html); Assert.Contains("smtp busy", html);
    }

    [Fact]
    public async Task RetryingTheEmail_CallsOnlyTheEmailRetry()
    {
        var api = Set(NewApi(), a => a.Sent = Dispatch(email: "Failed", emailError: "smtp busy"));
        using var factory = Factory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync($"/Collections/Campaigns?handler=RetryEmail&dispatch={DispatchId}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = await TokenAsync(client) }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, api.EmailRetries);
        Assert.Null(api.LastConfirmBody);                                                      // no new approval, no new upload
    }

    [Fact]
    public async Task OnlyAnUnconfirmedBatch_OffersReconciliation_AndTheFailedSendExplainsTheSafeRetry()
    {
        var api = Set(NewApi(), a => a.Sent = Dispatch("CompletedWithErrors", 0, 0, 2, "Sent", null, "UnknownOutcome"));
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?dispatch={DispatchId}")).Content.ReadAsStringAsync();
        Assert.Single(Regex.Matches(html, "class=\"review-reconcile\""));
        Assert.Contains("Do not send it again", html);
        Assert.Contains("data-send-retry-help", html);
    }

    [Fact]
    public async Task TheRecentSends_ListApprovalAndEmailStatus_OnTheMainPage()
    {
        var api = Set(NewApi(), a => a.Sent = Dispatch(email: "Failed"));
        using var factory = Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns")).Content.ReadAsStringAsync();
        var history = Regex.Match(html, "data-send-history.*?</table>", RegexOptions.Singleline).Value;
        Assert.Single(Regex.Matches(history, "data-history-row"));
        Assert.Contains("Nida Basem", history); Assert.Contains("Failed", history); Assert.Contains($"dispatch={DispatchId:D}", history);
    }
}
