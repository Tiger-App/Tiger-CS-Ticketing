// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Department UX: operational users work in department NAMES. Ticket
/// Details, the Queue, the Dashboard and Customer History resolve every
/// department through the Department directory, and Transfer offers a picker
/// of names bound to the existing DepartmentId — never a typed number, and
/// never a destination the Api would refuse for the viewer's role.
/// </summary>
public sealed class DepartmentUxTests
{
    private const int FacilityManagementId = 1;
    private const int CollectionsId = 3;
    private const int RegistrationId = 4;
    private const int DeactivatedDeskId = 9;

    private static readonly DepartmentDto[] ActiveDirectory =
    [
        new(RegistrationId, "Registration"),
        new(FacilityManagementId, "Facility Management"),
        new(CollectionsId, "Collections")
    ];

    private static readonly DepartmentDto[] FullDirectory =
    [
        .. ActiveDirectory,
        new(DeactivatedDeskId, "Legacy Desk")
    ];

    // ---------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string View(params string[] pathUnderPages) =>
        File.ReadAllText(SourceFile(Path.Combine(["TigerCS.Web", "Pages", .. pathUnderPages])));

    private static ClaimsPrincipal PrincipalWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Test Manager")
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static void GivePageContext(PageModel model, ClaimsPrincipal principal) =>
        model.PageContext = new PageContext(new ActionContext(
            new DefaultHttpContext { User = principal }, new RouteData(), new PageActionDescriptor()));

    private static TicketDetailDto Ticket(int currentDepartmentId = FacilityManagementId) => new(
        1, "TG-FM-20260905-0001", currentDepartmentId, currentDepartmentId, null, null, null,
        5, 3, "Open", "Verified", "None", "Running", null, null, "AC not cooling", 0,
        DateTime.UtcNow, Convert.ToBase64String([1, 2, 3, 4]));

    /// <summary>
    /// One responder standing in for the whole Api. The viewer is deliberately
    /// a member of NO department (users/me fails, a tolerated outcome), so any
    /// resolved name can only have come from the Department directory.
    /// </summary>
    private static Func<HttpRequestMessage, string?, HttpResponseMessage> Api(
        bool directoryAvailable = true, int currentDepartmentId = FacilityManagementId) =>
        (request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/departments")
            {
                if (!directoryAvailable)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                var activeOnly = !uri.Query.Contains("activeOnly=false", StringComparison.Ordinal);
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, activeOnly ? ActiveDirectory : FullDirectory);
            }

