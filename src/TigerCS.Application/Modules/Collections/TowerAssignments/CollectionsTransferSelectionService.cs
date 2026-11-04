using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

/// <param name="UnitId">The CRM unit id.</param>
/// <param name="UnitNumber">The unit number CRM reports.</param>
/// <param name="CrmProjectId">The CRM project the unit belongs to.</param>
/// <param name="ProjectName">The CRM project name (display only).</param>
public sealed record CollectionsUnitOptionDto(int UnitId, string UnitNumber, int CrmProjectId, string? ProjectName);

/// <summary>A tower of the customer and the units of the customer that belong to it.</summary>
public sealed record CollectionsTowerOptionDto(int TowerId, string TowerCode, string TowerName, int CompanyId, IReadOnlyList<CollectionsUnitOptionDto> Units);

/// <param name="Towers">The customer's verified towers, each with the customer's units in it.</param>
/// <param name="Message">Why the list is short or empty, when it is.</param>
/// <param name="CollectionsDepartmentId">The Collections department's id, so a page can tell which target department needs a tower and unit.</param>
/// <param name="Status">Ok, or CrmUnavailable (the customer's other units could not be read; the ticket's own verified unit is still offered).</param>
/// <param name="PreselectedTowerId">The ticket's own verified tower, when it has one.</param>
/// <param name="PreselectedUnitId">The ticket's own verified unit, when it has one.</param>
public sealed record CollectionsTransferOptionsDto(
    IReadOnlyList<CollectionsTowerOptionDto> Towers, int? PreselectedTowerId, int? PreselectedUnitId, string Status, string? Message, int? CollectionsDepartmentId = null);

public enum CollectionsSelectionOutcome { Valid, Missing, Invalid, Unavailable }

/// <summary>A tower and unit proven to belong to the ticket's customer. <see cref="FromTicket"/>: it is the ticket's own recorded unit, so nothing new needs storing.</summary>
public sealed record VerifiedCollectionsUnit(ResolvedTower Tower, int CrmUnitId, int CrmProjectId, string UnitNumber, bool FromTicket);

public sealed record CollectionsSelectionResult(CollectionsSelectionOutcome Outcome, VerifiedCollectionsUnit? Unit = null, string? Message = null)
{
    public bool IsValid => Outcome == CollectionsSelectionOutcome.Valid;
}

