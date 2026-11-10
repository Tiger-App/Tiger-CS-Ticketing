using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Services;

/// <summary>
/// Best-effort id → name resolution for the fields TigerCS.Api returns as
/// raw ids with no dedicated lookup endpoint (see the PR description's
/// "Contract gaps" section). Every resolution here is backed by a real,
/// contract-guaranteed fact — never a guess:
///
/// <list type="bullet">
/// <item>Department names come from the Department directory
/// (<c>GET /api/departments?activeOnly=false</c>, viewable by any
/// authenticated staff member), so every department a ticket can belong to
/// — including one deactivated since — resolves to its name; the caller's
/// own memberships (<c>GET /api/users/me</c>) are merged in as well. A raw
/// department id is never the normal display; only a failed directory call
/// leaves a name unresolved, and callers then degrade to neutral wording.</item>
/// <item>An assigned owner is guaranteed by the Assign endpoint's own
/// business rule to be an active member of the ticket's current department
/// (<c>EmployeeNotInDepartment</c> outcome), so paging that department's
/// member list (<c>GET /api/departments/{id}/users</c>) to match the id is a
/// correct resolution, not a guess. Bounded to 100 members; beyond that the
/// id is shown as-is rather than paging indefinitely.</item>
/// <item>A note's author is resolved only when it is the signed-in caller
/// themself (a real, already-known fact) — there is no membership guarantee
/// for note authorship, so any other author id is shown as-is.</item>
/// </list>
///
/// Category names are resolved by <see cref="CategoriesApiClient"/> where a
/// Category is being <i>selected</i> (the New Ticket wizard's Category
/// dropdown) — this resolver's own remit is only the ids above, so a
/// Category shown elsewhere as a bare id (e.g. an existing ticket's
/// CategoryId) is still shown as-is by callers of this service; wiring that
/// display up to the same endpoint is a separate follow-up, not done here.
/// Registered scoped, so its per-request caches live for one page render.
/// </summary>
public sealed class TicketNameResolver(UsersApiClient usersApiClient, DepartmentsApiClient departmentsApiClient, ViewerAccessService? viewerAccess = null)
{
    private readonly Dictionary<int, string> _departmentNames = new();
    private readonly Dictionary<(int DepartmentId, Guid EmployeeId), string?> _employeeNames = new();
    private bool _ownDepartmentsLoaded;
    private bool _directoryLoaded;
    private IReadOnlyCollection<DepartmentDto>? _activeDepartments;

    /// <summary>
    /// False only when the directory call itself failed (or has not been
    /// primed) — the signal for a page to say "department list unavailable"
    /// rather than silently offering nothing.
    /// </summary>
    public bool DepartmentDirectoryAvailable { get; private set; }

    /// <summary>
    /// Loads the full Department directory (deactivated departments included,
    /// so any department a ticket already sits in gets its name) into the
    /// id→name map. Failure is tolerated: names fall back per the display
    /// helpers, never to an exception on a read page.
    /// </summary>
    public async Task PrimeDepartmentDirectoryAsync(CancellationToken cancellationToken)
    {
        if (_directoryLoaded)
        {
            return;
        }

        _directoryLoaded = true;
        ApplyDirectory(await departmentsApiClient.GetDepartmentsAsync(activeOnly: false, cancellationToken));
    }

    /// <summary>
    /// The ACTIVE departments only — the set a Transfer picker may offer,
    /// mirroring the Api's own TargetDepartmentInactive rule so the UI never
    /// offers a destination the transfer would reject. Null when the call
    /// failed. Ordered by name, as the directory returns it.
    /// </summary>
    public async Task<IReadOnlyCollection<DepartmentDto>?> GetActiveDepartmentsAsync(CancellationToken cancellationToken)
    {
        if (_activeDepartments is not null)
        {
            return _activeDepartments;
        }

        var result = await departmentsApiClient.GetDepartmentsAsync(activeOnly: true, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return null;
        }

        _activeDepartments = result.Value;
        foreach (var department in result.Value)
        {
            _departmentNames.TryAdd(department.DepartmentId, department.Name);
        }

        return _activeDepartments;
    }

    /// <summary>The caller's own department memberships — the only set of department id→name pairs the Api exposes.</summary>
    public IReadOnlyCollection<DepartmentMembershipDto> OwnDepartments { get; private set; } = [];

    /// <summary>
    /// The departments the Tickets Department filter offers: every department the Api lets the caller see (<c>AccessibleDepartments</c> from the
    /// same /me call). Falls back to the memberships when an older Api does not send it. Filtering is only a narrowing - the Api still scopes every list, count, search and export.
    /// </summary>
    public IReadOnlyCollection<DepartmentMembershipDto> AccessibleDepartments { get; private set; } = [];

