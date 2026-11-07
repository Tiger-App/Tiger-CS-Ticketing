using System.Globalization;
using System.Net;
using System.Text;
using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Application.Modules.Notifications.Templates;

/// <summary>
/// Staff-facing ticket emails ("a ticket was assigned to you", "the customer replied"). English only — the Ticketing UI is English —
/// and deliberately plainer than the customer templates: a short headline, the facts, and a link to Ticket Details. Every dynamic value
/// is HTML-encoded; the link is rendered only when it is an absolute http(s) URL. Customer contact details are never rendered.
/// </summary>
public static class InternalEmailTemplates
{
    public static CustomerEmailContent TicketAssignedToYou(InternalTicketEmailModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var how = model.ActorName is null ? "was assigned to you automatically" : $"was assigned to you by {model.ActorName}";
        var transfer = model.PreviousOwnerName is null ? string.Empty : $" It was previously handled by {model.PreviousOwnerName}.";

        return Render(
            model,
            subject: $"Ticket {model.TicketNumber} assigned to you",
            headline: "A ticket has been assigned to you",
            paragraphs:
            [
                $"Ticket {model.TicketNumber} {how}.{transfer}",
                "Please review it and continue the work in TigerCS."
            ]);
    }

    public static CustomerEmailContent CustomerReplyReceived(InternalTicketEmailModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var count = model.MessageCount <= 1 ? "a new message" : $"{model.MessageCount.ToString(CultureInfo.InvariantCulture)} new messages";

        return Render(
            model,
            subject: $"Customer replied on ticket {model.TicketNumber}",
            headline: "The customer has replied",
            paragraphs:
            [
                $"The customer sent {count} on ticket {model.TicketNumber}, which is assigned to you.",
                "Open the ticket in TigerCS to read the conversation and continue."
            ]);
    }

    private static CustomerEmailContent Render(InternalTicketEmailModel model, string subject, string headline, IReadOnlyList<string> paragraphs)
    {
        var details = new List<(string Label, string Value)> { ("Ticket number", model.TicketNumber) };
        if (!string.IsNullOrWhiteSpace(model.TicketSubject))
        {
            details.Add(("Subject", model.TicketSubject.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(model.CustomerName))
        {
            details.Add(("Customer", model.CustomerName.Trim()));
        }

        details.Add(("Status", model.TicketStatus));
        if (!string.IsNullOrWhiteSpace(model.DepartmentName))
        {
            details.Add(("Department", model.DepartmentName));
        }

        details.Add(("When", CustomerEmailTemplates.FormatDate(model.OccurredAtUtc)));

        var link = IsHttpUrl(model.TicketUrl) ? model.TicketUrl : null;

        var text = new StringBuilder();
        text.AppendLine($"Hello {model.RecipientName ?? "there"},").AppendLine();
        foreach (var paragraph in paragraphs)
        {
            text.AppendLine(paragraph).AppendLine();
        }

        foreach (var (label, value) in details)
        {
            text.AppendLine($"{label}: {value}");
        }

        if (link is not null)
        {
            text.AppendLine().AppendLine($"Open the ticket: {link}");
        }

        text.AppendLine().AppendLine("This is an automated notification from TigerCS.");

        var html = new StringBuilder(2048);
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(E(subject)).Append("</title></head><body style=\"margin:0;padding:0;background-color:#ffffff;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\"><tr><td align=\"center\" style=\"padding:20px 10px;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"max-width:650px;border:1px solid ")
            .Append(CustomerEmailTemplates.Gold).Append(";font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:22px;color:#1f2933;\">")
            .Append("<tr><td style=\"padding:24px 36px 6px 36px;\"><h1 style=\"margin:0 0 16px 0;font-size:18px;line-height:26px;\">").Append(E(headline)).Append("</h1>")
            .Append("<p style=\"margin:0 0 14px 0;\">").Append(E($"Hello {model.RecipientName ?? "there"},")).Append("</p>");
        foreach (var paragraph in paragraphs)
        {
            html.Append("<p style=\"margin:0 0 14px 0;\">").Append(E(paragraph)).Append("</p>");
        }

        html.Append("</td></tr><tr><td style=\"padding:4px 36px 18px 36px;\"><table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"border-top:1px solid #e5e5e5;font-size:13px;line-height:19px;\">");
        foreach (var (label, value) in details)
        {
            html.Append("<tr><td width=\"36%\" valign=\"top\" style=\"padding:9px 10px;border-bottom:1px solid #e5e5e5;color:#6b6b6b;\">").Append(E(label))
                .Append("</td><td valign=\"top\" style=\"padding:9px 10px;border-bottom:1px solid #e5e5e5;font-weight:bold;\">").Append(E(value)).Append("</td></tr>");
        }

        html.Append("</table></td></tr>");
        if (link is not null)
        {
            html.Append("<tr><td align=\"center\" style=\"padding:0 36px 24px 36px;\"><a href=\"").Append(E(link))
                .Append("\" style=\"display:inline-block;padding:10px 22px;border:1px solid ").Append(CustomerEmailTemplates.Gold)
                .Append(";color:#8a6d1f;text-decoration:none;font-weight:bold;\">Open the ticket</a></td></tr>");
        }

        html.Append("<tr><td align=\"center\" style=\"border-top:1px solid #e5e5e5;padding:16px 24px;font-size:11px;line-height:17px;color:#8c8c8c;\">")
            .Append("This is an automated notification from TigerCS.</td></tr></table></td></tr></table></body></html>");

        return new CustomerEmailContent(subject, text.ToString().TrimEnd(), html.ToString());
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string E(string value) => WebUtility.HtmlEncode(value);
}

/// <summary>Everything a staff-facing ticket email may render. Built by the internal notification handlers from persisted data only.</summary>
/// <param name="TicketNumber">The customer-facing ticket reference.</param>
/// <param name="RecipientName">The staff member's display name.</param>
/// <param name="TicketStatus">The ticket's current status name.</param>
/// <param name="OccurredAtUtc">When the event happened.</param>
/// <param name="TicketSubject">Customer-friendly subject of the ticket (request type, category or the summary's first line).</param>
/// <param name="CustomerName">The customer's name when known. Never their contact details.</param>
/// <param name="DepartmentName">The ticket's current department.</param>
/// <param name="TicketUrl">Absolute Ticket Details URL (<c>{PublicBaseUrl}/Tickets/{id}</c>), or null when no public base URL is configured.</param>
/// <param name="ActorName">Who assigned the ticket; null for an automatic assignment.</param>
/// <param name="PreviousOwnerName">Who handled it before a transfer, when anybody did.</param>
/// <param name="MessageCount">Customer messages received (reply notification).</param>
public sealed record InternalTicketEmailModel(
    string TicketNumber,
    string? RecipientName,
    string TicketStatus,
    DateTime OccurredAtUtc,
    string? TicketSubject = null,
    string? CustomerName = null,
    string? DepartmentName = null,
    string? TicketUrl = null,
    string? ActorName = null,
    string? PreviousOwnerName = null,
    int MessageCount = 0);
