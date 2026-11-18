namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

/// <summary>The Identity account behind an employee — the fields ASP.NET Core Identity owns (never the password hash).</summary>
public sealed record UserAccountInfo(Guid EmployeeId, string UserName, string? Email, bool IsLockedOut);

/// <summary>Identity's password options, as configured.</summary>
public sealed record PasswordPolicyInfo(
    int RequiredLength, bool RequireDigit, bool RequireLowercase, bool RequireUppercase, bool RequireNonAlphanumeric, int RequiredUniqueChars);

/// <summary>Outcome of an Identity operation, in Identity's own error wording (password policy, duplicate user name, …).</summary>
public sealed record UserAccountResult(bool Succeeded, IReadOnlyList<string> Errors, Guid EmployeeId = default)
{
    public static UserAccountResult Success(Guid employeeId) => new(true, [], employeeId);

    public static UserAccountResult Failure(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>How a password reset or change landed inside Identity.</summary>
public enum PasswordChangeOutcome
{
    Success,

    /// <summary>No Identity account exists for the employee id.</summary>
    UserNotFound,

    /// <summary>Self-service change only: the current password did not verify. Nothing else is revealed.</summary>
    CurrentPasswordIncorrect,

    /// <summary>The new password failed Identity's registered password validators; <c>Errors</c> carries their descriptions.</summary>
    PolicyViolation
}

/// <summary>
/// Outcome of a password reset/change. Carries Identity's validator wording
/// for a policy failure and nothing else — never the password, never a
/// reset token.
/// </summary>
public sealed record PasswordChangeResult(PasswordChangeOutcome Outcome, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Outcome == PasswordChangeOutcome.Success;

    public static PasswordChangeResult Success() => new(PasswordChangeOutcome.Success, []);

    public static PasswordChangeResult UserNotFound() => new(PasswordChangeOutcome.UserNotFound, []);

    public static PasswordChangeResult CurrentPasswordIncorrect() => new(PasswordChangeOutcome.CurrentPasswordIncorrect, []);

    public static PasswordChangeResult PolicyViolation(IReadOnlyList<string> errors) => new(PasswordChangeOutcome.PolicyViolation, errors);
}

/// <summary>
/// The Administration phase's boundary to ASP.NET Core Identity. Every
/// credential/role operation goes through Identity's own managers behind
/// this interface — the application layer never sees or manipulates a
/// password hash, and no custom password handling exists.
/// </summary>
public interface IUserAccountManager
{
    Task<UserAccountInfo?> GetAccountAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, UserAccountInfo>> GetAccountsAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default);

    /// <summary>Creates the Identity user with Identity's password validators; the initial password is set exactly as the development seed sets one.</summary>
    Task<UserAccountResult> CreateAccountAsync(string userName, string? email, string initialPassword, CancellationToken cancellationToken = default);

    Task<UserAccountResult> UpdateEmailAsync(Guid employeeId, string? email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the login name through <c>UserManager.SetUserNameAsync</c> (which maintains the normalized name, enforces
    /// case-insensitive uniqueness and rotates the security stamp, so every existing session stops working). The account's id is unchanged.
    /// </summary>
    Task<UserAccountResult> SetUserNameAsync(Guid employeeId, string userName, CancellationToken cancellationToken = default);

    /// <summary>Replaces the user's role set with exactly the given fixed roles (adds the missing, removes the extra).</summary>
    Task<UserAccountResult> SetRolesAsync(Guid employeeId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default);

    /// <summary>Employee ids whose user name or email contains the search text (case-insensitive) — for the administration user search.</summary>
    Task<IReadOnlyCollection<Guid>> SearchAccountIdsAsync(string search, CancellationToken cancellationToken = default);

    /// <summary>
    /// Administrative reset: replaces the password without knowing the old one
    /// (Identity's validators decide whether the new one is acceptable), then
    /// rotates the security stamp, clears the failed-attempt count and lifts
    /// any lockout, so a locked-out user can sign in with the new password at
    /// once — and every token issued before the reset stops working.
    /// </summary>
    Task<PasswordChangeResult> ResetPasswordAsync(Guid employeeId, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>The password options Identity is configured with (including any "Identity:Password:*" overrides).</summary>
    PasswordPolicyInfo GetPasswordPolicy();

    /// <summary>
    /// Administrative unlock: clears the lockout end date and the failed-attempt
    /// count through Identity. Touches nothing else — not the password, roles,
    /// security stamp or active status. Returns false when no account exists.
    /// </summary>
    Task<bool> UnlockAccountAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Self-service change: Identity verifies the current password and runs its
    /// validators on the new one; on success the security stamp is rotated so
    /// every previously issued token stops working.
    /// </summary>
    Task<PasswordChangeResult> ChangePasswordAsync(Guid employeeId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// The account's current Identity security stamp — the value a JWT's
    /// <c>sst</c> claim is compared against on every request — or null when
    /// no account exists. Rotated by Identity on every credential change.
    /// </summary>
    Task<string?> GetSecurityStampAsync(Guid employeeId, CancellationToken cancellationToken = default);
}
