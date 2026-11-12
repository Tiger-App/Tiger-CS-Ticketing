using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

public sealed class IndexModel(ViewerAccessService access) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true) return RedirectToPage("/Login");
        var landing = LandingPage.For(User.FindAll(ClaimTypes.Role).Select(c => c.Value), await access.GetAsync(cancellationToken), "/Tickets");
        return LocalRedirect(landing);
    }
}
