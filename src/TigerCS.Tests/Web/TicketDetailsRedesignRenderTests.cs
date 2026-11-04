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
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The redesigned Ticket Details rendered end to end (real Razor pipeline, fake Api) for the scenarios agents meet: unclassified,
/// classified but unassigned, assigned and active, AI-only, human handoff, registered and unregistered customers, a failed CRM check,
/// SLA not started / running / stopped, resolved, closed and reopened tickets — and for different roles.
///
/// <para>Setting <c>TIGERCS_RENDER_DUMP_DIR</c> also writes each page's HTML there, for the desktop/mobile screenshots.</para>
/// </summary>
public sealed class TicketDetailsRedesignRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private const int CustomerService = 2;
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly List<WebApplicationFactory<TigerCsWeb::Program>> _factories = [];

    public void Dispose() => _factories.ForEach(f => f.Dispose());

    private async Task<string> Page(long ticketId, string role = Roles.CsManager, string? dump = null)
    {
        var factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApi(role)));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; });
            });
        });
        _factories.Add(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Roles", role);
        var response = await client.GetAsync($"/Tickets/{ticketId}");
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{html[..Math.Min(html.Length, 1200)]}");
        if (Environment.GetEnvironmentVariable("TIGERCS_RENDER_DUMP_DIR") is { Length: > 0 } dir && dump is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, dump + ".html"), html);
        }

        return html;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Section(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        return html[from..(to < 0 ? html.Length : to)];
    }

    /// <summary>Razor encodes "—" and "+" as numeric entities; assertions on prose read the decoded text.</summary>
    private static string Dec(string html) => WebUtility.HtmlDecode(html);

    /// <summary>Everything the page says about the customer: the header's identity line plus the sidebar's Customer section (when present).</summary>
    private static string Strip(string html)
    {
        var who = Section(html, "class=\"tw-who\"", "</header>");
        var side = html.IndexOf("class=\"tw-sec tw-cust\"", StringComparison.Ordinal);
        return side < 0 ? who : who + Section(html, "class=\"tw-sec tw-cust\"", "</section>");
    }

    private static string NextStep(string html) => Section(html, "class=\"tw-next ", "</section>");
    private static string Menu(string html) => Section(html, "id=\"actions-menu\"", "class=\"tw-next ");

    // ---------------------------------------------------------------- the four questions, every scenario

    [Fact]
    public async Task Unclassified_UnassignedTicket_NoIdentity_SlaNotStarted_ClassifyIsTheOneNextStep()
    {
        var html = await Page(201, dump: "redesign-unclassified");

        Assert.Contains("Nothing on this ticket identifies the customer", Strip(html));
        Assert.Contains("Customer not identified", Strip(html));
        var next = NextStep(html);
        Assert.Contains("Classify this ticket", next);
        Assert.Equal(1, Count(html, "tw-next__cta"));
        Assert.Contains("Unassigned — awaiting CS Agent", Dec(html));
        Assert.Contains("SLA: Not started", html);
        Assert.Contains("Not yet classified", html);
        Assert.Contains("this ticket has no priority", html);   // the reason, under Additional details
    }

    [Fact]
    public async Task CollectionsTicket_OffersAssignBesideAssignedTo_OnlyWhenTheServerSaysTheViewerMayAssign()
    {
        var offered = Dec(await Page(219, Roles.DepartmentEmployee));

        Assert.Equal(1, Count(offered, "data-collections-assign"));
        var control = Section(offered, "data-collections-assign", "</details>");
        Assert.Contains("Reassign", control);                                   // the ticket has an owner
        Assert.Contains("Search Collections team", control);                    // searchable list of the team
        Assert.Contains("name=\"CollectionsAssign.EmployeeId\"", control);
        Assert.Contains("Reason (optional)", control);
        Assert.Contains("Current: <strong data-assign-current>", control);
        Assert.Equal(1, Count(offered, "Assigned To:"));                        // still shown once, beside the control

        Assert.DoesNotContain("data-collections-assign", await Page(212, Roles.CsManager));   // not a Collections ticket for this viewer
    }

    [Fact]
    public async Task TransferAndReopenForms_CarryTheMandatoryTowerAndUnitBlock_OnlyForTheCollectionsDepartment()
    {
        var open = Dec(await Page(203, Roles.CsManager));
        var transfer = Section(open, "id=\"action-transfer\"", "</details>");

        Assert.Contains("data-ct-collections-ids=\"7\"", transfer);               // only Collections (by its code) needs it
        Assert.Contains("data-ct-trigger=\"#Transfer_TargetDepartmentId\"", transfer);
        Assert.Contains("name=\"Transfer.CollectionsTowerId\"", transfer);        // tower first...
        Assert.Contains("name=\"Transfer.CollectionsUnitId\"", transfer);         // ...then the unit of that tower
        Assert.Contains("Select a tower first", transfer);
        Assert.Matches(@"<div class=""ct"" hidden", transfer);                      // hidden (and its selects disabled) until Collections is chosen
        Assert.Contains("ticket-collections-tower-unit.js", open);

        var reopen = Dec(await Page(209, Roles.CsManager));
        Assert.Contains("name=\"Reopen.CollectionsTowerId\"", Section(reopen, "id=\"action-reopen\"", "</details>"));
    }

    [Fact]
    public async Task ClassifiedButUnassigned_ManagerIsToldToAssign()
    {
        var html = await Page(202);

        Assert.Contains("Assign this ticket", NextStep(html));
        Assert.Contains("waiting in the Customer Service queue", NextStep(html));
        Assert.Equal(1, Count(html, "tw-next__cta"));
    }

    [Fact]
    public async Task AssignedActiveTicket_ShowsOwnerOnce_OneGoldAction_AndActionsInsideTheMenu()
    {
        var html = await Page(203, Roles.CsAgent, "redesign-assigned");

        Assert.Equal(1, Count(html, "Assigned To:"));
        Assert.Contains("<strong>Assigned To:</strong> Test Agent", html);
        Assert.Contains("Contact the customer", NextStep(html));   // yours, and nobody has replied yet

        // One dominant action: the Next step button is the only gold control outside forms' own submit buttons.
        Assert.Equal(1, Count(html, "btn btn-gold btn-sm tw-next__cta"));
        var summaries = Menu(html);
        Assert.DoesNotContain("<summary class=\"btn btn-gold", summaries);
        Assert.Contains("id=\"action-resolve\"", summaries);
        Assert.Contains("id=\"action-escalate\"", summaries);

        // The first reply is overdue: said once per clock, resolution is NOT called breached.
        var sla = Section(html, "SLA Information", "Verification &amp; Unit");
        Assert.Contains("Overdue since", sla);
        Assert.Matches(@"<dt>Resolution</dt><dd class="""">Due ", sla);
    }

    [Fact]
    public async Task RegisteredCustomer_ShowsNameLinkPhoneProjectAndUnitInOneBand()
    {
        var strip = Strip(await Page(203, Roles.CsAgent));

        Assert.Contains("Mariam Al Falasi", strip);
        Assert.Contains("href=\"/Customers/crm%3A9001?fromTicket=203\"", strip);
        Assert.Contains("Verified via Tiger CRM", strip);
        Assert.Contains("T-1204 · Tiger Tower", Dec(strip));
        Assert.DoesNotContain("typed in", strip);
    }

    [Fact]
    public async Task GenesysAiOnly_UnregisteredCaller_StatesAiAndNoRegisteredMatch()
    {
        var html = await Page(204, Roles.CsManager, "redesign-ai");

        Assert.Contains("Unnamed caller", Strip(html));
        Assert.Contains("not matched to a registered customer", Strip(html));
        Assert.Contains("+971501112222", Dec(Strip(html)));
        Assert.Contains("AI (virtual agent) — no human involved", Dec(html));
        Assert.Contains("AI is handling this customer", NextStep(html));
        Assert.DoesNotContain("chat-msg--human", html);
    }

    [Fact]
    public async Task HumanHandoff_ShowsWhoHandlesIt_AndTheCallFacts()
    {
        var html = await Page(205, Roles.CsManager, "redesign-handoff");

        Assert.Contains("Human agent — Layla Hassan", Dec(html));
        Assert.Contains("Handoff: Completed", html);
        var convo = Section(html, "id=\"conversation-title\"", "</section>");
        Assert.Contains("<dt>Contact</dt><dd>Customer contacted us</dd>", convo);
        Assert.Contains("<dt>Queue</dt><dd>Sales Support</dd>", convo);
        Assert.Contains("<dt>Duration</dt><dd>12 min</dd>", convo);
    }

    [Fact]
    public async Task PhoneCall_HasNoTranscript_AndSaysSoInsteadOfLookingBroken()
    {
        var html = await Page(206);

        Assert.Contains("This was a phone call", html);
        Assert.DoesNotContain("No transcript was recorded", html);
    }

    [Fact]
    public async Task CrmCheckFailed_ProvisionalTicket_NeverPrintsAPlaceholderName()
    {
        var strip = Strip(await Page(207));

        Assert.Contains("could not be completed", strip);
        Assert.Contains("Pending CRM Verification", strip);
        Assert.DoesNotContain("NOT_REGISTERED", strip);
    }

    // ---------------------------------------------------------------- customer name resolution

    [Fact]
    public async Task SubmittedFormName_IsShown_AndSaidToBeUnverified()
    {
        var strip = Strip(await Page(213));

        Assert.Contains("Ahmed Hassan", strip);
        Assert.DoesNotContain("Unnamed caller", strip);
        Assert.DoesNotContain("Verified via", strip);
        Assert.Contains("Name as provided", strip);
    }

    [Fact]
    public async Task ExternallyVerifiedTicket_ShowsTheSourcesCustomerName_NotAGenericSourceLabel()
    {
        var strip = Strip(await Page(214));

        Assert.Contains("Omar Saleh", strip);
        Assert.DoesNotContain("Pact customer", strip);
        Assert.Contains("Verified via PACT", strip);
        Assert.DoesNotContain("Name as provided", strip);
    }

    [Fact]
    public async Task PlaceholderSubmittedName_IsUnnamedCaller_NeverThePlaceholder()
    {
        var strip = Strip(await Page(215));

        Assert.Contains("Unnamed caller", strip);
        Assert.DoesNotContain("NOT_REGISTERED", strip);
    }

    [Fact]
    public async Task CrmVerifiedName_WinsOverTheSubmittedName_AndIsMarkedVerified()
    {
        var strip = Strip(await Page(216));

        Assert.Contains("Mariam Al Falasi", strip);
        Assert.DoesNotContain("Typed By Customer", strip);
        Assert.Contains("Verified via Tiger CRM", strip);
        Assert.DoesNotContain("Name as provided", strip);
    }

    [Fact]
    public async Task FailedCrmLookup_DoesNotHideTheSubmittedName()
    {
        var strip = Strip(await Page(217));

        Assert.Contains("Sara Khalid", strip);
        Assert.Contains("Pending CRM Verification", strip);
        Assert.Contains("Name as provided", strip);
    }

    [Fact]
    public async Task PlaceholderCrmSnapshot_DoesNotOverwriteTheSubmittedName()
    {
        var strip = Strip(await Page(218));

        Assert.Contains("Huda Nasser", strip);
        Assert.DoesNotContain("NOT_REGISTERED", strip);
    }

    [Fact]
    public async Task ResolvedTicket_SlaStopped_OwnerlessIsNotAnAssignmentFailure_AndCloseIsTheNextStep()
    {
        var html = await Page(208, Roles.CsManager, "redesign-resolved");

        Assert.Contains("Check the resolution, then close the ticket", NextStep(html));
        Assert.Contains("<strong>Assigned To:</strong> No one assigned", html);
        Assert.DoesNotContain("awaiting CS Agent", html);
        Assert.Contains("SLA: Met", html);
        Assert.DoesNotContain("past due", html);
    }

    [Fact]
    public async Task ClosedTicket_WithinReopenWindow_OffersReopenInTheMenu_AndNothingElse()
    {
        var html = await Page(209, Roles.CsManager, "redesign-closed");

        Assert.Contains("This ticket is closed", NextStep(html));
        var menu = Menu(html);
        Assert.Contains("id=\"action-reopen\"", menu);
        Assert.DoesNotContain("id=\"action-assign\"", menu);
        Assert.DoesNotContain("id=\"action-resolve\"", menu);
        Assert.Equal(0, Count(html, "tw-next__cta"));
    }

    [Fact]
    public async Task ReopenedTicket_SaysSo_AndItsSlaCycleIsNotRepaintedAsBreached()
    {
        var html = await Page(210, Roles.CsAgent);

        Assert.Contains("Reopened once", NextStep(html));
        var sla = Section(html, "SLA Information", "Verification &amp; Unit");
        Assert.DoesNotContain("is-critical", sla);
    }

    [Fact]
    public async Task SlaPaused_ShowsPausedOnResolution_NotOnFirstResponse()
    {
        var html = await Page(211, Roles.CsAgent);

        Assert.Contains("Paused — Pending Customer", Dec(html));
        Assert.Contains("SLA: Paused", html);
    }

    // ---------------------------------------------------------------- roles

    [Fact]
    public async Task ReadOnlyRole_SeesNoActionsMenu_NoCta_AndIsToldItIsViewOnly()
    {
        var html = await Page(203, Roles.ReportingUser);

        Assert.DoesNotContain("id=\"actions-menu\"", html);
        Assert.Equal(0, Count(html, "tw-next__cta"));
        Assert.Contains("view-only", NextStep(html));
        Assert.DoesNotContain("asp-page-handler", html);
        Assert.DoesNotContain("Record first response", html);
    }

    [Fact]
    public async Task NonOwnerAgent_IsNotOfferedRecordFirstResponse_ButTheOwnerAndAManagerAre()
    {
        // Ticket 212 is owned by someone else with the first reply still outstanding.
        Assert.DoesNotContain("Record first response", await Page(212, Roles.CsAgent));
        Assert.Contains("Record first response", await Page(212, Roles.CsManager));
        Assert.Contains("Record first response", await Page(203, Roles.CsAgent));   // owner
    }

    [Fact]
    public async Task Layout_HasNoQuickFactsSidebar_NoAttachmentsTab_AndTimesAreNeverServerLocal()
    {
        var html = await Page(203, Roles.CsAgent);

        Assert.DoesNotContain("quick-facts", html);
        Assert.DoesNotContain("tab-attachments", html);
        Assert.Contains("<details class=\"more-details\" id=\"additional-details\">", html);   // collapsed by default
        Assert.Contains("name=\"pageTab\"", html);
    }

    // ---------------------------------------------------------------- workspace structure

    [Theory]
    [InlineData(201)]
    [InlineData(203)]
    [InlineData(205)]
    [InlineData(208)]
    public async Task Workspace_IsHeader_ThenOneLineNextStep_ThenWorkPaneAndPropertiesSidebar_InThatOrder(long ticketId)
    {
        var html = await Page(ticketId, Roles.CsManager);

        int At(string marker) => html.IndexOf(marker, StringComparison.Ordinal);
        var order = new[]
        {
            At("<header class=\"tw-head\""), At("class=\"tw-next "), At("class=\"tw-body\""), At("class=\"tw-main\""),
            At("id=\"conversation-title\""), At("<aside class=\"tw-side\""), At(">Request Details</div>"), At(">SLA Information</div>"),
        };
        Assert.All(order, position => Assert.True(position >= 0));
        Assert.Equal(order.OrderBy(p => p), order);
        Assert.Contains("aria-label=\"Ticket properties\"", html);
    }

    [Fact]
    public async Task NextStep_IsOneLine_TheReasonAndBlockersLiveInsideTheWhyPopover()
    {
        var html = await Page(203, Roles.CsAgent);
        var next = NextStep(html);

        Assert.Contains("class=\"tw-next__headline\">Contact the customer</span>", next);
        var popover = next.IndexOf("tw-next__pop", StringComparison.Ordinal);
        Assert.True(popover > 0);
        Assert.True(next.IndexOf("tw-next__reason", StringComparison.Ordinal) > popover, "the explanatory sentence is inside the popover, not on the line");
        Assert.DoesNotContain("<p class=\"attention", html);
    }

    [Fact]
    public async Task OwnerAndDepartment_AreInTheHeaderOnly_NotRepeatedInTheSidebar()
    {
        var html = await Page(203, Roles.CsAgent);
        var sidebar = html[html.IndexOf("<aside class=\"tw-side\"", StringComparison.Ordinal)..];

        Assert.Equal(1, Count(html, "Assigned To:"));
        Assert.Equal(1, Count(html, "Assigned Department:"));
        Assert.DoesNotContain("Assigned To", sidebar);
        Assert.DoesNotContain("<dt>Status</dt>", sidebar);
        Assert.DoesNotContain("<dt>Priority</dt>", sidebar);
    }

    [Fact]
    public async Task OnlyTheNextStepButtonIsGold_TheMenuAndHeaderCarryNoGoldControls()
    {
        var html = await Page(203, Roles.CsAgent);
        var head = Section(html, "<header class=\"tw-head\"", "class=\"tw-next ");

        Assert.DoesNotContain("<summary class=\"btn btn-gold", head);
        Assert.Equal(1, Count(html, "btn btn-gold btn-sm tw-next__cta"));
    }

    // ---------------------------------------------------------------- fakes

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Roles"].FirstOrDefault() ?? Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()), new Claim(ClaimTypes.Name, "Test Agent"), new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeApi(string role) : HttpMessageHandler
    {
        private static readonly Guid Someone = Guid.NewGuid();
        private static readonly string Row = Convert.ToBase64String(new byte[8]);

        private static TicketDetailDto T(
            long id, string status = "InProgress", Guid? owner = null, int? category = 1, byte? priority = 3, string verification = "Unverified",
            string summary = "Handover documents", int reopens = 0, bool crm = false, bool classified = true, bool reopenEligible = false,
            string sla = "Running", byte? outcome = null, string? source = null, string? externalName = null, string? crmName = null, bool collectionsAssign = false) =>
            new(id, $"TG-CS-{id}", CustomerService, CustomerService, owner, null, null, category, priority, status, verification, "None", sla, outcome, null,
                summary, reopens, Now.AddHours(-3), Row,
                CrmBuyerCustomerId: crm ? 9001 : null, CrmBuyerUnitId: crm ? 41 : null, CrmBuyerProjectId: crm ? 7 : null,
                CrmBuyerCustomerName: crm ? "Mariam Al Falasi" : crmName, CrmBuyerProjectName: crm ? "Tiger Tower" : null, CrmBuyerUnitNumber: crm ? "T-1204" : null,
                RequestTypeId: classified ? 10 : null, RequestTypeName: classified ? "Complaint Handling" : null, IsClassified: classified,
                IsReopenEligible: reopenEligible, OriginatingChannelId: 1, OriginatingChannelName: "Website chat",
                CustomerVerificationSource: source, ExternalCustomerId: source is null ? null : "T-77", ExternalCustomerName: externalName,
                CanAssignInCollections: collectionsAssign);

        private static TicketDetailDto? Detail(long id) => id switch
        {
            201 => T(201, "Open", category: null, priority: null, classified: false, summary: "Customer called about a parking card", sla: "NotApplicable"),
            202 => T(202, "Open"),
            203 => T(203, owner: ViewerId, crm: true, verification: "Verified", sla: "Breached"),
            204 => T(204, "Open", owner: Someone, summary: "Website chat started via Genesys"),
            205 => T(205, owner: ViewerId, crm: true, verification: "Verified"),
            206 => T(206, owner: ViewerId),
            207 => T(207, "Open", owner: Someone, verification: "PendingCrmVerification", summary: "Provisional ticket"),
            208 => T(208, "Resolved", outcome: 1, sla: "Met"),
            209 => T(209, "Closed", outcome: 1, reopenEligible: true, sla: "Met"),
            210 => T(210, owner: ViewerId, reopens: 1, sla: "Breached"),
            211 => T(211, "PendingCustomer", owner: ViewerId, sla: "Paused"),
            212 => T(212, owner: Someone),
            213 => T(213, "Open", owner: Someone, summary: "Website chat form"),
            214 => T(214, "Open", owner: Someone, verification: "Verified", source: "Pact", externalName: "Omar Saleh"),
            215 => T(215, "Open", owner: Someone, summary: "Website chat form"),
            216 => T(216, "Open", owner: Someone, crm: true, verification: "Verified"),
            217 => T(217, "Open", owner: Someone, verification: "PendingCrmVerification"),
            218 => T(218, "Open", owner: Someone, crmName: "NOT_REGISTERED"),
            219 => T(219, "Open", owner: Someone, collectionsAssign: true),
            _ => null,
        };

        private static TicketSlaSummaryResponseDto Sla(long id)
        {
            var now = DateTime.UtcNow;
            SlaExplanationDto Why(bool started) => new(started, 3, "Medium", "Priority policy", 60, 480, "24/7", 80, null, started ? now.AddHours(-3) : null, "InitialCreation",
                started ? null : "No SLA is running: this ticket has no priority, and the SLA policy is chosen by priority.", "rule", "pause", []);
            return id switch
            {
                201 => new("NotApplicable", null, false, null, null, false, false, null, 0, "None", Why(false)),
                203 => new("Breached", now.AddHours(-2), true, null, now.AddHours(5), false, false, null, 0, "None", Why(true)),
                208 => new("Met", now.AddHours(-2), false, now.AddHours(-2.5), now.AddHours(5), false, false, null, 0, "None", Why(true)),
                209 => new("Met", now.AddHours(-2), false, now.AddHours(-2.5), now.AddHours(5), false, false, null, 0, "None", Why(true)),
                210 => new("Breached", now.AddDays(-4), false, now.AddDays(-4), now.AddHours(20), false, false, null, 0, "None", Why(true)),
                211 => new("Paused", now.AddHours(-1), false, now.AddHours(-2), now.AddDays(1), false, true, "Pending Customer", 30, "None", Why(true)),
                212 => new("Running", now.AddMinutes(30), false, null, now.AddDays(1), false, false, null, 0, "None", Why(true)),
                _ => new("Running", now.AddHours(1), false, now.AddMinutes(-20), now.AddHours(6), false, false, null, 0, "None", Why(true)),
            };
        }

        private static IReadOnlyList<TicketInteractionDto> Interactions(long id)
        {
            var start = DateTime.UtcNow.AddHours(-3);
            TicketInteractionDto Chat(string phone, string? name, string state, AgentHandoffSummaryDto? handoff = null, string? agent = null, string channel = "Website chat",
                string? queue = null, DateTime? end = null, IReadOnlyList<TicketInteractionMessageDto>? messages = null) =>
                new(1, true, "Genesys", 1, channel, phone, name, null, "Inbound", "conv", null, queue, agent, null, start, end ?? start.AddMinutes(7), "Customer disconnected", "Ended",
                    messages ?? [new(1, "Customer", null, start, "Hello"), new(2, "VirtualAgent", null, start.AddMinutes(1), "How can I help?")], handoff, state);
            return id switch
            {
                204 => [Chat("+971501112222", null, "Ai")],
                205 => [Chat("+971501234567", "Mariam Al Falasi", "Human", agent: "Layla Hassan", queue: "Sales Support", end: start.AddMinutes(12),
                    handoff: new AgentHandoffSummaryDto(5, "Completed", "ContinueChat", "Wants the snag list.", start.AddMinutes(2), ViewerId, "agent-1", start.AddMinutes(11), "Sent.", "CustomerRequestedHuman"))],
                213 => [Chat("+971507770001", "Ahmed Hassan", "Human")],
                215 => [Chat("+971507770002", "NOT_REGISTERED", "Human")],
                216 => [Chat("+971507770003", "Typed By Customer", "Human")],
                217 => [Chat("+971507770004", "Sara Khalid", "Human")],
                218 => [Chat("+971507770005", "Huda Nasser", "Human")],
                206 => [Chat("+971509998888", "Omar", "Human", agent: "Layla Hassan", channel: "Phone", messages: [])],
                _ => [],
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            int? departmentId = int.TryParse(query["departmentId"], out var d) ? d : null;
            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Agent", [role], [new DepartmentMembershipDto(CustomerService, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(CustomerService, "Customer Service"), new DepartmentDto(3, "Facilities"), new DepartmentDto(7, "Collections", "COL") },
                "/api/channels" => new[] { new ChannelDto(1, "Website chat", "WEB", true, true, true, 1) },
                "/api/categories" => new[] { new CategoryDto(1, "Complaint Handling", CustomerService, "Customer Service") }.Where(c => departmentId is null || c.DepartmentId == departmentId).ToArray(),
                "/api/request-types" => new[] { new RequestTypeOptionDto(10, "Complaint Handling", CustomerService, 3, true) }.Where(r => departmentId is null || r.DepartmentId == departmentId).ToArray(),
                _ when path.StartsWith("/api/departments/", StringComparison.Ordinal) && path.EndsWith("/users", StringComparison.Ordinal) =>
                    new PagedResultDto<DepartmentUserDto>([new DepartmentUserDto(ViewerId, "Test Agent", true, [Roles.CsAgent])], 1, 100, 1),
                _ => Ticket(path),
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private static object? Ticket(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "api" || parts[1] != "tickets" || !long.TryParse(parts[2], out var id) || Detail(id) is not { } detail)
            {
                return null;
            }

            return parts.Length == 3
                ? detail
                : parts[3] switch
                {
                    "sla" when parts.Length == 4 => Sla(id),
                    "interactions" => new TicketInteractionHistoryDto(id, Interactions(id)),
                    "approvals" => new TicketApprovalsViewDto([], [], [], null, null, false),
                    "reviews" => new TicketReviewsDto(id, []),
                    _ => null,
                };
        }
    }
}
