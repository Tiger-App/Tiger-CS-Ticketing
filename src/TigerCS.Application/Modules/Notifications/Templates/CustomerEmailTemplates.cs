using System.Globalization;
using System.Net;
using System.Text;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Templates;

/// <summary>
/// The customer-facing email templates — one method per notification, all
/// rendered through one shared Tiger Properties layout so header, footer and
/// signature can never drift between notification types.
///
/// <para>
/// <b>Layout.</b> A table-based, inline-CSS design that renders in Outlook
/// (a 650px ghost table for Word-engine clients, <c>max-width</c> for the rest),
/// with the Tiger Properties branding: a white page with a thin gold
/// (<c>#C1A562</c>) outer border, the original logo over a thin light-grey divider,
/// gold rating stars, and a white footer with the team, phone, website and
/// customer-service mailbox. No gradients, shadows or coloured backgrounds. English and Arabic are both supported; Arabic renders right-to-left.
/// There is no script, form or other interactive element — the only interaction
/// is a link.
/// </para>
///
/// <para>
/// <b>Every dynamic value is HTML-encoded</b> on its way into the HTML body
/// (<see cref="WebUtility.HtmlEncode(string)"/>), and the plain-text body is
/// built from the same values without markup. Names, ticket numbers,
/// request-type names and resolution notes are all customer- or
/// agent-supplied text and are treated as untrusted. Links are rendered only
/// when they are absolute http(s) URLs.
/// </para>
///
/// <para>
/// <b>Nothing internal is rendered.</b> Templates receive only the fields
/// listed on <see cref="CustomerEmailTicketModel"/>: no database or employee
/// identifiers, no queue or workflow identifiers, no SLA state, no internal
/// notes. Adding a field here is a deliberate, reviewable act.
/// </para>
/// </summary>
public static class CustomerEmailTemplates
{
    public const string BrandName = "Tiger Properties";

    public const string SignatureTeam = "Tiger Customer Service Team";

    /// <summary>The Tiger Properties gold used for the border, the stars and the accents.</summary>
    public const string Gold = "#C1A562";

    public const string English = "en";

    public const string Arabic = "ar";

    public static CustomerEmailContent TicketCreated(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)>
        {
            (t.TicketNumber, model.TicketNumber),
            (t.ReceivedOn, FormatDate(model.OccurredAtUtc))
        };

        if (!string.IsNullOrWhiteSpace(model.RequestTypeName))
        {
            details.Insert(1, (t.RequestType, model.RequestTypeName));
        }

        if (!string.IsNullOrWhiteSpace(model.TicketSubject)
            && !string.Equals(model.TicketSubject.Trim(), model.RequestTypeName?.Trim(), StringComparison.Ordinal))
        {
            details.Insert(1, (t.Subject, model.TicketSubject.Trim()));
        }

        details.Add((t.Status, t.StatusOpen));

