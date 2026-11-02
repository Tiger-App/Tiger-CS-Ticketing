using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/departments")]
[Tags(OpenApiTags.Departments)]
[NoPermissionRequired("Reference data needed by every signed-in user.")]
public class DepartmentsController(
    DepartmentUserAppService departmentUserAppService,
    DepartmentDirectoryAppService departmentDirectoryAppService) : ControllerBase
{
    /// <summary>
    /// The Department directory — every Department a Department dropdown may
    /// offer, so no UI ever asks anyone to type a raw <c>DepartmentId</c>.
    /// Any authenticated staff member may view.
    /// </summary>
    /// <param name="activeOnly">When true (the default), excludes deactivated departments.</param>
    /// <response code="200">The Department directory, ordered by name.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<DepartmentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        var departments = await departmentDirectoryAppService.ListAsync(activeOnly, cancellationToken);
        return Ok(departments);
    }

    /// <summary>List the users assigned to a department. Any authenticated staff member may view.</summary>
    /// <remarks>MVP-API-Contracts.md §1.4.</remarks>
    /// <param name="departmentId">The department to list.</param>
    /// <param name="activeOnly">When true (the default), excludes deactivated users.</param>
    /// <param name="page">1-based page number. Values below 1 are raised to 1.</param>
    /// <param name="pageSize">Page size. Clamped to the range 1-100.</param>
    /// <response code="200">A page of department members, with the total count.</response>
    /// <response code="404">No such department.</response>
    [HttpGet("{departmentId:int}/users")]
    [ProducesResponseType<PagedResultDto<DepartmentUserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUsers(
        int departmentId,
        [FromQuery] bool activeOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);

        var result = await departmentUserAppService.ListAsync(departmentId, activeOnly, page, pageSize, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
