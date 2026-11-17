using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TigerCS.Api.OpenApi;

/// <summary>
/// Fills in the document-level parts of the generated OpenAPI document that
/// the framework cannot infer from the endpoints themselves: the API's
/// title/version/description, the <c>Bearer</c> JWT security scheme that
/// backs Swagger UI's <b>Authorize</b> button, and the ordered tag list with
/// its group descriptions (<see cref="OpenApiTags"/>).
///
/// <para>
/// Documentation only — nothing here participates in request handling. The
/// security scheme <i>describes</i> the authentication Program.cs already
/// configures (JWT bearer, <c>Authorization: Bearer {token}</c>); it does
/// not define or change it.
/// </para>
/// </summary>
internal sealed class TigerCsOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// The security-scheme key. Named exactly "Bearer" so the header Swagger
    /// UI sends is <c>Authorization: Bearer {token}</c> and the value the
    /// user pastes into the Authorize box is the bare access token, with no
    /// "Bearer " prefix of their own.
    /// </summary>
    public const string BearerSchemeName = "Bearer";

    /// <summary>The Queueing System's API-key scheme (header <c>X-Queueing-Api-Key</c>), used only by <c>POST /api/integrations/queueing/events</c>.</summary>
    public const string QueueingApiKeySchemeName = "QueueingApiKey";

    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Tiger CS Ticketing API",
            Version = "v1",
            Description =
                "HTTP API for the Tiger CS Ticketing MVP: authentication and user/role/department " +
                "administration, read-only Tiger CRM lookups, customer verification, intake capture, " +
                "and the ticket lifecycle (creation, assignment, transfer, status, resolution, closing, " +
                "notes, and CRM reconciliation).\n\n" +
                "**Authentication.** Every endpoint except `GET /health` and `POST /api/auth/login` requires " +
                "a JWT access token. Call `POST /api/auth/login`, copy the `accessToken` from the response, " +
                "press **Authorize** above and paste the token on its own — Swagger UI adds the " +
                "`Bearer ` prefix and sends `Authorization: Bearer {token}` with each request.\n\n" +
                "**Errors.** Failures are returned as RFC 7807 problem details."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerSchemeName] = new OpenApiSecurityScheme
        {
            // type: http + scheme: bearer is what makes Swagger UI send the
            // token as `Authorization: Bearer {token}`. (`name`/`in` are not
            // set: OpenAPI only defines them for apiKey schemes, and they are
            // dropped when an http scheme is serialised.)
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "Paste only the access token returned by POST /api/auth/login — do not include the " +
                "word \"Bearer\". Swagger UI sends it as: Authorization: Bearer {token}."
        };

        document.Components.SecuritySchemes[QueueingApiKeySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = TigerCS.Api.Integrations.QueueingApiKeyDefaults.DefaultHeaderName,
            Description =
                "The Queueing System's own API key (not a staff token). Only the key's SHA-256 is configured in TigerCS " +
                "(QueueingIntegration:Authentication:ApiKeys). It opens POST /api/integrations/queueing/events and nothing else."
        };

        // Declared up-front and in a fixed order so Swagger UI renders the
        // groups in flow order with a description each, instead of inferring
        // a bare, alphabetically-sorted tag list from the operations.
        document.Tags ??= new HashSet<OpenApiTag>();
        document.Tags.Clear();
        foreach (var (name, description) in OpenApiTags.Ordered)
        {
            document.Tags.Add(new OpenApiTag { Name = name, Description = description });
        }

        return Task.CompletedTask;
    }
}