        return Render(
            model,
            subject: t.CreatedSubject(model.TicketNumber),
            headline: t.CreatedHeadline,
            paragraphs: [t.CreatedBody1, t.CreatedBody2],
            details: details,
            review: null);
    }

    public static CustomerEmailContent TicketResolved(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)> { (t.TicketNumber, model.TicketNumber) };
        AddSubject(details, t, model);
        details.Add((t.Status, t.TranslateStatus(model.ResolutionStatus ?? "Resolved")));
        details.Add((t.ResolvedOn, FormatDate(model.OccurredAtUtc)));

        if (!string.IsNullOrWhiteSpace(model.ResolutionMessage))
        {
            details.Add((t.Resolution, model.ResolutionMessage));
        }

        return Render(
            model,
            subject: t.ResolvedSubject(model.TicketNumber),
            headline: t.ResolvedHeadline,
            paragraphs: [t.ResolvedBody, t.ThankYou, t.ResolvedFollowUp],
            details: details,
            review: model.Review);
    }

    public static CustomerEmailContent TicketClosed(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)> { (t.TicketNumber, model.TicketNumber) };
        AddSubject(details, t, model);
        details.Add((t.Status, t.Closed));

        // The outcome the closure carries. A system closure (the AI, document delivery, inactivity) goes straight to Closed and this is
        // the only email the customer receives for it, so the outcome must be here and not only in a "resolved" email that never comes.
        if (!string.IsNullOrWhiteSpace(model.ResolutionStatus))
        {
            details.Add((t.Outcome, t.TranslateStatus(model.ResolutionStatus)));
        }

        details.Add((t.ClosedOn, FormatDate(model.OccurredAtUtc)));

        if (!string.IsNullOrWhiteSpace(model.ResolutionMessage))
        {
            details.Add((t.Resolution, model.ResolutionMessage));
        }

        // Automatic closures say why in plain words: the customer never saw an agent act, so "closed" alone would be unexplained.
        var (body, followUp) = model switch
        {
            { ClosedForCustomerInactivity: true } => (t.ClosedInactivityBody, t.ClosedInactivityFollowUp),
            { ClosedForAiOnlyTimeout: true } => (t.ClosedAiOnlyTimeoutBody, t.ClosedInactivityFollowUp),
            _ => (t.ClosedBody, t.ClosedFollowUp)
        };

        return Render(
            model,
            subject: t.ClosedSubject(model.TicketNumber),
            headline: t.ClosedHeadline,
            paragraphs: [body, followUp],
            details: details,
            review: model.Review);
    }

    public static CustomerEmailContent TicketReopened(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)> { (t.TicketNumber, model.TicketNumber) };
        AddSubject(details, t, model);
        details.Add((t.Status, t.StatusInProgress));
        details.Add((t.ReopenedOn, FormatDate(model.OccurredAtUtc)));

        return Render(
            model,
            subject: t.ReopenedSubject(model.TicketNumber),
            headline: t.ReopenedHeadline,
            paragraphs: [t.ReopenedBody1, t.ReopenedBody2],
            details: details,
            review: null);
    }

    /// <summary>"Your request has been assigned" — the ticket gained an owner. The owner's name is deliberately not rendered.</summary>
    public static CustomerEmailContent TicketAssigned(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)> { (t.TicketNumber, model.TicketNumber) };
        AddSubject(details, t, model);
        details.Add((t.Status, t.TranslateTicketStatus(model.TicketStatus ?? "Open")));
        details.Add((t.AssignedOn, FormatDate(model.OccurredAtUtc)));

        return Render(
            model,
            subject: t.AssignedSubject(model.TicketNumber),
            headline: t.AssignedHeadline,
            paragraphs: [t.AssignedBody1, t.AssignedBody2],
            details: details,
            review: null);
    }

    /// <summary>"We are working on your request" — the ticket entered In Progress (for the first time, or again after waiting on the customer).</summary>
    public static CustomerEmailContent TicketInProgress(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)> { (t.TicketNumber, model.TicketNumber) };
        AddSubject(details, t, model);
        details.Add((t.Status, t.StatusInProgress));
        details.Add((t.UpdatedOn, FormatDate(model.OccurredAtUtc)));

        return Render(
            model,
            subject: t.InProgressSubject(model.TicketNumber),
            headline: t.InProgressHeadline,
            paragraphs: model.ResumedAfterWaiting ? [t.InProgressResumedBody, t.InProgressBody2] : [t.InProgressBody1, t.InProgressBody2],
            details: details,
            review: null);
    }

    /// <summary>"We need something from you" — the ticket is waiting for the customer. The agent's reason is quoted only when configuration allows it.</summary>
    public static CustomerEmailContent TicketWaitingForCustomer(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)> { (t.TicketNumber, model.TicketNumber) };
        AddSubject(details, t, model);
        details.Add((t.Status, t.StatusWaitingForCustomer));
        details.Add((t.UpdatedOn, FormatDate(model.OccurredAtUtc)));

        if (!string.IsNullOrWhiteSpace(model.PendingReason))
        {
            details.Add((t.WhatWeNeed, model.PendingReason.Trim()));
        }

        return Render(
            model,
            subject: t.WaitingSubject(model.TicketNumber),
            headline: t.WaitingHeadline,
            paragraphs: [t.WaitingBody1, t.WaitingBody2],
            details: details,
            review: null);
    }

    /// <summary>"Your request has been cancelled" — a resolution with the Cancelled outcome, before the ticket is closed.</summary>
    public static CustomerEmailContent TicketCancelled(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var t = Text.For(model.Language);

        var details = new List<(string Label, string Value)> { (t.TicketNumber, model.TicketNumber) };
        AddSubject(details, t, model);
        details.Add((t.Status, t.TranslateStatus("Cancelled")));
        details.Add((t.CancelledOn, FormatDate(model.OccurredAtUtc)));

        if (!string.IsNullOrWhiteSpace(model.ResolutionMessage))
        {
            details.Add((t.Resolution, model.ResolutionMessage));
        }

        return Render(
            model,
            subject: t.CancelledSubject(model.TicketNumber),
            headline: t.CancelledHeadline,
            paragraphs: [t.CancelledBody, t.CancelledFollowUp],
            details: details,
            review: null);
    }

    /// <summary>
    /// Customer wording for a <see cref="ResolutionOutcome"/>. Deliberately
    /// plain: "Rejected" and "Duplicate" are agent vocabulary, not something
    /// a customer should read unexplained.
    /// </summary>
    public static string DescribeResolution(ResolutionOutcome? outcome) => outcome switch
    {
        ResolutionOutcome.Cancelled => "Cancelled",
        ResolutionOutcome.Rejected => "Reviewed – no further action required",
        ResolutionOutcome.Duplicate => "Merged with an existing request",
        _ => "Resolved"
    };

    private static void AddSubject(List<(string Label, string Value)> details, Text t, CustomerEmailTicketModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.TicketSubject))
        {
            details.Add((t.Subject, model.TicketSubject.Trim()));
        }
    }

    /// <summary>The UAE has no daylight saving: local time is always UTC+4.</summary>
    private static readonly TimeSpan UaeOffset = TimeSpan.FromHours(4);

    /// <summary>Every date in a customer email is UAE time (UTC+4), whatever the host's own zone.</summary>
    internal static string FormatDate(DateTime utc)
    {
        var instant = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return instant.Add(UaeOffset).ToString("dd MMMM yyyy, HH:mm 'GST (UTC+4)'", CultureInfo.InvariantCulture);
    }

    private static CustomerEmailContent Render(
        CustomerEmailTicketModel model,
        string subject,
        string headline,
        IReadOnlyList<string> paragraphs,
        IReadOnlyList<(string Label, string Value)> details,
        ReviewInvitationLinks? review)
    {
        var t = Text.For(model.Language);
        var validName = CustomerNameValidator.Sanitize(model.CustomerName);
        var greeting = validName is null ? t.GreetingAnonymous : t.Greeting(validName);
        var branding = model.Branding ?? CustomerEmailBranding.Default;
        var safeReview = SafeReview(review);

        if (model.IsTest)
        {
            subject = CustomerNotificationModePolicy.TestSubjectPrefix + subject;
        }

        return new CustomerEmailContent(
            subject,
            RenderText(model, t, greeting, paragraphs, details, safeReview, branding),
            RenderHtml(model, t, subject, headline, greeting, paragraphs, details, safeReview, branding));
    }

    /// <summary>A review section is rendered only when every link is an absolute http(s) URL.</summary>
    private static ReviewInvitationLinks? SafeReview(ReviewInvitationLinks? review) =>
        review is not null && review.StarUrls.Count == 5 && IsHttpUrl(review.PageUrl) && review.StarUrls.All(IsHttpUrl) ? review : null;

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string RenderText(
        CustomerEmailTicketModel model, Text t, string greeting, IReadOnlyList<string> paragraphs,
        IReadOnlyList<(string Label, string Value)> details, ReviewInvitationLinks? review, CustomerEmailBranding branding)
    {
        var lines = new List<string>();

        if (model.IsTest)
        {
            lines.Add(Text.TestBanner);
            lines.Add(string.Empty);
        }

        lines.Add(greeting);
        lines.Add(string.Empty);

        foreach (var paragraph in paragraphs)
        {
            lines.Add(paragraph);
            lines.Add(string.Empty);
        }

        foreach (var (label, value) in details)
        {
            lines.Add($"{label}: {value}");
        }

        if (review is not null)
        {
            lines.Add(string.Empty);
            lines.Add(t.ReviewHeading);
            lines.Add(t.ReviewIntro);
            for (var rating = 1; rating <= 5; rating++)
            {
                lines.Add($"{new string('*', rating)} ({rating}/5): {review.StarUrls[rating - 1]}");
            }
        }

        lines.AddRange([string.Empty, t.KindRegards, t.Signature, string.Empty, t.AutomatedNotice]);

        if (IsHttpUrl(branding.WebsiteUrl))
        {
            lines.Add($"{t.Website}: {branding.WebsiteUrl}");
        }

        if (branding.ContactEmail is not null)
        {
            lines.Add($"{t.ContactEmail}: {branding.ContactEmail}");
        }

        if (branding.ContactPhone is not null)
        {
            lines.Add($"{t.ContactPhone}: {branding.ContactPhone}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderHtml(
        CustomerEmailTicketModel model, Text t, string title, string headline, string greeting,
        IReadOnlyList<string> paragraphs, IReadOnlyList<(string Label, string Value)> details,
        ReviewInvitationLinks? review, CustomerEmailBranding branding)
    {
        var rtl = t.IsRtl;
        var dir = rtl ? "rtl" : "ltr";
        var align = rtl ? "right" : "left";
        var font = rtl ? "Tahoma,Arial,sans-serif" : "Arial,Helvetica,sans-serif";
        var sb = new StringBuilder(4096);

        sb.Append("<!DOCTYPE html><html lang=\"").Append(t.Code).Append("\" dir=\"").Append(dir)
          .Append("\" xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"><head>")
          .Append("<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta name=\"x-apple-disable-message-reformatting\">")
          .Append("<meta name=\"color-scheme\" content=\"light only\"><meta name=\"supported-color-schemes\" content=\"light only\">")
          .Append("<!--[if mso]><xml><o:OfficeDocumentSettings><o:PixelsPerInch>96</o:PixelsPerInch></o:OfficeDocumentSettings></xml><![endif]-->")
          .Append("<title>").Append(E(title)).Append("</title></head>")
          .Append("<body style=\"margin:0;padding:0;background-color:#ffffff;\" bgcolor=\"#ffffff\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" bgcolor=\"#ffffff\" style=\"background-color:#ffffff;\">")
          .Append("<tr><td align=\"center\" style=\"padding:20px 10px;\">")
          .Append("<!--[if mso]><table role=\"presentation\" width=\"650\" align=\"center\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\"><tr><td><![endif]-->")
          .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" dir=\"").Append(dir)
          .Append("\" bgcolor=\"#ffffff\" style=\"width:100%;max-width:650px;background-color:#ffffff;border:1px solid ").Append(Gold)
          .Append(";font-family:").Append(font).Append(";\">");

        if (model.IsTest)
        {
            sb.Append("<tr><td align=\"center\" bgcolor=\"#ffffff\" style=\"background-color:#ffffff;padding:10px 24px;border-bottom:1px solid #e5e5e5;font-family:Arial,Helvetica,sans-serif;font-size:11px;line-height:16px;color:#8a6d1f;\">")
              .Append(E(Text.TestBanner)).Append("</td></tr>");
        }

        // Header: logo (or the brand name when no logo URL is configured) over a gold rule.
        sb.Append("<tr><td align=\"center\" style=\"padding:26px 24px 20px 24px;border-bottom:1px solid #e5e5e5;background-color:#ffffff;\" bgcolor=\"#ffffff\">");
        if (CustomerEmailBranding.IsPublicHttpsUrl(branding.LogoUrl))
        {
            sb.Append("<img src=\"").Append(E(branding.LogoUrl!)).Append("\" alt=\"").Append(E(BrandName))
              .Append("\" width=\"110\" style=\"display:block;margin:0 auto;border:0;outline:none;text-decoration:none;width:110px;height:auto;\">");
        }
        else
        {
            sb.Append("<span style=\"font-family:Georgia,'Times New Roman',serif;font-size:22px;font-weight:bold;letter-spacing:1px;color:").Append(Gold).Append(";\">")
              .Append(E(BrandName)).Append("</span>");
        }

        sb.Append("</td></tr>");

        // Body.
        sb.Append("<tr><td align=\"").Append(align).Append("\" style=\"padding:26px 36px 6px 36px;font-family:").Append(font)
          .Append(";font-size:14px;line-height:22px;color:#1f2933;text-align:").Append(align).Append(";\">")
          .Append("<h1 style=\"margin:0 0 16px 0;font-family:").Append(font).Append(";font-size:18px;line-height:26px;font-weight:bold;color:#1f2933;\">")
          .Append(E(headline)).Append("</h1>")
          .Append("<p style=\"margin:0 0 14px 0;\">").Append(E(greeting)).Append("</p>");

        foreach (var paragraph in paragraphs)
        {
            sb.Append("<p style=\"margin:0 0 14px 0;\">").Append(E(paragraph)).Append("</p>");
        }

        sb.Append("</td></tr>");

        // Ticket details.
        sb.Append("<tr><td style=\"padding:4px 36px 22px 36px;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-top:1px solid #e5e5e5;font-size:13px;line-height:19px;\">");
        foreach (var (label, value) in details)
        {
            sb.Append("<tr><td width=\"36%\" valign=\"top\" align=\"").Append(align)
              .Append("\" style=\"padding:9px 10px;border-bottom:1px solid #e5e5e5;color:#6b6b6b;text-align:").Append(align).Append(";\">")
              .Append(E(label)).Append("</td><td valign=\"top\" align=\"").Append(align)
              .Append("\" style=\"padding:9px 10px;border-bottom:1px solid #e5e5e5;color:#1f2933;font-weight:bold;text-align:").Append(align).Append(";\">")
              .Append(E(value)).Append("</td></tr>");
        }

        sb.Append("</table></td></tr>");

        if (review is not null)
        {
            AppendReview(sb, t, review);
        }

        // Footer: white, set off by a thin light-grey divider; the team, phone, website and customer-service mailbox.
        sb.Append("<tr><td align=\"center\" bgcolor=\"#ffffff\" style=\"background-color:#ffffff;border-top:1px solid #e5e5e5;")
          .Append("padding:20px 24px 22px 24px;font-family:").Append(font).Append(";font-size:12px;line-height:19px;color:#6b6b6b;\">")
          .Append("<div style=\"margin:0;\">").Append(E(t.KindRegards)).Append("</div>")
          .Append("<div style=\"margin:0 0 4px 0;font-size:13px;font-weight:bold;color:#1f2933;\">").Append(E(t.Signature)).Append("</div>");

        var contact = new List<string>();
        if (branding.ContactPhone is not null)
        {
            contact.Add($"<span dir=\"ltr\">{E(branding.ContactPhone)}</span>");
        }

        if (IsHttpUrl(branding.WebsiteUrl))
        {
            contact.Add($"<a href=\"{E(branding.WebsiteUrl)}\" style=\"color:#8a6d1f;text-decoration:none;\">{E(DisplayHost(branding.WebsiteUrl))}</a>");
        }

        if (branding.ContactEmail is not null)
        {
            contact.Add($"<a href=\"mailto:{E(branding.ContactEmail)}\" style=\"color:#8a6d1f;text-decoration:none;\">{E(branding.ContactEmail)}</a>");
        }

        if (contact.Count > 0)
        {
            sb.Append("<div style=\"margin:0 0 10px 0;\">").Append(string.Join(" &nbsp;|&nbsp; ", contact)).Append("</div>");
        }

        sb.Append("<div style=\"font-size:11px;line-height:17px;color:#8c8c8c;\">").Append(E(t.AutomatedNotice)).Append("</div>")
          .Append("</td></tr></table>")
          .Append("<!--[if mso]></td></tr></table><![endif]-->")
          .Append("</td></tr></table></body></html>");

        return sb.ToString();
    }

    private static void AppendReview(StringBuilder sb, Text t, ReviewInvitationLinks review)
    {
        sb.Append("<tr><td align=\"center\" bgcolor=\"#ffffff\" style=\"background-color:#ffffff;padding:0 36px 24px 36px;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" bgcolor=\"#ffffff\" style=\"background-color:#ffffff;border-top:1px solid #e5e5e5;\">")
          .Append("<tr><td align=\"center\" style=\"padding:22px 0 4px 0;\">")
          .Append("<div style=\"font-size:15px;line-height:22px;font-weight:bold;color:#1f2933;\">").Append(E(t.ReviewHeading)).Append("</div>")
          .Append("<div style=\"padding-top:3px;font-size:12px;line-height:18px;color:#6b6b6b;\">").Append(E(t.ReviewIntro)).Append("</div>")
          .Append("</td></tr><tr><td align=\"center\" dir=\"ltr\" style=\"padding:8px 0 6px 0;font-size:0;line-height:0;\">");

        for (var rating = 1; rating <= 5; rating++)
        {
            var label = t.StarLabel(rating);
            sb.Append("<a href=\"").Append(E(review.StarUrls[rating - 1])).Append("\" title=\"").Append(E(label)).Append("\" aria-label=\"").Append(E(label))
              .Append("\" style=\"display:inline-block;padding:0 4px;font-family:'Segoe UI Symbol',Arial,sans-serif;font-size:32px;line-height:40px;color:").Append(Gold)
              .Append(";text-decoration:none;\">&#9733;</a>");
        }

        sb.Append("</td></tr><tr><td align=\"center\" style=\"padding:0 0 4px 0;font-size:12px;line-height:18px;\">")
          .Append("<a href=\"").Append(E(review.PageUrl)).Append("\" style=\"color:#8a6d1f;text-decoration:underline;\">").Append(E(t.ReviewLink)).Append("</a>")
          .Append("</td></tr></table></td></tr>");
    }

    private static string DisplayHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static string E(string value) => WebUtility.HtmlEncode(value);

    /// <summary>The wording of one language. Every customer-visible string lives here so English and Arabic cannot drift apart.</summary>
    private sealed class Text
    {
        public const string TestBanner = "TEST NOTIFICATION — this message was redirected to the UAT test mailbox. The customer was not contacted.";

        private readonly bool _ar;

        private Text(bool ar) => _ar = ar;

        public static Text For(string? language) =>
            string.Equals(language, Arabic, StringComparison.OrdinalIgnoreCase) ? new Text(true) : new Text(false);

        public bool IsRtl => _ar;
        public string Code => _ar ? Arabic : English;

        private string P(string en, string ar) => _ar ? ar : en;

        public string TicketNumber => P("Ticket number", "رقم التذكرة");
        public string Subject => P("Subject", "الموضوع");
        public string Status => P("Status", "الحالة");
        public string RequestType => P("Request type", "نوع الطلب");
        public string Resolution => P("Resolution", "ملخص الحل");
        public string ReceivedOn => P("Received on", "تاريخ الاستلام");
        public string ResolvedOn => P("Resolved on", "تاريخ الحل");
        public string ClosedOn => P("Closed on", "تاريخ الإغلاق");
        public string ReopenedOn => P("Reopened on", "تاريخ إعادة الفتح");
        public string Closed => P("Closed", "مغلقة");
        public string Outcome => P("Outcome", "النتيجة");
        public string AssignedOn => P("Assigned on", "تاريخ الإسناد");
        public string UpdatedOn => P("Updated on", "تاريخ التحديث");
        public string CancelledOn => P("Cancelled on", "تاريخ الإلغاء");
        public string WhatWeNeed => P("What we need from you", "ما نحتاجه منك");
        public string StatusOpen => P("Open", "مفتوحة");
        public string StatusInProgress => P("In progress", "قيد المعالجة");
        public string StatusWaitingForCustomer => P("Waiting for your reply", "بانتظار ردك");

        /// <summary>Customer wording for a ticket status name (<c>Open</c>, <c>InProgress</c>, <c>PendingCustomer</c>, …); unknown text is left as is.</summary>
        public string TranslateTicketStatus(string status) => status switch
        {
            "Open" => StatusOpen,
            "InProgress" => StatusInProgress,
            "PendingCustomer" => StatusWaitingForCustomer,
            "Resolved" => TranslateStatus("Resolved"),
            "Closed" => Closed,
            _ => status
        };

        public string AssignedSubject(string n) => P($"Your request has been assigned – Ticket {n}", $"تم إسناد طلبك – تذكرة {n}");
        public string AssignedHeadline => P("Your request has been assigned", "تم إسناد طلبك");
        public string AssignedBody1 => P(
            "Your request has been assigned to a member of our team, who will look into it and keep you informed.",
            "تم إسناد طلبك إلى أحد أعضاء فريقنا، وسيقوم بمراجعته وإبقائك على اطلاع.");
        public string AssignedBody2 => P(
            "Please quote the ticket number in any further correspondence.",
            "يرجى ذكر رقم التذكرة في أي مراسلات لاحقة.");

        public string InProgressSubject(string n) => P($"We are working on your request – Ticket {n}", $"نعمل على طلبك – تذكرة {n}");
        public string InProgressHeadline => P("We are working on your request", "نعمل على طلبك");
        public string InProgressBody1 => P(
            "Work on your request has started and our team is handling it now.",
            "بدأ العمل على طلبك ويقوم فريقنا بمعالجته الآن.");
        public string InProgressResumedBody => P(
            "Thank you for your reply. Our team has resumed work on your request.",
            "شكراً لردك. استأنف فريقنا العمل على طلبك.");
        public string InProgressBody2 => P(
            "We will let you know as soon as there is an update or as soon as it has been resolved.",
            "سنخبرك فور وجود أي مستجدات أو فور حل الطلب.");

        public string WaitingSubject(string n) => P($"We need your input – Ticket {n}", $"نحتاج إلى ردك – تذكرة {n}");
        public string WaitingHeadline => P("Your request is waiting for your reply", "طلبك بانتظار ردك");
        public string WaitingBody1 => P(
            "To continue with your request, our team needs some information or an action from you.",
            "لمتابعة طلبك، يحتاج فريقنا إلى بعض المعلومات أو إجراء من جانبك.");
        public string WaitingBody2 => P(
            "Please contact us and quote your ticket number, and we will carry on as soon as we hear from you.",
            "يرجى التواصل معنا مع ذكر رقم التذكرة، وسنتابع طلبك فور تلقي ردك.");

        public string CancelledSubject(string n) => P($"Your request has been cancelled – Ticket {n}", $"تم إلغاء طلبك – تذكرة {n}");
        public string CancelledHeadline => P("Your request has been cancelled", "تم إلغاء طلبك");
        public string CancelledBody => P(
            "Your request has been cancelled and no further action will be taken on it.",
            "تم إلغاء طلبك ولن يتم اتخاذ أي إجراء إضافي بشأنه.");
        public string CancelledFollowUp => P(
            "If this was not expected, or you still need our help, please contact us and quote your ticket number.",
            "إذا لم يكن ذلك متوقعاً، أو كنت لا تزال بحاجة إلى مساعدتنا، يرجى التواصل معنا وذكر رقم التذكرة.");

        public string ClosedInactivityBody => P(
            "We did not receive a reply from you, so your request has now been closed.",
            "لم نتلقَّ رداً منك، لذلك تم إغلاق طلبك الآن.");
        public string ClosedAiOnlyTimeoutBody => P(
            "Your request was handled by our virtual assistant. As there was no further activity on it, it has now been closed automatically.",
            "تمت معالجة طلبك بواسطة المساعد الافتراضي. ونظراً لعدم وجود أي نشاط إضافي عليه، تم إغلاقه تلقائياً الآن.");
        public string ClosedInactivityFollowUp => P(
            "If you still need help with this matter, please contact us and quote your ticket number, and we will be glad to assist you.",
            "إذا كنت لا تزال بحاجة إلى مساعدة بخصوص هذا الموضوع، يرجى التواصل معنا وذكر رقم التذكرة وسيسعدنا مساعدتك.");

        public string GreetingAnonymous => P("Dear Customer,", "عزيزنا العميل،");
        public string Greeting(string name) => P($"Dear {name},", $"عزيزي/عزيزتي {name}،");

        public string KindRegards => P("Kind regards,", "مع أطيب التحيات،");
        public string Signature => P(SignatureTeam, "فريق خدمة عملاء Tiger");
        public string AutomatedNotice => P(
            $"This is an automated message from {BrandName}. Please do not reply to this email.",
            $"هذه رسالة آلية من {BrandName}. يرجى عدم الرد على هذا البريد الإلكتروني.");
        public string Website => P("Website", "الموقع الإلكتروني");
        public string ContactEmail => P("Email", "البريد الإلكتروني");
        public string ContactPhone => P("Phone", "الهاتف");

        public string CreatedSubject(string n) => P($"Your request has been received – Ticket {n}", $"تم استلام طلبك – تذكرة {n}");
        public string CreatedHeadline => P("We have received your request", "لقد استلمنا طلبك");
        public string CreatedBody1 => P(
            "Thank you for contacting Tiger Properties. Your request has been received and logged with the reference below.",
            "شكراً لتواصلك مع Tiger Properties. لقد تم استلام طلبك وتسجيله بالرقم المرجعي أدناه.");
        public string CreatedBody2 => P(
            "Our team is reviewing it and will be in touch with you as soon as possible. Please quote the ticket number in any further correspondence.",
            "يقوم فريقنا بمراجعته وسنتواصل معك في أقرب وقت ممكن. يرجى ذكر رقم التذكرة في أي مراسلات لاحقة.");

        public string ResolvedSubject(string n) => P($"Your Ticket #{n} Has Been Resolved", $"تم حل تذكرتك رقم #{n}");
        public string ResolvedHeadline => P("Your ticket has been resolved", "تم حل تذكرتك");
        public string ResolvedBody => P(
            "We are pleased to let you know that your request has been resolved.",
            "يسعدنا إبلاغك بأنه تم حل طلبك.");
        public string ThankYou => P(
            "Thank you for your patience and for choosing Tiger Properties.",
            "نشكرك على صبرك وعلى اختيارك Tiger Properties.");
        public string ResolvedFollowUp => P(
            "If you feel the matter has not been fully addressed, please contact us and quote your ticket number so we can look into it again.",
            "إذا شعرت أن الموضوع لم يُعالَج بالكامل، يرجى التواصل معنا وذكر رقم التذكرة لنعيد النظر فيه.");

        public string ClosedSubject(string n) => P($"Your Ticket #{n} Has Been Closed", $"تم إغلاق تذكرتك رقم #{n}");
        public string ClosedHeadline => P("Your ticket has been closed", "تم إغلاق تذكرتك");
        public string ClosedBody => P(
            "Your request has now been closed. Thank you for giving us the opportunity to assist you.",
            "تم الآن إغلاق طلبك. نشكرك على إتاحة الفرصة لنا لخدمتك.");
        public string ClosedFollowUp => P(
            "If you need further help with this or any other matter, please contact us and we will be glad to open a new request for you.",
            "إذا احتجت إلى مساعدة إضافية بخصوص هذا الموضوع أو أي موضوع آخر، يرجى التواصل معنا وسيسعدنا فتح طلب جديد لك.");

        public string ReopenedSubject(string n) => P($"Your request has been reopened – Ticket {n}", $"تمت إعادة فتح طلبك – تذكرة {n}");
        public string ReopenedHeadline => P("Your request has been reopened", "تمت إعادة فتح طلبك");
        public string ReopenedBody1 => P(
            "Your request has been reopened and our team is working on it again.",
            "تمت إعادة فتح طلبك ويعمل فريقنا عليه من جديد.");
        public string ReopenedBody2 => P(
            "We will keep you informed and let you know as soon as it has been resolved.",
            "سنبقيك على اطلاع وسنخبرك فور حله.");

        public string ReviewHeading => P("How was your experience?", "كيف كانت تجربتك؟");
        public string ReviewIntro => P(
            "Tap a star to rate our service. You can add a comment before you submit.",
            "اضغط على نجمة لتقييم خدمتنا. يمكنك إضافة تعليق قبل الإرسال.");
        public string ReviewLink => P("Rate your experience", "قيّم تجربتك");
        public string StarLabel(int rating) => _ar
            ? $"{rating} من 5 نجوم"
            : rating == 1 ? "1 star" : $"{rating} stars";

        /// <summary>The customer wording <see cref="DescribeResolution"/> produces, in Arabic when needed; unknown text is left as is.</summary>
        public string TranslateStatus(string english) => !_ar ? english : english switch
        {
            "Resolved" => "تم الحل",
            "Cancelled" => "ملغاة",
            "Reviewed – no further action required" => "تمت المراجعة – لا يلزم أي إجراء إضافي",
            "Merged with an existing request" => "تم دمجها مع طلب قائم",
            _ => english
        };
    }
}

/// <summary>
/// Everything a customer email may render — and, by construction, nothing
/// else. Built by the notification handler from the ticket and its
/// resolved contact; never from request input.
/// </summary>
/// <param name="TicketNumber">The customer-facing ticket reference.</param>
/// <param name="CustomerName">Greeting name, when known.</param>
/// <param name="OccurredAtUtc">When the event happened (creation, resolution, closure, reopening).</param>
/// <param name="RequestTypeName">Request type or category name, when the ticket is classified.</param>
/// <param name="ResolutionStatus">Customer wording for the resolution outcome (resolved email only).</param>
/// <param name="ResolutionMessage">The resolution note, only when configuration allows sharing it.</param>
/// <param name="TicketSubject">Customer-friendly subject of the ticket (resolved and closed emails).</param>
/// <param name="Language"><c>en</c> (default) or <c>ar</c>; Arabic renders right-to-left.</param>
/// <param name="Branding">Logo, website and contact details; <see cref="CustomerEmailBranding.Default"/> when omitted.</param>
/// <param name="Review">The review links to offer, or <c>null</c> when no review should be requested.</param>
/// <param name="IsTest">A Test-mode notification: marked in the subject and with a banner.</param>
/// <param name="TicketStatus">The ticket's status name (<c>Open</c>, <c>InProgress</c>, …) for the "assigned" email's status line.</param>
/// <param name="PendingReason">The agent's "waiting for customer" reason, only when configuration allows sharing agent-written notes.</param>
/// <param name="ClosedForCustomerInactivity">The closure happened because the customer stopped replying; the "closed" email says so.</param>
/// <param name="ResumedAfterWaiting">The "in progress" email follows a waiting-for-customer period (the customer replied).</param>
/// <param name="ClosedForAiOnlyTimeout">The AI-only time-limit rule closed the ticket; the "closed" email explains the automatic closure.</param>
public sealed record CustomerEmailTicketModel(
    string TicketNumber,
    string? CustomerName,
    DateTime OccurredAtUtc,
    string? RequestTypeName = null,
    string? ResolutionStatus = null,
    string? ResolutionMessage = null,
    string? TicketSubject = null,
    string Language = "en",
    CustomerEmailBranding? Branding = null,
    ReviewInvitationLinks? Review = null,
    bool IsTest = false,
    string? TicketStatus = null,
    string? PendingReason = null,
    bool ClosedForCustomerInactivity = false,
    bool ResumedAfterWaiting = false,
    bool ClosedForAiOnlyTimeout = false);
