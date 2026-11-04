using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Append-only assignment event log (<see cref="TicketAssignmentHistoryEntry"/>). Writes join the caller's unit of work.</summary>
public interface ITicketAssignmentHistoryRepository
{
    Task AddAsync(TicketAssignmentHistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Every entry of the ticket, oldest first.</summary>
    Task<IReadOnlyList<TicketAssignmentHistoryEntry>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>The newest entry of the ticket (any department), or null.</summary>
    Task<TicketAssignmentHistoryEntry?> GetLatestAsync(long ticketId, CancellationToken cancellationToken = default);
}
