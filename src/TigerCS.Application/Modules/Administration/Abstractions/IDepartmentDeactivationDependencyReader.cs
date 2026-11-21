namespace TigerCS.Application.Modules.Administration.Abstractions;

/// <summary>
/// Reads what still depends on a department being active, so deactivating it cannot strand live work. Read-only.
/// Closed tickets and every historical record (assignments, approvals already decided, audit, workflow events) are NOT dependencies:
/// they keep naming the department and are never touched.
/// </summary>
public interface IDepartmentDeactivationDependencyReader
{
    /// <summary>One human-readable line per unresolved dependency; empty when the department can be deactivated safely.</summary>
    Task<IReadOnlyList<string>> GetBlockersAsync(int departmentId, CancellationToken cancellationToken = default);
}
