using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

/// <param name="UnitRef">Where the unit was verified and its id there: <c>crm:{unitId}</c> or <c>pact:{unitId}</c>.</param>
/// <param name="UnitNumber">The unit number the source reports.</param>
/// <param name="ProjectName">The project name (display only).</param>
/// <param name="Source">Crm or Pact.</param>
public sealed record CollectionsUnitOptionDto(string UnitRef, string UnitNumber, string? ProjectName, string Source);

/// <summary>A tower of the customer and the units of the customer that belong to it.</summary>
public sealed record CollectionsTowerOptionDto(int TowerId, string TowerCode, string TowerName, int CompanyId, IReadOnlyList<CollectionsUnitOptionDto> Units);

/// <param name="Towers">The customer's verified towers, each with the customer's units in it.</param>
/// <param name="PreselectedTowerId">The ticket's own verified tower, when it has one.</param>
/// <param name="PreselectedUnitRef">The ticket's own verified unit, when it has one.</param>
/// <param name="Status">Ok, or SourceUnavailable (the customer's units could not be read from CRM/PACT; whatever is still verifiable is offered).</param>
/// <param name="Message">Why the list is short or empty, when it is.</param>
/// <param name="CollectionsDepartmentId">The Collections department's id, so a page can tell which target department needs a tower and unit.</param>
public sealed record CollectionsTransferOptionsDto(
    IReadOnlyList<CollectionsTowerOptionDto> Towers, int? PreselectedTowerId, string? PreselectedUnitRef, string Status, string? Message, int? CollectionsDepartmentId = null);

public enum CollectionsSelectionOutcome { Valid, Missing, Invalid, Unavailable }

/// <summary>A tower and unit proven to belong to the ticket's customer. <see cref="FromTicket"/>: it is the ticket's own recorded unit, so nothing new needs storing.</summary>
public sealed record VerifiedCollectionsUnit(ResolvedTower Tower, string UnitRef, string UnitNumber, bool FromTicket);

public sealed record CollectionsSelectionResult(CollectionsSelectionOutcome Outcome, VerifiedCollectionsUnit? Unit = null, string? Message = null)
{
    public bool IsValid => Outcome == CollectionsSelectionOutcome.Valid;
}

