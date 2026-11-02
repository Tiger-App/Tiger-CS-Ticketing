using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

/// <summary>One audit-trail line of a permission change, resolved for display.</summary>
public sealed record PermissionChangeLogEntry(
    DateTime OccurredAtUtc, Guid? ActorEmployeeId, string? ActorDisplayName, string PermissionKey, string? Before, string? After);

/// <summary>Application-layer port over <c>UserPermissionOverrides</c>; implemented in Infrastructure with EF Core.</summary>
public interface IUserPermissionOverrideRepository
{
    /// <summary>Tracked rows of one employee (the save path edits them in place).</summary>
    Task<IReadOnlyList<UserPermissionOverride>> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Untracked read of one employee's overrides as key to effect - the authorization hot path.</summary>
    Task<IReadOnlyDictionary<string, PermissionEffect>> GetEffectsAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task AddAsync(UserPermissionOverride permissionOverride, CancellationToken cancellationToken = default);

    void Remove(UserPermissionOverride permissionOverride);

    /// <summary>
    /// Active employees holding <paramref name="roleName"/> who are NOT
    /// denied <paramref name="permissionKey"/>, other than
    /// <paramref name="excludedEmployeeId"/> - the last-administrator guard.
    /// </summary>
    Task<int> CountActiveInRoleWithoutDenialAsync(
        string roleName, string permissionKey, Guid excludedEmployeeId, CancellationToken cancellationToken = default);

    /// <summary>Most recent permission changes recorded for an employee, newest first, read from the audit trail.</summary>
    Task<IReadOnlyList<PermissionChangeLogEntry>> GetRecentChangesAsync(
        Guid employeeId, int take, CancellationToken cancellationToken = default);
}
