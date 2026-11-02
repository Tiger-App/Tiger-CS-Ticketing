using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// Administration → Users → Manage Permissions. A view over two Api
/// resources: the employee's roles (the existing roles endpoint) and their
/// permission overrides. Nothing is decided here - the Api enforces the
/// System Administrator policy, the Manage Permissions permission, the
/// self-edit / escalation / last-administrator rules and the audit trail; this page
/// only renders the answer and shows the Api's refusal reason verbatim.
/// </summary>
public sealed class UserPermissionsModel(AdminApiClient adminApi) : AdminPageModel
{
    public Guid EmployeeId { get; private set; }
    public UserPermissionsDto? Permissions { get; private set; }
    public IReadOnlyCollection<RoleDto> RoleCatalog { get; private set; } = [];
    public string? LoadError { get; private set; }

    [BindProperty] public List<string>? SelectedRoles { get; set; }

    /// <summary>permission key to "Inherit" | "Grant" | "Deny", posted as <c>Overrides[Tickets.Edit]=Deny</c>.</summary>
    [BindProperty] public Dictionary<string, string>? Overrides { get; set; }

    [BindProperty] public string? Reason { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        EmployeeId = id;
        var permissions = adminApi.GetUserPermissionsAsync(id, cancellationToken);
        var roles = adminApi.GetRolesAsync(cancellationToken);
        await Task.WhenAll(permissions, roles);

        if (permissions.Result.Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        if (!permissions.Result.IsSuccess)
        {
            LoadError = DescribeFailure(permissions.Result, "The permissions could not be loaded.");
            return Page();
        }

        Permissions = permissions.Result.Value;
        RoleCatalog = roles.Result.IsSuccess ? roles.Result.Value ?? [] : [];
        SelectedRoles = Permissions!.Roles.ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id, CancellationToken cancellationToken)
    {
        // Only Grant/Deny are sent; "Inherit" (and anything else) means no override.
        var overrides = (Overrides ?? [])
            .Where(o => o.Value is "Grant" or "Deny")
            .ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal);

        var result = await adminApi.SetUserPermissionsAsync(id, new SetUserPermissionsRequestDto(overrides, Reason), cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = "Permissions saved. They apply from the user's next request.";
        }
        else
        {
            ErrorMessage = DescribeFailure(result, "The permissions could not be saved.");
        }

        return RedirectToPage("/Admin/UserPermissions", new { id });
    }

    public async Task<IActionResult> OnPostRolesAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await adminApi.SetUserRolesAsync(id, new SetUserRolesRequestDto(SelectedRoles ?? []), cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = "Roles updated.";
        }
        else
        {
            ErrorMessage = DescribeFailure(result, "The roles could not be updated.");
        }

        return RedirectToPage("/Admin/UserPermissions", new { id });
    }
}
