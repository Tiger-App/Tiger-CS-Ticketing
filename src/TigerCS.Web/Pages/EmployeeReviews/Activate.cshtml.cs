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
    Unavailable
}

/// <summary>
/// Activates a tablet: the administrator's one-time code is checked with the Api and, only if the Api accepts it, stored in the tablet's
/// HttpOnly cookie. The code can also arrive in the link's <c>#fragment</c> (never sent to any server or log); the page's script moves it
/// into the form. Re-activating a tablet simply replaces the previous cookie.
/// </summary>
public sealed class ActivateModel(EmployeeReviewTabletApiClient tabletApi) : PageModel
{
    [BindProperty, StringLength(128)]
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

        var code = Code?.Trim();
        if (!DeviceToken.IsWellFormed(code))
        {
            Problem = ActivationProblem.Invalid;
            return Page();
        }

        var session = await tabletApi.GetSessionAsync(code!, cancellationToken);
        switch (session?.Status)
        {
            case null:
                Problem = ActivationProblem.Unavailable;
                return Page();
            case TabletSessionStatus.Invalid:
                Problem = ActivationProblem.Invalid;
                return Page();
            case TabletSessionStatus.Inactive:
                Problem = ActivationProblem.Inactive;
                return Page();
        }

        var options = TabletModel.CookieOptions(Request);
        options.MaxAge = TimeSpan.FromDays(3650);
        Response.Cookies.Append(TabletModel.TokenCookie, code!, options);
        return Redirect("/EmployeeReviews/Tablet");
    }

    private void ApplyNoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}
