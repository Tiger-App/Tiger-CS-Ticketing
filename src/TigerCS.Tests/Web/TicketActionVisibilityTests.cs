// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' action affordances against the Api's own authorization.
///
/// <para>
/// The bug these cover: the Resolve control rendered for every viewer of a
/// non-Closed ticket, while <c>TicketLifecycleAppService.IsResolveAuthorizedAsync</c>
/// admits only Department Employee (as current owner) and Department Head (in
/// the ticket's department). A CS Manager was shown the button and answered
/// 403. Assign, Change Status and Escalate carried the same gap.
/// </para>
///
/// <para>
/// These are display assertions. The rules themselves are enforced server-side
/// and covered by TicketLifecycleAppServiceTests, TicketAssignmentAppServiceTests
/// and TicketEscalationAppServiceTests — which are unchanged, because the
/// server rules are unchanged. What matters here is that the page offers
/// exactly what those rules would accept.
/// </para>
/// </summary>
public sealed class TicketActionVisibilityTests
{
    private const int TicketDepartmentId = 1;
    private const int OtherDepartmentId = 7;

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Stranger = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ---------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------

    private static TicketActionContext Context(
        Guid viewer,
        string[] roles,
        Guid? currentOwner = null,
        int[]? viewerDepartmentIds = null) =>
        new(roles, viewer, currentOwner ?? Owner, TicketDepartmentId, viewerDepartmentIds ?? []);

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string TicketDetailsViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

    private static ClaimsPrincipal Principal(Guid employeeId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, employeeId.ToString()),
            new(ClaimTypes.Name, "Test Viewer")
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static TicketDetailDto Ticket(Guid? owner) => new(
        1, "TG-FM-20260905-0001", TicketDepartmentId, TicketDepartmentId, owner, null, null,
        5, 3, "InProgress", "Verified", "None", "Running", null, null, "AC not cooling", 0,
        DateTime.UtcNow, Convert.ToBase64String([1, 2, 3, 4]));

