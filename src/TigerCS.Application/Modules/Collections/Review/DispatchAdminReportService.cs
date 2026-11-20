using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Emails the Collections administrator(s) the approved customer list and the ACTUAL Genesys results of a finished send.
/// <list type="bullet">
/// <item>Recipients come only from <c>Collections:Campaigns:AdminNotification</c>; none is hardcoded. Enabled with no valid recipient is recorded as
/// <see cref="AdminEmailStatus.NotConfigured"/> - never as sent.</item>
/// <item>The outcome is stored in the AdminEmail* columns of the dispatch, apart from the Genesys status. A failed or retried report never touches Genesys:
/// this class has no Genesys client at all.</item>
/// <item>Phones and emails are masked in the attachment, the body carries only totals, and nothing sensitive is logged (recipients are masked).</item>
/// </list>
/// </summary>
public sealed class DispatchAdminReportService(
    CollectionsCampaignOptions campaignOptions, IReviewStore store, IEmailSender emailSender, CollectionsClock clock,
    CollectionsAuthorizationService authorization, ILogger<DispatchAdminReportService> logger, IEmployeeRepository? employees = null)
{
    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(15);

    public sealed record Outcome(AdminEmailStatus Status, string? Error, int RecipientCount, int FailedRecipients);

    /// <summary>Called by the dispatch job after the send ended. Never throws; a send that did not finish is left alone.</summary>
    public async Task<Outcome?> SendAfterDispatchAsync(long dispatchId, CancellationToken ct)
    {
        try { return await DeliverAsync(dispatchId, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Administrator report of dispatch {DispatchId} failed unexpectedly ({ExceptionType}).", dispatchId, ex.GetType().Name);
            try { await store.RecordAdminEmailAsync(dispatchId, AdminEmailStatus.Failed, $"Unexpected {ex.GetType().Name} while preparing the report.", 0, clock.UtcNow, CancellationToken.None); }
            catch (Exception) { /* the dispatch result is already stored; nothing more to do */ }
            return new(AdminEmailStatus.Failed, ex.GetType().Name, 0, 0);
        }
    }

    /// <summary>Retry of the report only (a person pressed Retry). The Genesys upload is never repeated.</summary>
    public async Task<CollectionsResult<DispatchDto>> RetryAsync(CollectionsCaller caller, Guid dispatchId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        if (dispatch is null) return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.NotFound);
        if (dispatch.Status is not (DispatchStatus.Completed or DispatchStatus.CompletedWithErrors))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "The administrator report is sent once the Genesys send has finished.");
        if (dispatch.AdminEmailStatus == AdminEmailStatus.Sent)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "The administrator report was already sent.");
        if (dispatch.AdminEmailStatus == AdminEmailStatus.Sending && dispatch.AdminEmailAttemptedAtUtc > clock.UtcNow - StaleClaim)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "The administrator report is being sent right now.");
        await SendAfterDispatchAsync(dispatch.CollectionsDispatchId, ct);
        var refreshed = await store.GetDispatchAsync(dispatchId, ct);
        return CollectionsResult<DispatchDto>.Ok(DispatchService.ToDto(refreshed!));
    }

    private async Task<Outcome?> DeliverAsync(long dispatchId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var settings = campaignOptions.AdminNotification;
        // Disabled by configuration: recorded, so the page says so instead of staying silent.
        if (!settings.Enabled)
        {
            if (!await store.TryClaimAdminEmailAsync(dispatchId, now, now - StaleClaim, ct)) return null;
            await store.RecordAdminEmailAsync(dispatchId, AdminEmailStatus.Disabled, "Administrator notification is switched off (Collections:Campaigns:AdminNotification:Enabled).", 0, clock.UtcNow, ct);
            return new(AdminEmailStatus.Disabled, null, 0, 0);
        }
        if (!await store.TryClaimAdminEmailAsync(dispatchId, now, now - StaleClaim, ct)) return null;

        var (recipients, invalid) = settings.ResolveRecipients();
        if (recipients.Count == 0)
        {
            var error = invalid > 0
                ? $"Administrator notification is enabled but none of the {invalid} configured recipient(s) is a valid email address (Collections:Campaigns:AdminNotification:Recipients)."
                : "Administrator notification is enabled but no recipient is configured (Collections:Campaigns:AdminNotification:Recipients).";
            logger.LogWarning("Dispatch {DispatchId}: {Error}", dispatchId, error);
            await store.RecordAdminEmailAsync(dispatchId, AdminEmailStatus.NotConfigured, error, 0, clock.UtcNow, ct);
            return new(AdminEmailStatus.NotConfigured, error, 0, 0);
        }
        if (settings.AttachCustomerList && !settings.IsSupportedFormat)
        {
            var error = $"AttachmentFormat '{settings.AttachmentFormat}' is not supported; use '{CollectionsAdminNotificationOptions.CsvFormat}'. The report was not sent.";
            await store.RecordAdminEmailAsync(dispatchId, AdminEmailStatus.NotConfigured, error, recipients.Count, clock.UtcNow, ct);
            return new(AdminEmailStatus.NotConfigured, error, recipients.Count, 0);
        }
        if (!emailSender.SendsRealMail)
        {
            // A recording adapter keeps the message in memory: telling the administrator "sent" would be false.
            const string error = "The email provider only records messages (EmailNotifications:Provider = Recording); no real email was sent.";
            await store.RecordAdminEmailAsync(dispatchId, AdminEmailStatus.Failed, error, recipients.Count, clock.UtcNow, ct);
            return new(AdminEmailStatus.Failed, error, recipients.Count, recipients.Count);
        }

        var dispatch = await store.GetDispatchByIdAsync(dispatchId, ct);
        if (dispatch is null) return null;
        var approver = await ApproverNameAsync(dispatch.InitiatedByEmployeeId, ct);
        var report = Build(dispatch, approver, settings.AttachCustomerList);

        var correlation = Guid.NewGuid();
        var failed = 0;
        string? firstError = null;
        foreach (var recipient in recipients)
        {
            EmailSendResult result;
            try { result = await emailSender.SendAsync(new EmailMessage(recipient, report.Subject, report.Text, correlation, report.Html, report.Attachments, EmailAudience.Internal), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = EmailSendResult.Transient($"Email provider threw {ex.GetType().Name}."); }
            if (result.Outcome != EmailSendOutcome.Sent)
            {
                failed++;
                firstError ??= result.Error ?? "The email provider did not accept the message.";
                logger.LogWarning("Dispatch {DispatchId}: administrator report to {MaskedRecipient} failed ({Outcome}).", dispatchId, Mask(recipient), result.Outcome);
            }
        }
        if (failed == 0)
        {
            logger.LogInformation("Dispatch {DispatchId}: administrator report sent to {Count} recipient(s).", dispatchId, recipients.Count);
            await store.RecordAdminEmailAsync(dispatchId, AdminEmailStatus.Sent, null, recipients.Count, clock.UtcNow, ct);
            return new(AdminEmailStatus.Sent, null, recipients.Count, 0);
        }
        var message = $"{failed} of {recipients.Count} recipient(s) did not receive the report: {firstError}";
        await store.RecordAdminEmailAsync(dispatchId, AdminEmailStatus.Failed, message, recipients.Count, clock.UtcNow, ct);
        return new(AdminEmailStatus.Failed, message, recipients.Count, failed);
    }

    private async Task<string> ApproverNameAsync(Guid employeeId, CancellationToken ct)
    {
        try
        {
            if (employees is not null && await employees.GetByIdAsync(employeeId, ct) is { } employee && !string.IsNullOrWhiteSpace(employee.DisplayName))
                return employee.DisplayName;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* fall back to the id */ }
        return employeeId.ToString("D");
    }

    // ---------------------------------------------------------------- content

    public sealed record Report(string Subject, string Text, string Html, IReadOnlyList<EmailAttachment>? Attachments);

    public static Report Build(CollectionsDispatch d, string approvedBy, bool attachList)
    {
        var items = d.Items.OrderBy(i => i.ReminderType).ThenBy(i => i.CollectionsGenesysBatchId).ThenBy(i => i.BatchPosition).ThenBy(i => i.RecordKey, StringComparer.Ordinal).ToList();
        var stages = d.Batches.Select(b => b.StageName ?? CampaignReminderTypes.StageName(b.ReminderType)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (stages.Count == 0) stages = items.Select(i => CampaignReminderTypes.StageName(i.ReminderType)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var campaign = string.Join(", ", stages);
        var uploaded = items.Count(i => i.Status == DispatchItemStatus.UploadedToGenesys);
        var failed = items.Count(i => i.Status == DispatchItemStatus.Failed);
        var unknown = items.Count(i => i.Status == DispatchItemStatus.UnknownOutcome);
        var excluded = items.Count(i => i.Status is DispatchItemStatus.Excluded or DispatchItemStatus.Released);
        var verdict = d.Status == DispatchStatus.Completed && failed == 0 && unknown == 0 ? "Success" : "Partial failure";
        if (uploaded == 0) verdict = failed > 0 || unknown > 0 ? "Failure" : "Nothing uploaded";
        var totals = string.Join(" · ", ToTotals(items.Where(i => i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released), i => i.Amount));
        var uploadedTotals = string.Join(" · ", ToTotals(items.Where(i => i.Status == DispatchItemStatus.UploadedToGenesys), i => i.Amount));
        var approvedAt = TigerCS.Domain.Common.UaeCalendar.ToUae(d.InitiatedAtUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UAE (Asia/Dubai)";
        var subject = $"Collections campaign {verdict}: {campaign} ({uploaded} of {d.ApprovedCount} uploaded to Genesys)";

        var lines = new List<(string Label, string Value)>
        {
            ("Campaign", campaign),
            ("Result", verdict),
            ("Approved by", approvedBy),
            ("Approved at", approvedAt),
            ("Approved customers", d.ApprovedCount.ToString(CultureInfo.InvariantCulture)),
            ("Approved amount", totals.Length == 0 ? "—" : totals),
            ("Uploaded to Genesys", $"{uploaded} ({(uploadedTotals.Length == 0 ? "—" : uploadedTotals)})"),
            ("Rejected by Genesys", failed.ToString(CultureInfo.InvariantCulture)),
            ("Unconfirmed", unknown.ToString(CultureInfo.InvariantCulture)),
            ("Excluded before sending", excluded.ToString(CultureInfo.InvariantCulture)),
            ("Send reference", d.PublicId.ToString("D")),
        };
        foreach (var b in d.Batches.OrderBy(b => b.Sequence))
            lines.Add(($"Genesys batch {b.Sequence}", $"{b.StageName ?? CampaignReminderTypes.StageName(b.ReminderType)} · list {b.ContactListId} · "
                + $"{(string.IsNullOrEmpty(b.GenesysCampaignId) ? "" : $"campaign {b.GenesysCampaignId} · ")}batch id {b.CollectionsGenesysBatchId} · {b.ContactCount} contact(s) · {b.Status}"
                + (string.IsNullOrEmpty(b.Error) ? "" : $" · {b.Error}")));

        var text = new StringBuilder();
        foreach (var (label, value) in lines) text.Append(label).Append(": ").Append(value).Append('\n');
        text.Append("\nUploaded to Genesys means the contact was added to the outbound contact list; it does not mean the customer was called or reached.\n");
        if (attachList) text.Append("The approved customer list with each customer's result is attached (phone numbers and emails are masked).\n");
        else text.Append("The customer list is not attached (AttachCustomerList is off).\n");

        var html = new StringBuilder("<table cellpadding=\"4\" style=\"border-collapse:collapse;font-family:Segoe UI,Arial,sans-serif;font-size:14px\">");
        foreach (var (label, value) in lines) html.Append("<tr><td><strong>").Append(WebUtility.HtmlEncode(label)).Append("</strong></td><td>").Append(WebUtility.HtmlEncode(value)).Append("</td></tr>");
        html.Append("</table><p style=\"font-family:Segoe UI,Arial,sans-serif;font-size:13px\">Uploaded to Genesys means the contact was added to the outbound contact list; it does not mean the customer was called or reached.</p>");

        IReadOnlyList<EmailAttachment>? attachments = null;
        if (attachList)
        {
            var batchRef = d.Batches.ToDictionary(b => b.CollectionsGenesysBatchId, b => b);
            var csv = new StringBuilder();
            csv.Append(string.Join(',', "Campaign", "Customer", "Unit", "Phone", "Email", "Amount", "Currency", "DueDate", "Result", "Detail", "GenesysBatch", "GenesysBatchId", "GenesysContactList", "GenesysCampaignId", "GenesysContactId")).Append("\r\n");
            foreach (var i in items)
            {
                batchRef.TryGetValue(i.CollectionsGenesysBatchId ?? 0, out var b);
                csv.Append(string.Join(',', new[]
                {
                    Cell(b?.StageName ?? CampaignReminderTypes.StageName(i.ReminderType)), Cell(i.CustomerName), Cell(i.UnitCode), Cell(MaskPhone(i.Phone)), Cell(MaskEmail(i.Email)),
                    Cell(i.Amount.ToString("0.00", CultureInfo.InvariantCulture)), Cell(i.Currency), Cell(i.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Cell(ResultLabel(i.Status)), Cell(i.StatusReason), Cell(b is null ? "" : b.Sequence.ToString(CultureInfo.InvariantCulture)),
                    Cell(b is null ? "" : b.CollectionsGenesysBatchId.ToString(CultureInfo.InvariantCulture)), Cell(b?.ContactListId), Cell(b?.GenesysCampaignId), Cell(i.GenesysContactId)
                })).Append("\r\n");
            }
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            attachments = [new EmailAttachment($"collections-send-{d.PublicId:N}.csv", "text/csv", bytes)];
        }
        return new(subject, text.ToString(), html.ToString(), attachments);
    }

    private static IEnumerable<string> ToTotals(IEnumerable<CollectionsDispatchItem> items, Func<CollectionsDispatchItem, decimal> amount) =>
        items.GroupBy(i => i.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Sum(amount).ToString("N2", CultureInfo.InvariantCulture)}");

    private static string ResultLabel(DispatchItemStatus status) => status switch
    {
        DispatchItemStatus.UploadedToGenesys => "Uploaded to Genesys",
        DispatchItemStatus.Failed => "Rejected by Genesys",
        DispatchItemStatus.UnknownOutcome => "Unconfirmed",
        DispatchItemStatus.Excluded => "Excluded before sending",
        DispatchItemStatus.Released => "Not sent (released)",
        _ => "Not sent"
    };

    /// <summary>A CSV cell: quoted, and a leading = + - @ (or tab / CR) is defused so a spreadsheet never runs it as a formula.</summary>
    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>"+97150***4567": the country code and the last digits only.</summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "";
        return phone.Length <= 8 ? new string('*', phone.Length) : phone[..Math.Min(6, phone.Length - 3)] + new string('*', phone.Length - Math.Min(6, phone.Length - 3) - 3) + phone[^3..];
    }

    /// <summary>"j***@example.com".</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }

    private static string Mask(string address) => MaskEmail(address);
}
