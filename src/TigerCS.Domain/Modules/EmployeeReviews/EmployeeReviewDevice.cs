namespace TigerCS.Domain.Modules.EmployeeReviews;

/// <summary>
/// A registered review tablet, permanently assigned to one Customer Service employee until an administrator changes it.
/// The tablet's credential is a random secret shown to the administrator once; only its SHA-256 hash is stored here, and a
/// revoked tablet has no hash at all, so no token can ever match it.
/// </summary>
public class EmployeeReviewDevice
{
    public const int NameMaxLength = 100;

    public Guid Id { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>The employee the tablet currently collects reviews for (FK to the existing <c>Employees</c> table).</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>SHA-256 of the device token; <c>null</c> once revoked.</summary>
    public byte[]? TokenHash { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? LastSeenAtUtc { get; private set; }

    private EmployeeReviewDevice()
    {
    }

    public EmployeeReviewDevice(string deviceName, Guid employeeId, byte[] tokenHash, DateTime nowUtc)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("An employee is required.", nameof(employeeId));
        }

        ArgumentNullException.ThrowIfNull(tokenHash);

        Id = Guid.NewGuid();
        DeviceName = NormalizeName(deviceName);
        EmployeeId = employeeId;
        TokenHash = tokenHash;
        IsActive = true;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>True when the tablet may collect reviews right now: switched on and holding a credential.</summary>
    public bool CanCollectReviews => IsActive && TokenHash is not null;

    public void Rename(string deviceName, DateTime nowUtc)
    {
        DeviceName = NormalizeName(deviceName);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Points the tablet at another employee. Reviews already stored keep the employee they were given for.</summary>
    public void Reassign(Guid employeeId, DateTime nowUtc)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("An employee is required.", nameof(employeeId));
        }

        EmployeeId = employeeId;
        UpdatedAtUtc = nowUtc;
    }

    public void SetActive(bool isActive, DateTime nowUtc)
    {
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Replaces the credential: the previous token stops working immediately. Also re-enables a revoked tablet's ability to be activated again.</summary>
    public void ReplaceToken(byte[] tokenHash, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        TokenHash = tokenHash;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Removes the credential and switches the tablet off.</summary>
    public void Revoke(DateTime nowUtc)
    {
        TokenHash = null;
        IsActive = false;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkSeen(DateTime nowUtc) => LastSeenAtUtc = nowUtc;

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("A tablet name is required.", nameof(name));
        }

        return trimmed.Length > NameMaxLength ? throw new ArgumentException($"A tablet name is at most {NameMaxLength} characters.", nameof(name)) : trimmed;
    }
}