    /// <summary>
    /// Stands in for the Api. GET /api/users/me carries the viewer's real
    /// department memberships — the same UserDepartmentAssignments rows the
    /// Api's own ExistsAsync check reads — so the page's department-scoped
    /// affordances are decided from server-supplied data, not a claim guess.
    /// </summary>
    private static Func<HttpRequestMessage, string?, HttpResponseMessage> Api(
        Guid viewer, string[] roles, Guid? ticketOwner, int[] viewerDepartmentIds, bool meAvailable = true) =>
        (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/users/me")
            {
                return meAvailable
                    ? FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CurrentUserResponseDto(
                        viewer, "Test Viewer", roles,
                        [.. viewerDepartmentIds.Select(id => new DepartmentMembershipDto(id, $"Department {id}", id == viewerDepartmentIds[0]))],
                        IsGeynessStaff: false))
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (path == "/api/tickets/1" && request.Method == HttpMethod.Get)
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, Ticket(ticketOwner));
            }

            // Directory, SLA, notes, escalations, members, history, approvals:
            // every one of these failing is tolerated by the page.
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

    private static async Task<TicketDetailsModel> RenderAsync(
        Guid viewer, string[] roles, Guid? ticketOwner, params int[] viewerDepartmentIds)
    {
        var handler = new FakeApiHandler(Api(viewer, roles, ticketOwner, viewerDepartmentIds));
        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };

        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var departments = new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance);
        var model = new TicketDetailsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            users,
            new TicketNameResolver(users, departments));

        model.PageContext = new PageContext(new ActionContext(
            new DefaultHttpContext { User = Principal(viewer, roles) }, new RouteData(), new PageActionDescriptor()));

        await model.OnGetAsync(1, null, CancellationToken.None);
        return model;
    }

    // ===============================================================
    // Resolve — the reported bug
    // ===============================================================

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsAgent)]
    public void Resolve_IsOfferedToTheCsLayerOnlyWhenTheyAreTheAssignee(string csRole)
    {
        // The reported repro: a CS Manager who IS the assignee, on a ticket in
        // a department they belong to. TicketRoleSets.Resolve excludes the CS
        // layer outright (ISSUE-022's Resolve/Close split), so neither
        // ownership nor membership may rescue it. CS resolves nothing; CS
        // closes and reopens.
        // Updated rule: whoever the ticket is assigned to may resolve it, so the
        // assignee is offered Resolve; a CS user who is NOT the assignee is not.
        var context = Context(Owner, [csRole], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId]);
        Assert.True(TicketActions.CanResolve(context));
        Assert.False(TicketActions.CanResolve(
            Context(Owner, [csRole], currentOwner: Stranger, viewerDepartmentIds: [TicketDepartmentId])));

        // ... and the actions the CS layer DOES hold are untouched.
        Assert.True(TicketActions.CanReopen([csRole]));
    }

    [Fact]
    public void Resolve_IsOfferedToTheDepartmentEmployeeWhoOwnsTheTicket()
    {
        Assert.True(TicketActions.CanResolve(
            Context(Owner, [Roles.DepartmentEmployee], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));
    }

    [Fact]
    public void Resolve_IsNotOfferedToADepartmentEmployeeWhoIsNotTheOwner_EvenInTheTicketsDepartment()
    {
        // The server's non-DepartmentHead branch is ownership, not membership:
        // ticket.CurrentOwnerEmployeeId == callerEmployeeId.
        Assert.False(TicketActions.CanResolve(
            Context(Stranger, [Roles.DepartmentEmployee], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));

        // An unassigned ticket has no owner to match, so nobody qualifies that way.
        Assert.False(TicketActions.CanResolve(
            Context(Stranger, [Roles.DepartmentEmployee], currentOwner: null, viewerDepartmentIds: [TicketDepartmentId])));
    }

    [Fact]
    public void Resolve_IsOfferedToADepartmentHeadOfTheTicketsDepartment_WithoutOwningIt()
    {
        Assert.True(TicketActions.CanResolve(
            Context(Stranger, [Roles.DepartmentHead], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));

        // Membership of a SECOND department counts — which is why the check
        // reads the full memberships list and not the primary-department claim.
        Assert.True(TicketActions.CanResolve(
            Context(Stranger, [Roles.DepartmentHead], currentOwner: Owner, viewerDepartmentIds: [OtherDepartmentId, TicketDepartmentId])));
    }

    [Fact]
    public void Resolve_IsNotOfferedToADepartmentHeadOfAnotherDepartment()
    {
        Assert.False(TicketActions.CanResolve(
            Context(Stranger, [Roles.DepartmentHead], currentOwner: Owner, viewerDepartmentIds: [OtherDepartmentId])));
    }

    [Fact]
    public void Resolve_IsOfferedToTheSystemAdministrator_ThroughTheAdr0024Override()
    {
        // Absent from TicketRoleSets.Resolve by design, authorized by the
        // central override — neither owner nor a member of the department.
        Assert.DoesNotContain(Roles.SystemAdministrator, TicketRoleSets.Resolve);
        Assert.True(TicketActions.CanResolve(
            Context(Stranger, [Roles.SystemAdministrator], currentOwner: Owner, viewerDepartmentIds: [])));
    }

    [Theory]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public void Resolve_IsNotOfferedToRolesOutsideTheResolveSet(string role)
    {
        Assert.False(TicketActions.CanResolve(
            Context(Owner, [role], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));
    }

    [Fact]
    public void Resolve_IsNotOfferedWhenThereIsNoViewerAtAll()
    {
        Assert.False(TicketActions.CanResolve(null));
    }

    // ===============================================================
    // Assign / Change Status / Escalate — the same class of gap
    // ===============================================================

    [Fact]
    public void Assign_MirrorsTheApisCrossDepartmentAndWithinDepartmentSplit()
    {
        // CS Manager assigns cross-department, no membership needed.
        Assert.True(TicketActions.CanAssign(Context(Stranger, [Roles.CsManager], viewerDepartmentIds: [])));

        // CS Supervisor and Department Head only inside a department they belong to.
        Assert.True(TicketActions.CanAssign(Context(Stranger, [Roles.CsSupervisor], viewerDepartmentIds: [TicketDepartmentId])));
        Assert.True(TicketActions.CanAssign(Context(Stranger, [Roles.DepartmentHead], viewerDepartmentIds: [TicketDepartmentId])));
        Assert.False(TicketActions.CanAssign(Context(Stranger, [Roles.CsSupervisor], viewerDepartmentIds: [OtherDepartmentId])));
        Assert.False(TicketActions.CanAssign(Context(Stranger, [Roles.DepartmentHead], viewerDepartmentIds: [OtherDepartmentId])));

        Assert.True(TicketActions.CanAssign(Context(Stranger, [Roles.SystemAdministrator], viewerDepartmentIds: [])));
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public void Assign_IsNotOfferedToRolesWithNoAssignmentCapability_NotEvenSelfClaimByTheOwner(string role)
    {
        // The PR correction is explicit: CS Agent and Department Employee hold
        // no assignment capability at all, and GM/Chairman/Reporting User hold
        // none operationally. Owning the ticket does not add one.
        Assert.False(TicketActions.CanAssign(
            Context(Owner, [role], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));
    }

    [Fact]
    public void ChangeStatus_IsOfferedToTheOwner_ToSupervisorsAbove_AndToTheDepartmentsHead()
    {
        // Ownership alone authorizes, whatever the role.
        Assert.True(TicketActions.CanChangeStatus(
            Context(Owner, [Roles.DepartmentEmployee], currentOwner: Owner, viewerDepartmentIds: [])));

        foreach (var supervisory in new[] { Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager })
        {
            Assert.True(TicketActions.CanChangeStatus(Context(Stranger, [supervisory], viewerDepartmentIds: [])));
        }

        // Chairman/CEO is read-only: no status control, even for a ticket it somehow owns.
        Assert.False(TicketActions.CanChangeStatus(Context(Stranger, [Roles.ChairmanCeo], viewerDepartmentIds: [])));
        Assert.False(TicketActions.CanChangeStatus(
            Context(Owner, [Roles.ChairmanCeo], currentOwner: Owner, viewerDepartmentIds: [])));

        Assert.True(TicketActions.CanChangeStatus(Context(Stranger, [Roles.DepartmentHead], viewerDepartmentIds: [TicketDepartmentId])));
        Assert.True(TicketActions.CanChangeStatus(Context(Stranger, [Roles.SystemAdministrator], viewerDepartmentIds: [])));
    }

    [Fact]
    public void ChangeStatus_IsNotOfferedToANonOwnerWithoutSupervisoryOrDepartmentAuthority()
    {
        // CS Agent is NOT in CrossDepartmentSupervisory — a non-owning agent is refused.
        Assert.False(TicketActions.CanChangeStatus(
            Context(Stranger, [Roles.CsAgent], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));

        Assert.False(TicketActions.CanChangeStatus(
            Context(Stranger, [Roles.DepartmentEmployee], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));

        // A Department Head of some OTHER department has no authority here.
        Assert.False(TicketActions.CanChangeStatus(
            Context(Stranger, [Roles.DepartmentHead], currentOwner: Owner, viewerDepartmentIds: [OtherDepartmentId])));

        Assert.False(TicketActions.CanChangeStatus(
            Context(Stranger, [Roles.ReportingUser], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));
    }

    [Fact]
    public void Escalate_MirrorsTheManualFlagTier_IncludingItsDepartmentScoping()
    {
        // CS layer is cross-department for ManualFlag.
        Assert.True(TicketActions.CanEscalate(Context(Stranger, [Roles.CsAgent], viewerDepartmentIds: [])));

        // Department-side roles are scoped to the ticket's department unless they own it.
        Assert.True(TicketActions.CanEscalate(
            Context(Stranger, [Roles.DepartmentEmployee], viewerDepartmentIds: [TicketDepartmentId])));
        Assert.False(TicketActions.CanEscalate(
            Context(Stranger, [Roles.DepartmentEmployee], currentOwner: Owner, viewerDepartmentIds: [OtherDepartmentId])));
        Assert.True(TicketActions.CanEscalate(
            Context(Owner, [Roles.DepartmentEmployee], currentOwner: Owner, viewerDepartmentIds: [OtherDepartmentId])));

        // Reporting User holds no escalation at all, at either tier.
        Assert.False(TicketActions.CanEscalate(
            Context(Owner, [Roles.ReportingUser], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));

        Assert.True(TicketActions.CanEscalate(Context(Stranger, [Roles.SystemAdministrator], viewerDepartmentIds: [])));
    }

    [Fact]
    public void EscalateToLevel4_IsCsManagerOrGeneralManagerOnly_NeverWidenedByOwnershipOrDepartment()
    {
        Assert.True(TicketActions.CanEscalateToLevel4(Context(Stranger, [Roles.CsManager], viewerDepartmentIds: [])));
        Assert.True(TicketActions.CanEscalateToLevel4(Context(Stranger, [Roles.GeneralManager], viewerDepartmentIds: [])));
        Assert.True(TicketActions.CanEscalateToLevel4(Context(Stranger, [Roles.SystemAdministrator], viewerDepartmentIds: [])));

        // Owning the ticket in its own department does not grant Level 4.
        foreach (var role in new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.DepartmentEmployee, Roles.DepartmentHead, Roles.ChairmanCeo })
        {
            Assert.False(TicketActions.CanEscalateToLevel4(
                Context(Owner, [role], currentOwner: Owner, viewerDepartmentIds: [TicketDepartmentId])));
        }
    }

    [Fact]
    public void Close_IsTheCsLayerOnly_TheMirrorImageOfResolve()
    {
        // ISSUE-022's split, from the other side: the roles that may Close are
        // exactly the ones that may not Resolve, and vice versa.
        foreach (var csRole in new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager })
        {
            Assert.True(TicketActions.CanClose([csRole]));
            Assert.False(TicketActions.CanResolve(
                Context(Owner, [csRole], currentOwner: Stranger, viewerDepartmentIds: [TicketDepartmentId])));
        }

        foreach (var departmentRole in new[] { Roles.DepartmentEmployee, Roles.DepartmentHead })
        {
            Assert.False(TicketActions.CanClose([departmentRole]));
        }

        Assert.False(TicketActions.CanClose([Roles.GeneralManager]));
        Assert.False(TicketActions.CanClose([Roles.ChairmanCeo]));
        Assert.False(TicketActions.CanClose([Roles.ReportingUser]));
        Assert.False(TicketActions.CanClose([]));
        Assert.False(TicketActions.CanClose(null));

        Assert.True(TicketActions.CanClose([Roles.SystemAdministrator]));
    }

    // ===============================================================
    // End to end: the page computes these from GET /api/users/me
    // ===============================================================

    [Fact]
    public async Task TicketDetails_CsManagerAssignedToTheTicket_IsOfferedCloseAndTransferButNotResolve()
    {
        // The exact reported repro, end to end through the page.
        var model = await RenderAsync(Owner, [Roles.CsManager], ticketOwner: Owner, TicketDepartmentId);

        Assert.NotNull(model.Ticket);
        Assert.True(model.CanResolve); // the assignee may resolve, whatever their role

        // The CS layer's own lifecycle authority is unchanged by this fix.
        Assert.True(model.CanTransfer);
        Assert.True(model.CanReopen);
        Assert.True(model.CanChangeStatus);
        Assert.True(model.CanAssign);
        Assert.True(model.CanClose);
    }

    [Fact]
    public async Task TicketDetails_DepartmentEmployeeOwner_IsOfferedResolve_ButNotAssignOrTransfer()
    {
        var model = await RenderAsync(Owner, [Roles.DepartmentEmployee], ticketOwner: Owner, TicketDepartmentId);

        Assert.True(model.CanResolve);
        Assert.True(model.CanChangeStatus);
        Assert.False(model.CanAssign);
        Assert.False(model.CanTransfer);
        Assert.False(model.CanReopen);
        Assert.True(model.CanClose); // the assignee may close, whatever their department/role
    }

    [Fact]
    public async Task TicketDetails_DepartmentEmployeeWhoIsNotTheOwner_IsNotOfferedResolve()
    {
        var model = await RenderAsync(Stranger, [Roles.DepartmentEmployee], ticketOwner: Owner, TicketDepartmentId);

        Assert.False(model.CanResolve);
        Assert.False(model.CanChangeStatus);
    }

    [Fact]
    public async Task TicketDetails_DepartmentHeadOfTheTicketsDepartment_IsOfferedResolve()
    {
        var model = await RenderAsync(Stranger, [Roles.DepartmentHead], ticketOwner: Owner, TicketDepartmentId);

        Assert.True(model.CanResolve);
        Assert.True(model.CanAssign);
        Assert.True(model.CanChangeStatus);
    }

    [Fact]
    public async Task TicketDetails_DepartmentHeadOfAnotherDepartment_IsNotOfferedResolve()
    {
        var model = await RenderAsync(Stranger, [Roles.DepartmentHead], ticketOwner: Owner, OtherDepartmentId);

        Assert.False(model.CanResolve);
        Assert.False(model.CanAssign);
        Assert.False(model.CanChangeStatus);
    }

    [Fact]
    public async Task TicketDetails_SystemAdministrator_IsOfferedResolve_ThroughTheOverride()
    {
        // Member of no department and not the owner: only the override can explain this.
        var model = await RenderAsync(Stranger, [Roles.SystemAdministrator], ticketOwner: Owner, OtherDepartmentId);

        Assert.True(model.CanResolve);
        Assert.True(model.CanAssign);
        Assert.True(model.CanChangeStatus);
        Assert.True(model.CanEscalate);
    }

    [Fact]
    public async Task TicketDetails_WhenUsersMeIsUnavailable_DepartmentScopedControlsFailClosed()
    {
        // No membership data means no membership claim: a Department Head is
        // shown nothing they might be refused for, rather than being offered a
        // control on an assumption.
        var handler = new FakeApiHandler(
            Api(Stranger, [Roles.DepartmentHead], Owner, [TicketDepartmentId], meAvailable: false));
        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };

        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var model = new TicketDetailsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            users,
            new TicketNameResolver(users, new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)));
        model.PageContext = new PageContext(new ActionContext(
            new DefaultHttpContext { User = Principal(Stranger, Roles.DepartmentHead) }, new RouteData(), new PageActionDescriptor()));

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.NotNull(model.Ticket);
        Assert.False(model.CanResolve);
        Assert.False(model.CanAssign);
        Assert.False(model.CanChangeStatus);
    }

    [Fact]
    public async Task TicketDetails_ReadsMembershipFromUsersMe_NotFromThePrimaryDepartmentClaim()
    {
        // Proves the data source: the page asked the Api who the viewer is,
        // and the affordance followed that answer.
        var handler = new FakeApiHandler(Api(Stranger, [Roles.DepartmentHead], Owner, [OtherDepartmentId, TicketDepartmentId]));
        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };

        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var model = new TicketDetailsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            users,
            new TicketNameResolver(users, new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)));
        model.PageContext = new PageContext(new ActionContext(
            new DefaultHttpContext { User = Principal(Stranger, Roles.DepartmentHead) }, new RouteData(), new PageActionDescriptor()));

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.Contains(handler.Requests, r => r.RequestUri.EndsWith("/api/users/me", StringComparison.Ordinal));

        // The ticket's department is the viewer's SECOND membership — a
        // primary-department-only check would have got this wrong.
        Assert.Equal([OtherDepartmentId, TicketDepartmentId], model.ActionContext!.ViewerDepartmentIds);
        Assert.True(model.CanResolve);
    }

    // ===============================================================
    // The view actually consumes these flags
    // ===============================================================

    [Fact]
    public void EveryMutatingControl_IsWrappedInItsOwnAuthorizationFlag()
    {
        var html = TicketDetailsViewHtml();

        foreach (var gate in new[]
                 {
                     "@if (Model.CanAssign)",
                     "@if (Model.CanTransfer)",
                     "@if (Model.CanChangeStatus)",
                     "@if (Model.CanResolve)",
                     "@if (Model.CanEscalate)",
                     "@if (isResolved && Model.CanClose)"
                 })
        {
            Assert.Contains(gate, html, StringComparison.Ordinal);
        }

        // Level 4 is offered only where the ManualLevel4 tier allows it.
        Assert.Contains("var topLevel = Model.CanEscalateToLevel4 ? 4 : 3;", html);
        Assert.DoesNotContain("for (byte lvl = 1; lvl <= 4; lvl++)", html);
    }

    [Fact]
    public void TheView_CarriesNoRoleListOfItsOwn()
    {
        // The whole point of the fix: visibility comes from the model's flags,
        // which read the Api's canonical sets. A role name typed into the view
        // would be a second rule, free to drift from the endpoint's.
        var html = TicketDetailsViewHtml();

        foreach (var role in Roles.All)
        {
            Assert.DoesNotContain($"\"{role}\"", html, StringComparison.Ordinal);
        }

        // No role-set MEMBER is read in the view either — the flags on the
        // model are the only source. (Prose naming the classes is fine; a
        // dotted reference would be a rule living in the view.)
        Assert.DoesNotContain("Roles.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("TicketRoleSets.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("SlaRoleSets.", html, StringComparison.Ordinal);
    }
}
