namespace TigerCS.Domain.Modules.EmployeeReviews;

/// <summary>
/// A registered review tablet, permanently assigned to one Customer Service employee until an administrator changes it.
/// <para>
/// Two different secrets exist, and they are never the same value. The <b>activation code</b> is short (8 characters), typed by a person,
/// valid for 24 hours and for one use; only a salted PBKDF2 hash of it is stored. The <b>device token</b> is the tablet's permanent
/// credential, minted by the server at the moment the code is redeemed and only ever handed to that tablet; only its SHA-256 hash is
/// stored, and a revoked tablet has no hash at all, so no token can ever match it.
/// </para>
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

    /// <summary>Salted PBKDF2 hash of the pending activation code; <c>null</c> when none is pending (never issued, redeemed, or revoked).</summary>
    public byte[]? ActivationCodeHash { get; private set; }

    /// <summary>The salt for <see cref="ActivationCodeHash"/>; present exactly when the hash is.</summary>
    public byte[]? ActivationCodeSalt { get; private set; }

    /// <summary>When the pending activation code stops working (UTC); present exactly when the hash is.</summary>
    public DateTime? ActivationCodeExpiresAtUtc { get; private set; }

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

    /// <summary>A new tablet that has no credential yet: it is activated by redeeming <paramref name="activationCodeHash"/> before <paramref name="activationCodeExpiresAtUtc"/>.</summary>
    public EmployeeReviewDevice(
        string deviceName, Guid employeeId, byte[] activationCodeHash, byte[] activationCodeSalt, DateTime activationCodeExpiresAtUtc, DateTime nowUtc)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("An employee is required.", nameof(employeeId));
        }

        ArgumentNullException.ThrowIfNull(activationCodeHash);
        ArgumentNullException.ThrowIfNull(activationCodeSalt);

        Id = Guid.NewGuid();
        DeviceName = NormalizeName(deviceName);
        EmployeeId = employeeId;
        ActivationCodeHash = activationCodeHash;
        ActivationCodeSalt = activationCodeSalt;
        ActivationCodeExpiresAtUtc = activationCodeExpiresAtUtc;
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

    /// <summary>
    /// Waits for a new activation code. The tablet's current device token (if any) stops working at once, so replacing the credential of a tablet
    /// that is already activated must be asked for explicitly with <paramref name="replaceActiveCredential"/>; otherwise this throws and changes
    /// nothing. A tablet with no credential needs no such flag. Also brings a switched-off or revoked tablet back to "on, awaiting activation".
    /// </summary>
    public void IssueActivationCode(byte[] codeHash, byte[] codeSalt, DateTime expiresAtUtc, bool replaceActiveCredential, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(codeHash);
        ArgumentNullException.ThrowIfNull(codeSalt);
        if (TokenHash is not null && !replaceActiveCredential)
        {
            throw new InvalidOperationException("The tablet is already activated; replacing its credential must be confirmed explicitly.");
        }

        TokenHash = null;
        ActivationCodeHash = codeHash;
        ActivationCodeSalt = codeSalt;
        ActivationCodeExpiresAtUtc = expiresAtUtc;
        IsActive = true;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Redeems the pending activation code: stores the freshly minted device token's hash and erases the code, its salt and its expiry. Returns
    /// <c>false</c> — changing nothing — unless this very code is still pending, unexpired, and the tablet is switched on. The code's hash is a
    /// concurrency token, so when two requests redeem the same code at once the database lets exactly one save succeed.
    /// </summary>
    public bool TryRedeemActivationCode(byte[] expectedCodeHash, byte[] newTokenHash, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(expectedCodeHash);
        ArgumentNullException.ThrowIfNull(newTokenHash);
        if (!IsActive
            || !HasRedeemableActivationCode(nowUtc)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ActivationCodeHash, expectedCodeHash))
        {
            return false;
        }

        TokenHash = newTokenHash;
        ActivationCodeHash = null;
        ActivationCodeSalt = null;
        ActivationCodeExpiresAtUtc = null;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>True when a code is waiting to be redeemed and has not expired at <paramref name="nowUtc"/>.</summary>
    public bool HasRedeemableActivationCode(DateTime nowUtc) =>
        ActivationCodeHash is not null && ActivationCodeSalt is not null && ActivationCodeExpiresAtUtc is { } expires && nowUtc < expires;

    /// <summary>Removes the credential and any pending activation code, and switches the tablet off.</summary>
    public void Revoke(DateTime nowUtc)
    {
        TokenHash = null;
        ActivationCodeHash = null;
        ActivationCodeSalt = null;
        ActivationCodeExpiresAtUtc = null;
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
