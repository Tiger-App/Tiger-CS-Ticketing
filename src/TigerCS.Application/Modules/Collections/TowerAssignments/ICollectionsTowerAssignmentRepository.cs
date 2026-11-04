using TigerCS.Domain.Modules.Collections.TowerAssignments;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

public interface ICollectionsTowerAssignmentRepository
{
    /// <summary>Every row (Draft and Active) of one month, tracked so the caller can change it.</summary>
    Task<IReadOnlyList<CollectionsTowerAssignment>> ListByMonthAsync(int year, int month, CancellationToken cancellationToken = default);

    /// <summary>The ACTIVE primary row of a tower for a month, or null. Drafts never count.</summary>
    Task<CollectionsTowerAssignment?> GetActivePrimaryAsync(int towerId, int year, int month, CancellationToken cancellationToken = default);

    Task AddAsync(CollectionsTowerAssignment assignment, CancellationToken cancellationToken = default);

    void Remove(CollectionsTowerAssignment assignment);

    Task AddHistoryAsync(CollectionsTowerAssignmentHistory entry, CancellationToken cancellationToken = default);

    /// <summary>Newest first. Any filter may be null.</summary>
    Task<IReadOnlyList<CollectionsTowerAssignmentHistory>> ListHistoryAsync(int? year, int? month, int? towerId, int take, CancellationToken cancellationToken = default);
}
