using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Customer Details/Profile — the Overview/Contact Info/Units tabs on the
/// Customer Profile page. Ticket-anchored, exactly like
/// <see cref="CustomerHistoryAppService.GetForTicketAsync"/>: the identity
/// (<c>CrmBuyerCustomerId</c>) and the department-visibility check both come
/// from the anchor ticket, never from a caller-supplied phone number or CRM
/// id — a caller cannot use this to browse a customer they could not
/// otherwise see via Ticket Details.
///
/// <para>
/// <b>Thin orchestration only — no CRM logic duplicated.</b> This service
/// does exactly four things: authorize against the ticket (reusing
/// <see cref="TicketQueryAppService"/>, same as Ticket Details itself),
/// resolve the phone number the ticket was verified against (see below),
/// delegate the actual CRM search to <see cref="CrmBuyerLookupAppService"/>
/// — the same service the New Ticket wizard's CRM Buyer Lookup step uses,
/// through the same real <c>ICrmBuyerLookupGateway</c> — and confirm the
/// Buyer CRM returned is the ticket's own persisted customer. It re-filters
/// nothing, selects no unit, and re-queries CRM through nothing else: the
/// older generic <c>ICrmGateway</c> port (unit-number lookup, Mock-only at
/// this phase) is deliberately not a dependency here — every field the
/// profile shows is already carried by <see cref="CrmBuyerMatchDto"/>.
/// </para>
///
/// <para>
/// <b>Phone resolution.</b> CRM is searched by phone number only, and the
/// phone is never caller-supplied: it comes from the IntakeRecord this
/// ticket was promoted from (the same resolution
/// <see cref="CustomerHistoryAppService"/> uses for its unverified
/// fallback), or — for a ticket with no linked IntakeRecord — from the
/// ticket's originating <c>TicketInteraction</c>, which persists the same
/// intake phone at creation time. A CRM-verified ticket with neither on
/// record reports <c>"NoPhoneOnRecord"</c> rather than pretending CRM was
/// asked and could not answer.
/// </para>
///
/// <para>
/// <b>Identity is validated, never inferred from the phone alone.</b> A
/// phone number can be reassigned in CRM after the ticket was created. The
/// Buyer CRM returns is accepted only when its CustomerId equals the
/// ticket's persisted <c>CrmBuyerCustomerId</c>; any other customer is
/// reported as <c>"NotFoundInCrm"</c> (CRM no longer resolves this phone to
/// this customer) and logged for investigation, so the page never silently
/// shows a different customer's name, contact details or units.
/// </para>
///
/// <para>
/// <b>Live CRM data, unlike Customer History.</b> Previous Tickets on the
/// Customer Profile page reuses <c>CustomerHistoryAppService</c> unchanged
/// (via its own existing endpoint) precisely because history must work with
/// CRM offline; Overview/Contact Info/Units cannot make that promise — full
/// name (Arabic), mobile number, email, and the customer's full current unit
/// list simply do not exist anywhere in Ticketing's own persisted data, only
/// in CRM. When CRM cannot be searched (unavailable, no longer finds a
/// match, or a data-integrity conflict), <see cref="CustomerProfileDto.Status"/>
/// says so and the caller still knows the ticket's own CrmBuyerCustomerId.
/// </para>
/// </summary>
public sealed class CustomerProfileAppService(
    ITicketRepository ticketRepository,
    IIntakeRecordRepository intakeRecordRepository,
    ITicketInteractionRepository interactionRepository,
    CrmBuyerLookupAppService crmBuyerLookupAppService,
    TicketQueryAppService ticketQueryAppService,
    ILogger<CustomerProfileAppService> logger)
{
    public async Task<CustomerProfileResult> GetForTicketAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return CustomerProfileResult.Failure(CustomerProfileOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return CustomerProfileResult.Failure(CustomerProfileOutcome.Forbidden);
        }

        if (ticket.CrmBuyerCustomerId is not { } crmBuyerCustomerId)
        {
            return CustomerProfileResult.Success(Empty(null, "NotCrmVerified"));
        }

        var phoneNumber = await ResolvePhoneNumberAsync(ticketId, cancellationToken);
        if (phoneNumber is null)
        {
            logger.LogWarning(
                "Customer profile for ticket {TicketId} (CrmBuyerCustomerId {CrmBuyerCustomerId}) has no phone number on record — "
                + "no linked IntakeRecord and no originating TicketInteraction — so CRM cannot be re-queried.",
                ticketId, crmBuyerCustomerId);
            return CustomerProfileResult.Success(Empty(crmBuyerCustomerId, "NoPhoneOnRecord"));
        }

        var lookup = await crmBuyerLookupAppService.GetBuyerByPhoneAsync(phoneNumber, cancellationToken);
        return CustomerProfileResult.Success(ToDto(ticketId, crmBuyerCustomerId, lookup));
    }

    /// <summary>
    /// The intake phone this ticket was CRM-verified against — never
    /// caller-supplied. The linked IntakeRecord is authoritative; the
    /// originating TicketInteraction (which copies that same phone at
    /// creation) covers a ticket whose IntakeRecord link is missing.
    /// </summary>
    private async Task<string?> ResolvePhoneNumberAsync(long ticketId, CancellationToken cancellationToken)
    {
        var intakeRecord = await intakeRecordRepository.GetByLinkedTicketIdAsync(ticketId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(intakeRecord?.PhoneNumber))
        {
            return intakeRecord.PhoneNumber;
        }

        var originatingInteraction = await interactionRepository.GetOriginatingAsync(ticketId, cancellationToken);
        return string.IsNullOrWhiteSpace(originatingInteraction?.CustomerPhone) ? null : originatingInteraction.CustomerPhone;
    }

    private CustomerProfileDto ToDto(long ticketId, int crmBuyerCustomerId, CrmBuyerLookupResult lookup)
    {
        if (lookup.Outcome == CrmBuyerLookupOutcome.Success && lookup.Buyers is { Count: > 0 } buyers)
        {
            // CrmBuyerLookupAppService already consolidates to one customer
            // (and reports AmbiguousCustomerMatch instead of guessing when
            // CRM names several), so this is a strict identity check, not a
            // choice between candidates: the returned Buyer must be the
            // customer this ticket was verified against.
            var match = buyers.FirstOrDefault(b => b.Customer.CustomerId == crmBuyerCustomerId);
            if (match is null)
            {
                logger.LogWarning(
                    "CRM GetBuyerByPhone resolved ticket {TicketId}'s phone to CRM customer(s) {ReturnedCustomerIds}, not the ticket's own "
                    + "CrmBuyerCustomerId {CrmBuyerCustomerId}. Not showing another customer's profile — reporting NotFoundInCrm.",
                    ticketId, string.Join(",", buyers.Select(b => b.Customer.CustomerId).Distinct()), crmBuyerCustomerId);
                return Empty(crmBuyerCustomerId, "NotFoundInCrm");
            }

            var customer = match.Customer;
            var units = match.Units.Select(u => new CustomerProfileUnitDto(
                u.UnitId, u.ProjectName, u.UnitNumber, u.LeadStatus, u.LeadStatusName, u.UnitType, u.FloorNumber)).ToList();
            return new CustomerProfileDto(
                crmBuyerCustomerId, "Found", customer.FullNameEnglish, customer.FullNameArabic, customer.MobileNumber, customer.Email, units);
        }

        var status = lookup.Outcome switch
        {
            CrmBuyerLookupOutcome.AmbiguousCustomerMatch => "AmbiguousCustomerMatch",
            CrmBuyerLookupOutcome.NotFound => "NotFoundInCrm",
            _ => "CrmUnavailable"
        };

        // The one diagnostic that tells an operator WHY the page says "CRM
        // unavailable": the gateway's own outcome (Unavailable / Unauthorized
        // / InvalidResponse all surface as CrmUnavailable) and CRM's message.
        // The phone number is deliberately not logged here — CrmBuyerHttpGateway
        // already logs it masked alongside the underlying HTTP failure.
        logger.LogWarning(
            "Customer profile for ticket {TicketId} (CrmBuyerCustomerId {CrmBuyerCustomerId}) is {Status}: CRM Buyer lookup outcome "
            + "{LookupOutcome} ({FailureReason}){CrmMessage}.",
            ticketId, crmBuyerCustomerId, status, lookup.Outcome, lookup.FailureReason?.ToString() ?? "n/a",
            string.IsNullOrWhiteSpace(lookup.Message) ? string.Empty : $" — \"{lookup.Message}\"");
        return Empty(crmBuyerCustomerId, status);
    }

    private static CustomerProfileDto Empty(int? crmBuyerCustomerId, string status) =>
        new(crmBuyerCustomerId, status, null, null, null, null, []);
}
