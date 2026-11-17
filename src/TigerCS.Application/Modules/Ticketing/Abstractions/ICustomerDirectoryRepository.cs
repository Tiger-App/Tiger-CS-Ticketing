using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>The directory list query — the caller's visibility scope plus the page's own filters (see <see cref="CustomerDirectoryListRequestDto"/>).</summary>
public sealed record CustomerDirectoryQuery(
    IReadOnlyCollection<int>? VisibleDepartmentIds,
    string? Search,
    string? VerificationSource,
    int? DepartmentId,
    bool OpenOnly,
    int Page,
    int PageSize,
    Guid? AgentScopeEmployeeId = null,
    Guid? OwnerVisibilityEmployeeId = null,
    CustomerLocation? Location = null,
    CustomerDirectoryTicketFilter? TicketFilter = null);

/// <summary>
/// Ticket-level narrowing that SELECTS customers (those with at least one visible ticket matching every set field at once): the
/// Dashboard's own filters, so the Customers by Location card and the list its counts open are the same query.
/// Dates are a half-open UTC range: <c>CreatedFromUtc</c> inclusive, <c>CreatedToUtc</c> exclusive.
/// </summary>
public sealed record CustomerDirectoryTicketFilter(
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null,
    int? DepartmentId = null,
    Guid? OwnerEmployeeId = null,
    byte? ChannelId = null,
    int? RequestTypeId = null,
    TicketStatus? TicketStatus = null,
    byte? PriorityId = null);

/// <summary>Distinct customers per location. Mutually exclusive: Within + Outside + Unknown is every customer selected.</summary>
public sealed record CustomerLocationCounts(int WithinUae, int OutsideUae, int Unknown)
{
    public int Total => WithinUae + OutsideUae + Unknown;
}

public sealed record CustomerDirectoryPage(IReadOnlyList<CustomerDirectoryRowDto> Items, int TotalCount);

/// <summary>
/// Read-only access to the customers TigerCS already knows — derived from
/// persisted tickets, intakes and interactions, grouped by
/// <see cref="CustomerIdentity"/>. A query repository like the dashboard's:
/// it produces the read models directly, never a live CRM call.
/// </summary>
public interface ICustomerDirectoryRepository
{
    Task<CustomerDirectoryPage> ListAsync(CustomerDirectoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct customers by phone-derived location (<see cref="CustomerPhoneLocation"/>) over exactly the customers
    /// <see cref="ListAsync"/> would list for the same query (its <c>Location</c> is ignored) — aggregated in the database, never a CRM call.
    /// </summary>
    Task<CustomerLocationCounts> CountByLocationAsync(CustomerDirectoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Everything persisted about one identity within the visible departments, or null when no visible ticket carries it.</summary>
    Task<CustomerDirectoryProfileDto?> GetProfileAsync(
        CustomerIdentity identity, IReadOnlyCollection<int>? visibleDepartmentIds, CancellationToken cancellationToken = default,
        Guid? agentScopeEmployeeId = null, Guid? ownerEmployeeId = null);
}
