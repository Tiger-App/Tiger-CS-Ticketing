using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The Genesys Campaign History: every campaign submission from TigerCS to Genesys, its records and its outcome. READ ONLY: every route is a GET over data the approval and upload already
/// stored; none uploads, retries, resends, queues, refreshes or calls Genesys or the financial source. Internal routes called server-side by the Web application as the signed-in user.
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Route("api/collections/genesys-history")]
[Tags(OpenApiTags.Collections)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CollectionsGenesysHistoryController(GenesysHistoryService history, GenesysCampaignSyncService sync) : ControllerBase
{
    /// <summary>Submissions, newest first, filtered and paged in the database, with summary figures for the filtered set and the filter options.</summary>
    [HttpGet("submissions")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<GenesysHistoryPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submissions(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? stage, [FromQuery] string? status, [FromQuery] string? mode,
        [FromQuery] Guid? employeeId, [FromQuery] string? list, [FromQuery] string? search, [FromQuery] int? company = null, [FromQuery] int page = 1, [FromQuery] int pageSize = GenesysHistoryService.DefaultPageSize, CancellationToken ct = default) =>
        Caller() is { } caller
            ? Result(await history.ListAsync(caller, new GenesysHistoryFilter(from, to, stage, status, mode, employeeId, list, search, company), page, pageSize, ct))
            : Unauthorized();

    /// <summary>One submission: who approved it, its Genesys batches and their outcomes. "Uploaded" never means the customer was contacted.</summary>
    [HttpGet("submissions/{submissionId:guid}")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<GenesysSubmissionDetailDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submission(Guid submissionId, CancellationToken ct) =>
        Caller() is { } caller ? Result(await history.GetAsync(caller, submissionId, ct)) : Unauthorized();

    /// <summary>The unit records of one submission, paged: one row per unit (a shared phone number is never merged).</summary>
    [HttpGet("submissions/{submissionId:guid}/records")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<GenesysRecordsPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Records(Guid submissionId, [FromQuery] string? result, [FromQuery] string? search, [FromQuery] int page = 1,
        [FromQuery] int pageSize = GenesysHistoryService.DefaultPageSize, CancellationToken ct = default) =>
        Caller() is { } caller ? Result(await history.RecordsAsync(caller, submissionId, result, search, page, pageSize, ct)) : Unauthorized();

    /// <summary>Downloaded "Export for Genesys" files, from the audit trail. Separate from submissions: a download is not an upload.</summary>
    [HttpGet("exports")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<GenesysExportHistoryPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Exports([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1,
        [FromQuery] int pageSize = GenesysHistoryService.DefaultPageSize, CancellationToken ct = default) =>
        Caller() is { } caller ? Result(await history.ExportsAsync(caller, from, to, page, pageSize, ct)) : Unauthorized();

    /// <summary>
    /// Manual "Sync from Genesys" for the given campaigns (at most 25). READ ONLY towards Genesys: it reads interactions and message statuses and stores the confirmed result locally; it uploads,
    /// resends and changes nothing in Genesys, and there is no scheduled equivalent. Needs the send right (it spends Genesys API quota).
    /// </summary>
    [HttpPost("sync")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<GenesysSyncResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Sync([FromBody] GenesysSyncRequest request, CancellationToken ct) =>
        Caller() is { } caller ? Result(await sync.SyncAsync(caller, request.SubmissionIds ?? [], request.Force, ct)) : Unauthorized();

    private CollectionsCaller? Caller() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? new(id, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            User.FindAll(TigerCsClaimTypes.DepartmentId).Select(c => int.TryParse(c.Value, out var d) ? d : (int?)null).OfType<int>().ToArray())
        : null;

    private IActionResult Result<T>(CollectionsResult<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        var (status, code) = result.Outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.NotFound => (404, "NotFound"),
            _ => (503, "FinanceUnavailable")
        };
        var response = Problem(statusCode: status, title: code, detail: result.Detail ?? code, type: $"https://tigercs.internal/problems/collections/{code}");
        if (response.Value is ProblemDetails problem) problem.Extensions["code"] = code;
        return response;
    }
}

/// <param name="SubmissionIds">The campaigns (submission ids) to sync.</param>
/// <param name="Force">True: read Genesys again even for a campaign whose contacts are all confirmed.</param>
public sealed record GenesysSyncRequest(IReadOnlyList<Guid>? SubmissionIds, bool Force = false);
