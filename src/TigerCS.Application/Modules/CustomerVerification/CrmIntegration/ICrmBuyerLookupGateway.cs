using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

/// <summary>
/// Tiger CRM's real, manually-verified <c>GET /TicketingSystem/GetBuyerByPhone</c>
/// endpoint — a second, narrow read-only CRM port alongside <see cref="ICrmGateway"/>
/// (unit-number lookup) and <c>ICrmCustomerLookupGateway</c> (generic
/// CRM/PACT/Tasleeh phone enrichment). This one is Buyer-specific: it returns
/// every matching CRM customer plus the units each owns whose Lead is Sold or
/// Contract, exactly as Tiger CRM itself already filters
/// (MVP-Implementation-Backlog.md's CRM Buyer Lookup increment).
///
/// <para>
/// <b>Read-only, and never resolves ambiguity on its own.</b> This is the raw
/// CRM contract boundary — it returns CRM's response exactly as CRM sent it,
/// including the (expected-never-to-happen) case of more than one customer
/// entry for one phone number. Business rule: CRM guarantees a phone number
/// belongs to exactly one customer; enforcing/consolidating that (and staying
/// resilient if CRM's own response does not honor it) is
/// <c>CrmBuyerLookupAppService</c>'s job, not this port's — this interface
/// stays a thin, unopinionated passthrough. One customer may still own
/// multiple valid units, and this port picks none of them: no automatic unit
/// selection, matching the business rule that only the CS agent (via the New
/// Ticket UI) may choose the relevant unit. This interface holds no
/// verification state and makes no ticket-creation decision, matching every
/// other CRM port's own documented boundary.
/// </para>
///
/// <para>
/// Implemented in TigerCS.Integrations.Modules.CrmIntegration
/// (<c>CrmBuyerHttpGateway</c>) as a real HTTP-backed gateway — unlike
/// <see cref="ICrmGateway"/>/<c>ICrmCustomerLookupGateway</c>, no Mock
/// implementation exists for this port: the CRM endpoint it calls has
/// already been implemented and manually verified CRM-side, so this is a
/// genuine integration from the start.
/// </para>
/// </summary>
public interface ICrmBuyerLookupGateway
{
    Task<CrmBuyerLookupResult> GetBuyerByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every outcome <see cref="ICrmBuyerLookupGateway.GetBuyerByPhoneAsync"/> can
/// return. Never throws for an expected CRM response shape (found, not
/// found, unauthorized, malformed) — only a genuinely unexpected failure
/// (timeout, DNS/connection failure) collapses into <see cref="Unavailable"/>.
/// </summary>
public enum CrmBuyerLookupOutcome
{
    /// <summary>At least one buyer with at least one Sold/Contract unit was found.</summary>
    Success,

    /// <summary>CRM answered with no matching buyer for this phone number (or every match's units were filtered out — see <c>CrmBuyerLookupAppService</c>).</summary>
    NotFound,

    /// <summary>CRM rejected the request's X-SECRET-KEY (401) — a configuration problem, not a data-not-found result.</summary>
    Unauthorized,

    /// <summary>CRM answered 200 with a body that does not parse as the documented contract, or answered 400, or <c>success:false</c>.</summary>
    InvalidResponse,

    /// <summary>CRM could not be reached at all — timeout, network failure, or an unexpected HTTP status.</summary>
    Unavailable,

    /// <summary>
    /// CRM answered with more than one distinct CustomerId for the same
    /// phone number — a CRM data-integrity conflict, since a phone number is
    /// expected to uniquely identify exactly one CRM customer. Never
    /// produced by the gateway itself (a straight passthrough of whatever
    /// CRM sent) — only <c>CrmBuyerLookupAppService</c> detects and returns
    /// this, after grouping CRM's response by CustomerId. No customer or
    /// unit is auto-selected when this outcome is returned: the caller (New
    /// Ticket) falls back to the manual Project/Unit Number path exactly as
    /// it does for <see cref="NotFound"/>, but is expected to show a
    /// distinct message naming the conflict rather than "not found".
    /// </summary>
    AmbiguousCustomerMatch
}

/// <summary>
/// WHY a CRM lookup did not produce an answer — a compatible extension: <see cref="CrmBuyerLookupOutcome"/> is unchanged (every existing
/// consumer keeps working), and this carries the detail the outcome collapses. A failure is never a "not found": callers that must tell
/// the two apart (the Genesys ingestion audit, the customer search) read this instead of guessing from the message text.
/// </summary>
public enum CrmLookupFailureKind
{
    /// <summary>The request timed out (HttpClient.Timeout elapsed).</summary>
    TimedOut,

    /// <summary>A network/DNS/connection failure — CRM could not be reached.</summary>
    Unreachable,

    /// <summary>The integration is not configured (missing secret key or base address) — nothing was sent.</summary>
    NotConfigured,

    /// <summary>CRM rejected the secret key (401).</summary>
    Unauthorized,

    /// <summary>CRM answered 400 — it rejected the request.</summary>
    RejectedRequest,

    /// <summary>CRM answered with a status the contract does not define (e.g. 500/503).</summary>
    UnexpectedStatus,

    /// <summary>CRM answered 200 with a body that does not parse as the documented contract, or <c>success:false</c>.</summary>
    MalformedResponse
}

/// <summary>
/// The outcome-wrapped result of a Buyer-by-phone lookup. <c>Buyers</c> is
/// only populated for <see cref="CrmBuyerLookupOutcome.Success"/>; every
/// other outcome carries no payload beyond an optional CRM-supplied
/// <c>Message</c> for diagnostics/logging (never shown to the customer).
/// <c>FailureKind</c> is set for the failure outcomes only (never for NotFound / Ambiguous) and says which kind of failure it was.
/// </summary>
public sealed record CrmBuyerLookupResult(
    CrmBuyerLookupOutcome Outcome, IReadOnlyList<CrmBuyerMatchDto>? Buyers = null, string? Message = null,
    CrmLookupFailureKind? FailureKind = null)
{
    public static CrmBuyerLookupResult Success(IReadOnlyList<CrmBuyerMatchDto> buyers, string? message = null) =>
        new(CrmBuyerLookupOutcome.Success, buyers, message);

    public static CrmBuyerLookupResult NotFound(string? message = null) =>
        new(CrmBuyerLookupOutcome.NotFound, Message: message);

    public static CrmBuyerLookupResult Unauthorized(string? message = null) =>
        new(CrmBuyerLookupOutcome.Unauthorized, Message: message, FailureKind: CrmLookupFailureKind.Unauthorized);

    public static CrmBuyerLookupResult InvalidResponse(string? message = null, CrmLookupFailureKind kind = CrmLookupFailureKind.MalformedResponse) =>
        new(CrmBuyerLookupOutcome.InvalidResponse, Message: message, FailureKind: kind);

    public static CrmBuyerLookupResult Unavailable(string? message = null, CrmLookupFailureKind kind = CrmLookupFailureKind.Unreachable) =>
        new(CrmBuyerLookupOutcome.Unavailable, Message: message, FailureKind: kind);

    public static CrmBuyerLookupResult AmbiguousCustomerMatch(string? message = null) =>
        new(CrmBuyerLookupOutcome.AmbiguousCustomerMatch, Message: message);
}
