using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>Body of a backfill run. <see cref="DryRun"/> defaults to TRUE: a live run must say so explicitly.</summary>
public sealed record RunCsAgentBackfillRequestDto(bool DryRun = true, int? BatchSize = null, long AfterTicketId = 0, string? BatchId = null);

/// <summary>
/// One-time backfill of active tickets that have no CS Agent - System Administrator only, manual only (nothing schedules it).
/// Preview and dry runs are read-only. A live run additionally needs <c>CsAgentBackfill:Enabled=true</c>.
/// </summary>
[Route("api/admin/cs-agent-backfill")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminCsAgentBackfillController(
    CsAgentBackfillService backfill, CsAgentBackfillRunner runner, IOptionsSnapshot<CsAgentBackfillOptions> options) : AdminControllerBase
{
    /// <summary>Read-only preview: ticket count, current department, proposed CS Agent in the real rotation order, and every exclusion with its reason. Writes nothing and does not advance the cursor.</summary>
    [HttpGet("preview")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<CsAgentBackfillPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview([FromQuery] long afterTicketId = 0, [FromQuery] int? limit = null, CancellationToken cancellationToken = default) =>
        Ok(await backfill.PreviewAsync(Math.Max(0, afterTicketId), limit ?? options.Value.DefaultBatchSize, options.Value, cancellationToken));

    /// <summary>Runs ONE throttled batch (dry run by default). Resume with the returned <c>resumeAfterTicketId</c>; repeat until <c>backlogExhausted</c>.</summary>
    [HttpPost("run")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<CsAgentBackfillRunResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Run([FromBody] RunCsAgentBackfillRequestDto? request, CancellationToken cancellationToken)
    {
        request ??= new RunCsAgentBackfillRequestDto();
        var result = await runner.RunAsync(
            new CsAgentBackfillRunRequest(request.DryRun, request.BatchSize, request.AfterTicketId, request.BatchId), cancellationToken);

        return result.Status switch
        {
            CsAgentBackfillRunStatus.Disabled => Problem(
                type: "https://tigercs.internal/problems/cs-agent-backfill-disabled", title: "Live backfill is disabled",
                detail: "Set CsAgentBackfill:Enabled=true to run live. Dry runs are always allowed.", statusCode: StatusCodes.Status409Conflict),
            CsAgentBackfillRunStatus.Busy => Problem(
                type: "https://tigercs.internal/problems/cs-agent-backfill-busy", title: "Another run is in progress",
                detail: "A backfill or reconciliation run holds the lock. Nothing was done.", statusCode: StatusCodes.Status409Conflict),
            CsAgentBackfillRunStatus.NoEligiblePool => Problem(
                type: "https://tigercs.internal/problems/no-eligible-cs-agent", title: "No eligible CS Agent",
                detail: "The shared pool is empty. Nothing was written.", statusCode: StatusCodes.Status409Conflict),
            _ => Ok(result)
        };
    }
}
