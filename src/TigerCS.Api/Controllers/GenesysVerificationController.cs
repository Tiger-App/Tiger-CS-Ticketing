using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Chatbot customer verification for CRM buyers: <c>/api/genesys/verification</c>.
/// Look the buyer up → choose a unit → TigerCS emails a one-time code to the
/// address <b>CRM</b> holds → the code is checked on the server → an
/// OTP-verified verification session comes back. That session is what
/// <c>POST /api/genesys/documents/send-copy</c> requires.
///
/// <para>
/// Same door as every other Genesys route (TigerGroupWeb forwards it as the
/// integration service account; policy <c>CustomerVerification</c>). No request
/// here carries a destination address, a customer id, a lead id, a "confirmed"
/// flag or a verification method: the only inputs are the phone number CRM is
/// searched by, the unit the customer picked, and the code they received.
/// </para>
/// </summary>
[ApiController]
[Route("api/genesys/verification")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.Genesys)]
[NoPermissionRequired("Genesys integration surface; reached by the integration service account, which cannot hold permission overrides.")]
public class GenesysVerificationController(CustomerOtpAppService otpAppService) : ControllerBase
{
    /// <summary>Find the CRM buyer by phone and list the units they can verify for.</summary>
    /// <remarks>Sends nothing and writes nothing. Returns unit labels and a masked email — never the name, phone, full email or CRM customer id.</remarks>
    /// <response code="200">status <c>Found</c>, with <c>units</c> and the masked email CRM holds.</response>
    /// <response code="400">phoneNumber is not a phone number.</response>
    /// <response code="404">CUSTOMER_NOT_FOUND — no CRM buyer for this number.</response>
    /// <response code="409">CUSTOMER_AMBIGUOUS — CRM returned more than one customer for this number.</response>
    /// <response code="502">CRM_AUTHENTICATION_FAILED / CRM_INVALID_RESPONSE.</response>
    /// <response code="503">CRM_UNAVAILABLE, or DOCUMENT_COPY_DISABLED.</response>
    [HttpPost("buyer-lookup")]
    [ProducesResponseType<CustomerOtpResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LookUp([FromBody] BuyerLookupRequestDto request, CancellationToken cancellationToken) =>
        Map(await otpAppService.LookupAsync(request.PhoneNumber, cancellationToken));

    /// <summary>Choose the unit and send a one-time code — by email (default) or SMS — to the address / mobile CRM holds for the customer.</summary>
    /// <remarks>
    /// <c>channel: "Sms"</c> (optional <c>language</c> en/ar) sends the same kind of code to the mobile CRM holds, never to a number the caller
    /// supplies. <c>CodeSent</c> means the provider's response was verified as acceptance; a timeout or unreadable provider response is
    /// <c>504 OTP_DELIVERY_UNCONFIRMED</c> (the SMS may have arrived, the challenge stays valid, nothing is resent automatically — call
    /// <c>otp/resend</c> only if the customer got nothing).
    /// A repeated call for a live challenge does not email again (it answers <c>AlreadySent</c> with the same
    /// <c>challengeId</c>, or — after the resend interval — acts as a resend). Limits: 3 codes per challenge, 60 s
    /// apart, 5 challenges per customer per hour, 10-minute expiry, 5 wrong tries.
    /// </remarks>
    /// <response code="200">status <c>CodeSent</c>, <c>AlreadySent</c>, or <c>UnitSelectionRequired</c> (several units; nothing sent).</response>
    /// <response code="400">phoneNumber is not a phone number.</response>
    /// <response code="403">UNIT_NOT_OWNED — the unit is not one of this customer's.</response>
    /// <response code="404">CUSTOMER_NOT_FOUND.</response>
    /// <response code="409">CUSTOMER_AMBIGUOUS.</response>
    /// <response code="422">NO_EMAIL_ON_RECORD / NO_MOBILE_ON_RECORD — CRM holds no valid email / mobile, so no code can be sent.</response>
    /// <response code="429">OTP_RATE_LIMITED / OTP_RESEND_LIMIT_REACHED (with Retry-After where applicable).</response>
    /// <response code="502">OTP_DELIVERY_FAILED, CRM_AUTHENTICATION_FAILED, CRM_INVALID_RESPONSE.</response>
    /// <response code="503">CRM_UNAVAILABLE, DOCUMENT_COPY_DISABLED, or OTP_SMS_NOT_CONFIGURED.</response>
    /// <response code="504">OTP_DELIVERY_UNCONFIRMED (SMS only).</response>
    [HttpPost("otp/send")]
    [ProducesResponseType<CustomerOtpResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Send([FromBody] OtpSendRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } caller)
        {
            return Unauthorized();
        }

        return Map(await otpAppService.SendAsync(caller, request.PhoneNumber, request.CrmUnitId, request.Channel, request.Language, cancellationToken));
    }

    /// <summary>Send a new code for the same challenge (the previous code stops working).</summary>
    /// <response code="200">status <c>CodeSent</c>.</response>
    /// <response code="404">OTP_CHALLENGE_NOT_FOUND — unknown, or another caller's challenge.</response>
    /// <response code="410">OTP_EXPIRED.</response>
    /// <response code="423">OTP_LOCKED.</response>
    /// <response code="429">OTP_RESEND_TOO_SOON (Retry-After) / OTP_RESEND_LIMIT_REACHED.</response>
    /// <response code="502">OTP_DELIVERY_FAILED and CRM errors.</response>
    /// <response code="503">OTP_SMS_NOT_CONFIGURED (an SMS challenge while SMS is switched off).</response>
    /// <response code="504">OTP_DELIVERY_UNCONFIRMED (SMS only).</response>
    [HttpPost("otp/resend")]
    [ProducesResponseType<CustomerOtpResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Resend([FromBody] OtpResendRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } caller)
        {
            return Unauthorized();
        }

