using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>Department administration — System Administrator only. Departments are deactivated, never deleted.</summary>
[Route("api/admin/departments")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminDepartmentsController(AdminDepartmentAppService departments, DepartmentCoverageService coverage) : AdminControllerBase
{
    /// <summary>Staffing coverage of every active department: active employees, employees eligible for automatic assignment, and an Unstaffed / NoAssignableEmployee flag. Read-only.</summary>
    [HttpGet("coverage")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<DepartmentCoverageLine>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Coverage(CancellationToken cancellationToken) =>
        Ok(await coverage.GetAsync(cancellationToken));

    /// <summary>Every department with member, request-type and ticket-reference counts (inactive included by default).</summary>
    [HttpGet]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<AdminDepartmentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = true, CancellationToken cancellationToken = default) =>
        Ok(await departments.ListAsync(includeInactive, cancellationToken));

    /// <summary>One department with its members.</summary>
    [HttpGet("{departmentId:int}")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<AdminDepartmentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int departmentId, CancellationToken cancellationToken)
    {
        var department = await departments.GetAsync(departmentId, cancellationToken);
        return department is null ? NotFound() : Ok(department);
    }

    /// <summary>Creates a department (name and code must be unique).</summary>
    [HttpPost]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminDepartmentDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] SaveDepartmentRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await departments.CreateAsync(CallerEmployeeId, request, cancellationToken),
            created => CreatedAtAction(nameof(Get), new { departmentId = created.DepartmentId }, created));

    /// <summary>Renames a department / changes its code; historical tickets keep the same department id.</summary>
    [HttpPut("{departmentId:int}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminDepartmentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int departmentId, [FromBody] SaveDepartmentRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await departments.UpdateAsync(CallerEmployeeId, departmentId, request, cancellationToken));

    /// <summary>Activate/deactivate — an inactive department is never offered for new work and is never deleted.</summary>
    [HttpPatch("{departmentId:int}/activation")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminDepartmentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetActivation(int departmentId, [FromBody] SetActiveRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await departments.SetActivationAsync(CallerEmployeeId, departmentId, request, cancellationToken));

    /// <summary>Adds an existing user to the department using the existing membership model.</summary>
    [HttpPost("{departmentId:int}/members")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminDepartmentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddMember(int departmentId, [FromBody] AddDepartmentMemberRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await departments.AddMemberAsync(CallerEmployeeId, departmentId, request, cancellationToken));

    /// <summary>Ends a member's department membership.</summary>
    [HttpDelete("{departmentId:int}/members/{employeeId:guid}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminDepartmentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMember(int departmentId, Guid employeeId, CancellationToken cancellationToken) =>
        FromResult(await departments.RemoveMemberAsync(CallerEmployeeId, departmentId, employeeId, cancellationToken));
}
