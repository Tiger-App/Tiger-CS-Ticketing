using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The Project / Unit options of New Ticket Step 2: the projects and units Tiger Ticketing already holds (from its tickets — it keeps
/// no project table). Read-only; creates and changes nothing. Scoped like the wizard's other steps (CS Agent / CS Supervisor with
/// <c>Tickets.Create</c>).
/// </summary>
[ApiController]
[Route("api/projects")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.CustomerLookup)]
[RequirePermission(Permissions.TicketsCreate)]
public class ProjectsController(ProjectCatalogAppService projectCatalogAppService) : ControllerBase
{
    /// <summary>Every known project, one per name, alphabetical.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProjectOptionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await projectCatalogAppService.ListProjectsAsync(cancellationToken));

    /// <summary>The unit numbers recorded for one project; an empty list when none are known.</summary>
    [HttpGet("units")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Units([FromQuery] string? project, CancellationToken cancellationToken) =>
        Ok(await projectCatalogAppService.ListUnitNumbersAsync(project, cancellationToken));
}
