using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The Customers directory: the paginated list of customers TigerCS already
/// knows (from its own persisted tickets — never a CRM list call, which no
/// integrated system offers) and the per-customer profile behind each row.
/// Visibility is the ticket queue's own department scope, resolved by
/// <see cref="TicketQueryAppService"/> from the caller's roles and
/// memberships, so a customer only appears through tickets the caller may
/// see.
/// </summary>
public sealed class CustomerDirectoryAppService(
    ICustomerDirectoryRepository repository,
    TicketQueryAppService ticketQueryAppService,
    ITicketRepository ticketRepository)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public async Task<CustomerDirectoryListResultDto> ListAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        CustomerDirectoryListRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(callerEmployeeId, callerRoles, cancellationToken);

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > MaxPageSize ? DefaultPageSize : request.PageSize;

        var result = await repository.ListAsync(
            new CustomerDirectoryQuery(
                visibleDepartmentIds,
                string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
                string.IsNullOrWhiteSpace(request.VerificationSource) ? null : request.VerificationSource.Trim(),
                request.DepartmentId,
                request.OpenOnly,
                page,
                pageSize,
                ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles)),
            cancellationToken);

        return new CustomerDirectoryListResultDto(result.Items, result.TotalCount, page, pageSize);
    }

    public async Task<CustomerDirectoryProfileResult> GetProfileAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        string customerKey,
        CancellationToken cancellationToken = default)
    {
        if (!CustomerIdentity.TryParse(customerKey, out var identity))
        {
            return CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.InvalidKey);
        }

        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(callerEmployeeId, callerRoles, cancellationToken);
        var agentScope = ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles);

        // Customer Ticket History: a restricted CS Agent who currently owns an ACTIVE ticket of this VERIFIED customer (CRM id or external
        // pair - never the phone fallback) sees the customer's whole profile and ticket history, read-only. Everyone else, and any other
        // customer, keeps the unchanged own-tickets scope; the grant ends with the agent's last active ticket for the customer.
        if (agentScope is { } agentId
            && identity.Kind != CustomerIdentityKind.Phone
            && await ticketRepository.HasActiveTicketForCustomerAsync(
                agentId, identity.CrmBuyerCustomerId, identity.ExternalSource, identity.ExternalCustomerId, cancellationToken))
        {
            visibleDepartmentIds = null;
            agentScope = null;
        }

        var profile = await repository.GetProfileAsync(identity, visibleDepartmentIds, cancellationToken, agentScope);

        return profile is null
            ? CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound)
            : CustomerDirectoryProfileResult.Success(profile);
    }
}
