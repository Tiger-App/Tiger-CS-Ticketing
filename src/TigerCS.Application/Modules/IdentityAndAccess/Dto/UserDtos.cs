namespace TigerCS.Application.Modules.IdentityAndAccess.Dto;

/// <summary>One of the caller's department assignments (MVP-API-Contracts.md §1.3).</summary>
/// <param name="DepartmentId">The department.</param>
/// <param name="Name">The department's display name.</param>
/// <param name="IsPrimary">True for the caller's primary department. At most one membership is primary.</param>
public sealed record DepartmentMembershipDto(int DepartmentId, string Name, bool IsPrimary);

/// <summary>The signed-in user's own profile (MVP-API-Contracts.md §1.3).</summary>
/// <param name="EmployeeId">The caller, resolved from the access token — never from a client-supplied id.</param>
/// <param name="DisplayName">The caller's display name.</param>
/// <param name="Roles">Every role the caller holds, by name.</param>
/// <param name="Departments">Every department the caller is assigned to.</param>
/// <param name="IsGeynessStaff">Whether the caller is customer-service (Geyness) staff.</param>
/// <param name="EffectivePermissions">Role baseline combined with the caller's own overrides (display only; the Api enforces).</param>
/// <param name="GrantedPermissions">Permissions the caller holds through an explicit grant.</param>
/// <param name="DeniedPermissions">Permissions explicitly denied to the caller - a UI must never offer these actions.</param>
public sealed record CurrentUserResponseDto(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<DepartmentMembershipDto> Departments,
    bool IsGeynessStaff,
    IReadOnlyCollection<string>? EffectivePermissions = null,
    IReadOnlyCollection<string>? GrantedPermissions = null,
    IReadOnlyCollection<string>? DeniedPermissions = null);

/// <summary>One member of a department (MVP-API-Contracts.md §1.4).</summary>
/// <param name="EmployeeId">The member.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="IsPrimary">True when this department is that member's primary one.</param>
/// <param name="Roles">Every role the member holds, by name.</param>
public sealed record DepartmentUserDto(
    Guid EmployeeId,
    string DisplayName,
    bool IsPrimary,
    IReadOnlyCollection<string> Roles);

/// <summary>
/// One employee in the cross-department assignee directory
/// (<c>GET /api/users/assignable</c>): who they are, every role they hold
/// and every department they belong to (primary first). Exactly one row per
/// employee, however many departments or roles they have.
/// </summary>
/// <param name="EmployeeId">The employee.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Roles">Every role the employee holds, by name.</param>
/// <param name="Departments">Every active department the employee is a member of, primary first.</param>
public sealed record AssignableUserDto(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<DepartmentMembershipDto> Departments);

/// <summary>A single page of results.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">This page's items.</param>
/// <param name="Page">The 1-based page number that was served.</param>
/// <param name="PageSize">The page size that was served, after clamping.</param>
/// <param name="TotalCount">How many items match in total, across all pages.</param>
public sealed record PagedResultDto<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>One role in the fixed MVP role catalogue (MVP-API-Contracts.md §1.5).</summary>
/// <param name="RoleName">The role name, e.g. "CS Agent".</param>
/// <param name="Description">A high-level description of what the role may do.</param>
public sealed record RoleDto(string RoleName, string Description);

/// <summary>The desired activation state for a user (MVP-API-Contracts.md §1.6).</summary>
/// <param name="IsActive">Required. True to activate, false to deactivate.</param>
/// <param name="Reason">Optional free-text reason, recorded with the change.</param>
public sealed record ActivationRequestDto(bool IsActive, string? Reason);

/// <summary>The result of an activation change (MVP-API-Contracts.md §1.6).</summary>
/// <param name="EmployeeId">The employee whose activation changed.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="IsActive">The state after the change.</param>
/// <param name="DeactivatedAtUtc">When they were deactivated, in UTC, or null while active.</param>
/// <param name="AffectedOpenTicketCount">How many of that employee's open tickets the change touches.</param>
public sealed record ActivationResponseDto(
    Guid EmployeeId,
    string DisplayName,
    bool IsActive,
    DateTime? DeactivatedAtUtc,
    int AffectedOpenTicketCount);

public enum ActivationOutcome
{
    Success,
    NotFound,
    LastActiveAdministrator
}

public sealed record ActivationResult(ActivationOutcome Outcome, ActivationResponseDto? Response = null)
{
    public static ActivationResult Success(ActivationResponseDto response) => new(ActivationOutcome.Success, response);
    public static ActivationResult NotFound() => new(ActivationOutcome.NotFound);
    public static ActivationResult LastActiveAdministrator() => new(ActivationOutcome.LastActiveAdministrator);
}
