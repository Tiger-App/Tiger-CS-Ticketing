namespace TigerCS.Application.Modules.Administration.Dto;

/// <summary>One permission as the Manage Permissions screen shows it for one employee.</summary>
/// <param name="Key">Stable permission key.</param>
/// <param name="Action">View, Create, Edit, ...</param>
/// <param name="Name">Short display name.</param>
/// <param name="Description">One-line explanation.</param>
/// <param name="IsAdministratorOnly">Reserved permission: can be denied, never granted outside the System Administrator role.</param>
/// <param name="FromRole">Whether the employee's roles carry it.</param>
/// <param name="IsEffective">Whether the employee ends up holding it.</param>
/// <param name="Override">"Inherit" (no row), "Grant" or "Deny".</param>
/// <param name="Source">"Role", "Granted", "Denied" or "None" - why <paramref name="IsEffective"/> is what it is.</param>
/// <param name="CanGrant">False when a grant would be refused (administrator-only permission, or the employee is read-only / an integration account).</param>
public sealed record PermissionStateDto(
    string Key,
    string Action,
    string Name,
    string Description,
    bool IsAdministratorOnly,
    bool FromRole,
    string Override,
    bool IsEffective,
    string Source,
    bool CanGrant);

public sealed record PermissionModuleDto(string Module, IReadOnlyList<PermissionStateDto> Permissions);

public sealed record PermissionChangeDto(
    DateTime OccurredAtUtc, string? ChangedBy, string PermissionKey, string? Before, string? After);

public sealed record UserPermissionsDto(
    Guid EmployeeId,
    string DisplayName,
    string UserName,
    bool IsActive,
    IReadOnlyList<string> Roles,
    bool IsReadOnlyCaller,
    bool IsServiceIdentity,
    bool IsSelf,
    IReadOnlyList<PermissionModuleDto> Modules,
    IReadOnlyList<PermissionChangeDto> RecentChanges);

/// <summary>
/// The complete desired override set for the employee: permission key to
/// "Grant" or "Deny". A permission that is absent inherits from the roles.
/// </summary>
public sealed record SetUserPermissionsRequestDto(IReadOnlyDictionary<string, string> Overrides, string? Reason = null);
