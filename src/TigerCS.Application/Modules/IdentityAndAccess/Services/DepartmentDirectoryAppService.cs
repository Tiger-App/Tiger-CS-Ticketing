using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>
/// <c>GET /api/departments</c>: the Department directory a Department dropdown
/// reads from, so no UI ever asks anyone to type a raw <c>DepartmentId</c> —
/// the same rationale as <c>CategoryCatalogAppService</c> for Categories.
/// </summary>
public sealed class DepartmentDirectoryAppService(IDepartmentRepository departmentRepository)
{
    public async Task<IReadOnlyCollection<DepartmentDto>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var departments = await departmentRepository.ListAsync(activeOnly, cancellationToken);
        return departments
            .Select(d => new DepartmentDto(d.DepartmentId, d.Name, d.Code))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }
}
