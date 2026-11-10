using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.Administration.Dto;

public sealed record AdminUserDto(
    Guid EmployeeId,
    string UserName,
    string? Email,
    string DisplayName,
    bool IsGeynessStaff,
    bool IsActive,
    DateTime? DeactivatedAtUtc,
    DateTime CreatedAtUtc,
    bool IsLockedOut,
    IReadOnlyList<string> Roles,
    IReadOnlyList<DepartmentMembershipDto> Departments,
    bool HasHistory);

public sealed record AdminUserListDto(IReadOnlyList<AdminUserDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Creates the Identity account AND the employee profile in one step. The
/// initial password is validated by ASP.NET Core Identity's own password
/// policy (the same path the development seed uses) — no custom password
/// handling exists. An administrator can later replace a forgotten password
/// with <see cref="ResetUserPasswordRequestDto"/>.
/// </summary>
public sealed record CreateUserRequestDto(
    string UserName,
    string? Email,
    string DisplayName,
    bool IsGeynessStaff,
    string InitialPassword,
    IReadOnlyList<string> Roles,
    int? PrimaryDepartmentId);

public sealed record UpdateUserProfileRequestDto(string DisplayName, string? Email, bool IsGeynessStaff);

public sealed record SetUserRolesRequestDto(IReadOnlyList<string> Roles);

public sealed record AddDepartmentMembershipRequestDto(int DepartmentId, bool IsPrimary);

/// <summary>
/// PUT api/admin/users/{employeeId}/departments - the complete set of departments the user may access, besides their primary one (which is
/// never changed or removed by this call). Departments not listed are removed; listed ones not yet held are added.
/// </summary>
public sealed record SetAccessibleDepartmentsRequestDto(IReadOnlyCollection<int> DepartmentIds);

/// <summary>
/// Administrative password reset (POST api/admin/users/{employeeId}/password).
/// The new password goes through Identity's password validators; the reason
/// is recorded in the audit trail. The password itself never is.
/// </summary>
public sealed record ResetUserPasswordRequestDto(string NewPassword, string? Reason);

/// <summary>The password rules ASP.NET Core Identity is currently configured with — what the reset form shows and checks live.</summary>
public sealed record PasswordPolicyDto(
    int RequiredLength, bool RequireDigit, bool RequireLowercase, bool RequireUppercase, bool RequireNonAlphanumeric, int RequiredUniqueChars);
