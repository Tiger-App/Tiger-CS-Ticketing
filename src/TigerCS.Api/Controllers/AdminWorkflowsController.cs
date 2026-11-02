using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The Workflow Designer's API — System Administrator only. Logical
/// workflows, their versions, and the Draft-only editing operations; a
/// Published or Historical version answers every edit with 409.
/// </summary>
[Route("api/admin/workflows")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminWorkflowsController(AdminWorkflowAppService workflows) : AdminControllerBase
{
    /// <summary>The controlled step types and approval types the designer may offer.</summary>
    [HttpGet("catalog")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<WorkflowDesignerCatalogDto>(StatusCodes.Status200OK)]
    public IActionResult Catalog() => Ok(AdminWorkflowAppService.Catalog());

    /// <summary>Every workflow with its active and draft version numbers and usage counts.</summary>
    [HttpGet]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<AdminWorkflowSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = true, CancellationToken cancellationToken = default) =>
        Ok(await workflows.ListAsync(includeInactive, cancellationToken));

    /// <summary>One workflow with every version (Draft / Active / Historical, ticket counts) and the request types using it.</summary>
    [HttpGet("{workflowId:int}")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<AdminWorkflowDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int workflowId, CancellationToken cancellationToken)
    {
        var workflow = await workflows.GetAsync(workflowId, cancellationToken);
        return workflow is null ? NotFound() : Ok(workflow);
    }

    /// <summary>Creates a workflow with a Draft version 1 pre-filled with the standard skeleton.</summary>
    [HttpPost]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminWorkflowDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateWorkflowRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.CreateAsync(CallerEmployeeId, request, cancellationToken),
            created => CreatedAtAction(nameof(Get), new { workflowId = created.WorkflowId }, created));

    /// <summary>Renames a workflow or changes its description.</summary>
    [HttpPut("{workflowId:int}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminWorkflowDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int workflowId, [FromBody] UpdateWorkflowRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.UpdateAsync(CallerEmployeeId, workflowId, request, cancellationToken));

    /// <summary>Activate/deactivate a workflow; refused while active request types still use it.</summary>
    [HttpPatch("{workflowId:int}/activation")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminWorkflowDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetActivation(int workflowId, [FromBody] SetActiveRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.SetActivationAsync(CallerEmployeeId, workflowId, request, cancellationToken));

    /// <summary>"Create New Version": a Draft copied from the Published version. 409 while a Draft already exists.</summary>
    [HttpPost("{workflowId:int}/versions")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateVersion(int workflowId, CancellationToken cancellationToken) =>
        FromResult(await workflows.CreateVersionAsync(CallerEmployeeId, workflowId, cancellationToken),
            created => CreatedAtAction(nameof(GetVersion), new { versionId = created.WorkflowTemplateId }, created));

    /// <summary>One version with its steps, branches and (for a Draft) the current validation summary.</summary>
    [HttpGet("versions/{versionId:int}")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVersion(int versionId, CancellationToken cancellationToken)
    {
        var version = await workflows.GetVersionAsync(versionId, cancellationToken);
        return version is null ? NotFound() : Ok(version);
    }

    /// <summary>Draft only: name, description and capability flags of the version.</summary>
    [HttpPut("versions/{versionId:int}/settings")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateVersionSettings(int versionId, [FromBody] UpdateVersionSettingsRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.UpdateVersionSettingsAsync(CallerEmployeeId, versionId, request, cancellationToken));

    /// <summary>Draft only: appends a step of a controlled type.</summary>
    [HttpPost("versions/{versionId:int}/steps")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddStep(int versionId, [FromBody] SaveStepRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.AddStepAsync(CallerEmployeeId, versionId, request, cancellationToken));

    /// <summary>Draft only: edits a step's name, type, optionality and approval type.</summary>
    [HttpPut("versions/{versionId:int}/steps/{stepId:int}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateStep(int versionId, int stepId, [FromBody] SaveStepRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.UpdateStepAsync(CallerEmployeeId, versionId, stepId, request, cancellationToken));

    /// <summary>Draft only: deletes a step and every branch pointing at it, renumbering the rest.</summary>
    [HttpDelete("versions/{versionId:int}/steps/{stepId:int}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveStep(int versionId, int stepId, CancellationToken cancellationToken) =>
        FromResult(await workflows.RemoveStepAsync(CallerEmployeeId, versionId, stepId, cancellationToken));

    /// <summary>Draft only: moves a step one position up or down.</summary>
    [HttpPost("versions/{versionId:int}/steps/{stepId:int}/move")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MoveStep(int versionId, int stepId, [FromBody] MoveStepRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.MoveStepAsync(CallerEmployeeId, versionId, stepId, request, cancellationToken));

    /// <summary>Configures (or clears, with a null target) where an approval step's Approved/Rejected outcome leads.</summary>
    [HttpPut("versions/{versionId:int}/steps/{stepId:int}/transitions")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetTransition(int versionId, int stepId, [FromBody] SetTransitionRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await workflows.SetTransitionAsync(CallerEmployeeId, versionId, stepId, request, cancellationToken));

    /// <summary>Validates and publishes a Draft; the previously Published version becomes Historical. Existing tickets are untouched.</summary>
    [HttpPost("versions/{versionId:int}/publish")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<WorkflowVersionDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(int versionId, CancellationToken cancellationToken) =>
        FromResult(await workflows.PublishAsync(CallerEmployeeId, versionId, cancellationToken));

    /// <summary>Deletes an unreferenced Draft. Published/Historical versions answer 409 — they are never deleted.</summary>
    [HttpDelete("versions/{versionId:int}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteDraft(int versionId, CancellationToken cancellationToken) =>
        FromResult(await workflows.DeleteDraftAsync(CallerEmployeeId, versionId, cancellationToken));
}
