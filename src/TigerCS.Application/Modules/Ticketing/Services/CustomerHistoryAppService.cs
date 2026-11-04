using TigerCS.Application.Modules.Ticketing.CustomerNames;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Customer → Previous Ticket History. Reads exclusively from the existing
/// Tickets (and, for the unverified fallback, IntakeRecords) tables — never
/// a live CRM call — so history is available even when CRM is offline.
///
/// <para>
/// <b>Three identities, never combined.</b> A CRM-verified customer's
/// identity is <c>Ticket.CrmBuyerCustomerId</c> — a phone number may match
/// more than one CRM customer, so history for a verified customer is always
/// scoped by the exact selected <c>CrmBuyerCustomerId</c>, never widened by
/// phone. An externally-verified customer's identity (PACT/Tasleeh) is the
/// persisted <c>Ticket.CustomerVerificationSource</c> +
/// <c>Ticket.ExternalCustomerId</c> pair — never a display-name or phone
/// match, so two different customers sharing similar contact data can never
/// share a history. Only when a ticket carries neither identity does this
/// service fall back to the persisted <c>IntakeRecord.PhoneNumber</c>
/// snapshot linked to that ticket — a weaker identity (one phone number can
/// belong to more than one real customer), so that result is always labelled
/// <c>"Unverified"</c> rather than presented the same way as verified
/// history.
/// </para>
///
/// <para>
/// <b>Authorization: identical department visibility to the ticket
/// queue/detail (TicketQueryAppService), never widened.</b> Knowing a CRM
/// customer id, an external customer id, or a ticket id never grants access
/// to a ticket the caller could not otherwise see — every query here is
/// scoped by the caller's own visible departments, and the ticket-anchored
/// lookup additionally checks the anchor ticket's own department visibility
/// before running anything.
/// </para>
/// </summary>
public sealed class CustomerHistoryAppService(
    ITicketRepository ticketRepository,
    IIntakeRecordRepository intakeRecordRepository,
    ITicketResolutionRepository ticketResolutionRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    TicketQueryAppService ticketQueryAppService,
    ReopenPolicy reopenPolicy,
    TimeProvider timeProvider)
{
    /// <summary>The New Ticket preview's default page size — a "reasonable latest-ticket limit", never the customer's unbounded history.</summary>
    public const int DefaultLimit = 5;
    public const int MaxLimit = 50;

    /// <summary>
    /// Verified path: <c>GET /api/customers/crm/{crmCustomerId}/ticket-history</c>.
    /// Used by the New Ticket wizard's preview and the Customer Workspace
    /// once the agent has explicitly selected a CRM Buyer — the identity
    /// queried is always the one the agent selected, never inferred from
    /// the raw phone search results.
    ///
    /// <para>
    /// <paramref name="unitNumber"/>/<paramref name="orderActiveFirst"/> are
    /// Phase E's duplicate-ticket awareness knobs: the wizard's
    /// related-tickets check narrows this same identity's history to the
    /// selected unit and surfaces active tickets first — one scoped
    /// repository query either way, never a per-row follow-up.
    /// </para>
    /// </summary>
    public async Task<CustomerHistoryDto> GetByCrmCustomerIdAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        int crmBuyerCustomerId,
        long? excludeTicketId = null,
        int? limit = null,
        string? unitNumber = null,
        bool orderActiveFirst = false,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);

        var result = await ticketRepository.SearchCustomerHistoryAsync(
            new CustomerHistoryQuery(
                visibleDepartmentIds, crmBuyerCustomerId, TicketIds: null, excludeTicketId, NormalizeLimit(limit),
                UnitNumber: NormalizeUnitNumber(unitNumber), OrderActiveFirst: orderActiveFirst, AgentScopeEmployeeId: ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles)),
            cancellationToken);

        return await ToDtoAsync("Verified", crmBuyerCustomerId, phoneNumberSnapshot: null, result,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// External-identity path (Customer Workspace):
    /// <c>GET /api/customers/external/{source}/{externalCustomerId}/ticket-history</c>.
    /// Keyed by the persisted external verification identity a PACT/Tasleeh
    /// ticket already carries (<c>CustomerVerificationSource</c> +
    /// <c>ExternalCustomerId</c>) — never by display name and never by phone,
    /// so an external customer's history can never absorb a different
    /// customer's manual or CRM tickets.
    /// </summary>
    public async Task<CustomerHistoryDto> GetByExternalIdentityAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        string externalSource,
        string externalCustomerId,
        long? excludeTicketId = null,
        int? limit = null,
        string? unitNumber = null,
        bool orderActiveFirst = false,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);

        var result = await ticketRepository.SearchCustomerHistoryAsync(
            new CustomerHistoryQuery(
                visibleDepartmentIds, CrmBuyerCustomerId: null, TicketIds: null, excludeTicketId, NormalizeLimit(limit),
                ExternalSource: externalSource, ExternalCustomerId: externalCustomerId,
                UnitNumber: NormalizeUnitNumber(unitNumber), OrderActiveFirst: orderActiveFirst, AgentScopeEmployeeId: ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles)),
            cancellationToken);

        return await ToDtoAsync("ExternalVerified", crmBuyerCustomerId: null, phoneNumberSnapshot: null, result,
            externalSource: externalSource, externalCustomerId: externalCustomerId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Ticket-anchored path: <c>GET /api/tickets/{ticketId}/customer-history</c>.
    /// The only entry point Ticket Details uses — it derives the customer
    /// identity from the ticket itself (CrmBuyerCustomerId when present,
    /// then the persisted external verification identity, otherwise the
    /// linked IntakeRecord's phone snapshot), so callers never supply a raw
    /// phone number or customer id directly. The current ticket is always
    /// excluded from its own history.
    /// </summary>
    public async Task<CustomerHistoryResult> GetForTicketAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return CustomerHistoryResult.Failure(CustomerHistoryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return CustomerHistoryResult.Failure(CustomerHistoryOutcome.Forbidden);
        }

        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);
        var boundedLimit = NormalizeLimit(limit);

        if (ticket.CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            var verifiedResult = await ticketRepository.SearchCustomerHistoryAsync(
                new CustomerHistoryQuery(visibleDepartmentIds, crmBuyerCustomerId, TicketIds: null, ticketId, boundedLimit, AgentScopeEmployeeId: ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles)),
                cancellationToken);

            return CustomerHistoryResult.Success(
                await ToDtoAsync("Verified", crmBuyerCustomerId, phoneNumberSnapshot: null, verifiedResult,
                    ticket.CrmBuyerCustomerName, cancellationToken: cancellationToken));
        }

        // Externally-verified anchor (PACT/Tasleeh): the persisted external
        // identity pair is a real identity — use it, never the phone
        // fallback, so external history stays exactly this customer's.
        if (ticket.CustomerVerificationSource is { } anchorSource && ticket.ExternalCustomerId is { } anchorExternalId)
        {
            var externalResult = await ticketRepository.SearchCustomerHistoryAsync(
                new CustomerHistoryQuery(
                    visibleDepartmentIds, CrmBuyerCustomerId: null, TicketIds: null, ticketId, boundedLimit,
                    ExternalSource: anchorSource, ExternalCustomerId: anchorExternalId, AgentScopeEmployeeId: ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles)),
                cancellationToken);

            return CustomerHistoryResult.Success(
                await ToDtoAsync("ExternalVerified", crmBuyerCustomerId: null, phoneNumberSnapshot: null, externalResult,
                    // The verified customer's own name, snapshotted on the ticket - without it an externally verified
                    // customer reads as "Pact customer" although the ticket holds their name.
                    ticket.ExternalCustomerName,
                    externalSource: anchorSource, externalCustomerId: anchorExternalId, cancellationToken: cancellationToken));
        }

        // Fallback path — no verified identity on this ticket at all. The
        // phone number is read from the IntakeRecord this ticket was
        // promoted from (never a live CRM call, never caller-supplied), and
        // ListLinkedTicketIdsByPhoneNumberAsync resolves the matching ticket
        // ids without loading every Ticket into memory to filter by hand.
        var intakeRecord = await intakeRecordRepository.GetByLinkedTicketIdAsync(ticketId, cancellationToken);
        if (intakeRecord is null)
        {
            return CustomerHistoryResult.Success(
                await ToDtoAsync("Unverified", null, null, new CustomerHistoryQueryResult([], 0, 0, 0),
                    cancellationToken: cancellationToken));
        }

        var linkedTicketIds = await intakeRecordRepository.ListLinkedTicketIdsByPhoneNumberAsync(intakeRecord.PhoneNumber, cancellationToken);
        var unverifiedResult = await ticketRepository.SearchCustomerHistoryAsync(
            new CustomerHistoryQuery(visibleDepartmentIds, CrmBuyerCustomerId: null, linkedTicketIds, ticketId, boundedLimit, AgentScopeEmployeeId: ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles)),
            cancellationToken);

        return CustomerHistoryResult.Success(
            await ToDtoAsync("Unverified", null, intakeRecord.PhoneNumber, unverifiedResult,
                cancellationToken: cancellationToken));
    }

    /// <summary>Called only after the lookup has verified the CRM/PACT association.</summary>
    public async Task<CustomerHistoryDto> GetByLinkedIdentityAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles,
        int crmCustomerId, string pactCustomerId, int? limit, string? unitNumber,
        bool orderActiveFirst, CancellationToken cancellationToken)
    {
        var visible = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(callerEmployeeId, callerRoles, cancellationToken);
        var result = await ticketRepository.SearchCustomerHistoryAsync(new CustomerHistoryQuery(
            visible, crmCustomerId, null, null, NormalizeLimit(limit), "Pact", pactCustomerId,
            NormalizeUnitNumber(unitNumber), orderActiveFirst, IncludeLinkedExternalIdentity: true, AgentScopeEmployeeId: ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles)), cancellationToken);
        return await ToDtoAsync("LinkedVerified", crmCustomerId, null, result,
            externalSource: "Pact", externalCustomerId: pactCustomerId, cancellationToken: cancellationToken);
    }

    private static int NormalizeLimit(int? limit) => limit is null or <= 0 || limit > MaxLimit ? DefaultLimit : limit.Value;

    private static string? NormalizeUnitNumber(string? unitNumber) =>
        string.IsNullOrWhiteSpace(unitNumber) ? null : unitNumber.Trim();

    private async Task<CustomerHistoryDto> ToDtoAsync(
        string verificationType,
        int? crmBuyerCustomerId,
        string? phoneNumberSnapshot,
        CustomerHistoryQueryResult result,
        string? customerDisplayName = null,
        string? externalSource = null,
        string? externalCustomerId = null,
        CancellationToken cancellationToken = default)
    {
        // One batched lookup stamps ResolvedAtUtc/reopen-eligibility onto
        // the returned page — the same ReopenPolicy the Reopen action
        // enforces, so a "Reopen" affordance in a history list can never
        // disagree with the endpoint's own rule.
        var resolvedTicketIds = result.Tickets
            .Where(t => t.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed)
            .Select(t => t.TicketId)
            .ToList();
        var currentResolutions = resolvedTicketIds.Count > 0
            ? await ticketResolutionRepository.ListCurrentByTicketIdsAsync(resolvedTicketIds, cancellationToken)
            : new Dictionary<long, TicketResolution>();

        // Reopen eligibility runs from the CLOSURE moment under the approved
        // rule, which lives in lifecycle history rather than on the ticket —
        // so it is batched here too, one query for the whole page, never one
        // per row.
        var closedTicketIds = result.Tickets
            .Where(t => t.TicketStatus is TicketStatus.Closed)
            .Select(t => t.TicketId)
            .ToList();
        var closedMoments = closedTicketIds.Count > 0
            ? await statusHistoryRepository.ListLatestTransitionMomentsAsync(
                closedTicketIds, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Closed, cancellationToken)
            : new Dictionary<long, DateTime>();

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        return new(
            verificationType,
            crmBuyerCustomerId,
            phoneNumberSnapshot,
            // A real name only: a placeholder snapshot ("NOT_REGISTERED") is neither shown nor allowed to stand in for a name.
            CustomerNameResolver.FirstValid(customerDisplayName)
                ?? CustomerNameResolver.FirstValid(result.Tickets.Select(t => t.CrmBuyerCustomerName).ToArray()),
            result.TotalCount,
            result.OpenCount,
            result.ClosedCount,
            result.Tickets.Select(t => ToTicketDto(t, currentResolutions, closedMoments, nowUtc)).ToList(),
            externalSource,
            externalCustomerId,
            // The Customers directory key this history belongs to (CRM id,
            // else external identity, else intake phone) — what the Web
            // links to as the customer's profile. Null when the anchor
            // ticket has no customer identity at all.
            CustomerIdentity.FromTicketFacts(crmBuyerCustomerId, externalSource, externalCustomerId, phoneNumberSnapshot)?.Key);
    }

    private CustomerHistoryTicketDto ToTicketDto(
        Ticket ticket,
        IReadOnlyDictionary<long, TicketResolution> currentResolutions,
        IReadOnlyDictionary<long, DateTime> closedMoments,
        DateTime nowUtc)
    {
        var resolvedAtUtc = currentResolutions.TryGetValue(ticket.TicketId, out var resolution)
            ? resolution.ResolvedAtUtc
            : (DateTime?)null;

        var closedAtUtc = closedMoments.TryGetValue(ticket.TicketId, out var closedAt)
            ? closedAt
            : (DateTime?)null;

        return new(
            ticket.TicketId,
            ticket.TicketNumber,
            ticket.CreatedAtUtc,
            ticket.TicketStatus.ToString(),
            ticket.PriorityId,
            ticket.CategoryId,
            ticket.CurrentDepartmentId,
            ticket.CrmBuyerProjectName ?? ticket.ManualProjectName,
            ticket.CrmBuyerUnitNumber ?? ticket.ManualUnitNumber,
            ticket.VerificationStatus.ToString(),
            ticket.RequestSummary,
            resolvedAtUtc,
            reopenPolicy.IsReopenEligible(
                ticket.TicketStatus, ticket.ResolutionOutcome, closedAtUtc, nowUtc,
                resolution?.ClosedForCustomerInactivity == true));
    }
}
