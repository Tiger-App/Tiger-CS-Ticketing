using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// The projects and units Tiger Ticketing already knows. TigerCS keeps no project table: a project is a name carried by the tickets
/// (CRM Buyer snapshot, manual entry, requester snapshot), so the catalog is read from those and never creates or changes anything.
/// </summary>
public interface IProjectCatalogRepository
{
    /// <summary>Every distinct project name on record, one entry per name (case-insensitive), alphabetical.</summary>
    Task<IReadOnlyList<ProjectOptionDto>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>The unit numbers previously recorded for a project (matched case-insensitively), alphabetical.</summary>
    Task<IReadOnlyList<string>> ListUnitNumbersAsync(string projectName, CancellationToken cancellationToken = default);
}
