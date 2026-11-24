using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.QueueingIntegration.Abstractions;

/// <summary>
/// Persistence port of the queueing integration: the event inbox, the per-ticket state, and the three mapping tables. One scoped implementation over
/// the same unit of work as every other Ticketing repository, so the integration's rows commit in the very transaction of the ticket change they describe.
/// </summary>
public interface IQueueingStore
{
    Task<QueueingEvent?> GetEventAsync(string eventId, CancellationToken cancellationToken = default);

    Task AddEventAsync(QueueingEvent queueingEvent, CancellationToken cancellationToken = default);

    /// <summary>Events that are Unresolved and due, oldest first.</summary>
    Task<IReadOnlyList<QueueingEvent>> ListDueForRetryAsync(DateTime nowUtc, int take, CancellationToken cancellationToken = default);

    /// <summary>Successfully processed events whose payload is still held and was processed before <paramref name="processedBeforeUtc"/> - the retention sweep's work list. Never an unresolved, failed, superseded or rejected event.</summary>
    Task<IReadOnlyList<QueueingEvent>> ListPurgeablePayloadsAsync(DateTime processedBeforeUtc, int take, CancellationToken cancellationToken = default);

    /// <summary>The ticket's events still waiting to be applied (Unresolved or interrupted), in sequence order - drained right after the ticket is created.</summary>
    Task<IReadOnlyList<QueueingEvent>> ListWaitingForTicketAsync(string externalTicketId, CancellationToken cancellationToken = default);

    /// <summary>The ticket created for this external id (any integration source of queue tickets), or null.</summary>
    Task<Ticket?> FindTicketAsync(string source, string externalTicketId, CancellationToken cancellationToken = default);

    Task<QueueingTicketLink?> GetLinkAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddLinkAsync(QueueingTicketLink link, CancellationToken cancellationToken = default);

    /// <summary>Read-only health snapshot: counts per status, held / failed events by outcome, and the first <paramref name="take"/> events that need attention.</summary>
    Task<TigerCS.Application.Modules.QueueingIntegration.Dto.QueueingMonitoringSnapshot> GetMonitoringSnapshotAsync(
        DateTime nowUtc, int take, CancellationToken cancellationToken = default);

    Task<QueueingCompanyMapping?> GetCompanyMappingAsync(string companyId, CancellationToken cancellationToken = default);

    Task<QueueingTicketTypeMapping?> GetTicketTypeMappingAsync(string companyId, string ticketTypeId, CancellationToken cancellationToken = default);

    Task<QueueingUserMapping?> GetUserMappingAsync(string externalUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws away every change that is staged but not saved, and forgets what was read. Called when an operation was refused or failed after the
    /// integration had already staged its own state, so that state is never committed by somebody else's later save.
    /// </summary>
    void DiscardPendingChanges();
}
