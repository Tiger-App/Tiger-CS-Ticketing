using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/projects</c> — the Project / Unit options of New Ticket Step 2. Read-only.</summary>
public sealed class ProjectCatalogApiClient(HttpClient httpClient, ILogger<ProjectCatalogApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<List<ProjectOptionDto>>> ListProjectsAsync(CancellationToken cancellationToken) =>
        GetAsync<List<ProjectOptionDto>>("api/projects", cancellationToken);

    public Task<ApiResult<List<string>>> ListUnitsAsync(string project, CancellationToken cancellationToken) =>
        GetAsync<List<string>>($"api/projects/units?project={Uri.EscapeDataString(project)}", cancellationToken);
}
