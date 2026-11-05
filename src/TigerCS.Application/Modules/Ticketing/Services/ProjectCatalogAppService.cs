using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>The New Ticket wizard's Project and Unit options: read-only, from what Tiger Ticketing already holds.</summary>
public sealed class ProjectCatalogAppService(IProjectCatalogRepository repository)
{
    public const int MaxProjectNameLength = 200;

    public Task<IReadOnlyList<ProjectOptionDto>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        repository.ListProjectsAsync(cancellationToken);

    /// <summary>Units known for the project; empty for a blank or over-long name rather than an error.</summary>
    public Task<IReadOnlyList<string>> ListUnitNumbersAsync(string? projectName, CancellationToken cancellationToken = default)
    {
        var name = projectName?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > MaxProjectNameLength
            ? Task.FromResult<IReadOnlyList<string>>([])
            : repository.ListUnitNumbersAsync(name, cancellationToken);
    }
}
