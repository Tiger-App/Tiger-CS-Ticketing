using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Ticketing.Services;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using TigerCS.Api.OpenApi;
using TigerCS.Infrastructure;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Infrastructure.Http;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Seed;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Integrations.Modules.EmailIntegration;

// Never log token/claim contents (review item 4) — IdentityModelEventSource's PII
// logging defaults to off already, but this makes the choice explicit rather than
// relying on the library default silently staying that way across upgrades.
IdentityModelEventSource.ShowPII = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Every expected failure path in this Api already returns a structured
// ProblemDetails body (Problem(...)/ValidationProblem(...) throughout the
// controllers). Without this, an *unexpected* exception instead falls
// through to Kestrel's bare, empty-body 500 — which TigerCS.Web's
// ApiClientBase.DescribeFailureAsync cannot parse for a "detail", so the
// Web page silently shows its own generic fallback text ("Unable to load
// the department list.", "Could not record this interaction.") with no
// indication anything actually went wrong server-side. This maps every
// unhandled exception to a real ProblemDetails JSON body instead.
builder.Services.AddProblemDetails();

// Swagger/OpenAPI document generation (TigerCS.Api/OpenApi). Registering the
// generator is unconditional; whether /swagger and /swagger/v1/swagger.json
// are actually reachable is decided by MapTigerCsSwagger below, which maps
// nothing outside OpenApiDocumentation.EnabledEnvironments.
builder.Services.AddTigerCsOpenApi();

builder.Services.AddTigerCsInfrastructure(builder.Configuration);
builder.Services.AddTigerCsIntegrations(builder.Configuration);

// Bound lazily from JwtOptions (IOptions<JwtOptions>, resolved at first use) rather
// than read from builder.Configuration inline here — the latter would run before
// test/host configuration overrides (e.g. WebApplicationFactory) are merged in.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>, IHostEnvironment>((bearerOptions, jwtOptions, env) =>
    {
        var jwt = jwtOptions.Value;

        // RequireHttpsMetadata governs fetching OIDC metadata over HTTP vs HTTPS
        // (moot here — no Authority/metadata address is configured, so no
        // metadata endpoint is ever fetched) but is set explicitly per
        // environment anyway, per review item 4, rather than left at whatever
        // the library's own default happens to be.
        bearerOptions.RequireHttpsMetadata = !env.IsDevelopment();

        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

// Dashboard display thresholds (the Awaiting Human Agent risk age). Display
// only — no business rule is configurable from here.
builder.Services.Configure<DashboardOptions>(
    builder.Configuration.GetSection(DashboardOptions.SectionName));
builder.Services.AddScoped(sp => sp.GetRequiredService<IOptions<DashboardOptions>>().Value);

// Priority-downgrade request lifetime (SlaAndEscalation:PriorityDowngrade:ExpiryHours,
// default 7 days). Only the expiry window is configurable; who may decide is not.
builder.Services.Configure<TigerCS.Application.Modules.SlaAndEscalation.Services.PriorityDowngradeOptions>(
    builder.Configuration.GetSection(TigerCS.Application.Modules.SlaAndEscalation.Services.PriorityDowngradeOptions.SectionName));
builder.Services.AddScoped(sp => sp.GetRequiredService<IOptions<TigerCS.Application.Modules.SlaAndEscalation.Services.PriorityDowngradeOptions>>().Value);

builder.Services.AddAuthorization(options => options.AddTigerCsAuthorizationPolicies());

var app = builder.Build();

// No production deployment is authorized at this pilot stage — enforced
// through release governance and documentation (docs/DEV-SETUP.md,
// docs/architecture/adr/0022-deployment-strategy.md), not by refusing to
// start here. An unconditional IsProduction() throw would make this
// application unable to ever run in Production even once that's actually
// authorized, which isn't this code's decision to make.

// Fail fast for the JWT signing key specifically: validate it
// exists and meets the minimum length for HS256 (256 bits / 32 bytes) before
// the app starts accepting traffic, instead of only discovering a missing or
// weak key on the first authenticated request.
using (var startupScope = app.Services.CreateScope())
{
    var jwtOptions = startupScope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
    if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
    {
        throw new InvalidOperationException("Jwt:SigningKey is not configured. See docs/DEV-SETUP.md.");
    }

    if (Encoding.UTF8.GetByteCount(jwtOptions.SigningKey) < 32)
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey must be at least 32 bytes (256 bits) for HMAC-SHA256. See docs/DEV-SETUP.md.");
    }

    if (string.IsNullOrWhiteSpace(jwtOptions.Issuer) || string.IsNullOrWhiteSpace(jwtOptions.Audience))
    {
        throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience must both be configured. See docs/DEV-SETUP.md.");
    }
}

