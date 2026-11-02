using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>Request Type administration — System Administrator only. Request types are deactivated, never deleted.</summary>
[Route("api/admin/request-types")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminRequestTypesController(AdminRequestTypeAppService requestTypes) : AdminControllerBase
{
    /// <summary>Every request type, optionally scoped to one department, with its workflow, assignment summary and ticket count.</summary>
    [HttpGet]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<AdminRequestTypeSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int? departmentId, [FromQuery] bool includeInactive = true, CancellationToken cancellationToken = default) =>
        Ok(await requestTypes.ListAsync(departmentId, includeInactive, cancellationToken));

    /// <summary>One request type with its workflow link, assignment rule, approval requirements and SLA rows.</summary>
    [HttpGet("{requestTypeId:int}")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<AdminRequestTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int requestTypeId, CancellationToken cancellationToken)
    {
        var requestType = await requestTypes.GetAsync(requestTypeId, cancellationToken);
        return requestType is null ? NotFound() : Ok(requestType);
    }

    /// <summary>Creates a request type in a department, following a workflow that already has a published version.</summary>
    [HttpPost]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminRequestTypeDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] SaveRequestTypeRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await requestTypes.CreateAsync(CallerEmployeeId, request, cancellationToken),
            created => CreatedAtAction(nameof(Get), new { requestTypeId = created.RequestTypeId }, created));

    /// <summary>Edits a request type; the department is immutable once tickets reference the request type.</summary>
    [HttpPut("{requestTypeId:int}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminRequestTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(int requestTypeId, [FromBody] SaveRequestTypeRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await requestTypes.UpdateAsync(CallerEmployeeId, requestTypeId, request, cancellationToken));

    /// <summary>Activate/deactivate — request types are never deleted; historical tickets keep them.</summary>
    [HttpPatch("{requestTypeId:int}/activation")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminRequestTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetActivation(int requestTypeId, [FromBody] SetActiveRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await requestTypes.SetActivationAsync(CallerEmployeeId, requestTypeId, request, cancellationToken));

    /// <summary>Replaces the request type's assignment configuration (Department Queue / Specific Employee / Team).</summary>
    [HttpPut("{requestTypeId:int}/assignment-rule")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminRequestTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveAssignmentRule(int requestTypeId, [FromBody] SaveAssignmentRuleRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await requestTypes.SaveAssignmentRuleAsync(CallerEmployeeId, requestTypeId, request, cancellationToken));

    /// <summary>Creates or edits the requirement for one controlled approval type.</summary>
    [HttpPut("{requestTypeId:int}/approval-requirements/{approvalType}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminRequestTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveApprovalRequirement(
        int requestTypeId, ApprovalType approvalType, [FromBody] SaveApprovalRequirementRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await requestTypes.SaveApprovalRequirementAsync(CallerEmployeeId, requestTypeId, approvalType, request, cancellationToken));

    /// <summary>Creates or edits the SLA row for one (request type, priority) pair — existing confirmed values only.</summary>
    [HttpPut("{requestTypeId:int}/sla-policies/{priorityId}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminRequestTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveSlaPolicy(
        int requestTypeId, byte priorityId, [FromBody] SaveSlaPolicyRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await requestTypes.SaveSlaPolicyAsync(CallerEmployeeId, requestTypeId, priorityId, request, cancellationToken));
}
