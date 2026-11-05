using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>Read-only: distinct project names / unit numbers from persisted tickets and requester snapshots. See <see cref="IProjectCatalogRepository"/>.</summary>
public sealed class ProjectCatalogRepository(TigerCsDbContext dbContext) : IProjectCatalogRepository
{
    private const int MaxRows = 2000;

    public async Task<IReadOnlyList<ProjectOptionDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var crm = await dbContext.Tickets.AsNoTracking()
            .Where(t => t.CrmBuyerProjectName != null && t.CrmBuyerProjectName != "")
            .Select(t => new { Name = t.CrmBuyerProjectName!, Id = t.CrmBuyerProjectId })
            .Distinct().Take(MaxRows).ToListAsync(cancellationToken);
        var manual = await dbContext.Tickets.AsNoTracking()
            .Where(t => t.ManualProjectName != null && t.ManualProjectName != "")
            .Select(t => t.ManualProjectName!).Distinct().Take(MaxRows).ToListAsync(cancellationToken);
        var snapshot = await dbContext.TicketRequesterSnapshots.AsNoTracking()
            .Where(s => s.SnapshotPropertyName != null && s.SnapshotPropertyName != "")
            .Select(s => s.SnapshotPropertyName!).Distinct().Take(MaxRows).ToListAsync(cancellationToken);

        // One entry per name regardless of case or stray spaces; a CRM id wins when any ticket carried one; the CRM spelling is preferred.
        var byName = new Dictionary<string, ProjectOptionDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in crm.Select(c => new ProjectOptionDto(c.Name.Trim(), c.Id))
                     .Concat(manual.Concat(snapshot).Select(n => new ProjectOptionDto(n.Trim(), null))))
        {
            if (item.Name.Length == 0)
            {
                continue;
            }

            byName[item.Name] = byName.TryGetValue(item.Name, out var existing)
                ? new ProjectOptionDto(existing.Name, existing.CrmProjectId ?? item.CrmProjectId)
                : item;
        }

        return [.. byName.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<string>> ListUnitNumbersAsync(string projectName, CancellationToken cancellationToken = default)
    {
        var crm = await dbContext.Tickets.AsNoTracking()
            .Where(t => t.CrmBuyerProjectName == projectName && t.CrmBuyerUnitNumber != null && t.CrmBuyerUnitNumber != "")
            .Select(t => t.CrmBuyerUnitNumber!).Distinct().Take(MaxRows).ToListAsync(cancellationToken);
        var manual = await dbContext.Tickets.AsNoTracking()
            .Where(t => t.ManualProjectName == projectName && t.ManualUnitNumber != null && t.ManualUnitNumber != "")
            .Select(t => t.ManualUnitNumber!).Distinct().Take(MaxRows).ToListAsync(cancellationToken);
        var snapshot = await dbContext.TicketRequesterSnapshots.AsNoTracking()
            .Where(s => s.SnapshotPropertyName == projectName && s.SnapshotUnitNumber != null && s.SnapshotUnitNumber != "")
            .Select(s => s.SnapshotUnitNumber!).Distinct().Take(MaxRows).ToListAsync(cancellationToken);

        return [.. crm.Concat(manual).Concat(snapshot)
            .Select(u => u.Trim())
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)];
    }
}
