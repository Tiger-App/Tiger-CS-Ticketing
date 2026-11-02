using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/categories")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.Categories)]
[NoPermissionRequired("Reference data needed by every signed-in user.")]
public class CategoriesController(CategoryCatalogAppService categoryCatalogAppService) : ControllerBase
{
    /// <summary>
    /// The active Ticket Categories an agent may route a ticket to.
    /// <c>departmentId</c> narrows the list to that Department's own
    /// Categories only; omitted, every active Category across every
    /// Department is returned. A Department with none configured (or an
    /// unknown <c>departmentId</c>) returns an empty list, not an error —
    /// the caller decides how to present that.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<CategoryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int? departmentId, CancellationToken cancellationToken)
    {
        var categories = await categoryCatalogAppService.ListAsync(departmentId, cancellationToken);
        return Ok(categories);
    }
}
