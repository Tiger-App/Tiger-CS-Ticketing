using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

/// <summary>
/// <see cref="IUserAccountManager"/> over ASP.NET Core Identity's own
/// <see cref="UserManager{TUser}"/>. Every credential and role change goes
/// through Identity (its validators, its hashing, its normalized keys);
/// nothing here reads or writes a password hash.
/// </summary>
public sealed class UserAccountManager(UserManager<ApplicationUser> userManager, TigerCsDbContext dbContext) : IUserAccountManager
{
    public async Task<UserAccountInfo?> GetAccountAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        return user is null ? null : await ToInfoAsync(user);
    }

    public async Task<IReadOnlyDictionary<Guid, UserAccountInfo>> GetAccountsAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users
            .Where(u => employeeIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<Guid, UserAccountInfo>();
        foreach (var user in users)
        {
            result[user.Id] = await ToInfoAsync(user);
        }

        return result;
    }

    public async Task<UserAccountResult> CreateAccountAsync(
        string userName, string? email, string initialPassword, CancellationToken cancellationToken = default)
    {
        var user = new ApplicationUser { UserName = userName, Email = string.IsNullOrWhiteSpace(email) ? null : email };
        var result = await userManager.CreateAsync(user, initialPassword);
        return result.Succeeded
            ? UserAccountResult.Success(user.Id)
            : UserAccountResult.Failure(result.Errors.Select(e => e.Description).ToList());
    }

    public async Task<UserAccountResult> UpdateEmailAsync(Guid employeeId, string? email, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return UserAccountResult.Failure(["The user account was not found."]);
        }

        var result = await userManager.SetEmailAsync(user, string.IsNullOrWhiteSpace(email) ? null : email);
        return result.Succeeded
            ? UserAccountResult.Success(user.Id)
            : UserAccountResult.Failure(result.Errors.Select(e => e.Description).ToList());
    }

    public async Task<UserAccountResult> SetUserNameAsync(Guid employeeId, string userName, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return UserAccountResult.Failure(["The user account was not found."]);
        }

        // Friendly, case-insensitive duplicate check (FindByNameAsync looks up the normalized name). Identity's own validator
        // would also refuse it; this just gives a clearer message. The same account re-cased ("bob" -> "Bob") is allowed.
        var existing = await userManager.FindByNameAsync(userName);
        if (existing is not null && existing.Id != user.Id)
        {
            return UserAccountResult.Failure([$"The user name '{userName}' is already taken."]);
        }

        // SetUserNameAsync sets UserName, re-normalizes NormalizedUserName, rotates the security stamp (invalidating issued
        // tokens) and runs Identity's user validators before saving.
        var result = await userManager.SetUserNameAsync(user, userName);
        return result.Succeeded
            ? UserAccountResult.Success(user.Id)
            : UserAccountResult.Failure(result.Errors.Select(e => e.Description).ToList());
    }

    public async Task<UserAccountResult> SetRolesAsync(
        Guid employeeId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return UserAccountResult.Failure(["The user account was not found."]);
        }

        var unknown = roleNames.Where(r => !Roles.All.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            return UserAccountResult.Failure([$"Unknown role(s): {string.Join(", ", unknown)}."]);
        }

        var current = await userManager.GetRolesAsync(user);
        var toAdd = roleNames.Except(current, StringComparer.Ordinal).ToList();
        var toRemove = current.Except(roleNames, StringComparer.Ordinal).ToList();

        if (toAdd.Count > 0)
        {
            var addResult = await userManager.AddToRolesAsync(user, toAdd);
            if (!addResult.Succeeded)
            {
                return UserAccountResult.Failure(addResult.Errors.Select(e => e.Description).ToList());
            }
        }

        if (toRemove.Count > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(user, toRemove);
            if (!removeResult.Succeeded)
            {
                return UserAccountResult.Failure(removeResult.Errors.Select(e => e.Description).ToList());
            }
        }

        return UserAccountResult.Success(user.Id);
    }

    public async Task<IReadOnlyCollection<Guid>> SearchAccountIdsAsync(string search, CancellationToken cancellationToken = default)
    {
        var normalized = search.Trim().ToUpperInvariant();
        return await dbContext.Users
            .Where(u => (u.NormalizedUserName != null && u.NormalizedUserName.Contains(normalized))
                || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalized)))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Implemented as <see cref="UserManager{TUser}.RemovePasswordAsync"/> +
    /// <see cref="UserManager{TUser}.AddPasswordAsync"/> rather than the
    /// token-based <c>GeneratePasswordResetTokenAsync</c>/<c>ResetPasswordAsync</c>
    /// pair: the latter needs <c>AddDefaultTokenProviders()</c> (and a data-
    /// protection token provider) which this solution does not register, and
    /// the token exists to prove possession of an out-of-band link — a
    /// System Administrator acting in-session has nothing to prove. Both are
    /// supported Identity APIs; <c>AddPasswordAsync</c> runs the same
    /// registered password validators <c>CreateAsync</c> does, so the policy
    /// applies unchanged. The validators are run up front as well, before the
    /// old hash is removed, so a policy failure leaves the current password in
    /// place rather than an account with none.
    /// </summary>
    public async Task<PasswordChangeResult> ResetPasswordAsync(Guid employeeId, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return PasswordChangeResult.UserNotFound();
        }

        // Validate before touching the stored hash, so a policy failure leaves
        // the current password in place.
        foreach (var validator in userManager.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(userManager, user, newPassword);
            if (!validation.Succeeded)
            {
                return PasswordChangeResult.PolicyViolation(validation.Errors.Select(e => e.Description).ToList());
            }
        }

        if (await userManager.HasPasswordAsync(user))
        {
            var removed = await userManager.RemovePasswordAsync(user);
            if (!removed.Succeeded)
            {
                return PasswordChangeResult.PolicyViolation(removed.Errors.Select(e => e.Description).ToList());
            }
        }

        var added = await userManager.AddPasswordAsync(user, newPassword);
        if (!added.Succeeded)
        {
            return PasswordChangeResult.PolicyViolation(added.Errors.Select(e => e.Description).ToList());
        }

        // A reset is the recovery path for a locked-out user too: lift the
        // lockout and forget the failed attempts, then rotate the stamp so
        // every token issued under the old password stops working.
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.UpdateSecurityStampAsync(user);

        return PasswordChangeResult.Success();
    }

    public PasswordPolicyInfo GetPasswordPolicy()
    {
        var p = userManager.Options.Password;
        return new(p.RequiredLength, p.RequireDigit, p.RequireLowercase, p.RequireUppercase, p.RequireNonAlphanumeric, p.RequiredUniqueChars);
    }

    public async Task<bool> UnlockAccountAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return false;
        }

        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        return true;
    }

    /// <summary>
    /// <see cref="UserManager{TUser}.ChangePasswordAsync"/> verifies the current
    /// password and runs the registered validators on the new one. Identity
    /// reports a wrong current password as the <c>PasswordMismatch</c> error
    /// code, which is mapped to <see cref="PasswordChangeOutcome.CurrentPasswordIncorrect"/>
    /// so the caller learns exactly that and nothing else.
    /// </summary>
    public async Task<PasswordChangeResult> ChangePasswordAsync(
        Guid employeeId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return PasswordChangeResult.UserNotFound();
        }

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            var mismatch = userManager.ErrorDescriber.PasswordMismatch().Code;
            return result.Errors.Any(e => e.Code == mismatch)
                ? PasswordChangeResult.CurrentPasswordIncorrect()
                : PasswordChangeResult.PolicyViolation(result.Errors.Select(e => e.Description).ToList());
        }

        await userManager.UpdateSecurityStampAsync(user);
        return PasswordChangeResult.Success();
    }

    public async Task<string?> GetSecurityStampAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == employeeId)
            .Select(u => u.SecurityStamp)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<UserAccountInfo> ToInfoAsync(ApplicationUser user) =>
        new(user.Id, user.UserName ?? string.Empty, user.Email, await userManager.IsLockedOutAsync(user));
}
