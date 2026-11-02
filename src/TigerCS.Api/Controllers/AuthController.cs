using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>MVP-API-Contracts.md §1.1/§1.2.</summary>
[ApiController]
[Route("api/auth")]
[Tags(OpenApiTags.Authentication)]
[NoPermissionRequired("Sign-in, sign-out and change-password are available to every account.")]
public class AuthController(
    AuthenticationAppService authenticationAppService,
    GenesysScreenPopAppService screenPopAppService) : ControllerBase
{
    /// <summary>Stable, machine-readable Screen Pop redemption codes — the <c>code</c> member of the ProblemDetails body.</summary>
    public static class ScreenPopErrorCodes
    {
        public const string TokenInvalid = "SCREEN_POP_TOKEN_INVALID";
        public const string TokenExpired = "SCREEN_POP_TOKEN_EXPIRED";
        public const string TokenUsed = "SCREEN_POP_TOKEN_USED";
    }

    /// <summary>Sign in and obtain a JWT access token.</summary>
    /// <remarks>
    /// The only endpoint besides <c>GET /health</c> that does not require an
    /// access token. Copy <c>accessToken</c> from a 200 response into
    /// Swagger UI's <b>Authorize</b> box to call the rest of the API.
    /// </remarks>
    /// <param name="request">Username and password. Both are required and must be non-blank.</param>
    /// <response code="200">Signed in. Returns the access token, its UTC expiry, and the caller's identity, roles, and primary department.</response>
    /// <response code="400">Username or password was missing or blank.</response>
    /// <response code="401">The username/password combination was not accepted.</response>
    /// <response code="423">The account is locked.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return ValidationProblem();
        }

        var result = await authenticationAppService.LoginAsync(request, cancellationToken);

        return result.Outcome switch
        {
            LoginOutcome.Success => Ok(result.Response),
            LoginOutcome.Locked => Problem(
                type: "https://tigercs.internal/problems/account-locked",
                title: "Account locked",
                statusCode: StatusCodes.Status423Locked),
            _ => Problem(
                type: "https://tigercs.internal/problems/invalid-credentials",
                title: "Invalid credentials",
                statusCode: StatusCodes.Status401Unauthorized)
        };
    }

    /// <summary>Redeem a Genesys Secure Screen Pop token for the mapped agent's ordinary TigerCS session.</summary>
    /// <remarks>
    /// Called by TigerCS Web when a Genesys launch URL is opened. Anonymous
    /// because the one-time token is itself the credential — issued by
    /// <c>POST /api/genesys/screen-pop</c> to the authenticated Genesys
    /// service account, valid for one hour, redeemable once.
    ///
    /// <para>
    /// The answer is exactly the session <c>POST /api/auth/login</c> would
    /// give the mapped user — the same access token, roles and primary
    /// department — plus the page to open. It confers no access beyond that
    /// user's own: the target page is authorized like any other request.
    /// </para>
    ///
    /// <para>
    /// The Genesys agent mapping is re-checked here: an agent unmapped,
    /// re-mapped, deactivated or locked out since the launch was issued is
    /// refused, and the token is consumed regardless.
    /// </para>
    /// </remarks>
    /// <param name="request">The token from the launch URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Signed in as the mapped user.</response>
    /// <response code="400">token was missing or blank.</response>
    /// <response code="401">The token is unknown (<c>SCREEN_POP_TOKEN_INVALID</c>), older than one hour (<c>SCREEN_POP_TOKEN_EXPIRED</c>), or already used (<c>SCREEN_POP_TOKEN_USED</c>).</response>
    /// <response code="403">The agent is no longer mapped (<c>GENESYS_AGENT_NOT_MAPPED</c>) or the mapped user is deactivated or locked out (<c>GENESYS_AGENT_INACTIVE</c>).</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpPost("screen-pop/redeem")]
    [AllowAnonymous]
    [ProducesResponseType<ScreenPopSessionResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RedeemScreenPop([FromBody] ScreenPopRedeemRequestDto request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            ModelState.AddModelError(nameof(request.Token), "token is required.");
            return ValidationProblem(ModelState);
        }

        var result = await screenPopAppService.RedeemAsync(request.Token, cancellationToken);

        return result.Outcome switch
        {
            GenesysScreenPopRedeemOutcome.SignedIn => Ok(new ScreenPopSessionResponseDto(
                result.Session!.AccessToken,
                result.Session.ExpiresAtUtc,
                result.Session.EmployeeId,
                result.Session.DisplayName,
                result.Session.Roles,
                result.Session.PrimaryDepartmentId,
                result.TargetPath!)),

            GenesysScreenPopRedeemOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysScreenPopRedeemOutcome.Expired => CodedProblem(
                ScreenPopErrorCodes.TokenExpired, "Screen Pop link expired",
                "This Screen Pop link is more than one hour old.", StatusCodes.Status401Unauthorized),

            GenesysScreenPopRedeemOutcome.AlreadyUsed => CodedProblem(
                ScreenPopErrorCodes.TokenUsed, "Screen Pop link already used",
                "Each Screen Pop link can be opened once.", StatusCodes.Status401Unauthorized),

            GenesysScreenPopRedeemOutcome.AgentNotMapped => CodedProblem(
                GenesysController.ErrorCodes.AgentNotMapped, "Genesys agent is not mapped to a Ticketing user",
                "Genesys agent is not mapped to a Ticketing user.", StatusCodes.Status403Forbidden),

            GenesysScreenPopRedeemOutcome.AgentInactive => CodedProblem(
                GenesysController.ErrorCodes.AgentInactive, "The mapped Ticketing user cannot sign in",
                "The Ticketing user mapped to this Genesys agent is deactivated or locked out.", StatusCodes.Status403Forbidden),

            _ => CodedProblem(
                ScreenPopErrorCodes.TokenInvalid, "Invalid Screen Pop link",
                "This Screen Pop link is not valid.", StatusCodes.Status401Unauthorized)
        };
    }

    /// <summary>Change the signed-in user's own password.</summary>
    /// <remarks>
    /// The account is the token's subject, never a client-supplied id. The
    /// current password must verify and the new one must satisfy the password
    /// policy (docs/DEV-SETUP.md §3). On success <b>every</b> session the user
    /// had — including the one that made this call — is invalidated: sign in
    /// again to obtain a fresh token.
    /// </remarks>
    /// <response code="204">Password changed; sign in again.</response>
    /// <response code="400">A field was missing or blank.</response>
    /// <response code="422">The current password did not verify (<c>CURRENT_PASSWORD_INCORRECT</c>), or the new password violates the policy (<c>errors</c> lists each reason).</response>
    [HttpPost("change-password")]
    [AllowServiceIdentity]
    [AllowReadOnlyCallerWrite]
    [Authorize(Policy = PolicyNames.AuthenticatedStaff)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequestDto request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            ModelState.AddModelError(nameof(request.CurrentPassword), "The current password is required.");
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            ModelState.AddModelError(nameof(request.NewPassword), "A new password is required.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return Unauthorized();
        }

        var result = await authenticationAppService.ChangePasswordAsync(employeeId, request, cancellationToken);

        switch (result.Outcome)
        {
            case ChangePasswordOutcome.Success:
                return NoContent();
            case ChangePasswordOutcome.NotFound:
                return Unauthorized();
            case ChangePasswordOutcome.CurrentPasswordIncorrect:
                ModelState.AddModelError(nameof(request.CurrentPassword), "The current password is incorrect.");
                return ValidationProblem(
                    modelStateDictionary: ModelState,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "The current password is incorrect",
                    type: "https://tigercs.internal/problems/current-password-incorrect");
            default:
                foreach (var error in result.Errors ?? ["The new password does not satisfy the password policy."])
                {
                    ModelState.AddModelError(nameof(request.NewPassword), error);
                }

                return ValidationProblem(
                    modelStateDictionary: ModelState,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "The new password does not satisfy the password policy",
                    type: "https://tigercs.internal/problems/password-policy-violation");
        }
    }

    /// <summary>Sign out the current user.</summary>
    /// <remarks>Succeeds with 204 whether or not the token carried a resolvable employee id.</remarks>
    /// <response code="204">Signed out.</response>
    [HttpPost("logout")]
    [AllowServiceIdentity]
    [AllowReadOnlyCallerWrite]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is not null && Guid.TryParse(idValue, out var employeeId))
        {
            await authenticationAppService.LogoutAsync(employeeId, cancellationToken);
        }

        return NoContent();
    }

    private ObjectResult CodedProblem(string code, string title, string detail, int statusCode)
    {
        var result = Problem(
            type: $"https://tigercs.internal/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            title: title, detail: detail, statusCode: statusCode);
        if (result.Value is ProblemDetails problem)
        {
            problem.Extensions["code"] = code;
        }

        return result;
    }
}