// Fail fast if the mock CRM adapter would run outside Development/Testing.
// The actual decision is CrmGatewaySafety.IsUnsafe — conditional on the
// selected gateway type (Crm:Provider), not on the environment name alone;
// see that class's own remarks. "Http" — the standard for Development, UAT
// and Production (IntegrationsServiceCollectionExtensions) — never resolves
// MockCrmGateway and is judged safe here in every environment, Production
// included.
using (var crmStartupScope = app.Services.CreateScope())
{
    var crmOptions = crmStartupScope.ServiceProvider.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
    if (CrmGatewaySafety.IsUnsafe(crmOptions.Provider, app.Environment.EnvironmentName))
    {
        throw new InvalidOperationException(
            $"Crm:Provider is 'Mock' in environment '{app.Environment.EnvironmentName}'. MockCrmGateway is " +
            "never production-ready (see its own remarks) and may only run in " +
            $"{string.Join("/", CrmGatewaySafety.MockAllowedEnvironments)}. Set Crm:Provider to \"Http\" — the " +
            "standard for every real environment — before deploying to this environment.");
    }
}

// Fail fast if the fake SMS sender (which keeps codes and pretends delivery worked) would run
// outside Development/Testing. "Broadnet" (real SMS) stays inert until fully configured; "Disabled" is the default.
using (var smsStartupScope = app.Services.CreateScope())
{
    var smsOptions = smsStartupScope.ServiceProvider.GetRequiredService<IOptions<SmsOptions>>().Value;
    if (SmsSafety.IsUnsafe(smsOptions.Provider, app.Environment.EnvironmentName))
    {
        throw new InvalidOperationException(
            $"Sms:Provider is 'Fake' in environment '{app.Environment.EnvironmentName}'. The fake sender never sends an SMS and " +
            $"may only run in {string.Join("/", SmsSafety.FakeAllowedEnvironments)}. Use \"Disabled\" or \"Broadnet\".");
    }
}

