using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

/// <summary>The tower (and unit) the request is about. <see cref="Tower"/> is null when it cannot be determined; <see cref="Reason"/> then says why. Nothing here is ever a guess.</summary>
public sealed record TicketTowerResolution(ResolvedTower? Tower, string? UnitNumber, string Reason)
{
    public static TicketTowerResolution Unresolved(string reason, string? unit = null) => new(null, unit, reason);
}

public sealed record ResolvedTower(int TowerId, string TowerCode, string TowerName, int CompanyId);

/// <summary>
/// Finds the tower of the unit a ticket was raised for - the unit of THAT request, not any unit the customer owns. Only verified identifiers are used:
/// <list type="bullet">
/// <item>a CRM Buyer ticket: its CRM <c>ProjectId</c> through the verified project mapping (<see cref="ICollectionsCrmProjectMap"/>) to the tower code, then the local tower (<c>CollectionsTowers.TowerId</c>);</item>
/// <item>anything else is unresolved: a PACT-only ticket (its project/unit text is an editable snapshot, not a verified project id), a project or unit typed by hand, no unit at all.
/// Names and typed unit numbers are never matched.</item>
/// </list>
/// An unmapped project, several different tower codes, an unknown tower or one that exists under two companies is unresolved too.
/// </summary>
public sealed class CollectionsTicketTowerResolver(
    ICollectionsCrmProjectMap projectMap, ICollectionsTowerCatalog towers, Ticketing.Abstractions.ITicketCollectionsUnitRepository? selections = null)
{
    public async Task<TicketTowerResolution> ResolveAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        // The tower and unit a person selected when moving the ticket into Collections (verified against the customer's CRM units then) come first.
        if (selections is not null && await selections.GetAsync(ticket.TicketId, cancellationToken) is { } selected)
            return new TicketTowerResolution(new ResolvedTower(selected.TowerId, selected.TowerCode, selected.TowerName, selected.CompanyId), selected.UnitNumber, "Resolved");

        string? unit;
        string towerCode;
        int? companyHint = null;

        if (ticket.CrmBuyerProjectId is { } projectId)
        {
            unit = ticket.CrmBuyerUnitNumber;
            IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>> map;
            try { map = await projectMap.GetAsync([projectId], cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return TicketTowerResolution.Unresolved("The CRM project mapping could not be read.", unit); }

            if (!map.TryGetValue(projectId, out var rows) || rows.Count == 0)
                return TicketTowerResolution.Unresolved($"CRM project {projectId} has no verified tower mapping.", unit);
            var codes = rows.Select(r => CollectionsTowerNumber.Parse(r.ProjectCode)).Where(c => c is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (codes.Count != 1)
                return TicketTowerResolution.Unresolved($"CRM project {projectId} maps to {(codes.Count == 0 ? "no usable" : "several")} tower codes.", unit);
            towerCode = codes[0]!;
            var companies = rows.Select(r => r.CompanyId).Distinct().ToList();
            if (companies is [{ } onlyCompany]) companyHint = onlyCompany;
        }
        else
        {
            return TicketTowerResolution.Unresolved("The ticket carries no verified CRM project and unit.", ticket.ManualUnitNumber);
        }

        IReadOnlyList<Dto.CollectionsTowerDto> catalogue;
        try { catalogue = await towers.ListActiveAsync(cancellationToken); }
        catch (PactReceivablesSourceException) { return TicketTowerResolution.Unresolved("The tower list could not be read.", unit); }

        var matches = catalogue
            .Where(t => t.IsActive && CollectionsTowerNumber.Matches(towerCode, t.TowerNumber) && (companyHint is null || t.CompanyId == companyHint))
            .ToList();
        return matches.Count switch
        {
            1 => new TicketTowerResolution(new ResolvedTower(matches[0].TowerId, matches[0].TowerNumber.Trim(), matches[0].TowerName, matches[0].CompanyId), unit, "Resolved"),
            0 => TicketTowerResolution.Unresolved($"Tower {towerCode} is not an active tower.", unit),
            _ => TicketTowerResolution.Unresolved($"Tower {towerCode} exists under more than one company.", unit)
        };
    }
}
