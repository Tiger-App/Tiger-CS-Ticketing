using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Application.Modules.Administration.Services;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;

public sealed class UserPermissionOverrideRepository(TigerCsDbContext dbContext) : IUserPermissionOverrideRepository
{
    public async Task<IReadOnlyList<UserPermissionOverride>> GetForEmployeeAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.UserPermissionOverrides
            .Where(o => o.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, PermissionEffect>> GetEffectsAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.UserPermissionOverrides.AsNoTracking()
            .Where(o => o.EmployeeId == employeeId)
            .Select(o => new { o.PermissionKey, o.Effect })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.PermissionKey, r => r.Effect, StringComparer.Ordinal);
    }

    public async Task AddAsync(UserPermissionOverride permissionOverride, CancellationToken cancellationToken = default) =>
        await dbContext.UserPermissionOverrides.AddAsync(permissionOverride, cancellationToken);

    public void Remove(UserPermissionOverride permissionOverride) =>
        dbContext.UserPermissionOverrides.Remove(permissionOverride);

    public async Task<int> CountActiveInRoleWithoutDenialAsync(
        string roleName, string permissionKey, Guid excludedEmployeeId, CancellationToken cancellationToken = default)
    {
        var role = await dbContext.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == roleName, cancellationToken);
        if (role is null)
        {
            return 0;
        }

        var inRole = dbContext.UserRoles.Where(ur => ur.RoleId == role.Id).Select(ur => ur.UserId);
        var denied = dbContext.UserPermissionOverrides
            .Where(o => o.PermissionKey == permissionKey && o.Effect == PermissionEffect.Deny)
            .Select(o => o.EmployeeId);

        return await dbContext.Employees.AsNoTracking()
            .Where(e => e.DeactivatedAtUtc == null
                && e.EmployeeId != excludedEmployeeId
                && inRole.Contains(e.EmployeeId)
                && !denied.Contains(e.EmployeeId))
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionChangeLogEntry>> GetRecentChangesAsync(
        Guid employeeId, int take, CancellationToken cancellationToken = default)
    {
        var entityId = employeeId.ToString();
        var rows = await dbContext.AuditEntries.AsNoTracking()
            .Where(a => a.Action == AdminUserPermissionAppService.AuditAction && a.EntityId == entityId)
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.AuditEntryId)
            .Take(take)
            .Select(a => new { a.OccurredAtUtc, a.ActorEmployeeId, a.BeforeValue, a.AfterValue })
            .ToListAsync(cancellationToken);

        var actorIds = rows.Where(r => r.ActorEmployeeId != null).Select(r => r.ActorEmployeeId!.Value).Distinct().ToList();
        var names = await dbContext.Employees.AsNoTracking()
            .Where(e => actorIds.Contains(e.EmployeeId))
            .ToDictionaryAsync(e => e.EmployeeId, e => e.DisplayName, cancellationToken);

        return rows.Select(r => new PermissionChangeLogEntry(
            DateTime.SpecifyKind(r.OccurredAtUtc, DateTimeKind.Utc),
            r.ActorEmployeeId,
            r.ActorEmployeeId is { } id && names.TryGetValue(id, out var name) ? name : null,
            Field(r.AfterValue ?? r.BeforeValue, "Permission") ?? string.Empty,
            r.BeforeValue, r.AfterValue)).ToList();
    }

    private static string? Field(string? value, string name)
    {
        var prefix = name + "=";
        return value?.Split(';').FirstOrDefault(p => p.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }
}
