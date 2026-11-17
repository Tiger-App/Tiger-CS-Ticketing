namespace TigerCS.Application.Modules.QueueingIntegration.Abstractions;

/// <summary>
/// Serialises the processing of events for one queue ticket inside this process, so two requests for the same ticket do not interleave their reads and
/// writes. It is an optimisation and a courtesy, not the guarantee: across instances the unique indexes (EventId, ExternalTicketId) and the link's row
/// version are what keep the ticket consistent, and a request that loses either simply answers "busy, resend".
/// </summary>
public interface IQueueingEventLock
{
    /// <summary>Waits up to <paramref name="timeout"/> for the ticket's lock; null when it could not be taken in time.</summary>
    Task<IDisposable?> TryAcquireAsync(string externalTicketId, TimeSpan timeout, CancellationToken cancellationToken = default);
}
