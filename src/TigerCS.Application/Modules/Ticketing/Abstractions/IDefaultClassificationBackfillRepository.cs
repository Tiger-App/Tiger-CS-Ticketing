using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// The tickets the default-classification backfill may need to look at: any ticket with a missing category or request type, or one whose ids
/// might not resolve. The list is a superset - the service makes the authoritative judgement per ticket - ordered by id, so a run can resume.
/// </summary>
public interface IDefaultClassificationBackfillRepository
{
    /// <summary>Tracked tickets with a missing, partial or possibly dangling classification, after <paramref name="afterTicketId"/>, oldest first.</summary>
    Task<IReadOnlyList<Ticket>> ListPossiblyUnclassifiedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default);
}
