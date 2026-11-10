using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>
/// Which departments a caller may transfer tickets TO. Membership of a department (the right to view it) contributes nothing here: the only input is
/// the employee's explicit transfer-destination list (Administration → Users). An employee with a list may transfer only to the departments on it;
/// one with no list keeps the role-based behaviour that existed before the list was introduced (nothing is revoked on deployment, nothing is
/// granted by being able to view a department). The System Administrator override is not narrowed by it. A null repository (tests, tools) means
/// "no list".
/// </summary>
public static class TransferDestinationPolicy
{
    public static async Task<bool> IsPermittedAsync(
        IUserTransferDepartmentRepository? lists, Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, int targetDepartmentId,
        CancellationToken cancellationToken)
    {
        if (lists is null || TigerCS.Domain.Modules.IdentityAndAccess.AuthorizationOverride.AppliesTo(callerRoles))
        {
            return true;
        }

        var allowed = await lists.GetDepartmentIdsAsync(callerEmployeeId, cancellationToken);
        return allowed.Count == 0 || allowed.Contains(targetDepartmentId);
    }

    /// <summary>The departments from <paramref name="candidates"/> the caller may transfer to (all of them when no list applies); the Transfer form offers exactly these.</summary>
    public static IReadOnlyList<int> Filter(IReadOnlyCollection<int>? explicitList, IEnumerable<int> candidates) =>
        explicitList is null || explicitList.Count == 0 ? [.. candidates] : [.. candidates.Where(explicitList.Contains)];
}
