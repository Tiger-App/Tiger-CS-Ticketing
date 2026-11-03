using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using TigerCS.Api.OpenApi;

namespace TigerCS.Tests.OpenApi;

/// <summary>
/// Boots the Api once and parses <c>/swagger/v1/swagger.json</c>, so the
/// content assertions below share one host instead of starting a new one per
/// fact.
/// </summary>
public sealed class SwaggerDocumentFixture : IAsyncLifetime
{
    private SwaggerApiFactory _factory = null!;

    public OpenApiDocument Document { get; private set; } = null!;

    /// <summary>Every endpoint the running application actually exposes, as "METHOD path".</summary>
    public IReadOnlyCollection<string> ApiExplorerEndpoints { get; private set; } = [];

    public async Task InitializeAsync()
    {
        _factory = new SwaggerApiFactory("Development");
        using var client = _factory.CreateClient();

        var json = await client.GetStringAsync("/swagger/v1/swagger.json");
        Document = OpenApiModelFactory.Parse(json, "json").Document
            ?? throw new InvalidOperationException("The OpenAPI document failed to parse.");

        var descriptions = _factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        ApiExplorerEndpoints = descriptions.ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Select(api => $"{api.HttpMethod?.ToUpperInvariant()} /{api.RelativePath}")
            .Distinct()
            .ToArray();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// What the generated OpenAPI document has to contain: every implemented
/// endpoint, the JWT bearer security definition behind Swagger UI's
/// <b>Authorize</b> button, and the documented tag groups.
/// </summary>
public class SwaggerDocumentTests(SwaggerDocumentFixture fixture) : IClassFixture<SwaggerDocumentFixture>
{
    /// <summary>
    /// Every endpoint implemented as of this change, as "METHOD path" with
    /// route constraints stripped (the form the OpenAPI document uses).
    /// Deliberately spelled out rather than derived, so adding an endpoint
    /// without documenting it fails here.
    /// </summary>
    public static readonly string[] ExpectedEndpoints =
    [
        "GET /health",

        "POST /api/auth/login",
        "POST /api/auth/screen-pop/redeem",
        "POST /api/auth/logout",
        "POST /api/auth/change-password",

        "POST /api/public/ticket-reviews/lookup",
        "POST /api/public/ticket-reviews/submit",

        "GET /api/users/me",
        "GET /api/users/assignable",
        "PATCH /api/users/{employeeId}/activation",

        "GET /api/roles",

        "GET /api/departments",
        "GET /api/departments/{departmentId}/users",
        "GET /api/channels",

        "GET /api/categories",

        "GET /api/crm/units/search",
        "GET /api/crm/units/{crmUnitId}",
        "GET /api/crm/units/{crmUnitId}/contacts",
        "GET /api/crm/buyers",

        "POST /api/verification-sessions",
        "GET /api/verification-sessions/{verificationSessionId}",

        "POST /api/intake-records",
        "GET /api/intake-records/{intakeRecordId}/customer-lookup",

        "POST /api/tickets",
        "GET /api/tickets",
        "GET /api/tickets/{ticketId}",
        "POST /api/tickets/{ticketId}/assignment",
        "POST /api/tickets/{ticketId}/ownership",
        "POST /api/tickets/{ticketId}/transfer",
        "POST /api/tickets/{ticketId}/status",
        "POST /api/tickets/{ticketId}/resolution",
        "POST /api/tickets/{ticketId}/close",
        "POST /api/tickets/{ticketId}/classification",
        "POST /api/tickets/{ticketId}/reopen",
        "POST /api/tickets/{ticketId}/reconciliation",
        "POST /api/tickets/{ticketId}/notes",
        "GET /api/tickets/{ticketId}/notes",
        "GET /api/tickets/{ticketId}/history",
        "GET /api/tickets/{ticketId}/approvals",
        "POST /api/tickets/{ticketId}/approvals",
        "POST /api/tickets/{ticketId}/approvals/{approvalId}/decision",
        "POST /api/tickets/{ticketId}/approvals/{approvalId}/cancellation",
        "POST /api/tickets/{ticketId}/workflow-events",
        "GET /api/tickets/{ticketId}/customer-history",
        "GET /api/tickets/{ticketId}/customer-profile",

        "GET /api/customers/lookup/ticket-history",
        "GET /api/customers/crm/{crmCustomerId}/ticket-history",
        "GET /api/customers/external/{source}/{externalCustomerId}/ticket-history",
        "GET /api/customers/search",
        "GET /api/customers",
        "GET /api/customers/profile/{customerKey}",
        "GET /api/customers/{customerKey}/documents",
        "GET /api/customers/{customerKey}/documents/{crmLeadId}/{documentType}/{recordId}/file",
        "POST /api/customers/{customerKey}/documents/send",

        "GET /api/dashboard",
        "GET /api/dashboard/overview",
        "GET /api/reports/team-performance",
        "GET /api/reports/team-performance/records",
        "GET /api/collections/receivables/customers",
        "GET /api/collections/receivables/instalments",
        "GET /api/collections/receivables/towers",
        "POST /api/collections/receivables/coverage/load",
        "GET /api/collections/receivables/snapshot/status",
        "GET /api/collections/reconciliation",
        "GET /api/collections/reconciliation/runs/{runId}",
        "POST /api/collections/reconciliation/run",
        "GET /api/collections/campaigns/preview",
        "GET /api/collections/campaigns/export",
        "GET /api/collections/units/payment-summary",
        "GET /api/collections/crm-owners/status",
        "GET /api/collections/leasing/payment-summary",
        "GET /api/collections/customers/lookup",
        "GET /api/collections/customers/crm/{crmCustomerId}/units",
        "GET /api/collections/review/runs/current",
        "GET /api/collections/review/runs/{runId}",
        "POST /api/collections/review/dispatches/{dispatchId}/cancel",
        "POST /api/collections/review/dispatches/{dispatchId}/admin-email/retry",
        "GET /api/collections/review/overlaps",
        "GET /api/collections/review/dispatches/{dispatchId}/contacts",
        "POST /api/collections/review/suppression/sweep",
        "POST /api/collections/review/refresh",
        "GET /api/collections/review/records",
        "GET /api/collections/review/reasons",
        "POST /api/collections/review/selection/summary",
        "POST /api/collections/review/dispatches",
        "GET /api/collections/review/dispatches",
        "GET /api/collections/review/dispatches/{dispatchId}",
        "POST /api/collections/review/dispatches/{dispatchId}/batches/{batchId}/reconcile",

        // SLA and Escalation (MVP-API-Contracts.md §5.1/§5.2/§5.7/§5.9).
        // Automatic Level 2 escalation on breach has no entry here on
        // purpose: §5.7 makes it system-triggered, so it is raised by the
        // background-job path and is not a client-callable endpoint.
        "GET /api/tickets/{ticketId}/sla",
        "POST /api/tickets/{ticketId}/sla/first-response",
        "POST /api/tickets/{ticketId}/escalations",
        "GET /api/tickets/{ticketId}/escalations",
        "POST /api/tickets/{ticketId}/sla/priority-downgrade-requests",
        "GET /api/tickets/{ticketId}/sla/priority-downgrade-requests",
        "GET /api/priority-downgrade-requests/pending",
        "POST /api/priority-downgrade-requests/{requestId}/approve",
        "POST /api/priority-downgrade-requests/{requestId}/reject",

        // Genesys integration phase 1 — the inbound boundary (one normalized
        // ingestion endpoint every channel converges on, plus conversation
        // end) and the ticket's conversation-history read.
        // The three externally consumable Genesys contracts — create, look
        // up, update. Nothing else on api/genesys: everything Genesys does to
        // a ticket after creating it goes through the one PATCH.
        "POST /api/genesys/tickets",
        "POST /api/genesys/documents/send-copy",
        "POST /api/genesys/verification/buyer-lookup",
        "POST /api/genesys/verification/otp/send",
        "POST /api/genesys/verification/otp/resend",
        "POST /api/genesys/verification/otp/verify",
        "PATCH /api/genesys/tickets/{ticketId}",
        "GET /api/genesys/customers/lookup",
        // Verified customer's unit and project details for the chatbot/voicebot.
        "POST /api/genesys/customers/unit-details",
        // Genesys agent identity mapping: the agent-action endpoint that
        // resolves a Genesys User ID to the Ticketing user and records
        // interaction ownership.
        "POST /api/genesys/agent-context",
        // Genesys Secure Screen Pop: issue a one-time, one-hour launch URL.
        "POST /api/genesys/screen-pop",
        // Collections (TigerCS_Collections_API_Specification.md): the same six
        // routes for Genesys and, internally, for TigerCS Web's Payment tab.
        "GET /api/genesys/collections/customers/{crmCustomerId}/outstanding",
        "GET /api/genesys/collections/customers/{crmCustomerId}/payments",
        "GET /api/genesys/collections/reminders/candidates",
        "POST /api/genesys/collections/reminders",
        "POST /api/genesys/collections/reminders/{reminderId}/outcomes",
        "GET /api/genesys/collections/customers/{crmCustomerId}/reminders",
        "GET /api/collections/customers/{crmCustomerId}/outstanding",
        "GET /api/collections/customers/{crmCustomerId}/payments",
        "GET /api/collections/reminders/candidates",
        "POST /api/collections/reminders",
        "POST /api/collections/reminders/{reminderId}/outcomes",
        "GET /api/collections/customers/{crmCustomerId}/reminders",
        "GET /api/collections/customer-lookup/payment-summary",
        "GET /api/collections/customers/by-key/{customerKey}/payment-summary",
        "GET /api/collections/customers/by-key/{customerKey}/payment-transactions",
        "GET /api/genesys/collections/customers/by-key/{customerKey}/payment-summary",
        "GET /api/genesys/collections/customers/by-key/{customerKey}/payment-transactions",
        "GET /api/pending-customer-interactions",
        "POST /api/pending-customer-interactions/{handoffId}/start",
        "POST /api/pending-customer-interactions/{handoffId}/complete",
        "POST /api/pending-customer-interactions/{handoffId}/cancel",
        "GET /api/tickets/{ticketId}/interactions",
        "GET /api/tickets/{ticketId}/activity-timeline",
        "GET /api/tickets/{ticketId}/reviews",
        "GET /api/admin/sla/configuration",
        "GET /api/admin/genesys/queue-mappings",
        "POST /api/admin/genesys/queue-mappings",
        "PUT /api/admin/genesys/queue-mappings/{genesysQueueMappingId}",

        // Administration / Workflow Designer phase (System Administrator only)
        "GET /api/request-types",
        "GET /api/admin/users",
        "GET /api/admin/users/{employeeId}",
        "POST /api/admin/users",
        "PUT /api/admin/users/{employeeId}/profile",
        "PATCH /api/admin/users/{employeeId}/activation",
        "PUT /api/admin/users/{employeeId}/roles",
        "POST /api/admin/users/{employeeId}/departments",
        "DELETE /api/admin/users/{employeeId}/departments/{departmentId}",
        "POST /api/admin/users/{employeeId}/password",
        "POST /api/admin/users/{employeeId}/unlock",
        "GET /api/admin/users/{employeeId}/permissions",
        "PUT /api/admin/users/{employeeId}/permissions",
        "GET /api/admin/users/password-policy",
        "GET /api/admin/departments",
        "GET /api/admin/departments/{departmentId}",
        "POST /api/admin/departments",
        "PUT /api/admin/departments/{departmentId}",
        "PATCH /api/admin/departments/{departmentId}/activation",
        "POST /api/admin/departments/{departmentId}/members",
        "DELETE /api/admin/departments/{departmentId}/members/{employeeId}",
        "GET /api/admin/request-types",
        "GET /api/admin/request-types/{requestTypeId}",
        "POST /api/admin/request-types",
        "PUT /api/admin/request-types/{requestTypeId}",
        "PATCH /api/admin/request-types/{requestTypeId}/activation",
        "PUT /api/admin/request-types/{requestTypeId}/assignment-rule",
        "PUT /api/admin/request-types/{requestTypeId}/approval-requirements/{approvalType}",
        "PUT /api/admin/request-types/{requestTypeId}/sla-policies/{priorityId}",
        "GET /api/admin/channels",
        "GET /api/admin/channels/{channelId}",
        "POST /api/admin/channels",
        "PUT /api/admin/channels/{channelId}",
        "PATCH /api/admin/channels/{channelId}/activation",
        "GET /api/admin/workflows/catalog",
        "GET /api/admin/workflows",
        "GET /api/admin/workflows/{workflowId}",
        "POST /api/admin/workflows",
        "PUT /api/admin/workflows/{workflowId}",
        "PATCH /api/admin/workflows/{workflowId}/activation",
        "POST /api/admin/workflows/{workflowId}/versions",
        "GET /api/admin/workflows/versions/{versionId}",
        "PUT /api/admin/workflows/versions/{versionId}/settings",
        "POST /api/admin/workflows/versions/{versionId}/steps",
        "PUT /api/admin/workflows/versions/{versionId}/steps/{stepId}",
        "DELETE /api/admin/workflows/versions/{versionId}/steps/{stepId}",
        "POST /api/admin/workflows/versions/{versionId}/steps/{stepId}/move",
        "PUT /api/admin/workflows/versions/{versionId}/steps/{stepId}/transitions",
        "POST /api/admin/workflows/versions/{versionId}/publish",
        "DELETE /api/admin/workflows/versions/{versionId}"
    ];

    private IReadOnlyCollection<string> DocumentedEndpoints() =>
        fixture.Document.Paths
            .SelectMany(path => path.Value.Operations!
                .Select(operation => $"{operation.Key.ToString().ToUpperInvariant()} {path.Key}"))
            .ToArray();

    [Fact]
    public void Document_ContainsEveryImplementedEndpoint()
    {
        var documented = DocumentedEndpoints();

        Assert.Empty(ExpectedEndpoints.Except(documented).Order());
    }

    [Fact]
    public void Document_ContainsNoEndpointBeyondTheImplementedOnes()
    {
        var documented = DocumentedEndpoints();

        Assert.Empty(documented.Except(ExpectedEndpoints).Order());
    }

    /// <summary>
    /// The direct check behind "every implemented endpoint is documented":
    /// compares the document against what the running application's API
    /// explorer reports, rather than against a hand-written list.
    /// </summary>
    [Fact]
    public void Document_MatchesTheApplicationsOwnEndpointList()
    {
        var documented = DocumentedEndpoints();

        Assert.NotEmpty(fixture.ApiExplorerEndpoints);
        Assert.Empty(fixture.ApiExplorerEndpoints.Except(documented).Order());
    }

    [Fact]
    public void Document_DeclaresTheBearerJwtSecurityScheme()
    {
        var schemes = fixture.Document.Components?.SecuritySchemes;

        Assert.NotNull(schemes);
        Assert.True(schemes!.ContainsKey("Bearer"));

        // type: http + scheme: bearer is what makes Swagger UI send the pasted
        // token as `Authorization: Bearer {token}`.
        var bearer = Assert.IsType<OpenApiSecurityScheme>(schemes["Bearer"]);
        Assert.Equal(SecuritySchemeType.Http, bearer.Type);
        Assert.Equal("bearer", bearer.Scheme);
        Assert.Equal("JWT", bearer.BearerFormat);
        Assert.NotNull(bearer.Description);
    }

    [Theory]
    [InlineData("/api/tickets", "POST")]
    [InlineData("/api/users/me", "GET")]
    [InlineData("/api/roles", "GET")]
    public void AuthenticatedEndpoints_RequireTheBearerScheme(string path, string method)
    {
        var operation = OperationFor(path, method);

        Assert.NotNull(operation.Security);
        Assert.Contains(
            operation.Security!,
            requirement => requirement.Keys.Any(scheme => scheme.Reference?.Id == "Bearer"));
    }

    [Theory]
    [InlineData("/health", "GET")]
    [InlineData("/api/auth/login", "POST")]
    public void AnonymousEndpoints_DoNotRequireTheBearerScheme(string path, string method)
    {
        var operation = OperationFor(path, method);

        Assert.True(operation.Security is null || operation.Security.Count == 0);
    }

    [Theory]
    [InlineData("/api/tickets", "POST")]
    [InlineData("/api/users/me", "GET")]
    public void AuthenticatedEndpoints_Document401And403(string path, string method)
    {
        var operation = OperationFor(path, method);

        Assert.NotNull(operation.Responses);
        Assert.True(operation.Responses!.ContainsKey("401"));
        Assert.True(operation.Responses.ContainsKey("403"));
    }

    [Fact]
    public void Document_DeclaresEveryExpectedTag()
    {
        var declared = fixture.Document.Tags?.Select(tag => tag.Name).ToArray() ?? [];

        Assert.Equal(OpenApiTags.Ordered.Select(t => t.Name).ToArray(), declared);
    }

    [Theory]
    [InlineData("/health", "GET", OpenApiTags.Health)]
    [InlineData("/api/auth/login", "POST", OpenApiTags.Authentication)]
    [InlineData("/api/users/me", "GET", OpenApiTags.Users)]
    [InlineData("/api/roles", "GET", OpenApiTags.Roles)]
    [InlineData("/api/departments", "GET", OpenApiTags.Departments)]
    [InlineData("/api/departments/{departmentId}/users", "GET", OpenApiTags.Departments)]
    [InlineData("/api/categories", "GET", OpenApiTags.Categories)]
    [InlineData("/api/crm/units/search", "GET", OpenApiTags.CrmLookup)]
    [InlineData("/api/crm/buyers", "GET", OpenApiTags.CrmLookup)]
    [InlineData("/api/verification-sessions", "POST", OpenApiTags.CustomerVerification)]
    [InlineData("/api/intake-records", "POST", OpenApiTags.Intake)]
    [InlineData("/api/tickets", "POST", OpenApiTags.Tickets)]
    [InlineData("/api/tickets/{ticketId}/assignment", "POST", OpenApiTags.Assignment)]
    [InlineData("/api/tickets/{ticketId}/ownership", "POST", OpenApiTags.Assignment)]
    [InlineData("/api/tickets/{ticketId}/transfer", "POST", OpenApiTags.Transfer)]
    [InlineData("/api/tickets/{ticketId}/status", "POST", OpenApiTags.TicketLifecycle)]
    [InlineData("/api/tickets/{ticketId}/resolution", "POST", OpenApiTags.TicketLifecycle)]
    [InlineData("/api/tickets/{ticketId}/close", "POST", OpenApiTags.TicketLifecycle)]
    [InlineData("/api/tickets/{ticketId}/notes", "POST", OpenApiTags.Notes)]
    [InlineData("/api/tickets/{ticketId}/reconciliation", "POST", OpenApiTags.CrmReconciliation)]
    public void EndpointGroups_AreTaggedAsDocumented(string path, string method, string expectedTag)
    {
        var operation = OperationFor(path, method);

        Assert.NotNull(operation.Tags);
        Assert.Contains(operation.Tags!, tag => tag.Name == expectedTag);
    }

    [Fact]
    public void EveryOperation_CarriesExactlyOneKnownTag()
    {
        var known = OpenApiTags.Ordered.Select(t => t.Name).ToHashSet();

        foreach (var (path, item) in fixture.Document.Paths)
        {
            foreach (var (method, operation) in item.Operations!)
            {
                var tags = operation.Tags?.Select(t => t.Name).ToArray() ?? [];
                Assert.True(tags.Length == 1, $"{method} {path} should carry exactly one tag, but carries {tags.Length}.");
                Assert.True(known.Contains(tags[0]!), $"{method} {path} carries unknown tag '{tags[0]}'.");
            }
        }
    }

    [Fact]
    public void RequestBodies_AreDocumentedForWriteEndpoints()
    {
        var login = OperationFor("/api/auth/login", "POST");

        Assert.NotNull(login.RequestBody);
        Assert.True(login.RequestBody!.Content!.ContainsKey("application/json"));
    }

    [Fact]
    public void QueryParameters_AreDocumentedForTheTicketQueue()
    {
        var queue = OperationFor("/api/tickets", "GET");
        var names = (queue.Parameters ?? []).Select(p => p.Name).ToArray();

        // Paging.
        Assert.Contains("Page", names);
        Assert.Contains("PageSize", names);

        // Filtering.
        Assert.Contains("DepartmentId", names);
        Assert.Contains("CategoryId", names);
        Assert.Contains("PriorityId", names);
        Assert.Contains("TicketStatus", names);
        Assert.Contains("VerificationStatus", names);
        Assert.Contains("OwnerEmployeeId", names);
        Assert.Contains("Search", names);

        // Sorting.
        Assert.Contains("SortBy", names);
        Assert.Contains("SortDir", names);
    }

    [Fact]
    public void PagingParameters_AreDocumentedForTheDepartmentUserListing()
    {
        var listing = OperationFor("/api/departments/{departmentId}/users", "GET");
        var names = (listing.Parameters ?? []).Select(p => p.Name).ToArray();

        Assert.Contains("page", names);
        Assert.Contains("pageSize", names);
        Assert.Contains("activeOnly", names);
    }

    /// <summary>
    /// Every enum-backed field crosses the wire as a string or a byte (no
    /// domain enum type is exposed directly), so the accepted values cannot
    /// come from the generator — they are written into the field's
    /// description. This asserts they are actually there, for each domain
    /// enum a client has to supply or interpret.
    /// </summary>
    [Theory]
    [InlineData("CreateIntakeRecordRequestDto", "channelId", new[] { "PHONE", "WHATSAPP", "LIVE_CHAT", "SOCIAL_DM", "WEBSITE", "WALK_IN_KIOSK", "MOBILE_APP", "INSTAGRAM", "FACEBOOK" })]
    [InlineData("IntakeRecordResponseDto", "crmVerificationStatus", new[] { "Unverified", "PendingCrmVerification", "Verified" })]
    [InlineData("CreateVerificationSessionRequestDto", "verificationMethod", new[] { "ManualAgentConfirmation", "AuthenticatedDigitalUser", "Otp", "FaceToFaceDocumentCheck", "Other" })]
    [InlineData("VerificationSessionResponseDto", "status", new[] { "InProgress", "Confirmed", "Consumed", "Expired", "Abandoned" })]
    [InlineData("ContactVerificationResponseDto", "contactType", new[] { "Owner", "Tenant", "Representative" })]
    [InlineData("TicketDetailDto", "ticketStatus", new[] { "Open", "InProgress", "PendingCustomer", "PendingThirdParty", "Resolved", "Closed" })]
    [InlineData("TicketDetailDto", "escalationLevel", new[] { "None", "Level1", "Level2", "Level3", "Level4" })]
    [InlineData("TicketDetailDto", "slaState", new[] { "Running", "Paused", "Met", "Breached", "NotApplicable" })]
    [InlineData("TicketDetailDto", "priorityId", new[] { "Critical", "High", "Medium", "Low" })]
    [InlineData("ResolveTicketRequestDto", "resolutionOutcome", new[] { "Resolved", "Cancelled", "Rejected", "Duplicate" })]
    public void EnumBackedFields_ListTheirAcceptedValues(string schemaName, string propertyName, string[] expectedValues)
    {
        var schemas = fixture.Document.Components?.Schemas;
        Assert.NotNull(schemas);
        Assert.True(schemas!.ContainsKey(schemaName), $"The document has no schema '{schemaName}'.");

        var properties = schemas[schemaName].Properties;
        Assert.NotNull(properties);
        Assert.True(properties!.ContainsKey(propertyName), $"'{schemaName}' has no property '{propertyName}'.");

        var description = properties[propertyName].Description ?? string.Empty;
        foreach (var value in expectedValues)
        {
            Assert.Contains(value, description, StringComparison.Ordinal);
        }
    }

    [Theory]
    // Nullable request fields the endpoints explicitly accept as absent.
    [InlineData("CreateIntakeRecordRequestDto", "rawUnitNumberEntered")]
    [InlineData("CreateIntakeRecordRequestDto", "priorityHint")]
    [InlineData("CreateTicketRequestDto", "unitReferenceId")]
    [InlineData("CreateTicketRequestDto", "contactReferenceId")]
    [InlineData("ActivationRequestDto", "reason")]
    [InlineData("ResolveTicketRequestDto", "reasonCode")]
    [InlineData("ResolveTicketRequestDto", "duplicateOfTicketId")]
    // Genesys Create Ticket: every context field is optional.
    [InlineData("GenesysInquiryRequest", "customerPhone")]
    [InlineData("GenesysInquiryRequest", "customerName")]
    [InlineData("GenesysInquiryRequest", "customerEmail")]
    [InlineData("GenesysInquiryRequest", "departmentId")]
    [InlineData("GenesysInquiryRequest", "departmentCode")]
    [InlineData("GenesysInquiryRequest", "queueId")]
    [InlineData("GenesysInquiryRequest", "queueName")]
    [InlineData("GenesysInquiryRequest", "agentId")]
    [InlineData("GenesysInquiryRequest", "agentName")]
    [InlineData("GenesysInquiryRequest", "direction")]
    [InlineData("GenesysInquiryRequest", "startedAtUtc")]
    [InlineData("GenesysInquiryRequest", "towerName")]
    [InlineData("GenesysInquiryRequest", "unitNumber")]
    [InlineData("GenesysInquiryRequest", "subject")]
    // Genesys Screen Pop: everything but the agent is optional.
    [InlineData("GenesysScreenPopRequest", "conversationId")]
    [InlineData("GenesysScreenPopRequest", "ticketId")]
    [InlineData("GenesysScreenPopRequest", "customerPhone")]
    public void NullableFields_AreNotMarkedRequired(string schemaName, string propertyName)
    {
        var schema = SchemaFor(schemaName);

        Assert.True(schema.Properties!.ContainsKey(propertyName), $"'{schemaName}' has no property '{propertyName}'.");
        Assert.DoesNotContain(propertyName, schema.Required ?? new HashSet<string>());
    }

    [Theory]
    [InlineData("LoginRequestDto", "username")]
    [InlineData("LoginRequestDto", "password")]
    [InlineData("CreateIntakeRecordRequestDto", "channelId")]
    [InlineData("CreateIntakeRecordRequestDto", "phoneNumber")]
    [InlineData("CreateIntakeRecordRequestDto", "isUnitRelated")]
    [InlineData("AssignTicketRequestDto", "assignedEmployeeId")]
    [InlineData("AssignTicketRequestDto", "rowVersion")]
    [InlineData("ChangeStatusRequestDto", "newStatus")]
    [InlineData("TransferTicketRequestDto", "targetDepartmentId")]
    [InlineData("GenesysInquiryRequest", "conversationId")]
    [InlineData("GenesysInquiryRequest", "channel")]
    public void MandatoryFields_AreMarkedRequired(string schemaName, string propertyName)
    {
        var schema = SchemaFor(schemaName);

        Assert.Contains(propertyName, schema.Required ?? new HashSet<string>());
    }

    [Theory]
    [InlineData("LoginRequestDto")]
    [InlineData("LoginResponseDto")]
    [InlineData("CreateIntakeRecordRequestDto")]
    [InlineData("TicketDetailDto")]
    [InlineData("TicketListResultDto")]
    public void Schemas_DescribeEveryProperty(string schemaName)
    {
        var schema = SchemaFor(schemaName);

        Assert.NotNull(schema.Properties);
        foreach (var (name, property) in schema.Properties!)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(property.Description),
                $"'{schemaName}.{name}' has no description in the OpenAPI document.");
        }
    }

    [Fact]
    public void Operations_CarryTheirSummaryFromTheXmlDocComments()
    {
        // Proves the XML documentation file is reaching the generator at all:
        // without it every summary would be null.
        foreach (var (path, item) in fixture.Document.Paths)
        {
            foreach (var (method, operation) in item.Operations!)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(operation.Summary),
                    $"{method} {path} has no summary in the OpenAPI document.");
            }
        }
    }

    private IOpenApiSchema SchemaFor(string schemaName)
    {
        var schemas = fixture.Document.Components?.Schemas;
        Assert.NotNull(schemas);
        Assert.True(schemas!.ContainsKey(schemaName), $"The document has no schema '{schemaName}'.");
        return schemas[schemaName];
    }

    private OpenApiOperation OperationFor(string path, string method)
    {
        Assert.True(fixture.Document.Paths.ContainsKey(path), $"The document has no path '{path}'.");
        var item = fixture.Document.Paths[path];

        var operation = item.Operations!
            .FirstOrDefault(o => string.Equals(o.Key.ToString(), method, StringComparison.OrdinalIgnoreCase));

        Assert.True(operation.Value is not null, $"The document has no {method} operation for '{path}'.");
        return operation.Value!;
    }
}
