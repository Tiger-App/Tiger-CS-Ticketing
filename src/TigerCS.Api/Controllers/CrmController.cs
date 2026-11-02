using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// A thin passthrough onto Tiger CRM's read-only data-access port
/// (<c>ICrmGateway</c>) — unit lookup by unit number, and the contacts/
/// owners/tenants/authorized representatives linked to a unit
/// (MVP-API-Contracts.md §2.1-§2.3). This controller makes no verification
/// decision of its own; it exists so an agent (or the verification-session
/// flow below) has something to look up and select from. See
/// <c>CrmUnitLookupAppService</c>'s and <c>VerificationSessionAppService</c>'s
/// remarks for the full ownership boundary between Tiger CRM (read-only)
/// and Tiger CS Ticketing (owns the verification decision).
///
/// <para>
/// Scoped to CS Agent/CS Supervisor only (PolicyNames.CustomerVerification)
/// — not the contract's literal "Agent and above" wording, and not the
/// global AuthenticatedStaff fallback. See
/// PolicyNames.CustomerVerification's remarks for why: Solution-Analysis.md
/// §4.1's Permission Matrix grants ticket-Create to only these two roles,
/// and customer/requester verification exists solely to gate ticket
/// creation. Reporting User, Department Employee, Department Head, CS
/// Manager, GM, Chairman/CEO, and System Administrator are all denied —
/// confirmed, not guessed.
/// </para>
/// </summary>
[ApiController]
[Route("api/crm")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.CrmLookup)]
[RequirePermission(Permissions.TicketsCreate)]
public class CrmController(CrmUnitLookupAppService crmUnitLookupAppService, CrmBuyerLookupAppService crmBuyerLookupAppService)
    : ControllerBase
{
    /// <summary>Look a single CRM unit up by its CRM identifier.</summary>
    /// <remarks>Read-only passthrough onto Tiger CRM. MVP-API-Contracts.md §2.1.</remarks>
    /// <param name="crmUnitId">The unit's Tiger CRM identifier.</param>
    /// <response code="200">The unit, with the number of contacts linked to it.</response>
    /// <response code="404">No unit with that CRM identifier.</response>
    /// <response code="502">Tiger CRM could not be reached.</response>
    [HttpGet("units/{crmUnitId}")]
    [ProducesResponseType<UnitVerificationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetUnit(string crmUnitId, CancellationToken cancellationToken)
    {
        var result = await crmUnitLookupAppService.GetUnitAsync(crmUnitId, cancellationToken);

        return result.Outcome switch
        {
            CrmLookupOutcome.Success => Ok(result.Response),
            CrmLookupOutcome.NotFound => Problem(
                type: "https://tigercs.internal/problems/unit-not-found",
                title: "Unit not found",
                statusCode: StatusCodes.Status404NotFound),
            _ => Problem(
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>Search CRM units by unit number, optionally narrowed by property name.</summary>
    /// <remarks>Read-only passthrough onto Tiger CRM. MVP-API-Contracts.md §2.2.</remarks>
    /// <param name="unitNumber">Required. The unit number to search for.</param>
    /// <param name="propertyName">Optional. Narrows the search to one property.</param>
    /// <response code="200">The matching units — an empty array when nothing matched.</response>
    /// <response code="400">unitNumber was missing or blank.</response>
    /// <response code="502">Tiger CRM could not be reached.</response>
    [HttpGet("units/search")]
    [ProducesResponseType<IReadOnlyList<UnitVerificationResponseDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SearchUnits(
        [FromQuery] string unitNumber, [FromQuery] string? propertyName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(unitNumber))
        {
            return ValidationProblem();
        }

        var result = await crmUnitLookupAppService.SearchUnitsAsync(unitNumber, propertyName, cancellationToken);

        return result.Outcome switch
        {
            CrmLookupOutcome.Success => Ok(result.Units),
            _ => Problem(
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>The contacts linked to a CRM unit — owners, tenants, and authorized representatives.</summary>
    /// <remarks>Read-only passthrough onto Tiger CRM. MVP-API-Contracts.md §2.3.</remarks>
    /// <param name="crmUnitId">The unit's Tiger CRM identifier.</param>
    /// <response code="200">The unit's contacts. contactType is one of Owner, Tenant, Representative.</response>
    /// <response code="404">No unit with that CRM identifier.</response>
    /// <response code="502">Tiger CRM could not be reached.</response>
    [HttpGet("units/{crmUnitId}/contacts")]
    [ProducesResponseType<IReadOnlyList<ContactVerificationResponseDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetContacts(string crmUnitId, CancellationToken cancellationToken)
    {
        var result = await crmUnitLookupAppService.GetContactsAsync(crmUnitId, cancellationToken);

        return result.Outcome switch
        {
            CrmLookupOutcome.Success => Ok(result.Contacts),
            CrmLookupOutcome.NotFound => Problem(
                type: "https://tigercs.internal/problems/unit-not-found",
                title: "Unit not found",
                statusCode: StatusCodes.Status404NotFound),
            _ => Problem(
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>
    /// CRM Buyer Lookup: search Tiger CRM by phone number for Buyer records
    /// (Lead status Sold or Contract). Read-only passthrough onto CRM's own
    /// <c>GET /TicketingSystem/GetBuyerByPhone</c> endpoint.
    /// </summary>
    /// <remarks>
    /// Business rule: a CRM phone number belongs to exactly one customer, so
    /// this endpoint returns at most one entry (<c>CrmBuyerLookupAppService</c>
    /// consolidates to a single customer even if CRM's own response does
    /// not honor that). A buyer is not assumed to own only one unit — every
    /// eligible (Sold/Contract, Buyer) unit that customer owns is returned,
    /// and this endpoint never selects one for the caller; that selection is
    /// left to the CS agent in the New Ticket UI.
    /// </remarks>
    /// <param name="phoneNumber">Required. The phone number to search CRM for.</param>
    /// <response code="200">The matching buyer (at most one), with the units they own that are eligible (Sold/Contract, Buyer).</response>
    /// <response code="400">phoneNumber was missing or blank, or CRM rejected/could not parse the request.</response>
    /// <response code="401">CRM rejected the configured Crm:SecretKey.</response>
    /// <response code="404">No matching Buyer for this phone number.</response>
    /// <response code="409">CRM returned more than one distinct customer for this phone number — a CRM data-integrity conflict. No customer/unit was selected; fall back to manual Project/Unit Number entry.</response>
    /// <response code="502">Tiger CRM could not be reached.</response>
    [HttpGet("buyers")]
    [ProducesResponseType<IReadOnlyList<CrmBuyerMatchDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetBuyerByPhone([FromQuery] string phoneNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return ValidationProblem();
        }

        var result = await crmBuyerLookupAppService.GetBuyerByPhoneAsync(phoneNumber, cancellationToken);

        return result.Outcome switch
        {
            CrmBuyerLookupOutcome.Success => Ok(result.Buyers),
            CrmBuyerLookupOutcome.NotFound => Problem(
                type: "https://tigercs.internal/problems/buyer-not-found",
                title: "No matching buyer",
                statusCode: StatusCodes.Status404NotFound),
            CrmBuyerLookupOutcome.Unauthorized => Problem(
                type: "https://tigercs.internal/problems/crm-unauthorized",
                title: "CRM rejected the configured secret key",
                statusCode: StatusCodes.Status401Unauthorized),
            CrmBuyerLookupOutcome.InvalidResponse => Problem(
                type: "https://tigercs.internal/problems/crm-invalid-response",
                title: "CRM rejected or returned an unusable response for this request",
                statusCode: StatusCodes.Status400BadRequest),
            CrmBuyerLookupOutcome.AmbiguousCustomerMatch => Problem(
                type: "https://tigercs.internal/problems/crm-buyer-ambiguous-customer-match",
                title: "Multiple CRM customer records were found for this phone number",
                detail: "CRM returned more than one distinct customer for this phone number. Continue with manual verification.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Problem(
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }
}