// The chatbot document-copy flow (email OTP verification + sending a customer's
// documents) ships disabled. In Production it stays disabled until real UAT
// verification has passed and someone sets CrmDocuments:AllowInProduction
// deliberately — enabling it alone is not enough.
//
// These two guards fail CLOSED but never stop the host: a misconfigured document feature must not take down Ticketing,
// Genesys or the Agent Desk. The feature is switched off for the process (the options object is a shared singleton, so
// every consumer sees it), the document/OTP routes answer 503 DOCUMENT_COPY_DISABLED, and the reason is logged as an error.
using (var documentsStartupScope = app.Services.CreateScope())
{
    var documentOptions = documentsStartupScope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.CrmDocuments.CrmDocumentOptions>();
    if (documentOptions.Enabled && app.Environment.IsProduction() && !documentOptions.AllowInProduction)
    {
        documentOptions.Enabled = false;
        app.Logger.LogError(
            "CrmDocuments:Enabled is true in Production but CrmDocuments:AllowInProduction is not. Customer document delivery stays "
            + "DISABLED for this process until real UAT verification has passed (docs/Genesys/Document-Copy-API.md, Status). "
            + "Set CrmDocuments:AllowInProduction=true deliberately after sign-off to enable it; the rest of the application is unaffected.");
    }

    // The OTP code hash is keyed with CrmDocuments:OtpCodePepper. Without it the code falls back to a constant key
    // that is public in the source, so refuse to run the OTP flow with it anywhere but Development/Testing.
    if (documentOptions.Enabled && string.IsNullOrWhiteSpace(documentOptions.OtpCodePepper)
        && !app.Environment.IsDevelopment() && !string.Equals(app.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
    {
        documentOptions.Enabled = false;
        app.Logger.LogError(
            "CrmDocuments:Enabled is true but CrmDocuments:OtpCodePepper is empty. Customer document delivery stays DISABLED for this "
            + "process. Set a random secret (environment variable CrmDocuments__OtpCodePepper) before enabling customer OTP "
            + "verification outside Development; the rest of the application is unaffected.");
    }
}

// Verification test mode redirects OTP/document messages for REAL CRM customers to test inboxes. It may only run in
// Local/Development/UAT/Testing, and only fully configured; anything else stops the host rather than risk a real send.
using (var verificationTestingScope = app.Services.CreateScope())
{
    var testingPolicy = verificationTestingScope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.CrmDocuments.VerificationTestingPolicy>();
    if (testingPolicy.IsBlocked)
    {
        throw new InvalidOperationException(testingPolicy.ConfigurationError);
    }

    if (testingPolicy.Enabled)
    {
        app.Logger.LogWarning(
            "VerificationTesting is ON in {Environment}: OTP and document messages are redirected to the configured test inboxes; no real customer is contacted.",
            app.Environment.EnvironmentName);
    }
}

// Customer notification routing (CustomerNotifications:IsTestMode). Never stops the host: missing/invalid configuration means test
// mode, and a test mode without a recipient simply skips that delivery. The reason is logged here once at startup.
{
    var customerNotifications = app.Services.GetRequiredService<TigerCS.Application.Modules.Notifications.CustomerNotificationModePolicy>();
    if (customerNotifications.ConfigurationWarning is { } notificationWarning)
    {
        app.Logger.LogWarning("CustomerNotifications: {Warning}", notificationWarning);
    }

    if (customerNotifications.IsTest)
    {
        app.Logger.LogWarning(
            "CustomerNotifications is in TEST mode in {Environment}: all customer email goes to the configured TestEmail and all customer SMS to TestPhone; no customer is contacted.",
            app.Environment.EnvironmentName);
    }

    if (customerNotifications.PublicBaseUrl is null)
    {
        app.Logger.LogWarning(
            "CustomerNotifications:PublicBaseUrl is not configured; Resolved/Closed emails will carry no review section.");
    }
}

// Fail fast if fixture Collections balances would be served outside
// Development/Testing — a sample balance shown to an agent, or read by the
// voice bot, as if it were a customer's real debt is the one failure this
// module must never have. Same conditional-on-provider shape as the CRM guard.
using (var collectionsStartupScope = app.Services.CreateScope())
{
    var sourceOptions = collectionsStartupScope.ServiceProvider.GetRequiredService<IOptions<CollectionsSourceOptions>>().Value;
    var edsmOptions = collectionsStartupScope.ServiceProvider.GetRequiredService<IOptions<CollectionsEdsmOptions>>().Value;
    if (CollectionsSourceSafety.IsUnsafe(sourceOptions, edsmOptions, app.Environment.EnvironmentName))
    {
        throw new InvalidOperationException(
            $"CollectionsSource:Provider or EdsmProvider is 'Fixture' in environment '{app.Environment.EnvironmentName}'. Fixture balances are "
            + $"sample data and may only run in {string.Join("/", CollectionsSourceSafety.FixtureAllowedEnvironments)}. "
            + "Set CollectionsSource:Provider / EdsmProvider to \"Unavailable\" (or EdsmProvider to \"Pact\").");
    }
}

// Fail fast if the recording email adapter would run outside
// Development/Testing while email delivery is expected. Same
// conditional-on-provider shape as the CRM guard above
// (EmailSenderSafety.IsUnsafe), and for a sharper reason:
// RecordingEmailSender reports every send as successful without contacting
// any provider, so running it for real would mark tickets acknowledged, write
// Sent notification rows and satisfy every dashboard and audit query while no
// customer ever received anything. A silent, total failure that looks exactly
// like success is worse than a loud one.
//
// EmailNotifications:Enabled = false (EmailNotificationOptions.Enabled) declares
// that email delivery is not part of the current phase (UAT / management
// demo), so the recording adapter cannot be mistaken for a delivering one and
// the host may start in any environment. The flag changes this guard only —
// the adapter wiring is untouched, so nothing is sent either way. With
// Enabled = true the original protection applies unchanged.
using (var emailStartupScope = app.Services.CreateScope())
{
    var emailOptions = emailStartupScope.ServiceProvider.GetRequiredService<IOptions<EmailNotificationOptions>>().Value;
    if (EmailSenderSafety.IsUnsafe(emailOptions.Enabled, emailOptions.Provider, app.Environment.EnvironmentName))
    {
        throw new InvalidOperationException(
            $"EmailNotifications:Provider is 'Recording' with EmailNotifications:Enabled = true in environment "
            + $"'{app.Environment.EnvironmentName}'. RecordingEmailSender never delivers anything (see its own "
            + $"remarks) and may only run in {string.Join("/", EmailSenderSafety.RecordingAllowedEnvironments)} "
            + "when delivery is enabled. Either set EmailNotifications:Enabled to false, or set "
            + "EmailNotifications:Provider to \"Smtp\" and supply the Microsoft 365 SMTP settings "
            + "(see docs/Customer-Email-Notifications.md) before deploying to this environment.");
    }

    // A configuration that enables SMTP delivery without the settings it
    // needs would pass startup and then fail on the first customer email —
    // in a background job, hours later. Refuse it now, naming the keys
    // (never the values).
    var emailConfigurationErrors = EmailSenderSafety.Validate(emailOptions);
    if (emailConfigurationErrors.Count > 0)
    {
        throw new InvalidOperationException(
            "Customer email notifications are enabled but not fully configured: "
            + string.Join(" ", emailConfigurationErrors)
            + " See docs/Customer-Email-Notifications.md.");
    }
}

// Swagger UI at /swagger and the OpenAPI JSON at /swagger/v1/swagger.json,
// in Development and Testing only — never mapped in Production (see
// OpenApiDocumentation.EnabledEnvironments).
app.MapTigerCsSwagger();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await DevSeedData.SeedAsync(scope.ServiceProvider);
}

// SLA-Architecture.md §14 / ADR-0015 — registers the recurring safety sweep
// with Hangfire. A no-op when BackgroundJobs:Enabled is false; registered
// after Build() because the recurring-job manager needs live job storage.
// The per-deadline scheduled jobs of §13 need no registration here: they are
// enqueued as each due timestamp is computed.
using (var backgroundJobScope = app.Services.CreateScope())
{
    var backgroundJobOptions = backgroundJobScope.ServiceProvider
        .GetRequiredService<IOptions<BackgroundJobOptions>>().Value;
    app.Services.UseTigerCsRecurringSlaSweep(backgroundJobOptions);

    // Chatbot inactivity closure — reads the persisted per-interaction timers,
    // so it is safe across restarts. Also a no-op when BackgroundJobs:Enabled
    // is false.
    app.Services.UseTigerCsRecurringChatbotInactivityClose(backgroundJobOptions);
    if (!backgroundJobOptions.Enabled)
    {
        app.Logger.LogWarning(
            "BackgroundJobs:Enabled is false: Hangfire is not running, so chatbot customer-inactivity tickets will NOT be closed "
            + "automatically (timers are still recorded and are honoured once the jobs are enabled), and the Collections receivables snapshot will NOT "
            + "refresh every two hours: the pages show it as stale or not loaded until a Retry runs a refresh in this process.");
    }

    // ADR-0013/ADR-0015 — the recurring Outbox dispatcher. Registered
    // alongside the sweep because both need live job storage. With
    // BackgroundJobs:Enabled false this is a no-op and Outbox rows simply
    // stay Pending: the switch governs when delivery happens, never whether
    // the intent to deliver was durably recorded.
    var outboxOptions = backgroundJobScope.ServiceProvider
        .GetRequiredService<IOptions<OutboxDispatchOptions>>().Value;
    app.Services.UseTigerCsRecurringOutboxDispatch(backgroundJobOptions, outboxOptions);

    // Collections payment reminders — registered only while automatic
    // scheduling is active (off until Collections confirms the open rules).
    app.Services.UseTigerCsRecurringCollectionsReminders(
        backgroundJobOptions,
        backgroundJobScope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Collections.CollectionsOptions>());

    // Collections PACT receivables snapshot refresh (Due & Overdue page, Campaigns). SQL Server Agent is not used.
    app.Services.UseTigerCsRecurringCollectionsReceivablesRefresh(
        backgroundJobOptions,
        backgroundJobScope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Collections.ReceivablesSnapshotOptions>());

    // CRM owners of each unit (bulk, local copy joined to the receivables) - registered only while CollectionsSource:CrmOwners:Enabled is true.
    app.Services.UseTigerCsRecurringCollectionsCrmOwnersRefresh(
        backgroundJobOptions,
        backgroundJobScope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Collections.CollectionsCrmOwnersOptions>());

    // Paid-after-upload suppression of Genesys contacts — registered only while upload and suppression are both enabled.
    app.Services.UseTigerCsRecurringGenesysSuppression(
        backgroundJobOptions,
        backgroundJobScope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Collections.Review.GenesysOutboundOptions>(),
        backgroundJobScope.ServiceProvider.GetRequiredService<TigerCS.Application.Modules.Collections.CollectionsOptions>().TimeZoneId);
}

app.UseExceptionHandler();

// Inside the exception handler, so a request the browser abandoned is
// recognised before the generic handler treats it as a defect. An agent who
// clicks Customers (or Apply on its filter) twice in quick succession makes
// the browser reset the first connection; RequestAborted then cancels the
// CancellationToken the controller passed down, and the Customers query
// throws TaskCanceledException from CustomerDirectoryRepository. That is
// cancellation working, not a fault: it is logged at Debug and answered 499,
// never logged at Error and never answered 500. A SQL timeout leaves
// RequestAborted unsignalled and still reaches UseExceptionHandler as the
// error it is.
app.UseTigerCsClientDisconnectHandling();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Phase 1 foundation placeholder (S-01), kept anonymous per
// Security-Architecture.md §5's explicit health-check exception.
app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
    .AllowAnonymous()
    .WithTags(OpenApiTags.Health)
    .WithName("GetHealth")
    .WithSummary("Liveness probe.")
    .WithDescription(
        "Returns 200 with a constant payload while the application is running. The only endpoint "
        + "that does not require authentication apart from POST /api/auth/login.")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    // Minimal-API endpoints carry no XML doc comments, so the 200's
    // description would otherwise be the generic reason phrase.
    .AddOpenApiOperationTransformer((operation, _, _) =>
    {
        operation.Responses!["200"].Description = "The application is running.";
        return Task.CompletedTask;
    });

app.MapControllers();

app.Run();

/// <summary>The <c>GET /health</c> payload.</summary>
/// <remarks>A named type rather than an anonymous object purely so the response has a schema in the OpenAPI document; the JSON it serialises to is unchanged.</remarks>
/// <param name="Status">Constant literal <c>"healthy"</c>.</param>
public sealed record HealthResponse(string Status);
