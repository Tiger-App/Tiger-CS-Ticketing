using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// Per-employee permission management (Administration → Users → Manage
/// Permissions). Permissions are the role baseline
/// (<see cref="RolePermissionDefaults"/>) plus employee-specific overrides;
/// this service reads and rewrites only the overrides, and the existing
/// authorization pipeline (<c>UserPermissionAuthorizationHandler</c>) enforces
/// them on every endpoint. Roles are still changed through
/// <see cref="AdminUserAppService.SetRolesAsync"/> - there is one place for each.
///
/// <para>Safeguards, all enforced here and not in the UI:</para>
/// <list type="bullet">
///   <item><description>nobody edits their own overrides (no self-escalation, no self-lock-out);</description></item>
///   <item><description>a permission can only be granted by someone who effectively holds it;</description></item>
///   <item><description>administrator-only permissions (<see cref="PermissionDefinition.IsReserved"/>) are never granted outside the roles that carry them;</description></item>
///   <item><description>an administrator-only permission cannot be denied to the last active System Administrator who still holds it;</description></item>
///   <item><description>read-only roles (Chairman/CEO, Reporting User) are never granted a mutating permission;</description></item>
///   <item><description>configured integration (Genesys) accounts are never given overrides;</description></item>
///   <item><description>every change is written to the audit trail in the same transaction.</description></item>
/// </list>
/// </summary>
public sealed class AdminUserPermissionAppService(
    IEmployeeRepository employeeRepository,
    IUserAccountManager accountManager,
    IUserRoleReader roleReader,
    IUserPermissionOverrideRepository overrideRepository,
    IServiceIdentityRegistry serviceIdentities,
    IIdentityUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public const string AuditAction = "AdminSetUserPermission";
    public const string AuditEntityType = "UserPermission";

    private const string Inherit = "Inherit";

    public async Task<AdminResult<UserPermissionsDto>> GetAsync(
        Guid actorEmployeeId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<UserPermissionsDto>.NotFound();
        }

        return AdminResult<UserPermissionsDto>.Success(await BuildAsync(actorEmployeeId, employee, cancellationToken));
    }

    public async Task<AdminResult<UserPermissionsDto>> SetAsync(
        Guid actorEmployeeId, Guid employeeId, SetUserPermissionsRequestDto request, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<UserPermissionsDto>.NotFound();
        }

        if (actorEmployeeId == employeeId)
        {
            return AdminResult<UserPermissionsDto>.Forbidden("You cannot change your own permissions. Ask another System Administrator.");
        }

        if (serviceIdentities.IsServiceIdentity(employeeId))
        {
            return AdminResult<UserPermissionsDto>.Conflict(
                "This is an integration (service) account. Its access is fixed to the integration surface and cannot be customised.");
        }

        // 1. Parse and validate the requested state.
        var errors = new List<string>();
        var desired = new Dictionary<string, PermissionEffect>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Overrides ?? new Dictionary<string, string>())
        {
            if (!PermissionCatalog.IsKnown(key))
            {
                errors.Add($"Unknown permission '{key}'.");
            }
            else if (string.Equals(value, "Grant", StringComparison.OrdinalIgnoreCase))
            {
                desired[key] = PermissionEffect.Grant;
            }
            else if (string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase))
            {
                desired[key] = PermissionEffect.Deny;
            }
            else if (!string.Equals(value, Inherit, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{value}' is not a valid setting for {key}. Use Grant, Deny or Inherit.");
            }
        }

        if (errors.Count > 0)
        {
            return AdminResult<UserPermissionsDto>.Invalid(errors);
        }

        var rows = await overrideRepository.GetForEmployeeAsync(employeeId, cancellationToken);
        var current = rows.ToDictionary(r => r.PermissionKey, r => r.Effect, StringComparer.Ordinal);
        var changedKeys = PermissionCatalog.All.Select(p => p.Key)
            .Where(k => current.GetValueOrDefault(k) != desired.GetValueOrDefault(k)
                || current.ContainsKey(k) != desired.ContainsKey(k))
            .ToList();

        var targetRoles = await roleReader.GetRolesAsync(employeeId, cancellationToken);
        var targetBaseline = RolePermissionDefaults.ForRoles(targetRoles);

        // 2. The caller's own authority.
        var actorRoles = await roleReader.GetRolesAsync(actorEmployeeId, cancellationToken);
        var actorEffective = EffectivePermissions.Compute(
            actorRoles, await overrideRepository.GetEffectsAsync(actorEmployeeId, cancellationToken));
        if (!actorEffective.Contains(Permissions.AdminPermissionsManage))
        {
            return AdminResult<UserPermissionsDto>.Forbidden("You do not have the Manage Permissions permission.");
        }

        // 3. Per-change rules.
        var readOnlyCaller = Roles.IsReadOnlyCaller(targetRoles.ToList());
        foreach (var key in changedKeys.Where(desired.ContainsKey))
        {
            var definition = PermissionCatalog.Find(key)!;
            if (desired[key] == PermissionEffect.Grant)
            {
                if (definition.IsReserved && !targetBaseline.Contains(key))
                {
                    return AdminResult<UserPermissionsDto>.Forbidden(
                        $"'{definition.Name}' is an administrator-only permission and cannot be granted. Assign the System Administrator role instead.");
                }

                if (!actorEffective.Contains(key))
                {
                    return AdminResult<UserPermissionsDto>.Forbidden(
                        $"You cannot grant '{definition.Name}' because you do not hold it yourself.");
                }

                if (readOnlyCaller && definition.IsMutating)
                {
                    errors.Add($"'{definition.Name}' cannot be granted: this employee holds only read-only roles (Chairman/CEO, Reporting User). Assign a working role first.");
                }
            }
            else if (definition.IsReserved
                && employee.IsActive
                && targetBaseline.Contains(key)
                && await overrideRepository.CountActiveInRoleWithoutDenialAsync(
                    Roles.SystemAdministrator, key, employeeId, cancellationToken) < 1)
            {
                return AdminResult<UserPermissionsDto>.Conflict(
                    $"Cannot deny '{definition.Name}': {employee.DisplayName} is the last active System Administrator who holds it.");
            }
        }

        if (errors.Count > 0)
        {
            return AdminResult<UserPermissionsDto>.Invalid(errors);
        }

        // 4. Apply and audit, in one transaction.
        if (changedKeys.Count > 0)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var correlationId = Guid.NewGuid();
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            var rowByKey = rows.ToDictionary(r => r.PermissionKey, StringComparer.Ordinal);
            var currentEffects = current;

            foreach (var key in changedKeys)
            {
                var hasAfter = desired.TryGetValue(key, out var afterEffect);
                if (rowByKey.TryGetValue(key, out var row))
                {
                    if (hasAfter)
                    {
                        row.ChangeEffect(afterEffect, actorEmployeeId, now);
                    }
                    else
                    {
                        overrideRepository.Remove(row);
                    }
                }
                else if (hasAfter)
                {
                    await overrideRepository.AddAsync(
                        new UserPermissionOverride(employeeId, key, afterEffect, actorEmployeeId, now), cancellationToken);
                }

                var before = currentEffects.TryGetValue(key, out var beforeEffect) ? beforeEffect.ToString() : Inherit;
                var after = hasAfter ? afterEffect.ToString() : Inherit;
                await auditWriter.WriteAsync(
                    actorEmployeeId, AuditAction, AuditEntityType, employeeId.ToString(),
                    Describe(key, before, currentEffects, targetBaseline),
                    Describe(key, after, desired, targetBaseline) + (reason is null ? string.Empty : $";Reason={reason}"),
                    correlationId, cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return AdminResult<UserPermissionsDto>.Success(await BuildAsync(actorEmployeeId, employee, cancellationToken));
    }

    /// <summary>The audit value format: <c>Permission=&lt;key&gt;;Override=&lt;Inherit|Grant|Deny&gt;;Effective=&lt;true|false&gt;</c>.</summary>
    internal static string Describe(
        string key, string overrideText, IReadOnlyDictionary<string, PermissionEffect> overrides, IReadOnlySet<string> baseline)
    {
        var effective = EffectivePermissions.IsEffective(
            EffectivePermissions.Resolve(key, baseline, WithOverride(key, overrideText, overrides)));
        return $"Permission={key};Override={overrideText};Effective={effective.ToString().ToLowerInvariant()}";
    }

    private static IReadOnlyDictionary<string, PermissionEffect> WithOverride(
        string key, string overrideText, IReadOnlyDictionary<string, PermissionEffect> overrides)
    {
        var copy = new Dictionary<string, PermissionEffect>(overrides, StringComparer.Ordinal);
        if (Enum.TryParse<PermissionEffect>(overrideText, out var effect))
        {
            copy[key] = effect;
        }
        else
        {
            copy.Remove(key);
        }

        return copy;
    }

    private async Task<UserPermissionsDto> BuildAsync(Guid actorEmployeeId, Employee employee, CancellationToken cancellationToken)
    {
        var roles = (await roleReader.GetRolesAsync(employee.EmployeeId, cancellationToken))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        var effects = await overrideRepository.GetEffectsAsync(employee.EmployeeId, cancellationToken);
        var account = await accountManager.GetAccountAsync(employee.EmployeeId, cancellationToken);
        var baseline = RolePermissionDefaults.ForRoles(roles);
        var readOnlyCaller = Roles.IsReadOnlyCaller(roles);
        var isService = serviceIdentities.IsServiceIdentity(employee.EmployeeId);

        var modules = new List<PermissionModuleDto>();
        foreach (var module in PermissionModules.Ordered)
        {
            var states = new List<PermissionStateDto>();
            foreach (var permission in PermissionCatalog.All.Where(p => p.Module == module))
            {
                var source = EffectivePermissions.Resolve(permission.Key, baseline, effects);
                var fromRole = baseline.Contains(permission.Key);
                var canGrant = !isService
                    && !(permission.IsReserved && !fromRole)
                    && !(readOnlyCaller && permission.IsMutating);
                states.Add(new PermissionStateDto(
                    permission.Key, permission.Action.ToString(), permission.Name, permission.Description,
                    permission.IsReserved, fromRole,
                    effects.TryGetValue(permission.Key, out var effect) ? effect.ToString() : Inherit,
                    EffectivePermissions.IsEffective(source), source.ToString(), canGrant));
            }

            if (states.Count > 0)
            {
                modules.Add(new PermissionModuleDto(module, states));
            }
        }

        var changes = (await overrideRepository.GetRecentChangesAsync(employee.EmployeeId, 20, cancellationToken))
            .Select(c => new PermissionChangeDto(c.OccurredAtUtc, c.ActorDisplayName, c.PermissionKey, c.Before, c.After))
            .ToList();

        return new UserPermissionsDto(
            employee.EmployeeId, employee.DisplayName, account?.UserName ?? string.Empty, employee.IsActive,
            roles, readOnlyCaller, isService, actorEmployeeId == employee.EmployeeId, modules, changes);
    }
}
