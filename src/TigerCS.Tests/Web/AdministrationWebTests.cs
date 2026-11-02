// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Pages.Admin;
using TigerCsWeb::TigerCS.Web.Services.Auth;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Administration area of TigerCS.Web: reachable only through the
/// System Administrator policy, linked from navigation only for that role,
/// and built from names/badges rather than raw ids or JSON editors.
/// </summary>
public sealed class AdministrationWebTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string View(params string[] pathUnderPages) =>
        File.ReadAllText(SourceFile(Path.Combine(["TigerCS.Web", "Pages", .. pathUnderPages])));

    private static ClaimsPrincipal PrincipalWithRoles(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "Test User") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static WebApplicationFactory<TigerCsWeb::Program> CreateFactory() =>
        new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    [Fact]
    public void AdministrationLink_IsOfferedOnlyToSystemAdministrators()
    {
        Assert.True(AdministrationPolicy.AppliesTo(CurrentUser.FromPrincipal(PrincipalWithRoles(Roles.SystemAdministrator))));
        Assert.False(AdministrationPolicy.AppliesTo(CurrentUser.FromPrincipal(PrincipalWithRoles(Roles.CsManager))));
        Assert.False(AdministrationPolicy.AppliesTo(CurrentUser.FromPrincipal(PrincipalWithRoles(Roles.CsAgent, Roles.DepartmentHead))));
        Assert.False(AdministrationPolicy.AppliesTo(null));

        var nav = View("Shared", "_Nav.cshtml");
        Assert.Contains("AdministrationPolicy.AppliesTo(currentUser)", nav);
        Assert.Contains("\"/Admin\"", nav);
    }

    [Fact]
    public async Task AdminFolder_RequiresTheSystemAdministratorPolicy_WhichRequiresTheRole()
    {
        using var factory = CreateFactory();

        var policy = await factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(AdministrationPolicy.Name);
        Assert.NotNull(policy);
        // The gate is the System Administrator role, or an explicit Administration grant - and never a denial (ViewerPermissionRequirement).
        var roleRequirement = Assert.Single(policy!.Requirements.OfType<TigerCsWeb::TigerCS.Web.Services.Auth.ViewerPermissionRequirement>());
        Assert.Equal([Roles.SystemAdministrator], roleRequirement.AllowedRoles);

        // Anonymous requests to every admin page are sent to sign-in, never rendered.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var path in new[] { "/Admin", "/Admin/Users", "/Admin/Departments", "/Admin/RequestTypes", "/Admin/Workflows", "/Admin/Workflows/Versions/1", "/Admin/Channels", "/Admin/Channels/1" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/Login", response.Headers.Location!.ToString());
        }
    }

    [Theory]
    [InlineData("Index.cshtml")]
    [InlineData("Users.cshtml")]
    [InlineData("UserEdit.cshtml")]
    [InlineData("Departments.cshtml")]
    [InlineData("DepartmentEdit.cshtml")]
    [InlineData("RequestTypes.cshtml")]
    [InlineData("RequestTypeEdit.cshtml")]
    [InlineData("Workflows.cshtml")]
    [InlineData("WorkflowDetails.cshtml")]
    [InlineData("WorkflowVersion.cshtml")]
    [InlineData("Channels.cshtml")]
    [InlineData("ChannelEdit.cshtml")]
    public void AdminViews_UseNamesAndBadges_NeverRawIdsOrJsonEditors(string view)
    {
        var html = View("Admin", view);

        Assert.DoesNotContain("Department #", html, StringComparison.Ordinal);
        Assert.DoesNotContain("textarea", html.Contains("RequiredFieldsJson") ? "RequiredFieldsJson" : "no-json-editor", StringComparison.Ordinal);
        Assert.DoesNotContain("<pre", html, StringComparison.Ordinal);
        Assert.Contains("_AdminMessages", html);

        // Every numeric/guid input bound to an id is hidden or a picker.
        foreach (var line in html.Split('\n'))
        {
            if (line.Contains("<input", StringComparison.OrdinalIgnoreCase)
                && (line.Contains("DepartmentId", StringComparison.Ordinal) || line.Contains("EmployeeId", StringComparison.Ordinal)
                    || line.Contains("WorkflowId", StringComparison.Ordinal) || line.Contains("StepId", StringComparison.Ordinal)))
            {
                Assert.True(
                    line.Contains("type=\"hidden\"", StringComparison.Ordinal) || line.Contains("type=\"checkbox\"", StringComparison.Ordinal),
                    $"{view}: an id is bound to a typed input: {line.Trim()}");
            }
        }
    }

    /// <summary>
    /// The five modules are described once — name, route, icon and accent —
    /// and the overview, the sub-navigation and every page header read that
    /// one description. A module cannot be named or routed two ways.
    /// </summary>
    [Fact]
    public void AdminHome_OffersTheFiveAreas_FromOneSharedDescription()
    {
        Assert.Equal(
            ["Users", "Departments", "Request Types", "Workflows", "Channels"],
            AdminModules.All.Select(m => m.Label));

        Assert.Equal(
            ["/Admin/Users", "/Admin/Departments", "/Admin/RequestTypes", "/Admin/Workflows", "/Admin/Channels"],
            AdminModules.All.Select(m => m.Href));

        // One accent each, and none of them is gold: gold stays the brand's.
        Assert.Equal(AdminModules.All.Count, AdminModules.All.Select(m => m.Tone).Distinct().Count());
        Assert.DoesNotContain("tone-brand", AdminModules.All.Select(m => m.Tone));

        // A section key resolves to its module; the overview is not one.
        Assert.Equal(AdminModules.Users, AdminModules.Find("users"));
        Assert.Null(AdminModules.Find(AdminModules.OverviewKey));

        // Both the overview and the sub-nav are built from the list, not from
        // their own copies of it.
        Assert.Contains("AdminModules.", View("Admin", "Index.cshtml.cs"));
        Assert.Contains("AdminModules.All", View("Shared", "_AdminSubNav.cshtml"));
    }

    /// <summary>
    /// Every Administration screen is built from the same header components
    /// and carries its module's accent, so no page is left on an older shape.
    /// </summary>
    [Theory]
    [InlineData("Users.cshtml", "users")]
    [InlineData("UserEdit.cshtml", "users")]
    [InlineData("Departments.cshtml", "departments")]
    [InlineData("DepartmentEdit.cshtml", "departments")]
    [InlineData("RequestTypes.cshtml", "requesttypes")]
    [InlineData("RequestTypeEdit.cshtml", "requesttypes")]
    [InlineData("Workflows.cshtml", "workflows")]
    [InlineData("WorkflowDetails.cshtml", "workflows")]
    [InlineData("WorkflowVersion.cshtml", "workflows")]
    [InlineData("Channels.cshtml", "channels")]
    [InlineData("ChannelEdit.cshtml", "channels")]
    public void EveryModulePage_UsesTheSharedHeader_AndItsOwnAccent(string view, string moduleKey)
    {
        var html = View("Admin", view);
        var module = AdminModules.Find(moduleKey)!;

        Assert.Contains("_AdminCrumbs", html);
        Assert.Contains("_AdminEyebrow", html);
        Assert.Contains("<header class=\"admin-head\"", html);
        Assert.Contains("admin-page @module.Tone", html);
        Assert.Contains($"AdminModules.{module.Key switch
        {
            "users" => "Users",
            "departments" => "Departments",
            "requesttypes" => "RequestTypes",
            "workflows" => "Workflows",
            _ => "Channels"
        }}", html);

        // The retired chrome: a page-header block, the old breadcrumb partial,
        // and a "Manage" button repeated on every row.
        Assert.DoesNotContain("_AdminBreadcrumb", html);
        Assert.DoesNotContain("class=\"page-header\"", html);
        Assert.DoesNotContain(">Manage</a>", html);
    }

    /// <summary>The retired breadcrumb partial is gone, not merely unused.</summary>
    [Fact]
    public void OldBreadcrumbPartial_IsRemoved()
    {
        Assert.False(File.Exists(SourceFile(Path.Combine("TigerCS.Web", "Pages", "Admin", "_AdminBreadcrumb.cshtml"))));
    }

    [Fact]
    public void DestructiveActions_AskForConfirmation_AndVersionBadgesAreExplicit()
    {
        Assert.Contains("data-confirm", View("Admin", "UserEdit.cshtml"));
        Assert.Contains("data-confirm", View("Admin", "DepartmentEdit.cshtml"));
        Assert.Contains("data-confirm", View("Admin", "RequestTypeEdit.cshtml"));
        Assert.Contains("data-confirm", View("Admin", "WorkflowDetails.cshtml"));
        Assert.Contains("data-confirm", View("Admin", "WorkflowVersion.cshtml"));
        Assert.Contains("data-confirm", View("Admin", "ChannelEdit.cshtml"));
        Assert.Contains("data-confirm", View("Admin", "Channels.cshtml"));

        var details = View("Admin", "WorkflowDetails.cshtml");
        Assert.Contains("badge-version-draft", details);
        Assert.Contains("badge-version-published", details);
        Assert.Contains("badge-version-historical", details);
        Assert.Contains("Create New Version", details);

        var designer = View("Admin", "WorkflowVersion.cshtml");
        Assert.Contains("asp-page-handler=\"AddStep\"", designer);
        Assert.Contains("asp-page-handler=\"MoveStep\"", designer);
        Assert.Contains("asp-page-handler=\"RemoveStep\"", designer);
        Assert.Contains("asp-page-handler=\"Publish\"", designer);
        Assert.Contains("validation-summary", designer);
        Assert.Contains("!v.IsEditable", designer);
    }

    [Fact]
    public void NewTicket_OffersRequestCategoryAndDependentRequestTypePickers()
    {
        var html = View("NewTicket.cshtml");

        Assert.Contains("asp-for=\"CreateStep.DepartmentId\">Request Category *</label>", html);
        Assert.Contains("<select class=\"form-control\" asp-for=\"CreateStep.DepartmentId\" data-autosubmit>", html);
        Assert.Contains("asp-for=\"CreateStep.RequestTypeChoice\">Request Type *</label>", html);
        Assert.Contains("<select class=\"form-control\" asp-for=\"CreateStep.RequestTypeChoice\" disabled=", html);
        Assert.Contains("name=\"CreateStep.UseRequestCategoryPicker\" value=\"true\"", html);
        Assert.Contains("<input type=\"hidden\" asp-for=\"CreateStep.RequestTypeId\" />", html);
        Assert.DoesNotContain("<select class=\"form-control\" asp-for=\"CreateStep.RequestTypeId\"", html);
        Assert.DoesNotContain("None — standard handling", html);
        Assert.DoesNotContain("<input class=\"form-control\" asp-for=\"CreateStep.RequestTypeId\"", html);
    }
}
