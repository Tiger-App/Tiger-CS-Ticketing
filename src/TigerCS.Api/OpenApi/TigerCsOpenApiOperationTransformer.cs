using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TigerCS.Api.OpenApi;

/// <summary>
/// Per-operation documentation: applies the <c>[Tags]</c> group an endpoint
/// declares, and — for endpoints that are not <c>[AllowAnonymous]</c> —
/// records that they require the <c>Bearer</c> scheme and can answer 401/403.
///
/// <para>
/// The 401/403 responses are not invented: Program.cs registers JWT bearer
/// authentication and <c>AddTigerCsAuthorizationPolicies</c> sets a fallback
/// policy requiring an authenticated, active employee, so every endpoint
/// without <c>[AllowAnonymous]</c> can already return them. Documenting them
/// per-operation avoids repeating an identical
/// <c>[ProducesResponseType]</c> pair on every authenticated action.
/// </para>
///
/// <para>Documentation only — no request handling depends on this class.</para>
/// </summary>
internal sealed class TigerCsOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        // Tags: read the endpoint's own [Tags]/WithTags metadata rather than
        // relying on the generator's default (the controller's type name),
        // which would produce "Tickets" for six distinct groups and
        // "IntakeRecords"/"VerificationSessions"/"Crm" for the rest.
        var tags = ResolveTags(context.Description);
        if (tags is { Count: > 0 })
        {
            operation.Tags = new HashSet<OpenApiTagReference>(
                tags.Select(tag => new OpenApiTagReference(tag, context.Document)));
        }

        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return Task.CompletedTask;
        }

        // An endpoint that names the Queueing System's own scheme is opened by its API key, not by a staff token.
        var usesApiKey = metadata.OfType<IAuthorizeData>().Any(a => a.AuthenticationSchemes == TigerCS.Api.Integrations.QueueingApiKeyDefaults.Scheme);

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(
                usesApiKey ? TigerCsOpenApiDocumentTransformer.QueueingApiKeySchemeName : TigerCsOpenApiDocumentTransformer.BearerSchemeName,
                context.Document)] = []
        });

        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd(
            "401",
            new OpenApiResponse { Description = "Unauthenticated — the access token is missing, expired, or invalid." });
        operation.Responses.TryAdd(
            "403",
            new OpenApiResponse { Description = "Authenticated, but not permitted to perform this operation." });

        return Task.CompletedTask;
    }

    /// <summary>
    /// The endpoint's declared tag group: the action method's own
    /// <c>[Tags]</c> if it has one, otherwise its controller's. Read
    /// directly off the action/controller rather than by scanning
    /// <c>EndpointMetadata</c>, so an action-level tag reliably wins over a
    /// controller-level one without depending on the order MVC happens to
    /// place attributes in that list. Minimal-API endpoints (<c>/health</c>)
    /// have no controller and fall through to their endpoint metadata.
    /// </summary>
    private static IReadOnlyList<string>? ResolveTags(ApiDescription description)
    {
        if (description.ActionDescriptor is ControllerActionDescriptor controllerAction)
        {
            var declared =
                controllerAction.MethodInfo.GetCustomAttribute<TagsAttribute>(inherit: false)
                ?? controllerAction.ControllerTypeInfo.GetCustomAttribute<TagsAttribute>(inherit: true);
            return declared?.Tags;
        }

        return description.ActionDescriptor.EndpointMetadata.OfType<ITagsMetadata>().LastOrDefault()?.Tags;
    }
}
