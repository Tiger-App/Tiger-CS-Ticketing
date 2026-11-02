using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>The Request Type directory for operational users — what the New Ticket wizard's Request Type picker reads.</summary>
[ApiController]
[Route("api/request-types")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.RequestTypes)]
[NoPermissionRequired("Reference data needed by every signed-in user.")]
public class RequestTypesController(AdminRequestTypeAppService requestTypes) : ControllerBase
{
    /// <summary>Active request types, filtered by department (the department the ticket's category routes to). Never includes inactive ones.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RequestTypeOptionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int? departmentId, CancellationToken cancellationToken) =>
        Ok(await requestTypes.ListOptionsAsync(departmentId, cancellationToken));
}
