using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Collections review and approval: filtered stored records, explicit refresh jobs, approval of an exact list and upload to
/// Genesys outbound contact lists. Reads never call the financial source; only the refresh job does. Internal routes called
/// server-side by the Web application as the signed-in user.
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Route("api/collections/review")]
[Tags(OpenApiTags.Collections)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CollectionsReviewController(ReviewQueryService query, ReviewRefreshService refresh, DispatchService dispatch, DispatchAdminReportService adminReport) : ControllerBase
{
    public sealed record StartRefreshRequest(int? CompanyId);

    /// <summary>The refresh run in progress, or the latest published one. Poll this to show progress.</summary>
    [HttpGet("runs/current")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CurrentRun(CancellationToken ct) =>
        Caller() is { } caller ? Result(await query.GetCurrentRunAsync(caller, ct)) : Unauthorized();

    /// <summary>One refresh run by id, with its progress and any failure.</summary>
    [HttpGet("runs/{runId:long}")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Run(long runId, CancellationToken ct) =>
        Caller() is { } caller ? Result(await query.GetRunAsync(caller, runId, ct)) : Unauthorized();

    /// <summary>Queues a refresh job. Only one runs at a time; a second request returns the run already in progress.</summary>
    [HttpPost("refresh")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Refresh([FromBody] StartRefreshRequest? request, CancellationToken ct) =>
        Caller() is { } caller ? Result(await refresh.StartAsync(caller, request?.CompanyId, ct)) : Unauthorized();

    /// <summary>Filtered, counted, server-side paged records of the current review data.</summary>
    [HttpGet("records")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<ReviewPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Records([FromQuery] ReviewFilter filter, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Caller() is { } caller ? Result(await query.QueryAsync(caller, filter, page, pageSize, ct)) : Unauthorized();

    /// <summary>Every validation reason with its plain-language explanation (for the reason filter).</summary>
    [HttpGet("reasons")]
    [RequirePermission(Permissions.CollectionsView)]
    public IActionResult Reasons() => Ok(ReviewReasons.All.Select(r => new ReviewReasonDto(r.Code, r.Kind.ToString(), r.Explanation)));

    /// <summary>Phone numbers held back because they would be called repeatedly (several units, or several reminder types for one unit), with customers and unit counts.</summary>
    [HttpGet("overlaps")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<OverlapPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Overlaps([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Caller() is { } caller ? Result(await query.OverlapsAsync(caller, page, pageSize, ct)) : Unauthorized();

    /// <summary>The exact count, totals by currency and contact list of a selection, with the fingerprint to confirm.</summary>
    [HttpPost("selection/summary")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<SelectionSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromBody] SelectionRequest request, CancellationToken ct) =>
        Caller() is { } caller ? Result(await query.SummarizeAsync(caller, request, ct)) : Unauthorized();

    /// <summary>Approves the exact list (count and fingerprint must match) and queues the revalidation-and-upload job.</summary>
    [HttpPost("dispatches")]
    [RequirePermission(Permissions.CollectionsSend)]
    [ProducesResponseType<DispatchDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Confirm([FromBody] ConfirmDispatchRequest request, [FromHeader(Name = "Idempotency-Key")] string? headerKey, CancellationToken ct)
    {
        if (Caller() is not { } caller) return Unauthorized();
        var effective = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? headerKey ?? "" : request.IdempotencyKey;
        return Result(await dispatch.ConfirmAsync(caller, request with { IdempotencyKey = effective }, ct));
    }

    /// <summary>The latest approved sends with their batch results.</summary>
    [HttpGet("dispatches")]
    [RequirePermission(Permissions.CollectionsView)]
    public async Task<IActionResult> Dispatches(CancellationToken ct) =>
        Caller() is { } caller ? Result(await dispatch.ListAsync(caller, ct)) : Unauthorized();

    /// <summary>One approved send: initiator, totals, per-batch Genesys results. "Uploaded" never means the customer was contacted.</summary>
    [HttpGet("dispatches/{dispatchId:guid}")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<DispatchDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dispatch(Guid dispatchId, CancellationToken ct) =>
        Caller() is { } caller ? Result(await dispatch.GetAsync(caller, dispatchId, ct)) : Unauthorized();

    /// <summary>The contacts of one send exactly as uploaded (the six Genesys contact-list columns) as a CSV file, for comparison with the Genesys list.</summary>
    [HttpGet("dispatches/{dispatchId:guid}/contacts")]
    [RequirePermission(Permissions.CollectionsView)]
    [Produces("text/csv")]
    public async Task<IActionResult> DispatchContacts(Guid dispatchId, CancellationToken ct)
    {
        if (Caller() is not { } caller) return Unauthorized();
        var result = await dispatch.ExportContactsAsync(caller, dispatchId, ct);
        return result.IsSuccess ? File(System.Text.Encoding.UTF8.GetBytes(result.Value!), "text/csv; charset=utf-8", $"genesys-contacts-{dispatchId:N}.csv") : Result(result);
    }

    /// <summary>Retries only the administrator report of a finished send. Never repeats the Genesys upload.</summary>
    [HttpPost("dispatches/{dispatchId:guid}/admin-email/retry")]
    [RequirePermission(Permissions.CollectionsSend)]
    [ProducesResponseType<DispatchDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RetryAdminEmail(Guid dispatchId, CancellationToken ct) =>
        Caller() is { } caller ? Result(await adminReport.RetryAsync(caller, dispatchId, ct)) : Unauthorized();

    /// <summary>Starts the paid-after-upload suppression sweep now (it also runs on a schedule when enabled).</summary>
    [HttpPost("suppression/sweep")]
    [RequirePermission(Permissions.CollectionsEdit)]
    public async Task<IActionResult> SweepSuppression([FromServices] CollectionsAuthorizationService authorization, [FromServices] IReviewJobScheduler scheduler, CancellationToken ct)
    {
        if (Caller() is not { } caller) return Unauthorized();
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders) return Result(CollectionsResult<object>.Fail(CollectionsOutcome.Forbidden));
        scheduler.EnqueueSuppressionSweep();
        return Accepted();
    }

    /// <summary>Cancels an approved send that has not started yet and releases its records.</summary>
    [HttpPost("dispatches/{dispatchId:guid}/cancel")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<DispatchDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid dispatchId, CancellationToken ct) =>
        Caller() is { } caller ? Result(await dispatch.CancelAsync(caller, dispatchId, ct)) : Unauthorized();

    /// <summary>Resolves a batch with an unknown outcome after someone checked the Genesys contact list.</summary>
    [HttpPost("dispatches/{dispatchId:guid}/batches/{batchId:long}/reconcile")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<DispatchDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reconcile(Guid dispatchId, long batchId, [FromBody] ReconcileBatchRequest request, CancellationToken ct) =>
        Caller() is { } caller ? Result(await dispatch.ReconcileAsync(caller, dispatchId, batchId, request, ct)) : Unauthorized();

    private CollectionsCaller? Caller() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? new(id, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            User.FindAll(TigerCsClaimTypes.DepartmentId).Select(c => int.TryParse(c.Value, out var d) ? d : (int?)null).OfType<int>().ToArray())
        : null;

    private IActionResult Result<T>(CollectionsResult<T> result)
    {
        if (result.IsSuccess)
            return result.Outcome == CollectionsOutcome.Accepted ? Accepted(result.Value) : Ok(result.Value);
        var (status, code) = result.Outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.Disabled => (503, "Disabled"),
            CollectionsOutcome.NotFound => (404, "NotFound"),
            CollectionsOutcome.ReviewRequired => (409, "ReviewRequired"),
            CollectionsOutcome.DuplicateDispatch => (409, "DuplicateDispatch"),
            CollectionsOutcome.IdempotencyConflict => (409, "IdempotencyConflict"),
            _ => (503, "FinanceUnavailable")
        };
        var response = Problem(statusCode: status, title: code, detail: result.Detail ?? code,
            type: $"https://tigercs.internal/problems/collections/{code}");
        if (response.Value is ProblemDetails problem) problem.Extensions["code"] = code;
        return response;
    }
}
