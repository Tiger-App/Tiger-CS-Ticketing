using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>Channel administration (Admin → Configuration → Channels) — System Administrator only. Channels are deactivated, never deleted.</summary>
[Route("api/admin/channels")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminChannelsController(AdminChannelAppService channels) : AdminControllerBase
{
    /// <summary>Every channel, ordered by display order then name, with its history reference count.</summary>
    [HttpGet]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<AdminChannelDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = true, CancellationToken cancellationToken = default) =>
        Ok(await channels.ListAsync(includeInactive, cancellationToken));

    /// <summary>One channel.</summary>
    [HttpGet("{channelId:int:range(1,255)}")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<AdminChannelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(byte channelId, CancellationToken cancellationToken)
    {
        var channel = await channels.GetAsync(channelId, cancellationToken);
        return channel is null ? NotFound() : Ok(channel);
    }

    /// <summary>Adds a channel. Code must be unique.</summary>
    [HttpPost]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminChannelDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] SaveChannelRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await channels.CreateAsync(CallerEmployeeId, request, cancellationToken),
            created => CreatedAtAction(nameof(Get), new { channelId = created.ChannelId }, created));

    /// <summary>Edits a channel. Historical intake records and interactions keep referencing it by id, so they display the new name.</summary>
    [HttpPut("{channelId:int:range(1,255)}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminChannelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(byte channelId, [FromBody] SaveChannelRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await channels.UpdateAsync(CallerEmployeeId, channelId, request, cancellationToken));

    /// <summary>Activate/deactivate — channels are never deleted; an inactive channel only disappears from new-ticket selection.</summary>
    [HttpPatch("{channelId:int:range(1,255)}/activation")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<AdminChannelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetActivation(byte channelId, [FromBody] SetActiveRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await channels.SetActivationAsync(CallerEmployeeId, channelId, request, cancellationToken));
}
