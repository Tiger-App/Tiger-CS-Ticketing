using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

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
    TicketQueryAppService ticketQueryAppService)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public async Task<CustomerDirectoryListResultDto> ListAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        CustomerDirectoryListRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > MaxPageSize ? DefaultPageSize : request.PageSize;

        var query = await BuildQueryAsync(callerEmployeeId, callerRoles, request, page, pageSize, cancellationToken);
        var result = await repository.ListAsync(query, cancellationToken);

        var items = result.Items;
        if (items.Count > 0 && CustomerLocationKeys.Parse(request.Location) == CustomerLocation.Unknown)
        {
            // Unknown list: say, per customer, which number(s) as stored and why (the same customers, same order and page, one extra query).
            var diagnoses = (await repository.DiagnoseUnknownAsync(query, paged: true, cancellationToken)).ToDictionary(d => d.CustomerKey);
            items = items
                .Select(row => diagnoses.TryGetValue(row.CustomerKey, out var d)
                    ? row with { LocationReason = d.Reason.ToString(), LocationReasonText = CustomerLocationKeys.ReasonText(d.Reason), LocationPhone = d.OriginalPhone, LocationOtherPhone = d.OtherPhone }
                    : row)
                .ToList();
        }

        return new CustomerDirectoryListResultDto(items, result.TotalCount, page, pageSize);
    }

    /// <summary>The Unknown customers of the same query as the list and the Dashboard card, counted by why they are Unknown. Read-only.</summary>
    public async Task<CustomerLocationBreakdownDto> UnknownBreakdownAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        CustomerDirectoryListRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var diagnoses = await repository.DiagnoseUnknownAsync(
            await BuildQueryAsync(callerEmployeeId, callerRoles, request, 1, DefaultPageSize, cancellationToken), paged: false, cancellationToken);
        var reasons = diagnoses
            .GroupBy(d => d.Reason)
            .OrderByDescending(g => g.Count())
            .Select(g => new CustomerLocationReasonCountDto(g.Key.ToString(), CustomerLocationKeys.ReasonText(g.Key), g.Count()))
            .ToList();
        return new CustomerLocationBreakdownDto(diagnoses.Count, reasons, diagnoses.Count >= ICustomerDirectoryRepository.MaxDiagnosed);
    }

    /// <summary>
    /// Distinct customers by phone-derived location (Within UAE / Outside UAE / Unknown) for exactly the customers
    /// <see cref="ListAsync"/> returns for the same <paramref name="request"/> — same visibility scope, same filters, same query — so the
    /// Dashboard's Customers by Location card can never disagree with the list its counts open. A <c>Location</c> on the request is ignored.
    /// </summary>
    public async Task<CustomerLocationCounts> CountByLocationAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        CustomerDirectoryListRequestDto request,
        CancellationToken cancellationToken = default) =>
        await repository.CountByLocationAsync(
            await BuildQueryAsync(callerEmployeeId, callerRoles, request, 1, DefaultPageSize, cancellationToken), cancellationToken);

    private async Task<CustomerDirectoryQuery> BuildQueryAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CustomerDirectoryListRequestDto request, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(callerEmployeeId, callerRoles, cancellationToken);

        // The Dashboard's filters travel as a ticket filter, so a department outside the caller's scope still yields nobody (the scope
        // is applied first and a filter can only narrow it). Only built when one is set: the plain list is unchanged.
        var ticketStatus = request.TicketStatus is { } statusText && Enum.TryParse<TicketStatus>(statusText, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : (TicketStatus?)null;
        var hasTicketFilter = request.DateFrom is not null || request.DateTo is not null || request.OwnerEmployeeId is not null
            || request.ChannelId is not null || request.RequestTypeId is not null || ticketStatus is not null || request.PriorityId is not null;
        var ticketFilter = hasTicketFilter
            ? new CustomerDirectoryTicketFilter(
                request.DateFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                request.DateTo?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                request.DepartmentId,
                request.OwnerEmployeeId,
                request.ChannelId,
                request.RequestTypeId,
                ticketStatus,
                request.PriorityId)
            : null;

        return new CustomerDirectoryQuery(
            visibleDepartmentIds,
            string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
            string.IsNullOrWhiteSpace(request.VerificationSource) ? null : request.VerificationSource.Trim(),
            // With a ticket filter the department is part of it (one ticket must match everything at once).
            hasTicketFilter ? null : request.DepartmentId,
            request.OpenOnly,
            page,
            pageSize,
            ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles),
            callerEmployeeId,
            CustomerLocationKeys.Parse(request.Location),
            ticketFilter);
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
        var profile = await repository.GetProfileAsync(
            identity, visibleDepartmentIds, cancellationToken, ticketQueryAppService.ResolveAgentScope(callerEmployeeId, callerRoles),
            callerEmployeeId);

        return profile is null
            ? CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound)
            : CustomerDirectoryProfileResult.Success(profile);
    }
}
