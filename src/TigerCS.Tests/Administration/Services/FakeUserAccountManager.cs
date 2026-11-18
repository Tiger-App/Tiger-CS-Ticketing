using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

namespace TigerCS.Tests.Administration.Services;

/// <summary>
/// Stands in for ASP.NET Core Identity in service tests: accounts, roles,
/// passwords with a minimal policy (8+ characters), lockout and the security
/// stamp Identity rotates on every credential change — so the
/// administration and authentication services can be exercised without the
/// Identity store. Also serves as the role reader, exactly as Identity does
/// for the real services.
/// </summary>
public sealed class FakeUserAccountManager : IUserAccountManager, IUserRoleReader
{
    private sealed class Account
    {
        public required Guid Id { get; init; }
        public required string UserName { get; set; }
        public string? Email { get; set; }
        public string Password { get; set; } = "Initial-Pass-1!";
        public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
        public bool IsLockedOut { get; set; }
        public List<string> Roles { get; } = [];
    }

    private readonly Dictionary<Guid, Account> _accounts = [];

    public FakeUserAccountManager Add(Guid employeeId, string userName, params string[] roles)
    {
        var account = new Account { Id = employeeId, UserName = userName };
        account.Roles.AddRange(roles);
        _accounts[employeeId] = account;
        return this;
    }

    /// <summary>Test setup: the password the account currently has (what a self-service change must verify against).</summary>
    public FakeUserAccountManager WithPassword(Guid employeeId, string password)
    {
        _accounts[employeeId].Password = password;
        return this;
    }

    /// <summary>Test setup: puts the account in Identity's locked-out state.</summary>
    public FakeUserAccountManager LockOut(Guid employeeId)
    {
        _accounts[employeeId].IsLockedOut = true;
        return this;
    }

    public string PasswordOf(Guid employeeId) => _accounts[employeeId].Password;

    public string SecurityStampOf(Guid employeeId) => _accounts[employeeId].SecurityStamp;

    public bool IsLockedOut(Guid employeeId) => _accounts[employeeId].IsLockedOut;

    public Task<UserAccountInfo?> GetAccountAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.TryGetValue(employeeId, out var a) ? new UserAccountInfo(a.Id, a.UserName, a.Email, a.IsLockedOut) : null);

    public Task<IReadOnlyDictionary<Guid, UserAccountInfo>> GetAccountsAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, UserAccountInfo>>(_accounts.Values
            .Where(a => employeeIds.Contains(a.Id))
            .ToDictionary(a => a.Id, a => new UserAccountInfo(a.Id, a.UserName, a.Email, a.IsLockedOut)));

    public Task<UserAccountResult> CreateAccountAsync(string userName, string? email, string initialPassword, CancellationToken cancellationToken = default)
    {
        if (_accounts.Values.Any(a => a.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(UserAccountResult.Failure([$"Username '{userName}' is already taken."]));
        }

        if (initialPassword.Length < 8)
        {
            return Task.FromResult(UserAccountResult.Failure(["Passwords must be at least 8 characters."]));
        }

        var id = Guid.NewGuid();
        _accounts[id] = new Account { Id = id, UserName = userName, Email = email, Password = initialPassword };
        return Task.FromResult(UserAccountResult.Success(id));
    }

    public Task<UserAccountResult> UpdateEmailAsync(Guid employeeId, string? email, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(UserAccountResult.Failure(["The user account was not found."]));
        }

        account.Email = email;
        return Task.FromResult(UserAccountResult.Success(employeeId));
    }

    public Task<UserAccountResult> SetUserNameAsync(Guid employeeId, string userName, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(UserAccountResult.Failure(["The user account was not found."]));
        }

        if (_accounts.Values.Any(a => a.Id != employeeId && a.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(UserAccountResult.Failure([$"The user name '{userName}' is already taken."]));
        }

        account.UserName = userName;
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        return Task.FromResult(UserAccountResult.Success(employeeId));
    }

    public Task<UserAccountResult> SetRolesAsync(Guid employeeId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(UserAccountResult.Failure(["The user account was not found."]));
        }

        account.Roles.Clear();
        account.Roles.AddRange(roleNames);
        return Task.FromResult(UserAccountResult.Success(employeeId));
    }

    public Task<IReadOnlyCollection<Guid>> SearchAccountIdsAsync(string search, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(_accounts.Values
            .Where(a => a.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (a.Email?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(a => a.Id)
            .ToList());

    public Task<PasswordChangeResult> ResetPasswordAsync(Guid employeeId, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(PasswordChangeResult.UserNotFound());
        }

        if (newPassword.Length < 8)
        {
            return Task.FromResult(PasswordChangeResult.PolicyViolation(["Passwords must be at least 8 characters."]));
        }

        account.Password = newPassword;
        account.IsLockedOut = false;
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        return Task.FromResult(PasswordChangeResult.Success());
    }

    public PasswordPolicyInfo GetPasswordPolicy() => new(8, true, true, true, true, 4);

    public Task<bool> UnlockAccountAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(false);
        }

        account.IsLockedOut = false;
        return Task.FromResult(true);
    }

    public Task<PasswordChangeResult> ChangePasswordAsync(Guid employeeId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(employeeId, out var account))
        {
            return Task.FromResult(PasswordChangeResult.UserNotFound());
        }

        if (!string.Equals(account.Password, currentPassword, StringComparison.Ordinal))
        {
            return Task.FromResult(PasswordChangeResult.CurrentPasswordIncorrect());
        }

        if (newPassword.Length < 8)
        {
            return Task.FromResult(PasswordChangeResult.PolicyViolation(["Passwords must be at least 8 characters."]));
        }

        account.Password = newPassword;
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        return Task.FromResult(PasswordChangeResult.Success());
    }

    public Task<string?> GetSecurityStampAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.TryGetValue(employeeId, out var a) ? a.SecurityStamp : null);

    public Task<IReadOnlyCollection<string>> GetRolesAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(_accounts.TryGetValue(employeeId, out var a) ? a.Roles.ToList() : []);
}
