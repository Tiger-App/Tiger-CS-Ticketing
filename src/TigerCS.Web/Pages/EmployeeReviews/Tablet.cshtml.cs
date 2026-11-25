using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.EmployeeReviews;

namespace TigerCS.Web.Pages.EmployeeReviews;

public enum TabletPageState
{
    Ready,
    Inactive,
    Unavailable
}

/// <summary>What the tablet's script posts. There is deliberately no employee or device field: the tablet's cookie decides both.</summary>
public sealed record TabletSubmission(Guid SubmissionId, int Rating, string? Comment);

/// <summary>
/// The customer review tablet: always the review screen, no customer login. The tablet's identity is its activation credential, kept in an
/// HttpOnly, SameSite=Strict cookie that script cannot read and no URL parameter can replace — the page never contains an employee id, a
/// device id or the token. Every submission goes through this page's own handler, which attaches the cookie's token as the
/// <c>X-Device-Token</c> header; TigerCS.Api resolves the employee from the tablet's assignment.
/// </summary>
public sealed class TabletModel(EmployeeReviewTabletApiClient tabletApi) : PageModel
{
    public const string TokenCookie = "tigercs_review_tablet";
    public const string LanguageCookie = "tigercs_tablet_lang";
    public const string ActivatePath = "/EmployeeReviews/Activate";

    public TabletPageState State { get; private set; }

    public string Language { get; private set; } = TabletText.English;

    public string? EmployeeName { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? lang, CancellationToken cancellationToken)
    {
        ApplyNoStore();
        Language = TabletText.Normalize(lang ?? Request.Cookies[LanguageCookie]);

        var token = Request.Cookies[TokenCookie];
        if (string.IsNullOrWhiteSpace(token))
        {
            return Redirect(ActivatePath);
        }

        var session = await tabletApi.GetSessionAsync(token, cancellationToken);
        if (session is null)
        {
            State = TabletPageState.Unavailable;
            return Page();
        }

        switch (session.Status)
        {
            case TabletSessionStatus.Invalid:
                // Revoked or replaced: forget the dead credential so the tablet asks for a new activation code.
                Response.Cookies.Delete(TokenCookie, CookieOptions(Request));
                return Redirect(ActivatePath);
            case TabletSessionStatus.Inactive:
                State = TabletPageState.Inactive;
                return Page();
            default:
                State = TabletPageState.Ready;
                EmployeeName = session.Tablet?.EmployeeName;
                return Page();
        }
    }

    /// <summary>One review from the tablet's script. Answers with a status the script acts on; an unsaved review is never discarded by the script.</summary>
    public async Task<IActionResult> OnPostSubmitAsync([FromBody] TabletSubmission? submission, CancellationToken cancellationToken)
    {
        ApplyNoStore();

        var token = Request.Cookies[TokenCookie];
        if (string.IsNullOrWhiteSpace(token))
        {
            return new JsonResult(new { status = "invalid-device" }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        if (submission is null)
        {
            return new JsonResult(new { status = "rejected" }) { StatusCode = StatusCodes.Status422UnprocessableEntity };
        }

        var status = await tabletApi.SubmitAsync(
            token, new SubmitEmployeeReviewRequestDto(submission.SubmissionId, submission.Rating, submission.Comment), cancellationToken);
        return status switch
        {
            TabletSubmitStatus.Saved => new JsonResult(new { status = "saved" }),
            TabletSubmitStatus.Rejected => new JsonResult(new { status = "rejected" }) { StatusCode = StatusCodes.Status422UnprocessableEntity },
            TabletSubmitStatus.InvalidDevice => new JsonResult(new { status = "invalid-device" }) { StatusCode = StatusCodes.Status401Unauthorized },
            TabletSubmitStatus.InactiveDevice => new JsonResult(new { status = "inactive" }) { StatusCode = StatusCodes.Status403Forbidden },
            _ => new JsonResult(new { status = "unavailable" }) { StatusCode = StatusCodes.Status503ServiceUnavailable }
        };
    }

    public static CookieOptions CookieOptions(HttpRequest request) =>
        new() { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = request.IsHttps };

    private void ApplyNoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}