    /// <summary>The caller's own explicit permission grants and denials from the same /me call (empty when none or unreadable).</summary>
    public ViewerPermissions OwnPermissions { get; private set; } = ViewerPermissions.None;

    public async Task PrimeOwnDepartmentsAsync(CancellationToken cancellationToken)
    {
        if (_ownDepartmentsLoaded)
        {
            return;
        }

        _ownDepartmentsLoaded = true;
        ApplyOwnDepartments(await GetMeAsync(cancellationToken));
    }

    /// <summary>
    /// Both department sources at once — the caller's memberships and the
    /// full directory — for the pages that display department names. The two
    /// HTTP calls run concurrently; the results are merged into the name map
    /// one after the other, so the (unsynchronized) cache is only ever
    /// written from a single continuation. Pages must use this rather than
    /// awaiting the two individual primes under Task.WhenAll.
    /// </summary>
    public async Task PrimeDepartmentsAsync(CancellationToken cancellationToken)
    {
        var ownTask = _ownDepartmentsLoaded ? null : GetMeAsync(cancellationToken);
        var directoryTask = _directoryLoaded ? null : departmentsApiClient.GetDepartmentsAsync(activeOnly: false, cancellationToken);
        _ownDepartmentsLoaded = true;
        _directoryLoaded = true;

        if (ownTask is not null)
        {
            ApplyOwnDepartments(await ownTask);
        }

        if (directoryTask is not null)
        {
            ApplyDirectory(await directoryTask);
        }
    }

    /// <summary>The one /me call of the request when the shared viewer-access service is wired (production); a direct call otherwise.</summary>
    private Task<ApiResult<CurrentUserResponseDto>> GetMeAsync(CancellationToken cancellationToken) =>
        viewerAccess is not null ? viewerAccess.GetMeAsync(cancellationToken) : usersApiClient.GetMeAsync(cancellationToken);

    private void ApplyOwnDepartments(ApiResult<CurrentUserResponseDto> me)
    {
        if (!me.IsSuccess || me.Value is null)
        {
            return;
        }

        OwnDepartments = me.Value.Departments;
        AccessibleDepartments = me.Value.AccessibleDepartments ?? me.Value.Departments;
        OwnPermissions = new ViewerPermissions(
            new HashSet<string>(me.Value.GrantedPermissions ?? [], StringComparer.Ordinal),
            new HashSet<string>(me.Value.DeniedPermissions ?? [], StringComparer.Ordinal));
        foreach (var department in me.Value.Departments.Concat(AccessibleDepartments))
        {
            _departmentNames[department.DepartmentId] = department.Name;
        }
    }

    private void ApplyDirectory(ApiResult<IReadOnlyCollection<DepartmentDto>> directory)
    {
        if (!directory.IsSuccess || directory.Value is null)
        {
            return;
        }

        DepartmentDirectoryAvailable = true;
        foreach (var department in directory.Value)
        {
            _departmentNames[department.DepartmentId] = department.Name;
        }
    }

    /// <summary>The department's name if the directory (or the caller's own memberships) resolved it, otherwise null — callers render neutral wording, never the raw id.</summary>
    public string? TryGetDepartmentName(int departmentId) =>
        _departmentNames.GetValueOrDefault(departmentId);

    /// <summary>
    /// The owner's display name if it could be resolved by paging the
    /// ticket's own department (guaranteed membership), otherwise null.
    /// </summary>
    public async Task<string?> ResolveOwnerNameAsync(int currentDepartmentId, Guid ownerEmployeeId, CancellationToken cancellationToken)
    {
        var key = (currentDepartmentId, ownerEmployeeId);
        if (_employeeNames.TryGetValue(key, out var cached))
        {
            return cached;
        }

        const int pageSize = 100;
        for (var page = 1; page <= 2; page++)
        {
            var result = await usersApiClient.GetDepartmentUsersAsync(currentDepartmentId, page, pageSize, cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                break;
            }

            var match = result.Value.Items.FirstOrDefault(u => u.EmployeeId == ownerEmployeeId);
            if (match is not null)
            {
                _employeeNames[key] = match.DisplayName;
                return match.DisplayName;
            }

            if (result.Value.Items.Count < pageSize)
            {
                break;
            }
        }

        _employeeNames[key] = null;
        return null;
    }

    /// <summary>The note author's display name if it is the signed-in caller themself, otherwise null.</summary>
    public static string? ResolveSelfAuthorName(Guid authorEmployeeId, CurrentUser? currentUser) =>
        currentUser is not null && currentUser.EmployeeId == authorEmployeeId ? currentUser.DisplayName : null;
}