        return Map(await otpAppService.ResendAsync(caller, request.ChallengeId, cancellationToken));
    }

    /// <summary>Check the code. On success the response carries the OTP-verified verification session.</summary>
    /// <remarks>A code works once. Wrong codes spend the attempt budget (<c>attemptsRemaining</c>); at zero the challenge locks.</remarks>
    /// <response code="200">status <c>Verified</c> with <c>session</c> (its <c>verificationSessionId</c> goes into send-copy).</response>
    /// <response code="400">OTP_INVALID (with attemptsRemaining) or INVALID_REQUEST.</response>
    /// <response code="404">OTP_CHALLENGE_NOT_FOUND — unknown, or another caller's challenge.</response>
    /// <response code="409">OTP_ALREADY_USED.</response>
    /// <response code="410">OTP_EXPIRED.</response>
    /// <response code="423">OTP_LOCKED.</response>
    [HttpPost("otp/verify")]
    [ProducesResponseType<CustomerOtpResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Verify([FromBody] OtpVerifyRequestDto request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } caller)
        {
            return Unauthorized();
        }

        return Map(await otpAppService.VerifyAsync(caller, request.ChallengeId, request.Code, cancellationToken));
    }

    private IActionResult Map(CustomerOtpResult result)
    {
        switch (result.Status)
        {
            case CustomerOtpStatus.Found:
            case CustomerOtpStatus.CodeSent:
            case CustomerOtpStatus.AlreadySent:
            case CustomerOtpStatus.Verified:
            case CustomerOtpStatus.UnitSelectionRequired:
                return Ok(result);
        }

        var (statusCode, title) = result.Status switch
        {
            CustomerOtpStatus.InvalidRequest => (StatusCodes.Status400BadRequest, "Invalid verification request"),
            CustomerOtpStatus.InvalidCode => (StatusCodes.Status400BadRequest, "The code is not correct"),
            CustomerOtpStatus.UnitNotOwned => (StatusCodes.Status403Forbidden, "The unit does not belong to this customer"),
            CustomerOtpStatus.CustomerNotFound or CustomerOtpStatus.ChallengeNotFound => (StatusCodes.Status404NotFound, "Not found"),
            CustomerOtpStatus.CustomerAmbiguous or CustomerOtpStatus.AlreadyUsed => (StatusCodes.Status409Conflict, "Conflict"),
            CustomerOtpStatus.Expired => (StatusCodes.Status410Gone, "The code has expired"),
            CustomerOtpStatus.NoEmailOnRecord => (StatusCodes.Status422UnprocessableEntity, "No email on record"),
            CustomerOtpStatus.NoMobileOnRecord => (StatusCodes.Status422UnprocessableEntity, "No mobile number on record"),
            CustomerOtpStatus.DeliveryUnconfirmed => (StatusCodes.Status504GatewayTimeout, "SMS delivery could not be confirmed"),
            CustomerOtpStatus.SmsNotConfigured => (StatusCodes.Status503ServiceUnavailable, "SMS delivery is not configured"),
            CustomerOtpStatus.Locked => (StatusCodes.Status423Locked, "The challenge is locked"),
            CustomerOtpStatus.RateLimited or CustomerOtpStatus.ResendTooSoon or CustomerOtpStatus.ResendLimitReached =>
                (StatusCodes.Status429TooManyRequests, "Too many requests"),
            CustomerOtpStatus.DeliveryFailed or CustomerOtpStatus.CrmAuthenticationFailed or CustomerOtpStatus.CrmInvalidResponse =>
                (StatusCodes.Status502BadGateway, "Verification could not be completed"),
            _ => (StatusCodes.Status503ServiceUnavailable, "Verification is unavailable")
        };

        var problem = Problem(
            type: $"https://tigercs.internal/problems/{(result.Code ?? "verification").ToLowerInvariant().Replace('_', '-')}",
            title: title, detail: result.Message, statusCode: statusCode);

        if (problem.Value is ProblemDetails details)
        {
            details.Extensions["code"] = result.Code;
            details.Extensions["outcome"] = result.Status.ToString();
            if (result.ChallengeId is not null) details.Extensions["challengeId"] = result.ChallengeId;
            if (result.MaskedDestination is not null) details.Extensions["maskedDestination"] = result.MaskedDestination;
            if (result.Channel is not null) details.Extensions["channel"] = result.Channel;
            if (result.ExpiresAtUtc is not null) details.Extensions["expiresAtUtc"] = result.ExpiresAtUtc;
            if (result.AttemptsRemaining is not null) details.Extensions["attemptsRemaining"] = result.AttemptsRemaining;
            if (result.ResendAvailableAtUtc is not null) details.Extensions["resendAvailableAtUtc"] = result.ResendAvailableAtUtc;
        }

        if (result.RetryAfterSeconds is { } retryAfter)
        {
            Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return problem;
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
