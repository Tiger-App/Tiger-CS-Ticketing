using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Shared plumbing of every Administration controller: the SystemAdministrator
/// policy on the whole class (backend enforcement — the Web only hides the
/// menu), the caller identity, and the one mapping from
/// <see cref="AdminOutcome"/> to HTTP problem responses.
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public abstract class AdminControllerBase : ControllerBase
{
    protected Guid CallerEmployeeId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    protected IActionResult FromResult<T>(AdminResult<T> result, Func<T, IActionResult>? onSuccess = null) =>
        result.Outcome switch
        {
            AdminOutcome.Success => onSuccess is null ? Ok(result.Value) : onSuccess(result.Value!),
            AdminOutcome.NotFound => NotFound(),
            AdminOutcome.ValidationFailed => ValidationProblem(result.Errors),
            AdminOutcome.Forbidden => Problem(
                type: "https://tigercs.internal/problems/administration-forbidden",
                title: "You are not allowed to make this change",
                detail: string.Join(" ", result.Errors ?? []),
                statusCode: StatusCodes.Status403Forbidden),
            AdminOutcome.Conflict => Problem(
                type: "https://tigercs.internal/problems/administration-conflict",
                title: "The change conflicts with current configuration",
                detail: string.Join(" ", result.Errors ?? []),
                statusCode: StatusCodes.Status409Conflict),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };

    protected IActionResult FromResult(AdminResult result) =>
        result.Outcome switch
        {
            AdminOutcome.Success => NoContent(),
            AdminOutcome.NotFound => NotFound(),
            AdminOutcome.ValidationFailed => ValidationProblem(result.Errors),
            AdminOutcome.PasswordPolicyViolation => PasswordPolicyProblem(result.Errors),
            AdminOutcome.Forbidden => Problem(
                type: "https://tigercs.internal/problems/administration-forbidden",
                title: "You are not allowed to make this change",
                detail: string.Join(" ", result.Errors ?? []),
                statusCode: StatusCodes.Status403Forbidden),
            AdminOutcome.Conflict => Problem(
                type: "https://tigercs.internal/problems/administration-conflict",
                title: "The change conflicts with current configuration",
                detail: string.Join(" ", result.Errors ?? []),
                statusCode: StatusCodes.Status409Conflict),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };

    private IActionResult ValidationProblem(IReadOnlyList<string>? errors)
    {
        var modelState = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
        foreach (var error in errors ?? ["The request is not valid."])
        {
            modelState.AddModelError(string.Empty, error);
        }

        return ValidationProblem(modelState);
    }

    /// <summary>422 with Identity's own password-policy descriptions in the <c>errors</c> map — the shape every Web form already reads.</summary>
    private IActionResult PasswordPolicyProblem(IReadOnlyList<string>? errors)
    {
        var modelState = new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
        foreach (var error in errors ?? ["The new password does not satisfy the password policy."])
        {
            modelState.AddModelError("NewPassword", error);
        }

        return ValidationProblem(
            modelStateDictionary: modelState,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The new password does not satisfy the password policy",
            type: "https://tigercs.internal/problems/password-policy-violation");
    }
}
