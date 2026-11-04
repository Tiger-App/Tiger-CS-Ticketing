using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>What the guard decided for a move: not a Collections move, a verified Tower + Unit, or a refusal.</summary>
public sealed record CollectionsTransferDecision(VerifiedCollectionsUnit? Unit, TicketMutationOutcome? Refusal)
{
    public static readonly CollectionsTransferDecision NotCollections = new(null, null);
}

/// <summary>
/// The one rule for every person-driven move into Collections (Transfer, Call Center classification into a Collections category, Reopen into Collections): a verified Tower AND Unit
/// are mandatory, checked before anything is written so a refusal leaves the department and assignee exactly as they were. Moves into any other department are never affected.
/// System routing (ticket creation, Genesys request-type routing) does not go through here: it has no person to ask and routes only by the ticket's own verified data.
/// </summary>
public sealed class CollectionsTransferGuard(CollectionsTeam team, CollectionsTransferSelectionService selection)
{
    public async Task<CollectionsTransferDecision> CheckAsync(
        Ticket ticket, int targetDepartmentId, int? towerId, string? unitRef, CancellationToken cancellationToken = default)
    {
        if (!await team.IsCollectionsDepartmentAsync(targetDepartmentId, cancellationToken))
        {
            return CollectionsTransferDecision.NotCollections;
        }

        var result = await selection.CheckAsync(ticket, towerId, unitRef, cancellationToken);
        return result.Outcome switch
        {
            CollectionsSelectionOutcome.Valid => new(result.Unit, null),
            CollectionsSelectionOutcome.Missing => new(null, TicketMutationOutcome.CollectionsTowerUnitRequired),
            CollectionsSelectionOutcome.Unavailable => new(null, TicketMutationOutcome.CollectionsTowerUnitUnavailable),
            _ => new(null, TicketMutationOutcome.CollectionsTowerUnitInvalid)
        };
    }

    /// <summary>Remembers the verified selection so routing (and the assignment history) use the unit this request is about.</summary>
    public Task StoreAsync(Ticket ticket, VerifiedCollectionsUnit unit, Guid selectedBy, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        selection.StoreAsync(ticket, unit, selectedBy, nowUtc, cancellationToken);

    public static string Describe(VerifiedCollectionsUnit unit) => $"CollectionsTower={unit.Tower.TowerCode};CollectionsUnit={unit.UnitNumber};UnitRef={unit.UnitRef}";
}
