using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Review tablet administration — System Administrator only (enforced here; the Web only hides the menu). A tablet's 8-character activation
/// code is returned exactly once, by register or by issuing a new code, and never again. The tablet's own device token is never shown to anyone.
/// </summary>
[Route("api/admin/employee-review-devices")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.EmployeeReviews)]
public class AdminEmployeeReviewDevicesController(EmployeeReviewDeviceAdminService devices) : AdminControllerBase
{
    /// <summary>Every registered tablet with its assigned employee, status and last activity.</summary>
    [HttpGet]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<EmployeeReviewDeviceDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await devices.ListAsync(cancellationToken));

    /// <summary>The active Customer Service employees a tablet can be assigned to (existing employees; nothing is created).</summary>
    [HttpGet("eligible-employees")]
    [RequirePermission(Permissions.AdminConfigurationView)]
    [ProducesResponseType<IReadOnlyList<EligibleReviewEmployeeDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EligibleEmployees(CancellationToken cancellationToken) =>
        Ok(await devices.ListEligibleEmployeesAsync(cancellationToken));

    /// <summary>Registers a tablet for an existing Customer Service employee and returns its one-time 8-character activation code (valid 24 hours, one use).</summary>
    /// <response code="201">The tablet and its activation code and expiry — shown once.</response>
    /// <response code="400">The name is missing or the employee is not an active Customer Service employee.</response>
    /// <response code="409">A tablet with this name already exists.</response>
    [HttpPost]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<EmployeeReviewActivationCodeDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register([FromBody] RegisterEmployeeReviewDeviceRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await devices.RegisterAsync(CallerEmployeeId, request, cancellationToken), created => StatusCode(StatusCodes.Status201Created, created));

    /// <summary>Renames a tablet and/or assigns it to another employee. Reviews already collected keep the employee they were given for.</summary>
    [HttpPut("{deviceId:guid}")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<EmployeeReviewDeviceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid deviceId, [FromBody] UpdateEmployeeReviewDeviceRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await devices.UpdateAsync(CallerEmployeeId, deviceId, request, cancellationToken));

    /// <summary>Switches a tablet on or off. An inactive tablet refuses every submission.</summary>
    [HttpPost("{deviceId:guid}/activation")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<EmployeeReviewDeviceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetActive(Guid deviceId, [FromBody] SetEmployeeReviewDeviceActiveRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await devices.SetActiveAsync(CallerEmployeeId, deviceId, request.IsActive, cancellationToken));

    /// <summary>
    /// Issues a new activation code, returned once. For a tablet with no credential (never activated, expired, revoked) that is all it does. For a tablet
    /// that is already activated it switches the current credential off immediately, so it needs <c>confirmReplaceActiveCredential: true</c>.
    /// </summary>
    /// <response code="200">The device and its new code and expiry — shown once.</response>
    /// <response code="409">The tablet is already activated and the re-activation was not confirmed; nothing changed.</response>
    [HttpPost("{deviceId:guid}/credential")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<EmployeeReviewActivationCodeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Regenerate(
        Guid deviceId, [FromBody] IssueEmployeeReviewActivationCodeRequestDto? request, CancellationToken cancellationToken) =>
        FromResult(await devices.IssueActivationCodeAsync(CallerEmployeeId, deviceId, request?.ConfirmReplaceActiveCredential ?? false, cancellationToken));

    /// <summary>Revokes the tablet's credential and any pending activation code, and switches it off.</summary>
    [HttpDelete("{deviceId:guid}/credential")]
    [RequirePermission(Permissions.AdminConfigurationEdit)]
    [ProducesResponseType<EmployeeReviewDeviceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(Guid deviceId, CancellationToken cancellationToken) =>
        FromResult(await devices.RevokeAsync(CallerEmployeeId, deviceId, cancellationToken));
}
