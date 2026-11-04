using TigerCS.Application.Modules.Ticketing.Dto;

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
    Guid? OwnerVisibilityEmployeeId = null);

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

    /// <summary>Everything persisted about one identity within the visible departments, or null when no visible ticket carries it.</summary>
    Task<CustomerDirectoryProfileDto?> GetProfileAsync(
        CustomerIdentity identity, IReadOnlyCollection<int>? visibleDepartmentIds, CancellationToken cancellationToken = default,
        Guid? agentScopeEmployeeId = null, Guid? ownerEmployeeId = null);
}
