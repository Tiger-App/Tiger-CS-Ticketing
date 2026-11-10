using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's Administration endpoints (<c>api/admin/*</c>) plus the
/// role catalogue and the operational request-type directory. Every call is
/// authorized server-side by the System Administrator policy — this client
/// never decides anything itself.
/// </summary>
public sealed class AdminApiClient(HttpClient httpClient, ILogger<AdminApiClient> logger) : ApiClientBase(httpClient, logger)
{
    // ---- users ----
    public Task<ApiResult<AdminUserListDto>> GetUsersAsync(string? search, bool includeInactive, int page, int pageSize, CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query["search"] = search;
        }

        query["includeInactive"] = includeInactive ? "true" : "false";
        query["page"] = page.ToString();
        query["pageSize"] = pageSize.ToString();
        return GetAsync<AdminUserListDto>($"api/admin/users?{query}", ct);
    }

    public Task<ApiResult<AdminUserDto>> GetUserAsync(Guid employeeId, CancellationToken ct) =>
        GetAsync<AdminUserDto>($"api/admin/users/{employeeId}", ct);

    public Task<ApiResult<AdminUserDto>> CreateUserAsync(CreateUserRequestDto request, CancellationToken ct) =>
        PostAsync<CreateUserRequestDto, AdminUserDto>("api/admin/users", request, ct);

    public Task<ApiResult<AdminUserDto>> UpdateUserProfileAsync(Guid employeeId, UpdateUserProfileRequestDto request, CancellationToken ct) =>
        PutAsync<UpdateUserProfileRequestDto, AdminUserDto>($"api/admin/users/{employeeId}/profile", request, ct);

    public Task<ApiResult<AdminUserDto>> SetUserActivationAsync(Guid employeeId, SetActiveRequestDto request, CancellationToken ct) =>
        PatchAsync<SetActiveRequestDto, AdminUserDto>($"api/admin/users/{employeeId}/activation", request, ct);

    public Task<ApiResult<AdminUserDto>> SetUserRolesAsync(Guid employeeId, SetUserRolesRequestDto request, CancellationToken ct) =>
        PutAsync<SetUserRolesRequestDto, AdminUserDto>($"api/admin/users/{employeeId}/roles", request, ct);

    public Task<ApiResult<AdminUserDto>> AddUserDepartmentAsync(Guid employeeId, AddDepartmentMembershipRequestDto request, CancellationToken ct) =>
        PostAsync<AddDepartmentMembershipRequestDto, AdminUserDto>($"api/admin/users/{employeeId}/departments", request, ct);

    public Task<ApiResult<AdminUserDto>> SetUserAccessibleDepartmentsAsync(Guid employeeId, SetAccessibleDepartmentsRequestDto request, CancellationToken ct) =>
        PutAsync<SetAccessibleDepartmentsRequestDto, AdminUserDto>($"api/admin/users/{employeeId}/departments", request, ct);

    public Task<ApiResult<AdminUserDto>> RemoveUserDepartmentAsync(Guid employeeId, int departmentId, CancellationToken ct) =>
        DeleteAsync<AdminUserDto>($"api/admin/users/{employeeId}/departments/{departmentId}", ct);

    /// <summary>Administrative password reset — 204 on success; 422 carries Identity's policy reasons in <c>Detail</c>.</summary>
    public Task<ApiResult> ResetUserPasswordAsync(Guid employeeId, ResetUserPasswordRequestDto request, CancellationToken ct) =>
        PostAsync($"api/admin/users/{employeeId}/password", request, ct);

    public Task<ApiResult<PasswordPolicyDto>> GetPasswordPolicyAsync(CancellationToken ct) =>
        GetAsync<PasswordPolicyDto>("api/admin/users/password-policy", ct);

    public Task<ApiResult<UserPermissionsDto>> GetUserPermissionsAsync(Guid employeeId, CancellationToken ct) =>
        GetAsync<UserPermissionsDto>($"api/admin/users/{employeeId}/permissions", ct);

    public Task<ApiResult<UserPermissionsDto>> SetUserPermissionsAsync(Guid employeeId, SetUserPermissionsRequestDto request, CancellationToken ct) =>
        PutAsync<SetUserPermissionsRequestDto, UserPermissionsDto>($"api/admin/users/{employeeId}/permissions", request, ct);

    public Task<ApiResult<AdminUserDto>> UnlockUserAsync(Guid employeeId, CancellationToken ct) =>
        PostAsync<object, AdminUserDto>($"api/admin/users/{employeeId}/unlock", new { }, ct);

    public Task<ApiResult<IReadOnlyCollection<RoleDto>>> GetRolesAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyCollection<RoleDto>>("api/roles", ct);

    // ---- channels ----
    public Task<ApiResult<IReadOnlyList<AdminChannelDto>>> GetChannelsAsync(bool includeInactive, CancellationToken ct) =>
        GetAsync<IReadOnlyList<AdminChannelDto>>($"api/admin/channels?includeInactive={(includeInactive ? "true" : "false")}", ct);

    public Task<ApiResult<AdminChannelDto>> GetChannelAsync(byte channelId, CancellationToken ct) =>
        GetAsync<AdminChannelDto>($"api/admin/channels/{channelId}", ct);

    public Task<ApiResult<AdminChannelDto>> CreateChannelAsync(SaveChannelRequestDto request, CancellationToken ct) =>
        PostAsync<SaveChannelRequestDto, AdminChannelDto>("api/admin/channels", request, ct);

    public Task<ApiResult<AdminChannelDto>> UpdateChannelAsync(byte channelId, SaveChannelRequestDto request, CancellationToken ct) =>
        PutAsync<SaveChannelRequestDto, AdminChannelDto>($"api/admin/channels/{channelId}", request, ct);

    public Task<ApiResult<AdminChannelDto>> SetChannelActivationAsync(byte channelId, SetActiveRequestDto request, CancellationToken ct) =>
        PatchAsync<SetActiveRequestDto, AdminChannelDto>($"api/admin/channels/{channelId}/activation", request, ct);

    // ---- departments ----
    public Task<ApiResult<IReadOnlyList<AdminDepartmentDto>>> GetDepartmentsAsync(bool includeInactive, CancellationToken ct) =>
        GetAsync<IReadOnlyList<AdminDepartmentDto>>($"api/admin/departments?includeInactive={(includeInactive ? "true" : "false")}", ct);

    public Task<ApiResult<AdminDepartmentDetailDto>> GetDepartmentAsync(int departmentId, CancellationToken ct) =>
        GetAsync<AdminDepartmentDetailDto>($"api/admin/departments/{departmentId}", ct);

    public Task<ApiResult<AdminDepartmentDetailDto>> CreateDepartmentAsync(SaveDepartmentRequestDto request, CancellationToken ct) =>
        PostAsync<SaveDepartmentRequestDto, AdminDepartmentDetailDto>("api/admin/departments", request, ct);

    public Task<ApiResult<AdminDepartmentDetailDto>> UpdateDepartmentAsync(int departmentId, SaveDepartmentRequestDto request, CancellationToken ct) =>
        PutAsync<SaveDepartmentRequestDto, AdminDepartmentDetailDto>($"api/admin/departments/{departmentId}", request, ct);

    public Task<ApiResult<AdminDepartmentDetailDto>> SetDepartmentActivationAsync(int departmentId, SetActiveRequestDto request, CancellationToken ct) =>
        PatchAsync<SetActiveRequestDto, AdminDepartmentDetailDto>($"api/admin/departments/{departmentId}/activation", request, ct);

    public Task<ApiResult<AdminDepartmentDetailDto>> AddDepartmentMemberAsync(int departmentId, AddDepartmentMemberRequestDto request, CancellationToken ct) =>
        PostAsync<AddDepartmentMemberRequestDto, AdminDepartmentDetailDto>($"api/admin/departments/{departmentId}/members", request, ct);

    public Task<ApiResult<AdminDepartmentDetailDto>> RemoveDepartmentMemberAsync(int departmentId, Guid employeeId, CancellationToken ct) =>
        DeleteAsync<AdminDepartmentDetailDto>($"api/admin/departments/{departmentId}/members/{employeeId}", ct);

    // ---- request types ----
    public Task<ApiResult<IReadOnlyList<AdminRequestTypeSummaryDto>>> GetRequestTypesAsync(int? departmentId, bool includeInactive, CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (departmentId is { } id)
        {
            query["departmentId"] = id.ToString();
        }

        query["includeInactive"] = includeInactive ? "true" : "false";
        return GetAsync<IReadOnlyList<AdminRequestTypeSummaryDto>>($"api/admin/request-types?{query}", ct);
    }

    public Task<ApiResult<AdminRequestTypeDetailDto>> GetRequestTypeAsync(int requestTypeId, CancellationToken ct) =>
        GetAsync<AdminRequestTypeDetailDto>($"api/admin/request-types/{requestTypeId}", ct);

    public Task<ApiResult<AdminRequestTypeDetailDto>> CreateRequestTypeAsync(SaveRequestTypeRequestDto request, CancellationToken ct) =>
        PostAsync<SaveRequestTypeRequestDto, AdminRequestTypeDetailDto>("api/admin/request-types", request, ct);

    public Task<ApiResult<AdminRequestTypeDetailDto>> UpdateRequestTypeAsync(int requestTypeId, SaveRequestTypeRequestDto request, CancellationToken ct) =>
        PutAsync<SaveRequestTypeRequestDto, AdminRequestTypeDetailDto>($"api/admin/request-types/{requestTypeId}", request, ct);

    public Task<ApiResult<AdminRequestTypeDetailDto>> SetRequestTypeActivationAsync(int requestTypeId, SetActiveRequestDto request, CancellationToken ct) =>
        PatchAsync<SetActiveRequestDto, AdminRequestTypeDetailDto>($"api/admin/request-types/{requestTypeId}/activation", request, ct);

    public Task<ApiResult<AdminRequestTypeDetailDto>> SaveAssignmentRuleAsync(int requestTypeId, SaveAssignmentRuleRequestDto request, CancellationToken ct) =>
        PutAsync<SaveAssignmentRuleRequestDto, AdminRequestTypeDetailDto>($"api/admin/request-types/{requestTypeId}/assignment-rule", request, ct);

    public Task<ApiResult<AdminRequestTypeDetailDto>> SaveApprovalRequirementAsync(
        int requestTypeId, ApprovalType approvalType, SaveApprovalRequirementRequestDto request, CancellationToken ct) =>
        PutAsync<SaveApprovalRequirementRequestDto, AdminRequestTypeDetailDto>(
            $"api/admin/request-types/{requestTypeId}/approval-requirements/{approvalType}", request, ct);

    /// <summary>The SLA configuration the calculation applies (per-priority policies, calendar, rules) — read-only reference for the administrator.</summary>
    public Task<ApiResult<SlaConfigurationDto>> GetSlaConfigurationAsync(CancellationToken ct) =>
        GetAsync<SlaConfigurationDto>("api/admin/sla/configuration", ct);

    public Task<ApiResult<AdminRequestTypeDetailDto>> SaveSlaPolicyAsync(int requestTypeId, byte priorityId, SaveSlaPolicyRequestDto request, CancellationToken ct) =>
        PutAsync<SaveSlaPolicyRequestDto, AdminRequestTypeDetailDto>($"api/admin/request-types/{requestTypeId}/sla-policies/{priorityId}", request, ct);

    // ---- workflows ----
    public Task<ApiResult<WorkflowDesignerCatalogDto>> GetWorkflowCatalogAsync(CancellationToken ct) =>
        GetAsync<WorkflowDesignerCatalogDto>("api/admin/workflows/catalog", ct);

    public Task<ApiResult<IReadOnlyList<AdminWorkflowSummaryDto>>> GetWorkflowsAsync(bool includeInactive, CancellationToken ct) =>
        GetAsync<IReadOnlyList<AdminWorkflowSummaryDto>>($"api/admin/workflows?includeInactive={(includeInactive ? "true" : "false")}", ct);

    public Task<ApiResult<AdminWorkflowDetailDto>> GetWorkflowAsync(int workflowId, CancellationToken ct) =>
        GetAsync<AdminWorkflowDetailDto>($"api/admin/workflows/{workflowId}", ct);

    public Task<ApiResult<AdminWorkflowDetailDto>> CreateWorkflowAsync(CreateWorkflowRequestDto request, CancellationToken ct) =>
        PostAsync<CreateWorkflowRequestDto, AdminWorkflowDetailDto>("api/admin/workflows", request, ct);

    public Task<ApiResult<AdminWorkflowDetailDto>> UpdateWorkflowAsync(int workflowId, UpdateWorkflowRequestDto request, CancellationToken ct) =>
        PutAsync<UpdateWorkflowRequestDto, AdminWorkflowDetailDto>($"api/admin/workflows/{workflowId}", request, ct);

    public Task<ApiResult<AdminWorkflowDetailDto>> SetWorkflowActivationAsync(int workflowId, SetActiveRequestDto request, CancellationToken ct) =>
        PatchAsync<SetActiveRequestDto, AdminWorkflowDetailDto>($"api/admin/workflows/{workflowId}/activation", request, ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> CreateWorkflowVersionAsync(int workflowId, CancellationToken ct) =>
        PostAsync<object, WorkflowVersionDetailDto>($"api/admin/workflows/{workflowId}/versions", new { }, ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> GetWorkflowVersionAsync(int versionId, CancellationToken ct) =>
        GetAsync<WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}", ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> UpdateVersionSettingsAsync(int versionId, UpdateVersionSettingsRequestDto request, CancellationToken ct) =>
        PutAsync<UpdateVersionSettingsRequestDto, WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}/settings", request, ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> AddStepAsync(int versionId, SaveStepRequestDto request, CancellationToken ct) =>
        PostAsync<SaveStepRequestDto, WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}/steps", request, ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> UpdateStepAsync(int versionId, int stepId, SaveStepRequestDto request, CancellationToken ct) =>
        PutAsync<SaveStepRequestDto, WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}/steps/{stepId}", request, ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> RemoveStepAsync(int versionId, int stepId, CancellationToken ct) =>
        DeleteAsync<WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}/steps/{stepId}", ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> MoveStepAsync(int versionId, int stepId, MoveStepRequestDto request, CancellationToken ct) =>
        PostAsync<MoveStepRequestDto, WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}/steps/{stepId}/move", request, ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> SetTransitionAsync(int versionId, int stepId, SetTransitionRequestDto request, CancellationToken ct) =>
        PutAsync<SetTransitionRequestDto, WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}/steps/{stepId}/transitions", request, ct);

    public Task<ApiResult<WorkflowVersionDetailDto>> PublishVersionAsync(int versionId, CancellationToken ct) =>
        PostAsync<object, WorkflowVersionDetailDto>($"api/admin/workflows/versions/{versionId}/publish", new { }, ct);

    public Task<ApiResult> DeleteDraftAsync(int versionId, CancellationToken ct) =>
        DeleteAsync($"api/admin/workflows/versions/{versionId}", ct);
}
