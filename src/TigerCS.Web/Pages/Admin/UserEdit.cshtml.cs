using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// One page for "Add User" (no id) and "Manage User" (id). Every section
/// posts to its own handler and redirects back, so a failure on one form
/// never loses the others' state.
/// </summary>
public sealed class UserEditModel(AdminApiClient adminApi, DepartmentsApiClient departmentsApi) : AdminPageModel
{
    public Guid? EmployeeId { get; private set; }
    public bool IsNew => EmployeeId is null;
    public AdminUserDto? UserRecord { get; private set; }
    public IReadOnlyCollection<RoleDto> Roles { get; private set; } = [];
    public IReadOnlyCollection<DepartmentDto> ActiveDepartments { get; private set; } = [];
    public string? LoadError { get; private set; }

    /// <summary>The departments this user may transfer tickets to. Empty = no explicit list (role-based behaviour). Independent of <see cref="UserRecord"/>'s memberships.</summary>
    public IReadOnlyCollection<int> TransferDepartmentIds { get; private set; } = [];

    /// <summary>Identity's configured password rules; null if they could not be loaded (the server still validates).</summary>
    public PasswordPolicyDto? PasswordPolicy { get; private set; }

    /// <summary>
    /// The user's Collections module access as the permission service reports it (role baseline plus overrides); null when it could not
    /// be read, in which case the panel says so and offers no change.
    /// </summary>
    public CollectionsAccessInfo? CollectionsAccessState { get; private set; }

    [BindProperty] public CollectionsAccessInput CollectionsAccessForm { get; set; } = new();
    [BindProperty] public CreateInput Create { get; set; } = new();
    [BindProperty] public ProfileInput Profile { get; set; } = new();
    [BindProperty] public RolesInput RolesForm { get; set; } = new();
    [BindProperty] public MembershipInput Membership { get; set; } = new();
    [BindProperty] public AccessibleDepartmentsInput AccessibleDepartments { get; set; } = new();
    [BindProperty] public ActivationInput Activation { get; set; } = new();
    [BindProperty] public TransferDepartmentsInput TransferDepartments { get; set; } = new();
    [BindProperty] public ResetPasswordInput ResetPassword { get; set; } = new();

