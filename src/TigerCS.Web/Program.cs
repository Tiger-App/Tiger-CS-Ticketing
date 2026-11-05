using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using Microsoft.AspNetCore.Authentication.Cookies;
using TigerCS.Infrastructure.Http;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddRazorPages(options =>
{
    // Every page requires sign-in by default; only Login/Index/AccessDenied
    // (and the anonymous health check below) opt out.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/AccessDenied");
    // Genesys Secure Screen Pop: opened in a browser with no TigerCS cookie;
    // the one-time token it carries is what signs the agent in.
    options.Conventions.AllowAnonymousToPage("/ScreenPop");
    // Customer satisfaction review: opened from an email link by a customer with no TigerCS login. The opaque token is the
    // credential and TigerCS.Api validates it on every call; a GET never saves anything.
    options.Conventions.AllowAnonymousToPage("/Review");

    // Administration area: System Administrator only. This only decides what
    // the Web renders — every api/admin/* endpoint enforces the same policy
    // server-side, so hiding the pages is never the actual protection.
    options.Conventions.AuthorizeFolder("/Admin", AdministrationPolicy.Name);

    // Reports: the CS Manager tier (CS Manager, General Manager, Chairman/
    // CEO, System Administrator). Same principle as /Admin — the Api's
    // ReportsRead policy is the real protection.
    options.Conventions.AuthorizeFolder("/Reports", ReportsPolicy.Name);

    // Administration -> Collections Tower Assignments: served at /Admin/CollectionsTowerAssignments but the page file lives in
    // Pages/Collections, so the /Admin folder's System-Administrator-only rule does not apply to it. It has its own gate: the Collections
    // managers and the System Administrator. The Api (CollectionsTeam.CanManageTowersAsync) is the real protection.
    options.Conventions.AuthorizePage("/Collections/TowerAssignments", TowerAssignmentsPolicy.Name);
});
builder.Services.AddHttpContextAccessor();

builder.Services.Configure<TigerCsApiOptions>(builder.Configuration.GetSection(TigerCsApiOptions.SectionName));
var apiBaseUrl = builder.Configuration.GetSection(TigerCsApiOptions.SectionName)["BaseUrl"]
    ?? throw new InvalidOperationException("TigerCsApi:BaseUrl is not configured.");

// Bridges the Web app's own session to TigerCS.Api: the browser only ever
// sees this app's encrypted, HttpOnly cookie (below); the Api's JWT lives
// server-side inside that cookie's claims and is attached to every
// outgoing call here, never sent to the client as readable data.
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services.AddScoped<TicketNameResolver>();
builder.Services.AddScoped<ViewerAccessService>();
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ViewerPermissionHandler>();

// AuthApiClient signs in/out and changes the caller's own password. Login
// and Screen Pop run before any session exists (the handler then attaches
// nothing); logout and change-password run inside one and need the token.
builder.Services.AddHttpClient<AuthApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();

// The public review page calls anonymous endpoints: no session exists, so no bearer handler.
builder.Services.AddHttpClient<ReviewsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));

// Every other client calls authenticated endpoints.
builder.Services.AddHttpClient<TicketsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<TicketSlaApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<PendingCustomerInteractionsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<UsersApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IntakeRecordsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CustomerLookupApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CrmBuyerLookupApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CategoriesApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<DepartmentsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ChannelsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CustomerHistoryApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CustomersApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<CustomerContactApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ProjectCatalogApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
// The receivables read runs two PACT report procedures concurrently; the API's request budget is
// CollectionsSource:PactReceivables:CommandTimeoutSeconds + 30 s (150 s by default), so the Web
// timeout must exceed it or the Web client gives up before the API can answer.
builder.Services.AddHttpClient<CollectionsApiClient>(client => { client.BaseAddress = new Uri(apiBaseUrl); client.Timeout = TimeSpan.FromSeconds(180); })
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<TowerAssignmentsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddScoped<CustomerPaymentPanelLoader>();
// Customer Details · Documents tab. Downloads buffer a CRM file (up to Crm:MaxDocumentBytes), so the timeout is the Api's CRM budget.
builder.Services.AddHttpClient<CustomerDocumentsApiClient>(client => { client.BaseAddress = new Uri(apiBaseUrl); client.Timeout = TimeSpan.FromSeconds(90); })
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddScoped<CustomerDocumentsPanelLoader>();
builder.Services.AddHttpClient<DashboardApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<AdminApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ReportsApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<RequestTypesApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Standard ASP.NET Core cookie authentication: encrypted via Data
        // Protection and HttpOnly by default. No prior cookie-auth pattern
        // exists anywhere in this solution (confirmed before writing this),
        // so this is the platform's own mechanism, not a custom scheme.
        options.Cookie.Name = "TigerCS.Web.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
        // Sliding is off: TigerCS.Api's JWT has a fixed lifetime and no
        // refresh endpoint, so the cookie's own expiry (set per sign-in to
        // the token's exact ExpiresAtUtc) must not silently outlive it.
        options.SlidingExpiration = false;
    });

builder.Services.AddAuthorization(options =>
{
    // Page gates follow the same decision as the Api: the role rule or an explicit grant, and never a denial. Display-side only.
    options.AddPolicy(AdministrationPolicy.Name, policy => policy.RequireAuthenticatedUser().AddRequirements(
        new ViewerPermissionRequirement([AdministrationPolicy.RequiredRole], Permissions.AdminUsersView, Permissions.AdminConfigurationView)));
    options.AddPolicy(ReportsPolicy.Name, policy => policy.RequireAuthenticatedUser().AddRequirements(
        new ViewerPermissionRequirement(ReportsPolicy.AllowedRoles, Permissions.ReportsView)));
    options.AddPolicy(TowerAssignmentsPolicy.Name, policy => policy.RequireAuthenticatedUser().AddRequirements(
        new ViewerPermissionRequirement(TowerAssignmentsPolicy.AllowedRoles, Permissions.CollectionsEdit)));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Strict CSP: same-origin only, no inline scripts/styles, no framing.
app.Use(async (context, next) =>
{
    context.Response.Headers.Append(
        "Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'");
    await next();
});

// An agent who clicks Customers twice, presses Enter twice on its filter, or
// navigates away mid-load makes the browser reset the first connection. That
// cancels this request's RequestAborted, which cancels the outbound call to
// TigerCS.Api (and, there, the Customers query itself). The resulting
// TaskCanceledException is expected: it is logged at Debug and answered 499
// rather than left unhandled to be logged as an application error and turned
// into a 500 for a browser that stopped listening. Anything that is not a
// client disconnect is untouched and still surfaces as the failure it is.
app.UseTigerCsClientDisconnectHandling();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// The former standalone Pending Customer Interactions page is now the
// Pending Interactions view of the Tickets workspace. Old links and
// bookmarks land there with their filters intact (the parameter names are
// unchanged); nothing is rendered at the old address.
app.MapGet("/PendingCustomerInteractions", (HttpRequest request) =>
{
    var query = QueryString.Create(TicketsViews.QueryKey, TicketsView.Pending.Key());
    foreach (var (key, values) in request.Query)
    {
        if (string.Equals(key, TicketsViews.QueryKey, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var value in values)
        {
            if (value is not null) query = query.Add(key, value);
        }
    }

    return Results.Redirect($"/Tickets{query}", permanent: true);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
