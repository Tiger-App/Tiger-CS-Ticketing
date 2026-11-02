using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Customer → Previous Ticket History for a CRM-verified customer, keyed by
/// the exact <c>CrmBuyerCustomerId</c> the agent selected on the New Ticket
/// wizard's CRM Buyer Lookup step (<c>GET /api/crm/buyers</c>) — never by
/// phone number, since one phone number may match more than one CRM
/// customer. Sourced entirely from the existing Tickets table; this
/// controller never calls CRM (<see cref="CrmController"/>) itself, so
/// history remains available even when CRM is offline. Department
/// visibility is resolved server-side from the caller's own roles/
/// department membership, exactly as the ticket queue does
/// (<see cref="TicketsController.GetQueue"/>) — a caller who happens to know
/// a valid CrmBuyerCustomerId never sees a ticket outside their own visible
/// departments.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.CustomerHistory)]
[RequirePermission(Permissions.CustomersView)]
public class CustomerHistoryController(
    CustomerHistoryAppService customerHistoryAppService,
    CustomerSearchAppService customerSearchAppService) : ControllerBase
{
    /// <summary>Search every integrated verification source for a customer by phone number. CS Agent/CS Supervisor only.</summary>
    /// <remarks>
    /// The Dashboard/Customer Workspace's search: the real CRM Buyer Lookup
    /// plus the PACT and Tasleeh lookups, each reporting Found/NotFound/
    /// Failed independently — the same sources, gateways, and verification
    /// semantics as the New Ticket wizard, with no intake record created.
    /// Phone number is the only supported search key: no integrated source
    /// searches customers by name or unit number today. Scoped to
    /// PolicyNames.CustomerVerification, matching every other customer
    /// lookup surface.
    /// </remarks>
    /// <param name="phoneNumber">Required. The phone number to search for.</param>
    /// <response code="200">Each source's outcome and matched customers — possibly none.</response>
    /// <response code="400">phoneNumber was missing or blank.</response>
    [HttpGet("search")]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    [Tags(OpenApiTags.CustomerSearch)]
    [ProducesResponseType<CustomerSearchResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchCustomers([FromQuery] string phoneNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return ValidationProblem();
        }

        var result = await customerSearchAppService.SearchByPhoneAsync(phoneNumber.Trim(), cancellationToken);
        return Ok(result);
    }

    /// <summary>Previous tickets for a CRM-verified customer, newest first.</summary>
    /// <remarks>
    /// Used by the New Ticket wizard's Step 3 preview, right after the agent
    /// selects a CRM Buyer/unit — always the selected customer, never the
    /// first raw phone-search result. Never a live CRM call.
    /// </remarks>
    /// <param name="crmCustomerId">The CRM Buyer's customer id, as selected from <c>GET /api/crm/buyers</c>.</param>
    /// <param name="limit">Maximum tickets to return, newest first. Defaults to 5; capped at 50.</param>
    /// <param name="unitNumber">Optional. Narrows the history to tickets whose unit-number snapshot exactly matches — the New Ticket wizard's same-customer/same-unit related-tickets check (Phase E). Advisory only; never blocks creation.</param>
    /// <param name="orderActiveFirst">Optional. Sorts currently-active tickets ahead of Resolved/Closed ones (newest first within each group) — used with <paramref name="unitNumber"/> so the most actionable related tickets surface within the limit.</param>
    /// <response code="200">The customer's ticket-count summary and its most recent tickets (possibly empty).</response>
    [HttpGet("crm/{crmCustomerId:int}/ticket-history")]
    [ProducesResponseType<CustomerHistoryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCrmCustomerHistory(
        int crmCustomerId, [FromQuery] int? limit, [FromQuery] string? unitNumber, [FromQuery] bool orderActiveFirst,
        CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerHistoryAppService.GetByCrmCustomerIdAsync(
            employeeId.Value, GetRoles(), crmCustomerId, excludeTicketId: null, limit, unitNumber, orderActiveFirst, cancellationToken);
        return Ok(result);
    }

    /// <summary>Previous tickets for an externally-verified customer (PACT/Tasleeh), newest first.</summary>
    /// <remarks>
    /// Keyed by the persisted external verification identity the customer's
    /// tickets already carry (CustomerVerificationSource + ExternalCustomerId)
    /// — never by display name and never by phone number, so two customers
    /// with similar contact data can never share a history. Same
    /// department-visibility scoping as the CRM history endpoint above.
    /// </remarks>
    /// <param name="source">The verification source, e.g. "Pact" or "Tasleeh".</param>
    /// <param name="externalCustomerId">The source's own customer identifier (for PACT, its tenantID).</param>
    /// <param name="limit">Maximum tickets to return, newest first. Defaults to 5; capped at 50.</param>
    /// <param name="unitNumber">Optional. Same-unit narrowing for the related-tickets check — see the CRM history endpoint above.</param>
    /// <param name="orderActiveFirst">Optional. Active-tickets-first ordering for the related-tickets check — see the CRM history endpoint above.</param>
    /// <response code="200">The customer's ticket-count summary and its most recent tickets (possibly empty).</response>
    [HttpGet("external/{source}/{externalCustomerId}/ticket-history")]
    [ProducesResponseType<CustomerHistoryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetExternalCustomerHistory(
        string source, string externalCustomerId, [FromQuery] int? limit, [FromQuery] string? unitNumber,
        [FromQuery] bool orderActiveFirst, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerHistoryAppService.GetByExternalIdentityAsync(
            employeeId.Value, GetRoles(), source, externalCustomerId, excludeTicketId: null, limit, unitNumber, orderActiveFirst,
            cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Returns visible ticket history for a CRM customer found by phone, including
    /// PACT history only when the customer identity and physical unit match.
    /// </summary>
    [HttpGet("lookup/ticket-history")]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    [ProducesResponseType<CustomerHistoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLookupHistory(
        [FromQuery] string phoneNumber, [FromQuery] int crmCustomerId, [FromQuery] int? limit,
        [FromQuery] string? unitNumber, [FromQuery] bool orderActiveFirst, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } employeeId) return Unauthorized();
        if (string.IsNullOrWhiteSpace(phoneNumber) || crmCustomerId <= 0) return BadRequest();
        var search = await customerSearchAppService.SearchByPhoneAsync(phoneNumber, cancellationToken);
        var buyer = search.CrmBuyers.SingleOrDefault(b => b.Customer.CustomerId == crmCustomerId);
        if (buyer is null) return NotFound();
        var pact = CustomerIdentityLinker.FindPactMatch(buyer, search.CrmBuyers, search.ExternalSources);
        return Ok(pact is null
            ? await customerHistoryAppService.GetByCrmCustomerIdAsync(employeeId, GetRoles(), crmCustomerId,
                limit: limit, unitNumber: unitNumber, orderActiveFirst: orderActiveFirst, cancellationToken: cancellationToken)
            : await customerHistoryAppService.GetByLinkedIdentityAsync(employeeId, GetRoles(), crmCustomerId,
                pact.ExternalCustomerId, limit, unitNumber, orderActiveFirst, cancellationToken));
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}
