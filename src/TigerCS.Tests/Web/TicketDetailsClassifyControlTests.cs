// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Classify control for an Unclassified ticket: offered to employees who can work the ticket, loaded with the ticket's own
/// department's categories and request types, absent for read-only roles, classified or closed tickets — and never a handoff.
/// </summary>
public sealed class TicketDetailsClassifyControlTests
{
    private const int DepartmentId = 4;
    private static readonly Guid Viewer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ClaimsPrincipal Principal(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Viewer.ToString()), new(ClaimTypes.Name, "Test Viewer") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private static TicketDetailDto Ticket(int? categoryId, string status = "Open", int? requestTypeId = null) => new(
        1, "TG-LCS-20261021-0002", DepartmentId, DepartmentId, null, null, null,
        categoryId, 3, status, "Unverified", "None", "Running", null, null, "Website chat started via Genesys", 0,
        DateTime.UtcNow, Convert.ToBase64String([1, 2, 3, 4]), RequestTypeId: requestTypeId);

    private static (TicketDetailsModel Model, List<string> Calls) Build(TicketDetailDto ticket, params string[] roles) =>
        Build(ticket, null, null, roles);

    private static (TicketDetailsModel Model, List<string> Calls) Build(
        TicketDetailDto ticket, CategoryDto[]? categories, RequestTypeOptionDto[]? requestTypes, params string[] roles)
    {
        var calls = new List<string>();
        var handler = new FakeApiHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path}{request.RequestUri.Query}");
            return path switch
            {
                "/api/users/me" => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CurrentUserResponseDto(
                    Viewer, "Test Viewer", roles, [new DepartmentMembershipDto(DepartmentId, "Leasing", IsPrimary: true)], IsGeynessStaff: false)),
                "/api/tickets/1" when request.Method == HttpMethod.Get => FakeApiHandler.JsonResponse(HttpStatusCode.OK, ticket),
                "/api/categories" => FakeApiHandler.JsonResponse(HttpStatusCode.OK, categories ??
                [
                    new CategoryDto(11, "Handover", DepartmentId, "Leasing"),
                    new CategoryDto(12, "Other department", 99, "Elsewhere")
                ]),
                "/api/request-types" => FakeApiHandler.JsonResponse(HttpStatusCode.OK, requestTypes ??
                [
                    new RequestTypeOptionDto(17, "Coordinate Handover", DepartmentId, 3, true),
                    new RequestTypeOptionDto(18, "Unpublished", DepartmentId, 3, false),
                    new RequestTypeOptionDto(19, "Other department", 99, 3, true)
                ]),
                "/api/tickets/1/classification" when request.Method == HttpMethod.Post => FakeApiHandler.JsonResponse(HttpStatusCode.OK, ticket),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            };
        });

        HttpClient Client() => new(handler) { BaseAddress = new Uri("http://localhost/") };
        var users = new UsersApiClient(Client(), NullLogger<UsersApiClient>.Instance);
        var model = new TicketDetailsModel(
            new TicketsApiClient(Client(), NullLogger<TicketsApiClient>.Instance),
            new TicketSlaApiClient(Client(), NullLogger<TicketSlaApiClient>.Instance),
            users,
            new TicketNameResolver(users, new DepartmentsApiClient(Client(), NullLogger<DepartmentsApiClient>.Instance)),
            new CategoriesApiClient(Client(), NullLogger<CategoriesApiClient>.Instance),
            new RequestTypesApiClient(Client(), NullLogger<RequestTypesApiClient>.Instance))
        {
            PageContext = new PageContext(new ActionContext(
                new DefaultHttpContext { User = Principal(roles) }, new RouteData(), new PageActionDescriptor()))
        };
        return (model, calls);
    }

    [Fact]
    public async Task AnUnclassifiedTicket_OffersClassify_WithOnlyItsOwnDepartmentsCategoriesAndPublishedRequestTypes()
    {
        var (model, calls) = Build(Ticket(categoryId: null), Roles.DepartmentEmployee);
        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanClassify);
        Assert.Equal([11], model.ClassifyCategories.Select(c => c.CategoryId));
        Assert.Equal([17], model.ClassifyRequestTypes.Select(r => r.RequestTypeId));
        Assert.Equal<byte>([1, 2, 3], model.ClassifyPriorities);              // current and more urgent only — lowering needs an approval
        Assert.Contains(calls, c => c.StartsWith("GET /api/categories?departmentId=4", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("handoff", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(Roles.ReportingUser)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task AReadOnlyRole_IsNotOfferedClassify(string role)
    {
        var (model, calls) = Build(Ticket(categoryId: null), role);
        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.False(model.CanClassify);
        Assert.DoesNotContain(calls, c => c.StartsWith("GET /api/categories", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClassifiedOrClosedTicket_IsNotOfferedClassify()
    {
        var (classified, _) = Build(Ticket(categoryId: 5), Roles.DepartmentEmployee);
        await classified.OnGetAsync(1, null, CancellationToken.None);
        var (closed, _) = Build(Ticket(categoryId: null, status: "Closed"), Roles.DepartmentEmployee);
        await closed.OnGetAsync(1, null, CancellationToken.None);

        Assert.False(classified.CanClassify);
        Assert.False(closed.CanClassify);
    }

    [Fact]
    public async Task ATicketWhoseRequestTypeTheAiAlreadySet_ClassifiesWithoutSendingAnotherRequestType()
    {
        var (model, _) = Build(Ticket(categoryId: null, requestTypeId: 17), Roles.DepartmentEmployee);
        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanClassify);
        Assert.True(model.Classify.RequestTypeAlreadySet);
    }

    [Fact]
    public async Task ATicketOnTheDefaultClassification_IsStillOfferedReclassify_ForAnyAuthorizedEmployee_AndTheDefaultIsNotPreselected()
    {
        var categories = new[] { new CategoryDto(10, "General", DepartmentId, "Leasing"), new CategoryDto(11, "Handover", DepartmentId, "Leasing") };
        var types = new[] { new RequestTypeOptionDto(16, "General", DepartmentId, 3, true), new RequestTypeOptionDto(17, "Handover", DepartmentId, 3, true) };
        var ticket = Ticket(categoryId: 10, requestTypeId: 16) with { IsDefaultClassification = true, RequestTypeName = "General" };
        var (model, _) = Build(ticket, categories, types, Roles.DepartmentEmployee);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanClassify);                                       // a placeholder is replaceable
        Assert.False(model.Classify.RequestTypeAlreadySet);                   // the request type is chosen again, not kept
        Assert.Equal(11, model.Classify.CategoryId);                          // starts on the first real category, not the default
        Assert.Equal(17, model.Classify.RequestTypeId);
    }

    [Fact]
    public async Task TheAssignedCsAgent_IsOfferedReclassify_OnAValidClassification_WithEveryDepartmentsChoices_AndTheCurrentRequestTypeIsNotLocked()
    {
        var categories = new[] { new CategoryDto(11, "Handover", DepartmentId, "Leasing"), new CategoryDto(12, "Billing", 99, "Accounting") };
        var types = new[] { new RequestTypeOptionDto(17, "Handover", DepartmentId, 3, true), new RequestTypeOptionDto(19, "Billing", 99, 3, true) };
        var ticket = Ticket(categoryId: 11, requestTypeId: 17) with { CanCorrectClassification = true };
        var (model, _) = Build(ticket, categories, types, Roles.CsAgent);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanClassify);
        Assert.False(model.Classify.RequestTypeAlreadySet);
        Assert.Equal(["Accounting \u2013 Billing", "Leasing \u2013 Handover"], model.ClassifyOptions.Categories.Select(c => c.Name));
        Assert.True(model.ClassifyOptions.Allows(12, 19));                    // a department correction offers that department's request types
    }

    [Fact]
    public async Task AnotherEmployee_IsNotOfferedReclassify_OnAValidClassification()
    {
        var (model, _) = Build(Ticket(categoryId: 11, requestTypeId: 17), Roles.CsAgent);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.False(model.CanClassify);
    }

    [Fact]
    public async Task AReadOnlyRole_IsNotOfferedReclassify_OnTheDefaultClassification()
    {
        var ticket = Ticket(categoryId: 10, requestTypeId: 16) with { IsDefaultClassification = true };
        var (model, _) = Build(ticket, Roles.ReportingUser);

        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.False(model.CanClassify);
    }

    [Fact]
    public void TicketDetailsView_ShowsCustomerServicesGeneral_ForTheFallback_AndRelabelsTheActionReclassify()
    {
        var view = File.ReadAllText(FindRepoFile("TigerCS.Web/Pages/TicketDetails.cshtml"));

        Assert.Contains("t.IsDefaultClassification", view);
        Assert.Contains("DefaultClassificationService.DisplayLabel", view);
        Assert.Contains("\"Reclassify\" : \"Classify\"", view);
        Assert.Equal("Customer Services / General", TigerCS.Application.Modules.Ticketing.Services.DefaultClassificationService.DisplayLabel);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException(relative), relative);
    }

    [Fact]
    public async Task ADepartmentWithNothingConfigured_HasClassifyOfferedButExplained_AndTheGuidanceDoesNotRecommendIt()
    {
        var (model, _) = Build(Ticket(categoryId: null), [], [], Roles.DepartmentEmployee);
        await model.OnGetAsync(1, null, CancellationToken.None);

        Assert.True(model.CanClassify);
        Assert.NotNull(model.ClassifyNotice);
        Assert.True(model.ClassifyNotice!.IsConfiguration);
        Assert.False(model.ClassifyOptions.IsAvailable);
        Assert.NotEqual("classify", model.Guidance!.Primary?.Key);
    }

    private static readonly CategoryDto[] TwoCategories =
        [new(11, "Handover", DepartmentId, "Leasing"), new(12, "General Inquiry", DepartmentId, "Leasing")];

    private static readonly RequestTypeOptionDto[] OneType = [new(17, "Handover", DepartmentId, 3, true)];

    private static async Task<(TicketDetailsModel Model, List<string> Calls)> Post(int categoryId, int requestTypeId, bool alreadySet = false)
    {
        var (model, calls) = Build(Ticket(categoryId: null), TwoCategories, OneType, Roles.DepartmentEmployee);
        model.Classify = new TicketDetailsModel.ClassifyInput
        {
            RowVersionBase64 = Convert.ToBase64String([1, 2, 3, 4]), CategoryId = categoryId, PriorityId = 3,
            RequestTypeId = requestTypeId, RequestTypeAlreadySet = alreadySet
        };
        await model.OnPostClassifyAsync(1, CancellationToken.None);
        return (model, calls);
    }

    [Fact]
    public async Task ARequestTypeUnderTheWrongCategory_IsRefused_AndNothingIsSent()
    {
        var (model, calls) = await Post(categoryId: 12, requestTypeId: 17);

        Assert.Contains("does not belong under the chosen category", model.ActionError);
        Assert.DoesNotContain(calls, c => c.StartsWith("POST", StringComparison.Ordinal) && c.Contains("classification"));
    }

    [Fact]
    public async Task ACategoryThatHasRequestTypes_RequiresOne()
    {
        var (model, calls) = await Post(categoryId: 11, requestTypeId: 0);

        Assert.Contains("Choose a request type", model.ActionError);
        Assert.DoesNotContain(calls, c => c.StartsWith("POST", StringComparison.Ordinal) && c.Contains("classification"));
    }

    [Fact]
    public async Task ACategoryWithoutRequestTypes_ClassifiesOnItsOwn_AndAMatchingPairIsAccepted()
    {
        var (_, own) = await Post(categoryId: 12, requestTypeId: 0);
        var (_, pair) = await Post(categoryId: 11, requestTypeId: 17);

        Assert.Contains(own, c => c == "POST /api/tickets/1/classification");
        Assert.Contains(pair, c => c == "POST /api/tickets/1/classification");
    }
}