/// <summary>
/// The mandatory Tower + Unit of a move into Collections. A unit is offered, and accepted, ONLY when it is a unit of the ticket's own customer that CRM reports (or the unit the ticket
/// itself was verified with) and whose CRM project has a verified tower mapping - nothing typed by hand is ever a verified reference. Reuses the CRM Buyer lookup the Customer Profile uses,
/// the CRM project map and the tower list. Read-only except <see cref="StoreAsync"/>, which joins the caller's transaction.
/// </summary>
public sealed class CollectionsTransferSelectionService(
    CrmBuyerLookupAppService crmBuyerLookup,
    ICollectionsCrmProjectMap projectMap,
    ICollectionsTowerCatalog towers,
    IIntakeRecordRepository intakeRecords,
    ITicketInteractionRepository interactions,
    ITicketCollectionsUnitRepository stored)
{
    public const string MissingMessage = "Select both a Tower and a Unit before transferring to Collections.";

    private sealed record Candidate(int UnitId, string UnitNumber, int ProjectId, string? ProjectName, bool IsTicketsOwn);

    public async Task<CollectionsTransferOptionsDto> GetOptionsAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        var (options, status, message, own) = await BuildAsync(ticket, cancellationToken);
        return new(options, own?.Tower.TowerId, own?.CrmUnitId, status, message);
    }

    /// <param name="ticket">The ticket being moved.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="towerId">The selected tower (<c>CollectionsTowers.TowerId</c>), or null.</param>
    /// <param name="unitId">The selected CRM unit id, or null.</param>
    public async Task<CollectionsSelectionResult> CheckAsync(Ticket ticket, int? towerId, int? unitId, CancellationToken cancellationToken = default)
    {
        var (options, status, _, own) = await BuildAsync(ticket, cancellationToken);
        if (towerId is null && unitId is null)
        {
            // Nothing sent: acceptable only when the ticket already carries a verified tower and unit (an API caller, or the page's preselection untouched).
            return own is not null
                ? new(CollectionsSelectionOutcome.Valid, own)
                : new(CollectionsSelectionOutcome.Missing, null, MissingMessage);
        }

        if (towerId is not > 0 || unitId is not > 0)
            return new(CollectionsSelectionOutcome.Missing, null, MissingMessage);

        var tower = options.FirstOrDefault(t => t.TowerId == towerId);
        var unit = tower?.Units.FirstOrDefault(u => u.UnitId == unitId);
        if (tower is null || unit is null)
        {
            return status == "CrmUnavailable"
                ? new(CollectionsSelectionOutcome.Unavailable, null, "The customer's units could not be verified in CRM right now. Try again shortly.")
                : new(CollectionsSelectionOutcome.Invalid, null, "That Tower and Unit are not a verified unit of this ticket's customer. Choose the tower first, then one of its units.");
        }

        var resolved = new ResolvedTower(tower.TowerId, tower.TowerCode, tower.TowerName, tower.CompanyId);
        var fromTicket = own is not null && own.Tower.TowerId == tower.TowerId && own.CrmUnitId == unit.UnitId;
        return new(CollectionsSelectionOutcome.Valid, new VerifiedCollectionsUnit(resolved, unit.UnitId, unit.CrmProjectId, unit.UnitNumber, fromTicket));
    }

    /// <summary>Remembers a verified selection for routing (joins the caller's transaction; the ticket's own unit needs no row).</summary>
    public async Task StoreAsync(Ticket ticket, VerifiedCollectionsUnit unit, Guid selectedBy, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (unit.FromTicket) return;
        var tower = unit.Tower;
        if (await stored.GetAsync(ticket.TicketId, cancellationToken) is { } existing)
            existing.Apply(tower.TowerId, tower.TowerCode, tower.TowerName, tower.CompanyId, unit.CrmUnitId, unit.CrmProjectId, unit.UnitNumber, selectedBy, nowUtc);
        else
            await stored.AddAsync(new TicketCollectionsUnit(ticket.TicketId, tower.TowerId, tower.TowerCode, tower.TowerName, tower.CompanyId, unit.CrmUnitId, unit.CrmProjectId, unit.UnitNumber, selectedBy, nowUtc), cancellationToken);
    }

    private async Task<(List<CollectionsTowerOptionDto> Options, string Status, string? Message, VerifiedCollectionsUnit? Own)> BuildAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        if (ticket.CrmBuyerProjectId is { } ownProject && ticket.CrmBuyerUnitId is { } ownUnit)
            candidates.Add(new(ownUnit, ticket.CrmBuyerUnitNumber?.Trim() ?? string.Empty, ownProject, ticket.CrmBuyerProjectName, true));

        var status = "Ok";
        string? message = null;
        if (ticket.CrmBuyerCustomerId is { } customerId)
        {
            var phone = await PhoneAsync(ticket.TicketId, cancellationToken);
            var lookup = phone is null ? null : await crmBuyerLookup.GetBuyerByPhoneAsync(phone, cancellationToken);
            // The buyer CRM returns is accepted only when it IS the ticket's own customer, exactly as the Customer Profile requires.
            if (lookup is { Outcome: CrmBuyerLookupOutcome.Success, Buyers: { } buyers } && buyers.FirstOrDefault(b => b.Customer.CustomerId == customerId) is { } match)
            {
                foreach (var u in match.Units.Where(u => !string.IsNullOrWhiteSpace(u.UnitNumber) && !candidates.Any(c => c.UnitId == u.UnitId)))
                    candidates.Add(new(u.UnitId, u.UnitNumber!.Trim(), u.ProjectId, u.ProjectName, false));
            }
            else
            {
                status = "CrmUnavailable";
                message = "The customer's other units could not be read from CRM; only the ticket's own unit is offered.";
            }
        }

        IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>> map = new Dictionary<int, IReadOnlyList<CrmProjectTowerMapping>>();
        IReadOnlyList<CollectionsTowerDto> catalogue = [];
        try
        {
            if (candidates.Count > 0)
            {
                map = await projectMap.GetAsync([.. candidates.Select(c => c.ProjectId).Distinct()], cancellationToken);
                catalogue = await towers.ListActiveAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is PactReceivablesSourceException or InvalidOperationException or System.Data.Common.DbException)
        {
            status = "CrmUnavailable";
            message = "The tower mapping could not be read right now.";
            candidates.Clear();
        }

        var grouped = new Dictionary<int, (CollectionsTowerDto Tower, List<CollectionsUnitOptionDto> Units)>();
        VerifiedCollectionsUnit? own = null;
        foreach (var c in candidates)
        {
            if (!map.TryGetValue(c.ProjectId, out var rows) || rows.Count == 0) continue;
            var codes = rows.Select(r => CollectionsTowerNumber.Parse(r.ProjectCode)).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (codes.Count != 1) continue;
            var companies = rows.Select(r => r.CompanyId).Distinct().ToList();
            int? hint = companies is [{ } only] ? only : null;
            var matches = catalogue.Where(t => t.IsActive && CollectionsTowerNumber.Matches(codes[0], t.TowerNumber) && (hint is null || t.CompanyId == hint)).ToList();
            if (matches.Count != 1) continue;
            var tower = matches[0];
            if (!grouped.TryGetValue(tower.TowerId, out var group)) grouped[tower.TowerId] = group = (tower, []);
            group.Units.Add(new(c.UnitId, c.UnitNumber, c.ProjectId, c.ProjectName));
            if (c.IsTicketsOwn)
                own = new(new ResolvedTower(tower.TowerId, tower.TowerNumber.Trim(), tower.TowerName, tower.CompanyId), c.UnitId, c.ProjectId, c.UnitNumber, true);
        }

        var options = grouped.Values
            .OrderBy(g => g.Tower.TowerNumber, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CollectionsTowerOptionDto(g.Tower.TowerId, g.Tower.TowerNumber.Trim(), g.Tower.TowerName, g.Tower.CompanyId,
                [.. g.Units.OrderBy(u => u.UnitNumber, StringComparer.OrdinalIgnoreCase)]))
            .ToList();
        if (options.Count == 0 && status == "Ok")
            message = "No verified tower and unit were found for this customer, so the ticket cannot be moved to Collections.";
        return (options, status, message, own);
    }

    private async Task<string?> PhoneAsync(long ticketId, CancellationToken cancellationToken)
    {
        var intake = await intakeRecords.GetByLinkedTicketIdAsync(ticketId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(intake?.PhoneNumber)) return intake.PhoneNumber;
        var originating = await interactions.GetOriginatingAsync(ticketId, cancellationToken);
        return string.IsNullOrWhiteSpace(originating?.CustomerPhone) ? null : originating.CustomerPhone;
    }
}
