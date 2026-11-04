// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details shows who first handled the customer, who owns the ticket now, who transferred it to whom and why, and who
/// resolved it - with names from the Api (the employee directory), including a person who is in no department any more.
/// </summary>
public sealed class TicketHandlingDisplayTests
{
    private static readonly Guid FirstHandler = Guid.NewGuid();
    private static readonly Guid Supervisor = Guid.NewGuid();
    private static readonly Guid Receiver = Guid.NewGuid();
    private static readonly Guid Resolver = Guid.NewGuid();
    private static readonly Guid GenesysNext = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "") =>
        Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..")), relativeToSrc);

    private static TicketDetailDto Ticket() => new(
        1, "TG-CS-20261030-0001", 1, 1, Receiver, null, null, 5, 3, "Resolved", "Verified", "None", "Running", 1, null,
        "AC not cooling", 0, T0, Convert.ToBase64String([1, 2, 3, 4]),
        ResolvedAtUtc: T0.AddHours(5),
        FirstHandledByEmployeeId: FirstHandler, FirstHandledByName: "Layla Left",
        ResolvedByEmployeeId: Resolver, ResolvedByName: "Rami Resolver",
        TransferCount: 1, EscalationCount: 1);

    private static TicketApprovalsViewDto Approvals() => new(
        [], [],
        [
            new TicketWorkflowEventDto("GenesysAgentConnected", T0, null, "Genesys conversation c-1: agent ga-1 answered.",
                ToEmployeeId: FirstHandler, ToEmployeeName: "Layla Left"),
            new TicketWorkflowEventDto("GenesysCallTransferred", T0.AddMinutes(4), FirstHandler, "Genesys conversation c-1: call transferred.",
                ToEmployeeId: GenesysNext, ToEmployeeName: "Omar Next", ActorName: "Layla Left"),
            new TicketWorkflowEventDto("TicketTransferred", T0.AddHours(1), Supervisor, "Could not fix on the phone",
                TransferReason: "UnableToResolve", FromDepartmentId: 1, ToDepartmentId: 1, ToEmployeeId: Receiver,
                ActorName: "Hana Head", ToEmployeeName: "Rana Receiver")
        ],
        null, null, CallerCanRecordEvents: false);

    private static TicketDetailsModel CreateModel()
    {
        var handler = new FakeApiHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tickets/1" && request.Method == HttpMethod.Get)
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, Ticket());
            }

            if (path == "/api/tickets/1/approvals")
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, Approvals());
            }

            if (path == "/api/departments")
            {
                return FakeApiHandler.JsonResponse(HttpStatusCode.OK, new[] { new DepartmentDto(1, "Customer Service") });
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };
        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var model = new TicketDetailsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            users,
            new TicketNameResolver(users, new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "Viewer"), new(ClaimTypes.Role, Roles.CsManager)
        };
        model.PageContext = new PageContext(new ActionContext(
            new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }, new RouteData(), new PageActionDescriptor()));
        return model;
    }

    [Fact]
    public async Task FirstHandledBy_AndResolvedBy_AreNamed_EvenForSomeoneInNoDepartment()
    {
        var model = CreateModel();

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.Equal("Layla Left", model.FirstHandledByName);
        Assert.Equal("Rami Resolver", model.ResolvedByName);
        Assert.Equal(Receiver, model.Ticket!.CurrentOwnerEmployeeId);          // CurrentOwner is a different person from FirstHandledBy
        Assert.NotEqual(model.Ticket.FirstHandledByEmployeeId, model.Ticket.CurrentOwnerEmployeeId);
        Assert.Equal(1, model.Ticket.TransferCount);
        Assert.Equal(1, model.Ticket.EscalationCount);
    }

    [Fact]
    public async Task TheActivityFeed_ShowsTransferredBy_TransferredTo_AndTheReason_ForAnEscalation()
    {
        var model = CreateModel();

        await model.OnGetAsync(1, null, CancellationToken.None);

        var escalation = Assert.Single(model.ActivityFeed, e => e.Kind == "escalation");
        Assert.Equal("Hana Head", escalation.Actor);                                            // Transferred By
        Assert.Contains("escalated the ticket to Rana Receiver", escalation.Description);        // Transferred To
        Assert.Contains("Unable to resolve", escalation.Description);                           // Reason
        Assert.Equal("Could not fix on the phone", escalation.Note);
        Assert.Equal(T0.AddHours(1), escalation.TimestampUtc);                                  // Transfer date/time
    }

    [Fact]
    public async Task AGenesysCallTransfer_IsShownAsHandlingHistory_NotAsAnEscalation()
    {
        var model = CreateModel();

        await model.OnGetAsync(1, null, CancellationToken.None);

        var genesys = model.ActivityFeed.Where(e => e.Kind == "genesys").OrderBy(e => e.TimestampUtc).ToList();
        Assert.Equal(2, genesys.Count);
        Assert.Equal("Layla Left", genesys[0].Actor);
        Assert.Contains("first agent to handle", genesys[0].Description);
        Assert.Equal("Layla Left", genesys[1].Actor);
        Assert.Contains("transferred the customer's call to Omar Next", genesys[1].Description);
        // Only the explicit TigerCS escalation is an escalation entry.
        Assert.Single(model.ActivityFeed, e => e.Kind == "escalation");
        Assert.DoesNotContain(genesys, e => e.Description.Contains("escalat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDetailsView_ShowsTheHandlingAndEscalationPanel_WithEveryFact()
    {
        var view = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

        foreach (var label in new[] { "First handled by", "Current owner", "Times transferred", "Escalated", "Resolved by", "Resolved on" })
        {
            Assert.Contains(label, view);
        }

        Assert.Contains("Model.FirstHandledByName", view);
        Assert.Contains("Model.ResolvedByName", view);
        Assert.Contains("t.TransferCount", view);
        Assert.Contains("t.EscalationCount", view);
    }
}
