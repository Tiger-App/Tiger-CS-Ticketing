using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Templates;

namespace TigerCS.Tests.Notifications.Templates;

/// <summary>
/// Renders the Resolved and Closed emails through the application's own template renderer and, when
/// <c>TIGER_EMAIL_PREVIEW_DIR</c> is set, writes them as HTML files for design review (docs/email-previews). Sample data only —
/// nothing is sent. Without the variable the test just checks that rendering works.
/// </summary>
public class EmailPreviewGenerator
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";
    private const string PreviewHost = "https://tickets.example.com";
    private static readonly DateTime When = new(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void RenderResolvedAndClosedPreviews()
    {
        // The same policy production uses, so the preview carries the real default footer and logo path.
        var policy = new CustomerNotificationModePolicy("false", null, null, PreviewHost);
        var links = new ReviewInvitationLinks(
            policy.BuildReviewUrl(Token)!,
            Enumerable.Range(1, 5).Select(r => policy.BuildReviewUrl(Token, r)!).ToList(),
            When.AddDays(14));

        var outputDir = Environment.GetEnvironmentVariable("TIGER_EMAIL_PREVIEW_DIR");
        foreach (var language in new[] { "en", "ar" })
        {
            var model = new CustomerEmailTicketModel(
                "TG-CS-20261022-0042", language == "ar" ? "أحمد الفارسي" : "Ahmed Al-Farsi", When,
                ResolutionStatus: "Resolved",
                ResolutionMessage: language == "ar" ? "تم استبدال الثرموستات وتأكيد عمله." : "The thermostat was replaced and the unit is cooling normally.",
                TicketSubject: language == "ar" ? "طلب صيانة" : "Maintenance Request",
                Language: language, Branding: policy.Branding, Review: links);

            foreach (var (name, content) in new[]
            {
                ("created", CustomerEmailTemplates.TicketCreated(model with { RequestTypeName = model.TicketSubject, TicketSubject = null })),
                ("assigned", CustomerEmailTemplates.TicketAssigned(model with { TicketStatus = "Open" })),
                ("in-progress", CustomerEmailTemplates.TicketInProgress(model)),
                ("waiting", CustomerEmailTemplates.TicketWaitingForCustomer(model)),
                ("resolved", CustomerEmailTemplates.TicketResolved(model)),
                ("cancelled", CustomerEmailTemplates.TicketCancelled(model with { ResolutionStatus = "Cancelled", ResolutionMessage = null })),
                ("closed", CustomerEmailTemplates.TicketClosed(model)),
                ("reopened", CustomerEmailTemplates.TicketReopened(model))
            })
            {
                if (name is "resolved" or "closed")
                {
                    Assert.Contains("&#9733;", content.HtmlBody, StringComparison.Ordinal);
                }

                if (outputDir is null)
                {
                    continue;
                }

                Directory.CreateDirectory(outputDir);
                // A preview opened from disk cannot reach the host; point the logo at the repository copy.
                var html = content.HtmlBody.Replace(
                    PreviewHost + "/img/tiger-logo.png", "../../src/TigerCS.Web/wwwroot/img/tiger-logo.png", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(outputDir, $"{name}-{language}.html"), html);
            }
        }
    }
}
