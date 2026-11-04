using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>Body of a backfill run. <see cref="DryRun"/> defaults to TRUE: a live run must say so explicitly.</summary>
public sealed record RunDefaultClassificationBackfillRequestDto(bool DryRun = true, int? BatchSize = null, long AfterTicketId = 0);

/// <summary>
/// Historical backfill of Customer Services / General onto existing tickets without a valid classification - System Administrator only, manual
/// only (nothing schedules it). Preview and dry runs are read-only. A live run additionally needs <c>DefaultClassificationBackfill:Enabled=true</c>,
/// which is false in every shipped configuration until the run is separately approved.
/// </summary>
[Route("api/admin/default-classification-backfill")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminDefaultClassificationBackfillController(
    DefaultClassificationBackfillService backfill, IOptionsSnapshot<DefaultClassificationBackfillOptions> options) : AdminControllerBase
{
    /// <summary>Read-only preview: tickets without a valid classification by state and status, what is excluded and why. Writes nothing.</summary>
    [HttpGet("preview")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<DefaultClassificationBackfillPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview([FromQuery] long afterTicketId = 0, [FromQuery] int? limit = null, CancellationToken cancellationToken = default) =>
        Ok(await backfill.PreviewAsync(Math.Max(0, afterTicketId), limit ?? options.Value.DefaultBatchSize, options.Value, cancellationToken));

    /// <summary>Runs ONE batch (dry run by default). Resume with the returned <c>resumeAfterTicketId</c>; repeat until <c>backlogExhausted</c>.</summary>
    [HttpPost("run")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<DefaultClassificationBackfillRunResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Run([FromBody] RunDefaultClassificationBackfillRequestDto? request, CancellationToken cancellationToken)
    {
        request ??= new RunDefaultClassificationBackfillRequestDto();
        var result = await backfill.RunAsync(
            Math.Max(0, request.AfterTicketId), request.BatchSize ?? options.Value.DefaultBatchSize, request.DryRun, options.Value, cancellationToken);

        return result.Status switch
        {
            DefaultClassificationBackfillRunStatus.Disabled => Problem(
                type: "https://tigercs.internal/problems/default-classification-backfill-disabled", title: "Live backfill is disabled",
                detail: result.Detail, statusCode: StatusCodes.Status409Conflict),
            DefaultClassificationBackfillRunStatus.ConfigurationMissing => Problem(
                type: "https://tigercs.internal/problems/default-classification-not-configured", title: "Customer Services / General is not configured",
                detail: result.Detail, statusCode: StatusCodes.Status409Conflict),
            _ => Ok(result)
        };
    }
}
