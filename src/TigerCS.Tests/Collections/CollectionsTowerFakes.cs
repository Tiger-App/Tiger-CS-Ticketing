using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Collections.TowerAssignments;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Collections;

public sealed class FakeCollectionsTowerAssignmentRepository : ICollectionsTowerAssignmentRepository
{
    public List<CollectionsTowerAssignment> Rows { get; } = [];
    public List<CollectionsTowerAssignmentHistory> History { get; } = [];

    public Task<IReadOnlyList<CollectionsTowerAssignment>> ListByMonthAsync(int year, int month, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollectionsTowerAssignment>>([.. Rows.Where(r => r.Year == year && r.Month == month)]);

    public Task<CollectionsTowerAssignment?> GetActivePrimaryAsync(int towerId, int year, int month, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.TowerId == towerId && r.Year == year && r.Month == month && r.IsPrimary && r.Status == TowerAssignmentStatus.Active));

    public Task AddAsync(CollectionsTowerAssignment assignment, CancellationToken cancellationToken = default)
    {
        Rows.Add(assignment);
        return Task.CompletedTask;
    }

    public void Remove(CollectionsTowerAssignment assignment) => Rows.Remove(assignment);

    public Task AddHistoryAsync(CollectionsTowerAssignmentHistory entry, CancellationToken cancellationToken = default)
    {
        History.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CollectionsTowerAssignmentHistory>> ListHistoryAsync(int? year, int? month, int? towerId, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollectionsTowerAssignmentHistory>>([.. History
            .Where(h => (year is null || h.Year == year) && (month is null || h.Month == month) && (towerId is null || h.TowerId == towerId))
            .OrderByDescending(h => h.ChangedAtUtc).Take(take)]);
}

public sealed class FakeTicketAssignmentHistoryRepository : ITicketAssignmentHistoryRepository
{
    public List<TicketAssignmentHistoryEntry> Entries { get; } = [];

    public Task AddAsync(TicketAssignmentHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TicketAssignmentHistoryEntry>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketAssignmentHistoryEntry>>([.. Entries.Where(e => e.TicketId == ticketId)]);

    public Task<TicketAssignmentHistoryEntry?> GetLatestAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Entries.LastOrDefault(e => e.TicketId == ticketId));
}

public sealed class FakeCollectionsTowerCatalog : ICollectionsTowerCatalog
{
    public List<CollectionsTowerDto> Towers { get; } = [];

    public Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CollectionsTowerDto>>([.. Towers.Where(t => t.IsActive)]);
}

public sealed class FakeCollectionsCrmProjectMap : ICollectionsCrmProjectMap
{
    public Dictionary<int, List<CrmProjectTowerMapping>> Mappings { get; } = [];

    public Task<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>> GetAsync(IReadOnlyCollection<int> crmProjectIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>>(
            crmProjectIds.Where(Mappings.ContainsKey).ToDictionary(id => id, id => (IReadOnlyList<CrmProjectTowerMapping>)Mappings[id]));
}

public sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTime Now { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc), TimeSpan.Zero);
}

public sealed class FakeTicketCollectionsUnitRepository : ITicketCollectionsUnitRepository
{
    public List<TicketCollectionsUnit> Rows { get; } = [];

    public Task<TicketCollectionsUnit?> GetAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.TicketId == ticketId));

    public Task AddAsync(TicketCollectionsUnit unit, CancellationToken cancellationToken = default)
    {
        Rows.Add(unit);
        return Task.CompletedTask;
    }
}

public sealed class FakePactLookup : TigerCS.Application.Modules.CustomerVerification.PactIntegration.IPactCustomerLookupGateway
{
    public TigerCS.Application.Modules.CustomerVerification.PactIntegration.PactCustomerLookupResult Result { get; set; } =
        TigerCS.Application.Modules.CustomerVerification.PactIntegration.PactCustomerLookupResult.NotFound();

    public int Calls { get; private set; }

    public Task<TigerCS.Application.Modules.CustomerVerification.PactIntegration.PactCustomerLookupResult> SearchByMobileAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
