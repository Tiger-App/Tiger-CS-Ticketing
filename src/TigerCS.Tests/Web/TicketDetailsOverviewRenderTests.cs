// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Ticket Details Overview rendered end to end through the real Razor pipeline against a fake TigerCS.Api: Classify with options,
/// with none configured (Call Center) and for another department; and Conversation History in the Overview for a Genesys AI ticket, a
/// CRM customer ticket handled by a human, several interactions, duplicate-looking messages and a ticket with no interactions.
///
/// <para>Setting <c>TIGERCS_RENDER_DUMP_DIR</c> also writes each scenario's HTML there, so the finished pages can be opened in a real
/// browser for screenshots (see <c>docs/Ticket-Details-Classify-and-Conversation.md</c>).</para>
/// </summary>
public sealed class TicketDetailsOverviewRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private const int CustomerService = 2;
    private const int CallCenter = 4;
    private const int Registration = 5;

    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public TicketDetailsOverviewRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApi()));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private async Task<string> Page(long ticketId, string role = Roles.CsAgent, string? dumpAs = null)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Roles", role);
        var response = await client.GetAsync($"/Tickets/{ticketId}");
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{html[..Math.Min(html.Length, 1500)]}");

        if (Environment.GetEnvironmentVariable("TIGERCS_RENDER_DUMP_DIR") is { Length: > 0 } dir && dumpAs is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, dumpAs + ".html"), html);
        }

        return html;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        return html[from..(to < 0 ? html.Length : to)];
    }

    // ---------------------------------------------------------------
    // Take Ownership - the button follows the server's answer
    // ---------------------------------------------------------------

    [Fact]
    public async Task AWaitingTicketOfAnotherDepartment_OffersTakeOwnership_WhenTheServerSaysTheViewerMayTakeIt_AndNothingElse()
    {
        var html = await Page(120, dumpAs: "take-ownership-offered");

        Assert.Contains("id=\"take-ownership-notice\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"action-take-ownership\"", html, StringComparison.Ordinal);
        Assert.Contains("Take Ownership", html, StringComparison.Ordinal);
        Assert.Contains("queue with no assignee", html, StringComparison.Ordinal);
        Assert.DoesNotContain("read-only-notice", html, StringComparison.Ordinal);      // not the "you created this" message
        // Until it is theirs the ticket is read-only: no Close, Transfer, Resolve or classify controls.
        Assert.DoesNotContain("id=\"action-close\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-classify-form", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Transfer.TargetDepartmentId\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Resolve.ResolutionOutcome\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWaitingTicket_OffersNoTakeOwnership_WhenTheServerSaysTheViewerMayNotTakeIt()
    {
        var html = await Page(121, dumpAs: "take-ownership-not-offered");

        Assert.DoesNotContain("action-take-ownership", html, StringComparison.Ordinal);
        Assert.DoesNotContain("take-ownership-notice", html, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page-handler", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=TakeOwnership", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Classify
    // ---------------------------------------------------------------

    [Fact]
    public async Task CallCenterGenesysTicket_WithNoClassificationOptionsConfigured_ExplainsWhy_AndOffersNoClassifyAction()
    {
        var html = await Page(101, dumpAs: "classify-call-center-agent");

        // The explanation, in the attention card and the Classify control — by department name, never by id.
        Assert.Contains("Call Center has no classification options set up", html, StringComparison.Ordinal);
        Assert.Contains("classify-notice", html, StringComparison.Ordinal);
        Assert.Contains("Classify (not available)", html, StringComparison.Ordinal);
        Assert.Contains("Ask a System Administrator", html, StringComparison.Ordinal);

        // No form to submit, no recommendation to click, no empty dropdowns.
        Assert.DoesNotContain("data-classify-form", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Classify.CategoryId\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-guidance-target=\"classify\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("is-recommended", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Admin/RequestTypes\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallCenterGenesysTicket_GivesASystemAdministrator_TheExistingAdministrativePath()
    {
        var html = await Page(101, Roles.SystemAdministrator, dumpAs: "classify-call-center-admin");

        Assert.Contains("href=\"/Admin/RequestTypes\"", html, StringComparison.Ordinal);
        Assert.Contains("Open Request Types", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-classify-form", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-guidance-target=\"classify\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenesysAiTicket_WithItsRequestTypeAlreadySet_ClassifiesUnderTheMatchingCategory_WithoutAskingForTheTypeAgain()
    {
        var html = await Page(102, dumpAs: "classify-genesys-ai");

        Assert.Contains("data-classify-form", html, StringComparison.Ordinal);
        Assert.Contains("data-guidance-target=\"classify\"", html, StringComparison.Ordinal);
        // Customer Service categories, by name; the category the AI's request type belongs under is preselected.
        Assert.Contains("<option value=\"2\" selected=\"selected\">Complaint Handling</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"1\">General Inquiry</option>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-classify-request-type", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Send SPA Link", html, StringComparison.Ordinal);   // another department's category
    }

    [Fact]
    public async Task ManualTicket_RequestTypesFollowTheirCategory_AndACategoryWithoutOneClassifiesOnItsOwn()
    {
        var html = await Page(103, dumpAs: "classify-manual");

        var form = Between(html, "data-classify-form", "</form>");
        Assert.Contains("data-classify-category", form, StringComparison.Ordinal);
        Assert.Contains("data-classify-request-type", form, StringComparison.Ordinal);
        // Each request type names the category it belongs under; a category with none (General Inquiry) simply has no options.
        Assert.Contains("value=\"10\" data-category-ids=\"2\"", form, StringComparison.Ordinal);
        Assert.Contains("value=\"11\" data-category-ids=\"3\"", form, StringComparison.Ordinal);
        Assert.DoesNotContain("data-category-ids=\"1\">", form, StringComparison.Ordinal);
        Assert.Contains("No request type for this category", form, StringComparison.Ordinal);
        // A request type with no same-named category is reported, not attached to an arbitrary one.
        Assert.Contains("1 request type in this department cannot be chosen", form, StringComparison.Ordinal);
        Assert.DoesNotContain(">Coordinate Handover</option>", form, StringComparison.Ordinal);
        // No technical ids are asked for.
        Assert.DoesNotContain("type=\"number\"", form, StringComparison.Ordinal);
        // Priority downgrades still need an approved request: only the current priority and more urgent ones are offered.
        Assert.Contains(">Medium</option>", form, StringComparison.Ordinal);
        Assert.DoesNotContain(">Low</option>", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherDepartmentsTicket_IsOfferedThatDepartmentsOptionsOnly()
    {
        var html = await Page(105, dumpAs: "classify-registration");

        var form = Between(html, "data-classify-form", "</form>");
        Assert.Contains(">Send SPA Link</option>", form, StringComparison.Ordinal);
        Assert.DoesNotContain("Complaint Handling", form, StringComparison.Ordinal);
        Assert.DoesNotContain("General Inquiry", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClassifiedTicket_ShowsItsCategoryByName_NotByNumber()
    {
        var html = await Page(104);

        var requestDetails = Between(html, ">Request Details</div>", "</section>");
        Assert.Contains("<dt>Category</dt><dd>Complaint Handling</dd>", requestDetails, StringComparison.Ordinal);
        Assert.Contains("<dt>Request type</dt><dd>Complaint Handling</dd>", requestDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("#2", requestDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("data-classify-form", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Conversation History
    // ---------------------------------------------------------------

    [Fact]
    public async Task GenesysAiTicket_ShowsTheWholeAiConversation_InTheOverview_WithoutAnInteractionsTab()
    {
        var html = await Page(102, dumpAs: "overview-genesys-ai");

        Assert.DoesNotContain("tab-conversations", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Interactions", html, StringComparison.Ordinal);

        var conversation = Between(html, "id=\"conversation-title\"", "id=\"customer-satisfaction\"");
        Assert.Contains("Website chat", conversation, StringComparison.Ordinal);
        Assert.Contains("AI (virtual agent)", conversation, StringComparison.Ordinal);
        Assert.Contains("No handoff", conversation, StringComparison.Ordinal);
        Assert.Contains("22 Oct 2026 14:05 &#x2192; 14:12 (UAE)", conversation, StringComparison.Ordinal);   // UAE time, not UTC
        Assert.Contains("Virtual agent", conversation, StringComparison.Ordinal);
        Assert.Contains("chat-msg chat-msg--customer", conversation, StringComparison.Ordinal);
        Assert.Contains("chat-msg chat-msg--bot", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-msg--human", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("timeline", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("Human agent ·", conversation, StringComparison.Ordinal);   // an AI-only conversation never shows an agent
    }

    [Fact]
    public async Task CrmCustomerTicket_WithAHumanAgent_ShowsEveryInteraction_TheHandoff_AndTheAgentsLines()
    {
        var html = await Page(104, dumpAs: "overview-crm-human-multi");

        var conversation = Between(html, "id=\"conversation-title\"", "id=\"customer-satisfaction\"");
        Assert.Contains("Conversation History (2 interactions)", conversation, StringComparison.Ordinal);
        Assert.Equal(2, Count(conversation, "<details class=\"convo-group\""));
        // Only the most recent group is expanded; the earlier one is a collapsed row.
        Assert.Equal(1, Count(conversation, "<details class=\"convo-group\" open=\"open\""));

        Assert.Contains("WhatsApp", conversation, StringComparison.Ordinal);
        Assert.Contains("Website chat", conversation, StringComparison.Ordinal);
        Assert.Contains("Originating", conversation, StringComparison.Ordinal);
        Assert.Contains("Human agent &#xB7; Layla Hassan", conversation, StringComparison.Ordinal);
        Assert.Contains("Handoff: Completed", conversation, StringComparison.Ordinal);
        Assert.Contains("Customer asked for a human", conversation, StringComparison.Ordinal);
        Assert.Contains("Wants the snag list before handover.", conversation, StringComparison.Ordinal);   // handoff reason
        Assert.Contains("chat-msg chat-msg--human", conversation, StringComparison.Ordinal);
        Assert.Contains("<li class=\"chat-event\">", conversation, StringComparison.Ordinal);               // a system event, apart from the bubbles
        Assert.Contains("Customer disconnected", conversation, StringComparison.Ordinal);                 // the interaction's own end reason
        Assert.Contains("&#x2B;971501234567", conversation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateLookingMessages_AreAllShown_AndLongTextIsKeptWhole_InChronologicalOrder()
    {
        var html = await Page(104);

        var conversation = Between(html, "id=\"conversation-title\"", "id=\"customer-satisfaction\"");
        // "Hello" is four real messages — one in the earlier chat, then two from the customer in the same minute and one from the bot in
        // the later one — and none is merged away.
        Assert.Equal(4, Count(conversation, "<div class=\"chat-bubble__text\">Hello</div>"));
        var longMessage = HtmlEncoder.Default.Encode(FakeApi.LongMessage);   // the same encoder Razor writes with
        Assert.Contains(longMessage, conversation, StringComparison.Ordinal);
        Assert.True(
            conversation.IndexOf("Thanks, how can I help?", StringComparison.Ordinal) < conversation.IndexOf(longMessage, StringComparison.Ordinal),
            "Messages must stay in the order the interactions API returned them.");
    }

    [Fact]
    public async Task ATicketWithNoInteractions_SaysSo_AndAFailedLoadIsNotMistakenForNoConversation()
    {
        var none = await Page(103, dumpAs: "overview-no-interactions");
        var failed = await Page(106);

        Assert.Contains("No calls or chats are recorded against this ticket.", none, StringComparison.Ordinal);
        Assert.Contains("Conversation history is unavailable right now.", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("No calls or chats are recorded against this ticket.", failed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOverview_IsStructuredAsTheWorkspace_WithCustomerSatisfactionVisible()
    {
        var html = await Page(104);

        int At(string marker) => html.IndexOf(marker, StringComparison.Ordinal);
        var order = new[]
        {
            At("class=\"ticket-header\""), At("What needs your attention?"), At("Customer Information"), At(">Request Details</div>"),
            At("id=\"conversation-title\""), At("id=\"customer-satisfaction\""), At(">Latest Activity</div>"), At("aria-label=\"Quick facts\""),
        };
        Assert.All(order, p => Assert.True(p >= 0));
        Assert.Equal(order.OrderBy(p => p), order);

        // Customer Satisfaction is open on the page (not inside the collapsed reference details), with rating, feedback, date and test marker.
        var satisfaction = Between(html, "id=\"customer-satisfaction\"", "</section>");
        Assert.Contains("4/5", satisfaction, StringComparison.Ordinal);
        Assert.Contains("Handover went smoothly.", satisfaction, StringComparison.Ordinal);
        Assert.Contains("22 Oct 2026 18:05 (UAE)", satisfaction, StringComparison.Ordinal);
        Assert.Contains("Test review", satisfaction, StringComparison.Ordinal);
        Assert.True(At("id=\"customer-satisfaction\"") < At("<details class=\"more-details\">"));

        // Information is said once: the quick-facts sidebar carries status/priority/owner/SLA, so the main column does not.
        Assert.Equal(1, Count(html, "<aside class=\"facts-panel quick-facts\""));
        Assert.DoesNotContain("<div class=\"facts-section__title\">Ticket Information</div>", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Roles"].FirstOrDefault() ?? Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Agent"),
                new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public const string LongMessage =
            "I bought unit T-1204 in Tiger Tower and the handover inspection is booked for next week, but I have not received the snag list, "
            + "the payment statement or the key-collection letter, and nobody has been able to tell me which documents I must bring with me, "
            + "so please confirm everything in one message — averyveryveryverylongwordwithoutanyspacesatalltotesthowthebubblewrapsinsideitsbox0123456789.";

        private static readonly DateTime Chat = new(2026, 10, 22, 10, 5, 0, DateTimeKind.Utc);   // 14:05 UAE

        private static readonly CategoryDto[] Categories =
        [
            new(1, "General Inquiry", CustomerService, "Customer Service"),
            new(2, "Complaint Handling", CustomerService, "Customer Service"),
            new(3, "NOC for Resale", CustomerService, "Customer Service"),
            new(20, "Send SPA Link", Registration, "Registration"),
        ];

        private static readonly RequestTypeOptionDto[] RequestTypes =
        [
            new(10, "Complaint Handling", CustomerService, 3, true),
            new(11, "NOC for Resale", CustomerService, 3, true),
            new(12, "Coordinate Handover", CustomerService, 3, true),     // no same-named routing category
            new(13, "Unpublished", CustomerService, 3, false),
            new(30, "Send SPA Link", Registration, 3, true),
        ];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            int? departmentId = int.TryParse(query["departmentId"], out var d) ? d : null;

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Agent", [Roles.CsAgent],
                    [new DepartmentMembershipDto(CustomerService, "Customer Service", true), new DepartmentMembershipDto(CallCenter, "Call Center", false)], true),
                "/api/departments" => new[]
                {
                    new DepartmentDto(CustomerService, "Customer Service"), new DepartmentDto(CallCenter, "Call Center"), new DepartmentDto(Registration, "Registration")
                },
                "/api/channels" => new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2), new ChannelDto(1, "Website chat", "WEB", true, true, true, 2) },
                "/api/categories" => Categories.Where(c => departmentId is null || c.DepartmentId == departmentId).ToArray(),
                "/api/request-types" => RequestTypes.Where(r => departmentId is null || r.DepartmentId == departmentId).ToArray(),
                _ when path.StartsWith("/api/departments/", StringComparison.Ordinal) && path.EndsWith("/users", StringComparison.Ordinal) =>
                    new PagedResultDto<DepartmentUserDto>([new DepartmentUserDto(ViewerId, "Test Agent", true, [Roles.CsAgent])], 1, 100, 1),
                _ => Ticket(path),
            };

            if (path == "/api/tickets/106/interactions")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private static object? Ticket(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "api" || parts[1] != "tickets" || !long.TryParse(parts[2], out var id))
            {
                return null;
            }

            var detail = Detail(id);
            if (detail is null)
            {
                return null;
            }

            return parts.Length == 3
                ? detail
                : parts[3] switch
                {
                    "interactions" => new TicketInteractionHistoryDto(id, Interactions(id)),
                    "approvals" => new TicketApprovalsViewDto([], [], [], null, null, false),
                    "reviews" => new TicketReviewsDto(id, id == 104
                        ? [new TicketReviewCycleDto(0, "Submitted", true, 4, "Handover went smoothly.", Chat.AddHours(4), Chat.AddHours(3))]
                        : []),
                    _ => null,
                };
        }

        private static TicketDetailDto? Detail(long id)
        {
            var created = Chat.AddMinutes(-1);
            var row = Convert.ToBase64String(new byte[8]);
            return id switch
            {
                // Genesys AI ticket created in the Call Center: no categories or request types exist for that department.
                101 => new(101, "TG-CC-20261022-0001", CallCenter, CallCenter, null, null, null, null, 3, "InProgress", "Unverified", "None", "Running", null, null,
                    "Website chat started via Genesys", 0, created, row, HandoffState: "NotRequired", OriginatingChannelId: 1, OriginatingChannelName: "Website chat", IsClassified: false),
                // Genesys AI ticket in Customer Service whose request type the AI already set.
                102 => new(102, "TG-CS-20261022-0002", CustomerService, CustomerService, null, null, null, null, 3, "Open", "Unverified", "None", "Running", null, null,
                    "Website chat started via Genesys", 0, created, row, RequestTypeId: 10, RequestTypeName: "Complaint Handling", OriginatingChannelId: 1, OriginatingChannelName: "Website chat", IsClassified: false),
                // A manually created, unclassified ticket.
                103 => new(103, "TG-CS-20261022-0003", CustomerService, CustomerService, null, null, null, null, 3, "Open", "Unverified", "None", "Running", null, null,
                    "Customer called about a parking card", 0, created, row, IsClassified: false),
                // A CRM-verified customer's classified ticket with two interactions.
                104 => new(104, "TG-CS-20261022-0004", CustomerService, CustomerService, ViewerId, null, null, 2, 2, "InProgress", "Verified", "None", "Running", null, null,
                    "Handover inspection documents", 0, created, row, CrmBuyerCustomerId: 9001, CrmBuyerLeadId: 90010, CrmBuyerUnitId: 41, CrmBuyerProjectId: 7,
                    CrmBuyerCustomerName: "Mariam Al Falasi", CrmBuyerProjectName: "Tiger Tower", CrmBuyerUnitNumber: "T-1204",
                    RequestTypeId: 10, RequestTypeName: "Complaint Handling", WorkflowName: "Standard Request", WorkflowVersionNumber: 1,
                    OriginatingChannelId: 3, OriginatingChannelName: "WhatsApp"),
                // A Call Center agent opening another department's waiting ticket: read-only, and the server says they may take it.
                120 => new(120, "TG-REG-20261022-0120", Registration, Registration, null, null, null, null, 3, "Open", "Unverified", "None", "Running", null, null,
                    "Old registration request", 0, created, row, IsClassified: false, IsReadOnlyAccess: true, CanTakeOwnership: true),
                // The same shape, but the server says they may NOT take it (e.g. an explicit Deny): nothing is offered.
                121 => new(121, "TG-REG-20261022-0121", Registration, Registration, null, null, null, null, 3, "Open", "Unverified", "None", "Running", null, null,
                    "Old registration request", 0, created, row, IsClassified: false, IsReadOnlyAccess: true, CanTakeOwnership: false),
                // Another department's unclassified ticket.
                105 => new(105, "TG-REG-20261022-0005", Registration, Registration, null, null, null, null, 3, "Open", "Unverified", "None", "Running", null, null,
                    "Needs the SPA link", 0, created, row, IsClassified: false),
                // Interactions fail to load.
                106 => new(106, "TG-CS-20261022-0006", CustomerService, CustomerService, ViewerId, null, null, 2, 3, "Open", "Unverified", "None", "Running", null, null,
                    "Interactions unavailable", 0, created, row),
                _ => null,
            };
        }

        private static IReadOnlyList<TicketInteractionDto> Interactions(long id) => id switch
        {
            102 =>
            [
                new(1, true, "Genesys", 1, "Website chat", "+971501112222", "Omar", null, "Inbound", "conv-102", null, null, null, null,
                    Chat, Chat.AddMinutes(7), "Customer disconnected", "Ended",
                    [
                        new(1, "Customer", null, Chat, "Hi, I want to complain about the noise from the construction next door."),
                        new(2, "VirtualAgent", null, Chat.AddMinutes(1), "I'm sorry to hear that. Is this about Tiger Tower or another project?"),
                        new(3, "Customer", null, Chat.AddMinutes(2), "Tiger Tower."),
                        new(4, "VirtualAgent", null, Chat.AddMinutes(3), "Thank you. I've logged a complaint request for you."),
                    ], null, "Ai"),
            ],
            104 =>
            [
                new(1, true, "Genesys", 3, "WhatsApp", "+971501234567", "Mariam Al Falasi", null, "Inbound", "conv-104a", null, null, null, null,
                    Chat.AddDays(-1), Chat.AddDays(-1).AddMinutes(3), "Customer disconnected", "Ended",
                    [
                        new(1, "Customer", null, Chat.AddDays(-1), "Hello"),
                        new(2, "VirtualAgent", null, Chat.AddDays(-1).AddMinutes(1), "Hello! Thanks, how can I help?"),
                        new(3, "Customer", null, Chat.AddDays(-1).AddMinutes(2), LongMessage),
                    ], null, "WaitingForHuman"),
                new(2, false, "Genesys", 1, "Website chat", "+971501234567", "Mariam Al Falasi", null, "Inbound", "conv-104b", null, null, "Layla Hassan", "agent-1",
                    Chat, Chat.AddMinutes(12), "Customer disconnected", "Ended",
                    [
                        new(1, "Customer", null, Chat, "Hello"),
                        new(2, "Customer", null, Chat, "Hello"),
                        new(3, "VirtualAgent", null, Chat.AddMinutes(1), "Hello"),
                        new(4, "System", null, Chat.AddMinutes(2), "Conversation transferred to a human agent"),
                        new(5, "HumanAgent", "Layla Hassan", Chat.AddMinutes(4), "Hi Mariam, this is Layla from Customer Service. I have your handover file open."),
                        new(6, "Customer", null, Chat.AddMinutes(5), "Thanks, please send the snag list."),
                    ],
                    new AgentHandoffSummaryDto(5, "Completed", "ContinueChat", "Wants the snag list before handover.", Chat.AddMinutes(2), ViewerId, "agent-1",
                        Chat.AddMinutes(11), "Snag list sent by e-mail.", "CustomerRequestedHuman"),
                    "Human"),
            ],
            _ => [],
        };
    }
}
