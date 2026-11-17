using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.QueueingIntegration.Abstractions;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.QueueingIntegration.Repositories;

/// <summary>EF Core implementation of <see cref="IQueueingStore"/>, on the same scoped DbContext as every other Ticketing repository so staged rows commit with the ticket change.</summary>
public sealed class QueueingStore(TigerCsDbContext dbContext) : IQueueingStore
{
    /// <summary>An event still marked Received after this long was interrupted (a crash between receipt and settlement) and is picked up by the retry sweep.</summary>
    private static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(2);

    public Task<QueueingEvent?> GetEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var id = eventId.Trim();
        return dbContext.QueueingEvents.FirstOrDefaultAsync(e => e.Source == QueueingEvent.SourceName && e.EventId == id, cancellationToken);
    }

    public async Task AddEventAsync(QueueingEvent queueingEvent, CancellationToken cancellationToken = default) =>
        await dbContext.QueueingEvents.AddAsync(queueingEvent, cancellationToken);

    public async Task<IReadOnlyList<QueueingEvent>> ListDueForRetryAsync(DateTime nowUtc, int take, CancellationToken cancellationToken = default)
    {
        var interruptedBefore = nowUtc - InterruptedAfter;
        return await dbContext.QueueingEvents
            .Where(e => (e.Status == QueueingEventStatus.Unresolved && e.NextRetryAtUtc != null && e.NextRetryAtUtc <= nowUtc)
                || (e.Status == QueueingEventStatus.Received && e.ReceivedAtUtc < interruptedBefore))
            .OrderBy(e => e.ExternalTicketId).ThenBy(e => e.EventSequence)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueueingEvent>> ListPurgeablePayloadsAsync(DateTime processedBeforeUtc, int take, CancellationToken cancellationToken = default) =>
        await dbContext.QueueingEvents
            .Where(e => (e.Status == QueueingEventStatus.Processed || e.Status == QueueingEventStatus.Superseded || e.Status == QueueingEventStatus.Rejected)
                && e.Payload != null && e.ProcessedAtUtc != null && e.ProcessedAtUtc < processedBeforeUtc)
            .OrderBy(e => e.ProcessedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<QueueingEvent>> ListWaitingForTicketAsync(string externalTicketId, CancellationToken cancellationToken = default)
    {
        var id = externalTicketId.Trim();
        return await dbContext.QueueingEvents
            .Where(e => e.Source == QueueingEvent.SourceName && e.ExternalTicketId == id
                && (e.Status == QueueingEventStatus.Unresolved || e.Status == QueueingEventStatus.Received))
            .OrderBy(e => e.EventSequence).ThenBy(e => e.QueueingEventId)
            .ToListAsync(cancellationToken);
    }

    public Task<Ticket?> FindTicketAsync(string source, string externalTicketId, CancellationToken cancellationToken = default)
    {
        var id = externalTicketId.Trim();
        return dbContext.Tickets.FirstOrDefaultAsync(t => t.IntegrationSource == source && t.ExternalTicketId == id, cancellationToken);
    }

    public Task<QueueingTicketLink?> GetLinkAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.QueueingTicketLinks.FirstOrDefaultAsync(l => l.TicketId == ticketId, cancellationToken);

    public async Task AddLinkAsync(QueueingTicketLink link, CancellationToken cancellationToken = default) =>
        await dbContext.QueueingTicketLinks.AddAsync(link, cancellationToken);

    public Task<QueueingCompanyMapping?> GetCompanyMappingAsync(string companyId, CancellationToken cancellationToken = default)
    {
        var id = companyId.Trim();
        return dbContext.QueueingCompanyMappings.AsNoTracking().FirstOrDefaultAsync(m => m.CompanyId == id, cancellationToken);
    }

    public Task<QueueingTicketTypeMapping?> GetTicketTypeMappingAsync(string companyId, string ticketTypeId, CancellationToken cancellationToken = default)
    {
        var company = companyId.Trim();
        var type = ticketTypeId.Trim();
        return dbContext.QueueingTicketTypeMappings.AsNoTracking().FirstOrDefaultAsync(m => m.CompanyId == company && m.TicketTypeId == type, cancellationToken);
    }

    public Task<QueueingUserMapping?> GetUserMappingAsync(string externalUserId, CancellationToken cancellationToken = default)
    {
        var id = externalUserId.Trim();
        return dbContext.QueueingUserMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ExternalUserId == id, cancellationToken);
    }

    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();
}

/// <summary>
/// In-process, per-queue-ticket serialisation (see <see cref="IQueueingEventLock"/>). Singleton. A reference-counted semaphore per external ticket id, removed when
/// nobody holds or awaits it, so the table never grows with the number of tickets.
/// </summary>
public sealed class InProcessQueueingEventLock : IQueueingEventLock
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<IDisposable?> TryAcquireAsync(string externalTicketId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var key = externalTicketId.Trim();
        Entry entry;
        lock (_entries)
        {
            entry = _entries.GetOrAdd(key, _ => new Entry());
            entry.Users++;
        }

        bool taken;
        try
        {
            taken = await entry.Gate.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        if (!taken)
        {
            Release(key, entry, held: false);
            return null;
        }

        return new Releaser(() => Release(key, entry, held: true));
    }

    private void Release(string key, Entry entry, bool held)
    {
        if (held)
        {
            entry.Gate.Release();
        }

        lock (_entries)
        {
            if (--entry.Users == 0)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }
}
