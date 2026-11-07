using TigerCS.Domain.Modules.Notifications;

namespace TigerCS.Application.Modules.Notifications.Abstractions;

/// <summary>
/// The customer-facing email boundary — what a notification handler calls
/// to get one rendered customer email delivered.
///
/// <para>
/// Sits one level above <see cref="IEmailSender"/> (the raw provider
/// adapter). This port owns the customer-notification rules that every
/// notification type shares and no provider should have to know: the
/// <c>EmailNotifications:Enabled</c> switch, recipient validation, turning a
/// provider exception into a classified result, and the outcome log line.
/// A handler therefore never has to guard those itself, and adding a new
/// notification type is a template plus a handler — not another copy of
/// the safety rules.
/// </para>
///
/// <para>
/// <b>Never throws for a delivery problem</b> and <b>never decides who the
/// recipient is</b>: the address arrives already resolved from trusted
/// ticket data (<c>CustomerContactResolver</c>), and an address that is
/// missing or malformed is reported as <see cref="CustomerEmailSendOutcome.Skipped"/>,
/// never guessed or defaulted.
/// </para>
/// </summary>
public interface ICustomerEmailSender
{
    Task<CustomerEmailSendResult> SendAsync(CustomerEmailRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One customer email, fully rendered, addressed to a recipient resolved from ticket data.</summary>
/// <param name="NotificationType">Which customer notification this is — logged and audited, never rendered.</param>
/// <param name="TicketId">Internal identifier, for logs and audit only. Never rendered into the email.</param>
/// <param name="TicketNumber">The customer-facing reference (<c>TG-CS-20260822-0001</c>).</param>
/// <param name="RecipientAddress">Resolved recipient, or <c>null</c>/blank when the ticket has no usable email.</param>
/// <param name="Content">Rendered subject, plain-text body and HTML body.</param>
/// <param name="CorrelationId">Propagated to the provider and the log line.</param>
public sealed record CustomerEmailRequest(
    NotificationType NotificationType,
    long TicketId,
    string TicketNumber,
    string? RecipientAddress,
    CustomerEmailContent Content,
    Guid CorrelationId);

/// <summary>A rendered customer email. The plain-text body is mandatory; the HTML body is the richer alternative.</summary>
public sealed record CustomerEmailContent(string Subject, string TextBody, string HtmlBody);

public enum CustomerEmailSendOutcome
{
    /// <summary>The provider accepted the message.</summary>
    Sent = 1,

    /// <summary>Deliberately not sent — disabled, no recipient, invalid recipient. Terminal and expected; not an error.</summary>
    Skipped = 2,

    /// <summary>Provider failure a later attempt may survive.</summary>
    TransientFailure = 3,

    /// <summary>Provider failure no retry can fix.</summary>
    PermanentFailure = 4
}

/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Reason">
/// Operator-facing diagnostic: a <see cref="CustomerEmailSkipReasons"/> code
/// for a skip, or a provider classification for a failure. Never contains
/// the recipient address, the body, or any credential.
/// </param>
public sealed record CustomerEmailSendResult(CustomerEmailSendOutcome Outcome, string? Reason = null)
{
    public bool IsSent => Outcome == CustomerEmailSendOutcome.Sent;

    public static CustomerEmailSendResult Sent() => new(CustomerEmailSendOutcome.Sent);

    public static CustomerEmailSendResult Skipped(string reason) => new(CustomerEmailSendOutcome.Skipped, reason);

    public static CustomerEmailSendResult Transient(string error) => new(CustomerEmailSendOutcome.TransientFailure, error);

    public static CustomerEmailSendResult Permanent(string error) => new(CustomerEmailSendOutcome.PermanentFailure, error);
}

/// <summary>
/// The fixed vocabulary of "why was this notification not sent". Stable
/// codes rather than free text, so the audit trail and log lines can be
/// searched and counted, and so no customer contact data ever ends up in a
/// reason string.
/// </summary>
public static class CustomerEmailSkipReasons
{
    public const string NotificationsDisabled = "NotificationsDisabled";

    public const string NoCustomerEmail = "NoCustomerEmail";

    public const string InvalidCustomerEmail = "InvalidCustomerEmail";

    public const string EventTooOld = "EventTooOld";

    /// <summary><c>IsTestMode</c> is on but <c>CustomerNotifications:TestEmail</c> is missing or invalid. Never falls back to the customer.</summary>
    public const string TestEmailMissing = "TestEmailMissing";

    /// <summary><c>IsTestMode</c> is on but <c>CustomerNotifications:TestPhone</c> is missing or invalid. Never falls back to the customer.</summary>
    public const string TestPhoneMissing = "TestPhoneMissing";

    /// <summary>
    /// The ticket was already Closed (in the same resolution cycle) when an earlier event of that cycle came up for delivery — e.g.
    /// Resolved immediately followed by Closed. The Closed email carries the outcome, so the intermediate email is not sent.
    /// </summary>
    public const string SupersededByClosure = "SupersededByClosure";

    /// <summary>
    /// The ticket had already moved past the state this event announces (another status, another owner, a new resolution cycle) when the
    /// event came up for delivery. The later state has its own notification, so this one is not sent.
    /// </summary>
    public const string SupersededByLaterUpdate = "SupersededByLaterUpdate";

    /// <summary>Internal notification: the staff member it is addressed to has no email address on their account.</summary>
    public const string NoAgentEmail = "NoAgentEmail";

    /// <summary>Internal notification: the staff member it is addressed to no longer has an account.</summary>
    public const string AgentNotFound = "AgentNotFound";
}
