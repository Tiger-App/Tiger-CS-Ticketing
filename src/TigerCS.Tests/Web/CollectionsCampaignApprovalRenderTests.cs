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
            if (path.EndsWith("/dispatches") && request.Method == HttpMethod.Post) { LastConfirmBody = await request.Content!.ReadAsStringAsync(cancellationToken); return Json(Dispatch("Queued", 0), HttpStatusCode.Accepted); }
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
        var page = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        return WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static Dictionary<string, string> Form(string token, params (string Key, string Value)[] extra)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["stage"] = "LegalNotice", ["businessDate"] = "2026-10-14", ["dateFrom"] = "2026-09-01", ["dateTo"] = "2026-10-31",
            ["towerId"] = "7", ["minTotal"] = "250", ["paymentStatus"] = "Overdue", ["search"] = "3001"
        };
        foreach (var (key, value) in extra) form[key] = value;
        return form;
    }

    // ------------------------------------------------------------------ one page, no Review step

    [Fact]
    public async Task ManualApproval_SendsTheModeAndTheSendDate_ToTheApi_AndAutoSendsNoDate()
    {
        var manualApi = NewApi();
        using (var factory = Factory(manualApi)) using (var client = factory.CreateClient())
        {
            var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
                ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1"), ("sendingMode", "Manual"))))).Content.ReadAsStringAsync();
            Assert.Contains("\"sendingMode\":\"Manual\"", manualApi.LastSummaryBody); Assert.Contains("\"sendDate\":\"2026-10-14\"", manualApi.LastSummaryBody);
            Assert.Contains("data-snapshot-mode>Manual, send date 14 Oct 2026 (today)<", html);
            Assert.Null(manualApi.LastConfirmBody);                                          // approving never sends
        }
        var autoApi = NewApi();
        using (var factory = Factory(autoApi)) using (var client = factory.CreateClient())
        {
            var html = await (await client.PostAsync("/Collections/Campaigns?handler=Approve", new FormUrlEncodedContent(Form(await TokenAsync(client),
                ("selectionMode", "SelectedUnits"), ("unitKeys", "4|3001|TP140-1"))))).Content.ReadAsStringAsync();
            Assert.Contains("\"sendingMode\":\"Auto\"", autoApi.LastSummaryBody); Assert.DoesNotContain("\"sendDate\":\"2", autoApi.LastSummaryBody);
            Assert.Contains("data-snapshot-mode>Auto (campaign schedule)<", html);
        }
    }

    [Fact]
    public async Task Campaigns_OffersApproveAndSend_OnlyForReadyCustomers_AndLinksToNoReviewPage()
    {
        using var factory = Factory(NewApi()); using var client = factory.CreateClient();
        var full = await (await client.GetAsync("/Collections/Campaigns?render=full&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("Approve &amp; Send to Genesys", full);
        Assert.Single(Regex.Matches(full, "class=\"campaign-select\""));                    // the Ready customer only; the not-eligible one has no checkbox
        Assert.Contains("data-all-matching", full);                                          // "all eligible customers matching the filters"
        Assert.Contains("data-unit-key=\"4|3001|TP140-101\"", full);
        Assert.DoesNotContain("/Collections/Review", full);
        Assert.DoesNotContain("Review &amp; approve", full);
        // Due / Overdue / Total and the unit stay on the row.
        Assert.Contains("data-unit-due", full); Assert.Contains("data-unit-overdue", full); Assert.Contains("TP140-101", full);
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
