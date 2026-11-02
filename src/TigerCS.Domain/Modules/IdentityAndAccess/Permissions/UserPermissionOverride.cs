namespace TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

public enum PermissionEffect
{
    /// <summary>Allows the permission even though no role carries it.</summary>
    Grant = 1,

    /// <summary>Refuses the permission even though a role carries it. A denial always wins over a grant and over roles.</summary>
    Deny = 2
}

/// <summary>
/// One employee-specific exception to the role baseline. Absence of a row
/// means "inherit from roles". At most one row exists per (employee,
/// permission); it is changed in place, so the table always describes the
/// current state and the audit trail (<c>AuditEntries</c>) carries the history.
/// </summary>
public class UserPermissionOverride
{
    public long UserPermissionOverrideId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public string PermissionKey { get; private set; } = string.Empty;
    public PermissionEffect Effect { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid? CreatedByEmployeeId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByEmployeeId { get; private set; }

    private UserPermissionOverride() { }

    public UserPermissionOverride(
        Guid employeeId, string permissionKey, PermissionEffect effect, Guid? actorEmployeeId, DateTime nowUtc)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("EmployeeId is required.", nameof(employeeId));
        }

        if (!PermissionCatalog.IsKnown(permissionKey))
        {
            throw new ArgumentException($"Unknown permission '{permissionKey}'.", nameof(permissionKey));
        }

        EmployeeId = employeeId;
        PermissionKey = permissionKey;
        Effect = effect;
        CreatedAtUtc = UpdatedAtUtc = nowUtc;
        CreatedByEmployeeId = UpdatedByEmployeeId = actorEmployeeId;
    }

    public void ChangeEffect(PermissionEffect effect, Guid? actorEmployeeId, DateTime nowUtc)
    {
        Effect = effect;
        UpdatedAtUtc = nowUtc;
        UpdatedByEmployeeId = actorEmployeeId;
    }
}
