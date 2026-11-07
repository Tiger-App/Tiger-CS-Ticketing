using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

public sealed class LoginModel(AuthApiClient authApiClient) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public bool SessionExpired { get; private set; }

    /// <summary>Set by the Change My Password page after a success: the old session was ended on purpose, not lost.</summary>
    public bool PasswordChanged { get; private set; }

    public string? ReturnUrl { get; private set; }

    public void OnGet(bool sessionExpired = false, bool passwordChanged = false, string? returnUrl = null)
    {
        SessionExpired = sessionExpired;
        PasswordChanged = passwordChanged;
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await authApiClient.LoginAsync(new LoginRequestDto(Input.Identifier, Input.Password), cancellationToken);

        if (result.IsSuccess && result.Value is not null)
        {
            var response = result.Value;

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                WebSessionPrincipal.Create(
                    response.EmployeeId, response.DisplayName, response.AccessToken,
                    response.Roles, response.PrimaryDepartmentId),
                new AuthenticationProperties
                {
                    IsPersistent = Input.RememberMe,
                    ExpiresUtc = response.ExpiresAtUtc
                });

            // A page the person asked for comes first; otherwise Collections for anyone with Collections access, else the landing they already had. The session cookie is not
            // readable on this request yet, so the decision uses the roles just returned by the Api (no per-user overrides); "/" decides with them on later visits.
            return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl
                : LandingPage.For(response.Roles, ViewerPermissions.None, "/Tickets"));
        }

        ErrorMessage = result.Outcome switch
        {
            ApiOutcome.Locked => "This account is locked. Contact your administrator.",
            ApiOutcome.ValidationError => "Enter your email or employee ID and password to continue.",
            ApiOutcome.Unreachable => "Tiger Ticketing System could not be reached. Try again in a moment.",
            _ => "The email/employee ID or password was not accepted."
        };
        return Page();
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Enter your email or employee ID.")]
        public string Identifier { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter your password.")]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }
}
