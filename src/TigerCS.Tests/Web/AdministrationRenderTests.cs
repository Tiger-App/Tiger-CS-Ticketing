// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCsWeb::TigerCS.Web.Pages.Admin;

namespace TigerCS.Tests.Web;

/// <summary>
/// Every Administration screen rendered end to end through the real Razor
/// pipeline against a populated Api, so the redesign is checked where it is
/// actually seen rather than in the source of one view: each page answers
/// 200, is built from the shared header components, carries its module's
/// accent, and shows the record facts its module is responsible for.
///
/// It also guards the two rules the redesign is built on — a list row is
/// itself the link rather than carrying a repeated "Manage" button, and no
/// page emits an inline <c>style</c> attribute, which "style-src 'self'"
/// would drop.
/// </summary>
public sealed class AdministrationRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly Guid AlmaId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RaviId = new("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Now = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public AdministrationRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new AdminApi()));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AdminAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>Every Administration route, in the order the console presents them.</summary>
    public static TheoryData<string, string> EveryScreen() => new()
    {
        { "/Admin", "tone-brand" },
        { "/Admin/Users", "tone-info" },
        { "/Admin/Users/New", "tone-info" },
        { $"/Admin/Users/{AlmaId}", "tone-info" },
        { $"/Admin/Users/{AlmaId}/Permissions", "tone-info" },
        { "/Admin/Departments", "tone-progress" },
        { "/Admin/Departments/1", "tone-progress" },
        { "/Admin/RequestTypes", "tone-secondary" },
        { "/Admin/RequestTypes/New", "tone-secondary" },
        { "/Admin/RequestTypes/1", "tone-secondary" },
        { "/Admin/Workflows", "tone-purple" },
        { "/Admin/Workflows/1", "tone-purple" },
        { "/Admin/Workflows/Versions/2", "tone-purple" },
        { "/Admin/Workflows/Versions/1", "tone-purple" },
        { "/Admin/Channels", "tone-success" },
        { "/Admin/Channels/1", "tone-success" }
    };

    [Theory]
    [MemberData(nameof(EveryScreen))]
    public async Task EveryScreen_Renders_OnTheRedesignedShell(string path, string tone)
    {
        var html = await Ok(await GetAsync(path));

        // The shared shell: primary nav, the segmented sub-nav, and the
        // module's own accent on the page.
        Assert.Contains("class=\"admin-subnav\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-subnav__track\"", html, StringComparison.Ordinal);
        Assert.Contains($"class=\"page admin-page {tone}\"", html, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"page-title", html, StringComparison.Ordinal);

        // Nothing fell back to an error state, and nothing is left on the old shape.
        Assert.DoesNotContain("class=\"error-state\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"page-header\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"page-eyebrow\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Manage</a>", html, StringComparison.Ordinal);

        // "style-src 'self'" drops an inline style attribute, so no page may emit one.
        Assert.DoesNotContain("style=\"", html, StringComparison.Ordinal);

        // Every screen but the overview sits under its module in the breadcrumb.
        if (path != "/Admin")
        {
            Assert.Contains("class=\"admin-crumbs", html, StringComparison.Ordinal);
            Assert.Contains("class=\"admin-head__eyebrow\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Overview_ShowsTheOperationalSummary_AndOneCardPerModule()
    {
        var html = await Ok(await GetAsync("/Admin"));

        // The summary counts what is configured, what is live, and the one
        // thing waiting on a decision.
        Assert.Contains("class=\"admin-summary\"", html, StringComparison.Ordinal);
        Assert.Contains(">Configuration records</span>", html, StringComparison.Ordinal);
        Assert.Contains(">Active and offered</span>", html, StringComparison.Ordinal);
        Assert.Contains(">Waiting on a published workflow</span>", html, StringComparison.Ordinal);

        // One card per module, each the link itself, each in its own accent.
        Assert.Contains("class=\"admin-modules\"", html, StringComparison.Ordinal);
        foreach (var module in AdminModules.All)
        {
            Assert.Contains($"<a class=\"admin-card {module.Tone}\" href=\"{module.Href}\">", html, StringComparison.Ordinal);
            Assert.Contains($"class=\"admin-card__title\">{module.Label}</span>", html, StringComparison.Ordinal);
            Assert.Contains($"class=\"admin-card__tagline\">{module.Tagline}</span>", html, StringComparison.Ordinal);
        }

        // Each figure carries a tinted chip, and the two neutral ones say so
        // rather than inheriting the page's brand tone.
        Assert.Equal(4, html.Split("class=\"admin-summary__icon\"").Length - 1);
        Assert.Equal(2, html.Split("class=\"admin-summary__item tone-neutral\"").Length - 1);
        Assert.Contains("class=\"admin-summary__item tone-success\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-summary__item tone-warning\"", html, StringComparison.Ordinal);

        // Counts split active from inactive, and the draft-only workflow is flagged.
        Assert.Contains("class=\"admin-card__count\"", html, StringComparison.Ordinal);
        Assert.Contains("inactive</em>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-card__flag\"", html, StringComparison.Ordinal);
        Assert.Contains("never published", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Users_ShowsIdentity_RoleAndDepartmentBadges_AndOpensFromTheRow()
    {
        var html = await Ok(await GetAsync("/Admin/Users"));

        // Initials avatar, name, and the quiet user-name/e-mail line under it.
        Assert.Contains("class=\"identity__avatar", html, StringComparison.Ordinal);
        Assert.Contains(">AH</span>", html, StringComparison.Ordinal);
        Assert.Contains("alma.hassan &#xB7; alma@example.com</span>", html, StringComparison.Ordinal);

        Assert.Contains("class=\"badge badge-role\">System Administrator</span>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-tone\" title=\"Primary department\">Collections</span>", html, StringComparison.Ordinal);
        // Account state and sign-in state are said in two registers, so a live
        // account that is locked out never shows a green and a red pill together.
        Assert.Contains("class=\"status-stack\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"status-note\"", html, StringComparison.Ordinal);
        Assert.Contains("Sign-in locked", html, StringComparison.Ordinal);
        Assert.DoesNotContain("badge-warning\">Locked out", html, StringComparison.Ordinal);

        // The row is the link; the arrow marks it, and "Manage" is gone.
        Assert.Contains($"<tr class=\"is-link \" data-href=\"/Admin/Users/{AlmaId}\">", html, StringComparison.Ordinal);
        Assert.Contains("class=\"row-open\"", html, StringComparison.Ordinal);

        // Account age is shown because it is available; a deactivated account
        // says when, once inactive accounts are included.
        Assert.Contains(">Added</th>", html, StringComparison.Ordinal);

        var withInactive = await Ok(await GetAsync("/Admin/Users?includeInactive=true"));
        Assert.Contains("class=\"cell-sub\">Deactivated ", withInactive, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"is-link is-inactive\"", withInactive, StringComparison.Ordinal);
        Assert.Contains("identity__avatar--muted", withInactive, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsersList_OffersManagePermissions_ForEveryUser_AndThePageShowsTheEffectiveMatrix()
    {
        var list = await Ok(await GetAsync("/Admin/Users"));
        Assert.Contains($"href=\"/Admin/Users/{AlmaId}/Permissions\"", list, StringComparison.Ordinal);
        Assert.Contains("Manage Permissions", list, StringComparison.Ordinal);

        var page = await Ok(await GetAsync($"/Admin/Users/{AlmaId}/Permissions"));
        Assert.Contains("Manage Permissions", page, StringComparison.Ordinal);
        Assert.Contains("id=\"roles-form\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"permissions-form\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Overrides[Tickets.Reopen]\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"SelectedRoles\"", page, StringComparison.Ordinal);
        Assert.Contains("Administrator only", page, StringComparison.Ordinal);
        Assert.Contains("Root Admin", page, StringComparison.Ordinal);
        Assert.Contains("Permission=Tickets.Reopen;Override=Deny;Effective=false", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The administrative "Reset password" panel exists for an existing user
    /// only — never on the Add User form, where the initial password is set —
    /// and states the policy beside the field, with both password entries
    /// required and the optional reason that lands in the audit trail.
    /// </summary>
    [Fact]
    public async Task UserEdit_OffersAResetPasswordPanel_ForAnExistingUserOnly()
    {
        var edit = await Ok(await GetAsync($"/Admin/Users/{AlmaId}"));

        Assert.Contains("id=\"reset-password-title\">Reset password</h2>", edit, StringComparison.Ordinal);
        Assert.Contains($"action=\"/Admin/Users/{AlmaId}?handler=ResetPassword\"", edit, StringComparison.Ordinal);
        Assert.Contains("name=\"ResetPassword.NewPassword\"", edit, StringComparison.Ordinal);
        Assert.Contains("name=\"ResetPassword.ConfirmPassword\"", edit, StringComparison.Ordinal);
        Assert.Contains("name=\"ResetPassword.Reason\"", edit, StringComparison.Ordinal);
        Assert.Contains("type=\"password\" required", edit, StringComparison.Ordinal);
        Assert.Contains("data-password-new", edit, StringComparison.Ordinal);
        Assert.Contains("data-password-confirm", edit, StringComparison.Ordinal);
        Assert.Contains(">Reset Password</button>", edit, StringComparison.Ordinal);
        // The password never appears anywhere but the two input fields.
        Assert.DoesNotContain("value=\"\" name=\"ResetPassword.NewPassword\"", edit, StringComparison.Ordinal);

        var create = await Ok(await GetAsync("/Admin/Users/New"));
        Assert.DoesNotContain("reset-password-title", create, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=ResetPassword", create, StringComparison.Ordinal);
        // The initial password carries the same live rules and confirm check.
        Assert.Contains("data-password-new", create, StringComparison.Ordinal);
        Assert.Contains("data-password-confirm", create, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared <c>_PasswordRules</c> partial lists exactly the rules of the
    /// Api's Identity policy — on Add User and on Reset Password — instead of a
    /// hardcoded sentence.
    /// </summary>
    [Theory]
    [InlineData("/Admin/Users/New", "create-password-rules")]
    [InlineData("/Admin/Users/11111111-1111-1111-1111-111111111111", "reset-password-rules")]
    public async Task PasswordRules_AreRenderedFromTheIdentityPolicy_OnAddUserAndResetPassword(string path, string id)
    {
        var html = await Ok(await GetAsync(path));

        Assert.Contains($"id=\"{id}\" data-password-rules", html, StringComparison.Ordinal);
        Assert.Contains("data-rule=\"length\" data-min=\"8\">At least 8 characters", html, StringComparison.Ordinal);
        Assert.Contains("data-rule=\"upper\"", html, StringComparison.Ordinal);
        Assert.Contains("data-rule=\"lower\"", html, StringComparison.Ordinal);
        Assert.Contains("data-rule=\"digit\"", html, StringComparison.Ordinal);
        Assert.Contains("data-rule=\"symbol\"", html, StringComparison.Ordinal);
        Assert.Contains("data-rule=\"unique\" data-min=\"4\">At least 4 different characters", html, StringComparison.Ordinal);
        Assert.Contains("data-password-match", html, StringComparison.Ordinal);
        // The rules replace the hardcoded wording.
        Assert.DoesNotContain(UserEditModel.PasswordPolicyText, html, StringComparison.Ordinal);
    }

    /// <summary>Unlock appears only on the row of a locked-out user.</summary>
    [Fact]
    public async Task UsersList_OffersUnlock_OnlyForLockedOutUsers()
    {
        var html = await Ok(await GetAsync("/Admin/Users?includeInactive=true"));

        // Alma is locked out, Ravi is not.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "data-unlock-form"));
        Assert.Matches($"action=\"/Admin/Users\\?[^\"]*handler=Unlock[^\"]*\"", html);
        Assert.Matches($"action=\"/Admin/Users\\?[^\"]*id={AlmaId}[^\"]*\"", html);
        Assert.DoesNotContain($"id={RaviId}\" class=\"admin-actions\" data-unlock-form", html, StringComparison.Ordinal);
        Assert.Contains(">Unlock</button>", html, StringComparison.Ordinal);
        Assert.Contains("data-lock-note", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Departments_ShowCodeNameMembersAndStatus_OnRowsThatOpen()
    {
        var list = await Ok(await GetAsync("/Admin/Departments"));
        Assert.Contains("class=\"badge badge-code text-mono\">COL</span>", list, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"is-link \" data-href=\"/Admin/Departments/1\">", list, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-active\"", list, StringComparison.Ordinal);

        // The edit page: grouped sections and one clear action bar.
        var edit = await Ok(await GetAsync("/Admin/Departments/1"));
        Assert.Contains("class=\"form-section\"", edit, StringComparison.Ordinal);
        Assert.Contains("class=\"form-section__title\">Identity</legend>", edit, StringComparison.Ordinal);
        Assert.Contains("class=\"form-actions form-actions--split\"", edit, StringComparison.Ordinal);
        Assert.Contains(">Save Department</button>", edit, StringComparison.Ordinal);
        Assert.Contains(">Cancel</a>", edit, StringComparison.Ordinal);
        Assert.Contains("class=\"identity__avatar identity__avatar--lg ", edit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestTypes_ShowDepartmentWorkflowAndVersion_AndFlagWhatCannotBeUsedYet()
    {
        var list = await Ok(await GetAsync("/Admin/RequestTypes"));
        Assert.Contains("class=\"version-chip\">V3</span>", list, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-warning\">No published version</span>", list, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-priority-", list, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"is-link \" data-href=\"/Admin/RequestTypes/1\">", list, StringComparison.Ordinal);
        Assert.Contains("no published workflow version", list, StringComparison.Ordinal);

        // SLA and approval requirements live on the record's own page, and the
        // list says so rather than implying it has them.
        Assert.Contains("Approval requirements and SLA values are configured on each request type's own page.", list, StringComparison.Ordinal);

        var edit = await Ok(await GetAsync("/Admin/RequestTypes/1"));
        Assert.Contains("approval requirement", edit, StringComparison.Ordinal);
        Assert.Contains("SLA value", edit, StringComparison.Ordinal);
        // The effective configuration the calculation applies is explained next to the request-type rows.
        Assert.Contains("How the SLA is applied to tickets (effective configuration)", edit, StringComparison.Ordinal);
        Assert.Contains("Targets come from the per-priority SLA policy.", edit, StringComparison.Ordinal);
        Assert.Contains("<td>15 min</td>", edit, StringComparison.Ordinal);
        Assert.Contains("<td>1 day</td>", edit, StringComparison.Ordinal);
        Assert.Contains("08:00–18:00 Asia/Dubai", edit, StringComparison.Ordinal);
        // Who decides is said as kind + name, not as one run-on sentence.
        Assert.Contains("class=\"target__kind\">Department</span>", edit, StringComparison.Ordinal);
        Assert.Contains("class=\"target__name\">Finance</span>", edit, StringComparison.Ordinal);
        Assert.Contains("class=\"flag flag--on\"", edit, StringComparison.Ordinal);
        Assert.DoesNotContain("form-actions--sticky", edit, StringComparison.Ordinal);

        var add = await Ok(await GetAsync("/Admin/RequestTypes/New"));
        Assert.Contains("class=\"form-actions form-actions--sticky\"", add, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflows_ReadAsVersionedCards_NotAsAConfigurationTable()
    {
        var html = await Ok(await GetAsync("/Admin/Workflows"));

        // Creating a workflow is a disclosure the page header's own button
        // opens (:target), so the form no longer competes with the list.
        Assert.Contains("class=\"panel panel--reveal admin-form-panel \" id=\"create-workflow\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"panel__reveal-toggle\" href=\"#create-workflow\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-head__actions\"", html, StringComparison.Ordinal);
        // The form itself is untouched — same handler, same two fields.
        Assert.Contains("handler=Create", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Create.Name\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Create.Description\"", html, StringComparison.Ordinal);

        Assert.Contains("class=\"wf-list\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"wf-item__rail\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"wf-item__rail-label\">Active version</span>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"version-chip\">V3</span>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-version-draft\"", html, StringComparison.Ordinal);
        Assert.Contains("</strong> drafts in progress", html, StringComparison.Ordinal);
        // A list of workflows is never a <table>.
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowDetails_ShowsVersionHistory_WithDraftActiveAndHistoricalBadges()
    {
        var html = await Ok(await GetAsync("/Admin/Workflows/1"));

        Assert.Contains("class=\"version-list\"", html, StringComparison.Ordinal);
        Assert.Contains("version-card version-card--draft", html, StringComparison.Ordinal);
        Assert.Contains("version-card version-card--published", html, StringComparison.Ordinal);
        Assert.Contains("version-card version-card--historical", html, StringComparison.Ordinal);
        Assert.Contains("class=\"version-legend\"", html, StringComparison.Ordinal);
        Assert.Contains("steps</span>", html, StringComparison.Ordinal);
        // This workflow already has a Draft, so the header offers to edit it
        // rather than to start a second one.
        Assert.Contains("Edit Draft V4", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Create New Version</button>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowDesigner_ApprovalTypeDropdown_OffersTheForwardFlowApprovalsOnly()
    {
        // A Reopen Approval is raised against a ticket that has already
        // reached the terminal step, so it can never be a stage the ticket
        // passes through — the designer must not offer a step no ticket can
        // occupy. Accounting and Customer Service approvals are unaffected.
        var draft = await Ok(await GetAsync("/Admin/Workflows/Versions/2"));

        Assert.Contains("Accounting Approval", draft, StringComparison.Ordinal);
        Assert.Contains("Customer Service Approval", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("Reopen Approval", draft, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ApprovalType.ReopenApproval), draft, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowVersion_DrawsTheStepsAsATimeline_WithApprovalsMarked()
    {
        var draft = await Ok(await GetAsync("/Admin/Workflows/Versions/2"));

        Assert.Contains("class=\"panel__title\" id=\"steps-title\">Step timeline", draft, StringComparison.Ordinal);
        Assert.Contains("class=\"designer-step__num\"", draft, StringComparison.Ordinal);
        Assert.Contains("class=\"designer-step__card\"", draft, StringComparison.Ordinal);
        // The approval step is marked on the rail itself.
        Assert.Contains("designer-step designer-step--approval", draft, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-approval-type\"", draft, StringComparison.Ordinal);
        // Its outcome branches, and where each one leads.
        Assert.Contains("class=\"designer-step__branches\"", draft, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-outcome-approved designer-branch__outcome\">Approved</span>", draft, StringComparison.Ordinal);
        Assert.Contains("class=\"badge badge-outcome-rejected designer-branch__outcome\">Rejected</span>", draft, StringComparison.Ordinal);
        Assert.Contains("</strong> approval step", draft, StringComparison.Ordinal);
        // A draft is editable and can be published.
        Assert.Contains("handler=AddStep", draft, StringComparison.Ordinal);
        Assert.Contains(">Add step</h2>", draft, StringComparison.Ordinal);

        // A published version is the same timeline, read-only.
        var published = await Ok(await GetAsync("/Admin/Workflows/Versions/1"));
        Assert.Contains("class=\"alert alert-info alert-lock\"", published, StringComparison.Ordinal);
        Assert.Contains("designer-step--readonly", published, StringComparison.Ordinal);
        Assert.DoesNotContain(">Add step</h2>", published, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Channels_ShowCustomerEntryBehaviour_AsFlagsRatherThanYesNoColumns()
    {
        var list = await Ok(await GetAsync("/Admin/Channels"));

        Assert.Contains(">Customer entry behaviour</th>", list, StringComparison.Ordinal);
        Assert.Contains("class=\"flag flag--on\"", list, StringComparison.Ordinal);
        Assert.Contains("class=\"flag flag--off\"", list, StringComparison.Ordinal);
        Assert.Contains(">Offered in Create Ticket</span>", list, StringComparison.Ordinal);
        Assert.Contains(">Phone required</span>", list, StringComparison.Ordinal);
        Assert.Contains(">Genesys</span>", list, StringComparison.Ordinal);
        // The old three-column Yes/No table is gone.
        Assert.DoesNotContain(">Phone Required</th>", list, StringComparison.Ordinal);

        var edit = await Ok(await GetAsync("/Admin/Channels/1"));
        Assert.Contains("class=\"form-section__title\">Customer entry behaviour</legend>", edit, StringComparison.Ordinal);
        Assert.Contains(">Save Channel</button>", edit, StringComparison.Ordinal);
    }

    /// <summary>The sub-navigation marks the section the current page named, and only that one.</summary>
    [Theory]
    [InlineData("/Admin", "Overview")]
    [InlineData("/Admin/Users", "Users")]
    [InlineData("/Admin/Departments/1", "Departments")]
    [InlineData("/Admin/RequestTypes", "Request Types")]
    [InlineData("/Admin/Workflows/Versions/2", "Workflows")]
    [InlineData("/Admin/Channels/1", "Channels")]
    public async Task SubNav_MarksExactlyOneSection(string path, string expected)
    {
        var html = await Ok(await GetAsync(path));
        var nav = html[html.IndexOf("<nav class=\"admin-subnav\"", StringComparison.Ordinal)..];
        nav = nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];

        // Exactly one pill is selected, and it is the one this page named.
        Assert.Equal(1, nav.Split("is-active").Length - 1);
        var selected = nav[nav.IndexOf("is-active", StringComparison.Ordinal)..];
        selected = selected[..selected.IndexOf("</a>", StringComparison.Ordinal)];
        Assert.Contains("aria-current=\"page\"", selected, StringComparison.Ordinal);
        Assert.EndsWith(expected, selected.Trim(), StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", Roles.SystemAdministrator);
        return await client.GetAsync(path);
    }

    private static async Task<string> Ok(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
        return html;
    }

    private sealed class AdminAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.TryGetValue("X-Test-Role", out var header) ? header.ToString() : Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Administrator"),
                new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>
    /// A populated Administration Api: two users (one deactivated and locked
    /// out), two departments, two request types (one of them blocked on an
    /// unpublished workflow), two workflows with a three-version history, and
    /// two channels — enough for every list, badge and empty/partial state on
    /// the twelve screens to be exercised for real.
    /// </summary>
    private sealed class AdminApi : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var query = request.RequestUri.Query;
            var includeInactive = query.Contains("includeInactive=true", StringComparison.Ordinal);

            return path switch
            {
                "api/roles" => Json(Roles.All.Select(r => new RoleDto(r, r)).ToList()),
                "api/departments" => Json(new List<DepartmentDto>
                {
                    new(1, "Collections"),
                    new(2, "Finance")
                }),
                "api/admin/users" => Json(Users(includeInactive)),
                "api/admin/users/password-policy" => Json(new PasswordPolicyDto(8, true, true, true, true, 4)),
                _ when path == $"api/admin/users/{AlmaId}" => Json(Alma),
                _ when path == $"api/admin/users/{AlmaId}/permissions" => Json(AlmaPermissions),
                "api/admin/channels" => Json(Channels(includeInactive)),
                "api/admin/channels/1" => Json(Channels(true)[0]),
                "api/admin/departments" => Json(Departments(includeInactive)),
                "api/admin/departments/1" => Json(CollectionsDetail),
                "api/admin/departments/2" => Json(FinanceDetail),
                "api/admin/request-types" => Json(RequestTypes(includeInactive)),
                "api/admin/request-types/1" => Json(SendReceipts),
                "api/admin/sla/configuration" => Json(new TigerCS.Application.Modules.SlaAndEscalation.Dto.SlaConfigurationDto(
                    [new(1, "Critical", 15, 240, "24/7", 50, true), new(2, "High", 60, 1440, "Business hours", 75, true)],
                    new("Default", "08:00", "18:00", "Asia/Dubai", ["Sunday", "Monday"], 0, []),
                    "The SLA clock starts at classification.", "Targets come from the per-priority SLA policy.",
                    "First response is satisfied only by a recorded human response.", "Pause and resume are not implemented in this release.")),
                "api/admin/workflows" => Json(Workflows(includeInactive)),
                "api/admin/workflows/catalog" => Json(Catalog),
                "api/admin/workflows/1" => Json(ReceiptsWorkflow),
                "api/admin/workflows/versions/1" => Json(PublishedVersion),
                "api/admin/workflows/versions/2" => Json(DraftVersion),
                _ => await Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            };

            static HttpResponseMessage Json<T>(T body) =>
                new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new(System.Text.Json.JsonSerializerDefaults.Web)) };
        }

        private static AdminUserDto Alma => new(
            AlmaId, "alma.hassan", "alma@example.com", "Alma Hassan", false, true, null, Now.AddYears(-2), true,
            [Roles.SystemAdministrator, Roles.CsManager],
            [new DepartmentMembershipDto(1, "Collections", true), new DepartmentMembershipDto(2, "Finance", false)],
            true);

        private static UserPermissionsDto AlmaPermissions => new(
            AlmaId, "Alma Hassan", "alma.hassan", true, [Roles.CsManager], false, false, false,
            [
                new("Tickets",
                [
                    new("Tickets.View", "View", "View tickets", "Open tickets.", false, true, "Inherit", true, "Role", true),
                    new("Tickets.Reopen", "Reopen", "Reopen tickets", "Reopen a closed ticket.", false, true, "Deny", false, "Denied", true)
                ]),
                new("Administration",
                [
                    new("Administration.Permissions.Manage", "Manage", "Manage permissions", "Change overrides.", true, false, "Inherit", false, "None", false)
                ])
            ],
            [new PermissionChangeDto(Now, "Root Admin", "Tickets.Reopen", "Permission=Tickets.Reopen;Override=Inherit;Effective=true", "Permission=Tickets.Reopen;Override=Deny;Effective=false")]);

        private static AdminUserDto Ravi => new(
            RaviId, "ravi.menon", null, "Ravi Menon", true, false, Now.AddMonths(-3), Now.AddYears(-1), false,
            [], [], true);

        private static AdminUserListDto Users(bool includeInactive) =>
            includeInactive ? new([Alma, Ravi], 1, 25, 2) : new([Alma], 1, 25, 1);

        private static List<AdminDepartmentDto> Departments(bool includeInactive) =>
            includeInactive
                ? [new(1, "Collections", "COL", true, 4, 120, 2), new(2, "Finance", "FIN", false, 1, 8, 0)]
                : [new(1, "Collections", "COL", true, 4, 120, 2)];

        private static AdminDepartmentDetailDto CollectionsDetail => new(
            1, "Collections", "COL", true, 120, 2,
            [
                new(AlmaId, "Alma Hassan", true, true, [Roles.SystemAdministrator]),
                new(RaviId, "Ravi Menon", false, false, [])
            ]);

        private static AdminDepartmentDetailDto FinanceDetail => new(2, "Finance", "FIN", false, 8, 0, []);

        private static List<AdminRequestTypeSummaryDto> RequestTypes(bool includeInactive) =>
            includeInactive
                ? [
                    new(1, "Send Receipts", 1, "Collections", true, 1, "Receipts Workflow", 3, "Collections Queue", 3, 42),
                    new(2, "Refund Request", 2, "Finance", true, 2, "Refund Workflow", null, "Specific Employee — Alma Hassan", 2, 0)
                  ]
                : [new(1, "Send Receipts", 1, "Collections", true, 1, "Receipts Workflow", 3, "Collections Queue", 3, 42)];

        private static AdminRequestTypeDetailDto SendReceipts => new(
            1, "Send Receipts", 1, "Collections", true, 3, true, true, false, true, null, 42,
            new WorkflowLinkDto(1, "Receipts Workflow", true, 1, 3, 2, 4, [ApprovalType.AccountingApproval]),
            new AssignmentRuleDto(AssignmentMode.DepartmentQueue, null, null, null, [], true),
            [
                new(ApprovalType.AccountingApproval, "Accounting Approval", ApprovalTargetKind.Department, 2, "Finance", Roles.DepartmentHead, null, null, true, true)
            ],
            [
                new(3, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, 2, 10, 12, false, null, true, null, 80m, true)
            ]);

        private static List<AdminWorkflowSummaryDto> Workflows(bool includeInactive) =>
            includeInactive
                ? [
                    new(1, "Receipts Workflow", "Everything from a receipt request to its close.", true, 1, 3, 2, 4, 3, 1, 42),
                    new(2, "Refund Workflow", null, true, null, null, 5, 1, 1, 1, 0)
                  ]
                : [
                    new(1, "Receipts Workflow", "Everything from a receipt request to its close.", true, 1, 3, 2, 4, 3, 1, 42),
                    new(2, "Refund Workflow", null, true, null, null, 5, 1, 1, 1, 0)
                  ];

        private static AdminWorkflowDetailDto ReceiptsWorkflow => new(
            1, "Receipts Workflow", "Everything from a receipt request to its close.", true, Now.AddYears(-1),
            [
                new(2, 4, WorkflowVersionStatus.Draft, "V4 draft", Now.AddDays(-3), "Alma Hassan", null, null, 5, 0, true),
                new(1, 3, WorkflowVersionStatus.Published, "V3", Now.AddMonths(-4), "Alma Hassan", Now.AddMonths(-4), "Alma Hassan", 5, 42, false),
                new(3, 2, WorkflowVersionStatus.Historical, "V2", Now.AddYears(-1), "Alma Hassan", Now.AddMonths(-9), "Alma Hassan", 4, 18, false)
            ],
            [new(1, "Send Receipts", "Collections", true)]);

        private static List<WorkflowVersionStepDto> Steps =>
        [
            new(11, 1, "Start", WorkflowStepKind.Created, "Created", false, null, null, false, false, []),
            new(12, 2, "Collections Queue", WorkflowStepKind.InProgress, "In Progress", false, null, null, false, false, []),
            new(13, 3, "Accounting Approval", WorkflowStepKind.WaitingForApproval, "Waiting for Approval", false,
                ApprovalType.AccountingApproval, "Accounting Approval", true, true,
                [new(WorkflowStepOutcome.Approved, 14, 4, "Resolve")]),
            new(14, 4, "Resolve", WorkflowStepKind.Resolved, "Resolved", false, null, null, false, false, []),
            new(15, 5, "Close", WorkflowStepKind.Closed, "Closed", true, null, null, false, false, [])
        ];

        private static WorkflowVersionDetailDto DraftVersion => new(
            2, 1, "Receipts Workflow", 4, WorkflowVersionStatus.Draft, "V4 draft", "Adds the accounting approval.",
            true, false, true, Now.AddDays(-3), "Alma Hassan", null, null, 0, true, false, true, Steps, []);

        private static WorkflowVersionDetailDto PublishedVersion => new(
            1, 1, "Receipts Workflow", 3, WorkflowVersionStatus.Published, "V3", null,
            true, false, true, Now.AddMonths(-4), "Alma Hassan", Now.AddMonths(-4), "Alma Hassan", 42, false, false, false, Steps, []);

        private static WorkflowDesignerCatalogDto Catalog => new(
            [
                new(WorkflowStepKind.Created, "Created", "The ticket has just been raised.", false, false, true, false),
                new(WorkflowStepKind.InProgress, "In Progress", "Someone is working on it.", false, false, false, false),
                new(WorkflowStepKind.WaitingForApproval, "Waiting for Approval", "A decision is needed before work continues.", true, true, false, false),
                new(WorkflowStepKind.Resolved, "Resolved", "The work is done.", false, false, false, false),
                new(WorkflowStepKind.Closed, "Closed", "The ticket is closed.", false, false, false, true)
            ],
            // The real catalog, not a transcription of it — so the designer's
            // dropdown is rendered from exactly what the Api would serve, and
            // a type excluded by ApprovalTypeRules is genuinely absent here.
            AdminWorkflowAppService.Catalog().ApprovalTypes);

        private static List<AdminChannelDto> Channels(bool includeInactive) =>
            includeInactive
                ? [new(1, "Phone", "PHONE", true, true, true, 1, 980), new(2, "Walk-in", "WALKIN", false, false, false, 5, 12)]
                : [new(1, "Phone", "PHONE", true, true, true, 1, 980)];
    }
}
