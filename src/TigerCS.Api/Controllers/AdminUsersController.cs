using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>User administration — System Administrator only. Users are deactivated, never deleted.</summary>
[Route("api/admin/users")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminUsersController(AdminUserAppService users, AdminUserPermissionAppService permissions) : AdminControllerBase
{
    /// <summary>Lists users with optional search (display name, user name, e-mail) and inactive users included on request.</summary>
    [HttpGet]
    [RequirePermission(Permissions.AdminUsersView)]
    [ProducesResponseType<AdminUserListDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? search, [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await users.ListAsync(search, includeInactive, page, pageSize, cancellationToken));

    /// <summary>The password rules ASP.NET Core Identity is configured with.</summary>
    [HttpGet("password-policy")]
    [RequirePermission(Permissions.AdminUsersView)]
    [ProducesResponseType<PasswordPolicyDto>(StatusCodes.Status200OK)]
    public IActionResult GetPasswordPolicy() => Ok(users.GetPasswordPolicy());

    /// <summary>One user with roles, department memberships and whether ticket history references them.</summary>
    [HttpGet("{employeeId:guid}")]
    [RequirePermission(Permissions.AdminUsersView)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid employeeId, CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(employeeId, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>Creates the Identity account (Identity validates the initial password) and the employee profile.</summary>
    [HttpPost]
    [RequirePermission(Permissions.AdminUsersCreate)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.CreateAsync(CallerEmployeeId, request, cancellationToken),
            created => CreatedAtAction(nameof(Get), new { employeeId = created.EmployeeId }, created));

    /// <summary>Edits the supported profile fields (display name, e-mail, Geyness-staff flag).</summary>
    [HttpPut("{employeeId:guid}/profile")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProfile(Guid employeeId, [FromBody] UpdateUserProfileRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.UpdateProfileAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Activate/deactivate — the existing last-active-administrator guard applies.</summary>
    [HttpPatch("{employeeId:guid}/activation")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetActivation(Guid employeeId, [FromBody] SetActiveRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.SetActivationAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Replaces the user's roles with exactly the given fixed roles.</summary>
    [HttpPut("{employeeId:guid}/roles")]
    [RequirePermission(Permissions.AdminRolesAssign)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetRoles(Guid employeeId, [FromBody] SetUserRolesRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.SetRolesAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Adds a department membership using the existing membership model.</summary>
    [HttpPost("{employeeId:guid}/departments")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddDepartment(Guid employeeId, [FromBody] AddDepartmentMembershipRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.AddDepartmentAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Sets every department (besides the primary one) the user may access, in one call.</summary>
    /// <remarks>The primary department is never changed or removed. Departments not listed are removed; listed ones not yet held are added (active departments only).</remarks>
    [HttpPut("{employeeId:guid}/departments")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetAccessibleDepartments(Guid employeeId, [FromBody] SetAccessibleDepartmentsRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.SetAccessibleDepartmentsAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Resets the user's password (the "forgot my password" path, taken on the user's behalf).</summary>
    /// <remarks>
    /// Identity's password policy decides whether the new password is
    /// acceptable (docs/DEV-SETUP.md §3). On success every session the user
    /// had is invalidated — their next request with an old token is refused —
    /// and any lockout is lifted, so they can sign in with the new password at
    /// once. The password is never written to the audit trail or logs.
    /// </remarks>
    /// <response code="204">Password replaced; the user's existing sessions are invalid.</response>
    /// <response code="404">No such employee.</response>
    /// <response code="422">The new password violates the password policy; <c>errors</c> lists each reason.</response>
    [HttpPost("{employeeId:guid}/password")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ResetPassword(Guid employeeId, [FromBody] ResetUserPasswordRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.ResetPasswordAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Unlocks a locked-out account (clears the lockout end date and failed-attempt count) and audits it.</summary>
    [HttpPost("{employeeId:guid}/unlock")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Unlock(Guid employeeId, CancellationToken cancellationToken) =>
        FromResult(await users.UnlockAsync(CallerEmployeeId, employeeId, cancellationToken));

    /// <summary>Ends a department membership; the primary membership cannot be removed while others remain.</summary>
    [HttpDelete("{employeeId:guid}/departments/{departmentId:int}")]
    [RequirePermission(Permissions.AdminUsersEdit)]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveDepartment(Guid employeeId, int departmentId, CancellationToken cancellationToken) =>
        FromResult(await users.RemoveDepartmentAsync(CallerEmployeeId, employeeId, departmentId, cancellationToken));

    /// <summary>The employee's roles, role baseline, per-permission overrides, effective permissions and recent changes.</summary>
    [HttpGet("{employeeId:guid}/permissions")]
    [RequirePermission(Permissions.AdminPermissionsManage)]
    [ProducesResponseType<UserPermissionsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPermissions(Guid employeeId, CancellationToken cancellationToken) =>
        FromResult(await permissions.GetAsync(CallerEmployeeId, employeeId, cancellationToken));

    /// <summary>Replaces the employee's permission overrides (Grant / Deny; absent = inherit from roles). Audited per changed permission.</summary>
    /// <remarks>
    /// 403 when the caller edits their own permissions, grants something they do not hold, or grants an administrator-only
    /// permission; 409 when the change would remove an administrator-only permission from the last active System Administrator
    /// or targets an integration account; 400 for unknown permissions or mutating grants to read-only roles.
    /// </remarks>
    [HttpPut("{employeeId:guid}/permissions")]
    [RequirePermission(Permissions.AdminPermissionsManage)]
    [ProducesResponseType<UserPermissionsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetPermissions(Guid employeeId, [FromBody] SetUserPermissionsRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await permissions.SetAsync(CallerEmployeeId, employeeId, request, cancellationToken));
}