            if (uri.AbsolutePath == "/api/tickets/1" && request.Method == HttpMethod.Get)
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, Ticket(currentDepartmentId));
            }

            if (uri.AbsolutePath == "/api/tickets/1/transfer" && request.Method == HttpMethod.Post)
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, Ticket(CollectionsId));
            }

            // users/me, SLA, notes, escalations, members, history, approvals:
            // every one of these failing is tolerated by the page.
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

    private static (TicketDetailsModel Model, FakeApiHandler Handler) CreateTicketDetails(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder, params string[] roles)
    {
        var handler = new FakeApiHandler(responder);
        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };

        var tickets = new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance);
        var sla = new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance);
        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var departments = new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance);
        var resolver = new TicketNameResolver(users, departments);

        var model = new TicketDetailsModel(tickets, sla, users, resolver);
        GivePageContext(model, PrincipalWithRoles(roles));
        return (model, handler);
    }

    // ---------------------------------------------------------------
    // Names render instead of raw ids
    // ---------------------------------------------------------------

    [Fact]
    public async Task NameResolver_ResolvesADepartmentTheViewerIsNotAMemberOf_FromTheDirectory()
    {
        var handler = new FakeApiHandler(Api());
        var resolver = new TicketNameResolver(
            new UsersApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<UsersApiClient>.Instance),
            new DepartmentsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<DepartmentsApiClient>.Instance));

        await resolver.PrimeDepartmentsAsync(CancellationToken.None);

        Assert.True(resolver.DepartmentDirectoryAvailable);
        Assert.Empty(resolver.OwnDepartments);
        Assert.Equal("Facility Management", resolver.TryGetDepartmentName(FacilityManagementId));
        Assert.Equal("Collections", resolver.TryGetDepartmentName(CollectionsId));

        // A department deactivated since a ticket landed in it still has a
        // name — the name lookup asks for the FULL directory.
        Assert.Equal("Legacy Desk", resolver.TryGetDepartmentName(DeactivatedDeskId));
        Assert.Contains(handler.Requests, r => r.RequestUri.EndsWith("/api/departments?activeOnly=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TicketDetails_ResolvesTheDepartmentName_ForAViewerOutsideThatDepartment()
    {
        var (model, _) = CreateTicketDetails(Api(), Roles.CsManager);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.NotNull(model.Ticket);
        Assert.Equal("Facility Management", model.DepartmentName);
        Assert.Equal("Facility Management", TicketDisplay.AssignedDepartmentLabel(model.Ticket!.CurrentDepartmentId, model.DepartmentName));
        Assert.Equal("Facility Management Queue (Unassigned)", TicketDisplay.AssignedToLabel(null, null, model.Ticket.CurrentDepartmentId, model.DepartmentName));
    }

    [Fact]
    public async Task TicketDetails_WhenTheDirectoryIsDown_DegradesToNeutralWording_NeverARawId()
    {
        var (model, _) = CreateTicketDetails(Api(directoryAvailable: false), Roles.CsManager);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.NotNull(model.Ticket);
        Assert.Null(model.DepartmentName);

        var label = TicketDisplay.AssignedDepartmentLabel(model.Ticket!.CurrentDepartmentId, model.DepartmentName);
        Assert.Equal(TicketDisplay.UnknownDepartmentLabel, label);
        Assert.DoesNotContain("#", label, StringComparison.Ordinal);
        Assert.DoesNotContain("1", label, StringComparison.Ordinal);

        // The transfer form says the list is unavailable rather than
        // offering an empty picker or, worse, a number box.
        Assert.True(model.CanTransfer);
        Assert.True(model.TransferTargetsUnavailable);
        Assert.Empty(model.TransferTargets);
    }

    // ---------------------------------------------------------------
    // Transfer picker: names bound to ids, only authorized destinations
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketDetails_OffersTransferTargetsByName_ActiveOnly_ExcludingTheCurrentDepartment()
    {
        var (model, handler) = CreateTicketDetails(Api(), Roles.CsManager);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanTransfer);
        Assert.False(model.TransferTargetsUnavailable);

        // Names, ordered; the ticket's own department (Facility Management)
        // and the deactivated Legacy Desk are never offered — the Api would
        // reject both.
        Assert.Equal(["Collections", "Registration"], model.TransferTargets.Select(d => d.Name));
        Assert.Equal([CollectionsId, RegistrationId], model.TransferTargets.Select(d => d.DepartmentId));
        Assert.DoesNotContain(model.TransferTargets, d => d.DepartmentId == FacilityManagementId);
        Assert.DoesNotContain(model.TransferTargets, d => d.DepartmentId == DeactivatedDeskId);

        // The picker's candidates came from the ACTIVE directory call.
        Assert.Contains(handler.Requests, r => r.RequestUri.EndsWith("/api/departments?activeOnly=true", StringComparison.Ordinal));

        // The form defaults to a real, offered target — never to the current department.
        Assert.Equal(CollectionsId, model.Transfer.TargetDepartmentId);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ReportingUser)]
    public async Task TicketDetails_OffersNoTransferTargets_ToARoleWithoutTransferAuthority(string role)
    {
        var (model, handler) = CreateTicketDetails(Api(), role);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.False(model.CanTransfer);
        Assert.Empty(model.TransferTargets);
        Assert.False(model.TransferTargetsUnavailable);

        // Not a single destination is even fetched for a viewer who cannot
        // transfer — the active-only picker call never happens. (The full
        // directory is still read: it only puts a NAME on the department.)
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri.EndsWith("/api/departments?activeOnly=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TicketDetails_SystemAdministrator_IsOfferedTransferTargets_ViaTheOverride()
    {
        var (model, _) = CreateTicketDetails(Api(), Roles.SystemAdministrator);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanTransfer);
        Assert.Equal(["Collections", "Registration"], model.TransferTargets.Select(d => d.Name));
    }

    [Fact]
    public void TicketActions_CanTransfer_MirrorsTheApiTransferRoleSetPlusTheAdminOverride()
    {
        Assert.True(TicketActions.CanTransfer([Roles.CsManager]));
        Assert.True(TicketActions.CanTransfer([Roles.SystemAdministrator]));

        Assert.False(TicketActions.CanTransfer([Roles.CsAgent]));
        Assert.False(TicketActions.CanTransfer([Roles.CsSupervisor]));
        Assert.False(TicketActions.CanTransfer([Roles.DepartmentHead]));
        Assert.False(TicketActions.CanTransfer([Roles.DepartmentEmployee]));
        Assert.False(TicketActions.CanTransfer([Roles.GeneralManager]));
        Assert.False(TicketActions.CanTransfer([Roles.ChairmanCeo]));
        Assert.False(TicketActions.CanTransfer([Roles.ReportingUser]));
        Assert.False(TicketActions.CanTransfer([]));
        Assert.False(TicketActions.CanTransfer(null));
    }

    // ---------------------------------------------------------------
    // The selected NAME posts the underlying DepartmentId — contract unchanged
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketDetails_PostingTheSelectedDepartment_SendsItsDepartmentIdOnTheExistingTransferContract()
    {
        var (model, handler) = CreateTicketDetails(Api(), Roles.CsManager);
        await model.OnGetAsync(1, null, CancellationToken.None);

        // The user picked "Registration" from the picker; the browser posts
        // that option's value — the existing DepartmentId, nothing typed.
        var registration = Assert.Single(model.TransferTargets, d => d.Name == "Registration");
        model.Transfer = new TicketDetailsModel.TransferInput
        {
            TargetDepartmentId = registration.DepartmentId,
            Reason = "Misrouted",
            ReasonCode = TigerCS.Domain.Modules.Ticketing.TicketTransferReason.WrongDepartment,
            RowVersionBase64 = model.Ticket!.RowVersion
        };

        var result = await model.OnPostTransferAsync(1, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.RequestUri.EndsWith("/api/tickets/1/transfer", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(RegistrationId, body.RootElement.GetProperty("targetDepartmentId").GetInt32());
        Assert.Equal("Misrouted", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal((int)TigerCS.Domain.Modules.Ticketing.TicketTransferReason.WrongDepartment, body.RootElement.GetProperty("reasonCode").GetInt32());
    }

    [Fact]
    public async Task TicketDetails_TransferWithoutAReasonCode_IsRefusedBeforeCallingTheApi()
    {
        var (model, handler) = CreateTicketDetails(Api(), Roles.CsManager);
        await model.OnGetAsync(1, null, CancellationToken.None);

        var registration = Assert.Single(model.TransferTargets, d => d.Name == "Registration");
        model.Transfer = new TicketDetailsModel.TransferInput
        {
            TargetDepartmentId = registration.DepartmentId,
            Reason = "Misrouted",
            RowVersionBase64 = model.Ticket!.RowVersion
        };

        await model.OnPostTransferAsync(1, CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.RequestUri.EndsWith("/api/tickets/1/transfer", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------
    // No numeric department input, no raw department id, anywhere operational
    // ---------------------------------------------------------------

    [Fact]
    public void TicketDetailsView_TransferUsesANamePicker_BoundToTheDepartmentId()
    {
        var html = View("TicketDetails.cshtml");

        // A <select> bound to the existing model field, whose options carry
        // the id as the value and the NAME as the text.
        Assert.Contains("<select class=\"field-select\" asp-for=\"Transfer.TargetDepartmentId\">", html);
        Assert.Contains("@foreach (var d in Model.TransferTargets)", html);
        Assert.Contains("<option value=\"@d.DepartmentId\">@d.Name</option>", html);

        // Nothing that would let, or ask, a user to type a department id.
        Assert.DoesNotContain("<input class=\"form-control\" asp-for=\"Transfer.TargetDepartmentId\"", html);
        Assert.DoesNotContain("datalist", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Target department ID", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("numeric department id", html, StringComparison.OrdinalIgnoreCase);

        // And the whole control sits behind the transfer affordance.
        Assert.Contains("@if (Model.CanTransfer)", html);
        Assert.True(
            html.IndexOf("@if (Model.CanTransfer)", StringComparison.Ordinal)
            < html.IndexOf("asp-page-handler=\"Transfer\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TicketDetails.cshtml")]
    [InlineData("Tickets.cshtml")]
    [InlineData("Dashboard.cshtml")]
    [InlineData("CustomerProfile.cshtml")]
    [InlineData("Customers.cshtml")]
    [InlineData("NewTicket.cshtml")]
    [InlineData("Shared/_TicketRow.cshtml")]
    public void OperationalViews_NeverSurfaceARawDepartmentId_NorANumericDepartmentInput(string relativePath)
    {
        var html = View(relativePath.Split('/'));

        Assert.DoesNotContain("Department #", html, StringComparison.Ordinal);

        // No text/number input is ever bound to a department id — pickers only.
        foreach (var line in html.Split('\n'))
        {
            if (line.Contains("<input", StringComparison.OrdinalIgnoreCase)
                && line.Contains("DepartmentId", StringComparison.Ordinal))
            {
                Assert.Contains("type=\"hidden\"", line);
            }
        }
    }

    [Fact]
    public void DisplayHelpers_NeverRenderARawDepartmentId()
    {
        Assert.Equal("Facility Management", TicketDisplay.AssignedDepartmentLabel(1, "Facility Management"));
        Assert.Equal("Unknown department", TicketDisplay.AssignedDepartmentLabel(1, null));
        Assert.Equal("Facility Management Queue (Unassigned)", TicketDisplay.AssignedToLabel(null, null, 1, "Facility Management"));
        Assert.Equal("Department queue", TicketDisplay.AssignedToLabel(null, null, 1, null));
    }

    /// <summary>
    /// An Unclassified ticket has no priority, and the display must say so.
    /// The trap this guards is the <c>_ =&gt;</c> arm: before nullability,
    /// <c>PriorityCssKey</c> fell through to <c>medium</c>, so a null would
    /// have rendered as a quiet Medium badge — the UI asserting an urgency
    /// judgement nobody made.
    /// </summary>
    [Theory]
    [InlineData(null, "Not set", "none")]
    [InlineData((byte)1, "Critical", "critical")]
    [InlineData((byte)2, "High", "high")]
    [InlineData((byte)3, "Medium", "medium")]
    [InlineData((byte)4, "Low", "low")]
    public void PriorityDisplay_ShowsNotSetForAnUnclassifiedTicket_AndNeverFallsThroughToMedium(
        byte? priorityId, string expectedLabel, string expectedCssKey)
    {
        Assert.Equal(expectedLabel, TicketDisplay.PriorityLabel(priorityId));
        Assert.Equal(expectedCssKey, TicketDisplay.PriorityCssKey(priorityId));
    }
}
