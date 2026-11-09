using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Route("api/collections/campaigns")]
[Tags(OpenApiTags.Collections)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CollectionsCampaignsController(CollectionsCampaignAppService service) : ControllerBase
{
    /// <summary>Preview one communication stage, per unit, using current remaining balances from PACT companies 4 and 32. Only instalments due before the end of the current Dubai month are listed (Overdue: before the first day of the month, Due: inside it); <c>minTotal</c> keeps units whose Due + Overdue is greater than it (omit for no minimum). <c>floors</c> (repeat the parameter; 0 = ground floor; omit for all floors) keeps only apartments on those floors, derived from the unit number; <c>paymentStatus</c> is Due, Overdue or Both (default): a single status keeps only that status' instalments in the figures; preview, counts and both CSV exports share it.</summary>
    [HttpGet("preview")]
    [RequirePermission(Permissions.CampaignsView)]
    [ProducesResponseType<CollectionsCampaignPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Preview([FromQuery] string? stage, [FromQuery] DateOnly? businessDate,
        [FromQuery] int? companyId, [FromQuery] string? search, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default,
        [FromQuery] DateOnly? dateFrom = null, [FromQuery] DateOnly? dateTo = null, [FromQuery] int? towerId = null,
        [FromQuery] decimal? minTotal = null, [FromQuery] string? paymentStatus = null, [FromQuery] int[]? towerIds = null,
        [FromQuery] string? sendingMode = null, [FromQuery] int[]? floors = null)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        return Result(await service.PreviewAsync(caller, stage, businessDate, companyId, search, page, pageSize,
            cancellationToken: cancellationToken, dateFrom: dateFrom, dateTo: dateTo, towerId: towerId, minTotal: minTotal, paymentStatus: paymentStatus, towerIds: towerIds, sendingMode: sendingMode, floors: floors));
    }

    /// <summary>Export the full filtered list. Review mode is for internal review; Genesys mode refuses any unresolved row,
    /// an unscheduled or non-current date, and internal legal referrals. CSV is returned in an internal JSON envelope.</summary>
    [HttpGet("export")]
    [RequirePermission(Permissions.CampaignsExport)]
    [ProducesResponseType<CollectionsCampaignExportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Export([FromQuery] string? stage, [FromQuery] string? mode,
        [FromQuery] DateOnly? businessDate, [FromQuery] int? companyId, [FromQuery] string? search,
        CancellationToken cancellationToken = default, [FromQuery] DateOnly? dateFrom = null, [FromQuery] DateOnly? dateTo = null,
        [FromQuery] int? towerId = null, [FromQuery] decimal? minTotal = null, [FromQuery] string? paymentStatus = null, [FromQuery] int[]? towerIds = null,
        [FromQuery] string? sendingMode = null, [FromQuery] int[]? floors = null)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        return Result(await service.ExportAsync(caller, stage, mode, businessDate, companyId, search, cancellationToken, dateFrom, dateTo, towerId, minTotal, paymentStatus, towerIds, sendingMode, floors));
    }

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
            CollectionsOutcome.Disabled => (503, "CollectionsDisabled"),
            _ => (503, "FinanceUnavailable")
        };
        var response = Problem(statusCode: status, title: code, detail: result.Detail ?? code,
            type: $"https://tigercs.internal/problems/collections/{code}");
        if (response.Value is ProblemDetails problem) problem.Extensions["code"] = code;
        return response;
    }
}
