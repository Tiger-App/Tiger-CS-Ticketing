using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// The development and testing email adapter: records what <i>would</i> have
/// been delivered and contacts no external service.
///
/// <para>
/// <b>Never production-ready, and the application refuses to start with it
/// outside Development/Testing</b> (<see cref="EmailSenderSafety"/>,
/// enforced in <c>Program.cs</c>). It reports success without sending
/// anything, so running it in a real environment would silently satisfy every
/// acknowledgement, audit row and dashboard count while no customer received
/// a thing.
/// </para>
///
/// <para>
/// <b>Recorded in memory only.</b> Nothing is written to disk, to a database
/// table, or to a log line that could carry a customer's address or the body
/// text — Security-Architecture.md §11 applies to a test double exactly as it
/// applies to a real one, and a development mailbox dump is a plausible way
/// for real contact data to escape once this is pointed at restored
/// production data. The log line below records the correlation ID and the
/// subject only; the recipient address is reduced to a masked form and the
/// body is never logged at all.
/// </para>
///
/// <para>
/// Registered as a singleton so a test can assert against what was recorded
/// across the request or job that produced it. The collection is bounded —
/// see <see cref="MaxRecordedDeliveries"/> — so a long-running development
/// session cannot grow it without limit.
/// </para>
/// </summary>
public sealed class RecordingEmailSender(ILogger<RecordingEmailSender> logger) : IEmailSender
{
    /// <summary>A development convenience, not an archive. Oldest entries are dropped past this point.</summary>
    public const int MaxRecordedDeliveries = 500;

    private readonly ConcurrentQueue<RecordedEmail> _recorded = new();

    /// <summary>This adapter keeps messages in memory; nothing leaves the process.</summary>
    public bool SendsRealMail => false;

    public IReadOnlyCollection<RecordedEmail> Recorded => _recorded.ToArray();

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _recorded.Enqueue(new RecordedEmail(
            message.ToAddress, message.Subject, message.Body, message.CorrelationId,
            message.Attachments?.Select(a => a.FileName).ToList(), message.HtmlBody, message.Audience));

        while (_recorded.Count > MaxRecordedDeliveries && _recorded.TryDequeue(out _))
        {
            // Bounded on purpose — see this type's remarks.
        }

        logger.LogInformation(
            "Recording email adapter accepted a message for {MaskedRecipient} with subject {Subject}. "
            + "CorrelationId {CorrelationId}. NOTHING WAS SENT — no email provider is configured.",
            Mask(message.ToAddress), message.Subject, message.CorrelationId);

        return Task.FromResult(EmailSendResult.Sent());
    }

    /// <summary>Clears the recorded deliveries — for a test that wants a clean slate between cases.</summary>
    public void Clear()
    {
        while (_recorded.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Security-Architecture.md §11's masking discipline, applied to an
    /// address: enough to correlate a delivery in a development log, never
    /// enough to reconstruct the address.
    /// </summary>
    private static string Mask(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return "***";
        }

        var local = address[..at];
        var visible = local.Length <= 1 ? local : local[..1];
        return $"{visible}***{address[at..]}";
    }
}

/// <param name="ToAddress">The address the message would have gone to.</param>
/// <param name="Subject">Rendered subject.</param>
/// <param name="Body">Rendered body. In memory only — never logged, never persisted.</param>
/// <param name="CorrelationId">ADR-0014's correlation ID.</param>
/// <param name="AttachmentFileNames">Names of attached files (never their content), so a test can see a document copy was attached.</param>
/// <param name="Audience">Customer or staff mail, so a test can tell the two apart.</param>
public sealed record RecordedEmail(
    string ToAddress, string Subject, string Body, Guid CorrelationId, IReadOnlyList<string>? AttachmentFileNames = null,
    string? HtmlBody = null, EmailAudience Audience = EmailAudience.Customer);
