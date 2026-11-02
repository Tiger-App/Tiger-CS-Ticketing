using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Roles)]
[RequirePermission(Permissions.AdminUsersView)]
public class RolesController(RoleCatalogAppService roleCatalogAppService) : ControllerBase
{
    /// <summary>The fixed role catalogue. System Administrator only.</summary>
    /// <remarks>Not paged — the MVP role set is fixed. MVP-API-Contracts.md §1.5.</remarks>
    /// <response code="200">Every role, with its high-level description.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<RoleDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var roles = await roleCatalogAppService.ListRolesAsync(cancellationToken);
        return Ok(roles);
    }
}
