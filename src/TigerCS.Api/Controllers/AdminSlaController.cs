using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// SLA configuration reference — System Administrator only, read-only.
/// Shows the per-priority policies and the business calendar the due-date
/// calculation actually applies, with the implementation facts an
/// administrator needs to read the request-type SLA values correctly.
/// Approved durations, working hours, pause rules and policy precedence are
/// not editable here: they are seeded reference data and change only by a
/// reviewed data change.
/// </summary>
[Route("api/admin/sla")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminSlaController(SlaQueryAppService slaQueryAppService) : AdminControllerBase
{
    /// <summary>The effective SLA configuration: per-priority policies, the active business calendar and the clock/first-response/pause rules in force.</summary>
    [HttpGet("configuration")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<SlaConfigurationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConfiguration(CancellationToken cancellationToken) =>
        Ok(await slaQueryAppService.GetConfigurationAsync(cancellationToken));
}
