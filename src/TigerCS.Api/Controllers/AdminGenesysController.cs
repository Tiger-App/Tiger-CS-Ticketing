using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Genesys routing configuration — System Administrator only.
///
/// <para>
/// This is where the Genesys Queue → Department mapping is entered — and
/// only that: a queue says which department an inquiry belongs to, never
/// what the customer wants, so there is no queue → category mapping and no
/// default category anywhere. Nothing is seeded:
/// the real Genesys queue ids are not known to this repository, and none is
/// invented — an administrator enters them once the Genesys team supplies
/// them. Mappings are deactivated, never deleted, so an inquiry that arrived
/// under one stays explainable.
/// </para>
/// </summary>
[Route("api/admin/genesys")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminGenesysController(AdminGenesysRoutingAppService genesys) : AdminControllerBase
{
    /// <summary>Every configured Genesys queue → department mapping.</summary>
    [HttpGet("queue-mappings")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<AdminGenesysQueueMappingDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListQueueMappings(
        [FromQuery] bool includeInactive = true, CancellationToken cancellationToken = default) =>
        Ok(await genesys.ListQueueMappingsAsync(includeInactive, cancellationToken));

    /// <summary>Maps a Genesys queue to a department. The queue id must be unique — re-pointing an existing queue is an edit, not a second mapping.</summary>
    [HttpPost("queue-mappings")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminGenesysQueueMappingDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateQueueMapping(
        [FromBody] SaveGenesysQueueMappingRequestDto request, CancellationToken cancellationToken) =>
        FromResult(
            await genesys.CreateQueueMappingAsync(CallerEmployeeId, request, cancellationToken),
            created => Created($"/api/admin/genesys/queue-mappings/{created.GenesysQueueMappingId}", created));

    /// <summary>Re-points a queue at another department, renames it, or activates/deactivates it. The queue id itself is the identity and is never edited.</summary>
    [HttpPut("queue-mappings/{genesysQueueMappingId:int}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminGenesysQueueMappingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateQueueMapping(
        int genesysQueueMappingId, [FromBody] SaveGenesysQueueMappingRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await genesys.UpdateQueueMappingAsync(CallerEmployeeId, genesysQueueMappingId, request, cancellationToken));
}
