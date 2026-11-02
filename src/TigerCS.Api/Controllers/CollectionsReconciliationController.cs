using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Reconciliation of the receivables snapshot with a fresh read of the configured PACT procedure. Reading the result is cheap and never contacts PACT; a run is started only by
/// an explicit POST (one at a time, with a cooldown), so page views can never trigger one. Internal routes called server-side by the Web application as the signed-in user.
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Route("api/collections/reconciliation")]
[Tags(OpenApiTags.Collections)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CollectionsReconciliationController(FinancialReconciliationService service) : ControllerBase
{
    public sealed record StartRequest(int? CompanyId);

    /// <summary>The source gate (why the financial source is or is not reconciled), the latest run with its per-unit differences, and recent runs.</summary>
    [HttpGet]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<ReconciliationOverviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview(CancellationToken ct) =>
        Caller() is { } caller ? Result(await service.OverviewAsync(caller, ct)) : Unauthorized();

    /// <summary>One run by id (poll it after starting one).</summary>
    [HttpGet("runs/{runId:guid}")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<ReconciliationRunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Run(Guid runId, CancellationToken ct) =>
        Caller() is { } caller ? Result(await service.GetRunAsync(caller, runId, ct)) : Unauthorized();

    /// <summary>Starts a reconciliation in the background. 409 while one is running or was started in the last two minutes.</summary>
    [HttpPost("run")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<ReconciliationRunDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Start([FromBody] StartRequest? request, CancellationToken ct) =>
        Caller() is { } caller ? Result(await service.StartAsync(caller, request?.CompanyId, ct)) : Unauthorized();

    private CollectionsCaller? Caller() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? new(id, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            User.FindAll(TigerCsClaimTypes.DepartmentId).Select(c => int.TryParse(c.Value, out var d) ? d : (int?)null).OfType<int>().ToArray())
        : null;

    private IActionResult Result<T>(CollectionsResult<T> result)
    {
        if (result.IsSuccess) return result.Outcome == CollectionsOutcome.Accepted ? Accepted(result.Value) : Ok(result.Value);
        var (status, code) = result.Outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.Disabled => (503, "Disabled"),
            CollectionsOutcome.NotFound => (404, "NotFound"),
            CollectionsOutcome.Conflict => (409, "Conflict"),
            _ => (503, "FinanceUnavailable")
        };
        var response = Problem(statusCode: status, title: code, detail: result.Detail ?? code, type: $"https://tigercs.internal/problems/collections/{code}");
        if (response.Value is ProblemDetails problem) problem.Extensions["code"] = code;
        return response;
    }
}
