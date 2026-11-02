using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The channel directory — what the Create Ticket wizard's Channel picker
/// reads, so no channel list is hard-coded in any UI. Any authenticated
/// staff member may view; management is under <c>api/admin/channels</c>.
/// </summary>
[ApiController]
[Route("api/channels")]
[Tags(OpenApiTags.Channels)]
[NoPermissionRequired("Reference data needed by every signed-in user.")]
public class ChannelsController(ChannelDirectoryAppService channelDirectory) : ControllerBase
{
    /// <summary>The configured channels, ordered by display order then name.</summary>
    /// <param name="activeOnly">When true (the default), excludes deactivated channels — a new-ticket picker never offers those; false is for naming a channel a historical record references.</param>
    /// <response code="200">The channel directory.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ChannelDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool activeOnly = true, CancellationToken cancellationToken = default) =>
        Ok(await channelDirectory.ListAsync(activeOnly, cancellationToken));
}