    /// <summary>The policy stated beside every new-password field — the pilot defaults in docs/DEV-SETUP.md §3.</summary>
    public const string PasswordPolicyText =
        "At least 8 characters, with an uppercase letter, a lowercase letter, a digit and a symbol.";

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken cancellationToken)
    {
        EmployeeId = id;
        await LoadReferenceDataAsync(cancellationToken);
        if (id is null)
        {
            await LoadPasswordPolicyAsync(cancellationToken);
        }

        if (id is { } employeeId)
        {
            var result = await adminApi.GetUserAsync(employeeId, cancellationToken);
            if (result.Outcome == ApiOutcome.NotFound)
            {
                return NotFound();
            }

            if (!result.IsSuccess)
            {
                LoadError = DescribeFailure(result, "The user could not be loaded.");
                return Page();
            }

            UserRecord = result.Value;
            var transfer = await adminApi.GetUserTransferDepartmentsAsync(employeeId, cancellationToken);
            TransferDepartmentIds = transfer.IsSuccess && transfer.Value is not null ? transfer.Value.DepartmentIds : [];
            await LoadPasswordPolicyAsync(cancellationToken);
            await LoadCollectionsAccessAsync(employeeId, cancellationToken);
            Profile = new ProfileInput
            {
                UserName = UserRecord!.UserName,
                DisplayName = UserRecord.DisplayName,
                Email = UserRecord.Email,
                IsGeynessStaff = UserRecord.IsGeynessStaff
            };
            RolesForm = new RolesInput { Roles = UserRecord.Roles.ToList() };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        EmployeeId = null;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Create.UserName)) errors.Add("User name is required.");
        if (string.IsNullOrWhiteSpace(Create.DisplayName)) errors.Add("Display name is required.");
        if (string.IsNullOrWhiteSpace(Create.InitialPassword)) errors.Add("An initial password is required.");
        if (Create.InitialPassword != Create.ConfirmPassword) errors.Add("The two passwords do not match.");
        if (errors.Count > 0)
        {
            ModelState.Clear();
            ErrorMessage = string.Join("\n", errors);
            await LoadReferenceDataAsync(cancellationToken);
            await LoadPasswordPolicyAsync(cancellationToken);
            return Page();
        }

        var result = await adminApi.CreateUserAsync(
            new CreateUserRequestDto(
                Create.UserName.Trim(), Create.Email, Create.DisplayName.Trim(), Create.IsGeynessStaff,
                Create.InitialPassword, Create.Roles ?? [], Create.PrimaryDepartmentId),
            cancellationToken);

        if (!result.IsSuccess)
        {
            ModelState.Clear();
            ErrorMessage = DescribeFailure(result, "The user could not be created.");
            await LoadReferenceDataAsync(cancellationToken);
            await LoadPasswordPolicyAsync(cancellationToken);
            return Page();
        }

        StatusMessage = $"User '{result.Value!.DisplayName}' created.";
        return RedirectToPage("/Admin/UserEdit", new { id = result.Value.EmployeeId });
    }

    public Task<IActionResult> OnPostProfileAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.UpdateUserProfileAsync(id, new UpdateUserProfileRequestDto(Profile.DisplayName, Profile.Email, Profile.IsGeynessStaff, Profile.UserName), cancellationToken),
            "Profile saved. If the username was changed, the user's existing sessions have been signed out.", "The profile could not be saved.");

    /// <summary>
    /// Grants or revokes the Collections module access permission. It changes exactly one override: the Api takes the user's COMPLETE
    /// override set, so the current set is read back first and only <c>Collections.Access</c> is added (grant) or removed (revoke);
    /// every other grant and denial - including the granular Collections and Campaign permissions - is sent back unchanged. System
    /// Administrators and CS Managers always have access through their role, so there is nothing to change for them. The Api
    /// re-checks everything (who may change permissions, not your own, never an integration account) and audits the change.
    /// </summary>
    public async Task<IActionResult> OnPostCollectionsAccessAsync(Guid id, CancellationToken cancellationToken)
    {
        var current = await adminApi.GetUserPermissionsAsync(id, cancellationToken);
        if (!current.IsSuccess || current.Value is null)
        {
            ErrorMessage = DescribeFailure(current, "The user's permissions could not be read.");
            return RedirectToPage("/Admin/UserEdit", new { id });
        }

        var key = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.CollectionsAccess;
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in current.Value.Modules.SelectMany(m => m.Permissions))
        {
            if (!string.Equals(state.Override, "Inherit", StringComparison.OrdinalIgnoreCase))
            {
                overrides[state.Key] = state.Override;
            }
        }

        if (CollectionsAccessForm.Enabled)
        {
            overrides[key] = "Grant";
        }
        else
        {
            overrides.Remove(key);
        }

        var result = await adminApi.SetUserPermissionsAsync(
            id, new SetUserPermissionsRequestDto(overrides, CollectionsAccessForm.Reason), cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = CollectionsAccessForm.Enabled
                ? "Collections access granted."
                : "Collections access revoked.";
        }
        else
        {
            ErrorMessage = DescribeFailure(result, "Collections access could not be changed.");
        }

        return RedirectToPage("/Admin/UserEdit", new { id });
    }

    public Task<IActionResult> OnPostRolesAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetUserRolesAsync(id, new SetUserRolesRequestDto(RolesForm.Roles ?? []), cancellationToken),
            "Roles updated.", "The roles could not be updated.");

    public Task<IActionResult> OnPostActivationAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetUserActivationAsync(id, new SetActiveRequestDto(Activation.IsActive, Activation.Reason), cancellationToken),
            Activation.IsActive ? "User activated." : "User deactivated. Their history is unchanged.",
            Activation.IsActive ? "The user could not be activated." : "The user could not be deactivated.");

    public Task<IActionResult> OnPostAddDepartmentAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.AddUserDepartmentAsync(id, new AddDepartmentMembershipRequestDto(Membership.DepartmentId, Membership.IsPrimary), cancellationToken),
            "Department membership added.", "The department membership could not be added.");

    /// <summary>Saves the whole Accessible Departments selection at once. The primary department is never changed here (the Api keeps it whatever is posted).</summary>
    public Task<IActionResult> OnPostAccessibleDepartmentsAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetUserAccessibleDepartmentsAsync(id, new SetAccessibleDepartmentsRequestDto(AccessibleDepartments.DepartmentIds ?? []), cancellationToken),
            "Accessible departments saved.", "The accessible departments could not be saved.");

    /// <summary>Saves the departments this user may transfer tickets to. Posting no checkbox clears the list. Membership (view access) is never touched.</summary>
    public async Task<IActionResult> OnPostTransferDepartmentsAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await adminApi.SetUserTransferDepartmentsAsync(
            id, new SetUserTransferDepartmentsRequestDto(TransferDepartments.DepartmentIds ?? []), cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = result.Value is { DepartmentIds.Count: > 0 }
                ? "Transfer destinations saved."
                : "Transfer destinations cleared. This user's transfers follow their role again.";
        }
        else
        {
            ErrorMessage = DescribeFailure(result, "The transfer destinations could not be saved.");
        }

        return RedirectToPage("/Admin/UserEdit", new { id });
    }

    public Task<IActionResult> OnPostRemoveDepartmentAsync(Guid id, int departmentId, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.RemoveUserDepartmentAsync(id, departmentId, cancellationToken),
            "Department membership removed.", "The department membership could not be removed.");

    /// <summary>
    /// Administrative password reset. The confirm-match check is the only
    /// Web-side rule; the policy itself is the Api's (Identity's), whose 422
    /// reasons are shown verbatim. Nothing about the password is logged or
    /// kept — the redirect carries only the outcome message.
    /// </summary>
    public async Task<IActionResult> OnPostResetPasswordAsync(Guid id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ResetPassword.NewPassword))
        {
            ErrorMessage = "Enter a new password.";
        }
        else if (ResetPassword.NewPassword != ResetPassword.ConfirmPassword)
        {
            ErrorMessage = "The new password and its confirmation do not match.";
        }
        else
        {
            var result = await adminApi.ResetUserPasswordAsync(
                id, new ResetUserPasswordRequestDto(ResetPassword.NewPassword, ResetPassword.Reason), cancellationToken);
            if (result.IsSuccess)
            {
                StatusMessage = "Password reset. The user's existing sessions have been signed out; they can sign in with the new password now.";
            }
            else
            {
                ErrorMessage = DescribeFailure(result, "The password could not be reset.");
            }
        }

        return RedirectToPage("/Admin/UserEdit", new { id });
    }

    private async Task<IActionResult> ApplyAsync(Guid id, Task<ApiResult<AdminUserDto>> call, string success, string failure)
    {
        var result = await call;
        if (result.IsSuccess)
        {
            StatusMessage = success;
        }
        else
        {
            ErrorMessage = DescribeFailure(result, failure);
        }

        return RedirectToPage("/Admin/UserEdit", new { id });
    }

    private async Task LoadCollectionsAccessAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var permissions = await adminApi.GetUserPermissionsAsync(employeeId, cancellationToken);
        if (!permissions.IsSuccess || permissions.Value is null)
        {
            return;
        }

        var all = permissions.Value.Modules.SelectMany(m => m.Permissions).ToList();
        var access = all.FirstOrDefault(p => p.Key == TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.CollectionsAccess);
        if (access is null)
        {
            return;
        }

        var view = all.FirstOrDefault(p => p.Key == TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions.CollectionsView);
        var alwaysAllowed = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.CollectionsAccess.IsAlwaysAllowed(permissions.Value.Roles);
        CollectionsAccessState = new CollectionsAccessInfo(
            IsEffective: access.IsEffective,
            IsGranted: string.Equals(access.Override, "Grant", StringComparison.OrdinalIgnoreCase),
            AlwaysAllowedByRole: alwaysAllowed,
            CanChange: !alwaysAllowed && !permissions.Value.IsServiceIdentity && !permissions.Value.IsSelf && access.CanGrant,
            ChangeBlockedReason: alwaysAllowed ? "Always allowed by this user's role (System Administrator or CS Manager)."
                : permissions.Value.IsServiceIdentity ? "This is an integration account; its access is fixed."
                : permissions.Value.IsSelf ? "You cannot change your own permissions. Ask another System Administrator."
                : !access.CanGrant ? "Not available for this user's roles." : null,
            HoldsViewPermission: view?.IsEffective ?? false);
        CollectionsAccessForm = new CollectionsAccessInput { Enabled = access.IsEffective };
    }

    private async Task LoadPasswordPolicyAsync(CancellationToken cancellationToken)
    {
        var policy = await adminApi.GetPasswordPolicyAsync(cancellationToken);
        PasswordPolicy = policy.IsSuccess ? policy.Value : null;
    }

    private async Task LoadReferenceDataAsync(CancellationToken cancellationToken)
    {
        var roles = adminApi.GetRolesAsync(cancellationToken);
        var departments = departmentsApi.GetDepartmentsAsync(activeOnly: true, cancellationToken);
        await Task.WhenAll(roles, departments);
        Roles = roles.Result.IsSuccess ? roles.Result.Value ?? [] : [];
        ActiveDepartments = departments.Result.IsSuccess ? departments.Result.Value ?? [] : [];
    }

    public sealed class CreateInput
    {
        [Required] public string UserName { get; set; } = string.Empty;
        public string? Email { get; set; }
        [Required] public string DisplayName { get; set; } = string.Empty;
        public bool IsGeynessStaff { get; set; }
        [Required] public string InitialPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
        public List<string>? Roles { get; set; }
        public int? PrimaryDepartmentId { get; set; }
    }

    /// <summary>What the Collections access panel shows. <see cref="HoldsViewPermission"/> is informational: module access opens the door, the View permission decides what is readable inside.</summary>
    public sealed record CollectionsAccessInfo(
        bool IsEffective, bool IsGranted, bool AlwaysAllowedByRole, bool CanChange, string? ChangeBlockedReason, bool HoldsViewPermission);

    public sealed class CollectionsAccessInput
    {
        public bool Enabled { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class ProfileInput
    {
        [Required] public string UserName { get; set; } = string.Empty;
        [Required] public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public bool IsGeynessStaff { get; set; }
    }

    public sealed class RolesInput
    {
        public List<string>? Roles { get; set; }
    }

    public sealed class TransferDepartmentsInput
    {
        public List<int>? DepartmentIds { get; set; }
    }

    public sealed class MembershipInput
    {
        public int DepartmentId { get; set; }
        public bool IsPrimary { get; set; }
    }

    public sealed class AccessibleDepartmentsInput
    {
        public List<int>? DepartmentIds { get; set; }
    }

    public sealed class ActivationInput
    {
        public bool IsActive { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class ResetPasswordInput
    {
        [Required] public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
        public string? Reason { get; set; }
    }
}