/// <summary>
/// The mandatory Tower + Unit of a move into Collections. A unit is offered, and accepted, ONLY when a trusted source reliably ties it to THIS ticket's customer:
/// <list type="bullet">
/// <item><b>CRM</b>: a unit CRM reports for the ticket's own customer (the buyer CRM returns must be the ticket's customer), or the unit the ticket was verified with; its CRM project must have a
/// verified tower mapping.</item>
/// <item><b>PACT</b>: for a ticket verified against PACT, a contract PACT reports for the SAME PACT customer (tenant id) the ticket was verified with; the tower is the one in PACT's own unit code
/// (<c>TP140-101</c>) and must be an active tower.</item>
/// </list>
/// Nothing typed by hand is ever a verified reference. When neither source can verify, the move is refused with a clear message. Read-only except <see cref="StoreAsync"/>, which joins the caller's transaction.
/// </summary>
public sealed class CollectionsTransferSelectionService(
    CrmBuyerLookupAppService crmBuyerLookup,
    IPactCustomerLookupGateway pact,
    ICollectionsCrmProjectMap projectMap,
    ICollectionsTowerCatalog towers,
    IIntakeRecordRepository intakeRecords,
    ITicketInteractionRepository interactions,
    ITicketCollectionsUnitRepository stored)
{
    public const string MissingMessage = "Select both a Tower and a Unit before transferring to Collections.";
    private const string PactSource = "Pact";

    private sealed record Candidate(string Ref, string UnitNumber, string? ProjectName, string Source, int? CrmProjectId, string? PactUnitCode, int? PactCompanyId, bool IsTicketsOwn);

    public async Task<CollectionsTransferOptionsDto> GetOptionsAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        var (options, status, message, own) = await BuildAsync(ticket, cancellationToken);
        return new(options, own?.Tower.TowerId, own?.UnitRef, status, message);
    }

    /// <param name="ticket">The ticket being moved.</param>
    /// <param name="towerId">The selected tower (<c>CollectionsTowers.TowerId</c>), or null.</param>
    /// <param name="unitRef">The selected unit reference (<c>crm:3</c> / <c>pact:12</c>), or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<CollectionsSelectionResult> CheckAsync(Ticket ticket, int? towerId, string? unitRef, CancellationToken cancellationToken = default)
    {
        var (options, status, message, own) = await BuildAsync(ticket, cancellationToken);
        if (towerId is null && string.IsNullOrWhiteSpace(unitRef))
        {
            // Nothing sent: acceptable only when the ticket already carries a verified tower and unit (an API caller, or the page's preselection untouched).
            if (own is not null) return new(CollectionsSelectionOutcome.Valid, own);
            return options.Count == 0 && message is not null
                ? new(CollectionsSelectionOutcome.Missing, null, MissingMessage + " " + message)
                : new(CollectionsSelectionOutcome.Missing, null, MissingMessage);
        }

        if (towerId is not > 0 || string.IsNullOrWhiteSpace(unitRef))
            return new(CollectionsSelectionOutcome.Missing, null, MissingMessage);

        var tower = options.FirstOrDefault(t => t.TowerId == towerId);
        var unit = tower?.Units.FirstOrDefault(u => string.Equals(u.UnitRef, unitRef.Trim(), StringComparison.Ordinal));
        if (tower is null || unit is null)
        {
            return status == "SourceUnavailable"
                ? new(CollectionsSelectionOutcome.Unavailable, null, "The customer's units could not be verified right now. Try again shortly.")
                : new(CollectionsSelectionOutcome.Invalid, null, "That Tower and Unit are not a verified unit of this ticket's customer. Choose the tower first, then one of its units.");
        }

        var resolved = new ResolvedTower(tower.TowerId, tower.TowerCode, tower.TowerName, tower.CompanyId);
        var fromTicket = own is not null && own.Tower.TowerId == tower.TowerId && own.UnitRef == unit.UnitRef;
        return new(CollectionsSelectionOutcome.Valid, new VerifiedCollectionsUnit(resolved, unit.UnitRef, unit.UnitNumber, fromTicket));
    }

    /// <summary>Remembers a verified selection for routing (joins the caller's transaction; the ticket's own unit needs no row).</summary>
    public async Task StoreAsync(Ticket ticket, VerifiedCollectionsUnit unit, Guid selectedBy, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // A CRM unit the ticket already carries is read from the ticket itself; a PACT unit is not stored on the ticket, so it is kept here for routing.
        if (unit.FromTicket && unit.UnitRef.StartsWith("crm:", StringComparison.Ordinal)) return;
        var tower = unit.Tower;
        if (await stored.GetAsync(ticket.TicketId, cancellationToken) is { } existing)
            existing.Apply(tower.TowerId, tower.TowerCode, tower.TowerName, tower.CompanyId, unit.UnitRef, unit.UnitNumber, selectedBy, nowUtc);
        else
            await stored.AddAsync(new TicketCollectionsUnit(ticket.TicketId, tower.TowerId, tower.TowerCode, tower.TowerName, tower.CompanyId, unit.UnitRef, unit.UnitNumber, selectedBy, nowUtc), cancellationToken);
    }

    private async Task<(List<CollectionsTowerOptionDto> Options, string Status, string? Message, VerifiedCollectionsUnit? Own)> BuildAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        var status = "Ok";
        string? message = null;

        // ---- CRM: the ticket's own verified unit, and the other units of the same CRM customer. ----
        if (ticket.CrmBuyerProjectId is { } ownProject && ticket.CrmBuyerUnitId is { } ownUnit)
            candidates.Add(new($"crm:{ownUnit}", ticket.CrmBuyerUnitNumber?.Trim() ?? string.Empty, ticket.CrmBuyerProjectName, "Crm", ownProject, null, null, true));

        string? phone = null;
        if (ticket.CrmBuyerCustomerId is { } customerId)
        {
            phone = await PhoneAsync(ticket.TicketId, cancellationToken);
            var lookup = phone is null ? null : await crmBuyerLookup.GetBuyerByPhoneAsync(phone, cancellationToken);
            // The buyer CRM returns is accepted only when it IS the ticket's own customer, exactly as the Customer Profile requires.
            if (lookup is { Outcome: CrmBuyerLookupOutcome.Success, Buyers: { } buyers } && buyers.FirstOrDefault(b => b.Customer.CustomerId == customerId) is { } match)
            {
                foreach (var u in match.Units.Where(u => !string.IsNullOrWhiteSpace(u.UnitNumber) && candidates.All(c => c.Ref != $"crm:{u.UnitId}")))
                    candidates.Add(new($"crm:{u.UnitId}", u.UnitNumber!.Trim(), u.ProjectName, "Crm", u.ProjectId, null, null, false));
            }
            else
            {
                status = "SourceUnavailable";
                message = "The customer's other units could not be read from CRM; only the ticket's own unit is offered.";
            }
        }

        // ---- PACT: a ticket verified against PACT; the contracts PACT holds for the SAME PACT customer (tenant id). ----
        if (string.Equals(ticket.CustomerVerificationSource, PactSource, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ticket.ExternalCustomerId))
        {
            phone ??= await PhoneAsync(ticket.TicketId, cancellationToken);
            var normalized = CustomerUnitLinkService.NormalizePhone(phone);
            PactCustomerLookupResult? lookup = null;
            if (normalized.Length > 0)
            {
                try { lookup = await pact.SearchByMobileAsync(normalized, cancellationToken); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { lookup = null; }
            }

            if (lookup is { Outcome: PactCustomerLookupOutcome.Success, Customers: { } customers }
                && customers.FirstOrDefault(c => string.Equals(c.PactCustomerId, ticket.ExternalCustomerId, StringComparison.Ordinal)) is { } tenant)
            {
                foreach (var c in tenant.Contracts.Where(c => !string.IsNullOrWhiteSpace(c.UnitCode) && c.UnitCode.Contains('-')
                                                              && !string.IsNullOrWhiteSpace(c.ExternalUnitId) && candidates.All(x => x.Ref != $"pact:{c.ExternalUnitId}")))
                {
                    candidates.Add(new($"pact:{c.ExternalUnitId.Trim()}", (c.UnitNumber ?? c.UnitCode!).Trim(), c.ProjectName, "Pact", null, c.UnitCode, c.CompanyId,
                        string.Equals(c.ExternalUnitId, ticket.ExternalUnitId, StringComparison.Ordinal)));
                }
            }
            else
            {
                status = "SourceUnavailable";
                message = "The customer's units could not be read from PACT, so they cannot be verified.";
            }
        }

        IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>> map = new Dictionary<int, IReadOnlyList<CrmProjectTowerMapping>>();
        IReadOnlyList<CollectionsTowerDto> catalogue = [];
        try
        {
            if (candidates.Count > 0)
            {
                var projectIds = candidates.Where(c => c.CrmProjectId is not null).Select(c => c.CrmProjectId!.Value).Distinct().ToList();
                if (projectIds.Count > 0) map = await projectMap.GetAsync(projectIds, cancellationToken);
                catalogue = await towers.ListActiveAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is PactReceivablesSourceException or InvalidOperationException or System.Data.Common.DbException)
        {
            status = "SourceUnavailable";
            message = "The tower mapping could not be read right now.";
            candidates.Clear();
        }

        var grouped = new Dictionary<int, (CollectionsTowerDto Tower, List<CollectionsUnitOptionDto> Units)>();
        VerifiedCollectionsUnit? own = null;
        foreach (var c in candidates)
        {
            string? code;
            int? companyHint;
            if (c.CrmProjectId is { } projectId)
            {
                if (!map.TryGetValue(projectId, out var rows) || rows.Count == 0) continue;
                var codes = rows.Select(r => CollectionsTowerNumber.Parse(r.ProjectCode)).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (codes.Count != 1) continue;
                code = codes[0];
                companyHint = rows.Select(r => r.CompanyId).Distinct().ToList() is [{ } only] ? only : null;
            }
            else
            {
                code = CollectionsTowerNumber.Parse(c.PactUnitCode);
                companyHint = c.PactCompanyId;
            }

            if (code is null) continue;
            var matches = catalogue.Where(t => t.IsActive && CollectionsTowerNumber.Matches(code, t.TowerNumber) && (companyHint is null || t.CompanyId == companyHint)).ToList();
            if (matches.Count != 1) continue;
            var tower = matches[0];
            if (!grouped.TryGetValue(tower.TowerId, out var group)) grouped[tower.TowerId] = group = (tower, []);
            group.Units.Add(new(c.Ref, c.UnitNumber, c.ProjectName, c.Source));
            if (c.IsTicketsOwn)
                own = new(new ResolvedTower(tower.TowerId, tower.TowerNumber.Trim(), tower.TowerName, tower.CompanyId), c.Ref, c.UnitNumber, true);
        }

        var options = grouped.Values
            .OrderBy(g => g.Tower.TowerNumber, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CollectionsTowerOptionDto(g.Tower.TowerId, g.Tower.TowerNumber.Trim(), g.Tower.TowerName, g.Tower.CompanyId,
                [.. g.Units.OrderBy(u => u.UnitNumber, StringComparer.OrdinalIgnoreCase)]))
            .ToList();
        if (options.Count == 0 && message is null)
            message = "No verified tower and unit were found for this customer in CRM or PACT, so the ticket cannot be moved to Collections.";
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
