using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// The Customer Workspace search result (<c>GET /api/customers/search</c>):
/// one phone number searched across every verification source the system
/// already integrates — the real CRM Buyer Lookup plus the PACT/Tasleeh
/// external lookups — with each source's own outcome reported independently,
/// exactly as the New Ticket wizard's lookup step reports them. Composes the
/// existing per-source DTO shapes rather than remapping them, so the
/// workspace and the wizard can never drift apart on what a match looks
/// like. Phone is the only search key: no integrated source supports
/// customer search by name or unit number today, and this contract does not
/// pretend otherwise.
/// </summary>
/// <param name="PhoneNumber">The phone number that was searched, echoed back.</param>
/// <param name="CrmStatus">The CRM Buyer Lookup outcome: Found, NotFound, AmbiguousMatch (CRM returned conflicting customers — fall back to manual), or Failed (CRM unreachable/misconfigured).</param>
/// <param name="CrmBuyers">The matched CRM Buyer (at most one, with every eligible unit) when <paramref name="CrmStatus"/> is Found; empty otherwise.</param>
/// <param name="ExternalSources">The PACT and Tasleeh results — each Found/NotFound/Failed with its matched customers, same shape as the intake-anchored customer lookup.</param>
/// <param name="CrmFailureKind">
/// Additive refinement of <paramref name="CrmStatus"/> = Failed, null otherwise: <c>Unavailable</c> (CRM not configured, unreachable or answering with an
/// error status), <c>Timeout</c> (no answer in time) or <c>Error</c> (rejected credentials, an unusable response or any other fault). A Failed lookup is
/// never evidence that the customer is not registered - only <c>NotFound</c> is.
/// </param>
public sealed record CustomerSearchResultDto(
    string PhoneNumber,
    string CrmStatus,
    IReadOnlyList<CrmBuyerMatchDto> CrmBuyers,
    IReadOnlyList<CustomerLookupSourceResultDto> ExternalSources,
    string? CrmFailureKind = null);
