using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>Collections Campaigns one-page flow: select customers, approve and send to Genesys, report to the administrator.</summary>
public sealed class OneClickApprovalTests
{
    private const string AdminA = "admin.one@example.test";
    private const string AdminB = "admin.two@example.test";

    private static async Task<ReviewHarness> SeededAsync(int count = 3, params string[] recipients)
    {
        var h = new ReviewHarness();
        h.Campaign.AdminNotification.Recipients = recipients.Length == 0 ? [AdminA] : recipients;
        for (var n = 1; n <= count; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n));
        await h.RefreshAsync();
        return h;
    }

    private static string KeyOf(int company, int n) => ReviewQueryService.UnitKey(company, $"T{n:D4}", $"TP140-{n}");

    private static async Task<(DispatchDto Dispatch, long Id)> ApproveAndSendAsync(ReviewHarness h, SelectionRequest selection, string key = "k1", bool runReport = true)
    {
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, selection, CancellationToken.None)).Value!;
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(selection, summary.Count, summary.Fingerprint, key, true), CancellationToken.None);
        Assert.True(accepted.IsSuccess, accepted.Detail);
        var id = h.Scheduler.Dispatches.Last();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        if (runReport) await scope.AdminReport.SendAfterDispatchAsync(id, CancellationToken.None);
        return ((await scope.Dispatch.GetAsync(h.Manager, accepted.Value!.DispatchId, CancellationToken.None)).Value!, id);
    }

    private static SelectionRequest Units(params int[] numbers) =>
        new(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeSelectedUnits, UnitKeys: numbers.Select(n => KeyOf(4, n)).ToList());

    private static string Attachment(FakeAdminEmailSender email, int index = 0) =>
        System.Text.Encoding.UTF8.GetString(email.Sent[index].Attachments!.Single().Content).TrimStart('﻿');

    // ------------------------------------------------------------ selection

    [Fact]
    public async Task TickedCustomers_AreExactlyWhatIsSummarisedAndSent_AndUntickedOnesAreNot()
    {
        using var h = await SeededAsync(5);
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, Units(1, 3), CancellationToken.None)).Value!;
        Assert.Equal(2, summary.Count);
        Assert.Equal(101m + 103m, summary.TotalsByCurrency["AED"]);
        Assert.Equal(new[] { "CurrentMonth" }, summary.CountByReminderType.Keys);
        Assert.Equal(new[] { "CurrentMonth" }, summary.ContactListByReminderType!.Keys);
        Assert.True(Guid.TryParse(summary.ContactListByReminderType["CurrentMonth"], out _));   // the destination Genesys contact list is shown before confirming

        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1, 3));
        Assert.Equal(2, dispatch.UploadedCount);
        Assert.Equal(2, h.Client.Calls.Single().Contacts.Count);
        Assert.DoesNotContain(h.Client.Calls.Single().Contacts, c => c.CustomerName == "Customer 2");
    }

    [Fact]
    public async Task SelectAllEligible_MatchingTheFilters_CoversEveryCustomer()
    {
        using var h = await SeededAsync(30);
        var all = new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeAllMatching);
        var (dispatch, _) = await ApproveAndSendAsync(h, all);
        Assert.Equal(30, dispatch.ApprovedCount);
        Assert.Equal(30, dispatch.UploadedCount);
    }

    [Fact]
    public async Task ATickedCustomerWhoIsNoLongerReady_IsLeftOutAndCounted_NeverSilentlyKept()
    {
        using var h = await SeededAsync(3);
        using var scope = h.NewScope();
        var request = new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeSelectedUnits,
            UnitKeys: [KeyOf(4, 1), KeyOf(4, 99)]);
        var summary = (await scope.Query.SummarizeAsync(h.Manager, request, CancellationToken.None)).Value!;
        Assert.Equal(1, summary.Count);
        Assert.Contains(summary.Notices, n => n.Contains("left out"));
        var none = await scope.Query.SummarizeAsync(h.Manager, request with { UnitKeys = [] }, CancellationToken.None);
        Assert.False(none.IsSuccess);
    }

    // ------------------------------------------------------------ approval, financial validation, duplicates

    [Fact]
    public async Task Approval_RecordsWhoAndWhen_AndAnUnreconciledSourceCannotBeSent()
    {
        using var h = new ReviewHarness();
        h.Campaign.FinancialSourceValidated = false;                         // financial reconciliation is never bypassed
        h.Source.Items.Add(ReviewHarness.Row(1, 150m));
        await h.RefreshAsync();
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, Units(1), CancellationToken.None)).Value!;
        Assert.False(summary.SourceReconciled);
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(Units(1), summary.Count, summary.Fingerprint, "gate", true), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("not reconciled", refused.Detail); Assert.Empty(h.Scheduler.Dispatches);

        using var ok = await SeededAsync(1);
        var (dispatch, _) = await ApproveAndSendAsync(ok, Units(1));
        Assert.Equal(ok.Manager.EmployeeId, dispatch.InitiatedByEmployeeId);
        Assert.Equal(ReviewHarness.Now, dispatch.InitiatedAtUtc);
    }

    [Fact]
    public async Task ABalanceThatChangedAfterApproval_StopsTheWholeSend_NothingIsUploaded_AndNoAdminReportIsSent()
    {
        using var h = await SeededAsync(2);
        using var scope = h.NewScope();
        var selection = Units(1, 2);
        var summary = (await scope.Query.SummarizeAsync(h.Manager, selection, CancellationToken.None)).Value!;
        await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(selection, summary.Count, summary.Fingerprint, "k1", true), CancellationToken.None);
        h.Source.Items[0] = ReviewHarness.Row(1, 999m);                       // the balance moved
        var id = h.Scheduler.Dispatches.Last();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        await scope.AdminReport.SendAfterDispatchAsync(id, CancellationToken.None);
        Assert.Empty(h.Client.Calls);
        Assert.Empty(h.Email.Sent);
        using var context = h.CreateContext();
        var row = await context.CollectionsDispatches.SingleAsync();
        Assert.Equal(DispatchStatus.ReviewRequired, row.Status);
        Assert.Equal(AdminEmailStatus.None, row.AdminEmailStatus);
    }

    [Fact]
    public async Task SendingTheSameCustomersAgain_IsBlockedByIdempotencyAndByDispatchHistory()
    {
        using var h = await SeededAsync(2);
        await ApproveAndSendAsync(h, Units(1, 2), "k1");
        using var scope = h.NewScope();
        // Same key + same list: replayed, not sent twice.
        var summary = (await scope.Query.SummarizeAsync(h.Manager, Units(1, 2), CancellationToken.None)).Value;
        Assert.Null(summary?.Count > 0 ? summary : null);                     // already uploaded: no longer Ready, so nothing to approve
        var replay = await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(Units(1, 2), 2, "x", "k1", true), CancellationToken.None);
        Assert.False(replay.IsSuccess);                                      // key already used for a different approval, never a second upload
        Assert.Single(h.Client.Calls);
    }

    // ------------------------------------------------------------ Genesys results

    [Fact]
    public async Task RejectedBatch_IsReportedAsFailure_NeverAsSent_AndTheCustomersCanBeApprovedAgain()
    {
        using var h = await SeededAsync(2);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Rejected);
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1, 2));
        Assert.Equal("CompletedWithErrors", dispatch.Status);
        Assert.Equal(0, dispatch.UploadedCount);
        Assert.Equal(2, dispatch.FailedCount);
        var report = h.Email.Sent.Single();
        Assert.Contains("Failure", report.Subject);
        Assert.Contains("Rejected by Genesys", Attachment(h.Email));

        // Safe retry: the rejected customers are free again and a new approval sends them once.
        var (retry, _) = await ApproveAndSendAsync(h, Units(1, 2), "k2");
        Assert.Equal(2, retry.UploadedCount);
        Assert.Equal(2, h.Client.Calls.Count);
    }

    [Fact]
    public async Task UnconfirmedBatch_IsNeverReportedAsSent_AndIsNotResent()
    {
        using var h = await SeededAsync(2);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Unknown);
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1, 2));
        Assert.Equal(0, dispatch.UploadedCount);
        Assert.Equal(2, dispatch.UnknownCount);
        Assert.Contains("Unconfirmed", Attachment(h.Email));
        Assert.Single(h.Client.Calls);
    }

    [Fact]
    public async Task BatchesNeverExceed1000Contacts()
    {
        using var h = await SeededAsync(0);
        h.Genesys.BatchSize = 5000;                                          // the cap is Genesys' own 1,000
        for (var n = 1; n <= 1001; n++) h.Source.Items.Add(ReviewHarness.Row(n, 150m));
        await h.RefreshAsync();
        var (dispatch, _) = await ApproveAndSendAsync(h, new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeAllMatching));
        Assert.Equal(2, dispatch.Batches.Count);
        Assert.All(h.Client.Calls, c => Assert.True(c.Contacts.Count <= 1000));
    }

    // ------------------------------------------------------------ administrator report

    [Fact]
    public async Task AdminReport_CarriesCampaignApproverTimeCountsAmountsBatchAndResults_WithMaskedContacts()
    {
        using var h = await SeededAsync(2);
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1, 2));
        var message = h.Email.Sent.Single();
        Assert.Equal(AdminA, message.ToAddress);
        Assert.Contains("Success", message.Subject);
        Assert.Contains(CampaignReminderTypes.StageName(CampaignReminderType.CurrentMonth), message.Body);
        Assert.Contains($"Approved by: {h.Manager.EmployeeId:D}", message.Body);
        Assert.Contains("Approved at: 2026-10-14 08:00 UTC", message.Body);
        Assert.Contains("Approved customers: 2", message.Body);
        Assert.Contains("AED 203.00", message.Body);
        Assert.Contains("Uploaded to Genesys: 2", message.Body);
        Assert.Contains("batch id", message.Body);
        Assert.DoesNotContain("+9715", message.Body);                        // the body carries totals only, no contact details
        Assert.DoesNotContain("@example.test", message.Body.Replace(AdminA, ""));

        var csv = Attachment(h.Email);
        Assert.Contains("Uploaded to Genesys", csv);
        Assert.Contains("\"Customer 1\"", csv);
        Assert.DoesNotContain("c1@example.test", csv);                       // email and phone are masked in the attachment
        Assert.DoesNotContain("971500000001", csv);
        Assert.Contains("c***@example.test", csv);
        Assert.Contains(".csv", message.Attachments!.Single().FileName);

        using var context = h.CreateContext();
        Assert.Equal(AdminEmailStatus.Sent, (await context.CollectionsDispatches.SingleAsync()).AdminEmailStatus);
        Assert.Equal("Sent", dispatch.AdminEmailStatus);
    }

    [Fact]
    public async Task MultipleRecipients_EachGetTheReport_AndSeparatorsInOneEntryAreSplit()
    {
        using var h = await SeededAsync(1, $"{AdminA}; {AdminB}", AdminA.ToUpperInvariant());
        await ApproveAndSendAsync(h, Units(1));
        Assert.Equal(new[] { AdminA, AdminB }, h.Email.Sent.Select(m => m.ToAddress).Order());   // de-duplicated, case-insensitive
    }

    [Fact]
    public async Task ChangingTheRecipients_ChangesWhoIsEmailed_WithoutAnyCodeChange()
    {
        using var first = await SeededAsync(1, AdminA);
        await ApproveAndSendAsync(first, Units(1));
        Assert.Equal(AdminA, first.Email.Sent.Single().ToAddress);
        using var second = await SeededAsync(1, AdminB);
        await ApproveAndSendAsync(second, Units(1));
        Assert.Equal(AdminB, second.Email.Sent.Single().ToAddress);
    }

    [Fact]
    public void Recipients_AreReadFromConfiguration_IncludingEnvironmentStyleKeysAndLaterSourcesOverridingEarlierOnes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collections:Campaigns:AdminNotification:Recipients:0"] = "base@example.test",
                ["Collections:Campaigns:AdminNotification:AttachmentFormat"] = "Csv"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>   // e.g. appsettings.UAT.json or Collections__Campaigns__AdminNotification__Recipients__0
            {
                ["Collections:Campaigns:AdminNotification:Recipients:0"] = "uat.one@example.test",
                ["Collections:Campaigns:AdminNotification:Recipients:1"] = "uat.two@example.test"
            }).Build();
        var options = configuration.GetSection(CollectionsCampaignOptions.SectionName).Get<CollectionsCampaignOptions>()!;
        Assert.Equal(["uat.one@example.test", "uat.two@example.test"], options.AdminNotification.ResolveRecipients().Valid);
        Assert.True(options.AdminNotification.Enabled);
        Assert.True(options.AdminNotification.AttachCustomerList);
    }

    [Fact]
    public void ShippedConfiguration_HardcodesNoAdministratorInTheBaseFile_AndUatCarriesItsOwn()
    {
        var api = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TigerCS.Api");
        var baseFile = File.ReadAllText(Path.Combine(api, "appsettings.json"));
        var uat = File.ReadAllText(Path.Combine(api, "appsettings.UAT.json"));
        var baseOptions = new ConfigurationBuilder().AddJsonFile(Path.Combine(api, "appsettings.json")).Build()
            .GetSection(CollectionsCampaignOptions.SectionName).Get<CollectionsCampaignOptions>()!;
        Assert.Empty(baseOptions.AdminNotification.ResolveRecipients().Valid);
        Assert.Contains("AdminNotification", baseFile);
        Assert.Contains("\"Recipients\"", uat);
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(api, "..", "TigerCS.Application", "Modules", "Collections", "Review", "DispatchAdminReportService.cs")), @"[\w.]+@tigergroup"));
    }

    [Fact]
    public async Task EnabledWithNoValidRecipient_IsNotConfigured_NeverSilentlySuccessful_AndGenesysIsUnaffected()
    {
        foreach (var recipients in new[] { new string[] { }, ["not-an-email", " "] })
        {
            using var h = await SeededAsync(1);
            h.Campaign.AdminNotification.Recipients = recipients;
            var (dispatch, _) = await ApproveAndSendAsync(h, Units(1));
            Assert.Equal("NotConfigured", dispatch.AdminEmailStatus);
            Assert.Contains("Recipients", dispatch.AdminEmailError);
            Assert.Empty(h.Email.Sent);
            Assert.Equal(1, dispatch.UploadedCount);                           // Genesys result stands
        }
    }

    [Fact]
    public async Task DisabledNotification_IsRecordedAsDisabled_NotAsSent()
    {
        using var h = await SeededAsync(1);
        h.Campaign.AdminNotification.Enabled = false;
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1));
        Assert.Equal("Disabled", dispatch.AdminEmailStatus);
        Assert.Empty(h.Email.Sent);
    }

    [Fact]
    public async Task UnsupportedAttachmentFormat_IsAConfigurationProblem_NotIgnored()
    {
        using var h = await SeededAsync(1);
        h.Campaign.AdminNotification.AttachmentFormat = "Xlsx";
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1));
        Assert.Equal("NotConfigured", dispatch.AdminEmailStatus);
        Assert.Contains("not supported", dispatch.AdminEmailError);
        Assert.Empty(h.Email.Sent);
    }

    [Fact]
    public async Task AttachmentCanBeSwitchedOff_AndTheBodyStillCarriesTheResult()
    {
        using var h = await SeededAsync(1);
        h.Campaign.AdminNotification.AttachCustomerList = false;
        await ApproveAndSendAsync(h, Units(1));
        Assert.Null(h.Email.Sent.Single().Attachments);
        Assert.Contains("Uploaded to Genesys: 1", h.Email.Sent.Single().Body);
    }

    [Fact]
    public async Task EmailFailure_IsRecordedSeparately_NeverResendsGenesys_AndRetryOnlyResendsTheEmail()
    {
        using var h = await SeededAsync(2, AdminA, AdminB);
        h.Email.Respond = to => to == AdminB ? EmailSendResult.Transient("smtp busy") : EmailSendResult.Sent();
        var (dispatch, id) = await ApproveAndSendAsync(h, Units(1, 2));
        Assert.Equal("Completed", dispatch.Status);                           // Genesys outcome untouched
        Assert.Equal(2, dispatch.UploadedCount);
        Assert.Equal("Failed", dispatch.AdminEmailStatus);
        Assert.Contains("1 of 2", dispatch.AdminEmailError);
        Assert.DoesNotContain(AdminB, dispatch.AdminEmailError);              // no address in the stored diagnostic
        Assert.Single(h.Client.Calls);

        h.Email.Respond = null;
        using var scope = h.NewScope();
        var retried = await scope.AdminReport.RetryAsync(h.Manager, dispatch.DispatchId, CancellationToken.None);
        Assert.True(retried.IsSuccess, retried.Detail);
        Assert.Equal("Sent", retried.Value!.AdminEmailStatus);
        Assert.Single(h.Client.Calls);                                         // Genesys was NOT sent again to retry the email
        Assert.Equal(2, h.Email.Sent.Count(m => m.ToAddress == AdminA));       // the retry resent the report (to everyone) and nothing else
        Assert.Equal(1, h.Email.Sent.Count(m => m.ToAddress == AdminB));

        var again = await scope.AdminReport.RetryAsync(h.Manager, dispatch.DispatchId, CancellationToken.None);
        Assert.False(again.IsSuccess);                                        // already sent
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);         // a duplicate job run uploads nothing either
        Assert.Single(h.Client.Calls);
    }

    [Fact]
    public async Task AProviderThatThrows_IsAFailedReport_NotACrashedSend()
    {
        using var h = await SeededAsync(1);
        h.Email.Throw = new InvalidOperationException("smtp down");
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1));
        Assert.Equal("Failed", dispatch.AdminEmailStatus);
        Assert.Equal(1, dispatch.UploadedCount);
    }

    [Fact]
    public async Task ARecordingProvider_IsNeverReportedAsDelivered()
    {
        using var h = await SeededAsync(1);
        h.Email.SendsRealMail = false;
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1));
        Assert.Equal("Failed", dispatch.AdminEmailStatus);
        Assert.Contains("Recording", dispatch.AdminEmailError);
    }

    [Fact]
    public async Task OnlyAnAuthorisedUser_MayRetryTheEmail()
    {
        using var h = await SeededAsync(1);
        h.Email.Respond = _ => EmailSendResult.Permanent("rejected");
        var (dispatch, _) = await ApproveAndSendAsync(h, Units(1));
        using var scope = h.NewScope();
        var denied = await scope.AdminReport.RetryAsync(h.Agent, dispatch.DispatchId, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Forbidden, denied.Outcome);
    }

    [Fact]
    public void CsvCells_DefuseSpreadsheetFormulas_AndContactsAreMasked()
    {
        Assert.Equal("\"'=HYPERLINK(\"\"x\"\")\"", DispatchAdminReportService.Cell("=HYPERLINK(\"x\")"));
        Assert.Equal("\"'+1\"", DispatchAdminReportService.Cell("+1"));
        Assert.Equal("\"a,b\"", DispatchAdminReportService.Cell("a,b"));
        Assert.Equal("+97150****567", DispatchAdminReportService.MaskPhone("+971501234567"));
        Assert.Equal("j***@x.test", DispatchAdminReportService.MaskEmail("jane@x.test"));
        Assert.Equal("", DispatchAdminReportService.MaskPhone(""));
    }
}
