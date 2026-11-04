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
[Route("api/collections/receivables/customers")]
[Tags(OpenApiTags.Collections)]
public sealed class PactReceivablesController(PactReceivableCustomersAppService service, PactInstalmentsAppService instalments) : ControllerBase
{
    /// <summary>Lists due and overdue PACT customers for companies 4 and 32, including customers without tickets.</summary>
    /// <param name="companyId">Optional company filter; only 4 or 32 are accepted.</param>
    /// <param name="status">Optional view: <c>all</c> (default), <c>due</c> or <c>overdue</c>.</param>
    /// <param name="search">Optional free-text filter (customer name, unit or phone), up to 200 characters.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size, 1 to 100.</param>
    /// <param name="year">Reporting year; supply together with <c>month</c>. Defaults to the current Dubai month when both are omitted.</param>
    /// <param name="month">Reporting month, 1-12.</param>
    /// <param name="towerId">Optional tower (local <c>CollectionsTowers.TowerId</c>); the company is resolved from the tower. Omit for all towers.</param>
    /// <param name="dateFrom">First instalment due date included. Defaults to 1 January of the report year.</param>
    /// <param name="dateTo">Last instalment due date included (whole day). Defaults to the end of the reporting month.</param>
    /// <param name="minAmount">Minimum outstanding amount: an instalment counts only when its remaining unpaid amount is &gt;= this value. Default 100.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<PactReceivableCustomersDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List([FromQuery] int? companyId, [FromQuery] string? status,
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] int? year = null, [FromQuery] int? month = null, CancellationToken cancellationToken = default,
        [FromQuery] int? towerId = null, [FromQuery] DateOnly? dateFrom = null, [FromQuery] DateOnly? dateTo = null,
        [FromQuery] decimal? minAmount = null)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await service.ListAsync(caller, companyId, status, search, page, pageSize, year, month, cancellationToken, towerId, dateFrom, dateTo, minAmount);
        if (result.IsSuccess) return Ok(result.Value);
        return Failure(result.Outcome, result.Detail);
    }

    /// <summary>
    /// Instalment-level list from the local snapshot (never PACT): tower, due-date range (any range or a whole month), payment status, minimum outstanding amount.
    /// Totals and paging are computed over exactly the filtered instalments.
    /// </summary>
    /// <param name="towerId">Optional tower; omit for all towers. Still accepted so old links work; combined with <c>towerIds</c>.</param>
    /// <param name="towerIds">Optional several towers (repeat the parameter: <c>towerIds=1&amp;towerIds=2</c>). An instalment is kept when it belongs to ANY of them (OR); filtering happens before totals and paging. Omit for all towers; an unknown id is a 400.</param>
    /// <param name="dateFrom">First due date included (default 1 January of the current year).</param>
    /// <param name="dateTo">Last due date included, whole day (default end of the current month).</param>
    /// <param name="paymentStatus"><c>outstanding</c> (default: remaining balance &gt; 0), <c>unpaid</c>, <c>partial</c>, <c>paid</c> or <c>all</c>.</param>
    /// <param name="minAmount">Remaining balance &gt;= this value (default 100). Ignored for <c>paid</c> and <c>all</c>.</param>
    /// <param name="search">Customer, unit, voucher, phone or e-mail.</param>
    /// <param name="page">One-based page.</param>
    /// <param name="pageSize">1 to 100.</param>
    /// <param name="view"><c>instalments</c> (default): one row per instalment. <c>units</c>: one row per unit (company + customer + unit) with all its matching instalments, grouped in SQL before paging.</param>
    /// <param name="dueMonth">Optional <c>yyyy-MM</c>: narrow the list and its totals to instalments due in that month. The returned <c>months</c> overview ignores it and always covers every month of the other filters.</param>
    /// <param name="status">With <c>view=units</c> only: <c>all</c> (default), <c>overdue</c> or <c>due</c> - the units that have an instalment of that class. The unit view lists only instalments due before the end of the current Dubai month (Overdue: before the first day of the month, Due: any day inside it, even a coming one), so <c>dateTo</c> is capped at the month end; a month filter only narrows the window.</param>
    /// <param name="minTotal">With <c>view=units</c> only: keep units whose total (Due + Overdue) is GREATER than this value; omit for no minimum. <c>minAmount</c> is not applied per instalment in the unit view.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("/api/collections/receivables/instalments")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<PactInstalmentsPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Instalments([FromQuery] int? towerId, [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, [FromQuery] string? paymentStatus,
        [FromQuery] decimal? minAmount, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? view = null, [FromQuery] string? dueMonth = null, [FromQuery] string? status = null, [FromQuery] decimal? minTotal = null, CancellationToken cancellationToken = default, [FromQuery] int[]? towerIds = null)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await instalments.ListAsync(caller, towerId, dateFrom, dateTo, paymentStatus, minAmount, search, page, pageSize, cancellationToken, view, dueMonth, status, minTotal, towerIds);
        return result.IsSuccess ? Ok(result.Value) : Failure(result.Outcome, result.Detail);
    }

    /// <summary>
    /// Per-company state of the local receivables snapshot (Loading / Ready / Stale / Failed / NeverLoaded / Stuck, last good refresh, failure text). A few hundred bytes; the pages poll this
    /// while a refresh runs instead of re-reading the list. Never starts a refresh.
    /// </summary>
    [HttpGet("/api/collections/receivables/snapshot/status")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<SnapshotStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SnapshotStatus(CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await service.GetSnapshotStatusAsync(caller, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : Failure(result.Outcome, result.Detail);
    }

    /// <summary>Active towers (number and name) for the tower dropdown. Read from the local table; PACT is not contacted.</summary>
    [HttpGet("/api/collections/receivables/towers")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<IReadOnlyList<CollectionsTowerDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Towers(CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await service.ListTowersAsync(caller, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : Failure(result.Outcome, result.Detail);
    }

    /// <summary>
    /// Starts a background load of an instalment due-date range the snapshot does not cover. Returns 202 immediately; 200 when
    /// nothing needs loading or a load is already running. Never blocks on PACT.
    /// </summary>
    /// <param name="companyId">Optional 4 or 32: retry the refresh of that company only (over the standard window); the dates are then not used to decide whether to load.</param>
    [HttpPost("/api/collections/receivables/coverage/load")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<ReceivablesRangeLoadDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReceivablesRangeLoadDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> LoadCoverage([FromQuery] DateOnly dateFrom, [FromQuery] DateOnly dateTo, [FromQuery] int? companyId = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await service.RequestLoadAsync(caller, dateFrom, dateTo, cancellationToken, companyId);
        if (!result.IsSuccess) return Failure(result.Outcome, result.Detail);
        return result.Value!.Accepted ? Accepted(result.Value) : Ok(result.Value);
    }

    private CollectionsCaller? Caller() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var employeeId)
        ? new CollectionsCaller(employeeId, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            User.FindAll(TigerCsClaimTypes.DepartmentId).Select(c => int.TryParse(c.Value, out var d) ? d : (int?)null).OfType<int>().ToArray())
        : null;

    private IActionResult Failure(CollectionsOutcome outcome, string? detail)
    {
        var (http, code) = outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.Disabled => (503, "CollectionsDisabled"),
            _ => (503, "FinanceUnavailable")
        };
        var response = Problem(statusCode: http, title: code, detail: detail ?? code,
            type: $"https://tigercs.internal/problems/collections/{code}");
        if (response.Value is ProblemDetails problem)
        {
            problem.Extensions["code"] = code;
            problem.Extensions["message"] = detail ?? code;
        }
        return response;
    }
}
