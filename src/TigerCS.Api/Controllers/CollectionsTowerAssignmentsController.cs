using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Collections -> Tower Assignments: which Collections employee is responsible for which tower in which month. Who may read and who may change it is decided in
/// <see cref="CollectionsTeam"/> (the Collections manager, CS Manager and the System Administrator manage; the Collections team reads) - never by the page.
/// Internal routes called server-side by the Web application as the signed-in user.
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Route("api/collections/tower-assignments")]
[Tags(OpenApiTags.Collections)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CollectionsTowerAssignmentsController(CollectionsTowerAssignmentAppService service) : ControllerBase
{
    /// <summary>Everything the page shows for one month: the collectors with their towers, the tower list and the towers nobody holds yet.</summary>
    [HttpGet]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<TowerAssignmentMonthDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMonth([FromQuery] int year, [FromQuery] int month, CancellationToken ct) =>
        Caller() is { } c ? Result(await service.GetMonthAsync(c.Id, c.Roles, year, month, ct)) : Unauthorized();

    /// <summary>Replaces the set of towers one employee holds for a month (Draft, or Active when <c>activate</c> is true). 409 when a tower belongs to another collector and <c>moveFromOthers</c> is false.</summary>
    [HttpPut("employees/{employeeId:guid}")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<TowerAssignmentMonthDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveEmployee(Guid employeeId, [FromBody] SaveEmployeeTowersRequest request, CancellationToken ct) =>
        Caller() is { } c ? Result(await service.SaveEmployeeTowersAsync(c.Id, c.Roles, employeeId, request, ct)) : Unauthorized();

    /// <summary>Puts the month's drafts (or one employee's) in force.</summary>
    [HttpPost("activate")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<TowerAssignmentMonthDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate([FromBody] ActivateTowerAssignmentsRequest request, CancellationToken ct) =>
        Caller() is { } c ? Result(await service.ActivateAsync(c.Id, c.Roles, request, ct)) : Unauthorized();

    /// <summary>Copies the previous month's assignments into this month as Drafts, for towers that have none yet.</summary>
    [HttpPost("copy-previous")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<CopyPreviousMonthResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CopyPrevious([FromBody] CopyPreviousMonthRequest request, CancellationToken ct) =>
        Caller() is { } c ? Result(await service.CopyPreviousMonthAsync(c.Id, c.Roles, request, ct)) : Unauthorized();

    /// <summary>The tower-assignment change history (newest first), optionally for one month and/or tower.</summary>
    [HttpGet("history")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<IReadOnlyList<TowerAssignmentHistoryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History([FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? towerId, CancellationToken ct) =>
        Caller() is { } c ? Result(await service.ListHistoryAsync(c.Id, c.Roles, year, month, towerId, ct)) : Unauthorized();

    private (Guid Id, string[] Roles)? Caller() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? (id, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray())
        : null;

    private IActionResult Result<T>(CollectionsResult<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        var (status, code) = result.Outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.NotFound => (404, "NotFound"),
            CollectionsOutcome.Conflict => (409, "Conflict"),
            _ => (503, "FinanceUnavailable")
        };
        var response = Problem(statusCode: status, title: code, detail: result.Detail ?? code, type: $"https://tigercs.internal/problems/collections/{code}");
        if (response.Value is ProblemDetails problem) problem.Extensions["code"] = code;
        return response;
    }
}
