using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.EmployeeReviews;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.EmployeeReviews;

namespace TigerCS.Web.Pages.EmployeeReviews;

public enum ActivationProblem
{
    None,
    Invalid,
    Inactive,
    TooManyAttempts,
    Unavailable
}

/// <summary>
/// Activates a tablet: the administrator's 8-character, 24-hour, single-use code is redeemed with the Api, which answers with a separate,
/// long device token. Only that token is stored (in the tablet's HttpOnly cookie) — the code is never kept. The code can also arrive in the
/// link's <c>#fragment</c> (never sent to any server or log); the page's script moves it into the form. Re-activating replaces the cookie.
/// </summary>
public sealed class ActivateModel(EmployeeReviewTabletApiClient tabletApi) : PageModel
{
    [BindProperty, StringLength(ActivationCode.Length)]
    public string? Code { get; set; }

    public ActivationProblem Problem { get; private set; }

    public string Language { get; private set; } = TabletText.English;

    public void OnGet(string? lang)
    {
        ApplyNoStore();
        Language = TabletText.Normalize(lang ?? Request.Cookies[TabletModel.LanguageCookie]);
    }

    public async Task<IActionResult> OnPostAsync(string? lang, CancellationToken cancellationToken)
    {
        ApplyNoStore();
        Language = TabletText.Normalize(lang ?? Request.Cookies[TabletModel.LanguageCookie]);

        // Cheap shape check first: anything that is not exactly 8 characters from the code alphabet cannot be a code, so it never reaches the Api.
        var code = ActivationCode.Normalize(Code);
        if (!ActivationCode.IsWellFormed(code))
        {
            Problem = ActivationProblem.Invalid;
            return Page();
        }

        var result = await tabletApi.ActivateAsync(code!, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        switch (result?.Outcome)
        {
            case null:
                Problem = ActivationProblem.Unavailable;
                return Page();
            case ActivateTabletOutcome.InvalidCode:
                Problem = ActivationProblem.Invalid;
                return Page();
            case ActivateTabletOutcome.Inactive:
                Problem = ActivationProblem.Inactive;
                return Page();
            case ActivateTabletOutcome.TooManyAttempts:
                Problem = ActivationProblem.TooManyAttempts;
                return Page();
        }

        if (!DeviceToken.IsWellFormed(result.DeviceToken))
        {
            Problem = ActivationProblem.Unavailable;
            return Page();
        }

        var options = TabletModel.CookieOptions(Request);
        options.MaxAge = TimeSpan.FromDays(3650);
        Response.Cookies.Append(TabletModel.TokenCookie, result.DeviceToken!, options);
        return Redirect("/EmployeeReviews/Tablet");
    }

    private void ApplyNoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}
